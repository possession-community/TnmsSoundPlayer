using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using TnmsSoundPlayer.Core;
using TnmsSoundPlayer.Media;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer;

public sealed class TnmsSoundPlayer : IModSharpModule
{
    public string DisplayName => "TnmsSoundPlayer";
    public string DisplayAuthor => "faketuna";

    private readonly ILogger<TnmsSoundPlayer> _logger;
    private readonly ISharedSystem _sharedSystem;
    private readonly string _moduleDirectory;
    private readonly bool _hotReload;

    private ToolManager? _tools;
    private SoundPlayerCore? _core;
    private SpeakerManager? _speaker;
    private Guid? _pumpTimer;

    public TnmsSoundPlayer(
        ISharedSystem sharedSystem, string dllPath, string sharpPath,
        Version? version, IConfiguration coreConfiguration, bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<TnmsSoundPlayer>();
        // ModSharp passes the module *directory* here despite the parameter name.
        _moduleDirectory = dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dllPath) ?? AppContext.BaseDirectory
            : dllPath;
        _hotReload = hotReload;
    }

    public bool Init()
    {
        _tools = new ToolManager(_logger, _moduleDirectory);
        _tools.Initialize();

        _core = new SoundPlayerCore(
            _logger,
            _sharedSystem.GetModSharp(),
            _sharedSystem.GetClientManager(),
            _tools,
            new AudioFileService(_tools),
            new NetworkAudioService(_tools));

        _speaker = new SpeakerManager(
            _logger,
            _sharedSystem.GetModSharp(),
            _sharedSystem.GetClientManager(),
            _sharedSystem.GetHookManager());
        _speaker.InstallHooks();
        _core.ClientDisconnected += _speaker.OnClientDisconnected;
        _core.ClientConnected += _speaker.OnClientConnected;

        var core = _core;
        var speaker = _speaker;
        _speaker.SpeakerSlotChanged += slot => core.SpeakerSlot = slot;
        _speaker.SpeakerXuidChanged += xuid => core.SpeakerXuid = xuid;
        _core.SpeakerSteamIdReader = () => speaker.SpoofSteamId;
        _core.SpeakerSteamIdWriter = speaker.SetSpoofSteamId;
        _core.SpeakerNameReader = () => speaker.SpeakerName;
        _core.SpeakerNameWriter = name => speaker.SpeakerName = name;
        _core.SpeakerNameOverrideWriter = speaker.SetNameOverride;
        _sharedSystem.GetModSharp().InstallGameListener(_speaker);

        if (_hotReload)
        {
            // Reloaded mid-map: OnServerActivate already fired, request the bot ourselves.
            _sharedSystem.GetModSharp().PushTimer(() => speaker.EnsureBot(), 1.0, GameTimerFlags.StopOnMapEnd);
        }

        _sharedSystem.GetClientManager().InstallClientListener(_core);
        _pumpTimer = _sharedSystem.GetModSharp().PushTimer(_core.OnPump, 0.02, GameTimerFlags.Repeatable);

        _logger.LogInformation("TnmsSoundPlayer initialized.");
        return true;
    }

    public void PostInit()
    {
        _sharedSystem.GetSharpModuleManager()
            .RegisterSharpModuleInterface<ITnmsSoundPlayer>(this, ITnmsSoundPlayer.Identity, _core!);
    }

    public void Shutdown()
    {
        if (_pumpTimer is { } timer)
        {
            _sharedSystem.GetModSharp().StopTimer(timer);
            _pumpTimer = null;
        }

        if (_speaker is { } speaker)
        {
            speaker.RemoveHooks();
            _sharedSystem.GetModSharp().RemoveGameListener(speaker);
            speaker.RemoveBot();
            _speaker = null;
        }

        if (_core is { } core)
        {
            _sharedSystem.GetClientManager().RemoveClientListener(core);
            core.Shutdown();
            _core = null;
        }

        _logger.LogInformation("TnmsSoundPlayer shutdown.");
    }
}
