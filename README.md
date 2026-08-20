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
- ffmpeg, ffprobe, yt-dlp and deno are downloaded automatically at first start
- No dependency on TnmsPluginFoundation

## Requirements

- Counter-Strike 2 dedicated server with ModSharp installed
- ModSharp.Sharp.Shared 2.1.137 or later
- .NET 10 runtime (provided by ModSharp)
- Outbound HTTPS access on first start, to download the external tools

## Installation

Grab `TnmsSoundPlayer-<platform>.zip` from the
[latest release](https://github.com/possession-community/TnmsSoundPlayer/releases/latest); it holds
the `modules\` and `shared\` trees ready to merge into `%MOD_SHARP_DIR%`. To place the files by hand:

1. Copy `TnmsSoundPlayer.dll`, `TnmsSoundPlayer.deps.json` and `Concentus.dll` into
   `%MOD_SHARP_DIR%\modules\TnmsSoundPlayer\`.
2. Copy `TnmsSoundPlayer.Shared.dll` into `%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\`.
   Shared assemblies belong in `shared\` only — never under `modules\`.
3. Start the server. On first start the module downloads ffmpeg, ffprobe, yt-dlp and deno into
   `modules\TnmsSoundPlayer\tools\`.
4. Set `ITnmsSoundPlayer.SpeakerSteamId` to a SteamID64 you control from a plugin. No id ships in
   the source, and until one is set the speaker still works but the scoreboard marks it as a bot
   and shows no avatar.

> A module directory's `reload\` subfolder only works for a module that is **already loaded**.
> The first deploy has to go into the module root, or the module never loads.

### External Tools

Each tool is resolved in order: the module's `tools` directory, then `PATH`, then downloaded from
its official release. Caches stay inside `modules\TnmsSoundPlayer\tools\cache\`, so nothing is
written to the user profile.

| Tool | Purpose |
|---|---|
| ffmpeg | Decodes every source to PCM |
| ffprobe | Reads a local file's duration. Ships in the ffmpeg archive; without it file playbacks report an unknown duration |
| yt-dlp | Resolves URLs to a media stream. Self-updates daily when it lives in `tools\`, because YouTube breaks older builds within weeks — extraction keeps working while the media download starts failing with HTTP 403 |
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
- **Streamed URL sources cannot seek.** `ISoundPlayback.Seek` and `PlayOptions.Loop` are unavailable
  for them and their duration is unknown, because yt-dlp is piped straight into ffmpeg. Set
  `PlayOptions.DownloadFirst` to fetch the source first and get all three back, at the cost of
  waiting for the download before anything plays.

## Commands

The library itself registers no commands — it is driven entirely through its API. `TnmsSoundPlayerTest`
is a separate, optional module that exercises the API by hand; load it only while developing. Type its
commands in chat with `!`, or in console with the `ms_` prefix.

| Command | Description |
|---|---|
| `sp_url <url> [volume]` | Play a URL, crediting the requester in the speaker name |
| `sp_dlurl <url> [volume]` | Same, but downloaded first, so it can seek |
| `sp_file <path> [volume]` | Play a local file — the seekable kind of source |
| `sp_seek [seconds]` | Seek the current playback, or print its seekable range |
| `sp_meta <url>` | Fetch metadata without playing; the result goes to the server console |
| `sp_stop` | Stop whatever is playing |
| `sp_status` | Tool availability, speaker identity, current playback and queue |
| `sp_spk_name [name]` | Show or set the resting speaker name |
| `sp_spk_steam [steamid64]` | Show or set the SteamID64 the speaker masquerades as |

## Build

```
dotnet build TnmsSoundPlayer/TnmsSoundPlayer.csproj
```

With the `MOD_SHARP_DIR` environment variable set, the build copies the module to
`%MOD_SHARP_DIR%\modules\TnmsSoundPlayer\reload\` for hot reload, and the shared assembly to
`%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\`.

## Documentation

| Category | Links |
|---|---|
| API | [Getting Started](docs/en/development/USING_SOUNDPLAYER_API.md) / [Playback](docs/en/development/api/playback.md) / [Audio Sources](docs/en/development/api/sources.md) |

## For Plugin Developers

See [Using the TnmsSoundPlayer API](docs/en/development/USING_SOUNDPLAYER_API.md) for details.

## License

AGPLv3, with the ModSharp linking and dual-licensing exceptions. See [LICENSE](LICENSE).

Copyright (c) 2026 faketuna
