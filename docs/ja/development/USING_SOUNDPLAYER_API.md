# TnmsSoundPlayer API ガイド

TnmsSoundPlayer は、任意の音声を CS2 のボイスチャットに流すライブラリである。
ローカルファイル、メモリ上のバッファ、yt-dlp が解決できる URL、自前で生成した PCM ストリームを再生できる。
外部プラグインからは `TnmsSoundPlayer.Shared` アセンブリ経由で利用する。

サーバー全体で同時に鳴る音は**常に一つ**である。
すべてのプラグインの再生はサーバー全体で一本のキューを通るため、明示的に指定しない限り、再生要求が他のプラグインの音を止めることはない。

## 導入

### 1. TnmsSoundPlayer.Shared を参照する

Shared アセンブリは [`TnmsSoundPlayer.Shared`](https://www.nuget.org/packages/TnmsSoundPlayer.Shared) として NuGet に公開している。

```xml
<ItemGroup>
    <PackageReference Include="TnmsSoundPlayer.Shared" Version="0.0.1" ExcludeAssets="runtime" />
</ItemGroup>
```

同じソリューションで両方をビルドするなら、プロジェクト参照でもよい。

```xml
<ItemGroup>
    <ProjectReference Include="..\TnmsSoundPlayer.Shared\TnmsSoundPlayer.Shared.csproj"
                      Private="false" ExcludeAssets="runtime" />
</ItemGroup>
```

どちらの場合も `ExcludeAssets="runtime"`（プロジェクト参照なら加えて `Private="false"`）は必須である。
サーバーは Shared アセンブリを `shared\TnmsSoundPlayer.Shared\` から一度だけロードするため、自分のモジュールの隣に複製を置くと型の同一性が壊れる。

### 2. API のエントリポイントを取得する

`ISharpModuleManager` から `ITnmsSoundPlayer` を解決する。
TnmsSoundPlayer がインターフェースを登録するのは `PostInit` なので、自分の `Init` からは取得できない。
`OnAllModulesLoaded` で取得する。

```csharp
private ITnmsSoundPlayer _player = null!;

public void OnAllModulesLoaded()
    => _player = _shared.GetSharpModuleManager()
        .GetRequiredSharpModuleInterface<ITnmsSoundPlayer>(ITnmsSoundPlayer.Identity).Instance!;
```

`GetRequiredSharpModuleInterface` は TnmsSoundPlayer が入っていなければ例外を投げる。
音を鳴らすモジュールは TnmsSoundPlayer 無しでは何もできないのだから、これでよい。
黙って何もしないより、ロード時に落ちたほうが原因を追いやすい。

### 3. セッションを作る

再生はすべてセッション経由で行う。
セッションはプラグインごとのハンドルである。

```csharp
var session = _player.CreateSession("MyPlugin");
```

`CreateSession` は冪等で、同じ名前で呼べば同じセッションが返る。
そのため保持せず、使う場所で都度呼んでもよい。
セッションはプラグインごとのキュー上限と `StopAll` の単位になるので、名前には自分のプラグインを識別できるものを使う。

### 4. スピーカーの識別情報を設定する

音声は、モジュールが観戦席に置いているボットの声として送出される。
このボットには自分が管理する SteamID64 を与える。
与えないとスコアボードでボット扱いになり、アバターも表示されない。

```csharp
_player.SpeakerSteamId = 7656119XXXXXXXXXX;
```

実在のアカウントを指す値なので、ソースコードには埋め込んでいない。
以後作成されるボットにも適用されるため、起動時に一度設定すればよい。

何も再生していないときにそのスコアボード行に出る名前は `SpeakerName` で決める。

```csharp
_player.SpeakerName = "Jukebox";
```

### 5. ITnmsSoundPlayer の全体像

| メンバー | 型 | 用途 |
|---|---|---|
| `CreateSession(string)` | `ISoundPlayerSession` | 自プラグインのセッションを取得または作成する |
| `CurrentPlayback` | `ISoundPlaybackInfo?` | 再生中のもの。アイドル時は `null`。自分のものとは限らないので読み取り専用 |
| `Queue` | `IReadOnlyList<ISoundPlaybackInfo>` | 待機中の再生を再生順に並べたもの。読み取り専用 |
| `StopAll()` | `void` | 全セッションの再生を止める（管理用） |
| `SpeakerSteamId` | `ulong` | スピーカーボットが偽装する SteamID64。`0` で偽装しない |
| `SpeakerName` | `string` | 何も再生していないときにスピーカーが表示する名前 |
| `FileService` | `IAudioFileService` | ローカルファイルやバッファを PCM として開く |
| `NetworkService` | `INetworkAudioService` | URL を PCM として開く。メタデータ取得も行う |
| `Diagnostics` | `SoundPlayerDiagnostics` | ツールの可用性とキューの統計 |

別途明記されているものを除き、すべてゲームスレッドから呼ぶ。

---

## よくある使い方

### URL を再生する

```csharp
var session = _player.CreateSession("MyPlugin");
var playback = session.PlayUrl("https://www.youtube.com/watch?v=...");
```

`PlayUrl` と `PlayFile` はメディア側の問題では例外を投げない。
開けなかったソースは `Failed` に到達した再生として返り、理由は `Error` に入る。

URL はダウンロードしながら流すため、ほぼ即座に鳴り始める代わりにシークもループもできず、再生時間も不明になる。
それらが必要なら先に全部ダウンロードさせる。

```csharp
session.PlayUrl(url, new PlayOptions { DownloadFirst = true });
```

ダウンロードが終わるまで何も鳴らないので、既定にはしていない。

### 特定のプレイヤーにだけファイルを再生する

```csharp
var ct = _shared.GetClientManager().GetGameClients(true)
    .Where(c => c.GetPlayerController()?.Team == CStrikeTeam.CT);

session.PlayFile(@"sounds\alert.mp3", new PlayOptions
{
    Recipients = SoundRecipients.Of(ct),
    Volume = 0.6f,
});
```

`SoundRecipients.Of` は呼び出し時点の集合を固定する。
「そのとき生存しているプレイヤー全員」のように再生中も評価し直したい場合は `SoundRecipients.Where(...)` を使う。

### リクエストした人をスピーカー名に出す

```csharp
session.PlayUrl(url, new PlayOptions
{
    SpeakerName = $"SoundPlayer: by {client.Name}",
});
```

音が鳴っている間だけスピーカーがその名前になり、終われば `SpeakerName` に戻る。
名前が切り替わるのは実際に音声が出始めた時点なので、キュー待ちの再生や失敗した再生が名前を書き換えることはない。

### 再生中の音を中断して割り込む

```csharp
session.PlayFile(@"sounds\countdown.wav", new PlayOptions
{
    WhenBusy = QueueBehavior.Interrupt,
    Priority = 100,
});
```

中断された側は `PlaybackErrorReason.Interrupted` を伴って `Stopped` に到達し、**あとで再開されることはない**。
他人の音を止めるくらいなら自分の音を諦めたい場合は `QueueBehavior.RejectIfBusy` を使う。

### 結果を受け取る

`ISoundPlaybackCallback` を実装し、再生を開始するときに渡す。
`OnFinished` はどの終了状態でもちょうど一度だけ呼ばれるので、後始末はここで行う。
結果を区別するには `State` と `Error` を見る。

```csharp
private sealed class TrackReporter(ILogger logger) : ISoundPlaybackCallback
{
    public void OnFinished(ISoundPlayback playback)
    {
        switch (playback.State)
        {
            case PlaybackState.Completed:
                break;
            case PlaybackState.Stopped when playback.Error?.Reason == PlaybackErrorReason.Interrupted:
                logger.LogInformation("優先度の高い音に中断された");
                break;
            case PlaybackState.Failed:
            case PlaybackState.Rejected:
                logger.LogWarning("再生失敗: {Reason} {Message}",
                    playback.Error?.Reason, playback.Error?.Message);
                break;
        }
    }
}

session.PlayUrl(url, null, new TrackReporter(_logger));
```

どちらのメソッドにもデフォルト実装があるため、終了だけ扱うクラスは `OnFinished` だけ実装すればよい。

`await` を使いたい場合は `Completion` が同じ情報を `Task` として提供する。
メディアのエラーで fault することはないので、await 後に `State` と `Error` を見る点は上と同じである。

### 再生せずにメタデータだけ取得する

```csharp
var meta = await _player.NetworkService.GetMetadataAsync(url);
_logger.LogInformation("{Title} ({Duration}) by {Uploader}", meta.Title, meta.Duration, meta.Uploader);
```

継続はワーカースレッドで走る。
そこで `IGameClient` やエンティティに触れてはいけない。
ゲームスレッドに戻すか、上のようにログに出すだけにする。

### 再生中に制御する

```csharp
playback.Volume = 0.3f;   // 即座に反映される
playback.Pause();
playback.Resume();
playback.Seek(TimeSpan.FromSeconds(30));  // URL ソースでは NotSupportedException
playback.Stop();
```

一時停止中も再生スロットは占有したままなので、キューは進まない。

### プレイヤーをミュートする

```csharp
session.SetHearing(false, [client]);      // このクライアントには自分のプラグインの音が届かなくなる
session.SetPlayerVolume(0.5f, [client]);  // 音量を下げるだけ
```

どちらも**セッション単位**なので、自分のプラグインをミュートしても他のプラグインの音には影響しない。
ジュークボックスを切ったプレイヤーが、ラウンド開始音は聞こえたままでいられる。
既に鳴っている再生にも以後の再生にも効き、エンコード前にサーバー側で適用される。
新規接続してくるクライアントの初期値は `DefaultHearing` で決める。

クライアントを省略すると、接続中の全員が対象になる。

```csharp
session.SetHearing(false);   // 戻すまで、このプラグインの音は誰にも届かない
```

`null` と違って**空のリストは誰も指さない**。
フィルタがたまたま0件だったときに、うっかりサーバー全員をミュートすることがないようにしてある。

### ツールが使える状態か確認する

```csharp
var d = _player.Diagnostics;
if (!d.YtdlpAvailable)
{
    _logger.LogWarning("yt-dlp が無いため URL 再生は失敗する");
}
```

`Diagnostics` はスナップショットで、どのスレッドから読んでも安全である。
ffmpeg と yt-dlp はモジュール起動時に自動ダウンロードされるため、ここが `false` なら通常はダウンロードに失敗している。

---

## API リファレンス

| ページ | 内容 |
|---|---|
| [再生](api/playback.md) | `ITnmsSoundPlayer`、`ISoundPlayerSession`、`ISoundPlayback`、`ISoundPlaybackInfo`、`PlayOptions`、`SoundRecipients`、`PlaybackState`、`QueueBehavior`、`PlaybackError` |
| [音声ソース](api/sources.md) | `IAudioFileService`、`INetworkAudioService`、`IPcmAudioStream`、`PcmAudioFormat`、`AudioMetadata`、独自ソースの実装 |
