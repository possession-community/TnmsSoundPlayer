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
| `CurrentPlayback` | `ISoundPlayback?` | The playback on air, or `null` when idle |
| `Queue` | `IReadOnlyList<ISoundPlayback>` | Snapshot of pending playbacks in playback order, excluding `CurrentPlayback` |
| `StopAll()` | `void` | Stops the current playback and clears the queue across all sessions |
| `SetHearing(IGameClient, bool)` | `void` | Enables or disables all sound player audio for one client |
| `GetHearing(IGameClient)` | `bool` | Current hearing state for a client |
| `DefaultHearing` | `bool` | Hearing state applied to clients connecting from now on. Does not affect already-connected clients |
| `SetPlayerVolume(IGameClient, float)` | `void` | Server-side volume multiplier for one client. `0.0` mutes, `1.0` is unmodified |
| `GetPlayerVolume(IGameClient)` | `float` | Current per-client multiplier |
| `SpeakerSteamId` | `ulong` | SteamID64 the speaker bot masquerades as. `0` (the default) disables the spoof. See [Speaker Identity](#speaker-identity) |
| `FileService` | `IAudioFileService` | See [Audio Sources](sources.md) |
| `NetworkService` | `INetworkAudioService` | See [Audio Sources](sources.md) |
| `Diagnostics` | `SoundPlayerDiagnostics` | Runtime health snapshot. Safe to read from any thread |

### Speaker Identity

Audio needs a client slot to be attributed to, so the module keeps one bot in spectator and sends
the voice data as if it came from there. Part of making that bot read as a real player rather than
a bot is giving its controller a real SteamID64, which is what `SpeakerSteamId` sets.

No id is baked into the source, because it names a real account. Until you set one, the speaker
works but the scoreboard marks it as a bot and shows no avatar.

```csharp
// Use an account you control.
Player!.SpeakerSteamId = 7656119XXXXXXXXXX;
```

The value applies immediately and is re-applied to every bot created afterwards, so setting it once
at startup is enough. Set it back to `0` to drop the spoof.

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

None of the `Play*` methods throw for media errors. A source that cannot be opened produces a
playback that reaches `Failed` with the reason in `Error`.

---

## ISoundPlayback

Handle to one queued or playing sound. Control methods must be called from the game thread;
property reads are thread-safe.

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
