namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Handle to a single queued or playing sound, with the controls the owner needs.
/// State transitions: Queued → Playing ⇄ Paused → Completed | Stopped | Failed,
/// or directly to Rejected when the queue refuses the playback.
/// You get one from your own <c>Play</c> call or from <see cref="ISoundPlayerSession.OwnPlaybacks"/>;
/// other plugins' playbacks are only visible as <see cref="ISoundPlaybackInfo"/>.
/// Control methods must be called from the game thread; property reads are thread-safe.
/// To observe start and completion, pass an <see cref="ISoundPlaybackCallback"/> when starting the
/// playback, or await <see cref="Completion"/>.
/// </summary>
public interface ISoundPlayback : ISoundPlaybackInfo
{
    /// <summary>Per-playback gain. Can be changed while playing.</summary>
    new float Volume { get; set; }

    /// <summary>Stops this playback (if playing) or removes it from the queue (if queued).</summary>
    void Stop();

    void Pause();

    void Resume();

    /// <summary>Seeks within the source. Throws <see cref="NotSupportedException"/> when the source cannot seek.</summary>
    void Seek(TimeSpan position);

    /// <summary>
    /// Completes when the playback reaches a terminal state. Never faults for media errors;
    /// inspect <see cref="ISoundPlaybackInfo.State"/> and <see cref="ISoundPlaybackInfo.Error"/>
    /// after awaiting.
    /// </summary>
    Task Completion { get; }
}
