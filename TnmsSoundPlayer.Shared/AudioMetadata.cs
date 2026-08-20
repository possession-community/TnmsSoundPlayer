namespace TnmsSoundPlayer.Shared;

/// <summary>Metadata of a network audio source, as reported by yt-dlp.</summary>
/// <param name="IsLive">
/// True for a source with no end: a live broadcast or a radio stream. Such a source occupies the
/// single playback slot until something stops it, and <see cref="PlayOptions.DownloadFirst"/>
/// cannot be honoured for it — there is nothing to finish downloading.
/// </param>
public sealed record AudioMetadata(
    string? Title,
    TimeSpan? Duration,
    string? Uploader,
    bool IsLive = false);
