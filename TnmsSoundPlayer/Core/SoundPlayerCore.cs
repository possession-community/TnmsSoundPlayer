using System.Diagnostics;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using TnmsSoundPlayer.Media;
using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer.Core;

/// <summary>
/// The sound player engine: a single global playback slot fed by a priority queue,
/// a 20 ms pump timer that paces encoded packets out as CSVCMsg_VoiceData, and
/// per-client hearing / volume state. All public API entry points run on the game thread.
/// </summary>
internal sealed class SoundPlayerCore : ITnmsSoundPlayer, IClientListener
{
    private const int MaxQueueLength = 32;
    private const int MaxQueuePerSession = 16;
    private const int PrebufferChunks = 4;   // ~240 ms before the first packet goes out
    private const int MaxSendsPerTick = 5;   // catch-up bound after hitches
    private const int MaxVolumeBuckets = 8;

    // SpeakerIdentity: which "speaker" clients see. Mutable for the Phase 0 in-game
    // experiments (slot / xuid / fake client). An invalid slot plays audio with no UI.
    internal int SpeakerSlot { get; set; } = -1;
    internal ulong SpeakerXuid { get; set; }

    // The speaker bot itself is owned by SpeakerManager, which the Shared API must not see.
    // The module wires these on startup; before that, SpeakerSteamId simply reports 0.
    internal Func<ulong>? SpeakerSteamIdReader { get; set; }
    internal Action<ulong>? SpeakerSteamIdWriter { get; set; }

    public ulong SpeakerSteamId
    {
        get => SpeakerSteamIdReader?.Invoke() ?? 0;
        set => SpeakerSteamIdWriter?.Invoke(value);
    }

    private readonly ILogger _logger;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly ToolManager _tools;

    private readonly Dictionary<string, SoundPlayerSession> _sessions = [];
    private readonly List<SoundPlayback> _queue = [];
    private readonly List<SoundPlayback> _pendingFinish = [];
    private readonly Dictionary<int, bool> _hearing = [];
    private readonly Dictionary<int, float> _playerVolumes = [];
    private readonly Lock _volumeLock = new();

    private SoundPlayback? _current;
    private long _nextPlaybackId;

    public SoundPlayerCore(ILogger logger, IModSharp modSharp, IClientManager clients, ToolManager tools,
        IAudioFileService fileService, INetworkAudioService networkService)
    {
        _logger = logger;
        _modSharp = modSharp;
        _clients = clients;
        _tools = tools;
        FileService = fileService;
        NetworkService = networkService;
    }

    // ---- ITnmsSoundPlayer ----

    public ISoundPlayerSession CreateSession(string ownerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);

        if (!_sessions.TryGetValue(ownerName, out var session))
        {
            session = new SoundPlayerSession(ownerName, this);
            _sessions[ownerName] = session;
        }
        return session;
    }

    public ISoundPlayback? CurrentPlayback => _current;

    public IReadOnlyList<ISoundPlayback> Queue => [.. _queue];

    public void StopAll()
    {
        var queued = _queue.ToArray();
        _queue.Clear();
        foreach (var playback in queued)
        {
            playback.Finalize(PlaybackState.Stopped, null, LogListenerError);
        }

        if (_current is { } current)
        {
            _current = null;
            current.Finalize(PlaybackState.Stopped, null, LogListenerError);
        }
    }

    public void SetHearing(IGameClient client, bool hearing)
        => _hearing[SlotOf(client)] = hearing;

    public bool GetHearing(IGameClient client)
        => _hearing.TryGetValue(SlotOf(client), out var hearing) ? hearing : DefaultHearing;

    public bool DefaultHearing { get; set; } = true;

    public void SetPlayerVolume(IGameClient client, float volume)
    {
        lock (_volumeLock)
        {
            _playerVolumes[SlotOf(client)] = Math.Clamp(volume, 0f, 4f);
        }
    }

    public float GetPlayerVolume(IGameClient client)
    {
        lock (_volumeLock)
        {
            return _playerVolumes.TryGetValue(SlotOf(client), out var volume) ? volume : 1f;
        }
    }

    public IAudioFileService FileService { get; }
    public INetworkAudioService NetworkService { get; }

    public SoundPlayerDiagnostics Diagnostics => new(
        FfmpegAvailable: _tools.FfmpegPath is not null,
        FfmpegPath: _tools.FfmpegPath,
        YtdlpAvailable: _tools.YtdlpPath is not null,
        YtdlpPath: _tools.YtdlpPath,
        QueueLength: _queue.Count,
        ActiveSessionCount: _sessions.Count);

    // ---- session-facing ----

    internal ISoundPlayback Play(
        string ownerName,
        Func<CancellationToken, Task<IPcmAudioStream>> sourceFactory,
        IPcmAudioStream? providedStream,
        PlayOptions? options,
        ISoundPlaybackCallback? callback)
    {
        options ??= new PlayOptions();
        var playback = new SoundPlayback(
            Interlocked.Increment(ref _nextPlaybackId), ownerName, options, sourceFactory, providedStream, this)
        {
            // Attached before any queue decision, so even an immediate rejection reaches the callback.
            Callback = callback,
        };

        if (options.Recipients is SoundRecipients.SnapshotRecipients snapshot)
        {
            playback.SnapshotSlots = [.. snapshot.Clients.Select(SlotOf)];
        }

        var activeTotal = _queue.Count + (_current is not null ? 1 : 0);
        var activeOwned = CountActiveOwnedBy(ownerName);
        if (activeTotal >= MaxQueueLength || activeOwned >= MaxQueuePerSession)
        {
            return Reject(playback, $"Queue limit reached (total={activeTotal}, own={activeOwned}).");
        }

        var busy = _current is not null || _queue.Count > 0;
        switch (options.WhenBusy)
        {
            case QueueBehavior.RejectIfBusy when busy:
                return Reject(playback, "Another playback is in progress (RejectIfBusy).");

            case QueueBehavior.Interrupt when busy:
                if (_current is { } current)
                {
                    _current = null;
                    current.Finalize(PlaybackState.Stopped,
                        new PlaybackError(PlaybackErrorReason.Interrupted, $"Interrupted by playback #{playback.Id} ({ownerName})."),
                        LogListenerError);
                }
                _queue.Insert(0, playback);
                break;

            default:
                EnqueueByPriority(playback);
                break;
        }

        return playback;
    }

    internal IReadOnlyList<ISoundPlayback> GetOwnedPlaybacks(string ownerName)
    {
        var result = new List<ISoundPlayback>();
        if (_current is { } current && current.OwnerName == ownerName)
        {
            result.Add(current);
        }
        result.AddRange(_queue.Where(p => p.OwnerName == ownerName));
        return result;
    }

    internal void StopAllOwnedBy(string ownerName)
    {
        foreach (var playback in _queue.Where(p => p.OwnerName == ownerName).ToArray())
        {
            _queue.Remove(playback);
            playback.Finalize(PlaybackState.Stopped, null, LogListenerError);
        }

        if (_current is { } current && current.OwnerName == ownerName)
        {
            _current = null;
            current.Finalize(PlaybackState.Stopped, null, LogListenerError);
        }
    }

    // ---- playback-facing ----

    internal void RequestStop(SoundPlayback playback)
    {
        if (_current == playback)
        {
            _current = null;
            playback.Finalize(PlaybackState.Stopped, null, LogListenerError);
        }
        else if (_queue.Remove(playback))
        {
            playback.Finalize(PlaybackState.Stopped, null, LogListenerError);
        }
    }

    internal void RequestPause(SoundPlayback playback)
    {
        if (_current == playback && playback.State == PlaybackState.Playing)
        {
            playback.SetState(PlaybackState.Paused);
        }
    }

    internal void RequestResume(SoundPlayback playback)
    {
        if (_current == playback && playback.State == PlaybackState.Paused)
        {
            playback.SetState(PlaybackState.Playing);
            playback.HoldClock();
        }
    }

    /// <summary>Distinct per-player volume values the encode worker must produce.</summary>
    internal float[] GetVolumeBuckets()
    {
        lock (_volumeLock)
        {
            return _playerVolumes.Values
                .Select(v => MathF.Round(v, 2))
                .Where(v => v > 0.001f && v != 1f)
                .Distinct()
                .Take(MaxVolumeBuckets - 1)
                .Append(1f)
                .ToArray();
        }
    }

    // ---- pump (game thread, every 20 ms) ----

    internal void OnPump()
    {
        try
        {
            FlushPendingFinish();
            PumpCurrent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sound player pump tick failed.");
        }
    }

    private void FlushPendingFinish()
    {
        if (_pendingFinish.Count == 0)
        {
            return;
        }

        var pending = _pendingFinish.ToArray();
        _pendingFinish.Clear();
        foreach (var playback in pending)
        {
            playback.Finalize(playback.State, playback.Error, LogListenerError);
        }
    }

    private void PumpCurrent()
    {
        var current = _current;
        if (current is null)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            current = _queue[0];
            _queue.RemoveAt(0);
            _current = current;
            current.StartWorker();
        }

        if (current.WorkerError is { } workerError && current.Chunks.IsEmpty)
        {
            FinishCurrent(current, PlaybackState.Failed, new PlaybackError(workerError.Reason, workerError.Message));
            return;
        }

        if (current.State == PlaybackState.Paused)
        {
            current.HoldClock();
            return;
        }

        if (!current.StartedFired && !current.DecodeDone && current.Chunks.Count < PrebufferChunks)
        {
            current.HoldClock();
            return;
        }

        var sent = 0;
        while (sent < MaxSendsPerTick
               && current.IsDue(Stopwatch.GetTimestamp())
               && current.Chunks.TryDequeue(out var chunk))
        {
            current.BufferSlots.Release();
            SendChunk(current, chunk);
            current.AdvanceClock(chunk.Seconds);
            current.OnChunkSent(chunk.Seconds);

            if (!current.StartedFired)
            {
                current.StartedFired = true;
                current.SetState(PlaybackState.Playing);
                current.FireStarted(LogListenerError);
            }
            sent++;
        }

        if (current.Chunks.IsEmpty)
        {
            if (current.DecodeDone)
            {
                if (current.WorkerError is { } lateError)
                {
                    FinishCurrent(current, PlaybackState.Failed, new PlaybackError(lateError.Reason, lateError.Message));
                }
                else
                {
                    FinishCurrent(current, PlaybackState.Completed, null);
                }
            }
            else
            {
                // Starving (slow network source): don't accumulate send debt.
                current.HoldClock();
            }
        }
    }

    private void FinishCurrent(SoundPlayback playback, PlaybackState state, PlaybackError? error)
    {
        _current = null;
        playback.Finalize(state, error, LogListenerError);
    }

    // ---- packet send ----

    private void SendChunk(SoundPlayback playback, EncodedChunk chunk)
    {
        playback.Section++;

        List<(IGameClient Client, float Volume)>? recipients = null;
        foreach (var client in _clients.GetGameClientList(true))
        {
            if (client.IsFakeClient || client.IsHltv || !GetHearing(client))
            {
                continue;
            }

            if (!Matches(playback, client))
            {
                continue;
            }

            var volume = GetPlayerVolume(client);
            if (volume <= 0.001f)
            {
                continue;
            }

            (recipients ??= []).Add((client, volume));
        }

        if (recipients is null)
        {
            return;
        }

        foreach (var group in recipients.GroupBy(r => NearestBucket(chunk, r.Volume)))
        {
            if (!chunk.Buckets.TryGetValue(group.Key, out var packet))
            {
                continue;
            }

            var message = BuildMessage(playback, packet);
            _modSharp.SendNetMessage(new RecipientFilter(group.Select(r => r.Client)), message);
        }
    }

    private static float NearestBucket(EncodedChunk chunk, float volume)
    {
        var best = 1f;
        var bestDistance = float.MaxValue;
        foreach (var key in chunk.Buckets.Keys)
        {
            var distance = MathF.Abs(key - volume);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = key;
            }
        }
        return best;
    }

    private CSVCMsg_VoiceData BuildMessage(SoundPlayback playback, OpusPacket packet)
    {
        var audio = new CMsgVoiceAudio
        {
            Format = VoiceDataFormat_t.VoicedataFormatOpus,
            SampleRate = PipelineFormat.SampleRate,
            SectionNumber = playback.Section,
            NumPackets = packet.NumPackets,
            SequenceBytes = 0,
            VoiceLevel = 0f,
            VoiceData = ByteString.CopyFrom(packet.Data),
        };
        foreach (var offset in packet.Offsets)
        {
            audio.PacketOffsets.Add(offset);
        }

        return new CSVCMsg_VoiceData
        {
            ClientDeprecated = SpeakerSlot,
            Xuid = SpeakerXuid,
            Audio = audio,
        };
    }

    private bool Matches(SoundPlayback playback, IGameClient client)
    {
        try
        {
            return playback.Options.Recipients switch
            {
                SoundRecipients.AllRecipients => true,
                SoundRecipients.SingleRecipient single => SlotOf(single.Client) == SlotOf(client),
                SoundRecipients.SnapshotRecipients => playback.SnapshotSlots?.Contains(SlotOf(client)) ?? false,
                SoundRecipients.PredicateRecipients predicate => predicate.Predicate(client),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recipient predicate threw; excluding client {Slot}.", SlotOf(client));
            return false;
        }
    }

    // ---- queue helpers ----

    private ISoundPlayback Reject(SoundPlayback playback, string message)
    {
        playback.SetState(PlaybackState.Rejected);
        playback.SetRejectedError(new PlaybackError(PlaybackErrorReason.QueueLimitReached, message));
        // Finished fires on the next pump tick so the caller can subscribe first.
        _pendingFinish.Add(playback);
        return playback;
    }

    private void EnqueueByPriority(SoundPlayback playback)
    {
        var index = _queue.Count;
        for (var i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Options.Priority < playback.Options.Priority)
            {
                index = i;
                break;
            }
        }
        _queue.Insert(index, playback);
    }

    private int CountActiveOwnedBy(string ownerName)
    {
        var count = _queue.Count(p => p.OwnerName == ownerName);
        if (_current is { } current && current.OwnerName == ownerName)
        {
            count++;
        }
        return count;
    }

    private static int SlotOf(IGameClient client)
        => (int)client.Slot.AsPrimitive();

    private void LogListenerError(Exception ex)
        => _logger.LogError(ex, "A playback event listener threw.");

    // ---- IClientListener ----

    public int ListenerVersion => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    /// <summary>Raised on the game thread when a client connects or is put in server (used by SpeakerManager).</summary>
    internal event Action<IGameClient>? ClientConnected;

    public void OnClientConnected(IGameClient client)
    {
        _hearing[SlotOf(client)] = DefaultHearing;
        lock (_volumeLock)
        {
            _playerVolumes[SlotOf(client)] = 1f;
        }
        ClientConnected?.Invoke(client);
    }

    public void OnClientPutInServer(IGameClient client)
        => ClientConnected?.Invoke(client);

    /// <summary>Raised on the game thread when any client disconnects (used by SpeakerManager).</summary>
    internal event Action<IGameClient>? ClientDisconnected;

    public void OnClientDisconnected(IGameClient client, Sharp.Shared.Enums.NetworkDisconnectionReason reason)
    {
        _hearing.Remove(SlotOf(client));
        lock (_volumeLock)
        {
            _playerVolumes.Remove(SlotOf(client));
        }
        ClientDisconnected?.Invoke(client);
    }

    // ---- lifecycle ----

    internal void Shutdown()
        => StopAll();
}
