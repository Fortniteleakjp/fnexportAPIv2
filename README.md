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
dotnet publish FortnitePorting/FortnitePorting.csproj -c Release -r win-x64
```

出力:

```
FortnitePorting/bin/Release/net9.0/win-x64/publish/FortnitePorting.exe
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

> **マッピング（.usmap）の挙動**: 既定では `.usmap` マッピングを読み込みます。`USMAP_PATH` 指定時かつファイルが存在すればそれを使用し、**それ以外（未指定／指定ファイルが無い）の場合は最新版を自動ダウンロード**します（取得失敗時は既存のローカルファイルにフォールバック）。どうしても入手できない場合のみ、起動を失敗させずにスキップします（マッピング無しでは一部アセットがデシリアライズできません）。`SKIP_MAPPING=true` で明示的に無効化できます。

> **自動更新（再起動不要）**:<br>・**新しい復号鍵**: 約30秒ごとに AES を取得（`api.fortniteapi.com` → ダウン時 `uedb.dev`）し、**GUID 一致**で必要な鍵を投入 → 対応する pak を自動マウントします（pak名に依存しません）。<br>・**新しいビルド**: 約3分ごとにビルド情報をポーリングし、変化を検出するとマニフェストを再取得して、**新規追加された VFS（utoc/pak）を自動登録・マウント**します。新規の暗号化 pak は鍵が届いた時点で上記のAES監視によりマウントされます。プロセスの自動再起動は行いません。

## API エンドポイント

ベース URL: `http://localhost:3849`

> **CORS**: すべてのオリジンからの呼び出しを許可しています（任意のオリジン／メソッド／ヘッダ）。
> 音声診断ヘッダ（`X-Audio-Format` / `X-Audio-Decoded` / `X-Rada-Native-Decoder`）と
> `Content-Disposition` はブラウザから読めるよう公開されています。

### アセットエクスポート — `/api/v1/export`

| メソッド & パス | 説明 |
|---|---|
| `GET /api/v1/export?path={path}&image={bool}&audio={bool}&lang={code}` | アセットをエクスポート。既定は JSON（**パッケージ内の全エクスポートを配列で返します**）。`image=true` でテクスチャを PNG、`audio=true` でサウンドを音声、`lang` でローカライズ（例: `ja`）。**`image=true` でも対象がテクスチャでない場合は自動的に JSON を返します。** |
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
| `GET /api/v1/pak/{pakName}/cosmetics?page={n}&pageSize={n}&lang={code}` | 指定 PAK／チャンク（番号可）内の `FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics` 配下の各コスメから、`ItemName`／`ItemDescription`／`ItemShortDescription` の Key（`lang` 指定時はローカライズ済みテキストも）、`LargeIcon` と `Icon` の `AssetPathName`、`Tags` を抽出（ページング、最大 200/頁）。 |

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
- OpenAPI JSON（英語）: `http://localhost:3849/swagger/en/swagger.json`#   f n e x p o r t A P I v 2  
 