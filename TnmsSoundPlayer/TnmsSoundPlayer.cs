using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using TnmsSoundPlayer.Core;
using TnmsSoundPlayer.Media;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer;

public sealed class TnmsSoundPlayer : IModSharpModule, IGameListener
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

    private IConVar? _speakerModeConVar;
    private IConVar? _speakerEntityConVar;

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
            new NetworkAudioService(_logger, _tools));

        RegisterConVars();
        InstallSpeakerManager(_core);

        // Runs before SpeakerManager's own listener (game listeners are sorted by descending
        // priority, and it uses 0), so a map starts with the speaker configuration already applied.
        _sharedSystem.GetModSharp().InstallGameListener(this);
        ApplySpeakerConfig(logUnchanged: true);

        _sharedSystem.GetClientManager().InstallClientListener(_core);
        _pumpTimer = _sharedSystem.GetModSharp().PushTimer(_core.OnPump, 0.02, GameTimerFlags.Repeatable);

        _logger.LogInformation("TnmsSoundPlayer initialized.");
        return true;
    }

    private void RegisterConVars()
    {
        var conVars = _sharedSystem.GetConVarManager();

        _speakerModeConVar = conVars.CreateConVar(
            "tnms_sound_speaker_mode",
            (int)SpeakerMode.Entity,
            (int)SpeakerMode.Bot,
            (int)SpeakerMode.Entity,
            "How sounds are attributed to a speaker. 0 = bot: a bot holds a player slot and shows on "
            + "the scoreboard. 1 = entity: no bot and no slot used, but no scoreboard row either, so "
            + "the speaker has no name and players cannot mute it client-side. "
            + "Takes effect on the next map change.",
            ConVarFlags.Release);

        _speakerEntityConVar = conVars.CreateConVar(
            "tnms_sound_speaker_entity",
            SoundPlayerCore.DefaultSpeakerEntity,
            1,
            16383,
            "Entity index sounds are attributed to in speaker mode 1. The default sits at the top of "
            + "the range, clear of both players (1..maxplayers) and the indices maps allocate; move "
            + "it only to dodge a collision, since a real entity at this index would pull the audio "
            + "to wherever that entity is. Takes effect on the next map change.",
            ConVarFlags.Release);
    }

    /// <summary>
    /// Copies the speaker ConVars into the core and tells the speaker manager whether it may hold a
    /// bot. Called at load and at every map start, so changing a ConVar mid-map applies at the next
    /// one: switching modes creates or kicks a bot, and a map boundary is where that is least
    /// disruptive — the bot is torn down there anyway.
    /// </summary>
    /// <param name="logUnchanged">
    /// Log the resolved mode even when nothing changed. True at load, so the mode is always visible
    /// in the startup log; false on map changes, which would otherwise repeat it forever.
    /// </param>
    private void ApplySpeakerConfig(bool logUnchanged)
    {
        if (_core is not { } core)
        {
            return;
        }

        var mode = _speakerModeConVar?.GetInt32() == (int)SpeakerMode.Entity
            ? SpeakerMode.Entity
            : SpeakerMode.Bot;
        var entity = _speakerEntityConVar?.GetInt32() ?? SoundPlayerCore.DefaultSpeakerEntity;
        var changed = core.Mode != mode || core.SpeakerEntity != entity;

        core.Mode = mode;
        core.SpeakerEntity = entity;

        // Entity mode keeps the manager wired (so SpeakerName and SpeakerSteamId still resolve
        // through it) but stops it holding a bot, which is what frees the slot.
        if (_speaker is { } speaker)
        {
            speaker.BotEnabled = mode == SpeakerMode.Bot;
        }

        if (!changed && !logUnchanged)
        {
            return;
        }

        if (mode == SpeakerMode.Entity)
        {
            _logger.LogInformation(
                "Speaker mode: entity (no player slot used), entity index {Entity}. The speaker has no "
                + "scoreboard row, so ITnmsSoundPlayer.SpeakerName is inert and players cannot mute it "
                + "from the client — use ISoundPlayerSession.SetHearing / SetPlayerVolume instead.",
                entity);
        }
        else
        {
            _logger.LogInformation(
                "Speaker mode: bot (occupies one player slot). Set tnms_sound_speaker_mode 1 to stop "
                + "spending a slot on it.");
        }
    }

    // ---- IGameListener ----

    public int ListenerVersion => IGameListener.ApiVersion;

    /// <summary>Above SpeakerManager's 0 so the configuration is in place before it looks at it.</summary>
    public int ListenerPriority => 100;

    public void OnServerActivate()
        => ApplySpeakerConfig(logUnchanged: false);

    /// <summary>Creates the speaker bot manager and points the core's speaker identity at it.</summary>
    private void InstallSpeakerManager(SoundPlayerCore core)
    {
        var speaker = new SpeakerManager(
            _logger,
            _sharedSystem.GetModSharp(),
            _sharedSystem.GetClientManager(),
            _sharedSystem.GetHookManager());
        _speaker = speaker;

        speaker.InstallHooks();
        core.ClientDisconnected += speaker.OnClientDisconnected;
        core.ClientConnected += speaker.OnClientConnected;

        speaker.SpeakerSlotChanged += slot => core.SpeakerSlot = slot;
        speaker.SpeakerXuidChanged += xuid => core.SpeakerXuid = xuid;
        core.SpeakerSteamIdReader = () => speaker.SpoofSteamId;
        core.SpeakerSteamIdWriter = speaker.SetSpoofSteamId;
        core.SpeakerNameReader = () => speaker.SpeakerName;
        core.SpeakerNameWriter = name => speaker.SpeakerName = name;
        core.SpeakerNameOverrideWriter = speaker.SetNameOverride;
        _sharedSystem.GetModSharp().InstallGameListener(speaker);

        if (_hotReload)
        {
            // Reloaded mid-map: OnServerActivate already fired, request the bot ourselves. EnsureBot
            // is a no-op while BotEnabled is false, so this stays correct in Entity mode.
            _sharedSystem.GetModSharp().PushTimer(() => speaker.EnsureBot(), 1.0, GameTimerFlags.StopOnMapEnd);
        }
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

        _sharedSystem.GetModSharp().RemoveGameListener(this);

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
