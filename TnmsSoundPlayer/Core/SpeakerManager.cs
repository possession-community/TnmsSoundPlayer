using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace TnmsSoundPlayer.Core;

/// <summary>
/// Maintains the resident speaker bot: requested via the game's own bot manager
/// ("bot_add" server command) on every map start, captured when it connects, renamed and
/// parked in spectator, then used as the voice attribution slot. Forgotten on map end
/// (the engine tears clients down with the map).
/// Raw IEngineServer::CreateFakeClient is NOT used: it crashes natively in CS2
/// (verified 2026-08-17; samyycX's Audio hit the same wall — "calling CreateFakeClient
/// ... will cause weird bug" — and used bot_add-style bots instead).
/// </summary>
internal sealed class SpeakerManager : IGameListener
{
    public const string DefaultBotName = "TnmsSpeaker";

    /// <summary>Value m_iPawnBotDifficulty carries for a human player.</summary>
    private const int HumanBotDifficulty = -1;

    /// <summary>
    /// The controller mirrors several pawn-derived fields for the scoreboard every tick, so a
    /// one-shot write can be reverted before anyone looks at it. Re-apply on a slow timer.
    /// </summary>
    private const double ReapplyInterval = 1.0;

    /// <summary>How many polite SwitchTeam attempts to make before forcing the team change.</summary>
    private const int SwitchTeamAttemptsBeforeForcing = 3;

    /// <summary>How long to wait for bot_add to produce a client before asking again.</summary>
    private const double BotRequestTimeout = 5.0;

    /// <summary>Watchdog period. Must exceed <see cref="BotRequestTimeout" /> so retries do not stack.</summary>
    private const double BotWatchdogInterval = 6.0;

    /// <summary>Retries to make before complaining that bot_add is not producing anything.</summary>
    private const int BotRequestAttemptsBeforeWarning = 5;

    private readonly ILogger _logger;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IHookManager _hooks;

    private string _speakerName = DefaultBotName;
    private string? _nameOverride;
    private string? _appliedName;
    private int _botSlot = -1;
    private bool _awaitingBot;
    private DateTime _awaitingDeadline;
    private Guid? _reapplyTimer;
    private Guid? _watchdogTimer;
    private int _spectatorCorrections;
    private int _botRequestAttempts;

    /// <summary>
    /// SteamID64 the speaker bot masquerades as (0 = no spoofing, the default). Set through
    /// ITnmsSoundPlayer.SpeakerSteamId; deliberately not hardcoded, since it names a real account.
    /// Applied automatically after every bot creation.
    /// </summary>
    public ulong SpoofSteamId { get; private set; }

    /// <summary>
    /// Name the speaker bot carries whenever no playback overrides it. Set through
    /// ITnmsSoundPlayer.SpeakerName; blank falls back to <see cref="DefaultBotName" />.
    /// </summary>
    public string SpeakerName
    {
        get => _speakerName;
        set
        {
            _speakerName = string.IsNullOrWhiteSpace(value) ? DefaultBotName : value;
            ApplyName();
        }
    }

    /// <summary>Raised on the game thread with the new speaker slot (-1 when the bot is gone).</summary>
    public event Action<int>? SpeakerSlotChanged;

    /// <summary>Raised on the game thread when the voice attribution xuid should change.</summary>
    public event Action<ulong>? SpeakerXuidChanged;

    public SpeakerManager(ILogger logger, IModSharp modSharp, IClientManager clients, IHookManager hooks)
    {
        _logger = logger;
        _modSharp = modSharp;
        _clients = clients;
        _hooks = hooks;
    }

    /// <summary>Starts blocking team changes that would pull the speaker bot out of spectator.</summary>
    public void InstallHooks()
        => _hooks.HandleCommandJoinTeam.InstallHookPre(OnHandleCommandJoinTeam);

    public void RemoveHooks()
        => _hooks.HandleCommandJoinTeam.RemoveHookPre(OnHandleCommandJoinTeam);

    /// <summary>
    /// The game's bot manager keeps assigning bots to playing teams to satisfy bot_quota, which would
    /// pull the speaker out of spectator and give it a live pawn.
    /// Rewrite the requested team to spectator rather than refusing the call: refusing left the
    /// controller stuck on <see cref="CStrikeTeam.UnAssigned" /> (so it showed in no scoreboard section
    /// at all), whereas redirecting lets the game's own join path run and park the bot where we want it.
    /// </summary>
    private HookReturnValue<bool> OnHandleCommandJoinTeam(
        IHandleCommandJoinTeamHookParams @params, HookReturnValue<bool> ret)
    {
        if (_botSlot < 0
            || @params.Client.Slot.AsPrimitive() != _botSlot
            || @params.Team == (int)CStrikeTeam.Spectator)
        {
            return ret;
        }

        @params.OverrideTeam((int)CStrikeTeam.Spectator);
        return new HookReturnValue<bool>(EHookAction.ChangeParamReturnDefault);
    }

    /// <summary>Slot of the speaker bot, or -1 when none exists.</summary>
    public int BotSlot => _botSlot;

    public IGameClient? BotClient
        => _botSlot >= 0 ? _clients.GetGameClient(new PlayerSlot((byte)_botSlot)) : null;

    // ---- IGameListener (map lifecycle) ----

    public int ListenerVersion => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public void OnServerActivate()
    {
        _botRequestAttempts = 0;

        // On an empty server the game never really starts and bot_add produces nothing, which is what
        // filled the log with "vanished before configuration" during idle map cycling. Ask now only if
        // somebody is already here (a map change with players on); otherwise the first human joining
        // triggers it via OnClientConnected, and the watchdog keeps retrying while anyone is present.
        StartWatchdog();

        if (HasHumanClient())
        {
            EnsureBot();
        }
    }

    public void OnGamePreShutdown()
    {
        StopWatchdog();
        Forget(); // The engine tears the client down with the map; just drop our reference.
    }

    // ---- name ----

    /// <summary>
    /// Sets the per-playback name override, or clears it with null. The core calls this every pump
    /// tick with the name of whatever is currently audible, so it must stay cheap: the actual
    /// SetName only happens when the resolved name really changes.
    /// </summary>
    public void SetNameOverride(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? null : name;
        if (_nameOverride == normalized)
        {
            return;
        }

        _nameOverride = normalized;
        ApplyName();
    }

    private string ResolvedName => _nameOverride ?? _speakerName;

    private void ApplyName()
    {
        if (BotClient is not { } client)
        {
            _appliedName = null;
            return;
        }

        var name = ResolvedName;
        if (_appliedName == name)
        {
            return;
        }

        client.SetName(name);
        _appliedName = name;
    }

    // ---- bot management (game thread only) ----

    /// <summary>Requests the speaker bot via bot_add if missing; renames it when it already exists.</summary>
    public void EnsureBot(string? name = null)
    {
        if (name is not null)
        {
            SpeakerName = name;
        }

        // Re-arm the watchdog: a manual respawn after a kick should start requesting again.
        StartWatchdog();

        if (BotClient is not null)
        {
            ApplyName();
            return;
        }

        if (_awaitingBot && DateTime.UtcNow < _awaitingDeadline)
        {
            return;
        }

        _awaitingBot = true;
        _awaitingDeadline = DateTime.UtcNow.AddSeconds(BotRequestTimeout);
        _botRequestAttempts++;
        _modSharp.ServerCommand("bot_add");

        if (_botRequestAttempts == 1)
        {
            _logger.LogInformation("Speaker bot requested via bot_add; waiting for it to connect.");
        }
        else if (_botRequestAttempts == BotRequestAttemptsBeforeWarning)
        {
            _logger.LogWarning(
                "bot_add has not produced a speaker bot after {Count} attempts; "
                + "check bot_quota / bot_quota_mode / bot_join_after_player.",
                _botRequestAttempts);
        }
    }

    /// <summary>
    /// Wired to client-connected notifications. Captures the bot we asked for, and treats the arrival of
    /// the first human as the cue to ask for one (bots cannot be added while the server sits empty).
    /// </summary>
    public void OnClientConnected(IGameClient client)
    {
        TryCaptureBot(client);

        if (client.IsFakeClient || client.IsHltv || _botSlot >= 0)
        {
            return;
        }

        // Give the joining player a moment to finish connecting before the game accepts bot_add.
        _modSharp.PushTimer(() => EnsureBot(), 1.5, GameTimerFlags.StopOnMapEnd);
    }

    private bool HasHumanClient()
    {
        foreach (var client in _clients.GetGameClientList(true))
        {
            if (!client.IsFakeClient && !client.IsHltv)
            {
                return true;
            }
        }

        return false;
    }

    private void StartWatchdog()
    {
        if (_watchdogTimer is not null)
        {
            return;
        }

        _watchdogTimer = _modSharp.PushTimer(
            WatchdogTick, BotWatchdogInterval, GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);
    }

    private void StopWatchdog()
    {
        if (_watchdogTimer is { } timer)
        {
            _modSharp.StopTimer(timer);
            _watchdogTimer = null;
        }
    }

    /// <summary>Re-requests the bot if it never arrived or went away, but only while someone is watching.</summary>
    private void WatchdogTick()
    {
        if (_botSlot >= 0 || !HasHumanClient())
        {
            return;
        }

        EnsureBot();
    }

    private void TryCaptureBot(IGameClient client)
    {
        if (!_awaitingBot || !client.IsFakeClient || client.IsHltv || _botSlot >= 0)
        {
            return;
        }

        if (DateTime.UtcNow >= _awaitingDeadline)
        {
            _awaitingBot = false;
            return;
        }

        _awaitingBot = false;
        _botSlot = client.Slot.AsPrimitive();
        _botRequestAttempts = 0;
        _logger.LogInformation("Speaker bot captured in slot {Slot}; configuring shortly.", _botSlot);

        // Before anything else: the quota manager kicks surplus bots within a few ticks, which is what
        // produced the "vanished before configuration" warnings.
        EnforceUnkickable();

        // Let the controller finish spawning before renaming / moving it.
        _modSharp.PushTimer(Configure, 0.3, GameTimerFlags.StopOnMapEnd);
    }

    private void Configure()
    {
        if (BotClient is not { } client)
        {
            _logger.LogWarning("Speaker bot vanished before configuration.");
            Forget();
            return;
        }

        ApplyName();
        client.GetPlayerController()?.SwitchTeam(CStrikeTeam.Spectator);
        _logger.LogInformation("Speaker bot '{Name}' configured in slot {Slot} (spectator).", ResolvedName, _botSlot);

        SpeakerSlotChanged?.Invoke(_botSlot);

        // Spoof after the engine settles (SetName/SwitchTeam may rewrite userinfo).
        _modSharp.PushTimer(ApplySpoof, 0.5, GameTimerFlags.StopOnMapEnd);
    }

    /// <summary>Sets the spoof identity and applies it immediately.</summary>
    public void SetSpoofSteamId(ulong steamId)
    {
        SpoofSteamId = steamId;
        ApplySpoof();

        // Changing identity on a bot clients already have costs one full update: the row has to be
        // rebuilt around the new xuid. It resets client-side prediction (the view snaps), so it is
        // confined to this explicit call — bot creation does not need it, since the first snapshot
        // clients ever receive of the bot already carries the disguise.
        if (BotClient is null)
        {
            return;
        }

        foreach (var connected in _clients.GetGameClientList(true))
        {
            if (!connected.IsFakeClient && !connected.IsHltv)
            {
                connected.ForceFullUpdate();
            }
        }
    }

    /// <summary>Makes the speaker bot look like a real player rather than a bot.</summary>
    public void ApplySpoof()
    {
        if (BotClient is null)
        {
            return;
        }

        ApplyEntityTraits();
        StartReapplyTimer();

        if (SpoofSteamId == 0)
        {
            _logger.LogInformation(
                "Speaker bot in slot {Slot} is left as a plain bot; set ITnmsSoundPlayer.SpeakerSteamId "
                + "to a SteamID64 you control to hide it on the scoreboard.",
                _botSlot);
            return;
        }

        SpeakerXuidChanged?.Invoke(SpoofSteamId);
        _logger.LogInformation(
            "Speaker bot disguise applied: steamId={SteamId} (slot {Slot}).", SpoofSteamId, _botSlot);
    }

    /// <summary>
    /// Applies the traits that keep the scoreboard from marking the speaker as a bot. Also the
    /// re-apply timer body: the controller re-derives its scoreboard mirror fields from the pawn, so a
    /// one-shot write can silently revert (m_iPawnBotDifficulty does exactly that — it is back to the
    /// bot value within a tick).
    /// The three that demonstrably drive the "BOT" marker are m_steamID, the controller's FL_FAKECLIENT
    /// and the pawn's FL_BOT; the rest were measured as no-ops on a bot_add bot (empty clan tag,
    /// m_bControllingBot already false) but are written anyway, because the marker came back in testing
    /// whenever the set was narrowed (verified in-game 2026-08-17).
    /// </summary>
    private void ApplyEntityTraits()
    {
        if (BotClient?.GetPlayerController() is not { } controller)
        {
            return;
        }

        // All of this is one package. Clearing the bot flags without a real SteamID64 leaves a client
        // the scoreboard cannot resolve, and the row vanishes entirely rather than falling back to
        // "BOT" (verified in-game 2026-08-20). A visible bot beats an invisible speaker, so with no
        // id set the bot is left exactly as the game made it.
        if (SpoofSteamId == 0)
        {
            return;
        }

        controller.SetNetVar("m_steamID", SpoofSteamId);

        // m_szClan is a CUtlSymbolLarge, so use the dedicated API, not a raw string SetNetVar.
        controller.SetClanTag(string.Empty);
        SetFlags(controller, controller.Flags & ~EntityFlags.FakeClient);

        if (controller.FindNetVar("m_iPawnBotDifficulty"))
        {
            controller.SetNetVar("m_iPawnBotDifficulty", HumanBotDifficulty);
        }

        if (controller.FindNetVar("m_bControllingBot"))
        {
            controller.SetNetVar("m_bControllingBot", false);
        }

        if (controller.GetPawn() is { } pawn)
        {
            SetFlags(pawn, pawn.Flags & ~EntityFlags.Bot);
        }
    }

    /// <summary>Timer body: keep the disguise applied and the bot parked, alive, in spectator.</summary>
    private void Reapply()
    {
        ApplyEntityTraits();
        EnforceSpectator();
        EnforceUnkickable();
    }

    /// <summary>
    /// bot_quota_mode is "fill" in the stock gamemode configs, so the bot manager kicks bots to keep the
    /// player count at the quota — including ours, which is why the speaker kept disappearing right after
    /// being captured. m_bCannotBeKicked is the game's own opt-out from that bookkeeping.
    /// </summary>
    private void EnforceUnkickable()
    {
        if (BotClient?.GetPlayerController() is not { } controller
            || !controller.FindNetVar("m_bCannotBeKicked")
            || controller.GetNetVar<bool>("m_bCannotBeKicked"))
        {
            return;
        }

        controller.SetNetVar("m_bCannotBeKicked", true);
    }

    /// <summary>
    /// Puts the bot back in spectator if something moved it anyway. <see cref="OnHandleCommandJoinTeam"/>
    /// only covers CCSPlayerController::HandleCommandJoinTeam; the bot manager can also call ChangeTeam
    /// directly, which that hook never sees.
    /// </summary>
    private void EnforceSpectator()
    {
        if (BotClient?.GetPlayerController() is not { } controller
            || controller.Team == CStrikeTeam.Spectator)
        {
            return;
        }

        var from = controller.Team;
        _spectatorCorrections++;

        // SwitchTeam runs the game's own switch path, which is what we want when it works. It did not
        // move this bot at all in testing (the controller sat on UnAssigned through 21 attempts), so
        // fall back to the raw entity ChangeTeam once it has clearly had its chance.
        var forced = _spectatorCorrections > SwitchTeamAttemptsBeforeForcing;
        if (forced)
        {
            controller.ChangeTeam(CStrikeTeam.Spectator);
        }
        else
        {
            controller.SwitchTeam(CStrikeTeam.Spectator);
        }

        // A steady drip means something is fighting us and neither path is holding.
        if (_spectatorCorrections <= 3 || _spectatorCorrections == SwitchTeamAttemptsBeforeForcing + 1)
        {
            _logger.LogWarning(
                "Speaker bot was on team {Team}; moved back to spectator via {Method} (correction #{Count}).",
                from, forced ? "ChangeTeam" : "SwitchTeam", _spectatorCorrections);
        }
    }

    /// <summary>Writes m_fFlags through SetNetVar so NetworkStateChanged fires for sure.</summary>
    private static void SetFlags(IBaseEntity entity, EntityFlags flags)
        => entity.SetNetVar("m_fFlags", (uint)flags);

    private void StartReapplyTimer()
    {
        if (_reapplyTimer is not null)
        {
            return;
        }

        _reapplyTimer = _modSharp.PushTimer(
            Reapply, ReapplyInterval, GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);
    }

    private void StopReapplyTimer()
    {
        if (_reapplyTimer is { } timer)
        {
            _modSharp.StopTimer(timer);
            _reapplyTimer = null;
        }
    }

    /// <summary>Kicks the speaker bot if one exists, and stops the watchdog from bringing it back.</summary>
    public void RemoveBot()
    {
        StopWatchdog();

        if (BotClient is { } client)
        {
            _clients.KickClient(client, "TnmsSoundPlayer speaker removed");
        }
        Forget();
    }

    /// <summary>Forgets the bot when it disconnects for any other reason.</summary>
    public void OnClientDisconnected(IGameClient client)
    {
        if (_botSlot >= 0 && client.Slot.AsPrimitive() == _botSlot)
        {
            Forget();
        }
    }

    private void Forget()
    {
        _awaitingBot = false;
        StopReapplyTimer();

        // Describes a client that no longer exists; the next bot must be named from scratch.
        _appliedName = null;
        _spectatorCorrections = 0;

        if (_botSlot < 0)
        {
            return;
        }
        _botSlot = -1;
        SpeakerSlotChanged?.Invoke(-1);
    }
}
