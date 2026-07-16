# fnexportAPI

**日本語** | [English](README.en.md)

**CUE4Parse** を利用した Fortnite アセットエクスポート Web API です。最新の Fortnite
マニフェストを取得し、必要な pak／チャンクを Epic CDN からストリーミングして、解析した
アセット（JSON／PNG／音声）を HTTP で公開します。全エンドポイントは **Swagger UI** で
確認・実行できます（日本語・英語の2言語）。

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
copy oo2core_9_win64.dll FortnitePorting/bin/Debug/net9.0/

# Linux
cp liboo2corelinux64.so.9 FortnitePorting/bin/Debug/net9.0/
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

## 実行方法

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
FortnitePorting/bin/Release/net9.0/FortnitePorting.exe
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
| `CONTENT_CACHE_MB` | `0`（無効） | 内容検索の解凍バイトキャッシュ予算（MB）。低速/ネットワークストレージで再走査を速める場合に有効化（例 `8192`）。ウォームなローカルストレージでは効果が薄いため既定無効。 |
| `AESFINDER_PATH` | `D:\AesFinder-main\...\AesFinder.exe` | `/aes` で使う外部 AesFinder ツールのパス（`.exe`／`.dll`／それを含むディレクトリ可）。 |
| `AESFINDER_AUTO` | `true` | バックグラウンドで AesFinder により MainAES を自動抽出・投入（**main 鍵が未適用の時のみ**動作。`false` で無効）。 |

> **マッピング（.usmap）の挙動**: 既定では `.usmap` マッピングを読み込みます。`USMAP_PATH` 指定時かつファイルが存在すればそれを使用し、**それ以外（未指定／指定ファイルが無い）の場合は最新版を自動ダウンロード**します（取得失敗時は既存のローカルファイルにフォールバック）。どうしても入手できない場合のみ、起動を失敗させずにスキップします（マッピング無しでは一部アセットがデシリアライズできません）。`SKIP_MAPPING=true` で明示的に無効化できます。

> **自動更新（再起動不要）**:<br>・**新しい復号鍵**: 約30秒ごとに AES を取得（`api.fortniteapi.com` → ダウン時 `uedb.dev`）し、**GUID 一致**で必要な鍵を投入 → 対応する pak を自動マウントします（pak名に依存しません）。<br>・**新しいビルド**: 約30秒ごとにビルド情報をポーリングし、変化を検出するとマニフェストを再取得して、**新規追加された VFS（utoc/pak）を自動登録・マウント**します。新規の暗号化 pak は鍵が届いた時点で上記のAES監視によりマウントされます。<br>・**マッピング(.usmap)**: 新ビルド検出時に**新ビルド用の最新 .usmap を自動再取得し、ホットスワップ**します（`USMAP_PATH` でファイルを固定している場合はそれを維持）。<br>これらはすべてプロセスの自動再起動なしで行われます（外部APIが新ビルドの鍵・マッピングを配信するまでの間は、その新規コンテンツのみ未対応となり、配信され次第自動で反映されます）。

## API エンドポイント

ベース URL: `http://localhost:3849`

> **CORS**: すべてのオリジンからの呼び出しを許可しています（任意のオリジン／メソッド／ヘッダ）。
> 音声診断ヘッダ（`X-Audio-Format` / `X-Audio-Decoded` / `X-Rada-Native-Decoder`）と
> `Content-Disposition` はブラウザから読めるよう公開されています。

### アセットエクスポート — `/api/v1/export`

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/export?path={path}&image={bool}&audio={bool}&lang={code}` | アセットをエクスポート。既定は JSON で、全エクスポートを `jsonOutput` 配列に返します。Unrealの通常プロパティ名は元の大文字・小文字を保持し、ローカライズ文字列のキーのみ FortniteAPI と同じ `namespace`・`key`・`sourceString`・`localizedString` にします。`hash` はその配列の UTF-8 JSON の SHA-256、`entries` は件数、`bytes` は同JSONのバイト数です。`image=true` でテクスチャを PNG、`audio=true` でサウンドを音声、`lang` でローカライズ（例: `ja`）。**`image=true` でも対象がテクスチャでない場合は自動的に JSON を返します。** |
| `GET /api/v1/export/audioinfo?path={path}` | サウンドアセットの形式や WAV 変換可否を、バイナリを返さずに報告。 |
| `GET /api/v1/export/locres?lang={code}` | 指定言語の結合済みローカライズテーブル。 |
| `GET /api/v1/export/locres/languages` | 利用可能なローカライズ言語の一覧。 |
| `GET /api/v1/export/filepath/{pakName}` | 指定 pak／チャンク番号内のファイルパス一覧。 |

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
> **高速化**: 走査は**全 CPU コアで並列実行**します（`SEARCH_THREADS` で調整可）。さらに**同一クエリの結果は15分間キャッシュ**されるため、2回目以降は即時に返ります（新しいビルド／鍵で読み込みファイル数が変わると自動的に無効化）。低速ストレージで別クエリの再走査も速くしたい場合は `CONTENT_CACHE_MB` で解凍バイトキャッシュを有効化できます。

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

### デバッグ — `/api/v1/debug`

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/debug/stats?page={n}` | 読み込み済み全ファイルパス（1000 件ずつページング）。 |
| `GET /api/v1/debug/search?query={text}` | 読み込み済みファイルパスを部分一致検索。 |
| `GET /api/v1/debug/paks` | マウント済み pak／utoc ファイル一覧。 |
| `GET /api/v1/debug/paks/{pakName}/files` | マウント済み pak 内のファイル一覧。 |

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
