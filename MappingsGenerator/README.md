# UEFNStaticMappingsGenerator

UEFNのEngine・Common DLLから、ゲームを起動せずに `.usmap` version 4を生成します。
元のコードは `D:\mappings_dumper` から取り込んでいます（2026-10-15 版。メニューとダウンロードを含みます）。
取り込み直すときは、このリポジトリだけの変更を残してください: `ExtraMappings.*`、`Dumper` の `AddExtraStructs`、
`Main.cpp` の `--extra`／`--game`／`--no-infer` と行構造体の推定、`CMakeLists.txt` の `ExtraMappings.cpp`、`build.bat` の `RowStructInferrer`。UHTのレイアウトはUE 6.0に対応しています。

Visual StudioのC++ツール、CMake、Ninja、Gitが必要です。
初回の構成時にzstd v1.5.7、Brotli v1.1.0、zlib v1.3.1、nlohmann/json v3.12.0を取得して静的リンクします。
HTTP通信とハッシュ検証にはWindows標準のWinHTTPとBCryptを使います。内蔵ライブラリのライセンスは `--licenses` で確認できます。

```bat
MappingsGenerator\build.bat libs
libs\UEFNStaticMappingsGenerator.exe --dir "C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64" --compression zstd --no-wait
```

### メニューから使う

EXEをダブルクリックするか、コンソールで引数を付けずに実行すると、日本語のメニューが開きます。
`--menu` を付けた場合も開きます（ほかの引数は無視されます）。
引数を1つでも付けた場合や、標準入出力がリダイレクトされている場合はメニューを出さず、コマンドラインの設定で実行します。
APIは常に引数を付けて呼び出すので、メニューは出ません。

- 処理: インストール済みのUEFNから生成／最新のUEFNをダウンロードして生成／ダウンロードのみ／最新ビルドの情報を確認／ライセンスを表示／終了
- 圧縮方式（生成するときだけ）: Zstandard／Brotli／Oodle／無圧縮（Enterだけなら無圧縮）

UEFNのフォルダーは自動で探します（`C:\Program Files` 以外も対象。見つからなければ入力を求めます）。
保存先はカレントディレクトリ、ダウンロード先はEXEと同じフォルダーの `uefn_cache` です。
メニューから生成したときも、行構造体の補完（下記）は行います。

### 最新UEFNをダウンロードして生成

```bat
libs\UEFNStaticMappingsGenerator.exe --download --no-wait
```

最新の `Fortnite_Studio` のマニフェストを取得し、全チャンクをEpicのCloudDirからダウンロード・復元してから、そのDLLで生成します。
UEFNのインストールは不要です。チャンクと復元ファイルはSHA-1で検証し、`uefn_cache` の検証済みデータは次回再利用します。
`--download-dir`、`--connections`、`--manifest-id`、`--download-only`、`--manifest-info` も使えます。
`--dir` を省略すると、インストール済みのUEFNを自動で探します。`--dir` にはFortniteのフォルダーと `Binaries\Win64` のどちらも指定できます。

`--out` で出力先、`--level` で圧縮レベル、`--oodle` でOodle DLLを指定できます。
`--extra <file.usmap>`（複数可）で、非圧縮の `.usmap` に入っている構造体のうち DLL に無いものを出力に加えます。
API はこれを使い、ゲーム本体の DataTable から推定した行構造体（UEFN の DLL に入っていない GameFeature プラグインの型）を加えます。

### DataTable の行構造体の補完

UEFN の DLL には、ゲーム本体だけにコンパイルされている GameFeature プラグインの型がありません
（例: 43.00 の `DelilahTreasureHuntSharedRuntime` の `DelilahTreasurePrizeRow`）。DLL だけから作ったマッピングでは、
それを行構造体に使う DataTable の `Rows` が空になります。

そこで生成器は、DLL から型を集めたあと、隣にある `RowStructInferrer.exe` を呼び出します。
`RowStructInferrer.exe` はインストール済みのゲームを、そのゲームのバイナリに入っている AES 鍵でマウントし
（鍵が足りないときだけ AES キー API に問い合わせます。`fnexportAPI` の `/api/v1/aes/local` と同じ処理です）、
マッピングに無い行構造体を cook 済みの行データから推定します。推定した構造体は同じ出力に加わります。
Fortnite 43.00 では、推定を含めて約1分半です。

- 対象のゲームは、`--dir` の3つ上の階層（`Fortnite\FortniteGame\Binaries\Win64` なら `Fortnite`）です。`--game` で変更できます。
- `--no-infer` で推定を省きます（API は自分で推定してから `--extra` で渡すので、常にこれを付けて呼び出します）。
- `RowStructInferrer.exe` が無い場合や推定に失敗した場合は、警告を出して DLL だけのマッピングを書き出します。
- プロパティ名は cook 済みデータに無いため `Name_0`、`Bool_4` のような仮名です（テキストのキーから分かる名前は使います）。
  行の区切り方が一意に決まらない表、1行しかない表、時間内に解けない表は補完しません。
- `build.bat` は `RowStructInferrer.exe`（.NET、自己完結型の単一ファイル）も発行し、生成器の隣に置きます。
圧縮方式は `zstd`（既定）、`brotli`、`oodle`、`none` です。
その他の引数は `--help` を参照してください。

EngineとCommonをロードし、`ConstructUClass`・`ConstructUScriptStruct`・`ConstructUEnum` をフックしてUHTの型情報を収集します。
コンテナの要素、親クラス、列挙型を解決し、cook時に除去されるEditorOnlyプロパティを除いて書き出します。
圧縮後は伸長結果を照合します。API側でもCUE4Parseで検証してから保存します。

APIからは `POST /api/v1/mappings/generate` で呼び出します。
DLLのロードとフックは生成プロセス内で完結します。
