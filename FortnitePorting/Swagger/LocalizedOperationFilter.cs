using System.Collections.Generic;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FortnitePorting.Swagger;

/// <summary>
/// Supplies concise Japanese and English operation/parameter descriptions for Swagger.
/// Keeping this text here makes the two OpenAPI documents readable even when controller XML
/// comments are written primarily for source-code users.
/// </summary>
public sealed class LocalizedOperationFilter : IOperationFilter
{
    private sealed record Localized(string Summary, string Description, string Returns);

    private static readonly Dictionary<string, (Localized Ja, Localized En)> Operations = new()
    {
        ["ExportController.Get"] = L("アセットをエクスポート", "アセットをJSON、テクスチャPNG、音声として取得します。langでFTextをローカライズできます。", "Export an asset", "Returns an asset as JSON by default, or PNG/audio when requested.", "アセットのエクスポート結果", "The exported JSON, image, or audio payload."),
        ["ExportController.Batch"] = L("アセットを一括エクスポート", "最大100件のアセットをJSONとして一度に取得します。画像・音声のバイナリは含めません。", "Batch-export assets", "Exports up to 100 asset packages as JSON in one request. Binary image/audio payloads are not embedded.", "一括エクスポート結果", "Per-path export results, including individual errors."),
        ["ExportController.GetAudioInfo"] = L("音声アセット情報", "音声形式、WAV変換可否、RAD Audioデコーダーの状態を返します。", "Inspect audio metadata", "Reports audio format and WAV conversion capability without returning the binary payload.", "音声メタデータ", "The audio metadata."),
        ["ExportController.GetDataTable"] = L("DataTable/CurveTableをCSVで取得", "DataTableとCurveTableを表計算ソフトでそのまま開けるCSV（またはJSON）として返します。CurveTableはキー1件につき1行の縦持ちです。", "Export a DataTable or CurveTable as CSV", "Returns a DataTable or CurveTable as spreadsheet-ready CSV (or JSON). A CurveTable is written in long form, one line per curve key.", "CSVまたはJSONのテーブル", "The table as CSV or JSON."),
        ["ExportController.GetLocres"] = L("ローカライズテーブルを取得", "指定言語のlocresを統合したnamespace/key/valueテーブルを返します。", "Get merged localization", "Merges loaded .locres entries for one language into a namespace/key/value table.", "統合ローカライズテーブル", "The merged localization data."),
        ["ExportController.GetLocresLanguages"] = L("利用可能な言語を一覧", "読み込み済みlocresから利用可能な言語コードを返します。", "List localization languages", "Lists language codes found in loaded .locres files.", "言語コード一覧", "Available language codes."),
        ["ExportController.GetFilePathsInPak"] = L("PAK内ファイルを一覧", "指定したPAKまたはチャンクに含まれる仮想ファイルパスを返します。", "List files in a PAK", "Lists virtual file paths contained in matching PAK/UTOC archives.", "PAK内ファイル一覧", "The matching archives and file paths."),
        ["PakController.GetPaks"] = L("マウント済みPAKを一覧", "このローカルAPIプロセスで現在マウントされているPAK/UTOCをページングして返します。", "List mounted PAKs", "Lists mounted PAK/UTOC archives in the local API process.", "マウント済みPAK一覧", "The paginated archive list."),
        ["PakController.GetFilesInPak"] = L("PAK内ファイルをページング", "指定したマウント済みPAK/UTOCに含まれるファイルをページングして返します。", "List files inside a mounted PAK", "Returns paginated virtual file paths inside a mounted PAK/UTOC archive.", "PAK内ファイル一覧", "The paginated file list."),
        ["CosmeticsController.GetCosmetics"] = L("PAK単位でコスメを抽出", "指定したPAKまたはチャンクからコスメ定義、表示名、アイコン、タグを抽出します。", "Extract cosmetics from a PAK", "Extracts cosmetic definitions and related offer display data from a specific PAK/chunk.", "コスメ抽出結果", "The paginated cosmetic results."),
        ["CosmeticsController.SearchCosmetics"] = L("全PAKからコスメを検索", "PAK名を指定せず、現在マウントされている全PAKからコスメIDまたはカテゴリを検索します。", "Search cosmetics across all mounted PAKs", "Searches cosmetic definitions across all mounted PAKs by ID/name fragment and optional category.", "コスメ検索結果", "The paginated cosmetic search results."),
        ["CosmeticsController.GetCosmeticById"] = L("IDでコスメを1件取得", "CID_/EID_などのアセット名、またはスキンID（例: HonestWasp）だけでコスメを1件返します。PAKを指定する必要はありません。", "Get one cosmetic by ID", "Returns a single cosmetic by asset name (CID_/EID_...) or by skin ID alone, without naming a PAK.", "コスメ1件", "The matched cosmetic."),
        ["CosmeticsController.GetCosmeticIcon"] = L("コスメのアイコンをPNGで取得", "コスメIDからアイコンを直接PNGで返します。variantでLargeIcon/Icon/OfferCatalogテクスチャを選べます。", "Get a cosmetic icon as PNG", "Returns the cosmetic's icon as a PNG. The variant parameter selects LargeIcon, Icon, or the OfferCatalog texture.", "アイコンPNG", "The icon PNG."),
        ["LocalizationController.GetLanguages"] = L("利用可能な言語を一覧", "マウント中のビルドが持つ.locresの言語コードを返します。", "List localization languages", "Lists the language codes the mounted build ships .locres files for.", "言語コード一覧", "Available language codes."),
        ["LocalizationController.Lookup"] = L("ローカライズ文字列を逆引き・対訳取得", "keyを指定すると全言語の対訳を、textを指定するとその文字列に対応するnamespace/keyを返します。", "Look up a localization key or string", "Resolves a key into every language, or finds the namespace/key behind a localized string.", "ローカライズ検索結果", "The matching localization entries."),
        ["AssetsController.GetDependencies"] = L("アセットの依存関係を取得", "アセットのハード参照・ソフト参照を解析し、指定深度まで依存アセットを返します。", "Get asset dependencies", "Returns best-effort hard and soft references from an asset package up to the requested depth.", "依存関係一覧", "Dependency entries and unresolved references."),
        ["ConfigController.GetFiles"] = L("INIファイルを一覧", "読み込み済みのConfig配下のテキストINIファイルを一覧します。", "List loaded INI files", "Lists loaded text-based .ini files under Config directories.", "INIファイル一覧", "The loaded INI paths."),
        ["ConfigController.Query"] = L("INI設定値を検索", "指定したINIのセクションとキーから設定値を取得します。", "Query an INI value", "Looks up a key in a section of a loaded text-based .ini file.", "INI検索結果", "The matching values, if any."),
        ["ItemsController.GetFiles"] = L("アイテムアセットを一覧", "WID_、AGID_、Athena_などの接頭辞に一致するアセットを一覧します。", "List item assets", "Lists asset paths matching one or more item-name prefixes.", "アイテムアセット一覧", "Matching paths and counts."),
        ["ItemsController.GetProperties"] = L("アイテム情報を抽出", "一致するアイテムアセットから表示名、Traits、LargeIconを抽出します。", "Extract item properties", "Extracts item name, traits, and icon data from matching item assets.", "アイテム抽出結果", "The extracted item data."),
        ["ItemsController.GetSingleProperties"] = L("単一アイテムを抽出", "指定したアセットから表示名、Traits、LargeIconを抽出します。", "Extract one item", "Extracts item properties from one asset path.", "アイテム抽出結果", "The extracted item data."),
        ["SearchController.Search"] = L("ファイルパスを検索", "読み込み済みファイルのパス・名前をcontains、prefix、regexなどで検索します。", "Search file paths", "Searches loaded virtual paths and names using contains, prefix, wildcard, regex, or token matching.", "ファイル検索結果", "Matching paths and pagination metadata."),
        ["SearchController.SearchContent"] = L("アセット内容を検索", "アセットのシリアライズ内容と設定・テキストファイルの内容を検索します。", "Search file contents", "Searches serialized asset exports and loaded text/config files for a string.", "内容検索結果", "Matching files and snippets."),
        ["DebugController.GetStats"] = L("読み込み済みファイルを一覧", "デバッグ用に読み込み済みの仮想ファイルパスをページングして返します。", "List loaded files", "Lists all currently loaded virtual file paths for diagnostics.", "読み込み済みファイル一覧", "The file paths and count."),
        ["DebugController.SearchFiles"] = L("読み込み済みファイルを検索", "デバッグ用にファイルパスを部分一致検索します。", "Search loaded files", "Searches loaded virtual file paths by substring.", "ファイル検索結果", "Matching file paths."),
        ["DebugController.GetMountedPaks"] = L("マウント済みPAKを確認", "デバッグ用にマウント済みPAK/UTOCを返します。", "Inspect mounted PAKs", "Returns mounted PAK/UTOC archives for diagnostics.", "マウント済みPAK一覧", "Mounted archive information."),
        ["DebugController.GetFilesInPak"] = L("PAK内ファイルを確認", "デバッグ用に指定PAKのファイルパスを返します。", "Inspect PAK files", "Returns file paths inside one mounted PAK for diagnostics.", "PAK内ファイル一覧", "The file paths."),
        ["MappingsController.Generate"] = L("マッピングからusmapを生成", "マッピングJSONをCUE4Parse用の.usmapへ変換します。", "Generate a usmap", "Converts a mappings JSON document into a CUE4Parse-compatible .usmap file.", "usmap生成結果", "The generated mapping or statistics."),
        ["MappingsController.Dump"] = L("ビルドからusmapをダンプ", "UnrealMappingsDumperと同じ手順（型の収集→.usmapへのシリアライズ）で、マウント中のビルドからマッピングを作成して配信します。Blueprint系の型はpakから収集し、ネイティブの/Script型は既存マッピングをマージして補完します。", "Dump a usmap from the mounted build", "Builds a mapping from the mounted build with the UnrealMappingsDumper procedure (collect the reflected types, then serialize them to .usmap). Blueprint types come from the paks; native /Script types are filled in by merging an existing mapping.", "ダンプした.usmapまたは統計", "The dumped .usmap binary or its statistics."),
        ["MappingsController.GetUefnStatus"] = L("UEFNダンプの実行可否", "起動中のUEFNからダンプできる状態かを返します。ダンパーDLLの有無と場所、注入できるUEFNプロセスの一覧、自動で選ばれる対象プロセス（target）、次に何をすればよいかのヒントを含みます。", "Check whether a UEFN dump can run", "Reports whether a dump from a running UEFN can start: where the dumper DLL is (or how to build it) and which UEFN processes are available to inject into.", "ダンパーDLLと対象プロセスの状態", "The dumper DLL and target process status."),
        ["MappingsController.DumpFromUefn"] = L("UEFNからusmapをダンプ", "起動中のUEFNへUnrealMappingsDumperのDLLを注入して.usmapを作成し、配信します。pak側のダンプと違いエンジンのリフレクション情報そのものを読むため、ネイティブの/Script型まで含んだマッピングが得られ、既存マッピングのマージが不要です。Windows専用で、UEFNが起動しきっている必要があります。APIはUEFNと同じWindowsユーザーで実行してください。", "Dump a usmap from a running UEFN", "Dumps a .usmap out of a running UEFN by injecting the UnrealMappingsDumper DLL. Unlike the pak-side dump this reads the engine's own reflection data, so the mapping covers native /Script types and needs no base mapping merged under it. Windows only; UEFN has to be running and fully loaded, and the API has to run as the same Windows user.", "ダンプした.usmapまたは統計", "The dumped .usmap binary or its statistics."),
        ["MappingsController.ListMappings"] = L("保存済みマッピングを一覧", "このインスタンスが保持する.usmapファイル（ダンプ・生成・ダウンロード）を新しい順に一覧します。", "List stored mappings", "Lists the .usmap files this instance holds (dumped, generated, or downloaded), newest first.", "マッピング一覧", "The stored mapping files."),
        ["MappingsController.DownloadMapping"] = L("マッピングファイルを取得", "保存済みの.usmapファイルをファイル名を指定して配信します。", "Download a mapping file", "Serves one stored .usmap file by name.", ".usmapバイナリ", "The .usmap binary."),
        ["AesController.Aes"] = L("MainAESキーを取得", "UEFN Common DLLからMainAESキーを抽出し、必要に応じてローカルVFSへ適用します。", "Extract the MainAES key", "Extracts the MainAES key from the UEFN Common DLL and optionally applies it to the local provider.", "AES抽出結果", "The extracted key and mount result."),
        ["AesController.Extract"] = L("AESキー抽出を実行", "スケジュール済みバイナリからAESキー候補を抽出します。", "Extract AES keys", "Extracts AES key candidates from the configured manifest binary.", "AES抽出結果", "Extracted keys and verification information."),
        ["AesController.SelfTest"] = L("AES Finderを自己診断", "外部AES Finderの設定と実行可否を確認します。", "Run the AES Finder self-test", "Checks whether the external AES Finder can be located and executed.", "自己診断結果", "The self-test result."),
        ["AesController.ScanLocal"] = L("ローカルバイナリをスキャン", "指定したローカルファイルまたはディレクトリからAESキー候補を抽出します。", "Scan local binaries", "Scans a local file or directory for AES key candidates.", "ローカルスキャン結果", "Extracted key candidates."),
        ["BuildController.GetBuild"] = L("現在のビルド状態を取得", "配信中のビルド、マニフェストのビルド、再読み込みの進行状況を返します。", "Get the current build state", "Reports the mounted build, the build the manifest points at, and the reload progress.", "ビルド状態", "The current build and reload state."),
        ["BuildController.Reload"] = L("最新ビルドへ再読み込み", "ポーリングを待たずに最新マニフェストでプロバイダーを再構築します。再構築中は他のエンドポイントが503を返します。", "Reload the newest build", "Rebuilds the provider from the newest manifest without waiting for the poll. Other endpoints return 503 while it runs.", "再読み込み結果", "The reload result."),
        ["AesController.Binaries"] = L("AES対象バイナリを一覧", "現在のマニフェストに含まれるAESスキャン対象バイナリを一覧します。", "List AES binaries", "Lists binaries available for AES extraction from the current manifest.", "バイナリ一覧", "The available binaries."),
        ["BackupController.GetInfo"] = L("バックアップ内容を確認", "生成される.fbkpの件数、バージョン、ファイル名を実際に生成せずに返します。", "Inspect the backup", "Reports the entry count, version, and file name of the backup without generating it.", "バックアップ情報", "The backup metadata."),
        ["BackupController.Download"] = L("FModel用.fbkpを取得", "現在マウント中のビルドのファイル一覧をFModelのバックアップ形式(.fbkp)で返します。ファイル名はビルド名(例: FortniteGame_42_00.fbkp)になります。", "Download an FModel .fbkp backup", "Returns the mounted build's file list as an FModel backup (.fbkp), named after that build (for example FortniteGame_42_00.fbkp).", ".fbkpバイナリ", "The .fbkp binary."),
        ["UpdateController.GetStatus"] = L("更新状況を確認", "実行中のバージョンとGitHubの最新リリースを比較し、更新可能かどうかを返します。", "Check for updates", "Compares the running version with the newest GitHub release and reports whether an update applies.", "更新状況", "The current and latest version, and whether an update applies."),
        ["UpdateController.Apply"] = L("最新リリースへ更新", "最新リリースをダウンロード・展開して差し替え、プロセスを終了します（既定では自動で再起動します）。", "Install the newest release", "Downloads, stages, and swaps in the newest release, then shuts this process down (it restarts automatically by default).", "更新結果", "Whether the update was staged and what happens next."),
        ["VersionsController.GetVersions"] = L("既知のビルドを一覧", "このインスタンスが把握しているビルドと、それぞれが今も読み取れるかどうかを返します。マニフェストを保管してあるビルドは load で復帰させられます。", "List known builds", "Lists every build this instance knows about and whether it can still be read. A build whose manifest is retained can be brought back with the load endpoint.", "ビルド一覧と保持状況", "The known builds and their retention state."),
        ["VersionsController.Import"] = L("マニフェストからビルドを取り込む", "手元のマニフェストファイル（アップロード、またはサーバー上のパス）をアーカイブに追加し、このインスタンスが配信していないビルドも読み取り・比較の対象にします。", "Import a build from a manifest", "Adds a build to the archive from an uploaded manifest file, or one already on the server, so a build this instance never served becomes readable and comparable.", "取り込んだビルド", "The imported build."),
        ["VersionsController.Load"] = L("アーカイブ済みビルドを読み込む", "保管してあるマニフェストからそのビルドをマウントし、最新ビルドと並べて読めるようにします。Fortnite 1ビルド分のメモリを使うため、同時に保持できる数は HISTORICAL_BUILDS_MAX で制限されます。", "Mount an archived build", "Mounts a build from its archived manifest so it can be read alongside the live one. A mounted build costs a whole build's worth of memory, so how many may exist at once is capped by HISTORICAL_BUILDS_MAX.", "マウント結果", "The mount result."),
        ["VersionsController.Unload"] = L("アーカイブ済みビルドを解放", "マウント中のビルドを解放してメモリを空けます。マニフェストは残るので、後からもう一度読み込めます。", "Unmount an archived build", "Unmounts a build and frees its memory. Its archived manifest is kept, so it can be mounted again later.", "解放結果", "Whether the build was unmounted."),
        ["VersionsController.DeleteData"] = L("旧バージョンのデータを削除", "そのビルドのマニフェスト・AESキー・チャンクキャッシュを削除します。アップデート時の「古いバージョンを削除する」手順にあたります。記録済みの変更リストは残るため、ファイルは読めなくなっても変更履歴は残ります。", "Delete an old build's data", "Deletes that build's manifest, archived keys and chunk cache — the \"delete the old version\" step of an update. Recorded changelists are kept, so its history stays queryable even though its files no longer are.", "削除結果", "The deletion result."),
        ["VersionsController.GetFiles"] = L("指定ビルドのファイルを一覧", "指定したビルドが持つ仮想ファイルパスをページングして返します。", "List one build's files", "Returns the virtual file paths of the build you name, paginated.", "ファイルパス一覧", "The paginated file paths."),
        ["VersionsController.GetFile"] = L("指定ビルドのファイル内容を取得", "「++Fortnite+Release-42.10-CL-57566230-Windows」のようにビルドを指定して、そのビルド時点でのファイル内容を返します。42.10 や previous・latest でも指定できます。既定では読み込み済みのビルドのみが対象で、load=true を付けるとその場でマウントします。", "Read a file from one build", "Returns a file's content as it is in the build you name, for example ++Fortnite+Release-42.10-CL-57566230-Windows (42.10, previous and latest also work). Only already-loaded builds are served by default; load=true mounts the build first.", "そのビルドでのファイル内容", "The file's content in that build."),
        ["ChangesController.GetRecorded"] = L("記録済みの変更リストを一覧", "このインスタンスに記録されているビルド間の変更リストを一覧します。", "List recorded changelists", "Lists the build-to-build changelists recorded on this instance.", "変更リスト一覧", "The recorded changelists."),
        ["ChangesController.GetChangelist"] = L("ビルド間の変更ファイルを取得", "2つのビルド間で追加・削除・変更されたファイルパスを返します。直接記録されていない組み合わせは、記録済みの連鎖から合成します（v40→v41 と v41→v42 から v40→v42 を作る）。", "Get the files that changed between two builds", "Returns the file paths added, removed or modified between two builds. A pair that was never recorded directly is composed out of the recorded chain, so v40→v41 plus v41→v42 yields v40→v42.", "変更ファイル一覧", "The paginated changelist."),
        ["ChangesController.GetFileChanges"] = L("ファイルの変わった行を取得", "1つのファイルについて、2つのビルド間で変わった行を返します。テキストはそのまま、.uasset/.umap はJSONエクスポートを介して比較するため、アセットでも「変わった行」が意味を持ちます。format=patch で unified diff テキストになります。", "Get the lines that changed in one file", "Returns the lines that changed in a single file between two builds. Text is diffed as-is and a .uasset/.umap through its JSON export, which is what makes \"changed lines\" meaningful for an asset. format=patch returns unified-diff text.", "行単位の差分", "The line-level diff."),
        ["ChangesController.Compute"] = L("変更リストを計算して記録", "2つのビルドを比較して変更リストを記録します。古い方のビルドをマウントする必要があるためバックグラウンドジョブとして実行し、jobId で進捗を確認します。quick はパス・サイズ・格納アーカイブのみで内容を読まず、full は同サイズのファイルも両側でハッシュして厳密に判定します。", "Compute and record a changelist", "Compares two builds and records the changelist. It runs as a background job because the older build has to be mounted; poll it with the returned jobId. quick uses path, size and containing archive without reading content; full additionally hashes same-size files on both sides.", "開始したジョブ", "The started job."),
        ["ChangesController.GetJobs"] = L("計算ジョブを一覧", "このインスタンスで開始された変更リスト計算ジョブを一覧します。", "List changelist jobs", "Lists the changelist computations started on this instance.", "ジョブ一覧", "The jobs."),
        ["ChangesController.GetJob"] = L("計算ジョブの進捗を取得", "変更リスト計算ジョブの状態と進捗を返します。", "Get a changelist job", "Returns the state and progress of one changelist computation.", "ジョブの状態", "The job state and progress."),
        ["ChangesController.CancelJob"] = L("計算ジョブを中止", "実行中の変更リスト計算を中止します。", "Cancel a changelist job", "Cancels a running changelist computation.", "中止結果", "Whether the job was cancelled."),
        ["ChangesController.DeleteChangelist"] = L("記録済み変更リストを削除", "記録されている変更リストを1件削除します。", "Delete a recorded changelist", "Deletes one recorded changelist.", "削除結果", "Whether it was deleted.")
    };

    private static (Localized Ja, Localized En) L(
        string jaSummary, string jaDescription, string enSummary, string enDescription,
        string jaReturns, string enReturns)
        => (new(jaSummary, jaDescription, jaReturns), new(enSummary, enDescription, enReturns));

    private static readonly Dictionary<string, Dictionary<string, (string Ja, string En)>> Parameters = new()
    {
        ["ExportController.Get"] = P(("path", "アセットの仮想パス。", "Asset virtual path."), ("image", "テクスチャをPNGで返すか。", "Return a texture as PNG."), ("audio", "音声をバイナリで返すか。", "Return a sound payload."), ("lang", "FTextの言語コード。例: ja。", "Localization language code, for example ja."), ("hotfix", "cloudstorageのホットフィックス（[AssetHotfix]のDataTable/CurveTable書き換えとFTextのテキスト差し替え）を適用した内容を返すか。既定はfalse。", "Return the asset with the live cloudstorage hotfixes applied — [AssetHotfix] table/curve edits and FText replacements; default false.")),
        ["ExportController.Batch"] = P(("request", "pathsとlangを含むJSONリクエスト。最大100件。", "JSON request containing paths and lang; maximum 100 paths."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["ExportController.GetAudioInfo"] = P(("path", "音声アセットの仮想パス。", "Sound asset virtual path.")),
        ["ExportController.GetLocres"] = P(("lang", "言語コード。例: ja。", "Language code, for example ja.")),
        ["ExportController.GetFilePathsInPak"] = P(("pakName", "PAK名またはチャンク番号。", "PAK name or chunk number.")),
        ["PakController.GetPaks"] = P(("q", "PAK名・パスの任意の絞り込み。", "Optional archive name/path filter."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大200。", "Items per page; maximum 200.")),
        ["PakController.GetFilesInPak"] = P(("pakName", "PAK名または名前の一部。", "PAK name or name fragment."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大10000。", "Items per page; maximum 10000.")),
        ["CosmeticsController.GetCosmetics"] = P(("pakName", "PAK名またはチャンク番号。", "PAK name or chunk number."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大200。", "Items per page; maximum 200."), ("lang", "表示名の言語コード。", "Localization language code.")),
        ["CosmeticsController.SearchCosmetics"] = P(("q", "コスメIDまたは名前の一部。", "Cosmetic ID or name fragment."), ("category", "カテゴリ接頭辞。例: Character。", "Category prefix, for example Character."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大200。", "Items per page; maximum 200."), ("lang", "表示名の言語コード。", "Localization language code.")),
        ["AssetsController.GetDependencies"] = P(("path", "対象アセットの仮想パス。", "Asset virtual path."), ("depth", "再帰深度。0〜3、既定値1。", "Recursion depth, 0-3; default 1."), ("limit", "依存関係の最大件数。最大500。", "Maximum dependency entries; max 500."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["ExportController.GetDataTable"] = P(("path", "DataTable/CurveTableアセットの仮想パス。", "DataTable / CurveTable virtual path."), ("format", "csv（既定）またはjson。", "csv (default) or json."), ("rows", "取得する行名のCSV。省略時は全行。", "Comma-separated row names to keep; every row when omitted."), ("delimiter", "区切り文字。comma（既定）/tab/semicolon/pipe、または1文字。", "Delimiter: comma (default), tab, semicolon, pipe, or a single character."), ("flatten", "ネストしたプロパティをドット区切りの列に展開するか。既定はtrue。", "Flatten nested properties into dotted columns; default true."), ("bom", "ExcelがUTF-8として開けるようBOMを付けるか。既定はtrue。", "Prefix a UTF-8 BOM so Excel reads the file as UTF-8; default true."), ("download", "Content-Dispositionを付けてダウンロードさせるか。既定はtrue。", "Send Content-Disposition so the CSV downloads; default true."), ("hotfix", "cloudstorageの[AssetHotfix]による行・カーブ書き換えを適用するか。", "Apply the live cloudstorage [AssetHotfix] row/curve edits.")),
        ["CosmeticsController.GetCosmeticById"] = P(("id", "コスメIDまたはアセット名。", "Cosmetic ID or asset name."), ("lang", "表示名の言語コード。", "Localization language code.")),
        ["CosmeticsController.GetCosmeticIcon"] = P(("id", "コスメIDまたはアセット名。", "Cosmetic ID or asset name."), ("variant", "large（既定）/small/offercatalog。largeとsmallは他方とOfferCatalogにフォールバックします。", "large (default) / small / offercatalog; large and small fall back to the other icon and then OfferCatalog.")),
        ["LocalizationController.Lookup"] = P(("key", "解決するFTextのKey。", "FText key to resolve."), ("namespace", "Keyの所属namespace。省略時は全namespaceを検索。", "Namespace the key belongs to; every namespace when omitted."), ("text", "逆引きする表示文字列。keyとの併用は不可。", "Localized string to reverse-look-up; cannot be combined with key."), ("mode", "逆引きの一致方法。contains（既定）/exact/prefix/suffix/regex。", "Reverse-lookup match mode: contains (default) / exact / prefix / suffix / regex."), ("lang", "対象言語を1つ指定。既定は全言語。", "Single language to use; defaults to every available language."), ("langs", "対象言語のCSV。allまたは*で全言語。", "Comma-separated languages; all or * selects every language."), ("caseSensitive", "大文字小文字を区別するか。", "Case-sensitive matching."), ("withTranslations", "逆引き結果を全言語の対訳付きで返すか。", "Resolve each reverse-lookup hit into every available language."), ("maxResults", "逆引きの最大件数。既定50、最大500。", "Maximum reverse-lookup entries; default 50, max 500.")),
        ["ConfigController.GetFiles"] = P(("q", "ファイル名またはパスの絞り込み。", "Optional file-name/path filter.")),
        ["ConfigController.Query"] = P(("file", "INIファイル名または仮想パス。", "INI file name or virtual path."), ("section", "セクション名。[]は省略可能。", "Section name; brackets are optional."), ("key", "設定キー名。", "Configuration key name.")),
        ["ItemsController.GetFiles"] = P(("prefixes", "接頭辞のCSV。", "Comma-separated prefixes."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。", "Items per page."), ("ext", "拡張子フィルター。", "Extension filter.")),
        ["ItemsController.GetProperties"] = P(("prefixes", "接頭辞のCSV。", "Comma-separated prefixes."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。", "Items per page.")),
        ["ItemsController.GetSingleProperties"] = P(("path", "対象アセットの仮想パス。", "Asset virtual path.")),
        ["DebugController.GetStats"] = P(("page", "ページ番号。", "1-based page number.")),
        ["DebugController.SearchFiles"] = P(("query", "検索文字列。", "Search string.")),
        ["DebugController.GetFilesInPak"] = P(("pakName", "PAKファイル名。", "PAK file name.")),
        ["SearchController.Search"] = P(("q", "検索文字列。", "Search string."), ("mode", "contains/prefix/suffix/exact/wildcard/regex/tokens。", "Match mode."), ("field", "path/name/stem。", "Search field."), ("caseSensitive", "大文字小文字を区別するか。", "Case-sensitive matching."), ("ext", "拡張子CSV。", "Comma-separated extensions."), ("dir", "検索対象ディレクトリ。", "Directory prefix."), ("dedupe", "Cooked重複をまとめるか。", "Collapse cooked duplicates."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。", "Items per page."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["SearchController.SearchContent"] = P(("q", "内容検索文字列。", "Content query."), ("dir", "検索対象ディレクトリ。", "Directory prefix."), ("pathContains", "候補パスの追加絞り込み。", "Additional path filter."), ("ext", "候補拡張子CSV。", "Candidate extensions."), ("caseSensitive", "大文字小文字を区別するか。", "Case-sensitive matching."), ("maxScan", "走査する最大ファイル数。", "Maximum candidates to scan."), ("maxResults", "返却する最大件数。", "Maximum results."), ("snippetsPerFile", "ファイルごとのスニペット数。", "Snippets per file."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["MappingsController.Generate"] = P(("url", "取得するマッピングJSONのURL。", "URL of the mappings JSON to download."), ("path", "ローカルのマッピングJSONファイル。", "Local mappings JSON file path."), ("fileName", "生成する.usmapのファイル名。", "Output .usmap file name."), ("load", "生成後にローカルプロバイダーへ読み込むか。", "Load the generated mapping into the local provider."), ("verify", "生成後の.usmapを再解析して検証するか。", "Parse and verify the generated .usmap."), ("download", "生成した.usmapをバイナリで返すか。", "Return the generated .usmap binary.")),
        ["MappingsController.Dump"] = P(("path", "走査対象を絞り込むパス断片。例: FortniteGame/Content/Athena。", "Path fragment restricting which packages are scanned, for example FortniteGame/Content/Athena."), ("maxPackages", "開くパッケージの上限。0でビルド全体（非常に低速）。", "Maximum packages to open; 0 scans the whole build (very slow)."), ("timeoutSeconds", "走査の制限時間。超過時はそこまでの収集結果を書き出します。", "Scan budget; whatever was collected is serialized when it expires."), ("merge", "既存マッピングをマージしてネイティブ/Script型を残すか。既定はtrue。", "Merge an existing mapping so native /Script types are kept; default true."), ("baseMapping", "マージ元の.usmapファイル名（mappings/内）または絶対パス。", "Base .usmap file name inside mappings/ or an absolute path."), ("version", "出力する.usmapのバージョン。0はUnrealMappingsDumper準拠、4が最新。", "usmap version to write: 0 matches UnrealMappingsDumper, 4 is the latest."), ("compression", "none（既定）またはzstd。OodleとBrotliの圧縮器は利用できません。", "none (default) or zstd; Oodle and Brotli compressors are unavailable here."), ("fileName", "出力ファイル名。既定は{build}_dumped.usmap。", "Output file name; defaults to {build}_dumped.usmap."), ("load", "ダンプしたマッピングをプロバイダーへ読み込むか。", "Hot-load the dumped mapping into the provider."), ("download", ".usmapをバイナリで返すか。falseで統計JSON。", "Return the .usmap binary; false returns JSON statistics."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["MappingsController.DumpFromUefn"] = P(("pid", "対象のUEFNプロセスID。省略すると自動で特定します。", "Target UEFN process id; auto-detected when omitted."), ("compression", "none（既定）またはoodle。Oodleはゲーム内のエンコーダを使うため指定できます。", "none (default) or oodle. Oodle runs inside the game, which has the encoder."), ("fileName", "出力ファイル名。既定は{build}_uefn.usmap。", "Output file name; defaults to {build}_uefn.usmap."), ("console", "UEFN内にダンパーのコンソールウィンドウを開くか。既定はfalse。失敗の切り分けに使います。", "Let the dumper open a console window inside UEFN; default false. Useful when debugging a failure."), ("timeoutSeconds", "DLLの読み込み後、ダンプ完了を待つ秒数。既定は120。", "How long to wait for the dump after the DLL is loaded; default 120."), ("load", "ダンプしたマッピングをプロバイダーへ読み込むか。", "Hot-load the dumped mapping into the provider."), ("download", ".usmapをバイナリで返すか。falseで統計JSON。", "Return the .usmap binary; false returns JSON statistics."), ("gobjects", "GObjectsのモジュール相対アドレス（16進）。ダンパーのシグネチャ走査がこのビルドで失敗する場合に指定します。", "Hex module-relative address of GObjects, for builds where the dumper's signature scan fails."), ("fnameToString", "FNameToStringのモジュール相対アドレス（16進）。gobjectsと同じ用途です。", "Hex module-relative address of FNameToString; same fallback as gobjects."), ("probeSignatures", "署名走査で見つかったアドレスを実際に呼び、FNameToStringかどうか判定させるか。既定はfalse。誤った関数を呼ぶとUEFNが落ちる可能性があります。", "Let the dumper call signature hits to identify FNameToString. Off by default: calling the wrong function can crash UEFN."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["MappingsController.DownloadMapping"] = P(("fileName", "GET /api/v1/mappings が返すファイル名。", "File name as listed by GET /api/v1/mappings.")),
        ["AesController.Aes"] = P(("force", "Common DLLのキャッシュを無視して再取得するか。", "Re-download the Common DLL instead of using its cache."), ("noApi", "外部AES APIを使わずバイナリから候補を選ぶか。", "Skip the external AES API and select from binary candidates."), ("submit", "抽出キーをローカルプロバイダーへ適用するか。", "Submit the extracted key to the local provider."), ("ct", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["AesController.Extract"] = P(("verify", "抽出キーを外部AES APIと照合するか。", "Cross-check extracted keys against the external AES API."), ("force", "バイナリのキャッシュを無視して再取得するか。", "Re-download the binary instead of using its cache."), ("file", "スキャン対象にする別バイナリの名前またはパス末尾。", "Alternate binary name or path suffix to scan."), ("ct", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["AesController.ScanLocal"] = P(("path", "スキャンするローカルファイル。", "Local file to scan."), ("dir", "スキャンするローカルディレクトリ。", "Local directory to scan."), ("verify", "検出キーを外部AES APIと照合するか。", "Cross-check detected keys against the external AES API."), ("ct", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["BackupController.GetInfo"] = P(("includePayloads", ".uexp/.ubulk等のペイロードを含めるか。FModelは除外します。", "Include .uexp/.ubulk payload files; FModel excludes them.")),
        ["BackupController.Download"] = P(("includePayloads", ".uexp/.ubulk等のペイロードを含めるか。FModelは除外します。", "Include .uexp/.ubulk payload files; FModel excludes them."), ("compress", "FModelと同じLZ4フレームで圧縮するか。既定はtrue。", "Wrap the body in the LZ4 frame FModel writes; default true."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["UpdateController.GetStatus"] = P(("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["UpdateController.Apply"] = P(("force", "適用に失敗したバージョンの再試行抑止を解除するか。", "Ignore the guard that suppresses retrying a version which previously failed to apply."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["VersionsController.Import"] = P(("file", "アップロードするマニフェストファイル。", "Manifest file to upload."), ("path", "サーバー上にあるマニフェストファイルのパス。fileの代わりに使えます。", "Path of a manifest file already on the server; an alternative to file."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["VersionsController.Load"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["VersionsController.Unload"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest.")),
        ["VersionsController.DeleteData"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest.")),
        ["VersionsController.GetFiles"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("q", "パスの部分一致による絞り込み。", "Optional case-insensitive path filter."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大10000。", "Paths per page; maximum 10000."), ("load", "アーカイブ済みで未読み込みのビルドをその場でマウントするか。既定はfalse。", "Mount the build when it is archived but not loaded. Default false."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["VersionsController.GetFile"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("path", "仮想ファイルパス。拡張子は省略できます。", "Virtual file path, with or without its extension."), ("raw", "JSONエクスポートではなく格納されているバイト列を返すか。既定はfalse。", "Return the stored bytes instead of the JSON export; default false."), ("load", "アーカイブ済みで未読み込みのビルドをその場でマウントするか。既定はfalseで、未読み込みのビルドを指定したリクエストは数分かかるマウントを起動せずに拒否されます。", "Mount the build when it is archived but not loaded. Default false, so naming an unloaded build is refused rather than triggering a multi-minute mount."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["ChangesController.GetChangelist"] = P(("from", "古い方のビルド。ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Older build. Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("to", "新しい方のビルド。省略時はライブビルド。", "Newer build; defaults to the live build."), ("kind", "変更種別での絞り込み。added、removed、modified。省略時は全件。", "Filter by change type: added, removed or modified; all when omitted."), ("pathFilter", "パスの部分一致による絞り込み。", "Only return paths containing this fragment."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大10000。", "Entries per page; maximum 10000.")),
        ["ChangesController.GetFileChanges"] = P(("from", "古い方のビルド。ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Older build. Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("to", "新しい方のビルド。省略時はライブビルド。", "Newer build; defaults to the live build."), ("path", "差分を取るファイルの仮想パス。", "Virtual file path to diff."), ("format", "jsonで構造化されたハンク（既定）、patchでunified diffテキスト。", "json for structured hunks (default), or patch for unified-diff text."), ("context", "各ハンクの前後に付ける行数。0〜20、既定は3。", "Context lines around each hunk, 0 to 20; default 3."), ("load", "アーカイブ済みで未読み込みのビルドをその場でマウントするか。既定はtrue。", "Mount the build when it is archived but not loaded. Default true."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["ChangesController.Compute"] = P(("from", "古い方のビルド。ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Older build. Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("to", "新しい方のビルド。省略時はライブビルド。", "Newer build; defaults to the live build."), ("mode", "quickはパス・サイズ・格納アーカイブのみで内容を読みません。fullは同サイズのファイルを両側でハッシュします。既定はquick。", "quick uses path, size and containing archive without reading content; full hashes same-size files on both sides. Default quick."), ("pathFilter", "この接頭辞で始まるパスだけを比較します。mode=fullでは指定を強く推奨します。", "Restrict the comparison to paths starting with this prefix; strongly recommended with mode=full."), ("maxEntries", "記録する最大件数。1〜500000、既定は200000。", "Maximum entries to record, 1 to 500000; default 200000."), ("maxHashFiles", "fullモードでハッシュする同サイズファイルの上限。1〜200000、既定は5000。", "In full mode, the maximum number of same-size files to hash, 1 to 200000; default 5000."), ("unloadWhenDone", "ジョブがマウントしたビルドを完了時に解放するか。既定はtrue。", "Unmount the builds the job had to mount once it finishes; default true.")),
        ["ChangesController.GetJob"] = P(("id", "computeが返したジョブID。", "Job id returned by the compute endpoint.")),
        ["ChangesController.CancelJob"] = P(("id", "computeが返したジョブID。", "Job id returned by the compute endpoint.")),
        ["ChangesController.DeleteChangelist"] = P(("from", "記録された組み合わせの古い方のビルド。", "Older build of the recorded pair."), ("to", "記録された組み合わせの新しい方のビルド。", "Newer build of the recorded pair.")),
    };

    private static Dictionary<string, (string Ja, string En)> P(params (string Name, string Ja, string En)[] values)
        => values.ToDictionary(x => x.Name, x => (x.Ja, x.En));

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var key = $"{context.MethodInfo.DeclaringType?.Name}.{context.MethodInfo.Name}";
        var isJa = context.DocumentName == "ja";

        if (Operations.TryGetValue(key, out var localized))
        {
            var text = isJa ? localized.Ja : localized.En;
            operation.Summary = text.Summary;
            operation.Description = text.Description;
            foreach (var response in operation.Responses ?? new OpenApiResponses())
            {
                if (response.Key.StartsWith("2") && response.Value is OpenApiResponse openApiResponse)
                {
                    openApiResponse.Description = text.Returns;
                }
            }
        }

        if (Parameters.TryGetValue(key, out var parameterMap))
        {
            foreach (var parameter in operation.Parameters ?? [])
            {
                if (parameter is OpenApiParameter openApiParameter &&
                    !string.IsNullOrEmpty(openApiParameter.Name) &&
                    parameterMap.TryGetValue(openApiParameter.Name, out var description))
                {
                    openApiParameter.Description = isJa ? description.Ja : description.En;
                }
            }
        }

        // Request-body properties are not exposed as OpenAPI parameters. Localize the batch
        // request schema explicitly so the Japanese document does not fall back to English XML
        // comments for `paths` and `lang`.
        if (key == "ExportController.Batch" && operation.RequestBody != null)
        {
            operation.RequestBody.Description = isJa
                ? "一括エクスポートするアセットパスとローカライズ言語を指定します。最大100件です。"
                : "Specifies asset paths and the localization language for batch export. Maximum 100 paths.";

            foreach (var mediaType in operation.RequestBody.Content?.Values ?? [])
            {
                if (mediaType.Schema?.Properties == null) continue;
                foreach (var property in mediaType.Schema.Properties)
                {
                    if (property.Value == null) continue;
                    if (property.Key.Equals("paths", StringComparison.OrdinalIgnoreCase))
                    {
                        property.Value.Description = isJa
                            ? "エクスポート対象のアセット仮想パス一覧。最大100件。"
                            : "Asset virtual paths to export; maximum 100 paths.";
                    }
                    else if (property.Key.Equals("lang", StringComparison.OrdinalIgnoreCase))
                    {
                        property.Value.Description = isJa
                            ? "FTextに適用する言語コード。例: ja。"
                            : "Language code applied to FText values, for example ja.";
                    }
                    else if (property.Key.Equals("hotfix", StringComparison.OrdinalIgnoreCase))
                    {
                        property.Value.Description = isJa
                            ? "cloudstorageのホットフィックスを適用した内容を返すか。既定はfalse。"
                            : "Apply the live cloudstorage hotfixes to every result; default false.";
                    }
                }
            }
        }
    }
}
