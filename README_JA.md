# TnmsSoundPlayer

任意の音声を Counter-Strike 2 のボイスチャットに流す [ModSharp](https://github.com/Kxnrl/modsharp-public) モジュールである。
ローカルファイル、メモリ上のバッファ、yt-dlp が解決できる URL、プラグインが自前で生成した PCM ストリームを再生できる。

## 翻訳された README

[English](README.md)

## 機能

- ローカルファイル、メモリ上のバッファ、yt-dlp 対応 URL（YouTube など）の再生
- ハンドルベースの API。停止、一時停止、再開、シーク、再生中の音量変更、終了イベント
- サーバー全体で一本の優先度キュー。プラグイン同士が意図せず音を潰し合うことがない
- 宛先の指定。全員、単一クライアント、固定した集合、再生中に評価される述語
- クライアント単位の受聴 on/off とサーバー側音量倍率。プラグインごとに独立している
- `IPcmAudioStream` による独自ソース。音声合成や手続き的に生成した音声を流せる

## 動作環境

- ModSharp を導入した Counter-Strike 2 専用サーバー
- ModSharp.Sharp.Shared 2.1.137 以降
- .NET 10 ランタイム（ModSharp が提供）
- 初回起動時、外部ツールのダウンロードのための HTTPS 通信

## 導入

[最新リリース](https://github.com/possession-community/TnmsSoundPlayer/releases/latest)から
`TnmsSoundPlayer-<platform>.zip` を取得する。
中身は `modules\` と `shared\` のツリーなので、そのまま `%MOD_SHARP_DIR%` にマージすればよい。
手で配置する場合は次のとおり。

1. `TnmsSoundPlayer.dll`、`TnmsSoundPlayer.deps.json`、`Concentus.dll` を
   `%MOD_SHARP_DIR%\modules\TnmsSoundPlayer\` に配置する。
2. `TnmsSoundPlayer.Shared.dll` を `%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\` に配置する。
   Shared アセンブリは `shared\` にのみ置く。`modules\` に置いてはいけない。
3. サーバーを起動する。初回起動時に ffmpeg、ffprobe、yt-dlp、deno が
   `modules\TnmsSoundPlayer\tools\` へダウンロードされる。

> モジュールディレクトリ配下の `reload\` は、**すでにロード済みの**モジュールにしか効かない。
> 初回配置はモジュール直下に置く必要がある。そうしないとロードされない。

### 外部ツール

各ツールは、モジュールの `tools` ディレクトリ、`PATH`、公式リリースからのダウンロードの順で解決される。
キャッシュは `modules\TnmsSoundPlayer\tools\cache\` 内に閉じ込めてあるため、ユーザープロファイルには何も書き込まれない。

| ツール | 用途 |
|---|---|
| ffmpeg | すべてのソースを PCM にデコードする |
| ffprobe | ローカルファイルの再生時間を読む。ffmpeg のアーカイブに同梱されている。無い場合、ファイル再生の再生時間は不明のままになる |
| yt-dlp | URL からメディアストリームを解決する。`tools\` に置かれている場合は1日1回自動更新する。YouTube は数週間で古いビルドを弾くようになり、抽出は通るのにメディア本体の取得だけが HTTP 403 で失敗するようになるため |
| deno | yt-dlp が YouTube の nsig チャレンジを解くために必要な JavaScript ランタイム。無いと YouTube のダウンロードが HTTP 403 で失敗する |

## 再生の仕組み

音声は Opus にエンコードされ、`CSVCMsg_VoiceData` として送信される。
つまりプレイヤーのボイスとまったく同じ経路を通るため、クライアント側のボイス設定がそのまま効く。

## 制限

- **同時に鳴る音はサーバー全体で 1 つだけ。** 同時再生とミキシングは対象外である。優先度キューと `QueueBehavior.Interrupt` で代替する。
- **48 kHz モノラルのみ。** ステレオの Opus パケットは実機で検証済みで、クライアントがダウンミックスするため 2 チャンネル目は完全な無駄になる。
- **音質はボイス経路の上限に縛られる。** ビットレートは 128 kbps だが、クライアント側のボイス DSP が結果を整形するため、特に低音が弱く出る。
- **ストリーミングの URL ソースはシークできない。** yt-dlp を ffmpeg に直接パイプしているため、`ISoundPlayback.Seek` と `PlayOptions.Loop` が使えず、再生時間も不明になる。`PlayOptions.DownloadFirst` を指定すると先にダウンロードしてこの3つが揃うが、ダウンロードが終わるまで何も鳴らない。

## ドキュメント

| 分類 | リンク |
|---|---|
| API | [はじめに](docs/ja/development/USING_SOUNDPLAYER_API.md) / [再生](docs/ja/development/api/playback.md) / [音声ソース](docs/ja/development/api/sources.md) |

## ライセンス

AGPLv3。ModSharp 向けのリンク例外とデュアルライセンス例外が付く。
[LICENSE](LICENSE) を参照。

Copyright (c) 2026 faketuna
