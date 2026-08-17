namespace TnmsSoundPlayer.Shared;

/// <summary>Runtime health and queue statistics. Snapshot; safe to read from any thread.</summary>
public sealed record SoundPlayerDiagnostics(
    bool FfmpegAvailable,
    string? FfmpegPath,
    bool YtdlpAvailable,
    string? YtdlpPath,
    int QueueLength,
    int ActiveSessionCount);
