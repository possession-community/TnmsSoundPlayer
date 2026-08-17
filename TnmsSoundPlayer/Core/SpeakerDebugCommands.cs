using System.Globalization;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace TnmsSoundPlayer.Core;

/// <summary>
/// Phase 0 experiment commands for choosing the SpeakerIdentity (chat: !sp_spk_*, console:
/// ms_sp_spk_*). Temporary tooling; remove once the speaker strategy is decided.
/// </summary>
internal sealed class SpeakerDebugCommands
{
    private readonly ILogger _logger;
    private readonly ISharedSystem _shared;
    private readonly SoundPlayerCore _core;
    private readonly SpeakerManager _speaker;
    private readonly Dictionary<string, IClientManager.DelegateClientCommand> _commands = [];

    public SpeakerDebugCommands(ILogger logger, ISharedSystem shared, SoundPlayerCore core, SpeakerManager speaker)
    {
        _logger = logger;
        _shared = shared;
        _core = core;
        _speaker = speaker;
    }

    public void Register()
    {
        Add("sp_spk_status", OnStatus);
        Add("sp_spk_bot", OnBotCreate);
        Add("sp_spk_kick", OnBotKick);
        Add("sp_spk_name", OnBotRename);
        Add("sp_spk_slot", OnSetSlot);
        Add("sp_spk_xuid", OnSetXuid);
        Add("sp_spk_steam", OnSpoofSteam);
        Add("sp_spk_disguise", OnSetDisguise);
        Add("sp_spk_probe", OnProbe);
    }

    public void Unregister()
    {
        var clientManager = _shared.GetClientManager();
        foreach (var (name, callback) in _commands)
        {
            clientManager.RemoveCommandCallback(name, callback);
        }
        _commands.Clear();
    }

    private void Add(string name, IClientManager.DelegateClientCommand callback)
    {
        _shared.GetClientManager().InstallCommandCallback(name, callback);
        _commands.Add(name, callback);
    }

    private static void Reply(IGameClient client, string message)
        => client.ConsolePrint($"[SP:SPK] {message}\n");

    private ECommandAction OnStatus(IGameClient client, StringCommand command)
    {
        Reply(client, $"speaker slot={_core.SpeakerSlot} xuid={_core.SpeakerXuid} spoof={_speaker.SpoofSteamId}");
        Reply(client, $"disguise={(int)_speaker.Disguise} ({_speaker.Disguise})");
        Reply(client, _speaker.BotClient is { } bot
            ? $"bot: slot={_speaker.BotSlot} steamId={bot.SteamId} signon={bot.SignOnState} fake={bot.IsFakeClient}"
            : "bot: (none)");
        return ECommandAction.Stopped;
    }

    /// <summary>
    /// Phase 0 bisection: the scoreboard's "BOT" marker comes from the client-side
    /// GameStateAPI.GetPlayerStatsJSO(xuid).is_fake_player, and it is not yet known which networked
    /// value feeds it. This switches the disguise traits on and off one at a time to find out.
    /// </summary>
    private ECommandAction OnSetDisguise(IGameClient client, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            Reply(client, $"disguise={(int)_speaker.Disguise} ({_speaker.Disguise})");
            foreach (var value in Enum.GetValues<SpeakerDisguise>())
            {
                Reply(client, $"  {(int)value,3} = {value}");
            }
            Reply(client, "usage: sp_spk_disguise <mask>");
            return ECommandAction.Stopped;
        }

        var arg = command.GetArg(1);
        var isHex = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(isHex ? arg[2..] : arg,
                isHex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var mask))
        {
            Reply(client, "usage: sp_spk_disguise <mask>");
            return ECommandAction.Stopped;
        }

        _speaker.SetDisguise((SpeakerDisguise)mask);
        Reply(client, $"disguise={mask} ({_speaker.Disguise}); re-applied.");
        return ECommandAction.Stopped;
    }

    /// <summary>Dumps the server-side state so it can be compared against what the client renders.</summary>
    private ECommandAction OnProbe(IGameClient client, StringCommand command)
    {
        foreach (var line in _speaker.Probe())
        {
            Reply(client, line);
        }
        return ECommandAction.Stopped;
    }

    private ECommandAction OnBotCreate(IGameClient client, StringCommand command)
    {
        var name = command.ArgCount >= 1 ? command.ArgString : SpeakerManager.DefaultBotName;

        _speaker.EnsureBot(name);
        Reply(client, _speaker.BotSlot >= 0
            ? $"speaker bot already present (slot={_speaker.BotSlot}); renamed to '{name}'."
            : "speaker bot requested via bot_add; it will be captured, renamed and moved to spectator on join.");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnBotKick(IGameClient client, StringCommand command)
    {
        _speaker.RemoveBot();
        Reply(client, "speaker bot removed; speaker slot reset to -1.");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnBotRename(IGameClient client, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            Reply(client, "usage: sp_spk_name <name>");
            return ECommandAction.Stopped;
        }

        if (_speaker.BotClient is not { } bot)
        {
            Reply(client, "no speaker bot; create one with sp_spk_bot first.");
            return ECommandAction.Stopped;
        }

        bot.SetName(command.ArgString);
        Reply(client, $"speaker bot renamed to '{command.ArgString}'.");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnSetSlot(IGameClient client, StringCommand command)
    {
        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var slot))
        {
            Reply(client, $"speaker slot={_core.SpeakerSlot} (usage: sp_spk_slot <n|-1>)");
            return ECommandAction.Stopped;
        }

        _core.SpeakerSlot = slot;
        Reply(client, $"speaker slot={slot}");
        return ECommandAction.Stopped;
    }

    /// <summary>
    /// Phase 0 core experiment: make the speaker bot look like a real player.
    /// Delegates to <see cref="SpeakerManager.SetSpoofSteamId"/> (userinfo rewrite +
    /// controller m_steamID + VoiceData xuid + ForceFullUpdate for connected clients).
    /// The spoof is also applied automatically on every bot creation.
    /// </summary>
    private ECommandAction OnSpoofSteam(IGameClient client, StringCommand command)
    {
        var steamId = _speaker.SpoofSteamId;
        if (command.ArgCount >= 1 && !ulong.TryParse(command.GetArg(1), out steamId))
        {
            Reply(client, "usage: sp_spk_steam [steamid64]");
            return ECommandAction.Stopped;
        }

        if (_speaker.BotClient is null)
        {
            Reply(client, "no speaker bot; create one with sp_spk_bot first.");
            return ECommandAction.Stopped;
        }

        _speaker.SetSpoofSteamId(steamId);
        Reply(client, $"spoof applied as {steamId} (details in server log); connected clients were force-full-updated.");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnSetXuid(IGameClient client, StringCommand command)
    {
        if (command.ArgCount < 1 || !ulong.TryParse(command.GetArg(1), out var xuid))
        {
            Reply(client, $"speaker xuid={_core.SpeakerXuid} (usage: sp_spk_xuid <steamid64|0>)");
            return ECommandAction.Stopped;
        }

        _core.SpeakerXuid = xuid;
        Reply(client, $"speaker xuid={xuid}");
        return ECommandAction.Stopped;
    }
}
