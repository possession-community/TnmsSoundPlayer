namespace TnmsSoundPlayer.Shared;

/// <summary>
/// A read-only view of a sound that is queued or playing, whoever started it.
/// <see cref="ITnmsSoundPlayer.CurrentPlayback"/> and <see cref="ITnmsSoundPlayer.Queue"/> hand out
/// this view rather than <see cref="ISoundPlayback"/> so that one plugin's "stop my music" command
/// cannot cut off another plugin's sound by accident. To control a playback, keep the handle
/// returned by your own <c>Play</c> call, or go through
/// <see cref="ISoundPlayerSession.OwnPlaybacks"/>.
/// Property reads are thread-safe.
/// </summary>
public interface ISoundPlaybackInfo
{
    /// <summary>Unique, monotonically increasing playback id.</summary>
    long Id { get; }

    /// <summary>Owner name of the session that started this playback.</summary>
    string OwnerName { get; }

    PlaybackState State { get; }

    TimeSpan Position { get; }

    /// <summary>Total duration, or null while unknown (e.g. live/network streams).</summary>
    TimeSpan? Duration { get; }

    /// <summary>Per-playback gain.</summary>
    float Volume { get; }

    /// <summary>
    /// Failure detail. Non-null when <see cref="State"/> is Failed or Rejected;
    /// also set with <see cref="PlaybackErrorReason.Interrupted"/> when a Stopped playback
    /// was cut off by another playback rather than an explicit Stop call.
    /// </summary>
    PlaybackError? Error { get; }
}
