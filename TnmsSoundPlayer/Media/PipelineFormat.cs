namespace TnmsSoundPlayer.Media;

/// <summary>
/// Internal wire format of the decode → encode → send pipeline.
/// Channels: tested 2026-08-17 — the client accepts stereo Opus voice packets but downmixes
/// them to mono on playback, so stereo only wastes bitrate. Keep 1.
/// </summary>
internal static class PipelineFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BytesPerSample = 2;
    public const int BlockBytes = Channels * BytesPerSample;

    /// <summary>Samples per channel in one 20 ms Opus frame.</summary>
    public const int FrameSamplesPerChannel = 960;

    public const int FrameBytes = FrameSamplesPerChannel * BlockBytes;
    public const int BytesPerSecond = SampleRate * BlockBytes;

    public static int GetByteCount(TimeSpan duration)
        => (int)((long)(duration.TotalSeconds * BytesPerSecond) / BlockBytes * BlockBytes);

    public static TimeSpan GetDuration(long byteCount)
        => TimeSpan.FromSeconds((double)byteCount / BytesPerSecond);
}
