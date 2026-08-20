namespace TnmsSoundPlayer.Shared;

/// <summary>Options controlling how a playback is queued and rendered.</summary>
public sealed record PlayOptions
{
    /// <summary>Who receives the audio. Defaults to every connected client (live set).</summary>
    public SoundRecipients Recipients { get; init; } = SoundRecipients.All;

    /// <summary>Initial per-playback gain.</summary>
    public float Volume { get; init; } = 1.0f;

    /// <summary>Position within the source to start playing from.</summary>
    public TimeSpan StartAt { get; init; }

    /// <summary>
    /// Loops the source until the playback is explicitly stopped or interrupted.
    /// Note: queued playbacks behind a looping one wait indefinitely by design.
    /// </summary>
    public bool Loop { get; init; }

    /// <summary>Queue priority. Higher plays first; equal priorities play in arrival order.</summary>
    public int Priority { get; init; }

    /// <summary>What to do when another playback is already on air.</summary>
    public QueueBehavior WhenBusy { get; init; } = QueueBehavior.Enqueue;

    /// <summary>
    /// Renames the speaker to this for as long as this playback is audible, then puts
    /// <see cref="ITnmsSoundPlayer.SpeakerName"/> back. Null (the default) leaves the name alone.
    /// Useful for showing who requested the sound, e.g. "SoundPlayer: by faketuna".
    /// </summary>
    public string? SpeakerName { get; init; }
}
