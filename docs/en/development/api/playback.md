# Playback API

Everything to do with starting a sound, controlling it, and observing how it ended.
Obtained via `ITnmsSoundPlayer.CreateSession`.

---

## Queue Model

The server plays at most one sound at a time. All sessions share a single global priority queue.

- Higher `Priority` plays first; equal priorities play in arrival order.
- `WhenBusy` decides what happens when something is already on air.
- A paused playback still holds the slot, so the queue does not advance while it is paused.
- A looping playback never ends on its own, so anything queued behind it waits indefinitely.
  This is by design — pair `Loop` with `Interrupt` or an explicit `Stop`.

### State Lifecycle

```
Queued --> Playing <--> Paused --> Completed
   |          |                 \-> Stopped
   |          \-------------------> Failed
   \-> Rejected   (terminal from the start)
```

---

## ITnmsSoundPlayer

Entry point, resolved from `ISharpModuleManager` with `ITnmsSoundPlayer.Identity`
(`"TnmsSoundPlayer"`). Call from the game thread unless noted.

| Member | Type | Description |
|---|---|---|
| `Identity` | `const string` | Interface identity used when resolving the module |
| `CreateSession(string ownerName)` | `ISoundPlayerSession` | Creates or returns the session for `ownerName`. Idempotent |
| `CurrentPlayback` | `ISoundPlaybackInfo?` | The playback on air, or `null` when idle. Read-only view |
| `Queue` | `IReadOnlyList<ISoundPlaybackInfo>` | Snapshot of pending playbacks in playback order, excluding `CurrentPlayback`. Read-only views |
| `StopAll()` | `void` | Stops the current playback and clears the queue across all sessions |
| `SpeakerSteamId` | `ulong` | Bot mode: SteamID64 the speaker bot masquerades as, `0` disables the spoof. Entity mode: the voice stream key. See [Speaker Identity](#speaker-identity) |
| `SpeakerName` | `string` | Name the speaker shows while nothing is playing. Blank resets it to `TnmsSpeaker`. Bot mode only. See [Speaker Identity](#speaker-identity) |
| `FileService` | `IAudioFileService` | See [Audio Sources](sources.md) |
| `NetworkService` | `INetworkAudioService` | See [Audio Sources](sources.md) |
| `Diagnostics` | `SoundPlayerDiagnostics` | Runtime health snapshot. Safe to read from any thread |

### Speaker Identity

Every voice packet has to say who is speaking. There are two ways to answer that, chosen by the
`tnms_sound_speaker_mode` ConVar. It and `tnms_sound_speaker_entity` are read at load and again at
every map start, so changing one mid-map takes effect on the next map — switching modes creates or
kicks a bot, and a map boundary is where that is least disruptive.

| | `0` — bot | `1` — entity (default) |
|---|---|---|
| Player slots used | 1 of the server's 64 | **none** |
| Scoreboard row | yes | no |
| `SpeakerName` | shown on that row | inert, see below |
| Client-side mute | works | not available — mute server-side instead |
| Per-player volume on the scoreboard | needs `SpeakerSteamId` set | not available |

**Bot mode** attributes audio to a player slot, so a bot has to sit in one for the slot to resolve
to somebody — which means bot mode permanently occupies one of the server's 64 player slots.
Making that bot read as a real player rather than a bot needs a real SteamID64 on its controller,
which is what `SpeakerSteamId` sets. No id is baked into the source, because it names a real
account; until you set one the speaker works, but the scoreboard marks it as a bot.

```csharp
// Use an account you control.
_player.SpeakerSteamId = 7656119XXXXXXXXXX;
```

The value applies immediately and is re-applied to every bot created afterwards, so setting it once
at startup is enough. Set it back to `0` to drop the spoof.

Set it if you want players to adjust the speaker's volume themselves: the per-player volume control
on the scoreboard belongs to a real player's row, and a row still marked as a bot does not carry
one. Without an id, the only volume control is the server-side `SetPlayerVolume`.

`SpeakerName` is the other half of that identity: the name on the scoreboard row, shown whenever
nothing is playing.

```csharp
_player.SpeakerName = "Jukebox";
```

An individual playback can borrow the name for as long as it is audible through
[`PlayOptions.SpeakerName`](#playoptions) — useful for crediting whoever requested the sound. The
resting name comes back the moment that playback ends, however it ends.

**Entity mode** attributes audio to an entity index instead. Nothing has to exist behind that index,
so no bot is created and no slot is spent — which is the reason to use it: a 64-slot server keeps
all 64 for players. `tnms_sound_speaker_entity` picks the index. The engine assigns entity indices
itself, so one cannot be reserved; the default instead sits near the top of the valid range
(`0..16383`), clear of both players (`1..maxplayers`) and the indices a map allocates from the
bottom up. Move it only to dodge a collision: a real entity at this index would pull the audio to
wherever that entity is, and a player's index would make that player appear to be talking.

The trade is that there is no scoreboard row. `SpeakerName` and `PlayOptions.SpeakerName` still
round-trip, but nothing renders them — show the current track in your own HUD or chat if you need
it. `SpeakerSteamId` keeps a narrower job here: the client keys one audio stream per xuid, so it
only has to be stable and non-zero, and the module supplies a default. Players also cannot mute the
speaker from their own client, so give them a command that calls `SetHearing` or `SetPlayerVolume`
instead; both drop the recipient server-side and never send the packets at all.

---

## ISoundPlayerSession

A per-plugin handle. Playbacks started here are attributed to the owner and count against the
session's queue limit.

| Member | Type | Description |
|---|---|---|
| `OwnerName` | `string` | The name passed to `CreateSession` |
| `Play(IPcmAudioStream, PlayOptions?, ISoundPlaybackCallback?)` | `ISoundPlayback` | Enqueues a PCM stream. **Ownership of the stream transfers to the playback**, which disposes it on any terminal state |
| `PlayFile(string path, PlayOptions?, ISoundPlaybackCallback?)` | `ISoundPlayback` | Opens the path through `FileService` and plays it |
| `PlayUrl(string url, PlayOptions?, ISoundPlaybackCallback?)` | `ISoundPlayback` | Opens the URL through `NetworkService` and plays it |
| `OwnPlaybacks` | `IReadOnlyList<ISoundPlayback>` | Snapshot of this session's playing and queued playbacks |
| `StopAll()` | `void` | Stops and dequeues this session's playbacks only |
| `SetHearing(bool, IEnumerable<IGameClient>?)` | `void` | Enables or disables **this session's** audio for those clients. `null` clients means everyone connected |
| `GetHearing(IGameClient)` | `bool` | Whether that client hears this session |
| `DefaultHearing` | `bool` | Hearing applied to clients connecting from now on. Does not affect already-connected clients |
| `SetPlayerVolume(float, IEnumerable<IGameClient>?)` | `void` | Volume multiplier for this session's audio. `0.0` mutes, `1.0` is unmodified |
| `GetPlayerVolume(IGameClient)` | `float` | That client's multiplier for this session |

None of the `Play*` methods throw for media errors. A source that cannot be opened produces a
playback that reaches `Failed` with the reason in `Error`.

### Hearing and Volume Are Per Session

Both are scoped to the session that owns the playback, so a player who muted the jukebox still
hears round-start sounds from another plugin. There is no server-wide equivalent — the one
deliberately global control is `ITnmsSoundPlayer.StopAll()`, since an admin silencing the server
wants the sound gone, not future sounds suppressed.

Three filters decide who hears a chunk, and all of them must pass:

| Filter | Set by | Question it answers |
|---|---|---|
| `PlayOptions.Recipients` | the plugin | Who is this particular sound for? |
| session hearing | the player | Do I want this plugin's audio at all? |
| `PlayOptions.Volume` × session volume | both | How loud, for this listener? |

`clients: null` means every connected client; an **empty sequence means none**. That asymmetry is
deliberate: `SetHearing(false, clients.Where(...))` with a filter that happened to match nobody
would otherwise mute the entire server.

---

## ISoundPlaybackInfo

A read-only view of a sound, whoever started it. This is what `ITnmsSoundPlayer.CurrentPlayback` and
`Queue` hand out, so that a plugin can display what is playing without being able to stop it. It
carries `Id`, `OwnerName`, `State`, `Position`, `Duration`, `Volume` (get only) and `Error`.

The split exists because "stop my music" commands collide otherwise: two plugins both calling
`CurrentPlayback.Stop()` would cut each other off. Control comes from owning the playback — the
handle your `Play` call returned, or `ISoundPlayerSession.OwnPlaybacks`. It is a guard rail rather
than a boundary; the runtime object implements both interfaces, so a cast still gets through.

---

## ISoundPlayback

`ISoundPlaybackInfo` plus the controls, handed only to the session that started the sound.
Control methods must be called from the game thread; property reads are thread-safe.

| Member | Type | Description |
|---|---|---|
| `Id` | `long` | Unique, monotonically increasing id |
| `OwnerName` | `string` | Owner of the session that started this playback |
| `State` | `PlaybackState` | Current lifecycle state |
| `Position` | `TimeSpan` | Current position within the source |
| `Duration` | `TimeSpan?` | Total duration, or `null` while unknown (live/network sources) |
| `Volume` | `float` | Per-playback gain. Settable while playing |
| `Error` | `PlaybackError?` | Failure detail. Non-null for `Failed` and `Rejected`, and for a `Stopped` playback that was interrupted |
| `Stop()` | `void` | Stops it if playing, or removes it from the queue if queued |
| `Pause()` | `void` | Pauses. Still holds the playback slot |
| `Resume()` | `void` | Resumes a paused playback |
| `Seek(TimeSpan)` | `void` | Seeks within the source. Throws `NotSupportedException` when the source cannot seek |
| `Completion` | `Task` | Completes on a terminal state. Never faults for media errors |

To be told when a playback starts and ends, pass an `ISoundPlaybackCallback` when you start it.

---

## ISoundPlaybackCallback

Notifications for one playback, handed to `Play`, `PlayFile` or `PlayUrl`. Both methods have a
default implementation, so implement only the ones you need.

| Method | Description |
|---|---|
| `OnStarted(ISoundPlayback)` | The playback actually started producing audio |
| `OnFinished(ISoundPlayback)` | Called exactly once on reaching a terminal state. Read `State` and `Error` to tell which |

```csharp
private sealed class AnnounceTrack(IGameClient client) : ISoundPlaybackCallback
{
    public void OnFinished(ISoundPlayback playback)
        => client.ConsolePrint($"finished: {playback.State}\n");
}

session.PlayUrl(url, null, new AnnounceTrack(client));
```

Both methods run on the game thread.

Passing the callback in rather than subscribing to it afterwards removes two problems by
construction. A playback that is rejected outright still reports through the callback, where a
subscription attached after the call could have missed it. And there is nothing to unsubscribe: the
player releases its reference the moment the playback reaches a terminal state, so a callback
holding an `IGameClient` never outlives the sound, even if you park the handle in a long-lived
field.

---

## PlayOptions

Record passed to the `Play*` methods. Every property has a default, so `new PlayOptions { ... }`
only needs the parts you care about.

| Property | Type | Default | Description |
|---|---|---|---|
| `Recipients` | `SoundRecipients` | `All` | Who receives the audio |
| `Volume` | `float` | `1.0f` | Initial per-playback gain |
| `StartAt` | `TimeSpan` | `0` | Position within the source to start from |
| `Loop` | `bool` | `false` | Repeats until stopped or interrupted. Only works on seekable sources |
| `Priority` | `int` | `0` | Queue priority. Higher plays first |
| `WhenBusy` | `QueueBehavior` | `Enqueue` | What to do when something is already on air |
| `SpeakerName` | `string?` | `null` | Renames the speaker while this playback is audible, then restores `ITnmsSoundPlayer.SpeakerName`. `null` leaves the name alone |
| `DownloadFirst` | `bool` | `false` | `PlayUrl` only: fetch the whole source before playing any of it. Makes it seekable and loopable and gives it a duration, at the cost of the download in startup latency |

The rename takes effect when the first audio packet goes out, not when the playback is queued, so a
sound that waits in the queue or fails to open never touches the name.

`DownloadFirst` is the difference between a sound effect and a jukebox. Streamed (the default), a URL
starts within a second but cannot seek, cannot loop and reports an unknown duration, because yt-dlp's
output is piped straight into ffmpeg and a pipe cannot rewind. Downloaded, it lands in a temporary
file first — which is a local file like any other, so everything works — but nothing is audible until
the download finishes. The temporary file is deleted when the playback ends.

---

## SoundRecipients

Describes who hears a playback. Construct with the factory members; the nested records exist so the
implementation can pattern-match and are not meant to be constructed directly.

| Member | Description |
|---|---|
| `All` | Every connected client, evaluated live — clients joining mid-playback are included |
| `Single(IGameClient)` | One client |
| `Of(IEnumerable<IGameClient>)` | A fixed set snapshotted at call time. Tracked by slot, so clients who disconnect stop receiving; nobody is added later |
| `Where(Func<IGameClient, bool>)` | A predicate re-evaluated live against all connected clients while the playback runs |

Note that `Where` runs your predicate repeatedly during playback, so keep it cheap and free of side
effects.

---

## PlaybackState

| Value | Description |
|---|---|
| `Queued` | Waiting in the global queue |
| `Playing` | On air |
| `Paused` | Paused, still occupying the single playback slot |
| `Completed` | The source played to its end |
| `Stopped` | Stopped early — explicitly, by a `StopAll`, or by an interrupt. Check `Error` to tell which |
| `Failed` | Could not start, or aborted on a media error. See `Error` |
| `Rejected` | The queue refused it (limit reached, or `RejectIfBusy` while busy). See `Error` |

---

## QueueBehavior

| Value | Description |
|---|---|
| `Enqueue` | Wait in the global priority queue |
| `Interrupt` | Stop the current playback and play immediately. The interrupted playback gets `Error.Reason == Interrupted` and is not resumed |
| `RejectIfBusy` | Return a playback already in `Rejected` instead of waiting |

---

## PlaybackError

`sealed record PlaybackError(PlaybackErrorReason Reason, string Message)`

| `PlaybackErrorReason` | Description |
|---|---|
| `SourceNotFound` | The file or buffer could not be found or opened |
| `DecodeFailed` | ffmpeg failed to decode the source |
| `FfmpegNotFound` | The ffmpeg executable is unavailable |
| `YtdlpNotFound` | The yt-dlp executable is unavailable |
| `UrlResolveFailed` | yt-dlp could not resolve the URL to a media stream |
| `QueueLimitReached` | The global or session queue limit was reached, or `RejectIfBusy` hit a busy player |
| `Interrupted` | Cut off by an `Interrupt` playback. Reported on a `Stopped` playback, not `Failed` |
| `Cancelled` | Cancelled before it could start, e.g. module shutdown |

---

## SoundPlayerDiagnostics

`sealed record SoundPlayerDiagnostics(bool FfmpegAvailable, string? FfmpegPath, bool YtdlpAvailable, string? YtdlpPath, int QueueLength, int ActiveSessionCount)`

A snapshot, safe to read from any thread. ffmpeg and yt-dlp are resolved at module start from the
module's `tools` directory, then `PATH`, then downloaded automatically — so `false` here generally
means the automatic download failed.
