namespace TnmsSoundPlayer.Shared;

/// <summary>
/// A per-plugin handle to the sound player, obtained from <see cref="ITnmsSoundPlayer.CreateSession"/>.
/// All playbacks started through a session are attributed to its owner and count against
/// the session's queue limit.
/// </summary>
public interface ISoundPlayerSession
{
    string OwnerName { get; }

    /// <summary>
    /// Enqueues the given PCM stream for playback. Ownership of <paramref name="stream"/> transfers
    /// to the returned playback, which disposes it when the playback finishes (in any terminal state).
    /// Never throws for media errors; inspect <see cref="ISoundPlayback.State"/> and
    /// <see cref="ISoundPlayback.Error"/> instead, or supply <paramref name="callback"/>.
    /// </summary>
    ISoundPlayback Play(IPcmAudioStream stream, PlayOptions? options = null, ISoundPlaybackCallback? callback = null);

    /// <summary>Convenience for opening <paramref name="path"/> via <see cref="ITnmsSoundPlayer.FileService"/> and playing it.</summary>
    ISoundPlayback PlayFile(string path, PlayOptions? options = null, ISoundPlaybackCallback? callback = null);

    /// <summary>Convenience for opening <paramref name="url"/> via <see cref="ITnmsSoundPlayer.NetworkService"/> and playing it.</summary>
    ISoundPlayback PlayUrl(string url, PlayOptions? options = null, ISoundPlaybackCallback? callback = null);

    /// <summary>Snapshot of this session's playbacks that are currently playing or queued.</summary>
    IReadOnlyList<ISoundPlayback> OwnPlaybacks { get; }

    /// <summary>Stops and dequeues this session's playbacks only.</summary>
    void StopAll();
}
