# TnmsSoundPlayer

A [ModSharp](https://github.com/Kxnrl/modsharp-public) module for Counter-Strike 2 that plays
arbitrary audio into the voice channel — local files, in-memory buffers, any URL yt-dlp can resolve,
or a PCM stream produced by your own plugin.

Audio is attributed to a resident bot that sits in spectator and is disguised to pass for a real
player, so clients can mute it and adjust its volume with the normal in-game controls.

## Translated README

[日本語](README_JA.md)

## Features

- Plays local files, in-memory buffers, and yt-dlp supported URLs (YouTube and friends)
- Handle-based API: stop, pause, resume, seek, live volume changes, and completion events
- A single server-wide priority queue, so plugins never cut each other off unintentionally
- Per-recipient targeting: everyone, one client, a fixed set, or a live predicate
- Per-client hearing toggle and server-side volume multiplier
- Custom sources via `IPcmAudioStream` for TTS or procedurally generated audio
- ffmpeg, yt-dlp and deno are downloaded automatically at first start
- No dependency on TnmsPluginFoundation

## Requirements

- Counter-Strike 2 dedicated server with ModSharp installed
- ModSharp.Sharp.Shared 2.1.137 or later
- .NET 10 runtime (provided by ModSharp)
- Outbound HTTPS access on first start, to download the external tools

## Installation

1. Copy `TnmsSoundPlayer.dll`, `TnmsSoundPlayer.deps.json` and `Concentus.dll` into
   `%MOD_SHARP_DIR%\modules\TnmsSoundPlayer\`.
2. Copy `TnmsSoundPlayer.Shared.dll` into `%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\`.
   Shared assemblies belong in `shared\` only — never under `modules\`.
3. Start the server. On first start the module downloads ffmpeg, yt-dlp and deno into
   `modules\TnmsSoundPlayer\tools\`.
4. Set `ITnmsSoundPlayer.SpeakerSteamId` to a SteamID64 you control, from a plugin or with
   `!sp_spk_steam <id>`. No id ships in the source, and until one is set the speaker still works
   but the scoreboard marks it as a bot and shows no avatar.

> A module directory's `reload\` subfolder only works for a module that is **already loaded**.
> The first deploy has to go into the module root, or the module never loads.

### External Tools

Each tool is resolved in order: the module's `tools` directory, then `PATH`, then downloaded from
its official release. Caches stay inside `modules\TnmsSoundPlayer\tools\cache\`, so nothing is
written to the user profile.

| Tool | Purpose |
|---|---|
| ffmpeg | Decodes every source to PCM |
| yt-dlp | Resolves URLs to a media stream |
| deno | JavaScript runtime yt-dlp needs to solve YouTube's nsig challenge. Without it, YouTube downloads fail with HTTP 403 |

## How Playback Works

Audio is encoded to Opus and injected as `CSVCMsg_VoiceData`, which means it travels the same path
as player voice and obeys the client's own voice controls.

Attribution needs a client slot to point at, so the module keeps one bot on the server:

- The bot is requested through the game's own manager (`bot_add`) once the first human joins —
  bots cannot be added while the server sits empty.
- It is moved to spectator and kept there. Attempts to assign it to a playing team are redirected
  back to spectator.
- It is marked as unkickable, otherwise the `bot_quota` manager removes it within a few ticks.
- Its controller and pawn are adjusted so the scoreboard does not mark it as a bot.

The bot occupies one player slot. On a server that runs at its player limit, account for that.

## Limitations

- **One sound at a time, server-wide.** Concurrent playback and mixing are out of scope; use the
  priority queue and `QueueBehavior.Interrupt` instead.
- **48 kHz mono only.** Stereo Opus packets were tested in-game and the client downmixes them, so
  the second channel is pure overhead.
- **Audio quality is capped by the voice path.** The bitrate is 128 kbps, but the client's voice DSP
  still shapes the result — bass in particular comes through weakly.
- **URL sources cannot seek.** `ISoundPlayback.Seek` and `PlayOptions.Loop` are unavailable for them,
  and their duration is unknown.

## Commands

The module currently registers experimental commands for tuning the speaker bot. They are
development tooling and will be removed once the speaker configuration is finalized. Type them in
chat with `!`, or in console with the `ms_` prefix.

| Command | Description |
|---|---|
| `sp_spk_status` | Current speaker slot, xuid, spoof id and disguise mask |
| `sp_spk_probe` | Dump the server-side controller/pawn/userinfo state |
| `sp_spk_bot [name]` | Request the speaker bot, or rename an existing one |
| `sp_spk_kick` | Remove the speaker bot and stop respawning it |
| `sp_spk_disguise <mask>` | Toggle individual disguise traits |
| `sp_spk_name` / `sp_spk_slot` / `sp_spk_xuid` / `sp_spk_steam` | Override the bot's name and the voice attribution identity |

## Build

```
dotnet build TnmsSoundPlayer/TnmsSoundPlayer.csproj
```

With the `MOD_SHARP_DIR` environment variable set, the build copies the module to
`%MOD_SHARP_DIR%\modules\TnmsSoundPlayer\reload\` for hot reload, and the shared assembly to
`%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\`.

For a release build:

```
dotnet publish TnmsSoundPlayer/TnmsSoundPlayer.csproj -f net10.0 -r win-x64 --no-self-contained -c Release -p:DebugType=None -p:DebugSymbols=false
```

Use `-r linux-x64` for Linux servers. Before copying the output to a server, remove the assemblies
ModSharp already ships (Microsoft.Extensions.\*, Serilog.\*, Google.Protobuf, System.Text.Json).

## Documentation

| Category | Links |
|---|---|
| API | [Getting Started](docs/en/development/USING_SOUNDPLAYER_API.md) / [Playback](docs/en/development/api/playback.md) / [Audio Sources](docs/en/development/api/sources.md) |

## For Plugin Developers

See [Using the TnmsSoundPlayer API](docs/en/development/USING_SOUNDPLAYER_API.md) for details.

## License

AGPLv3, with the ModSharp linking and dual-licensing exceptions. See [LICENSE](LICENSE).

Copyright (c) 2026 faketuna
