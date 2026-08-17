# TnmsSoundPlayer API ガイド

TnmsSoundPlayer は、任意の音声を CS2 のボイスチャットに流すライブラリである。
ローカルファイル、メモリ上のバッファ、yt-dlp が解決できる URL、自前で生成した PCM ストリームを再生できる。
外部プラグインからは `TnmsSoundPlayer.Shared` アセンブリ経由で利用する。

サーバー全体で同時に鳴る音は**常に一つ**である。
すべてのプラグインの再生はサーバー全体で一本のキューを通るため、明示的に指定しない限り、再生要求が他のプラグインの音を止めることはない。

## 導入

### 1. TnmsSoundPlayer.Shared を参照する

Shared アセンブリは NuGet に公開していない。
TnmsSoundPlayer をビルドすると `%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\` に配置されるので、そこの DLL を参照する。
同じソリューションで両方をビルドするなら、プロジェクト参照でよい。

```xml
<ItemGroup>
    <ProjectReference Include="..\TnmsSoundPlayer.Shared\TnmsSoundPlayer.Shared.csproj"
                      Private="false" ExcludeAssets="runtime" />
</ItemGroup>
```

`Private="false"` と `ExcludeAssets="runtime"` は必須である。
ModSharp は Shared アセンブリを一度だけロードするため、自分のモジュールの隣に複製を置くと型の同一性が壊れる。

### 2. API のエントリポイントを取得する

`ISharpModuleManager` から `ITnmsSoundPlayer` を解決する。
TnmsSoundPlayer がインターフェースを登録するのは `PostInit` なので、自分の `Init` からは取得できない。
`OnAllModulesLoaded` で取得するか、次のように初回利用時に遅延解決する。

```csharp
private IModSharpModuleInterface<ITnmsSoundPlayer>? _playerInterface;

private ITnmsSoundPlayer? Player
    => (_playerInterface ??= _shared.GetSharpModuleManager()
        .GetOptionalSharpModuleInterface<ITnmsSoundPlayer>(ITnmsSoundPlayer.Identity))?.Instance;

public void OnAllModulesLoaded()
{
    if (Player is null)
    {
        _logger.LogWarning("TnmsSoundPlayer が見つかりません。サウンド機能は利用できません。");
    }
}
```

インスタンスではなく `IModSharpModuleInterface<T>` をキャッシュしている。
こうすると解決コストを一度で済ませつつ、TnmsSoundPlayer が自分より後にロードされる場合にも対応できる。

### 3. セッションを作る

再生はすべてセッション経由で行う。
セッションはプラグインごとのハンドルである。

```csharp
var session = Player!.CreateSession("MyPlugin");
```

`CreateSession` は冪等で、同じ名前で呼べば同じセッションが返る。
そのため保持せず、使う場所で都度呼んでもよい。
セッションはプラグインごとのキュー上限と `StopAll` の単位になるので、名前には自分のプラグインを識別できるものを使う。

### 4. スピーカーの識別情報を設定する

音声は、モジュールが観戦席に置いているボットの声として送出される。
このボットには自分が管理する SteamID64 を与える。
与えないとスコアボードでボット扱いになり、アバターも表示されない。

```csharp
Player!.SpeakerSteamId = 7656119XXXXXXXXXX;
```

実在のアカウントを指す値なので、ソースコードには埋め込んでいない。
以後作成されるボットにも適用されるため、起動時に一度設定すればよい。

### 5. ITnmsSoundPlayer の全体像

| メンバー | 型 | 用途 |
|---|---|---|
| `CreateSession(string)` | `ISoundPlayerSession` | 自プラグインのセッションを取得または作成する |
| `CurrentPlayback` | `ISoundPlayback?` | 再生中のもの。アイドル時は `null` |
| `Queue` | `IReadOnlyList<ISoundPlayback>` | 待機中の再生を再生順に並べたもの |
| `StopAll()` | `void` | 全セッションの再生を止める（管理用） |
| `SetHearing` / `GetHearing` | `void` / `bool` | クライアント単位の受聴の on/off |
| `DefaultHearing` | `bool` | 以後接続してくるクライアントに適用される受聴状態 |
| `SetPlayerVolume` / `GetPlayerVolume` | `void` / `float` | クライアント単位の音量倍率 |
| `SpeakerSteamId` | `ulong` | スピーカーボットが偽装する SteamID64。`0` で偽装しない |
| `FileService` | `IAudioFileService` | ローカルファイルやバッファを PCM として開く |
| `NetworkService` | `INetworkAudioService` | URL を PCM として開く。メタデータ取得も行う |
| `Diagnostics` | `SoundPlayerDiagnostics` | ツールの可用性とキューの統計 |

別途明記されているものを除き、すべてゲームスレッドから呼ぶ。

---

## よくある使い方

### URL を再生する

```csharp
var session = Player!.CreateSession("MyPlugin");
var playback = session.PlayUrl("https://www.youtube.com/watch?v=...");
```

`PlayUrl` と `PlayFile` はメディア側の問題では例外を投げない。
開けなかったソースは `Failed` に到達した再生として返り、理由は `Error` に入る。

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
var meta = await Player!.NetworkService.GetMetadataAsync(url);
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
Player!.SetHearing(client, false);      // このクライアントには一切聞こえなくなる
Player!.SetPlayerVolume(client, 0.5f);  // 音量を下げるだけ
```

どちらもクライアント単位のグローバル設定で、個々の再生とは独立している。
エンコード前にサーバー側で適用される。
新規接続してくるクライアントの初期値は `DefaultHearing` で決める。

### ツールが使える状態か確認する

```csharp
var d = Player!.Diagnostics;
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
| [再生](api/playback.md) | `ITnmsSoundPlayer`、`ISoundPlayerSession`、`ISoundPlayback`、`PlayOptions`、`SoundRecipients`、`PlaybackState`、`QueueBehavior`、`PlaybackError` |
| [音声ソース](api/sources.md) | `IAudioFileService`、`INetworkAudioService`、`IPcmAudioStream`、`PcmAudioFormat`、`AudioMetadata`、独自ソースの実装 |
