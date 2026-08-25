# fnexportAPI

**Fortnite のアセットを HTTP API として取得・検索・エクスポートする Web API**

**日本語** | [English](README.en.md)

[`CUE4Parse`](https://github.com/FabianFG/CUE4Parse) を利用して、Fortnite の最新マニフェストを取得し、
必要な pak／チャンクを Epic CDN からストリーミングします。解析したアセットは JSON・PNG・音声として
HTTP で公開され、全エンドポイントを Swagger UI から確認・実行できます。

## 主な機能

| 機能 | 内容 |
|---|---|
| アセットエクスポート | `.uasset`／`.umap` を JSON、テクスチャを PNG、サウンドを音声として返却 |
| ローカライズ | `lang=ja` などを指定してローカライズ済み文字列を取得 |
| アイテム・コスメ検索 | アイテムのプロパティ、コスメ情報、アイコン、OfferCatalog を抽出 |
| 全文検索 | パス・ファイル名だけでなく、読み込み済みアセットの内容も検索（結果は既定 24 時間キャッシュ） |
| FModel バックアップ | 現在のビルドのファイル一覧を FModel の `.fbkp` 形式で配信 |
| 自動アップデート | 起動時に GitHub Releases API を確認し、確認プロンプト（y/n）を経て適用・再起動 |
| AES／マニフェスト監視 | 新しいビルド・復号鍵・`.usmap` をバックグラウンドで自動反映 |
| API ドキュメント | 日本語・英語対応の Swagger UI と OpenAPI JSON |

## 目次

- [必要なもの](#必要なもの)
- [起動・ビルド](#起動ビルド)
- [環境変数](#環境変数)
- [API エンドポイント](#api-エンドポイント)
- [RAD Audio デコーダ](#rad-audio-デコーダradadecoderradadecoder-cs)
- [Swagger / OpenAPI](#swagger--openapi)

## 最短で起動する

1. .NET 10 SDK を用意します。
2. Oodle と zlib-ng のネイティブライブラリを配置します（詳細は[必要なもの](#必要なもの)を参照）。
3. 次のコマンドを実行します。

```bash
cd FortnitePorting
dotnet run
```

起動後、[http://localhost:3849/swagger](http://localhost:3849/swagger) を開いて API を試せます。

## 必要なもの

### 1. Oodle 圧縮ライブラリ

Fortnite の pak は Oodle 圧縮を使うため、**Oodle ネイティブライブラリ**が必要です。

Unreal Engine のインストール（または FModel などのツール）から入手してください:

- **Windows**: `oo2core_9_win64.dll`
- **Linux**: `liboo2corelinux64.so.9`

以下の順で探索されます（いずれかで配置可能）:

**方法A — 実行ファイル／ビルド出力の隣に置く**
```bash
# Windows
copy oo2core_9_win64.dll FortnitePorting/bin/Debug/net10.0/

# Linux
cp liboo2corelinux64.so.9 FortnitePorting/bin/Debug/net10.0/
```

**方法B — プロジェクトの `libs/` ディレクトリに置く**
```bash
mkdir -p libs
copy oo2core_9_win64.dll libs/      # Windows
cp liboo2corelinux64.so.9 libs/     # Linux
```

**方法C — 環境変数でパスを指定する**
```bash
# Windows
set OODLE_DLL_PATH=C:\path\to\oo2core_9_win64.dll

# Linux
export OODLE_DLL_PATH=/path/to/liboo2corelinux64.so.9
```

> 同じ探索ルールが `zlib-ng2.dll`（Windows）／`libz-ng.so`（Linux）にも適用されます。

### 2. RAD Audio デコードライブラリ（任意 — RADA → WAV 変換にのみ必要）

最近の Fortnite サウンドの多くは **RADA**（RAD Audio）形式です。RADA を WAV に変換するには
ネイティブの RAD Audio デコードライブラリ（`rada_decode.dll`）が必要です。Oodle と同様に
（実行ファイルの隣、`libs/`、または `RADA_DLL_PATH`）で解決され、以下のいずれかの名前に対応します:
`rada_decode`、`radaudio`（Windows は `.dll`、Linux は `lib*.so`）。

このライブラリは本リポジトリに同梱されています（`libs/rada_decode.dll`）。Unreal Engine 5.6 に
同梱の RAD Audio デコーダ（`Engine/Source/Runtime/RadAudioCodec/SDK`）を薄いシムでラップして
ビルドしたものです。再ビルドする場合は [`RADADecoder/shim/build.bat`](RADADecoder/shim/build.bat)
を実行してください（VS 2022 と UE 5.6 のインストールが必要。Fortnite 固有のインライン「SEEK」
チャンク除去をシム側で行っています）。

ライブラリが**無くても** API は動作します。`audio=true` は変換に失敗せず**生の RADA
ストリーム**を HTTP 200 で返し、レスポンスヘッダ `X-Audio-Decoded: false` で未変換であることを
示します。その他の形式（PCM／ADPCM → WAV、BINKA、OPUS、OGG、WEM、AT9）は常に配信されます。

## 起動・ビルド

### ローカル（開発）

```bash
cd FortnitePorting
dotnet run
```

既定で `http://0.0.0.0:3849` を待ち受けます（`PORT` 環境変数で変更可能）。
**http://localhost:3849/swagger** を開くと全エンドポイントを確認・実行できます。
ルート `/` は `/swagger` へリダイレクトします。

### 単一の自己完結型 `.exe` をビルド（Windows）

このプロジェクトは、**ランタイム・全マネージド依存・ネイティブライブラリを1つの実行ファイル**に
まとめて発行するよう設定されています:

```bash
build.bat
```

出力:

```
FortnitePorting/bin/Release/net10.0/FortnitePorting.exe
```

> Oodle／zlib-ng／RAD Audio のネイティブライブラリは実行時に取得されるため、exe には
> 同梱されません。`oo2core_9_win64.dll`・`zlib-ng2.dll`（および任意で RAD Audio ライブラリ）を
> `FortnitePorting.exe` の隣に置いてください。

### Docker

```bash
docker build -t fnexportapi .
docker run -p 3849:3849 \
    -v fnexport-libs:/app/libs \
    -v fnexport-manifest:/app/manifest \
    -v fnexport-cache:/app/chunk_cache \
    -v fnexport-mappings:/app/mappings \
    -e PROJECT_ROOT=/app \
    fnexportapi
```

## 環境変数

| 変数 | 既定値 | 説明 |
|---|---|---|
| `PORT` | `3849` | 待ち受けポート。 |
| `PROJECT_ROOT` | (自動) | `libs/`・`manifest/`・`chunk_cache/`・`mappings/` の解決に使うルート。Docker では `/app`。 |
| `OODLE_DLL_PATH` | – | Oodle ネイティブライブラリの明示パス。 |
| `RADA_DLL_PATH` | – | RAD Audio デコードライブラリの明示パス（RADA → WAV を有効化）。 |
| `USMAP_PATH` | – | 読み込む `.usmap` の明示パス。指定時はそのファイルを使用（**存在しなければ最新版を自動ダウンロード**）。 |
| `SKIP_MAPPING` | `false` | `.usmap` マッピングのロードを完全にスキップ（省メモリ。一部アセットはデシリアライズ不可に）。 |
| `LOAD_ALL_VFS` | `false` | 厳選サブセットではなく全 VFS ファイルをマウント。 |
| `SEARCH_THREADS` | (CPU数) | 内容検索の並列スキャン数。既定は論理 CPU 数（全コア活用）。 |
| `CONTENT_CACHE_MB` | `unlimited`（無制限） | 内容検索で読み込んだ解凍バイトをキャッシュ。既定は無制限で、PAK状態が変わるまで保持。`0` で無効化、正の値でMB上限を指定可能。 |
| `SEARCH_CONTENT_CACHE_MINUTES` | `1440`（24時間） | 内容検索（`/api/v1/search/content`）の同一クエリ結果を保持する分数（スライディング）。`0` でキャッシュ無効。 |
| `SEARCH_PATH_CACHE_MINUTES` | `1440`（24時間） | パス検索（`/api/v1/search`）の同一クエリ結果を保持する分数（スライディング）。`0` でキャッシュ無効。 |
| `SEARCH_CACHE_MAX_MINUTES` | `10080`（7日） | 検索結果キャッシュの絶対上限。連続ヒットしても、この時間を超えたエントリは破棄されます。 |
| `HOTFIX_CLOUDSTORAGE_URL` | `https://api.fljpapi.jp/api/v2/cloudstorage` | `hotfix=true` で読み込む cloudstorage 一覧のURL。各ファイルは `{URL}/{uniqueFilename}` から取得します。 |
| `HOTFIX_CACHE_MINUTES` | `10` | ホットフィックスの一覧を確認し直すまでの分数。 |
| `HOTFIX_CACHE_DIR` | `<PROJECT_ROOT>/hotfix_cache` | ダウンロードしたホットフィックス設定ファイルの保存先。再起動後も再利用します。 |
| `HOTFIX_DISK_CACHE` | `true` | `false` でディスクキャッシュを無効化（毎回ダウンロード）。 |
| `AESFINDER_PATH` | `D:\AesFinder-main\...\AesFinder.exe` | `/aes` で使う外部 AesFinder ツールのパス（`.exe`／`.dll`／それを含むディレクトリ可）。 |
| `AESFINDER_AUTO` | `true` | バックグラウンドで AesFinder により MainAES を自動抽出・投入（**main 鍵が未適用の時のみ**動作。`false` で無効）。 |
| `AUTO_UPDATE` | (未設定) | `true` = 確認せず常に更新／`false` = GitHub へ一切アクセスしない／**未設定 = 更新がある時だけ起動時に y/n を尋ねる**。 |
| `UPDATE_CHECK_ONLY` | `false` | 新しいリリースを通知するだけで、適用しません。 |
| `UPDATE_RESTART` | `true` | 差し替え後に自動で再起動。`false` の場合は差し替えのみで、起動は手動になります。 |
| `UPDATE_REPO` | `Fortniteleakjp/fnexportAPIv2` | リリースを取得する `owner/name`（フォーク運用向け）。 |
| `GITHUB_TOKEN` | – | 任意。GitHub API の匿名レート制限（60回/時）を緩和します。 |

> **マッピング（.usmap）の挙動**: 既定では `.usmap` マッピングを読み込みます。`USMAP_PATH` 指定時かつファイルが存在すればそれを使用し、**それ以外（未指定／指定ファイルが無い）の場合は最新版を自動ダウンロード**します（取得失敗時は既存のローカルファイルにフォールバック）。どうしても入手できない場合のみ、起動を失敗させずにスキップします（マッピング無しでは一部アセットがデシリアライズできません）。`SKIP_MAPPING=true` で明示的に無効化できます。

> **自動更新（再起動不要）**:<br>・**新しい復号鍵**: 約30秒ごとにローカルの `/api/v1/archives/keys` を取得し、**GUID 一致**で必要な鍵を投入 → 対応する pak を自動マウントします（pak名に依存しません）。このエンドポイントが現在のアーカイブと外部キー情報を集約します。<br>・**新しいビルド**: 約30秒ごとにビルド情報をポーリングし、ビルド／マニフェストの変化を検出するとマニフェストを再取得したうえで、**旧ビルドの VFS をすべて破棄し、新マニフェストから全 VFS（utoc/pak）を登録・マウントし直します**（再起動と同じ処理）。アップデートでは既存の `pakchunk*.utoc/.ucas` が同名のまま中身ごと差し替わるため、追加分だけをマウントすると旧ビルドの内容を配信し続けてしまいます。再構築中は他のエンドポイントが `503`（`Retry-After: 30`）を返し、完了後は旧ビルド由来のキャッシュ（レスポンス／検索／ローカライズ）も全消去されます。新規の暗号化 pak は鍵が届いた時点で上記のAES監視によりマウントされます。<br>・**マッピング(.usmap)**: 新ビルド検出時に**新ビルド用の最新 .usmap を自動再取得し、ホットスワップ**します（`USMAP_PATH` でファイルを固定している場合はそれを維持）。<br>これらはすべてプロセスの自動再起動なしで行われます（外部APIが新ビルドの鍵・マッピングを配信するまでの間は、その新規コンテンツのみ未対応となり、配信され次第自動で反映されます）。

## API エンドポイント

ベース URL: `http://localhost:3849`

### エンドポイント一覧

| 用途 | エンドポイント |
|---|---|
| アセットの JSON／画像／音声エクスポート | [`/api/v1/export`](#アセットエクスポート--apiv1export) |
| アイテムの検索・プロパティ抽出 | [`/api/v1/items`](#アイテム検索--apiv1items) |
| ファイル名・アセット内容の全文検索 | [`/api/v1/search`](#文字列検索--apiv1search) |
| AES 鍵の取得・投入 | [`/aes`](#aes鍵取得-aes) |
| デバッグ情報・マウント済みファイルの確認 | [`/api/v1/debug`](#デバッグ--apiv1debug) |
| アーカイブ情報・AES 情報 | [`/api/v1/archives`](#アーカイブ情報aes--apiv1archives) |
| コスメ・表示アセットの抽出 | [`/api/v1/pak`](#コスメ抽出--apiv1pak) |
| 配信中ビルドの確認・最新ビルドへの再読み込み | [`/api/v1/build`](#ビルド状態--apiv1build) |
| FModel 用バックアップ（`.fbkp`）の配信 | [`/api/v1/backup`](#fmodel-バックアップ--apiv1backup) |
| 更新状況の確認・最新リリースへの更新 | [`/api/v1/update`](#自動アップデート--apiv1update) |

> **CORS**: すべてのオリジンからの呼び出しを許可しています（任意のオリジン／メソッド／ヘッダ）。
> 音声診断ヘッダ（`X-Audio-Format` / `X-Audio-Decoded` / `X-Rada-Native-Decoder`）と
> `Content-Disposition`、バックアップ診断ヘッダ（`X-Backup-Entries` / `X-Backup-Version`）、
> ホットフィックス診断ヘッダ（`X-Hotfix-Status` / `X-Hotfix-Applied`）はブラウザから読めるよう公開されています。

### アセットエクスポート — `/api/v1/export`

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/export?path={path}&image={bool}&audio={bool}&lang={code}&hotfix={bool}` | アセットをエクスポート。既定は JSON で、全エクスポートを `jsonOutput` 配列に返します。Unrealの通常プロパティ名は元の大文字・小文字を保持し、ローカライズ文字列のキーのみ FortniteAPI と同じ `namespace`・`key`・`sourceString`・`localizedString` にします。`hash` はその配列の UTF-8 JSON の SHA-256、`entries` は件数、`bytes` は同JSONのバイト数です。`image=true` でテクスチャを PNG、`audio=true` でサウンドを音声、`lang` でローカライズ（例: `ja`）、`hotfix=true` で[ホットフィックス適用済みの内容](#ホットフィックス適用--hotfixtrue)を返します。**`image=true` でも対象がテクスチャでない場合は自動的に JSON を返します。** |
| `GET /api/v1/export/audioinfo?path={path}` | サウンドアセットの形式や WAV 変換可否を、バイナリを返さずに報告。 |
| `GET /api/v1/export/locres?lang={code}` | 指定言語の結合済みローカライズテーブル。 |
| `GET /api/v1/export/locres/languages` | 利用可能なローカライズ言語の一覧。 |
| `GET /api/v1/export/filepath/{pakName}` | 指定 pak／チャンク番号内のファイルパス一覧。 |

#### ホットフィックス適用 — `hotfix=true`

Fortnite は pak に焼き込まれた値をそのまま使うのではなく、cloudstorage の設定ファイルで
DataTable／CurveTable の中身や表示テキストを上書きしてから実行します。
`hotfix=true` を付けると、その上書きを適用した JSON（＝実際にゲームが動いている値）を返します。
既定は `false` で、その場合は従来どおり pak の内容をそのまま返します。

読み取るセクションは 2 つです:

- `[AssetHotfix]` — DataTable／CurveTable／CurveFloat の中身の書き換え（アセット単位）。
- `[/Script/FortniteGame.FortTextHotfixConfig]` — `+TextReplacements=` による FText の差し替え（namespace と key で一致するテキストすべて）。

対象ファイルは `https://api.fljpapi.jp/api/v2/cloudstorage` の一覧に載っている**すべての**ファイルです
（`[AssetHotfix]` は `DefaultGame.ini` だけでなく `DefaultBlastberryGame.ini` や `IOS_Game.ini` などにもあり、
`+TextReplacements=` は `PS5_Game.ini` などプラットフォーム別ファイルにもあります）。
取得内容は既定で 10 分キャッシュされ、内容が変わるとレスポンスキャッシュも自動的に無効化されます。

##### キャッシュ

ダウンロードしたファイルは `hotfix_cache/` に保存され、**再起動しても再ダウンロードしません**。
cloudstorage の `uniqueFilename` は再アップロードのたびに変わるため、キャッシュは内容アドレス方式になり、
古い内容が居座ることはありません（更新されたファイルだけが新しい名前で降ってきます）。

- 一覧（`listing.json`）も保存するので、**cloudstorage に到達できない状態で起動しても**キャッシュだけでホットフィックスを提供できます。
- 読み込み時にサイズと SHA-256 を照合し、壊れていれば自動的に取り直します。
- 一覧から消えたファイルは自動削除されます。

| | 所要時間 | ダウンロード |
|---|---|---|
| 初回（キャッシュ無し） | 約 5.1 秒 | 62 ファイル |
| 2回目以降（キャッシュ有り） | 約 0.08 秒 | 0 ファイル |

| 環境変数 | 既定値 | 説明 |
|---|---|---|
| `HOTFIX_CLOUDSTORAGE_URL` | `https://api.fljpapi.jp/api/v2/cloudstorage` | 一覧のURL。各ファイルは `{URL}/{uniqueFilename}` から取得します。 |
| `HOTFIX_CACHE_MINUTES` | `10` | 一覧を確認し直すまでの分数（メモリ上の索引の保持時間）。 |
| `HOTFIX_CACHE_DIR` | `<PROJECT_ROOT>/hotfix_cache` | ダウンロードしたファイルの保存先。 |
| `HOTFIX_DISK_CACHE` | `true` | `false` にするとディスクキャッシュを使わず毎回ダウンロードします。 |

対応している書き換えは次のとおりです:

| 行 | 動作 |
|---|---|
| `+CurveTable=Path;RowUpdate;Row;KeyTime;Value` | 指定行のカーブの、そのキー時刻の値を書き換え（無ければキーを挿入）。 |
| `+CurveTable=Path;TableUpdate;"[{...}]"` | カーブテーブルの全行を置き換え。 |
| `+DataTable=Path;RowUpdate;Row;Property;Value` | 指定行の1プロパティを書き換え。構造体リテラル `(X=1,Y=3)` はメンバー単位でマージします。 |
| `+DataTable=Path;AddRow;"{...}"` | JSON で指定した行を追加。 |
| `+DataTable=Path;TableUpdate;"[{...}]"` | データテーブルの全行を置き換え。 |
| `+CurveFloat=Path;CurveUpdate;"{...}"` | `UCurveFloat` のカーブを置き換え。 |
| `+TextReplacements=(Category=…, Namespace="", Key="…", NativeString="…", LocalizedStrings=(("ja","…"),…))` | 同じ namespace・key を持つ FText の `SourceString` を `NativeString` に、`LocalizedString` をリクエストの `lang` に対応する訳文に差し替え。 |

pak に存在しない行への `RowUpdate` はゲームと同じく無視し、レスポンスで `rowNotFound` として報告します。

テキスト差し替えはアセット単位ではなく、書き出した JSON 中の FText すべてを namespace と key で照合します。
`.locres` によるローカライズの**後**に適用するため、ホットフィックスがある文字列は locres より優先されます。
`lang` に完全一致する訳文が無い場合は、同じ言語の別地域（`pt` → `pt-BR`）、`en`、`NativeString` の順にフォールバックします。
同じ key が複数ファイルにある場合（プラットフォーム別の文言など）は、ファイル名順で最後のものを採用します。

例（冒頭のカーブを `0.0 → 1.0` に書き換えるホットフィックス）:
```
http://localhost:3849/api/v1/export?path=/SpriteBoons_Ch7S4/DataTables/SpriteBoons_Ch7S4GameData&lang=ja&hotfix=true
```

レスポンスの形は `hotfix` の有無で変わりません。`hash`・`entries`・`bytes`・`jsonOutput` の
通常のレスポンスのまま、`jsonOutput` の中身だけがホットフィックス適用後の値になります
（`hash` も適用後の JSON から計算されます）。

適用状況はヘッダで確認できます:

- `X-Hotfix-Status` — `applied`（1件以上書き換えた）／`none`（書き換えなし。該当ホットフィックスが無いか、対象行が pak に存在しない）／`unavailable`（cloudstorage に到達できず）。
- `X-Hotfix-Applied` — 実際に適用された件数。

cloudstorage に到達できない場合でもエクスポート自体は失敗させず、pak のままの内容を
`X-Hotfix-Status: unavailable` を付けて返します（この応答はキャッシュしません）。
`POST /api/v1/export/batch` でもリクエストボディの `hotfix` で同じ指定ができます。

#### 音声出力

`audio=true` は `USoundWave` または Wwise（`UAkMediaAssetData`）アセットをデコード／配信します:

| 元の形式 | 出力 | Content-Type |
|---|---|---|
| PCM／ADPCM | WAV（RIFF/WAVE をそのまま） | `audio/wav` |
| RADA | RAD Audio ライブラリがあれば WAV、無ければ生の `.rada` ストリーム | `audio/wav` / `audio/x-rada` |
| BINKA／OPUS／OGG／WEM／AT9 | 生のエンコード済みストリーム | `audio/x-binka`、`audio/opus`、`audio/ogg`、`audio/x-wwise`、`audio/x-at9` |

レスポンスヘッダで結果がわかります:

- `X-Audio-Format` — 元の音声形式（例: `RADA`）。
- `X-Audio-Decoded` — WAV に変換できたら `true`、生ストリームを返したら `false`。
- `X-Rada-Native-Decoder` — `available` / `unavailable`。

例:
```
http://localhost:3849/api/v1/export?path=FortniteGame/Content/.../MySound.uasset&audio=true
```

### アイテム検索 — `/api/v1/items`

ファイル名が `WID_`、`AGID_`、`Athena_`、`Figment_Athena_` のいずれかで始まるアセットを
検索・抽出します（`prefixes` で上書き可能）。

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/items/files?prefixes={csv}&page={n}&pageSize={n}&ext={ext}` | 接頭辞に一致するファイルのパス（既定の拡張子は `.uasset`）。 |
| `GET /api/v1/items/properties?prefixes={csv}&page={n}&pageSize={n}` | 各アセットから `Properties.ItemName.SourceString`、`DataList → Traits`、`LargeIcon.AssetPathName` を抽出（ページング）。 |
| `GET /api/v1/items/properties/single?path={path}` | 単一アセットに対する同じ抽出。 |

レスポンス例（`/api/v1/items/properties/single`）:
```json
{
  "path": "FortniteGame/Content/Athena/Items/Consumables/AppleSun/WID_Athena_AppleSun.uasset",
  "name": "WID_Athena_AppleSun",
  "exportType": "FortWeaponRangedItemDefinition",
  "itemName": "Crash Pad",
  "traits": ["Item.Trait.AllowEmptyFinalStack", "Item.Trait.Transient"],
  "largeIcon": "/Game/UI/Foundation/Textures/Icons/Athena/T-T-Icon-BR-AppleSunGadget-L.T-T-Icon-BR-AppleSunGadget-L"
}
```

### 文字列検索 — `/api/v1/search`

単語・文字列・コードネームを入力して、**読み込み済みの全ファイル**を対象に検索します。
パス／ファイル名の高速検索に加え、アセットの内容（プロパティ）への限定的な全文検索も提供します。

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/search?q={text}&mode={mode}&field={field}&ext={csv}&dir={dir}&dedupe={bool}&caseSensitive={bool}&page={n}&pageSize={n}` | 全ファイルのパス／名を検索。一致ファイルの `path`／`name`／`ext` を総数つきで返す（ページング、最大 10000/頁）。 |
| `GET /api/v1/search/content?q={text}&dir={dir}&pathContains={text}&ext={csv}&maxScan={n}&maxResults={n}&snippetsPerFile={n}&caseSensitive={bool}` | ファイルの**内容**に含まれる文字列を検索。アセット（`.uasset`/`.umap`）はエクスポートを JSON 化、設定/テキスト/バイナリ（`.ini`/`.bin`/`.json` 等）は生バイトを復号して検索。一致ファイルと該当箇所スニペットを返す。既定の対象は「アセット＋設定/テキスト」、`ext=*` で全ファイル、`ext=.ini` 等で限定。**既定で全ファイル（約165万件・約11GB）を約40秒で走査**（バイト走査＋マルチコア並列）。走査順は **(1) パスにクエリを含む → (2) 近傍アセット → (3) 設定/テキスト → (4) その他アセット**。速度優先時は `maxScan` に小さい値を指定。 |

**`mode`（照合方法）**: `contains`（部分一致・既定）／`prefix`（前方一致）／`suffix`（後方一致）／`exact`（完全一致）／`wildcard`（`*` `?` のグロブ）／`regex`（正規表現）／`tokens`（空白区切りの全語 AND 一致）
**`field`（照合対象）**: `path`（フルパス・既定）／`name`（ファイル名）／`stem`（拡張子なしの名前）

例（コードネームで検索）:
```
http://localhost:3849/api/v1/search?q=HonestWasp
http://localhost:3849/api/v1/search?q=WID_&mode=prefix&field=name&dedupe=true
http://localhost:3849/api/v1/search?q=*Athena*Soldier*&mode=wildcard&field=name&ext=.uasset
```
レスポンス例（`/api/v1/search`）:
```json
{
  "query": "HonestWasp",
  "mode": "contains",
  "field": "path",
  "totalMatches": 7,
  "totalPages": 1,
  "currentPage": 1,
  "pageSize": 100,
  "results": [
    { "path": "FortniteGame/.../Character_HonestWasp.uasset", "name": "Character_HonestWasp.uasset", "ext": ".uasset" }
  ]
}
```

> **補足**: パス検索は全ファイル（約 240 万件）を走査します。`regex` は安全のため、評価ごとのタイムアウト（250 ミリ秒）・全体時間制限・パターン長制限が掛かります。内容検索（`/content`）は **アセットに加え `.ini`/`.bin`/`.json` 等の設定・テキストファイルも対象**で、パス一致 → **近傍アセット（同一プラグイン/フォルダ）** → 設定/テキスト → その他アセット の順で `maxScan` 件まで走査します。検出は文字列確保なしのバイト走査をマルチコアで並列実行するため、**既定で全ファイル（約165万件・約11GB）を約40秒で全件走査**します。これにより `RankedTier` のようにパスにヒントが無く多数のプラグインに散在するケースでも、`?q=RankedTier` だけで全件ヒットします。クイックに確認したいときは `maxScan` に小さい値（例: `maxScan=2000`）を指定すると先頭から部分走査します。対象が分かっていれば `dir`／`pathContains`／`ext` で絞ると高速です。
>
> **高速化**: 走査は**全 CPU コアで並列実行**します（`SEARCH_THREADS` で調整可）。さらに**同一クエリの結果は既定で24時間キャッシュ**されるため、2回目以降は即時に返ります（最終ヒットからのスライディング期限。上限は `SEARCH_CACHE_MAX_MINUTES` の7日）。キャッシュキーには読み込み済みファイル数が含まれ、加えて新ビルドでプロバイダーが再構築されるとレスポンスキャッシュ自体が全消去されるため、**古いビルドの結果が返ることはありません**。保持時間は `SEARCH_CONTENT_CACHE_MINUTES`／`SEARCH_PATH_CACHE_MINUTES` で変更でき、`0` で無効化できます。なお全体時間制限で打ち切られた（`truncated` かつ時間切れの）パス検索結果は、再試行で完全な結果を得られるよう **5分だけ**キャッシュされます。解凍済みバイトも既定で無制限にキャッシュするため、別クエリの再走査でも再読み込み・再展開を抑えます。

### AES鍵取得 — `/aes`

| メソッド & パス | 説明 |
|---|---|
| `GET /aes` | ライブの **Fortnite_Studio（UEFN）** マニフェストから `UnrealEditorFortnite-Common-Win64-Shipping.dll` を**ダウンロード**し、外部 **AesFinder** ツールで **MainAES 鍵を抽出**して返します（**ゲーム起動・注入なし**）。抽出鍵は**そのまま provider に投入してマウント**します。`{ mainKey, version, build, fullVersion, submitted, mountedNewFiles, totalFiles, ... }` を返却。 |
| `GET /aes?submit=false` | 鍵を返すだけで provider への投入・マウントは行いません（既定は `submit=true`）。 |
| `GET /aes?noApi=true` | fortnite-api を参照せず、バイナリ内の**最高エントロピー候補**を採用（純粋にバイナリから抽出）。 |
| `GET /aes?force=true` | キャッシュを無視して Common DLL を再ダウンロード。 |

> MainAES 鍵は Common DLL 内に `mov [rbp+d], imm32` 命令の即値（AESDumpster パターン）として**平文**で格納されています（連続した32バイトでもスケジュールでもないため、単純なバイト検索やスケジュール走査では見つかりません）。本エンドポイントは外部 AesFinder ツール（`AESFINDER_PATH` で指定）でこれを抽出します。Common DLL は初回のみダウンロードし、以降はキャッシュを再利用、**新ビルド検出時は自動で新しい DLL を取得**します。
>
> **自動投入（フォールバック）**: バックグラウンドの `AesFinderKeyService` が、**main 鍵が未適用の間だけ**（例：新ビルドの鍵を外部 AES API がまだ配信していない時）AesFinder で鍵を抽出して provider に投入し、pak を自動マウントします。鍵が既に適用済みの通常時は**一切ダウンロードせずアイドル**です（`AESFINDER_AUTO=false` で無効化）。これにより外部 AES API の配信を待たずに新ビルドへ追従できます。なお**ダイナミック鍵**（GUID 付き）は AesFinder の対象外で、従来どおり外部 AES 監視（`api.fortniteapi.com`／`uedb.dev`）が担当します。
>
> 補助として、ビルトインのスケジュール走査エンドポイント（`GET /api/v1/aes/extract`・`/api/v1/aes/scan/local`・`/api/v1/aes/finder/selftest`）も用意しています。

### ビルド状態 — `/api/v1/build`

| エンドポイント | 説明 |
|---|---|
| `GET /api/v1/build` | 現在配信中のビルド（`appliedBuild`／`appliedManifestId`）、マニフェストが指すビルド、マウント済み VFS 数、未取得の鍵数、再構築中かどうか（`reloading`）を返します。再構築中も応答します。 |
| `POST /api/v1/build/reload` | 30秒ポーリングを待たずに、最新マニフェストでプロバイダーを即座に再構築します。実行中は他のエンドポイントが `503` を返します。 |

### FModel バックアップ — `/api/v1/backup`

現在マウント中のビルドのファイル一覧を、**FModel のバックアップ形式（`.fbkp`）**で返します。
FModel の「Load → All But New／All But Modified」に読み込ませることで、**このビルドと後のビルドの差分だけ**を一覧できます。

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/backup/fbkp?includePayloads={bool}&compress={bool}` | `.fbkp` をダウンロード。ファイル名は**マウント中のビルド**から決まります（例: `FortniteGame_42_00.fbkp`）。 |
| `GET /api/v1/backup?includePayloads={bool}` | 生成せずに、収録件数・バージョン・想定ファイル名・現在のビルドを返します。 |

```
curl -OJ http://localhost:3849/api/v1/backup/fbkp
```

> **形式**: LZ4 フレームの中に、マジック `FBKP`（`0x504B4246`）、バックアップバージョン `2`（`PerfectPath`）、件数（int32）、
> 続けて 1 件ごとに サイズ（int64）・暗号化フラグ（bool）・パス（7bit 長プレフィックス文字列）を書き出します。
> [FModel の `BackupManagerViewModel.CreateBackup`](https://github.com/4sval/FModel/blob/63a7cbccd9fbaae9db45240069a49bd6a3a00b73/FModel/ViewModels/BackupManagerViewModel.cs#L23) と同一のバイト列です。
>
> **収録範囲**: FModel と同じく `.uexp`／`.ubulk`／`.uptnl` のペイロードは除外します（`includePayloads=true` で含められます）。
> `compress=false` を指定すると LZ4 で包まずに書き出します（FModel は先頭の LZ4 マジックを見て判別するため、どちらでも読み込めます）。
> レスポンスヘッダ `X-Backup-Entries`／`X-Backup-Version` に件数とバージョンが入ります。
>
> **ファイル名**: マウント中のビルド（`++Fortnite+Release-42.00-CL-...`）から `FortniteGame_42_00.fbkp` を生成します。
> ビルドが未取得のときのみ、FModel と同じ日付形式（`FortniteGame_MM_dd_yyyy.fbkp`）にフォールバックします。

### 自動アップデート — `/api/v1/update`

**起動時に GitHub Releases API（`https://api.github.com/repos/{owner}/{repo}/releases/latest`）を確認し、
新しいリリースがあればダウンロード・展開・差し替えを行い、再起動します。**
この確認は Fortnite ビルドのマウントより前に実行されるため、更新がある場合は重い初期化を無駄に行いません。

**更新があるときだけ、コンソールで適用するかを尋ねます**（`AUTO_UPDATE` 未設定時）:

```
Auto-update: current 1.1.0, v1.1.14 is available
Update to v1.1.14 now? [Y/n] (Y after 30s):
```

- `y` または Enter、および 30 秒無応答 → その場で更新して再起動します。
- `n` → `AUTO_UPDATE` の設定方法を表示し、**5 秒後に通常の起動処理を開始**します（更新は行いません）。
- 最新版の場合は何も尋ねず、そのまま起動します。
- `AUTO_UPDATE=true`／`false` を設定すると以後は尋ねません。サービス実行や Docker のように
  標準入力が端末でない場合も尋ねず、従来どおり自動で処理します。

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/update` | 実行中のバージョン、GitHub の最新リリース、更新可能かどうかとその理由を返します。 |
| `POST /api/v1/update?force={bool}` | 再起動を待たずに、その場で最新リリースへ更新します（差し替えのためプロセスは一度終了します）。 |

```
curl http://localhost:3849/api/v1/update
```

> **差し替えの流れ**: 実行中の実行ファイルは自分自身を上書きできないため、リリース資産
> （`FortnitePorting-win-x64.zip` / `FortnitePorting-linux-x64.tar.gz`）を `.update/staging` に展開し、
> 本プロセスの終了を待って差し替えるスクリプト（`apply.cmd` / `apply.sh`）を起動してから終了します。
> 差し替えは**コピー**であり、ミラーではありません。アーカイブに含まれない Oodle／zlib-ng などの
> ネイティブライブラリ、`libs/`、`mappings/`、`chunk_cache/`、ローカル設定はそのまま残ります。
>
> **自動更新されない場合**（`GET /api/v1/update` の `reason` に表示されます）:
> <br>・**ローカルビルド**: リリースワークフローがバージョンを刻んでいないビルド（`0.0.0-dev`）は、
> 比較すべきバージョンが無く、開発中の作業ツリーをリリース版で上書きしてしまうため対象外です。
> <br>・**コンテナ内**: Docker イメージは `dotnet FortnitePorting.dll` を実行する構成で、リリース資産
> （自己完結ビルド）とは形が異なり、書き込んでも次回起動で失われます。イメージを取得し直してください。
> <br>・**適用に失敗したバージョンの再試行**: 一度差し替えたのに古いままで起動した場合、無限ループを避けるため
> 自動での再試行はしません（`POST /api/v1/update?force=true` で解除できます）。

### デバッグ — `/api/v1/debug`

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/debug/stats?page={n}` | 読み込み済み全ファイルパス（1000 件ずつページング）。 |
| `GET /api/v1/debug/search?query={text}` | 読み込み済みファイルパスを部分一致検索。 |
| `GET /api/v1/debug/paks` | マウント済み pak／utoc ファイル一覧。 |
| `GET /api/v1/debug/paks/{pakName}/files` | マウント済み pak 内のファイル一覧。 |

### アーカイブ情報・AES — `/api/v1/archives`

| エンドポイント | 説明 |
|---|---|
| `GET /api/v1/archives` | 登録済みの `.pak`／`.utoc` アーカイブについて、名前、サイズ、ファイル数、マウントポイント、暗号化状態、GUID、圧縮方式などを返します。 |
| `GET /api/v1/archives/keys` | `version`、`mainKey`、`dynamicKeys`、`unloaded` を持つAESレスポンスを返します。GUID、AESキー、keychain文字列、ファイル数、サイズを含みます。GUID→AESは `https://fljpapi.jp/api/v2/keychain?rou=false` と照合し、Main AESなどはプロバイダーのキーを使用します。 |

### コスメ抽出 — `/api/v1/pak`

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/pak/{pakName}/cosmetics?page={n}&pageSize={n}&lang={code}` | 指定 PAK／チャンク（番号可）内の `FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics` 配下の各コスメと、`FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/DisplayAssets` 配下のバンドル／表示アセットを抽出（ページング、最大 200/頁）。コスメ結果には名称 Key、アイコン、Tags、OfferCatalog テクスチャを含み、表示アセット結果には `FortMtxOfferData` などの export データを含みます。 |

例（チャンク番号 30、日本語）:
```
http://localhost:3849/api/v1/pak/30/cosmetics?pageSize=50&lang=ja
```
レスポンス例（1件、`lang=ja`）:
```json
{
  "name": "Backpack_AbstractMirror",
  "exportType": "AthenaBackpackItemDefinition",
  "itemNameKey": "62B77828400008FD63C782B57223217D",
  "itemName": "メタルギアMk.II",
  "itemDescriptionKey": "1D0C41FF4E978741F86512A2027568AC",
  "itemDescription": "...",
  "itemShortDescriptionKey": "EC0A76294172A6021A503DB756D8D8A3",
  "itemShortDescription": "バックアクセサリー",
  "largeIcon": "/BRCosmetics/UI/Foundation/Textures/Icons/Backpacks/S28/T-Icon-Backpacks-AbstractMirror-L.T-Icon-Backpacks-AbstractMirror-L",
  "icon": "/BRCosmetics/UI/Foundation/Textures/Icons/Backpacks/S28/T-Icon-Backpacks-AbstractMirror.T-Icon-Backpacks-AbstractMirror",
  "tags": ["Cosmetics.Filter.Season.28", "Cosmetics.Set.HidingTime", "Cosmetics.Source.Season29.BattlePass.Paid"]
}
```
`lang` を省略（または `en`）すると、`itemName` 等には英語の原文（SourceString）が入ります。

対象 PAK 内に `FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/Textures` がある場合、各コスメの**スキンID**（名前の最初の `_` 以降。例 `Character_HonestWasp` → `HonestWasp`）に一致するテクスチャパスを `offerCatalog` キーで併記します。テクスチャは `T_Athena{カテゴリ}_{ID}` の規則で照合します（`Character` → `Soldiers`、その他は接頭辞名）。
例: `Character_HonestWasp` → `T_AthenaSoldiers_HonestWasp`、`Backpack_HonestWasp` → `T_AthenaBackpack_HonestWasp`。一致が無い／曖昧な場合は `null`。

## RAD Audio デコーダ（`RADADecoder` / `RADADecoder-cs`）

`RADADecoder-cs` はネイティブ RAD Audio デコードライブラリのマネージドラッパーで、API からは
`RadaDecoder.TryDecodeToWav(byte[], out byte[])` で利用されます:

- ネイティブライブラリは `DllImport` リゾルバで自動解決されます（実行ファイルの隣、`libs/`、
  `PROJECT_ROOT`、または `RADA_DLL_PATH`）。
- `RadaDecoder.IsNativeAvailable` でデコード可能かを報告します。
- ライブラリ欠如・入力破損でも例外を投げず `false` を返し、API は生ストリーム配信に
  フォールバックします。

`RADADecoder`（C++）はスタンドアロンのリファレンス CLI で、ビルドには RAD Audio SDK が必要です。

## Swagger / OpenAPI

UI には **日本語**（既定）と **English** の2ドキュメントがあり、画面右上のドロップダウンで
切り替えられます。

- Swagger UI: `http://localhost:3849/swagger`
- OpenAPI JSON（日本語）: `http://localhost:3849/swagger/ja/swagger.json`
- OpenAPI JSON（英語）: `http://localhost:3849/swagger/en/swagger.json`
