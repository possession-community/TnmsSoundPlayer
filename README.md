# TnmsSoundPlayer

A [ModSharp](https://github.com/Kxnrl/modsharp-public) module for Counter-Strike 2 that plays
arbitrary audio into the voice channel — local files, in-memory buffers, any URL yt-dlp can resolve,
or a PCM stream produced by your own plugin.

## Translated README

[日本語](README_JA.md)

## Features

- Plays local files, in-memory buffers, and yt-dlp supported URLs (YouTube and friends)
- Handle-based API: stop, pause, resume, seek, live volume changes, and completion events
- A single server-wide priority queue, so plugins never cut each other off unintentionally
- Per-recipient targeting: everyone, one client, a fixed set, or a live predicate
- Per-client hearing toggle and server-side volume multiplier, scoped per plugin
- Custom sources via `IPcmAudioStream` for TTS or procedurally generated audio

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

## Documentation

| Category | Links |
|---|---|
| API | [Getting Started](docs/en/development/USING_SOUNDPLAYER_API.md) / [Playback](docs/en/development/api/playback.md) / [Audio Sources](docs/en/development/api/sources.md) |

## License

AGPLv3, with the ModSharp linking and dual-licensing exceptions. See [LICENSE](LICENSE).

Copyright (c) 2026 faketuna
