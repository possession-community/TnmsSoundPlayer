# TnmsSoundPlayer

任意の音声を Counter-Strike 2 のボイスチャットに流す [ModSharp](https://github.com/Kxnrl/modsharp-public) モジュールである。
ローカルファイル、メモリ上のバッファ、yt-dlp が解決できる URL、プラグインが自前で生成した PCM ストリームを再生できる。

音声は観戦席に常駐するボットの声として送出される。
このボットは実プレイヤーに見えるよう偽装してあるため、クライアントは通常のゲーム内 UI からミュートや音量調整ができる。

## 翻訳された README

[English](README.md)

## 機能

- ローカルファイル、メモリ上のバッファ、yt-dlp 対応 URL（YouTube など）の再生
- ハンドルベースの API。停止、一時停止、再開、シーク、再生中の音量変更、終了イベント
- サーバー全体で一本の優先度キュー。プラグイン同士が意図せず音を潰し合うことがない
- 宛先の指定。全員、単一クライアント、固定した集合、再生中に評価される述語
- クライアント単位の受聴 on/off とサーバー側音量倍率
- `IPcmAudioStream` による独自ソース。音声合成や手続き的に生成した音声を流せる
- ffmpeg、yt-dlp、deno は初回起動時に自動ダウンロードされる
- TnmsPluginFoundation に依存しない

## 動作環境

- ModSharp を導入した Counter-Strike 2 専用サーバー
- ModSharp.Sharp.Shared 2.1.137 以降
- .NET 10 ランタイム（ModSharp が提供）
- 初回起動時、外部ツールのダウンロードのための HTTPS 通信

## 導入

1. `TnmsSoundPlayer.dll`、`TnmsSoundPlayer.deps.json`、`Concentus.dll` を
   `%MOD_SHARP_DIR%\modules\TnmsSoundPlayer\` に配置する。
2. `TnmsSoundPlayer.Shared.dll` を `%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\` に配置する。
   Shared アセンブリは `shared\` にのみ置く。`modules\` に置いてはいけない。
3. サーバーを起動する。初回起動時に ffmpeg、yt-dlp、deno が
   `modules\TnmsSoundPlayer\tools\` へダウンロードされる。
4. `ITnmsSoundPlayer.SpeakerSteamId` に自分が管理する SteamID64 を設定する。
   プラグインから設定するか、`!sp_spk_steam <id>` を使う。
   ソースコードには ID を埋め込んでいない。設定しなくてもスピーカーは動作するが、
   スコアボードではボット扱いになり、アバターも表示されない。

> モジュールディレクトリ配下の `reload\` は、**すでにロード済みの**モジュールにしか効かない。
> 初回配置はモジュール直下に置く必要がある。そうしないとロードされない。

### 外部ツール

各ツールは、モジュールの `tools` ディレクトリ、`PATH`、公式リリースからのダウンロードの順で解決される。
キャッシュは `modules\TnmsSoundPlayer\tools\cache\` 内に閉じ込めてあるため、ユーザープロファイルには何も書き込まれない。

| ツール | 用途 |
|---|---|
| ffmpeg | すべてのソースを PCM にデコードする |
| yt-dlp | URL からメディアストリームを解決する |
| deno | yt-dlp が YouTube の nsig チャレンジを解くために必要な JavaScript ランタイム。無いと YouTube のダウンロードが HTTP 403 で失敗する |

## 再生の仕組み

音声は Opus にエンコードされ、`CSVCMsg_VoiceData` として送信される。
つまりプレイヤーのボイスとまったく同じ経路を通るため、クライアント側のボイス設定がそのまま効く。

送出元としてクライアントスロットが必要になるので、モジュールはサーバーにボットを 1 体常駐させる。

- ボットはゲーム自身のマネージャ（`bot_add`）経由で、最初の人間が参加した時点で要求される。無人のサーバーではボットを追加できないためである。
- 観戦席へ移動させ、そこに留める。プレイ中のチームへ配属しようとする動きは観戦席へ差し戻す。
- kick 不可の印を付ける。付けないと `bot_quota` の管理によって数 tick 以内に削除される。
- コントローラと pawn を調整し、スコアボードでボット扱いされないようにする。

ボットはプレイヤースロットを 1 つ占有する。
定員いっぱいで運用しているサーバーではこの点を考慮する。

## 制限

- **同時に鳴る音はサーバー全体で 1 つだけ。** 同時再生とミキシングは対象外である。優先度キューと `QueueBehavior.Interrupt` で代替する。
- **48 kHz モノラルのみ。** ステレオの Opus パケットは実機で検証済みで、クライアントがダウンミックスするため 2 チャンネル目は完全な無駄になる。
- **音質はボイス経路の上限に縛られる。** ビットレートは 128 kbps だが、クライアント側のボイス DSP が結果を整形するため、特に低音が弱く出る。
- **URL ソースはシークできない。** `ISoundPlayback.Seek` と `PlayOptions.Loop` が使えず、再生時間も不明になる。

## コマンド

現在このモジュールは、スピーカーボットを調整するための実験的なコマンドを登録している。
これらは開発用であり、スピーカーの構成が確定した時点で削除する。
チャットでは `!`、コンソールでは `ms_` を接頭辞として入力する。

| コマンド | 説明 |
|---|---|
| `sp_spk_status` | 現在のスピーカースロット、xuid、偽装 ID、偽装マスク |
| `sp_spk_probe` | サーバー側のコントローラ、pawn、userinfo の状態をダンプする |
| `sp_spk_bot [name]` | スピーカーボットを要求する。既にいる場合は改名する |
| `sp_spk_kick` | スピーカーボットを削除し、再作成も止める |
| `sp_spk_disguise <mask>` | 偽装項目を個別に on/off する |
| `sp_spk_name` / `sp_spk_slot` / `sp_spk_xuid` / `sp_spk_steam` | ボットの名前と、音声の送出元となる識別情報を上書きする |

## ビルド

```
dotnet build TnmsSoundPlayer/TnmsSoundPlayer.csproj
```

環境変数 `MOD_SHARP_DIR` が設定されていれば、ビルド時にモジュールが
`%MOD_SHARP_DIR%\modules\TnmsSoundPlayer\reload\` へ、Shared アセンブリが
`%MOD_SHARP_DIR%\shared\TnmsSoundPlayer.Shared\` へコピーされる。

リリースビルドは次のとおり。

```
dotnet publish TnmsSoundPlayer/TnmsSoundPlayer.csproj -f net10.0 -r win-x64 --no-self-contained -c Release -p:DebugType=None -p:DebugSymbols=false
```

Linux サーバー向けには `-r linux-x64` を使う。
出力をサーバーへコピーする前に、ModSharp が既に同梱しているアセンブリ（Microsoft.Extensions.\*、Serilog.\*、Google.Protobuf、System.Text.Json）を取り除く。

## ドキュメント

| 分類 | リンク |
|---|---|
| API | [はじめに](docs/ja/development/USING_SOUNDPLAYER_API.md) / [再生](docs/ja/development/api/playback.md) / [音声ソース](docs/ja/development/api/sources.md) |

## プラグイン開発者向け

詳細は [TnmsSoundPlayer API ガイド](docs/ja/development/USING_SOUNDPLAYER_API.md) を参照。

## ライセンス

AGPLv3。ModSharp 向けのリンク例外とデュアルライセンス例外が付く。
[LICENSE](LICENSE) を参照。

Copyright (c) 2026 faketuna
