using Sharp.Shared.Objects;

namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Entry point of the sound player library, published through ISharpModuleManager.
/// The server plays at most one sound at any time; playbacks requested by every plugin
/// go through a single global priority queue.
/// All members must be called from the game thread unless noted otherwise.
/// </summary>
public interface ITnmsSoundPlayer
{
    /// <summary>Interface identity used when resolving this module from ISharpModuleManager.</summary>
    const string Identity = "TnmsSoundPlayer";

    /// <summary>
    /// Creates (or returns the existing) session for the given owner name.
    /// Calling twice with the same name returns the same session (idempotent).
    /// The session is the unit of per-plugin queue limits and <see cref="ISoundPlayerSession.StopAll"/>.
    /// </summary>
    ISoundPlayerSession CreateSession(string ownerName);

    /// <summary>
    /// The playback currently on air, or null when idle. Read-only on purpose: it may belong to
    /// another plugin, and stopping it is <see cref="StopAll"/>'s job, not a stray command's.
    /// </summary>
    ISoundPlaybackInfo? CurrentPlayback { get; }

    /// <summary>Snapshot of the pending queue in playback order (excludes <see cref="CurrentPlayback"/>).</summary>
    IReadOnlyList<ISoundPlaybackInfo> Queue { get; }

    /// <summary>
    /// Stops the current playback and clears the queue across all sessions (administrative).
    /// Per-client hearing and volume live on <see cref="ISoundPlayerSession"/>, so that muting one
    /// plugin cannot silence another; this is the one deliberately server-wide control.
    /// </summary>
    void StopAll();

    /// <summary>
    /// SteamID64 the speaker bot masquerades as; 0 (the default) disables the spoof.
    /// Set this to an account you control — the client resolves it to that account's avatar and,
    /// together with the rest of the disguise, is what stops the scoreboard from marking the
    /// speaker as a bot. Applied immediately, and re-applied to every bot created afterwards.
    /// </summary>
    ulong SpeakerSteamId { get; set; }

    /// <summary>
    /// Name the speaker shows on the scoreboard while nothing is playing. Blank resets it to the
    /// built-in default. A playback can take the name over for as long as it is audible through
    /// <see cref="PlayOptions.SpeakerName"/>; when it ends, the speaker goes back to this name.
    /// </summary>
    string SpeakerName { get; set; }

    /// <summary>Opens PCM streams from local files or in-memory encoded buffers (ffmpeg).</summary>
    IAudioFileService FileService { get; }

    /// <summary>Opens PCM streams from URLs (yt-dlp piped into ffmpeg).</summary>
    INetworkAudioService NetworkService { get; }

    /// <summary>Current runtime health and queue statistics. Safe to read from any thread.</summary>
    SoundPlayerDiagnostics Diagnostics { get; }
}
