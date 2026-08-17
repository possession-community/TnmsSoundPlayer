# 音声ソース API

音声をプレイヤーに取り込む部分である。
`ISoundPlayerSession.PlayFile` と `PlayUrl` はこれらのサービスの薄いラッパーなので、直接使う必要があるのは、再生前にストリームを手元に置きたい場合と、プレイヤーが開き方を知らない音声を流し込みたい場合に限られる。

---

## PCM フォーマット

プレイヤー内部はすべて **48 kHz、モノラル、符号付き 16 bit リトルエンディアン**で統一されている。
サービス側はこの形式にデコードして返す。
独自の `IPcmAudioStream` を実装する場合は、この形式で出力する。

```csharp
public static class PcmAudioFormat
{
    public const int SampleRate     = 48000;
    public const int Channels       = 1;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = 96000;
    public const int FrameBytes     = 1920;  // 20 ms の Opus フレーム 1 つ分（960 サンプル）

    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);

    public static int      GetByteCount(TimeSpan duration);
    public static TimeSpan GetDuration(long byteCount);
}
```

モノラルであるのは、あとから緩められる制限ではない。
ステレオの Opus パケットは実機で検証済みで、クライアントは受理するものの再生時にモノラルへダウンミックスする。
つまり 2 チャンネル目は帯域の無駄になる。

---

## IAudioFileService

ローカルのソースを ffmpeg 経由で開く。
ステートレスであり、再生位置は返されたストリームが持つ。
そのため同じソースを同時に複数回開いてもよい。

| メソッド | 戻り値 | 説明 |
|---|---|---|
| `OpenFileAsync(string path, CancellationToken)` | `Task<IPcmAudioStream>` | ディスク上の音声ファイルを開く |
| `OpenBufferAsync(ReadOnlyMemory<byte> encodedData, CancellationToken)` | `Task<IPcmAudioStream>` | メモリ上のエンコード済みバッファを開く。ffmpeg がデコードできる形式なら何でもよい |

どちらもソースを開けない場合は `SoundPlayerException` で fault する。
ここが `ISoundPlayerSession.PlayFile` との違いである。
`PlayFile` は同じ状況を例外ではなく `ISoundPlayback.Error` で伝える。

---

## INetworkAudioService

ネットワーク上のソースを開く。
yt-dlp が URL を解決し、ffmpeg がデコードする。

| メソッド | 戻り値 | 説明 |
|---|---|---|
| `OpenUrlAsync(string url, CancellationToken)` | `Task<IPcmAudioStream>` | yt-dlp が対応する URL を PCM ストリームとして開く |
| `GetMetadataAsync(string url, CancellationToken)` | `Task<AudioMetadata>` | メディア本体をダウンロードせずメタデータを取得する（`yt-dlp -J`） |

URL のストリームはシークできない。
`CanSeek` は `false`、`Duration` は `null` になり、`ISoundPlayback.Seek` と `PlayOptions.Loop` は使えない。

この API で本当に非同期なのはこの 2 つだけであり、継続はワーカースレッドで走る。
そこから `IGameClient`、エンティティ、その他 ModSharp が管理するものに触れてはいけない。
いずれもゲームスレッド専用である。

### AudioMetadata

`sealed record AudioMetadata(string? Title, TimeSpan? Duration, string? Uploader)`

すべてのフィールドが nullable である。
yt-dlp がすべてのサイトについて全項目を返すとは限らないためである。

---

## IPcmAudioStream

プル型の PCM ストリームである。
プレイヤー自身が開けないソースを流し込みたいときに実装する。
音声合成、手続き的に生成した音声、既にあるデコーダなどが該当する。

| メンバー | 型 | 説明 |
|---|---|---|
| `Read(Span<byte> destination)` | `int` | デコード済み PCM を `destination` に書き込み、バイト数を返す。`0` はストリーム終端を意味する。要求より少ないバイト数を返してもよい（ネットワークソースのバッファリング中など） |
| `CanSeek` | `bool` | `Seek` に対応しているか |
| `Duration` | `TimeSpan?` | 総再生時間。不明なら `null` |
| `Position` | `TimeSpan` | 現在位置 |
| `Seek(TimeSpan)` | `void` | シークする。`CanSeek` が `false` なら `NotSupportedException` を投げる |

ストリームのインスタンスは使い捨てである。
`ISoundPlayerSession.Play` に渡すと**所有権が移り**、再生が終了状態に到達した時点で再生側が Dispose する。
自分で Dispose してはいけないし、2 回目の再生に使い回すこともできない。

`Read` はゲームスレッドではなくワーカースレッドから呼ばれる。
早すぎる `0` は再生を `Completed` として終わらせてしまうので、データ待ちのあいだはブロックするか、短いバイト数を返す。

### PcmAudioStreamExtensions

| メソッド | 説明 |
|---|---|
| `ReadSeconds(this IPcmAudioStream, float seconds, Span<byte> destination)` | 最大 `seconds` 秒ぶんを読む。書き込み先の長さでクランプされる |

### 最小の独自ソース

```csharp
public sealed class SilenceStream(TimeSpan length) : IPcmAudioStream
{
    private int _offset;
    private readonly int _total = PcmAudioFormat.GetByteCount(length);

    public bool CanSeek => true;
    public TimeSpan? Duration => PcmAudioFormat.GetDuration(_total);
    public TimeSpan Position => PcmAudioFormat.GetDuration(_offset);

    public int Read(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _total - _offset);
        if (count <= 0)
        {
            return 0;
        }

        destination[..count].Clear();
        _offset += count;
        return count;
    }

    public void Seek(TimeSpan position)
        => _offset = Math.Clamp(PcmAudioFormat.GetByteCount(position), 0, _total);

    public void Dispose() { }
}

// 所有権は再生側へ移る。終了時に再生側が Dispose する。
session.Play(new SilenceStream(TimeSpan.FromSeconds(3)));
```

---

## SoundPlayerException

`PlaybackErrorReason Reason` プロパティを持つ `sealed class SoundPlayerException : Exception` である。

`IAudioFileService` と `INetworkAudioService` がソースを開けなかったときに投げる。
セッション経由で始めた再生がこれを投げることはない。
同じ理由を [`ISoundPlayback.Error`](playback.md#playbackerror) で伝える。

---

## 外部ツール

ffmpeg と yt-dlp はモジュール起動時に、モジュールの `tools` ディレクトリ、`PATH`、公式リリースからのダウンロードの順で解決される。
キャッシュは `modules/TnmsSoundPlayer/tools/cache/` の中に閉じ込めてあり、ユーザープロファイルを汚さない。

yt-dlp は加えて JavaScript ランタイム（deno。同じ仕組みでダウンロードされる）を必要とする。
YouTube の nsig チャレンジを解くためである。
これが無いと音声のダウンロードは HTTP 403 で失敗する。

可用性は [`ITnmsSoundPlayer.Diagnostics`](playback.md#soundplayerdiagnostics) で確認できる。
