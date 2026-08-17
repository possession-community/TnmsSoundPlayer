using Google.Protobuf;
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
/// Individually toggleable traits that make the speaker bot look like a real player.
/// The scoreboard "BOT" marker needs all of these together: dropping any single one brought it back
/// (verified in-game 2026-08-17), so <see cref="All"/> is the working configuration.
/// Bit 0 is deliberately unused: it was a userinfo stringtable rewrite (fakeplayer=false + spoofed
/// xuid). It never took effect — the written entry could not be read back — and mask 127 was not
/// needed, so the blind once-per-second write was removed. Bit values are kept stable so the masks
/// recorded in IMPLEMENTATION_PLAN.md still mean the same thing.
/// </summary>
[Flags]
internal enum SpeakerDisguise
{
    None = 0,

    /// <summary>Set the controller's networked m_steamID to the spoof id.</summary>
    ControllerSteamId = 1 << 1,

    /// <summary>Clear m_szClan.</summary>
    ClanTag = 1 << 2,

    /// <summary>Clear FL_FAKECLIENT (0x100) from the controller's m_fFlags.</summary>
    ControllerFlags = 1 << 3,

    /// <summary>Set m_iPawnBotDifficulty to -1 (the value real players carry).</summary>
    BotDifficulty = 1 << 4,

    /// <summary>Clear m_bControllingBot.</summary>
    ControllingBot = 1 << 5,

    /// <summary>Clear FL_BOT (0x10) from the pawn's m_fFlags.</summary>
    PawnFlags = 1 << 6,

    All = ControllerSteamId | ClanTag | ControllerFlags | BotDifficulty | ControllingBot | PawnFlags,
}

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

    private string _desiredName = DefaultBotName;
    private int _botSlot = -1;
    private bool _awaitingBot;
    private DateTime _awaitingDeadline;
    private Guid? _reapplyTimer;
    private Guid? _watchdogTimer;
    private int _spectatorCorrections;
    private int _botRequestAttempts;

    // Pre-spoof values, captured lazily the first time each trait is applied. Turning a trait back off
    // restores these, so a mask change actually isolates one trait instead of leaving earlier edits behind.
    private ulong? _originalSteamId;
    private string? _originalClanTag;
    private EntityFlags? _originalControllerFlags;
    private int? _originalBotDifficulty;
    private bool? _originalControllingBot;
    private EntityFlags? _originalPawnFlags;

    /// <summary>
    /// SteamID64 the speaker bot masquerades as (0 = no spoofing, the default). Set through
    /// ITnmsSoundPlayer.SpeakerSteamId; deliberately not hardcoded, since it names a real account.
    /// Applied automatically after every bot creation.
    /// </summary>
    public ulong SpoofSteamId { get; private set; }

    /// <summary>Which disguise traits are currently applied.</summary>
    public SpeakerDisguise Disguise { get; private set; } = SpeakerDisguise.All;

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

    // ---- bot management (game thread only) ----

    /// <summary>Requests the speaker bot via bot_add if missing; renames it when it already exists.</summary>
    public void EnsureBot(string? name = null)
    {
        if (name is not null)
        {
            _desiredName = name;
        }

        // Re-arm the watchdog: a manual sp_spk_bot after sp_spk_kick should start respawning again.
        StartWatchdog();

        if (BotClient is { } existing)
        {
            existing.SetName(_desiredName);
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

        client.SetName(_desiredName);
        client.GetPlayerController()?.SwitchTeam(CStrikeTeam.Spectator);
        _logger.LogInformation("Speaker bot '{Name}' configured in slot {Slot} (spectator).", _desiredName, _botSlot);

        SpeakerSlotChanged?.Invoke(_botSlot);

        // Spoof after the engine settles (SetName/SwitchTeam may rewrite userinfo).
        _modSharp.PushTimer(ApplySpoof, 0.5, GameTimerFlags.StopOnMapEnd);
    }

    /// <summary>Sets the spoof identity and applies it immediately.</summary>
    public void SetSpoofSteamId(ulong steamId)
    {
        SpoofSteamId = steamId;
        ApplySpoof();
    }

    /// <summary>Selects which disguise traits to apply and re-applies them immediately.</summary>
    public void SetDisguise(SpeakerDisguise disguise)
    {
        Disguise = disguise;
        ApplySpoof();
    }

    /// <summary>
    /// Makes the speaker bot look like a real player, applying whichever traits <see cref="Disguise"/>
    /// selects.
    /// </summary>
    public void ApplySpoof()
    {
        if (BotClient is null)
        {
            return;
        }

        ApplyEntityTraits();

        if (SpoofSteamId != 0)
        {
            SpeakerXuidChanged?.Invoke(SpoofSteamId);
        }

        // Resend everything to clients that already have the bot in their snapshot, so the disguise
        // is visible without a reconnect. Only fires on bot creation and on the debug commands.
        foreach (var connected in _clients.GetGameClientList(true))
        {
            if (!connected.IsFakeClient && !connected.IsHltv)
            {
                connected.ForceFullUpdate();
            }
        }

        StartReapplyTimer();

        _logger.LogInformation(
            "Speaker bot disguise applied: steamId={SteamId} traits={Traits} (slot {Slot}).",
            SpoofSteamId, Disguise, _botSlot);
    }

    /// <summary>Reads the speaker bot's userinfo entry back, or null when absent/unparseable.</summary>
    private CMsgPlayerInfo? ReadUserInfo()
    {
        var raw = ReadUserInfoBytes();
        if (raw is null)
        {
            return null;
        }

        try
        {
            return CMsgPlayerInfo.Parser.ParseFrom(raw);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    private byte[]? ReadUserInfoBytes()
    {
        if (FindUserInfoIndex() is not { } index)
        {
            return null;
        }

        var table = _modSharp.FindStringTable("userinfo");
        if (table is null)
        {
            return null;
        }

        unsafe
        {
            var userData = table.GetStringUserData(index);
            if (userData is null || userData->Data is null || userData->Size <= 0)
            {
                return null;
            }

            return new ReadOnlySpan<byte>(userData->Data, userData->Size).ToArray();
        }
    }

    /// <summary>Index of the speaker bot's userinfo entry, or null when it cannot be located.</summary>
    private int? FindUserInfoIndex()
    {
        var table = _modSharp.FindStringTable("userinfo");
        if (table is null)
        {
            _logger.LogWarning("userinfo stringtable not found; cannot spoof the speaker bot.");
            return null;
        }

        var index = table.FindStringIndex(_botSlot.ToString());
        if (index < 0 && _botSlot < table.GetStringCount())
        {
            index = _botSlot;
        }
        if (index < 0)
        {
            _logger.LogWarning(
                "userinfo entry for slot {Slot} not found (count={Count}).", _botSlot, table.GetStringCount());
            return null;
        }

        return index;
    }

    /// <summary>
    /// Applies the entity-side traits. Also the re-apply timer body: the controller re-derives its
    /// scoreboard mirror fields from the pawn, so a one-shot write can silently revert
    /// (m_iPawnBotDifficulty does exactly that — it is back to the bot value within a tick).
    /// </summary>
    private void ApplyEntityTraits()
    {
        if (BotClient?.GetPlayerController() is not { } controller)
        {
            return;
        }

        if (Disguise.HasFlag(SpeakerDisguise.ControllerSteamId) && SpoofSteamId != 0)
        {
            _originalSteamId ??= controller.GetNetVar<ulong>("m_steamID");
            controller.SetNetVar("m_steamID", SpoofSteamId);
        }
        else if (_originalSteamId is { } steamId)
        {
            controller.SetNetVar("m_steamID", steamId);
            _originalSteamId = null;
        }

        if (Disguise.HasFlag(SpeakerDisguise.ClanTag))
        {
            _originalClanTag ??= controller.ClanTag;
            // m_szClan is a CUtlSymbolLarge, so use the dedicated API, not a raw string SetNetVar.
            controller.SetClanTag(string.Empty);
        }
        else if (_originalClanTag is { } clanTag)
        {
            controller.SetClanTag(clanTag);
            _originalClanTag = null;
        }

        if (Disguise.HasFlag(SpeakerDisguise.ControllerFlags))
        {
            _originalControllerFlags ??= controller.Flags;
            SetFlags(controller, controller.Flags & ~EntityFlags.FakeClient);
        }
        else if (_originalControllerFlags is { } controllerFlags)
        {
            SetFlags(controller, controllerFlags);
            _originalControllerFlags = null;
        }

        if (controller.FindNetVar("m_iPawnBotDifficulty"))
        {
            if (Disguise.HasFlag(SpeakerDisguise.BotDifficulty))
            {
                _originalBotDifficulty ??= controller.GetNetVar<int>("m_iPawnBotDifficulty");
                controller.SetNetVar("m_iPawnBotDifficulty", HumanBotDifficulty);
            }
            else if (_originalBotDifficulty is { } difficulty)
            {
                controller.SetNetVar("m_iPawnBotDifficulty", difficulty);
                _originalBotDifficulty = null;
            }
        }

        if (controller.FindNetVar("m_bControllingBot"))
        {
            if (Disguise.HasFlag(SpeakerDisguise.ControllingBot))
            {
                _originalControllingBot ??= controller.GetNetVar<bool>("m_bControllingBot");
                controller.SetNetVar("m_bControllingBot", false);
            }
            else if (_originalControllingBot is { } controllingBot)
            {
                controller.SetNetVar("m_bControllingBot", controllingBot);
                _originalControllingBot = null;
            }
        }

        if (controller.GetPawn() is { } pawn)
        {
            if (Disguise.HasFlag(SpeakerDisguise.PawnFlags))
            {
                _originalPawnFlags ??= pawn.Flags;
                SetFlags(pawn, pawn.Flags & ~EntityFlags.Bot);
            }
            else if (_originalPawnFlags is { } pawnFlags)
            {
                SetFlags(pawn, pawnFlags);
                _originalPawnFlags = null;
            }
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
        if (_reapplyTimer is not null || Disguise == SpeakerDisguise.None)
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

    /// <summary>Reads back what the server currently holds, for comparing against what the client shows.</summary>
    public IReadOnlyList<string> Probe()
    {
        var lines = new List<string>();

        if (BotClient is not { } bot)
        {
            lines.Add("bot: (none)");
            return lines;
        }

        lines.Add($"client: slot={_botSlot} userId={bot.UserId} steamId={bot.SteamId} fake={bot.IsFakeClient}");

        if (bot.GetPlayerController() is { } controller)
        {
            var difficulty = controller.FindNetVar("m_iPawnBotDifficulty")
                ? controller.GetNetVar<int>("m_iPawnBotDifficulty").ToString()
                : "(absent)";
            var controllingBot = controller.FindNetVar("m_bControllingBot")
                ? controller.GetNetVar<bool>("m_bControllingBot").ToString()
                : "(absent)";

            var unkickable = controller.FindNetVar("m_bCannotBeKicked")
                ? controller.GetNetVar<bool>("m_bCannotBeKicked").ToString()
                : "(absent)";

            lines.Add($"controller: team={controller.Team} connected={controller.ConnectedState} "
                + $"cannotBeKicked={unkickable} specCorrections={_spectatorCorrections}");
            lines.Add($"controller: flags={controller.Flags} m_steamID={controller.GetNetVar<ulong>("m_steamID")}");
            lines.Add($"controller: botDifficulty={difficulty} controllingBot={controllingBot} clan='{controller.ClanTag}'");
            lines.Add(controller.GetPawn() is { } pawn
                ? $"pawn: {pawn.GetSchemaClassname()} team={pawn.Team} alive={pawn.IsAlive} flags={pawn.Flags}"
                : "pawn: (none)");
            lines.Add($"pawn kinds: player={controller.GetPlayerPawn() is not null} "
                + $"observer={controller.GetObserverPawn() is not null}");
        }
        else
        {
            lines.Add("controller: (none)");
        }

        lines.Add(ProbeUserInfo());
        lines.Add($"pre-spoof: flags={Describe(_originalControllerFlags)} steamId={Describe(_originalSteamId)} "
            + $"clan='{_originalClanTag ?? "(not captured)"}' botDifficulty={Describe(_originalBotDifficulty)} "
            + $"controllingBot={Describe(_originalControllingBot)} pawnFlags={Describe(_originalPawnFlags)}");
        return lines;
    }

    /// <summary>Renders a lazily-captured pre-spoof value, distinguishing "not captured yet" from a real value.</summary>
    private static string Describe<T>(T? value) where T : struct
        => value?.ToString() ?? "(not captured)";

    private string ProbeUserInfo()
    {
        if (ReadUserInfoBytes() is not { } raw)
        {
            return "userinfo: (entry not readable)";
        }

        var info = ReadUserInfo();
        var body = info is null
            ? "unparseable"
            : $"name='{info.Name}' xuid={info.Xuid} steamid={info.Steamid} "
                + $"userid={info.Userid} fakeplayer={info.Fakeplayer}";

        return $"userinfo: size={raw.Length} {body}";
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
        ForgetOriginals(); // They describe an entity that no longer exists.

        if (_botSlot < 0)
        {
            return;
        }
        _botSlot = -1;
        SpeakerSlotChanged?.Invoke(-1);
    }

    private void ForgetOriginals()
    {
        _originalSteamId = null;
        _originalClanTag = null;
        _originalControllerFlags = null;
        _originalBotDifficulty = null;
        _originalControllingBot = null;
        _originalPawnFlags = null;
        _spectatorCorrections = 0;
    }
}
