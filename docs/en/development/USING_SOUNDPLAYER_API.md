# TnmsSoundPlayer API Guide

TnmsSoundPlayer plays arbitrary audio into the CS2 voice channel: local files, in-memory
buffers, any URL yt-dlp can resolve, or a PCM stream you produce yourself.
External plugins access it through the `TnmsSoundPlayer.Shared` assembly.

The server plays **at most one sound at a time**. Every plugin's playbacks go through a single
global priority queue, so requesting a sound never cuts another plugin off unless you ask for it.

## Setup

### 1. Reference TnmsSoundPlayer.Shared

The shared assembly is not published to NuGet. Building TnmsSoundPlayer copies it to
`%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\`, so reference the DLL from there — or add a
project reference if you build both from the same solution.

```xml
<ItemGroup>
    <ProjectReference Include="..\TnmsSoundPlayer.Shared\TnmsSoundPlayer.Shared.csproj"
                      Private="false" ExcludeAssets="runtime" />
</ItemGroup>
```

`Private="false"` and `ExcludeAssets="runtime"` matter: ModSharp loads the shared assembly once,
and copying your own duplicate next to your module breaks type identity.

### 2. Obtain the API Entry Point

Resolve `ITnmsSoundPlayer` through `ISharpModuleManager`. TnmsSoundPlayer registers its interface
in `PostInit`, so it is not resolvable from your `Init` — resolve it in `OnAllModulesLoaded`.

```csharp
private ITnmsSoundPlayer _player = null!;

public void OnAllModulesLoaded()
    => _player = _shared.GetSharpModuleManager()
        .GetRequiredSharpModuleInterface<ITnmsSoundPlayer>(ITnmsSoundPlayer.Identity).Instance!;
```

`GetRequiredSharpModuleInterface` throws when TnmsSoundPlayer is not installed, which is what you
want: a module that plays sound has nothing useful to do without it, and failing at load is easier
to diagnose than silently doing nothing.

### 3. Create a Session

Everything you play goes through a session, which is your plugin's handle to the player.

```csharp
var session = _player.CreateSession("MyPlugin");
```

`CreateSession` is idempotent — calling it again with the same name returns the same session, so
you can call it at the point of use instead of storing it. The session is the unit of per-plugin
queue limits and of `StopAll`, so use a name that identifies your plugin.

### 4. Set the Speaker Identity

Audio is attributed to a bot the module keeps in spectator. Give that bot a SteamID64 you control,
or the scoreboard marks it as a bot and it shows no avatar:

```csharp
_player.SpeakerSteamId = 7656119XXXXXXXXXX;
```

No id ships in the source, since it names a real account. Setting it once at startup is enough — it
is re-applied to every bot created afterwards.

### 5. ITnmsSoundPlayer Overview

| Member | Type | Purpose |
|---|---|---|
| `CreateSession(string)` | `ISoundPlayerSession` | Get (or create) your plugin's session |
| `CurrentPlayback` | `ISoundPlayback?` | The playback on air, or `null` when idle |
| `Queue` | `IReadOnlyList<ISoundPlayback>` | Pending playbacks in playback order |
| `StopAll()` | `void` | Stop everything across all sessions (administrative) |
| `SetHearing` / `GetHearing` | `void` / `bool` | Per-client on/off for all sound player audio |
| `DefaultHearing` | `bool` | Hearing state applied to clients that connect later |
| `SetPlayerVolume` / `GetPlayerVolume` | `void` / `float` | Per-client volume multiplier |
| `SpeakerSteamId` | `ulong` | SteamID64 the speaker bot masquerades as. `0` disables the spoof |
| `FileService` | `IAudioFileService` | Open local files and buffers as PCM |
| `NetworkService` | `INetworkAudioService` | Open URLs as PCM, and fetch metadata |
| `Diagnostics` | `SoundPlayerDiagnostics` | Tool availability and queue statistics |

Unless a member says otherwise, call it from the game thread.

---

## Common Usage Examples

### Play a URL

```csharp
var session = _player.CreateSession("MyPlugin");
var playback = session.PlayUrl("https://www.youtube.com/watch?v=...");
```

`PlayUrl` and `PlayFile` never throw for media problems. A source that cannot be opened comes back
as a playback that reaches `Failed`, with the reason in `Error`.

### Play a File to Specific Players

```csharp
var ct = _shared.GetClientManager().GetGameClients(true)
    .Where(c => c.GetPlayerController()?.Team == CStrikeTeam.CT);

session.PlayFile(@"sounds\alert.mp3", new PlayOptions
{
    Recipients = SoundRecipients.Of(ct),
    Volume = 0.6f,
});
```

`SoundRecipients.Of` snapshots the set at call time. Use `SoundRecipients.Where(...)` instead when
you want the set re-evaluated live while the sound plays — for example "everyone currently alive".

### Interrupt Whatever Is Playing

```csharp
session.PlayFile(@"sounds\countdown.wav", new PlayOptions
{
    WhenBusy = QueueBehavior.Interrupt,
    Priority = 100,
});
```

The interrupted playback reaches `Stopped` with `PlaybackErrorReason.Interrupted`, and is **not**
resumed afterwards. Use `QueueBehavior.RejectIfBusy` if you would rather skip your sound than cut
somebody else's off.

### React to the Result

Implement `ISoundPlaybackCallback` and hand it over when starting the playback. `OnFinished` runs
exactly once for every terminal state, so it is the right place to clean up. To distinguish
outcomes, read `State` and `Error`:

```csharp
private sealed class TrackReporter(ILogger logger) : ISoundPlaybackCallback
{
    public void OnFinished(ISoundPlayback playback)
    {
        switch (playback.State)
        {
            case PlaybackState.Completed:
                break;
            case PlaybackState.Stopped when playback.Error?.Reason == PlaybackErrorReason.Interrupted:
                logger.LogInformation("we were cut off by a higher priority sound");
                break;
            case PlaybackState.Failed:
            case PlaybackState.Rejected:
                logger.LogWarning("playback failed: {Reason} {Message}",
                    playback.Error?.Reason, playback.Error?.Message);
                break;
        }
    }
}

session.PlayUrl(url, null, new TrackReporter(_logger));
```

Both callback methods have default implementations, so a class that only cares about completion
implements only `OnFinished`.

`Completion` is the same information as a `Task`, for `await`-style code. It never faults for media
errors — inspect `State` and `Error` after awaiting, exactly as above.

### Fetch Metadata Without Playing

```csharp
var meta = await _player.NetworkService.GetMetadataAsync(url);
_logger.LogInformation("{Title} ({Duration}) by {Uploader}", meta.Title, meta.Duration, meta.Uploader);
```

The continuation runs on a worker thread. **Do not touch `IGameClient` or any entity there** — hop
back to the game thread first, or log the result as above.

### Control Playback While It Runs

```csharp
playback.Volume = 0.3f;   // takes effect immediately
playback.Pause();
playback.Resume();
playback.Seek(TimeSpan.FromSeconds(30));  // throws NotSupportedException on URL sources
playback.Stop();
```

A paused playback still occupies the single playback slot, so queued sounds keep waiting.

### Mute a Player

```csharp
_player.SetHearing(client, false);      // this client hears nothing from the sound player
_player.SetPlayerVolume(client, 0.5f);  // or just quieter
```

These are global per client, independent of any individual playback, and are applied server-side
before encoding. Set `DefaultHearing` to control what newly connecting clients get.

### Check Whether Tools Are Available

```csharp
var d = _player.Diagnostics;
if (!d.YtdlpAvailable)
{
    _logger.LogWarning("yt-dlp missing; URL playback will fail");
}
```

`Diagnostics` is a snapshot and is safe to read from any thread. ffmpeg and yt-dlp are downloaded
automatically at module start, so a missing tool usually means the download failed.

---

## API Reference

| Page | Contents |
|---|---|
| [Playback](api/playback.md) | `ITnmsSoundPlayer`, `ISoundPlayerSession`, `ISoundPlayback`, `PlayOptions`, `SoundRecipients`, `PlaybackState`, `QueueBehavior`, `PlaybackError` |
| [Audio Sources](api/sources.md) | `IAudioFileService`, `INetworkAudioService`, `IPcmAudioStream`, `PcmAudioFormat`, `AudioMetadata`, feeding custom audio |
