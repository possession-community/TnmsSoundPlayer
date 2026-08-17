namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Notifications for a single playback, supplied when the playback is started.
/// Both methods have a default implementation, so implement only the ones you need.
/// <para>
/// Callbacks run on the game thread. Because the callback is handed over before the playback
/// exists, no notification can be missed, and the player releases its reference as soon as the
/// playback reaches a terminal state — there is nothing to unsubscribe.
/// </para>
/// </summary>
public interface ISoundPlaybackCallback
{
    /// <summary>Called when the playback actually starts producing audio.</summary>
    void OnStarted(ISoundPlayback playback)
    {
    }

    /// <summary>
    /// Called exactly once when the playback reaches a terminal state
    /// (Completed, Stopped, Failed or Rejected). Read <see cref="ISoundPlayback.State"/> and
    /// <see cref="ISoundPlayback.Error"/> to tell which.
    /// </summary>
    void OnFinished(ISoundPlayback playback)
    {
    }
}
