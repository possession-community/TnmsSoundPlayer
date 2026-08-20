using System.Diagnostics;
using System.Text;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer.Media;

/// <summary>
/// One running decode pipeline: an ffmpeg process producing raw PCM on stdout,
/// optionally fed by another process (yt-dlp) or an in-memory buffer.
/// </summary>
internal sealed class DecodePipeline : IDisposable
{
    private readonly List<Process> _processes = [];
    private readonly StringBuilder _stderr = new();
    private readonly Lock _stderrLock = new();

    public Stream PcmOutput { get; private set; } = Stream.Null;

    public string StderrTail
    {
        get
        {
            lock (_stderrLock)
            {
                return _stderr.ToString();
            }
        }
    }

    public bool AnyProcessFailed
        => _processes.Any(p => p.HasExited && p.ExitCode != 0);

    public void Attach(Process process, bool isOutput)
    {
        _processes.Add(process);
        if (isOutput)
        {
            PcmOutput = process.StandardOutput.BaseStream;
        }
        _ = DrainStderrAsync(process);
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            var reader = process.StandardError;
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (_stderrLock)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 4096)
                    {
                        _stderr.Remove(0, _stderr.Length - 4096);
                    }
                }
            }
        }
        catch
        {
            // Process killed; nothing left to drain.
        }
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already gone.
            }
            process.Dispose();
        }
        _processes.Clear();
    }
}

/// <summary>
/// <see cref="IPcmAudioStream"/> backed by an external decode pipeline.
/// Seeking (when the factory supports it) restarts the pipeline at the target position.
/// </summary>
internal sealed class FfmpegPcmStream : IPcmAudioStream
{
    private readonly Func<TimeSpan, DecodePipeline> _factory;
    private readonly Action? _onDispose;
    private DecodePipeline? _pipeline;
    private byte[] _prebuffer = [];
    private int _prebufferOffset;
    private long _positionBytes;
    private bool _endOfStream;
    private bool _disposed;

    public bool CanSeek { get; }
    public TimeSpan? Duration { get; }
    public TimeSpan Position => PipelineFormat.GetDuration(_positionBytes);

    /// <param name="onDispose">
    /// Runs after the pipeline is torn down. Used to delete a temporary file the stream was reading
    /// from, which cannot happen earlier: seeking restarts the pipeline against the same path.
    /// </param>
    public FfmpegPcmStream(
        Func<TimeSpan, DecodePipeline> factory, bool canSeek, TimeSpan? duration, Action? onDispose = null)
    {
        _factory = factory;
        CanSeek = canSeek;
        Duration = duration;
        _onDispose = onDispose;
    }

    /// <summary>
    /// Starts the pipeline and blocks until the first PCM bytes arrive, so open errors
    /// (bad file, unreachable URL) surface at open time instead of mid-playback.
    /// </summary>
    public async Task PrimeAsync(CancellationToken ct)
    {
        _pipeline = _factory(TimeSpan.Zero);

        var buffer = new byte[PipelineFormat.FrameBytes];
        var read = await Task.Run(() => ReadAtLeastOneByte(buffer), ct);

        if (read <= 0)
        {
            var stderr = _pipeline.StderrTail;
            _endOfStream = true;
            throw new SoundPlayerException(PlaybackErrorReason.DecodeFailed,
                stderr.Length > 0 ? stderr : "The source produced no audio data.");
        }

        _prebuffer = buffer[..read];
        _prebufferOffset = 0;
    }

    private int ReadAtLeastOneByte(byte[] buffer)
        => _pipeline!.PcmOutput.Read(buffer, 0, buffer.Length);

    public int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_endOfStream || destination.IsEmpty)
        {
            return 0;
        }

        if (_prebufferOffset < _prebuffer.Length)
        {
            var available = _prebuffer.Length - _prebufferOffset;
            var count = Math.Min(available, destination.Length);
            _prebuffer.AsSpan(_prebufferOffset, count).CopyTo(destination);
            _prebufferOffset += count;
            _positionBytes += count;
            return count;
        }

        _pipeline ??= _factory(Position);

        var read = _pipeline.PcmOutput.Read(destination);
        if (read == 0)
        {
            _endOfStream = true;
            if (_positionBytes == 0 && _pipeline.AnyProcessFailed)
            {
                throw new SoundPlayerException(PlaybackErrorReason.DecodeFailed, _pipeline.StderrTail);
            }
        }

        _positionBytes += read;
        return read;
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!CanSeek)
        {
            throw new NotSupportedException("This stream does not support seeking.");
        }

        _pipeline?.Dispose();
        _pipeline = _factory(position);
        _prebuffer = [];
        _prebufferOffset = 0;
        _positionBytes = PipelineFormat.GetByteCount(position);
        _endOfStream = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _pipeline?.Dispose();
        _pipeline = null;
        _onDispose?.Invoke();
    }
}
