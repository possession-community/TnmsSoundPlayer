using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus.Structs;
using TnmsSoundPlayer.Media;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer.Core;

/// <summary>One Opus packet variant of a chunk (a specific volume bucket).</summary>
internal sealed record OpusPacket(byte[] Data, uint NumPackets, uint[] Offsets);

/// <summary>60 ms of encoded audio: one Opus packet per active volume bucket.</summary>
internal sealed record EncodedChunk(Dictionary<float, OpusPacket> Buckets, double Seconds);

internal sealed class SoundPlayback : ISoundPlayback
{
    public const int FramesPerChunk = 3;
    public const int ChunkBytes = PipelineFormat.FrameBytes * FramesPerChunk;   // 60 ms of PCM
    public const int ChunkSamples = ChunkBytes / PipelineFormat.BytesPerSample; // interleaved shorts
    public const double ChunkSeconds = 0.06;

    private const int BufferedChunkLimit = 10; // ~600 ms of encode-ahead

    private readonly SoundPlayerCore _core;
    private readonly Func<CancellationToken, Task<IPcmAudioStream>> _sourceFactory;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile PlaybackState _state = PlaybackState.Queued;
    private PlaybackError? _error;
    private float _volume;
    private long _positionTicks;
    private TimeSpan? _pendingSeek;
    private readonly Lock _seekLock = new();
    private bool _finalized;
    private long _nextDueTimestamp;

    // Written by the decode worker, read by the pump.
    internal readonly ConcurrentQueue<EncodedChunk> Chunks = new();
    internal readonly SemaphoreSlim BufferSlots = new(BufferedChunkLimit);
    internal readonly CancellationTokenSource Cts = new();
    internal volatile bool DecodeDone;
    internal SoundPlayerException? WorkerError;
    internal IPcmAudioStream? Stream;
    internal Task? WorkerTask;

    // Pump-owned state. The voice section number is deliberately not here: it belongs to the
    // speaker, which outlives any one playback, and restarting it per playback made the client
    // drop whichever playback followed another. See SoundPlayerCore._voiceSection.
    internal bool StartedFired;

    /// <summary>Slot snapshot for <see cref="SoundRecipients.SnapshotRecipients"/>, captured at Play time.</summary>
    internal HashSet<int>? SnapshotSlots;

    internal PlayOptions Options { get; }

    public long Id { get; }
    public string OwnerName { get; }
    public PlaybackState State => _state;
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));
    public TimeSpan? Duration => Stream?.Duration;
    public PlaybackError? Error => _error;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 4f);
    }

    /// <summary>Supplied at Play time, so no notification can be missed. Released on finalize.</summary>
    internal ISoundPlaybackCallback? Callback;

    public Task Completion => _completion.Task;

    internal SoundPlayback(
        long id, string ownerName, PlayOptions options,
        Func<CancellationToken, Task<IPcmAudioStream>> sourceFactory,
        IPcmAudioStream? providedStream, SoundPlayerCore core)
    {
        Id = id;
        OwnerName = ownerName;
        Options = options;
        _sourceFactory = sourceFactory;
        Stream = providedStream;
        _core = core;
        _volume = Math.Clamp(options.Volume, 0f, 4f);
        Interlocked.Exchange(ref _positionTicks, options.StartAt.Ticks);
    }

    public void Stop() => _core.RequestStop(this);
    public void Pause() => _core.RequestPause(this);
    public void Resume() => _core.RequestResume(this);

    public void Seek(TimeSpan position)
    {
        if (Stream is { CanSeek: false })
        {
            throw new NotSupportedException("The underlying audio source does not support seeking.");
        }

        lock (_seekLock)
        {
            _pendingSeek = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }
    }

    // ---- pump-side helpers (game thread) ----

    internal bool IsDue(long now)
        => _nextDueTimestamp == 0 || now >= _nextDueTimestamp;

    /// <summary>Keeps the send clock from accumulating debt while paused, buffering or idle.</summary>
    internal void HoldClock()
    {
        var now = Stopwatch.GetTimestamp();
        if (_nextDueTimestamp < now)
        {
            _nextDueTimestamp = now;
        }
    }

    internal void AdvanceClock(double seconds)
    {
        if (_nextDueTimestamp == 0)
        {
            _nextDueTimestamp = Stopwatch.GetTimestamp();
        }
        _nextDueTimestamp += (long)(seconds * Stopwatch.Frequency);
    }

    internal void OnChunkSent(double seconds)
        => Interlocked.Add(ref _positionTicks, TimeSpan.FromSeconds(seconds).Ticks);

    internal void SetState(PlaybackState state)
        => _state = state;

    internal void SetRejectedError(PlaybackError error)
        => _error = error;

    internal void FireStarted(Action<Exception> onListenerError)
    {
        try
        {
            Callback?.OnStarted(this);
        }
        catch (Exception ex)
        {
            onListenerError(ex);
        }
    }

    /// <summary>Terminal transition; runs exactly once, on the game thread.</summary>
    internal void Finalize(PlaybackState state, PlaybackError? error, Action<Exception> onListenerError)
    {
        if (_finalized)
        {
            return;
        }
        _finalized = true;

        _error = error;
        _state = state;
        Cts.Cancel();

        // Kill the decode pipeline off-thread; process teardown must not block the game thread.
        var stream = Stream;
        var worker = WorkerTask;
        _ = Task.Run(async () =>
        {
            try
            {
                if (worker is not null)
                {
                    await worker;
                }
            }
            catch
            {
                // Worker exceptions are surfaced via WorkerError.
            }
            stream?.Dispose();
        });

        try
        {
            Callback?.OnFinished(this);
        }
        catch (Exception ex)
        {
            onListenerError(ex);
        }

        // Drop the callback now rather than relying on this playback becoming unreachable. A caller
        // that parks the handle in a long-lived field (a "currently playing" slot, say) would
        // otherwise keep the callback — and whatever it holds, an IGameClient typically — alive with it.
        Callback = null;

        _completion.TrySetResult();
    }

    // ---- decode worker (background thread) ----

    internal void StartWorker()
        => WorkerTask = Task.Run(RunWorkerAsync);

    private async Task RunWorkerAsync()
    {
        var ct = Cts.Token;
        try
        {
            Stream ??= await _sourceFactory(ct);
            ct.ThrowIfCancellationRequested();

            // Seek says so by throwing; Loop used to just quietly not happen. Say it out loud once,
            // because "my live stream does not loop" is otherwise invisible.
            if (Options.Loop && !Stream.CanSeek)
            {
                _core.WarnLoopUnsupported(Id, OwnerName);
            }

            if (Options.StartAt > TimeSpan.Zero)
            {
                SeekOrSkip(Options.StartAt);
            }

            var encoders = new Dictionary<float, OpusEncoder>();
            var pcm = new byte[ChunkBytes];
            var baseSamples = new short[ChunkSamples];
            var scaledSamples = new short[ChunkSamples];
            var opusScratch = new byte[4000];

            while (!ct.IsCancellationRequested)
            {
                ApplyPendingSeek();

                var read = ReadChunk(Stream, pcm);
                if (read <= 0)
                {
                    if (Options.Loop && Stream.CanSeek)
                    {
                        Stream.Seek(TimeSpan.Zero);
                        ResetPosition(TimeSpan.Zero);
                        continue;
                    }
                    break;
                }

                pcm.AsSpan(read).Clear();
                MemoryMarshal.Cast<byte, short>(pcm.AsSpan()).CopyTo(baseSamples);

                var playbackVolume = _volume;
                if (playbackVolume != 1f)
                {
                    Scale(baseSamples, playbackVolume);
                }

                var buckets = _core.GetVolumeBuckets(OwnerName);
                var packets = new Dictionary<float, OpusPacket>(buckets.Length);
                foreach (var bucketVolume in buckets)
                {
                    short[] source;
                    if (bucketVolume == 1f)
                    {
                        source = baseSamples;
                    }
                    else
                    {
                        baseSamples.CopyTo(scaledSamples, 0);
                        Scale(scaledSamples, bucketVolume);
                        source = scaledSamples;
                    }

                    if (!encoders.TryGetValue(bucketVolume, out var encoder))
                    {
                        encoder = CreateEncoder();
                        encoders[bucketVolume] = encoder;
                    }

                    packets[bucketVolume] = EncodeChunk(encoder, source, opusScratch);
                }

                await BufferSlots.WaitAsync(ct);
                Chunks.Enqueue(new EncodedChunk(packets, ChunkSeconds));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SoundPlayerException ex)
        {
            WorkerError = ex;
        }
        catch (Exception ex)
        {
            WorkerError = new SoundPlayerException(PlaybackErrorReason.DecodeFailed, ex.Message, ex);
        }
        finally
        {
            DecodeDone = true;
        }
    }

    private void SeekOrSkip(TimeSpan target)
    {
        if (Stream!.CanSeek)
        {
            Stream.Seek(target);
        }
        else
        {
            // Decode and discard up to the target position.
            var toSkip = PipelineFormat.GetByteCount(target);
            var scratch = new byte[ChunkBytes];
            while (toSkip > 0)
            {
                var read = Stream.Read(scratch.AsSpan(0, Math.Min(scratch.Length, toSkip)));
                if (read <= 0)
                {
                    break;
                }
                toSkip -= read;
            }
        }
        ResetPosition(target);
    }

    private void ApplyPendingSeek()
    {
        TimeSpan? target;
        lock (_seekLock)
        {
            target = _pendingSeek;
            _pendingSeek = null;
        }

        if (target is not { } position)
        {
            return;
        }

        // Discard everything already encoded at the old position.
        while (Chunks.TryDequeue(out _))
        {
            BufferSlots.Release();
        }

        SeekOrSkip(position);
    }

    private void ResetPosition(TimeSpan position)
        => Interlocked.Exchange(ref _positionTicks, position.Ticks);

    private static int ReadChunk(IPcmAudioStream stream, byte[] destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination.AsSpan(total));
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private static void Scale(short[] samples, float gain)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
        }
    }

    private static OpusEncoder CreateEncoder()
        => new(PipelineFormat.SampleRate, PipelineFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO)
        {
            // Near-transparent music bitrates; the voice-chat default (64k mono) audibly
            // thins out the low end.
            Bitrate = PipelineFormat.Channels == 2 ? 160000 : 128000,
            UseDTX = false,
            UseVBR = true,
        };

    private static OpusPacket EncodeChunk(OpusEncoder encoder, short[] samples, byte[] scratch)
    {
        // Opus frame size is per channel; the sample buffer is interleaved.
        const int frameSize = PipelineFormat.FrameSamplesPerChannel;
        const int shortsPerFrame = frameSize * PipelineFormat.Channels;

        var combined = new List<byte>(1024);
        var offsets = new uint[FramesPerChunk];

        for (var frame = 0; frame < FramesPerChunk; frame++)
        {
            var encoded = encoder.Encode(samples, frame * shortsPerFrame, frameSize, scratch, 0, scratch.Length);
            combined.AddRange(scratch.AsSpan(0, encoded));
            offsets[frame] = (uint)combined.Count;
        }

        return new OpusPacket(combined.ToArray(), FramesPerChunk, offsets);
    }
}
