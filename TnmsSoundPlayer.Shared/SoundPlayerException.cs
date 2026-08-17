namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Thrown by <see cref="IAudioFileService"/> / <see cref="INetworkAudioService"/> when a source
/// cannot be opened. Playbacks started through a session never throw this; they surface the same
/// reason via <see cref="ISoundPlayback.Error"/> instead.
/// </summary>
public sealed class SoundPlayerException : Exception
{
    public PlaybackErrorReason Reason { get; }

    public SoundPlayerException(PlaybackErrorReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}
