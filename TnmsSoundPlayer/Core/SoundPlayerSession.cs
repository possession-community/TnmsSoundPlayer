using Sharp.Shared.Objects;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer.Core;

internal sealed class SoundPlayerSession : ISoundPlayerSession
{
    private readonly SoundPlayerCore _core;

    // Keyed by player slot. Hearing is written and read on the game thread; volumes are also read by
    // the encode worker through GetVolumeBuckets, so those need the lock.
    private readonly Dictionary<int, bool> _hearing = [];
    private readonly Dictionary<int, float> _playerVolumes = [];
    private readonly Lock _volumeLock = new();

    public string OwnerName { get; }

    internal SoundPlayerSession(string ownerName, SoundPlayerCore core)
    {
        OwnerName = ownerName;
        _core = core;
    }

    public ISoundPlayback Play(IPcmAudioStream stream, PlayOptions? options = null, ISoundPlaybackCallback? callback = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return _core.Play(OwnerName, _ => Task.FromResult(stream), stream, options, callback);
    }

    public ISoundPlayback PlayFile(string path, PlayOptions? options = null, ISoundPlaybackCallback? callback = null)
        => _core.Play(OwnerName, ct => _core.FileService.OpenFileAsync(path, ct), null, options, callback);

    public ISoundPlayback PlayUrl(string url, PlayOptions? options = null, ISoundPlaybackCallback? callback = null)
    {
        var downloadFirst = options?.DownloadFirst ?? false;
        return _core.Play(
            OwnerName, ct => _core.NetworkService.OpenUrlAsync(url, downloadFirst, ct), null, options, callback);
    }

    public IReadOnlyList<ISoundPlayback> OwnPlaybacks => _core.GetOwnedPlaybacks(OwnerName);

    public void StopAll() => _core.StopAllOwnedBy(OwnerName);

    // ---- per-client state (this session only) ----

    public bool DefaultHearing { get; set; } = true;

    public void SetHearing(bool hearing, IEnumerable<IGameClient>? clients = null)
    {
        foreach (var slot in _core.SlotsOf(clients))
        {
            _hearing[slot] = hearing;
        }
    }

    public bool GetHearing(IGameClient client)
        => HearsSlot(SoundPlayerCore.SlotOf(client));

    internal bool HearsSlot(int slot)
        => _hearing.TryGetValue(slot, out var hearing) ? hearing : DefaultHearing;

    public void SetPlayerVolume(float volume, IEnumerable<IGameClient>? clients = null)
    {
        var clamped = Math.Clamp(volume, 0f, 4f);
        var slots = _core.SlotsOf(clients);

        lock (_volumeLock)
        {
            foreach (var slot in slots)
            {
                _playerVolumes[slot] = clamped;
            }
        }
    }

    public float GetPlayerVolume(IGameClient client)
        => VolumeOfSlot(SoundPlayerCore.SlotOf(client));

    internal float VolumeOfSlot(int slot)
    {
        lock (_volumeLock)
        {
            return _playerVolumes.TryGetValue(slot, out var volume) ? volume : 1f;
        }
    }

    /// <summary>
    /// Distinct volumes in use for this session — one Opus encode per entry, per chunk.
    /// No cap: the map is keyed by player slot, so the count is bounded by the server's player limit
    /// (64 measured at 14.7 ms per 60 ms chunk, ~25% of one worker thread). Rounding to 0.01
    /// collapses float noise; that step is inaudible.
    /// </summary>
    internal float[] GetVolumeBuckets()
    {
        lock (_volumeLock)
        {
            return _playerVolumes.Values
                .Select(v => MathF.Round(v, 2))
                .Where(v => v > 0.001f && v != 1f)
                .Distinct()
                .Append(1f)
                .ToArray();
        }
    }

    /// <summary>Drops a disconnected client's state; slots are reused, so leaving it would leak onto the next player.</summary>
    internal void ForgetSlot(int slot)
    {
        _hearing.Remove(slot);
        lock (_volumeLock)
        {
            _playerVolumes.Remove(slot);
        }
    }
}
