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

    /// <summary>
    /// Entity index voice is attributed to in <see cref="SpeakerMode.Entity" />. Nothing has to
    /// exist at it — that is the point — but nothing else should either, and the index cannot be
    /// reserved: the engine assigns indices itself, so this only has to sit where it is not going to
    /// be handed out. Near the top of the valid range (0..16383) and far above both the player
    /// indices (1..maxplayers, capped at 64) and anything a map allocates from the bottom up.
    /// </summary>
    internal const int DefaultSpeakerEntity = 15000;

    /// <summary>
    /// Stand-in xuid for <see cref="SpeakerMode.Entity" />, where nothing supplies a real one. The
    /// client keys one decoder and ring buffer per xuid, so this only has to be stable and non-zero.
    /// </summary>
    internal const ulong DefaultSpeakerXuid = 0x1100001_00000001;

    /// <summary>How the speaker is addressed on the wire. Fixed at startup by the module.</summary>
    internal SpeakerMode Mode { get; set; } = SpeakerMode.Bot;

    private bool UseEntityVoice => Mode == SpeakerMode.Entity;

    // SpeakerIdentity: which "speaker" clients see. An invalid slot plays audio with no UI.
    internal int SpeakerSlot { get; set; } = -1;
    internal ulong SpeakerXuid { get; set; }

    /// <summary>Entity index used in <see cref="SpeakerMode.Entity" />; ignored in Bot mode.</summary>
    internal int SpeakerEntity { get; set; } = DefaultSpeakerEntity;

    // In Bot mode the speaker bot owns this state and SpeakerManager wires the accessors below;
    // the Shared API must not see that type. In Entity mode there is no bot, so the accessors stay
    // null and these fields are the storage.
    private ulong _speakerSteamId = DefaultSpeakerXuid;
    private string _speakerName = string.Empty;

    internal Func<ulong>? SpeakerSteamIdReader { get; set; }
    internal Action<ulong>? SpeakerSteamIdWriter { get; set; }
    internal Func<string>? SpeakerNameReader { get; set; }
    internal Action<string>? SpeakerNameWriter { get; set; }

    /// <summary>Applies the name a playback borrowed the speaker with, or null to restore the default.</summary>
    internal Action<string?>? SpeakerNameOverrideWriter { get; set; }

    public ulong SpeakerSteamId
    {
        get => SpeakerSteamIdReader?.Invoke() ?? _speakerSteamId;
        set
        {
            // Entity mode sends this as the stream key on every packet, and 0 would key every
            // speaker to the same stream, so an explicit clear falls back to the stand-in.
            _speakerSteamId = value == 0 ? DefaultSpeakerXuid : value;
            SpeakerSteamIdWriter?.Invoke(value);
        }
    }

    public string SpeakerName
    {
        get => SpeakerNameReader?.Invoke() ?? _speakerName;
        set
        {
            _speakerName = value;
            SpeakerNameWriter?.Invoke(value);
        }
    }

    private readonly ILogger _logger;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly ToolManager _tools;

    private readonly Dictionary<string, SoundPlayerSession> _sessions = [];
    private readonly List<SoundPlayback> _queue = [];
    private readonly List<SoundPlayback> _pendingFinish = [];

    private SoundPlayback? _current;
    private long _nextPlaybackId;

    /// <summary>
    /// Highest section number sent before the counter starts over.
    /// <para>
    /// The field on the wire is a uint32, but this stops at int.MaxValue: a client that reads it
    /// into a signed int would see everything above that as negative, which is the one thing this
    /// counter must never do. Two other reasons to name a ceiling rather than let the type wrap —
    /// the build has CheckForOverflowUnderflow on, so unchecked rollover would throw instead, and a
    /// bound that is written down can be reasoned about.
    /// </para>
    /// <para>
    /// One section per 20 ms frame puts the wrap about 497 days of *continuous* audio away, and the
    /// counter only advances while something is actually playing. The wrap is the only moment the
    /// number goes backwards, so it has to stay far out of reach — see <see cref="_voiceSection"/>.
    /// </para>
    /// </summary>
    private const uint MaxVoiceSection = int.MaxValue;

    /// <summary>
    /// Section number stamped on every voice packet, counted per speaker rather than per playback,
    /// and increasing for as long as the server is up.
    /// <para>
    /// The client tracks this per talker to order one run of voice, and our speaker's identity — the
    /// entity index or player slot, plus the xuid — is the same for every playback. Counting it per
    /// playback made the number jump backwards whenever one playback followed another, and the
    /// client discarded the newcomer as stale until its own state timed out. Back-to-back playbacks
    /// therefore came out audible, silent, audible, silent: the silence of the dropped one was what
    /// let the client accept the one after it (reported 2026-08-22 on a run of short TTS lines).
    /// </para>
    /// <para>
    /// So: never reset it per playback. It cycles only at <see cref="MaxVoiceSection"/>, which is
    /// deliberately far enough away that no session reaches it.
    /// </para>
    /// </summary>
    private uint _voiceSection;

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

    public ISoundPlaybackInfo? CurrentPlayback => _current;

    public IReadOnlyList<ISoundPlaybackInfo> Queue => [.. _queue];

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

    /// <summary>
    /// Resolves the clients a session-level setter applies to. Null means every connected client;
    /// an empty sequence means none, so a filter that matched nobody cannot mute the whole server.
    /// </summary>
    internal int[] SlotsOf(IEnumerable<IGameClient>? clients)
        => clients is null
            ? [.. _clients.GetGameClientList(true).Where(c => !c.IsFakeClient && !c.IsHltv).Select(SlotOf)]
            : [.. clients.Select(SlotOf)];

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

    /// <summary>
    /// The volumes the encode worker must produce for a playback, read from its own session.
    /// Falls back to unmodified audio when the session is gone, which should not happen while one of
    /// its playbacks is still on air.
    /// </summary>
    internal float[] GetVolumeBuckets(string ownerName)
        => _sessions.TryGetValue(ownerName, out var session) ? session.GetVolumeBuckets() : [1f];

    /// <summary>The session a playback belongs to, or null once it has been dropped.</summary>
    private SoundPlayerSession? SessionOf(SoundPlayback playback)
        => _sessions.GetValueOrDefault(playback.OwnerName);

    // ---- pump (game thread, every 20 ms) ----

    internal void OnPump()
    {
        try
        {
            FlushPendingFinish();
            PumpCurrent();
            SyncSpeakerName();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sound player pump tick failed.");
        }
    }

    /// <summary>
    /// Hands the speaker the name of whatever is currently audible, or null when nothing is.
    /// Driven from the pump rather than from the start/finish paths because a playback can leave the
    /// air through half a dozen of them (completed, failed, stopped, interrupted, session shutdown);
    /// reconciling one piece of state every tick cannot miss one. The writer no-ops when the resolved
    /// name has not actually changed, so this stays a comparison in the common case.
    /// </summary>
    private void SyncSpeakerName()
        => SpeakerNameOverrideWriter?.Invoke(
            _current is { StartedFired: true } current ? current.Options.SpeakerName : null);

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
        // Explicit rather than letting the type roll over: CheckForOverflowUnderflow is on, so an
        // unchecked wrap would throw in the middle of the pump.
        _voiceSection = _voiceSection >= MaxVoiceSection ? 1 : _voiceSection + 1;

        // Hearing and volume are the owning session's, so muting one plugin never silences another.
        var session = SessionOf(playback);

        List<(IGameClient Client, float Volume)>? recipients = null;
        foreach (var client in _clients.GetGameClientList(true))
        {
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            var slot = SlotOf(client);
            if (session is not null && !session.HearsSlot(slot))
            {
                continue;
            }

            if (!Matches(playback, client))
            {
                continue;
            }

            var volume = session?.VolumeOfSlot(slot) ?? 1f;
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
            SectionNumber = _voiceSection,
            NumPackets = packet.NumPackets,
            SequenceBytes = 0,
            VoiceLevel = 0f,
            VoiceData = ByteString.CopyFrom(packet.Data),
        };
        foreach (var offset in packet.Offsets)
        {
            audio.PacketOffsets.Add(offset);
        }

        var message = new CSVCMsg_VoiceData { Audio = audio };

        if (UseEntityVoice)
        {
            // No player stands behind this index, which is the whole point: nothing occupies a slot.
            message.Entity = SpeakerEntity;
            message.Xuid = SpeakerSteamId;
        }
        else
        {
            message.ClientDeprecated = SpeakerSlot;
            message.Xuid = SpeakerXuid;
        }

        return message;
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

    internal static int SlotOf(IGameClient client)
        => (int)client.Slot.AsPrimitive();

    private void LogListenerError(Exception ex)
        => _logger.LogError(ex, "A playback event listener threw.");

    /// <summary>Called from the decode worker when Loop was asked for on a source that cannot rewind.</summary>
    internal void WarnLoopUnsupported(long playbackId, string ownerName)
        => _logger.LogWarning(
            "Playback #{Id} ({Owner}) asked to loop, but its source cannot seek (a streamed URL or a "
            + "live source). It will play once. Set PlayOptions.DownloadFirst for a URL you need to loop.",
            playbackId, ownerName);

    // ---- IClientListener ----

    public int ListenerVersion => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    /// <summary>Raised on the game thread when a client connects or is put in server (used by SpeakerManager).</summary>
    internal event Action<IGameClient>? ClientConnected;

    public void OnClientConnected(IGameClient client)
        => ClientConnected?.Invoke(client);

    public void OnClientPutInServer(IGameClient client)
        => ClientConnected?.Invoke(client);

    /// <summary>Raised on the game thread when any client disconnects (used by SpeakerManager).</summary>
    internal event Action<IGameClient>? ClientDisconnected;

    public void OnClientDisconnected(IGameClient client, Sharp.Shared.Enums.NetworkDisconnectionReason reason)
    {
        // Slots are reused, so a stale entry would apply to whoever connects next.
        var slot = SlotOf(client);
        foreach (var session in _sessions.Values)
        {
            session.ForgetSlot(slot);
        }

        ClientDisconnected?.Invoke(client);
    }

    // ---- lifecycle ----

    internal void Shutdown()
        => StopAll();
}
