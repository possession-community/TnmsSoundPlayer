namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Pull-based stream of decoded PCM audio in the fixed format described by <see cref="PcmAudioFormat"/>.
/// Implement this to feed arbitrary custom sources (e.g. TTS, procedural audio) into the player.
/// A stream instance is single-use and owned by whoever consumes it; when passed to
/// <see cref="ISoundPlayerSession.Play"/>, ownership transfers to the playback, which disposes it when finished.
/// </summary>
public interface IPcmAudioStream : IDisposable
{
    /// <summary>
    /// Reads decoded PCM bytes into <paramref name="destination"/>.
    /// Returns the number of bytes written; 0 means end of stream.
    /// May return fewer bytes than requested (e.g. while buffering a network source).
    /// </summary>
    int Read(Span<byte> destination);

    bool CanSeek { get; }

    /// <summary>Total duration, or null while unknown (e.g. live/network streams).</summary>
    TimeSpan? Duration { get; }

    TimeSpan Position { get; }

    /// <summary>Seeks to the given position. Throws <see cref="NotSupportedException"/> when <see cref="CanSeek"/> is false.</summary>
    void Seek(TimeSpan position);
}

public static class PcmAudioStreamExtensions
{
    /// <summary>
    /// Reads up to <paramref name="seconds"/> seconds of audio into <paramref name="destination"/>.
    /// Returns the number of bytes written.
    /// </summary>
    public static int ReadSeconds(this IPcmAudioStream stream, float seconds, Span<byte> destination)
    {
        var bytes = PcmAudioFormat.GetByteCount(TimeSpan.FromSeconds(seconds));
        return stream.Read(destination[..Math.Min(bytes, destination.Length)]);
    }
}
