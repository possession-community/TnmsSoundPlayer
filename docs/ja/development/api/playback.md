# 再生 API

音を鳴らし、制御し、どう終わったかを知るための API である。
`ITnmsSoundPlayer.CreateSession` から取得する。

---

## キューのモデル

サーバー全体で同時に鳴る音は一つだけである。
すべてのセッションが一本のグローバル優先度キューを共有する。

- `Priority` が高いものから再生される。同じ優先度なら到着順になる。
- 何かが再生中のときの挙動は `WhenBusy` で決まる。
- 一時停止中の再生もスロットを保持するため、キューは進まない。
- ループ再生は自分では終わらないため、その後ろに積まれたものは待ち続ける。これは仕様である。`Loop` は `Interrupt` か明示的な `Stop` と組み合わせて使う。

### 状態遷移

```
Queued --> Playing <--> Paused --> Completed
   |          |                 \-> Stopped
   |          \-------------------> Failed
   \-> Rejected   (最初から終了状態)
```

---

## ITnmsSoundPlayer

エントリポイント。
`ISharpModuleManager` から `ITnmsSoundPlayer.Identity`（`"TnmsSoundPlayer"`）で解決する。
別途明記されているものを除き、ゲームスレッドから呼ぶ。

| メンバー | 型 | 説明 |
|---|---|---|
| `Identity` | `const string` | モジュール解決に使うインターフェース識別子 |
| `CreateSession(string ownerName)` | `ISoundPlayerSession` | `ownerName` のセッションを作成または取得する。冪等 |
| `CurrentPlayback` | `ISoundPlaybackInfo?` | 再生中のもの。アイドル時は `null`。読み取り専用のビュー |
| `Queue` | `IReadOnlyList<ISoundPlaybackInfo>` | 待機中の再生を再生順に並べたスナップショット。`CurrentPlayback` は含まない。読み取り専用のビュー |
| `StopAll()` | `void` | 全セッションの再生を停止し、キューを空にする |
| `SpeakerSteamId` | `ulong` | ボットモードではスピーカーボットが偽装する SteamID64（`0` で偽装しない）。エンティティモードでは音声ストリームのキー。[スピーカーの識別情報](#スピーカーの識別情報)を参照 |
| `SpeakerName` | `string` | 何も再生していないときにスピーカーが表示する名前。空文字を入れると `TnmsSpeaker` に戻る。ボットモードのみ有効。[スピーカーの識別情報](#スピーカーの識別情報)を参照 |
| `FileService` | `IAudioFileService` | [音声ソース](sources.md)を参照 |
| `NetworkService` | `INetworkAudioService` | [音声ソース](sources.md)を参照 |
| `Diagnostics` | `SoundPlayerDiagnostics` | 実行時の状態のスナップショット。どのスレッドから読んでも安全 |

### スピーカーの識別情報

ボイスパケットには必ず「誰が喋っているか」を載せる必要がある。
その答え方が 2 通りあり、`tnms_sound_speaker_mode` ConVar で決まる。
この ConVar と `tnms_sound_speaker_entity` はロード時とマップ開始時に読まれるため、マップ中に変更した場合は次のマップから反映される。
モードの切り替えはボットの生成や退出を伴うので、影響が最も小さいマップの切れ目で行う。

| | `0` — ボット | `1` — エンティティ（既定） |
|---|---|---|
| 消費するプレイヤースロット | 64 枠のうち 1 つ | **なし** |
| スコアボードの行 | あり | なし |
| `SpeakerName` | その行に表示される | 効果を持たない（後述） |
| クライアント側ミュート | 効く | 効かない。サーバー側でミュートする |

**ボットモード**は音声をプレイヤースロットに紐付けるので、そのスロットを誰かで埋めるためにボットを 1 体置く必要がある。
このボットをボットではなく実プレイヤーに見せるにはコントローラに実在の SteamID64 が要り、`SpeakerSteamId` はそれを設定する。
ソースコードには ID を埋め込んでいない。実在のアカウントを指す値だからである。
設定するまでスピーカー自体は動作するが、スコアボードではボット扱いになる。

```csharp
// 自分が管理しているアカウントを使う。
_player.SpeakerSteamId = 7656119XXXXXXXXXX;
```

値は即座に反映され、以後作成されるボットにも適用される。
そのため起動時に一度設定すればよい。
`0` に戻すと偽装は解除される。

識別情報のもう半分がスコアボードに出る名前で、これは `SpeakerName` が決める。
何も再生していない間ずっと表示される平常時の名前である。

```csharp
_player.SpeakerName = "Jukebox";
```

個々の再生は [`PlayOptions.SpeakerName`](#playoptions) でこの名前を一時的に借りられる。
再生を要求した人をクレジットしたい場合に使う。
その再生が終わればどう終わったかによらず平常時の名前に戻る。

**エンティティモード**は音声をエンティティインデックスに紐付ける。
そのインデックスの先に何も存在していなくてよいため、ボットは作られず、スロットも消費しない。
これがこのモードを使う理由で、64 スロットのサーバーが 64 枠すべてをプレイヤーに使えるようになる。
インデックスは `tnms_sound_speaker_entity` で決まる。
エンティティインデックスはエンジンが自動採番するため予約できないので、既定値は有効範囲（`0..16383`）の上端寄りに置いてある。
プレイヤー（`1..maxplayers`）からも、マップが下から順に確保していくインデックスからも十分離れた位置である。
変更するのは衝突を避けるときだけにすること。
このインデックスに実体があると音がそのエンティティの位置に引っ張られ、プレイヤーのインデックスを使うとそのプレイヤーが喋っているように見える。

引き換えにスコアボードの行がなくなる。
`SpeakerName` と `PlayOptions.SpeakerName` は値としては保持されるが、どこにも描画されない。
再生中の曲名を出したい場合は自前の HUD やチャットで表示する。
`SpeakerSteamId` の役割はこのモードでは狭くなる。
クライアントは xuid ごとに音声ストリームを 1 本作るので、安定した非ゼロの値でありさえすればよく、モジュールが既定値を用意している。
プレイヤーが自分のクライアントからスピーカーをミュートすることもできなくなるので、`SetHearing` や `SetPlayerVolume` を呼ぶコマンドを用意すること。
どちらも受信者をサーバー側で除外するため、パケット自体が送られない。

---

## ISoundPlayerSession

プラグインごとのハンドル。
ここから始めた再生はオーナーに紐づき、そのセッションのキュー上限に数えられる。

| メンバー | 型 | 説明 |
|---|---|---|
| `OwnerName` | `string` | `CreateSession` に渡した名前 |
| `Play(IPcmAudioStream, PlayOptions?, ISoundPlaybackCallback?)` | `ISoundPlayback` | PCM ストリームをキューに積む。**ストリームの所有権は再生側へ移り**、どの終了状態でも再生側が Dispose する |
| `PlayFile(string path, PlayOptions?, ISoundPlaybackCallback?)` | `ISoundPlayback` | `FileService` でパスを開いて再生する |
| `PlayUrl(string url, PlayOptions?, ISoundPlaybackCallback?)` | `ISoundPlayback` | `NetworkService` で URL を開いて再生する |
| `OwnPlaybacks` | `IReadOnlyList<ISoundPlayback>` | このセッションの再生中・待機中のスナップショット |
| `StopAll()` | `void` | このセッションの再生だけを停止しキューから外す |
| `SetHearing(bool, IEnumerable<IGameClient>?)` | `void` | **このセッションの**音声を対象クライアントに対して on/off する。clients が `null` なら接続中の全員 |
| `GetHearing(IGameClient)` | `bool` | そのクライアントがこのセッションの音を聞くか |
| `DefaultHearing` | `bool` | 以後接続してくるクライアントに適用される受聴状態。接続済みには影響しない |
| `SetPlayerVolume(float, IEnumerable<IGameClient>?)` | `void` | このセッションの音声に対する音量倍率。`0.0` でミュート、`1.0` で無加工 |
| `GetPlayerVolume(IGameClient)` | `float` | そのクライアントのこのセッションに対する倍率 |

`Play*` はいずれもメディア側の問題では例外を投げない。
開けなかったソースは、理由を `Error` に持ったまま `Failed` に到達する再生として返る。

### 受聴と音量はセッション単位

どちらも再生を所有するセッションに紐づく。
ジュークボックスをミュートしたプレイヤーが、別プラグインのラウンド開始音は聞こえたままでいられる。
サーバー全体に効く同等の API は用意していない。
意図的にグローバルなのは `ITnmsSoundPlayer.StopAll()` だけである。
管理者がサーバーを黙らせたいときに欲しいのは「今鳴っている音を止めること」であって「以後の音を抑制すること」ではないからである。

誰にチャンクが届くかは3つのフィルタで決まり、**すべてを通過する必要がある**。

| フィルタ | 決めるのは | 答える問い |
|---|---|---|
| `PlayOptions.Recipients` | プラグイン | この音は誰向けか |
| セッションの受聴 | プレイヤー | このプラグインの音を聞きたいか |
| `PlayOptions.Volume` × セッションの音量 | 両方 | この聞き手にどれくらいの音量で |

`clients: null` は接続中の全員を指し、**空のシーケンスは誰も指さない**。
この非対称は意図的である。
`SetHearing(false, clients.Where(...))` のフィルタがたまたま0件だったときに、サーバー全員をミュートしてしまわないようにするため。

---

## ISoundPlaybackInfo

誰が開始したかによらず参照できる、読み取り専用のビュー。
`ITnmsSoundPlayer.CurrentPlayback` と `Queue` が返すのはこちらである。
何が鳴っているかを表示はできるが、止めることはできない。
持っているのは `Id`、`OwnerName`、`State`、`Position`、`Duration`、`Volume`（getのみ）、`Error`。

分けてある理由は、「自分の音楽を止める」コマンド同士が衝突するからである。
2つのプラグインがどちらも `CurrentPlayback.Stop()` を呼べば、互いの音を切ってしまう。
操作できるのは所有している再生だけで、`Play` の戻り値か `ISoundPlayerSession.OwnPlaybacks` から得る。
ただしこれは境界ではなく手すりである。実体は両方のインターフェースを実装しているので、キャストすれば通り抜けられる。

---

## ISoundPlayback

`ISoundPlaybackInfo` に制御を加えたもので、その音を開始したセッションだけが受け取る。
制御メソッドはゲームスレッドから呼ぶ。プロパティの読み取りはスレッドセーフである。

| メンバー | 型 | 説明 |
|---|---|---|
| `Id` | `long` | 単調増加する一意の ID |
| `OwnerName` | `string` | この再生を開始したセッションのオーナー |
| `State` | `PlaybackState` | 現在の状態 |
| `Position` | `TimeSpan` | ソース内の現在位置 |
| `Duration` | `TimeSpan?` | 総再生時間。不明な場合は `null`（ライブやネットワークのソース） |
| `Volume` | `float` | この再生のゲイン。再生中も変更できる |
| `Error` | `PlaybackError?` | 失敗の詳細。`Failed` と `Rejected`、および中断された `Stopped` で非 null になる |
| `Stop()` | `void` | 再生中なら停止し、待機中ならキューから外す |
| `Pause()` | `void` | 一時停止する。スロットは保持したままになる |
| `Resume()` | `void` | 一時停止を解除する |
| `Seek(TimeSpan)` | `void` | ソース内をシークする。シーク不可のソースでは `NotSupportedException` を投げる |
| `Completion` | `Task` | 終了状態で完了する。メディアのエラーで fault することはない |

開始と終了の通知を受け取るには、再生を開始するときに `ISoundPlaybackCallback` を渡す。

---

## ISoundPlaybackCallback

再生 1 件ぶんの通知を受け取るインターフェースである。
`Play`、`PlayFile`、`PlayUrl` に渡す。
どちらのメソッドにもデフォルト実装があるため、必要なものだけ実装すればよい。

| メソッド | 説明 |
|---|---|
| `OnStarted(ISoundPlayback)` | 実際に音が出始めた |
| `OnFinished(ISoundPlayback)` | 終了状態に到達した時点でちょうど一度だけ呼ばれる。どの終了かは `State` と `Error` で判別する |

```csharp
private sealed class AnnounceTrack(IGameClient client) : ISoundPlaybackCallback
{
    public void OnFinished(ISoundPlayback playback)
        => client.ConsolePrint($"finished: {playback.State}\n");
}

session.PlayUrl(url, null, new AnnounceTrack(client));
```

どちらのメソッドもゲームスレッド上で実行される。

あとから購読するのではなく渡す形にしていることで、2 つの問題が構造的に消えている。
第一に、即座に拒否された再生でもコールバックは必ず呼ばれる。あとから購読する形では取りこぼしうる。
第二に、購読解除が不要である。再生が終了状態に到達した時点でプレイヤーが参照を手放すため、`IGameClient` を保持したコールバックが再生より長生きすることはない。
ハンドルを長命なフィールドに保持し続けた場合でも同じである。

---

## PlayOptions

`Play*` に渡すレコード。
すべてのプロパティに既定値があるので、`new PlayOptions { ... }` では必要なものだけ指定すればよい。

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `Recipients` | `SoundRecipients` | `All` | 誰に聞かせるか |
| `Volume` | `float` | `1.0f` | この再生の初期ゲイン |
| `StartAt` | `TimeSpan` | `0` | ソース内のどこから再生を始めるか |
| `Loop` | `bool` | `false` | 停止または中断されるまで繰り返す。シーク可能なソースでのみ機能する |
| `Priority` | `int` | `0` | キューの優先度。高いものから再生される |
| `WhenBusy` | `QueueBehavior` | `Enqueue` | 何かが再生中だったときの挙動 |
| `SpeakerName` | `string?` | `null` | この再生が鳴っている間だけスピーカーの名前を差し替え、終了時に `ITnmsSoundPlayer.SpeakerName` に戻す。`null` なら名前に触れない |
| `DownloadFirst` | `bool` | `false` | `PlayUrl` のみ有効。ソースを全部ダウンロードしてから再生する。シーク・ループ・再生時間が使えるようになる代わりに、ダウンロード分だけ再生開始が遅れる |

名前が切り替わるのは最初の音声パケットが出た時点であり、キューに積まれた時点ではない。
そのためキューで待っている間や、ソースを開けずに失敗した再生が名前を書き換えることはない。

`DownloadFirst` は効果音とジュークボックスの分かれ目である。
既定のストリーミングでは URL は1秒程度で鳴り始めるが、シークもループもできず再生時間も不明になる。
yt-dlp の出力を ffmpeg に直接パイプしていて、パイプは巻き戻せないからである。
ダウンロードする場合は一旦テンポラリファイルに落ちる。
そうなれば普通のローカルファイルなので全部使えるが、ダウンロードが終わるまで何も聞こえない。
テンポラリファイルは再生終了時に削除される。

---

## SoundRecipients

再生の宛先を表す。
ファクトリメンバーで生成する。
入れ子のレコードは実装側がパターンマッチするために存在するもので、直接生成するためのものではない。

| メンバー | 説明 |
|---|---|
| `All` | 接続中の全クライアント。再生中も評価されるため、途中参加者も対象になる |
| `Single(IGameClient)` | 単一のクライアント |
| `Of(IEnumerable<IGameClient>)` | 呼び出し時点で固定した集合。スロットで追跡するため、切断した者は受信しなくなるが、後から追加されることはない |
| `Where(Func<IGameClient, bool>)` | 再生中、接続中の全クライアントに対して繰り返し評価される述語 |

`Where` の述語は再生中に何度も呼ばれるため、軽量かつ副作用のないものにする。

---

## PlaybackState

| 値 | 説明 |
|---|---|
| `Queued` | グローバルキューで待機中 |
| `Playing` | 再生中 |
| `Paused` | 一時停止中。再生スロットは保持したまま |
| `Completed` | ソースを最後まで再生した |
| `Stopped` | 途中で停止した。明示的な停止、`StopAll`、中断のいずれか。区別するには `Error` を見る |
| `Failed` | 開始できなかった、またはメディアのエラーで中断した。`Error` を参照 |
| `Rejected` | キューが受け付けなかった（上限到達、または `RejectIfBusy` で塞がっていた）。`Error` を参照 |

---

## QueueBehavior

| 値 | 説明 |
|---|---|
| `Enqueue` | グローバル優先度キューで待つ |
| `Interrupt` | 再生中のものを止めて即座に再生する。中断された側は `Error.Reason` が `Interrupted` になり、再開されない |
| `RejectIfBusy` | 待たずに `Rejected` 状態の再生を返す |

---

## PlaybackError

`sealed record PlaybackError(PlaybackErrorReason Reason, string Message)`

| `PlaybackErrorReason` | 説明 |
|---|---|
| `SourceNotFound` | ファイルまたはバッファが見つからない、開けない |
| `DecodeFailed` | ffmpeg がソースをデコードできなかった |
| `FfmpegNotFound` | ffmpeg の実行ファイルが利用できない |
| `YtdlpNotFound` | yt-dlp の実行ファイルが利用できない |
| `UrlResolveFailed` | yt-dlp が URL からメディアストリームを解決できなかった |
| `QueueLimitReached` | グローバルまたはセッションのキュー上限に達した。あるいは `RejectIfBusy` で塞がっていた |
| `Interrupted` | `Interrupt` の再生に中断された。`Failed` ではなく `Stopped` の再生に付く |
| `Cancelled` | 開始前に取り消された（モジュールのシャットダウンなど） |

---

## SoundPlayerDiagnostics

`sealed record SoundPlayerDiagnostics(bool FfmpegAvailable, string? FfmpegPath, bool YtdlpAvailable, string? YtdlpPath, int QueueLength, int ActiveSessionCount)`

スナップショットであり、どのスレッドから読んでも安全である。
ffmpeg と yt-dlp はモジュール起動時に、モジュールの `tools` ディレクトリ、`PATH`、自動ダウンロードの順で解決される。
したがってここが `false` なら、通常は自動ダウンロードに失敗している。
