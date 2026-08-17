namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Handle to a single queued or playing sound.
/// State transitions: Queued → Playing ⇄ Paused → Completed | Stopped | Failed,
/// or directly to Rejected when the queue refuses the playback.
/// Control methods must be called from the game thread; property reads are thread-safe.
/// To observe start and completion, pass an <see cref="ISoundPlaybackCallback"/> when starting the
/// playback, or await <see cref="Completion"/>.
/// </summary>
public interface ISoundPlayback
{
    /// <summary>Unique, monotonically increasing playback id.</summary>
    long Id { get; }

    /// <summary>Owner name of the session that started this playback.</summary>
    string OwnerName { get; }

    PlaybackState State { get; }

    TimeSpan Position { get; }

    /// <summary>Total duration, or null while unknown (e.g. live/network streams).</summary>
    TimeSpan? Duration { get; }

    /// <summary>Per-playback gain. Can be changed while playing.</summary>
    float Volume { get; set; }

    /// <summary>
    /// Failure detail. Non-null when <see cref="State"/> is Failed or Rejected;
    /// also set with <see cref="PlaybackErrorReason.Interrupted"/> when a Stopped playback
    /// was cut off by another playback rather than an explicit Stop call.
    /// </summary>
    PlaybackError? Error { get; }

    /// <summary>Stops this playback (if playing) or removes it from the queue (if queued).</summary>
    void Stop();

    void Pause();

    void Resume();

    /// <summary>Seeks within the source. Throws <see cref="NotSupportedException"/> when the source cannot seek.</summary>
    void Seek(TimeSpan position);

    /// <summary>
    /// Completes when the playback reaches a terminal state. Never faults for media errors;
    /// inspect <see cref="State"/> and <see cref="Error"/> after awaiting.
    /// </summary>
    Task Completion { get; }
}
