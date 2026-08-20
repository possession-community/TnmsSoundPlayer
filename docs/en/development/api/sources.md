# Audio Sources API

How audio gets into the player. `ISoundPlayerSession.PlayFile` and `PlayUrl` are thin wrappers over
these services, so you only need them directly when you want the stream before playing it — or when
you want to feed audio the player has no idea how to open.

---

## PCM Format

Everything inside the player is **48 kHz, mono, signed 16-bit little-endian**. The services decode
to that format for you; a custom `IPcmAudioStream` must produce it.

```csharp
public static class PcmAudioFormat
{
    public const int SampleRate     = 48000;
    public const int Channels       = 1;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = 96000;
    public const int FrameBytes     = 1920;  // one 20 ms Opus frame (960 samples)

    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);

    public static int      GetByteCount(TimeSpan duration);
    public static TimeSpan GetDuration(long byteCount);
}
```

Mono is not a simplification that can be lifted: stereo Opus packets were tested in-game and the
client downmixes them to mono on playback, so the second channel is wasted bandwidth.

---

## IAudioFileService

Opens local sources, decoded through ffmpeg. Stateless — playback position lives in the returned
stream, so the same source can be opened several times concurrently.

| Method | Return Type | Description |
|---|---|---|
| `OpenFileAsync(string path, CancellationToken)` | `Task<IPcmAudioStream>` | Opens an audio file from disk |
| `OpenBufferAsync(ReadOnlyMemory<byte> encodedData, CancellationToken)` | `Task<IPcmAudioStream>` | Opens an in-memory encoded buffer, in any format ffmpeg can decode |

Both faults with `SoundPlayerException` when the source cannot be opened. This is the difference
from `ISoundPlayerSession.PlayFile`, which reports the same condition through
`ISoundPlayback.Error` instead of throwing.

---

## INetworkAudioService

Opens network sources: yt-dlp resolves the URL, ffmpeg decodes the result.

| Method | Return Type | Description |
|---|---|---|
| `OpenUrlAsync(string url, CancellationToken)` | `Task<IPcmAudioStream>` | Opens any yt-dlp supported URL as a PCM stream, streamed while it downloads |
| `OpenUrlAsync(string url, bool downloadFirst, CancellationToken)` | `Task<IPcmAudioStream>` | As above, but `downloadFirst: true` fetches the whole source to a temporary file before returning |
| `GetMetadataAsync(string url, CancellationToken)` | `Task<AudioMetadata>` | Fetches metadata without downloading the media (`yt-dlp -J`) |

A streamed URL cannot seek: `CanSeek` is `false`, `Duration` is `null`, and both
`ISoundPlayback.Seek` and `PlayOptions.Loop` are unavailable for it. yt-dlp's output is piped
straight into ffmpeg, and a pipe cannot rewind.

`downloadFirst: true` trades startup latency for all of that: the source becomes an ordinary local
file, so it seeks, loops and knows its duration. Nothing plays until the download finishes. The
temporary file is deleted when the stream is disposed, and any left behind by a crash are swept at
module start. Through the playback API the same switch is
[`PlayOptions.DownloadFirst`](playback.md#playoptions).

These are the only genuinely `async` parts of the API, and their continuations run on worker
threads. **Never touch `IGameClient`, entities, or anything else ModSharp owns from there** — those
are game-thread only.

### AudioMetadata

`sealed record AudioMetadata(string? Title, TimeSpan? Duration, string? Uploader)`

Every field is nullable: yt-dlp does not report all of them for every site.

---

## IPcmAudioStream

A pull-based stream of decoded PCM. Implement it to feed sources the player cannot open on its
own — text-to-speech, procedurally generated audio, a decoder you already have.

| Member | Type | Description |
|---|---|---|
| `Read(Span<byte> destination)` | `int` | Writes decoded PCM into `destination` and returns the byte count. `0` means end of stream. May return fewer bytes than asked for, e.g. while a network source buffers |
| `CanSeek` | `bool` | Whether `Seek` is supported |
| `Duration` | `TimeSpan?` | Total duration, or `null` while unknown |
| `Position` | `TimeSpan` | Current position |
| `Seek(TimeSpan)` | `void` | Seeks. Throws `NotSupportedException` when `CanSeek` is `false` |

A stream instance is single-use. Passing it to `ISoundPlayerSession.Play` **transfers ownership** —
the playback disposes it when it reaches any terminal state, so do not dispose it yourself and do
not reuse it for a second playback.

`Read` is called from a worker thread, not the game thread. Returning `0` prematurely ends the
playback as `Completed`, so block or return a short count while you are waiting for data.

### PcmAudioStreamExtensions

| Method | Description |
|---|---|
| `ReadSeconds(this IPcmAudioStream, float seconds, Span<byte> destination)` | Reads up to `seconds` of audio, clamped to the destination length |

### Minimal Custom Source

```csharp
public sealed class SilenceStream(TimeSpan length) : IPcmAudioStream
{
    private int _offset;
    private readonly int _total = PcmAudioFormat.GetByteCount(length);

    public bool CanSeek => true;
    public TimeSpan? Duration => PcmAudioFormat.GetDuration(_total);
    public TimeSpan Position => PcmAudioFormat.GetDuration(_offset);

    public int Read(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _total - _offset);
        if (count <= 0)
        {
            return 0;
        }

        destination[..count].Clear();
        _offset += count;
        return count;
    }

    public void Seek(TimeSpan position)
        => _offset = Math.Clamp(PcmAudioFormat.GetByteCount(position), 0, _total);

    public void Dispose() { }
}

// Ownership transfers to the playback; it disposes the stream when finished.
session.Play(new SilenceStream(TimeSpan.FromSeconds(3)));
```

---

## SoundPlayerException

`sealed class SoundPlayerException : Exception` with a `PlaybackErrorReason Reason` property.

Thrown by `IAudioFileService` and `INetworkAudioService` when a source cannot be opened. Playbacks
started through a session never throw it — they surface the same reason through
[`ISoundPlayback.Error`](playback.md#playbackerror) instead.

---

## External Tools

ffmpeg and yt-dlp are resolved at module start in this order: the module's `tools` directory,
then `PATH`, then downloaded from the official releases into the module's `tools` directory.
Caches stay inside `modules/TnmsSoundPlayer/tools/cache/` so nothing leaks into the user profile.

yt-dlp additionally needs a JavaScript runtime (deno, downloaded the same way) to solve YouTube's
nsig challenge. Without it, audio downloads fail with HTTP 403.

Check availability through [`ITnmsSoundPlayer.Diagnostics`](playback.md#soundplayerdiagnostics).
