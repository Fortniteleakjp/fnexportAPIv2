# エンドポイントと処理の配置

MVCのHTTP操作を68から53に、URLの種類を63から48に減らしました。重複する旧URLは削除し、用途ごとの入口へ統合しています。[旧URLの移行先](endpoint-migration.md)を確認してください。各パラメーターの型・既定値・レスポンス形式は起動時の `/swagger` で確認できます。

## 構成

| 場所 | 担当 |
|---|---|
| `Controllers/Assets` | エクスポート、PAK、コスメ、アイテム、INI、参照解析、ローカライズ |
| `Controllers/Builds` | 現行・履歴・ローカルビルド、差分、AES、バックアップ |
| `Controllers/Tools` | マッピング生成・管理、統合検索 |
| `Controllers/System` | 更新 |
| `Services/Assets` | ファイル索引、アーカイブ情報・選択、アセット読み取り、ローカライズ |
| `Services/Builds` | マニフェスト、履歴保管・マウント、差分、再読み込み制御 |
| `Services/Encryption` | AES取得・検出・監視・投入 |
| `Services/Hotfix` | ホットフィックス取得・適用 |
| `Services/Local` | ローカルインストール検出・マウント |
| `Services/Mappings` | usmap取得・保管、UEFN DLLからの生成 |
| `Services/Runtime` | 起動時のプロバイダー生成、ネイティブライブラリ、自己更新、キャッシュ管理 |
| `Infrastructure` | HTTP登録・パイプライン、JSON応答、ページング |

`Program.cs` は起動・サービス登録・キャッシュ登録に絞っています。`ApiEndpoints` がHTTP設定をまとめ、`ProviderReloadMiddleware` が再読み込み中のアクセスを制御します。大きなコントローラーはpartialクラスで処理単位に分割しています。

- `ExportController`: 基本エクスポートと一括処理、`Audio`、`Tables`、`Localization`、`Paths`。
- `SearchController`: パス検索、`Content`（内容検索と読み取り）、`Matching`（照合と設定）。
- `CosmeticsController`: 一覧・検索、`Icons`、`Properties`。

## 入口の選び方

| やりたいこと | 入口と絞り込み |
|---|---|
| ファイルを一覧にする | `GET /api/v1/files`。`prefixes`、`ext`、除外条件、ページを指定可能。省略時は全ファイル・全拡張子。 |
| パス・ファイル名を検索する | `GET /api/v1/search?q=...`。`mode`、`field`、`ext`、`dir`などを指定。 |
| ファイルの内容を検索する | 同じ `/search` に `target=content`。`maxScan`、`maxResults`、`pathContains`などを指定。 |
| PAKを調べる | `GET /api/v1/paks`。`state=mounted`（既定）・`unloaded`・`all`を指定。内容は `/paks/{pakName}/files`。 |
| コスメを一覧・検索する | `GET /api/v1/cosmetics`。`q`、`category`、`pakName`で絞り込み。`includeOffers=true`でバンドル表示アセットを含める。 |
| アイテムのプロパティを取得する | `GET /api/v1/items/properties`。`path`を指定すると1件、省略すると条件付き一覧。 |
| ローカライズを取得する | `GET /api/v1/localization`。`key`で対訳、`text`で逆引き、両方省略で`lang`の統合テーブル（既定ja）。言語一覧は `/localization/languages`。 |
| バックアップを取得する | `GET /api/v1/backup`。`format=json`（既定）で情報、`format=fbkp`でファイル。 |
| マッピングを生成する | `POST /api/v1/mappings/generate`。圧縮や保存名はクエリで指定。 |

`path`指定時のアイテム応答は単一オブジェクト、一覧時はページ情報と`results`です。ローカライズもテーブル・順引き・逆引きで形が異なります。判定に使うクエリを明示すると、クライアント側の取り扱いを固定できます。

## 共通の処理

一覧・検索は `FileIndex` の索引を参照します。ディレクトリーと拡張子を指定した検索は、ソート済みの拡張子バケット内を二分探索して候補を絞ります。アイテム名の検索結果は索引ごとに再利用し、最大16条件・合計25万パス参照に制限します。PAK一覧のファイル順は `ArchiveFileIndex` に保管します。ページングは `PageSlice` に統一し、大きなページ番号でも整数がオーバーフローしないようにしています。

Newtonsoft.Jsonで整形するJSONは `JsonResponse` で直接UTF-8にシリアライズします。検索の応答キャッシュもUTF-8のバイト列で保管します。一括エクスポートはバイト列からJSONを読み、不要な大きな文字列を作りません。テキストの読み取り・判定は取得済みバイト列を再利用します。

`Accept-Encoding: br` または `gzip` を送ると、JSON・テキスト・CSV・XMLを速度優先で圧縮します。既定のHTTPサーバーを対象とし、画像・音声・usmapなどのバイナリはこの圧縮の対象外です。

ビルドの再読み込み、明示的なマッピング適用、マッピングの再読み込みで `CacheRegistry.ClearAll()` を呼びます。要求ごとのキャッシュキーにはビルドと世代を含め、更新前から処理中の要求が更新後の応答キャッシュに混入するのを防ぎます。同じファイルのサイズ・更新時刻・プロバイダーのマッピング参照が変わっていなければ、マッピングの解析とGCを省きます。

`[VersionAware]` の読み取りエンドポイントは `VersionParameterFilter` が `version` / `loadVersion` を解決し、`RequestBuildProvider` に対象ビルドを設定します。コントローラーはフィルター実行後にプロバイダーを参照します。ライブビルドを再読み込み中は対象要求に503と `Retry-After: 30` を返し、履歴・差分・ビルド状態の確認は継続できます。

## マッピング生成

`POST /api/v1/mappings/generate` は `StaticMappingsGenerator` が `UEFNStaticMappingsGenerator.exe` を別プロセスで実行し、インストール済みUEFNのEngine/Common DLLからusmapを生成します。C++ソースは `MappingsGenerator` にあります。`MappingStore` が検証してから保存し、タイムアウトやキャンセル時は生成プロセスを終了します。`load=true` は生成・検証後のマッピングを現在のプロバイダーに適用し、キャッシュを更新します。ビルド番号やCLが異なる場合も適用します。既存ファイルの取得は `MappingService`、生成・インポートしたファイルの管理は `MappingStore` が担当します。

## ルート一覧

### アセット

| メソッド | パス | 処理 | ソース |
|---|---|---|---|
| GET | `/api/v1/assets/dependencies` | アセットの依存関係を取得 | [AssetsController.cs](../FortnitePorting/Controllers/Assets/AssetsController.cs) |
| GET | `/api/v1/config/files` | INIファイルを一覧 | [ConfigController.cs](../FortnitePorting/Controllers/Assets/ConfigController.cs) |
| GET | `/api/v1/config/query` | INI設定値を検索 | [ConfigController.cs](../FortnitePorting/Controllers/Assets/ConfigController.cs) |
| GET | `/api/v1/cosmetics` | コスメを一覧・検索 | [CosmeticsController.cs](../FortnitePorting/Controllers/Assets/CosmeticsController.cs) |
| GET | `/api/v1/cosmetics/{id}` | IDでコスメを1件取得 | [CosmeticsController.cs](../FortnitePorting/Controllers/Assets/CosmeticsController.cs) |
| GET | `/api/v1/cosmetics/{id}/icon` | コスメのアイコンをPNGで取得 | [CosmeticsController.Icons.cs](../FortnitePorting/Controllers/Assets/CosmeticsController.Icons.cs) |
| GET | `/api/v1/export` | アセットをエクスポート | [ExportController.cs](../FortnitePorting/Controllers/Assets/ExportController.cs) |
| GET | `/api/v1/export/audioinfo` | 音声アセット情報 | [ExportController.Audio.cs](../FortnitePorting/Controllers/Assets/ExportController.Audio.cs) |
| POST | `/api/v1/export/batch` | アセットを一括エクスポート | [ExportController.cs](../FortnitePorting/Controllers/Assets/ExportController.cs) |
| GET | `/api/v1/export/datatable` | DataTable/CurveTableをCSVで取得 | [ExportController.Tables.cs](../FortnitePorting/Controllers/Assets/ExportController.Tables.cs) |
| GET | `/api/v1/files` | 仮想ファイルを一覧 | [FilesController.cs](../FortnitePorting/Controllers/Assets/FilesController.cs) |
| GET | `/api/v1/items/properties` | アイテム情報を抽出 | [ItemsController.cs](../FortnitePorting/Controllers/Assets/ItemsController.cs) |
| GET | `/api/v1/localization` | ローカライズを取得・検索 | [LocalizationController.cs](../FortnitePorting/Controllers/Assets/LocalizationController.cs) |
| GET | `/api/v1/localization/languages` | 利用可能な言語を一覧 | [LocalizationController.cs](../FortnitePorting/Controllers/Assets/LocalizationController.cs) |
| GET | `/api/v1/paks` | PAK・UTOCを一覧 | [PakController.cs](../FortnitePorting/Controllers/Assets/PakController.cs) |
| GET | `/api/v1/paks/{pakName}/files` | PAK内ファイルをページング | [PakController.cs](../FortnitePorting/Controllers/Assets/PakController.cs) |

### ビルド

| メソッド | パス | 処理 | ソース |
|---|---|---|---|
| GET | `/api/v1/aes` | MainAESキーを取得 | [AesController.cs](../FortnitePorting/Controllers/Builds/AesController.cs) |
| GET | `/api/v1/aes/binaries` | AES対象バイナリを一覧 | [AesController.cs](../FortnitePorting/Controllers/Builds/AesController.cs) |
| GET | `/api/v1/aes/extract` | AESキー抽出を実行 | [AesController.cs](../FortnitePorting/Controllers/Builds/AesController.cs) |
| GET | `/api/v1/aes/finder/selftest` | AES Finderを自己診断 | [AesController.cs](../FortnitePorting/Controllers/Builds/AesController.cs) |
| GET | `/api/v1/aes/keys` | アーカイブのAESキーを取得 | [AesController.Keys.cs](../FortnitePorting/Controllers/Builds/AesController.Keys.cs) |
| GET | `/api/v1/aes/local` | ローカルビルドのAESを取得 | [AesController.cs](../FortnitePorting/Controllers/Builds/AesController.cs) |
| GET | `/api/v1/aes/scan/local` | ローカルバイナリをスキャン | [AesController.cs](../FortnitePorting/Controllers/Builds/AesController.cs) |
| GET | `/api/v1/backup` | バックアップを取得 | [BackupController.cs](../FortnitePorting/Controllers/Builds/BackupController.cs) |
| GET | `/api/v1/build` | 現在のビルド状態を取得 | [BuildController.cs](../FortnitePorting/Controllers/Builds/BuildController.cs) |
| POST | `/api/v1/build/reload` | 最新ビルドへ再読み込み | [BuildController.cs](../FortnitePorting/Controllers/Builds/BuildController.cs) |
| GET | `/api/v1/changes` | 記録済みの変更リストを一覧 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| DELETE | `/api/v1/changes` | 記録済み変更リストを削除 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| POST | `/api/v1/changes/compute` | 変更リストを計算して記録 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| GET | `/api/v1/changes/file` | ファイルの変わった行を取得 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| GET | `/api/v1/changes/jobs` | 計算ジョブを一覧 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| GET | `/api/v1/changes/jobs/{id}` | 計算ジョブの進捗を取得 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| DELETE | `/api/v1/changes/jobs/{id}` | 計算ジョブを中止 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| GET | `/api/v1/changes/list` | ビルド間の変更ファイルを取得 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| GET | `/api/v1/changes/modified` | 実際に書き換わったファイルパスを取得 | [ChangesController.cs](../FortnitePorting/Controllers/Builds/ChangesController.cs) |
| GET | `/api/v1/local` | ローカルインストールを確認 | [LocalController.cs](../FortnitePorting/Controllers/Builds/LocalController.cs) |
| POST | `/api/v1/local/mount` | ローカルビルドをマウント | [LocalController.cs](../FortnitePorting/Controllers/Builds/LocalController.cs) |
| DELETE | `/api/v1/local/mount` | ローカルビルドを解放 | [LocalController.cs](../FortnitePorting/Controllers/Builds/LocalController.cs) |
| GET | `/api/v1/versions` | 既知のビルドを一覧 | [VersionsController.cs](../FortnitePorting/Controllers/Builds/VersionsController.cs) |
| DELETE | `/api/v1/versions/data` | 旧バージョンのデータを削除 | [VersionsController.cs](../FortnitePorting/Controllers/Builds/VersionsController.cs) |
| POST | `/api/v1/versions/import` | マニフェストからビルドを取り込む | [VersionsController.cs](../FortnitePorting/Controllers/Builds/VersionsController.cs) |
| GET | `/api/v1/versions/keys` | 履歴ビルドのキーを取得 | [VersionsController.cs](../FortnitePorting/Controllers/Builds/VersionsController.cs) |
| POST | `/api/v1/versions/keys` | 履歴ビルドのキーを設定 | [VersionsController.cs](../FortnitePorting/Controllers/Builds/VersionsController.cs) |
| POST | `/api/v1/versions/load` | アーカイブ済みビルドを読み込む | [VersionsController.cs](../FortnitePorting/Controllers/Builds/VersionsController.cs) |
| DELETE | `/api/v1/versions/unload` | アーカイブ済みビルドを解放 | [VersionsController.cs](../FortnitePorting/Controllers/Builds/VersionsController.cs) |

### ツール

| メソッド | パス | 処理 | ソース |
|---|---|---|---|
| GET | `/api/v1/mappings` | 保存済みマッピングを一覧 | [MappingsController.cs](../FortnitePorting/Controllers/Tools/MappingsController.cs) |
| POST | `/api/v1/mappings/generate` | UEFNのDLLからusmapを生成 | [MappingsController.cs](../FortnitePorting/Controllers/Tools/MappingsController.cs) |
| POST | `/api/v1/mappings/import` | マッピングを取り込み | [MappingsController.cs](../FortnitePorting/Controllers/Tools/MappingsController.cs) |
| GET | `/api/v1/mappings/uefn` | マッピング生成の実行可否 | [MappingsController.cs](../FortnitePorting/Controllers/Tools/MappingsController.cs) |
| GET | `/api/v1/mappings/{fileName}` | マッピングファイルを取得 | [MappingsController.cs](../FortnitePorting/Controllers/Tools/MappingsController.cs) |
| GET | `/api/v1/search` | パス・内容を検索 | [SearchController.cs](../FortnitePorting/Controllers/Tools/SearchController.cs) |

### システム

| メソッド | パス | 処理 | ソース |
|---|---|---|---|
| GET | `/api/v1/update` | 更新状況を確認 | [UpdateController.cs](../FortnitePorting/Controllers/System/UpdateController.cs) |
| POST | `/api/v1/update` | 最新リリースへ更新 | [UpdateController.cs](../FortnitePorting/Controllers/System/UpdateController.cs) |

Swaggerと `GET /`（Swaggerへのリダイレクト）はこの53操作に含めていません。
