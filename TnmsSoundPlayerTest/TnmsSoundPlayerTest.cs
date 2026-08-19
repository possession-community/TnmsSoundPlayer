using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayerTest;

/// <summary>
/// Manual test harness for TnmsSoundPlayer, focused on YouTube download + playback.
/// Registers sp_url / sp_meta / sp_stop / sp_status as client virtual commands
/// (chat: !sp_url, console: ms_sp_url).
/// </summary>
public sealed class TnmsSoundPlayerTest : IModSharpModule
{
    public string DisplayName => "TnmsSoundPlayer Test";
    public string DisplayAuthor => "faketuna";

    private const string SessionOwner = "TnmsSoundPlayerTest";

    private readonly ILogger<TnmsSoundPlayerTest> _logger;
    private readonly ISharedSystem _shared;
    private readonly Dictionary<string, IClientManager.DelegateClientCommand> _commands = [];

    private ITnmsSoundPlayer _player = null!;

    public TnmsSoundPlayerTest(
        ISharedSystem sharedSystem, string dllPath, string sharpPath,
        Version? version, IConfiguration coreConfiguration, bool hotReload)
    {
        _shared = sharedSystem;
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<TnmsSoundPlayerTest>();
    }

    public bool Init()
    {
        RegisterCommand("sp_url", OnUrl);
        RegisterCommand("sp_meta", OnMeta);
        RegisterCommand("sp_stop", OnStop);
        RegisterCommand("sp_status", OnStatus);

        _logger.LogInformation("TnmsSoundPlayerTest initialized, {Count} sp_* commands registered.", _commands.Count);
        return true;
    }

    public void Shutdown()
    {
        var clientManager = _shared.GetClientManager();
        foreach (var (name, callback) in _commands)
        {
            clientManager.RemoveCommandCallback(name, callback);
        }
        _commands.Clear();
    }

    public void OnAllModulesLoaded()
        => _player = _shared.GetSharpModuleManager()
            .GetRequiredSharpModuleInterface<ITnmsSoundPlayer>(ITnmsSoundPlayer.Identity).Instance!;

    private void RegisterCommand(string name, IClientManager.DelegateClientCommand callback)
    {
        _shared.GetClientManager().InstallCommandCallback(name, callback);
        _commands.Add(name, callback);
    }

    private static void Reply(IGameClient client, string message)
        => client.ConsolePrint($"[SP] {message}\n");

    /// <summary>
    /// The Source console strips everything after "//" as a comment, so an unquoted
    /// "https://..." arrives here truncated. Detect that and repair scheme-less URLs.
    /// </summary>
    private static bool TryNormalizeUrl(string raw, out string url)
    {
        url = raw.Trim().Trim('"');

        if (url is "https://" or "http://" or "https:" or "http:")
        {
            return false;
        }

        if (!url.Contains("://"))
        {
            url = "https://" + url;
        }
        return true;
    }

    private static string Describe(ISoundPlayback playback)
    {
        var error = playback.Error is { } e ? $" error={e.Reason}({e.Message})" : string.Empty;
        return $"#{playback.Id} [{playback.State}] owner={playback.OwnerName} pos={playback.Position:mm\\:ss\\.fff} dur={playback.Duration?.ToString(@"mm\:ss\.fff") ?? "?"} vol={playback.Volume.ToString("0.##", CultureInfo.InvariantCulture)}{error}";
    }

    private ECommandAction OnUrl(IGameClient client, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            Reply(client, "usage: sp_url <url> [volume]");
            return ECommandAction.Stopped;
        }

        var volume = 1.0f;
        if (command.ArgCount >= 2
            && !float.TryParse(command.GetArg(2), NumberStyles.Float, CultureInfo.InvariantCulture, out volume))
        {
            Reply(client, "invalid volume");
            return ECommandAction.Stopped;
        }

        if (!TryNormalizeUrl(command.GetArg(1), out var url))
        {
            Reply(client, "the console ate your URL after '//'. Quote it (\"https://...\"), drop the scheme (www.youtube.com/...), or use chat (!sp_url).");
            return ECommandAction.Stopped;
        }

        var session = _player.CreateSession(SessionOwner);
        var playback = session.PlayUrl(url, new PlayOptions { Volume = volume }, new ReportToClient(client));

        Reply(client, $"queued: {Describe(playback)}");
        return ECommandAction.Stopped;
    }

    /// <summary>Reports a playback's progress back to the player who requested it.</summary>
    private sealed class ReportToClient(IGameClient client) : ISoundPlaybackCallback
    {
        public void OnStarted(ISoundPlayback playback)
            => Reply(client, $"started: {Describe(playback)}");

        public void OnFinished(ISoundPlayback playback)
            => Reply(client, $"finished: {Describe(playback)}");
    }

    private ECommandAction OnMeta(IGameClient client, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            Reply(client, "usage: sp_meta <url>");
            return ECommandAction.Stopped;
        }

        if (!TryNormalizeUrl(command.GetArg(1), out var url))
        {
            Reply(client, "the console ate your URL after '//'. Quote it (\"https://...\"), drop the scheme (www.youtube.com/...), or use chat (!sp_meta).");
            return ECommandAction.Stopped;
        }

        // Result is logged instead of printed to the client: the continuation runs on a
        // worker thread, and IGameClient must only be touched from the game thread.
        _player.NetworkService.GetMetadataAsync(url).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                var m = t.Result;
                _logger.LogInformation("metadata of {Url}: title={Title} duration={Duration} uploader={Uploader}",
                    url, m.Title ?? "?", m.Duration?.ToString() ?? "?", m.Uploader ?? "?");
            }
            else
            {
                _logger.LogWarning("metadata fetch failed for {Url}: {Error}", url, t.Exception?.GetBaseException().Message);
            }
        }, TaskScheduler.Default);

        Reply(client, "fetching metadata, result goes to the server console...");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnStop(IGameClient client, StringCommand command)
    {
        if (_player.CurrentPlayback is not { } playback)
        {
            Reply(client, "nothing is playing.");
            return ECommandAction.Stopped;
        }

        playback.Stop();
        Reply(client, Describe(playback));
        return ECommandAction.Stopped;
    }

    private ECommandAction OnStatus(IGameClient client, StringCommand command)
    {
        var d = _player.Diagnostics;
        Reply(client, $"ffmpeg={(d.FfmpegAvailable ? d.FfmpegPath : "MISSING")} yt-dlp={(d.YtdlpAvailable ? d.YtdlpPath : "MISSING")} queue={d.QueueLength} sessions={d.ActiveSessionCount}");
        Reply(client, _player.CurrentPlayback is { } current ? $"current: {Describe(current)}" : "current: (idle)");

        var queue = _player.Queue;
        for (var i = 0; i < queue.Count; i++)
        {
            Reply(client, $"queue[{i}]: {Describe(queue[i])}");
        }

        return ECommandAction.Stopped;
    }
}
