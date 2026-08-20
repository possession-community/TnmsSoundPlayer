using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer.Media;

internal static class FfmpegProcess
{
    /// <summary>Common output options: raw 48 kHz mono s16le PCM on stdout.</summary>
    private static void AddOutputArgs(ProcessStartInfo psi)
    {
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(PipelineFormat.Channels.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(PipelineFormat.SampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-");
    }

    private static ProcessStartInfo CreateBase(string ffmpegPath, bool redirectStdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        return psi;
    }

    public static DecodePipeline StartForFile(string ffmpegPath, string filePath, TimeSpan startAt)
    {
        var psi = CreateBase(ffmpegPath, redirectStdin: false);
        if (startAt > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startAt.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        AddOutputArgs(psi);

        var pipeline = new DecodePipeline();
        pipeline.Attach(Start(psi), isOutput: true);
        return pipeline;
    }

    public static DecodePipeline StartForBuffer(string ffmpegPath, ReadOnlyMemory<byte> encodedData, TimeSpan startAt)
    {
        var psi = CreateBase(ffmpegPath, redirectStdin: true);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("pipe:0");
        if (startAt > TimeSpan.Zero)
        {
            // Pipes cannot input-seek; let ffmpeg decode and discard up to the target.
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startAt.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        AddOutputArgs(psi);

        var pipeline = new DecodePipeline();
        var ffmpeg = Start(psi);
        pipeline.Attach(ffmpeg, isOutput: true);
        _ = FeedAsync(ffmpeg, encodedData);
        return pipeline;
    }

    public static DecodePipeline StartForUrl(string ffmpegPath, string ytdlpPath, ToolManager tools, string url)
    {
        var ytdlpPsi = new ProcessStartInfo
        {
            FileName = ytdlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ytdlpPsi.ArgumentList.Add("--no-playlist");
        ytdlpPsi.ArgumentList.Add("--quiet");
        ytdlpPsi.ArgumentList.Add("--no-warnings");
        ConfigureYtdlp(ytdlpPsi, tools);
        ytdlpPsi.ArgumentList.Add("-f");
        ytdlpPsi.ArgumentList.Add("bestaudio/best");
        ytdlpPsi.ArgumentList.Add("-o");
        ytdlpPsi.ArgumentList.Add("-");
        ytdlpPsi.ArgumentList.Add(url);

        var ffmpegPsi = CreateBase(ffmpegPath, redirectStdin: true);
        ffmpegPsi.ArgumentList.Add("-i");
        ffmpegPsi.ArgumentList.Add("pipe:0");
        AddOutputArgs(ffmpegPsi);

        var pipeline = new DecodePipeline();
        var ytdlp = Start(ytdlpPsi);
        var ffmpeg = Start(ffmpegPsi);
        pipeline.Attach(ytdlp, isOutput: false);
        pipeline.Attach(ffmpeg, isOutput: true);
        _ = PipeAsync(ytdlp, ffmpeg);
        return pipeline;
    }

    /// <summary>
    /// Wires yt-dlp's JS runtime and keeps every cache inside the module tools directory,
    /// so nothing is written to the user profile. yt-dlp needs a JS runtime (deno) to solve
    /// YouTube's nsig challenge; without one, downloads fail with HTTP 403.
    /// </summary>
    public static void ConfigureYtdlp(ProcessStartInfo ytdlpPsi, ToolManager tools)
    {
        ytdlpPsi.ArgumentList.Add("--cache-dir");
        ytdlpPsi.ArgumentList.Add(Path.Combine(tools.ToolsDirectory, "cache", "yt-dlp"));

        if (tools.DenoPath is { } denoPath)
        {
            ytdlpPsi.ArgumentList.Add("--js-runtimes");
            ytdlpPsi.ArgumentList.Add($"deno:{denoPath}");
            ytdlpPsi.Environment["DENO_DIR"] = Path.Combine(tools.ToolsDirectory, "cache", "deno");
        }
    }

    private static Process Start(ProcessStartInfo psi)
        => Process.Start(psi) ?? throw new SoundPlayerException(
            PlaybackErrorReason.DecodeFailed, $"Failed to start process '{psi.FileName}'.");

    /// <summary>
    /// Reads a local file's duration with ffprobe. Returns null when ffprobe is unavailable or the
    /// container carries no duration, which is what ISoundPlayback.Duration reports as "unknown".
    /// </summary>
    public static async Task<TimeSpan?> ProbeDurationAsync(string? ffprobePath, string filePath, CancellationToken ct)
    {
        if (ffprobePath is null)
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(filePath);

        try
        {
            using var probe = Start(psi);
            var output = await probe.StandardOutput.ReadToEndAsync(ct);
            await probe.WaitForExitAsync(ct);

            if (probe.ExitCode != 0
                || !double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds)
                || seconds <= 0)
            {
                return null;
            }

            return TimeSpan.FromSeconds(seconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task FeedAsync(Process ffmpeg, ReadOnlyMemory<byte> data)
    {
        try
        {
            await ffmpeg.StandardInput.BaseStream.WriteAsync(data);
            ffmpeg.StandardInput.Close();
        }
        catch
        {
            // Broken pipe: ffmpeg exited or was killed. Nothing to do.
        }
    }

    private static async Task PipeAsync(Process source, Process sink)
    {
        try
        {
            await source.StandardOutput.BaseStream.CopyToAsync(sink.StandardInput.BaseStream);
            sink.StandardInput.Close();
        }
        catch
        {
            // Broken pipe: one side exited or was killed. Nothing to do.
        }
    }
}

internal sealed class AudioFileService : IAudioFileService
{
    private readonly ToolManager _tools;

    public AudioFileService(ToolManager tools)
        => _tools = tools;

    public async Task<IPcmAudioStream> OpenFileAsync(string path, CancellationToken ct = default)
    {
        var ffmpeg = RequireFfmpeg();
        if (!File.Exists(path))
        {
            throw new SoundPlayerException(PlaybackErrorReason.SourceNotFound, $"Audio file not found: {path}");
        }

        var stream = new FfmpegPcmStream(
            startAt => FfmpegProcess.StartForFile(ffmpeg, path, startAt),
            canSeek: true,
            duration: await FfmpegProcess.ProbeDurationAsync(_tools.FfprobePath, path, ct));
        await stream.PrimeAsync(ct);
        return stream;
    }

    public async Task<IPcmAudioStream> OpenBufferAsync(ReadOnlyMemory<byte> encodedData, CancellationToken ct = default)
    {
        var ffmpeg = RequireFfmpeg();
        if (encodedData.IsEmpty)
        {
            throw new SoundPlayerException(PlaybackErrorReason.SourceNotFound, "The audio buffer is empty.");
        }

        var stream = new FfmpegPcmStream(
            startAt => FfmpegProcess.StartForBuffer(ffmpeg, encodedData, startAt),
            canSeek: true,
            duration: null);
        await stream.PrimeAsync(ct);
        return stream;
    }

    private string RequireFfmpeg()
        => _tools.FfmpegPath ?? throw new SoundPlayerException(
            PlaybackErrorReason.FfmpegNotFound,
            _tools.Downloading ? "ffmpeg is still being downloaded; try again shortly." : "ffmpeg executable not found.");
}

internal sealed class NetworkAudioService : INetworkAudioService
{
    private readonly ToolManager _tools;

    public NetworkAudioService(ToolManager tools)
        => _tools = tools;

    public async Task<IPcmAudioStream> OpenUrlAsync(string url, CancellationToken ct = default)
    {
        var (ffmpeg, ytdlp) = RequireTools();

        var stream = new FfmpegPcmStream(
            _ => FfmpegProcess.StartForUrl(ffmpeg, ytdlp, _tools, url),
            canSeek: false,
            duration: null);
        await stream.PrimeAsync(ct);
        return stream;
    }

    public async Task<AudioMetadata> GetMetadataAsync(string url, CancellationToken ct = default)
    {
        var (_, ytdlp) = RequireTools();

        var psi = new ProcessStartInfo
        {
            FileName = ytdlp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-download");
        FfmpegProcess.ConfigureYtdlp(psi, _tools);
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add(url);

        using var process = Process.Start(psi)
            ?? throw new SoundPlayerException(PlaybackErrorReason.UrlResolveFailed, "Failed to start yt-dlp.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new SoundPlayerException(PlaybackErrorReason.UrlResolveFailed,
                $"yt-dlp exited with code {process.ExitCode}: {(await stderrTask).Trim()}");
        }

        try
        {
            using var json = JsonDocument.Parse(await stdoutTask);
            var root = json.RootElement;
            return new AudioMetadata(
                Title: root.TryGetProperty("title", out var title) ? title.GetString() : null,
                Duration: root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(duration.GetDouble())
                    : null,
                Uploader: root.TryGetProperty("uploader", out var uploader) ? uploader.GetString() : null);
        }
        catch (JsonException ex)
        {
            throw new SoundPlayerException(PlaybackErrorReason.UrlResolveFailed, "Failed to parse yt-dlp metadata output.", ex);
        }
    }

    private (string Ffmpeg, string Ytdlp) RequireTools()
    {
        var ffmpeg = _tools.FfmpegPath ?? throw new SoundPlayerException(
            PlaybackErrorReason.FfmpegNotFound,
            _tools.Downloading ? "ffmpeg is still being downloaded; try again shortly." : "ffmpeg executable not found.");
        var ytdlp = _tools.YtdlpPath ?? throw new SoundPlayerException(
            PlaybackErrorReason.YtdlpNotFound,
            _tools.Downloading ? "yt-dlp is still being downloaded; try again shortly." : "yt-dlp executable not found.");
        return (ffmpeg, ytdlp);
    }
}
