namespace TnmsSoundPlayer.Shared;

/// <summary>Behavior when a playback is requested while another one is on air.</summary>
public enum QueueBehavior
{
    /// <summary>Wait in the global priority queue.</summary>
    Enqueue,

    /// <summary>
    /// Stop the current playback (its <see cref="ISoundPlayback.Error"/> is set to
    /// <see cref="PlaybackErrorReason.Interrupted"/>) and play immediately.
    /// The interrupted playback is not resumed afterwards.
    /// </summary>
    Interrupt,

    /// <summary>Return a playback in the Rejected state instead of waiting.</summary>
    RejectIfBusy,
}
