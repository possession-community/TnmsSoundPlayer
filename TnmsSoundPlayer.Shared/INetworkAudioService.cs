namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Opens network sources as PCM streams (resolved by yt-dlp, decoded through ffmpeg).
/// Stateless like <see cref="IAudioFileService"/>.
/// </summary>
public interface INetworkAudioService
{
    /// <summary>Opens any yt-dlp supported URL (YouTube etc.) as a PCM stream.</summary>
    Task<IPcmAudioStream> OpenUrlAsync(string url, CancellationToken ct = default);

    /// <summary>Fetches metadata for the URL without downloading the media (yt-dlp -J).</summary>
    Task<AudioMetadata> GetMetadataAsync(string url, CancellationToken ct = default);
}
