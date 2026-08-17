namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Opens local sources as PCM streams (decoded through ffmpeg).
/// The service is stateless; playback position lives in the returned <see cref="IPcmAudioStream"/>,
/// so the same source can be opened multiple times concurrently.
/// </summary>
public interface IAudioFileService
{
    /// <summary>Opens an audio file from disk. Faults with a decode/not-found error when the source is unusable.</summary>
    Task<IPcmAudioStream> OpenFileAsync(string path, CancellationToken ct = default);

    /// <summary>Opens an in-memory encoded audio buffer (any format ffmpeg can decode).</summary>
    Task<IPcmAudioStream> OpenBufferAsync(ReadOnlyMemory<byte> encodedData, CancellationToken ct = default);
}
