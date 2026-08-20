using Sharp.Shared.Objects;

namespace TnmsSoundPlayer.Shared;

/// <summary>
/// A per-plugin handle to the sound player, obtained from <see cref="ITnmsSoundPlayer.CreateSession"/>.
/// All playbacks started through a session are attributed to its owner and count against
/// the session's queue limit.
/// </summary>
public interface ISoundPlayerSession
{
    string OwnerName { get; }

    /// <summary>
    /// Enqueues the given PCM stream for playback. Ownership of <paramref name="stream"/> transfers
    /// to the returned playback, which disposes it when the playback finishes (in any terminal state).
    /// Never throws for media errors; inspect <see cref="ISoundPlayback.State"/> and
    /// <see cref="ISoundPlayback.Error"/> instead, or supply <paramref name="callback"/>.
    /// </summary>
    ISoundPlayback Play(IPcmAudioStream stream, PlayOptions? options = null, ISoundPlaybackCallback? callback = null);

    /// <summary>Convenience for opening <paramref name="path"/> via <see cref="ITnmsSoundPlayer.FileService"/> and playing it.</summary>
    ISoundPlayback PlayFile(string path, PlayOptions? options = null, ISoundPlaybackCallback? callback = null);

    /// <summary>Convenience for opening <paramref name="url"/> via <see cref="ITnmsSoundPlayer.NetworkService"/> and playing it.</summary>
    ISoundPlayback PlayUrl(string url, PlayOptions? options = null, ISoundPlaybackCallback? callback = null);

    /// <summary>Snapshot of this session's playbacks that are currently playing or queued.</summary>
    IReadOnlyList<ISoundPlayback> OwnPlaybacks { get; }

    /// <summary>Stops and dequeues this session's playbacks only.</summary>
    void StopAll();

    /// <summary>
    /// Enables or disables this session's audio for the given clients, for every playback it starts
    /// from now on and any it has on air. Scoped to this session on purpose: a player who muted the
    /// jukebox should still hear round-start sounds from another plugin.
    /// <paramref name="clients"/> null (the default) means every connected client; an empty sequence
    /// means none, so a filter that matched nobody cannot silently mute the server.
    /// </summary>
    void SetHearing(bool hearing, IEnumerable<IGameClient>? clients = null);

    /// <summary>Whether this client currently hears this session, falling back to <see cref="DefaultHearing"/>.</summary>
    bool GetHearing(IGameClient client);

    /// <summary>
    /// Hearing state applied to clients that connect from now on.
    /// Changing it does not affect clients that are already connected.
    /// </summary>
    bool DefaultHearing { get; set; }

    /// <summary>
    /// Server-side volume multiplier for this session's audio, per client. 0.0 mutes, 1.0 is
    /// unmodified. Applied before encoding, and multiplied with <see cref="ISoundPlayback.Volume"/>.
    /// <paramref name="clients"/> follows the same null/empty rule as <see cref="SetHearing"/>.
    /// </summary>
    void SetPlayerVolume(float volume, IEnumerable<IGameClient>? clients = null);

    /// <summary>This client's multiplier for this session. 1.0 when never set.</summary>
    float GetPlayerVolume(IGameClient client);
}
