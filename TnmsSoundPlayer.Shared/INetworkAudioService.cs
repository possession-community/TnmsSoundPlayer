namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Opens network sources as PCM streams (resolved by yt-dlp, decoded through ffmpeg).
/// Stateless like <see cref="IAudioFileService"/>.
/// </summary>
public interface INetworkAudioService
{
    /// <summary>
    /// Opens any yt-dlp supported URL (YouTube etc.) as a PCM stream, streamed as it downloads.
    /// The result cannot seek and reports an unknown duration.
    /// </summary>
    Task<IPcmAudioStream> OpenUrlAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Opens a URL, optionally fetching the whole thing to a temporary file first.
    /// A downloaded source can seek and knows its duration, at the cost of waiting for the
    /// download before any audio plays. The temporary file is deleted when the stream is disposed.
    /// </summary>
    Task<IPcmAudioStream> OpenUrlAsync(string url, bool downloadFirst, CancellationToken ct = default);

    /// <summary>Fetches metadata for the URL without downloading the media (yt-dlp -J).</summary>
    Task<AudioMetadata> GetMetadataAsync(string url, CancellationToken ct = default);
}
