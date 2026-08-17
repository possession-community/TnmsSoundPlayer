namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Lifecycle state of a playback.
/// Queued → Playing ⇄ Paused → Completed | Stopped | Failed; Rejected is terminal from the start.
/// </summary>
public enum PlaybackState
{
    /// <summary>Waiting in the global queue.</summary>
    Queued,

    /// <summary>Currently on air.</summary>
    Playing,

    /// <summary>Paused by <see cref="ISoundPlayback.Pause"/>; still occupies the single playback slot.</summary>
    Paused,

    /// <summary>The source played to its end.</summary>
    Completed,

    /// <summary>Stopped before the end — explicitly, by a session/global StopAll, or by an interrupt
    /// (see <see cref="ISoundPlayback.Error"/> to distinguish).</summary>
    Stopped,

    /// <summary>Playback could not start or aborted due to a media error. See <see cref="ISoundPlayback.Error"/>.</summary>
    Failed,

    /// <summary>The queue refused this playback (limit reached or RejectIfBusy). See <see cref="ISoundPlayback.Error"/>.</summary>
    Rejected,
}
