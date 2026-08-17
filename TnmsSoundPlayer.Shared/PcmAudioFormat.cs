namespace TnmsSoundPlayer.Shared;

/// <summary>
/// The fixed PCM format used across the sound player pipeline:
/// 48 kHz, mono, signed 16-bit little-endian.
/// </summary>
public static class PcmAudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = SampleRate * Channels * BytesPerSample; // 96,000

    /// <summary>Duration of one Opus frame.</summary>
    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);

    /// <summary>PCM bytes per 20 ms Opus frame (960 samples).</summary>
    public const int FrameBytes = 1920;

    public static int GetByteCount(TimeSpan duration)
        => (int)(duration.TotalSeconds * BytesPerSecond) / BytesPerSample * BytesPerSample;

    public static TimeSpan GetDuration(long byteCount)
        => TimeSpan.FromSeconds((double)byteCount / BytesPerSecond);
}
