namespace TnmsSoundPlayer.Shared;

/// <summary>Why a playback ended in Failed/Rejected, or why a Stopped playback was cut off.</summary>
public enum PlaybackErrorReason
{
    /// <summary>The file or buffer source could not be found or opened.</summary>
    SourceNotFound,

    /// <summary>ffmpeg failed to decode the source.</summary>
    DecodeFailed,

    /// <summary>ffmpeg executable is not available.</summary>
    FfmpegNotFound,

    /// <summary>yt-dlp executable is not available.</summary>
    YtdlpNotFound,

    /// <summary>yt-dlp could not resolve the URL to a media stream.</summary>
    UrlResolveFailed,

    /// <summary>The global queue or the session's queue limit was reached (or RejectIfBusy hit a busy player).</summary>
    QueueLimitReached,

    /// <summary>Another playback with <see cref="QueueBehavior.Interrupt"/> cut this playback off.
    /// Reported on a playback in the Stopped state, not Failed.</summary>
    Interrupted,

    /// <summary>The playback was cancelled before it could start (e.g. module shutdown).</summary>
    Cancelled,
}

/// <summary>Detail attached to <see cref="ISoundPlayback.Error"/>.</summary>
public sealed record PlaybackError(PlaybackErrorReason Reason, string Message);
