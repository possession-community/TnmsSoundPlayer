using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace TnmsSoundPlayer.Media;

/// <summary>
/// Locates ffmpeg / yt-dlp executables. Resolution order: module tools directory → PATH →
/// automatic download of an official build into the module tools directory.
/// </summary>
internal sealed class ToolManager
{
    private const string FfmpegWindowsZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string FfmpegLinuxTarUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-linux64-gpl.tar.xz";
    private const string YtdlpWindowsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtdlpLinuxUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux";
    // yt-dlp needs a JS runtime to solve YouTube's nsig challenge; without one downloads 403.
    private const string DenoWindowsZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    private const string DenoLinuxZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip";

    private readonly ILogger _logger;
    private readonly string _toolsDir;

    private volatile string? _ffmpegPath;
    private volatile string? _ffprobePath;
    private volatile string? _ytdlpPath;
    private volatile string? _denoPath;
    private volatile bool _downloading;

    public string? FfmpegPath => _ffmpegPath;

    /// <summary>Ships in the same archive as ffmpeg; used to read a local file's duration.</summary>
    public string? FfprobePath => _ffprobePath;

    public string? YtdlpPath => _ytdlpPath;
    public string? DenoPath => _denoPath;
    public bool Downloading => _downloading;
    public string ToolsDirectory => _toolsDir;

    /// <summary>Where downloaded-first URL sources are staged. Swept at startup.</summary>
    public string DownloadDirectory => Path.Combine(_toolsDir, "cache", "downloads");

    public ToolManager(ILogger logger, string moduleDirectory)
    {
        _logger = logger;
        _toolsDir = Path.Combine(moduleDirectory, "tools");
    }

    /// <summary>Resolves both tools, downloading missing ones in the background.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(_toolsDir);
        SweepDownloads();

        _ffmpegPath = Resolve(WindowsName("ffmpeg"));
        _ffprobePath = Resolve(WindowsName("ffprobe"));
        _ytdlpPath = Resolve(WindowsName("yt-dlp"), linuxAlias: "yt-dlp_linux");
        _denoPath = Resolve(WindowsName("deno"));

        if (_ffmpegPath is not null && _ffprobePath is not null && _ytdlpPath is not null && _denoPath is not null)
        {
            _logger.LogInformation("Tools resolved: ffmpeg={Ffmpeg}, ffprobe={Ffprobe}, yt-dlp={Ytdlp}, deno={Deno}",
                _ffmpegPath, _ffprobePath, _ytdlpPath, _denoPath);
            return;
        }

        _downloading = true;
        _ = Task.Run(DownloadMissingAsync);
    }

    /// <summary>
    /// Deletes staged downloads left behind by a crash or a hard shutdown. Safe at startup because
    /// nothing is playing yet, and these files are only ever owned by a live playback.
    /// </summary>
    private void SweepDownloads()
    {
        if (!Directory.Exists(DownloadDirectory))
        {
            return;
        }

        var swept = 0;
        foreach (var file in Directory.EnumerateFiles(DownloadDirectory))
        {
            try
            {
                File.Delete(file);
                swept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by something; try again next start.
            }
        }

        if (swept > 0)
        {
            _logger.LogInformation("Removed {Count} leftover staged download(s).", swept);
        }
    }

    private static string WindowsName(string name)
        => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private string? Resolve(string fileName, string? linuxAlias = null)
    {
        var local = Path.Combine(_toolsDir, fileName);
        if (File.Exists(local))
        {
            return local;
        }

        if (!OperatingSystem.IsWindows() && linuxAlias is not null)
        {
            var aliasPath = Path.Combine(_toolsDir, linuxAlias);
            if (File.Exists(aliasPath))
            {
                return aliasPath;
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Malformed PATH entry; keep scanning.
            }
        }

        return null;
    }

    private async Task DownloadMissingAsync()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TnmsSoundPlayer/1.0");

        try
        {
            if (_ytdlpPath is null)
            {
                try
                {
                    _ytdlpPath = await DownloadYtdlpAsync(http);
                    _logger.LogInformation("yt-dlp downloaded to {Path}", _ytdlpPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download yt-dlp; URL playback will be unavailable.");
                }
            }

            // Both come out of the same archive, so a missing ffprobe is worth the download too.
            if (_ffmpegPath is null || _ffprobePath is null)
            {
                try
                {
                    (_ffmpegPath, _ffprobePath) = await DownloadFfmpegAsync(http);
                    _logger.LogInformation("ffmpeg downloaded to {Path} (ffprobe: {Probe})",
                        _ffmpegPath, _ffprobePath ?? "not in the archive");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download ffmpeg; playback will be unavailable.");
                }
            }

            if (_denoPath is null)
            {
                try
                {
                    _denoPath = await DownloadDenoAsync(http);
                    _logger.LogInformation("deno downloaded to {Path}", _denoPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download deno; YouTube downloads will likely fail with HTTP 403.");
                }
            }
        }
        finally
        {
            _downloading = false;
        }
    }

    private async Task<string> DownloadYtdlpAsync(HttpClient http)
    {
        var url = OperatingSystem.IsWindows() ? YtdlpWindowsUrl : YtdlpLinuxUrl;
        var target = Path.Combine(_toolsDir, WindowsName("yt-dlp"));
        var temp = target + ".tmp";

        await DownloadToFileAsync(http, url, temp);
        File.Move(temp, target, overwrite: true);
        MakeExecutable(target);
        return target;
    }

    /// <summary>
    /// Downloads the ffmpeg archive and lifts both binaries out of it. ffprobe is optional in the
    /// return value: a build that omits it still gives a working player, only without file durations.
    /// </summary>
    private async Task<(string Ffmpeg, string? Ffprobe)> DownloadFfmpegAsync(HttpClient http)
    {
        var target = Path.Combine(_toolsDir, WindowsName("ffmpeg"));
        var archive = Path.Combine(_toolsDir, OperatingSystem.IsWindows() ? "ffmpeg.zip" : "ffmpeg.tar.xz");

        await DownloadToFileAsync(http, OperatingSystem.IsWindows() ? FfmpegWindowsZipUrl : FfmpegLinuxTarUrl, archive);

        var extractDir = Path.Combine(_toolsDir, "ffmpeg_extract");
        try
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
            Directory.CreateDirectory(extractDir);

            if (OperatingSystem.IsWindows())
            {
                ZipFile.ExtractToDirectory(archive, extractDir);
            }
            else
            {
                // .NET cannot decompress xz; every mainstream Linux distro ships tar with xz support.
                var tar = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "tar",
                    ArgumentList = { "-xf", archive, "-C", extractDir },
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("Failed to start tar.");
                await tar.WaitForExitAsync();
                if (tar.ExitCode != 0)
                {
                    throw new InvalidOperationException($"tar exited with code {tar.ExitCode}.");
                }
            }

            var found = Directory.EnumerateFiles(extractDir, WindowsName("ffmpeg"), SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("ffmpeg binary not found in the downloaded archive.");

            File.Move(found, target, overwrite: true);
            MakeExecutable(target);

            string? probeTarget = null;
            if (Directory.EnumerateFiles(extractDir, WindowsName("ffprobe"), SearchOption.AllDirectories).FirstOrDefault() is { } probe)
            {
                probeTarget = Path.Combine(_toolsDir, WindowsName("ffprobe"));
                File.Move(probe, probeTarget, overwrite: true);
                MakeExecutable(probeTarget);
            }

            return (target, probeTarget);
        }
        finally
        {
            try
            {
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }
                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, recursive: true);
                }
            }
            catch
            {
                // Leftover temp files are harmless.
            }
        }
    }

    private async Task<string> DownloadDenoAsync(HttpClient http)
    {
        var target = Path.Combine(_toolsDir, WindowsName("deno"));
        var archive = Path.Combine(_toolsDir, "deno.zip");

        await DownloadToFileAsync(http, OperatingSystem.IsWindows() ? DenoWindowsZipUrl : DenoLinuxZipUrl, archive);

        var extractDir = Path.Combine(_toolsDir, "deno_extract");
        try
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
            ZipFile.ExtractToDirectory(archive, extractDir);

            var found = Directory.EnumerateFiles(extractDir, WindowsName("deno"), SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("deno binary not found in the downloaded archive.");

            File.Move(found, target, overwrite: true);
            MakeExecutable(target);
            return target;
        }
        finally
        {
            try
            {
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }
                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, recursive: true);
                }
            }
            catch
            {
                // Leftover temp files are harmless.
            }
        }
    }

    private static async Task DownloadToFileAsync(HttpClient http, string url, string path)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file);
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
