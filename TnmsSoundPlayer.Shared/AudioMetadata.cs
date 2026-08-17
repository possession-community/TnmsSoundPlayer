namespace TnmsSoundPlayer.Shared;

/// <summary>Metadata of a network audio source, as reported by yt-dlp.</summary>
public sealed record AudioMetadata(
    string? Title,
    TimeSpan? Duration,
    string? Uploader);
