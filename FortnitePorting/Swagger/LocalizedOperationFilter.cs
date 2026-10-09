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
        ["AesController.GetPakKeys"] = L("アーカイブのAESキーを取得", "現行ビルドのmainKey、dynamicKeys、unloadedを返します。", "Read archive AES keys", "Returns mainKey, dynamicKeys and unloaded archives for the live build.", "取得結果", "The result."),
        ["BackupController.Get"] = L("バックアップを取得", "format=jsonで内容情報、format=fbkpでFModel用ファイルを返します。", "Read a backup", "format=json returns metadata; format=fbkp downloads the generated FModel backup.", "取得結果", "The result."),
        ["FilesController.GetFiles"] = L("仮想ファイルを一覧", "全ファイルをページングして返します。prefixes、ext、除外条件で絞り込めます。", "List virtual files", "Returns every virtual file with optional name-prefix, extension and exclusion filters.", "取得結果", "The result."),
        ["ExportController.Get"] = L("アセットをエクスポート", "アセットをJSON、テクスチャPNG、音声として取得します。langでFTextをローカライズできます。", "Export an asset", "Returns an asset as JSON by default, or PNG/audio when requested.", "アセットのエクスポート結果", "The exported JSON, image, or audio payload."),
        ["ExportController.Batch"] = L("アセットを一括エクスポート", "最大100件のアセットをJSONとして一度に取得します。画像・音声のバイナリは含めません。", "Batch-export assets", "Exports up to 100 asset packages as JSON in one request. Binary image/audio payloads are not embedded.", "一括エクスポート結果", "Per-path export results, including individual errors."),
        ["ExportController.GetAudioInfo"] = L("音声アセット情報", "音声形式、WAV変換可否、RAD Audioデコーダーの状態を返します。", "Inspect audio metadata", "Reports audio format and WAV conversion capability without returning the binary payload.", "音声メタデータ", "The audio metadata."),
        ["ExportController.GetDataTable"] = L("DataTable/CurveTableをCSVで取得", "DataTableとCurveTableを表計算ソフトでそのまま開けるCSV（またはJSON）として返します。CurveTableはキー1件につき1行の縦持ちです。", "Export a DataTable or CurveTable as CSV", "Returns a DataTable or CurveTable as spreadsheet-ready CSV (or JSON). A CurveTable is written in long form, one line per curve key.", "CSVまたはJSONのテーブル", "The table as CSV or JSON."),
        ["PakController.GetPaks"] = L("PAK・UTOCを一覧", "state=mounted（既定）、unloaded、allで対象を選び、メタデータ付きで返します。", "List PAK and UTOC archives", "Select mounted (default), unloaded or all archives with state; metadata is included.", "取得結果", "The result."),
        ["PakController.GetFilesInPak"] = L("PAK内ファイルをページング", "指定したマウント済みPAK/UTOCに含まれるファイルをページングして返します。", "List files inside a mounted PAK", "Returns paginated virtual file paths inside a mounted PAK/UTOC archive.", "PAK内ファイル一覧", "The paginated file list."),
        ["CosmeticsController.SearchCosmetics"] = L("コスメを一覧・検索", "q、category、pakNameで絞り込みます。includeOffers=trueでバンドル表示アセットを含めます。", "List or search cosmetics", "Filter by q, category or pakName; includeOffers=true includes bundle display assets.", "取得結果", "The result."),
        ["CosmeticsController.GetCosmeticById"] = L("IDでコスメを1件取得", "CID_/EID_などのアセット名、またはスキンID（例: HonestWasp）だけでコスメを1件返します。PAKを指定する必要はありません。", "Get one cosmetic by ID", "Returns a single cosmetic by asset name (CID_/EID_...) or by skin ID alone, without naming a PAK.", "コスメ1件", "The matched cosmetic."),
        ["CosmeticsController.GetCosmeticIcon"] = L("コスメのアイコンをPNGで取得", "コスメIDからアイコンを直接PNGで返します。variantでLargeIcon/Icon/OfferCatalogテクスチャを選べます。", "Get a cosmetic icon as PNG", "Returns the cosmetic's icon as a PNG. The variant parameter selects LargeIcon, Icon, or the OfferCatalog texture.", "アイコンPNG", "The icon PNG."),
        ["LocalizationController.GetLanguages"] = L("利用可能な言語を一覧", "マウント中のビルドが持つ.locresの言語コードを返します。", "List localization languages", "Lists the language codes the mounted build ships .locres files for.", "言語コード一覧", "Available language codes."),
        ["LocalizationController.Lookup"] = L("ローカライズを取得・検索", "keyで対訳、textで逆引き、両方省略するとlangの統合テーブルを返します。", "Read or search localization", "Use key for translations, text for reverse lookup, or only lang for a merged table.", "取得結果", "The result."),
        ["AssetsController.GetDependencies"] = L("アセットの依存関係を取得", "アセットのハード参照・ソフト参照を解析し、指定深度まで依存アセットを返します。", "Get asset dependencies", "Returns best-effort hard and soft references from an asset package up to the requested depth.", "依存関係一覧", "Dependency entries and unresolved references."),
        ["ConfigController.GetFiles"] = L("INIファイルを一覧", "読み込み済みのConfig配下のテキストINIファイルを一覧します。", "List loaded INI files", "Lists loaded text-based .ini files under Config directories.", "INIファイル一覧", "The loaded INI paths."),
        ["ConfigController.Query"] = L("INI設定値を検索", "指定したINIのセクションとキーから設定値を取得します。", "Query an INI value", "Looks up a key in a section of a loaded text-based .ini file.", "INI検索結果", "The matching values, if any."),
        ["ItemsController.GetProperties"] = L("アイテム情報を抽出", "一致するアイテムアセットから表示名、Traits、LargeIconを抽出します。", "Extract item properties", "Extracts item name, traits, and icon data from matching item assets.", "アイテム抽出結果", "The extracted item data."),
        ["SearchController.Search"] = L("パス・内容を検索", "target=pathでパス検索、target=contentで内容検索を実行します。", "Search paths or contents", "Use target=path for path matching, or target=content to search file contents.", "取得結果", "The result."),
        ["MappingsController.Generate"] = L("UEFNのDLLからusmapを生成", "インストール済みUEFNのEngineとCommon DLLから.usmap version 4を生成します。UEFNの起動や既存マッピングは不要です。生成後に検証して保存します。", "Generate a usmap from UEFN DLLs", "Generates and verifies a version 4 .usmap from installed UEFN Engine and Common DLLs. Requires 64-bit Windows; UEFN does not need to be running.", "生成した.usmapまたは統計", "The generated .usmap or statistics."),
        ["MappingsController.GetUefnStatus"] = L("マッピング生成の実行可否", "生成ツールとUEFNのDLL、ビルド情報を確認します。", "Check mapping generation status", "Checks the generator executable, installed UEFN DLLs and build version.", "生成ツールとDLLの状態", "Generator and DLL status."),
        ["MappingsController.ListMappings"] = L("保存済みマッピングを一覧", "このインスタンスが保持する.usmapファイル（ダンプ・生成・ダウンロード）を新しい順に一覧します。", "List stored mappings", "Lists the .usmap files this instance holds (dumped, generated, or downloaded), newest first.", "マッピング一覧", "The stored mapping files."),
        ["MappingsController.DownloadMapping"] = L("マッピングファイルを取得", "保存済みの.usmapファイルをファイル名を指定して配信します。", "Download a mapping file", "Serves one stored .usmap file by name.", ".usmapバイナリ", "The .usmap binary."),
        ["AesController.Aes"] = L("MainAESキーを取得", "UEFN Common DLLからMainAESキーを抽出し、必要に応じてローカルVFSへ適用します。", "Extract the MainAES key", "Extracts the MainAES key from the UEFN Common DLL and optionally applies it to the local provider.", "AES抽出結果", "The extracted key and mount result."),
        ["AesController.Extract"] = L("AESキー抽出を実行", "スケジュール済みバイナリからAESキー候補を抽出します。", "Extract AES keys", "Extracts AES key candidates from the configured manifest binary.", "AES抽出結果", "Extracted keys and verification information."),
        ["AesController.SelfTest"] = L("AES Finderを自己診断", "外部AES Finderの設定と実行可否を確認します。", "Run the AES Finder self-test", "Checks whether the external AES Finder can be located and executed.", "自己診断結果", "The self-test result."),
        ["AesController.ScanLocal"] = L("ローカルバイナリをスキャン", "指定したローカルファイルまたはディレクトリからAESキー候補を抽出します。", "Scan local binaries", "Scans a local file or directory for AES key candidates.", "ローカルスキャン結果", "Extracted key candidates."),
        ["BuildController.GetBuild"] = L("現在のビルド状態を取得", "配信中のビルド、マニフェストのビルド、再読み込みの進行状況を返します。", "Get the current build state", "Reports the mounted build, the build the manifest points at, and the reload progress.", "ビルド状態", "The current build and reload state."),
        ["BuildController.Reload"] = L("最新ビルドへ再読み込み", "ポーリングを待たずに最新マニフェストでプロバイダーを再構築します。再構築中は他のエンドポイントが503を返します。", "Reload the newest build", "Rebuilds the provider from the newest manifest without waiting for the poll. Other endpoints return 503 while it runs.", "再読み込み結果", "The reload result."),
        ["AesController.Local"] = L(
            "ローカルのビルドからAESキーを生成",
            "このPCにインストール済みのFortniteを指定し、そのビルド自身からAESキーを作ります。インストール先のバイナリに埋め込まれたキー候補を走査し、そのビルドのPAK/UTOCで実際に復号できたものだけを採用するため、コンテナが要求するGUIDごとに1つずつキーが決まります。外部のAESキーAPIに依存しないので、APIがまだ公開していない新しいビルドや、すでに配信されていない古いインストールでもキーが取れます（api=falseで完全にオフラインになります）。",
            "Produce the AES keys of a local installation",
            "Names a Fortnite installation already on this machine and produces its AES keys from the build itself: its binaries are scanned for compiled-in candidates and its own containers decide which one is right, one key per encryption GUID they ask for. Nothing depends on the live key APIs, so an installation newer or older than what they publish still yields its keys (api=false stays entirely offline).",
            "ローカルビルドのAESキー", "The local build's AES keys."),
        ["LocalController.Status"] = L(
            "ローカルのインストールを検出",
            "このPCにあるFortniteのインストール先（環境変数LOCAL_GAME_DIR、Epic Games Launcherの記録、既定のインストール場所）と、現在マウント中のものを一覧します。何も開かず鍵も読まないので、繰り返し呼んでも安全です。",
            "Detect local installations",
            "Lists the Fortnite installations this machine appears to have (the LOCAL_GAME_DIR variable, the Epic Games Launcher's records, and the default install locations) and which of them are mounted right now. Nothing is opened and no key is read.",
            "検出したインストールとマウント状況", "The detected installations and what is mounted."),
        ["LocalController.Mount"] = L(
            "ローカルのビルドをマウント",
            "ローカルのインストールをマウントし、アセットを読み取れる状態で保持します。AESキーの決め方はGET /api/v1/aes/localと同じです。ビルド1つで数GBのメモリを使うため、同時にマウントできる数はLOCAL_BUILDS_MAX（既定1）で制限され、放置されたものは自動的に解放されます。",
            "Mount a local build",
            "Mounts a local installation and keeps it loaded for reading assets. The AES keys are worked out exactly as GET /api/v1/aes/local does. A mounted build costs a few GB, so LOCAL_BUILDS_MAX (default 1) caps how many are kept and an idle one is dropped.",
            "マウント結果", "The mount result."),
        ["LocalController.Unmount"] = L(
            "ローカルのビルドを解放",
            "マウント中のローカルビルドを解放してメモリを戻します。dirを省略すると全て解放します。読み取り中のビルドは、その読み取りが終わってから解放されます。",
            "Unmount a local build",
            "Drops a mounted local installation and frees its memory; every one of them when dir is omitted. A build still being read is dropped once that read finishes.",
            "解放結果", "The unmount result."),
        ["MappingsController.Import"] = L(
            "ローカルの.usmapを取り込む",
            "このPC上にある.usmapファイルをこのインスタンスのマッピング保管場所へ取り込みます。取り込んだものはGET /api/v1/mappingsで一覧され、ファイル名を指定して取得でき、loadでそのまま読み込めます。取り込む前に解析して読めることを確認するため、壊れたファイルはその場で拒否されます。",
            "Import a local .usmap",
            "Takes a .usmap that is already on this machine into this instance's mapping store, so it can be listed, served by name and hot-loaded like any mapping this API produced itself. The file is parsed before it is stored, so an unreadable one is rejected rather than kept.",
            "取り込み結果", "The import result."),
        ["AesController.Binaries"] = L("AES対象バイナリを一覧", "現在のマニフェストに含まれるAESスキャン対象バイナリを一覧します。", "List AES binaries", "Lists binaries available for AES extraction from the current manifest.", "バイナリ一覧", "The available binaries."),
        ["UpdateController.GetStatus"] = L("更新状況を確認", "実行中のバージョンとGitHubの最新リリースを比較し、更新可能かどうかを返します。", "Check for updates", "Compares the running version with the newest GitHub release and reports whether an update applies.", "更新状況", "The current and latest version, and whether an update applies."),
        ["UpdateController.Apply"] = L("最新リリースへ更新", "最新リリースをダウンロード・展開して差し替え、プロセスを終了します（既定では自動で再起動します）。", "Install the newest release", "Downloads, stages, and swaps in the newest release, then shuts this process down (it restarts automatically by default).", "更新結果", "Whether the update was staged and what happens next."),
        ["VersionsController.GetVersions"] = L("既知のビルドを一覧", "このインスタンスが把握しているビルドと、それぞれが今も読み取れるかどうかを返します。マニフェストを保管してあるビルドは load で復帰させられます。", "List known builds", "Lists every build this instance knows about and whether it can still be read. A build whose manifest is retained can be brought back with the load endpoint.", "ビルド一覧と保持状況", "The known builds and their retention state."),
        ["VersionsController.Import"] = L("マニフェストからビルドを取り込む", "手元のマニフェストファイル（アップロード、またはサーバー上のパス）をアーカイブに追加し、このインスタンスが配信していないビルドも読み取り・比較の対象にします。", "Import a build from a manifest", "Adds a build to the archive from an uploaded manifest file, or one already on the server, so a build this instance never served becomes readable and comparable.", "取り込んだビルド", "The imported build."),
        ["VersionsController.Load"] = L("アーカイブ済みビルドを読み込む", "保管してあるマニフェストからそのビルドをマウントし、最新ビルドと並べて読めるようにします。Fortnite 1ビルド分のメモリを使うため、同時に保持できる数は HISTORICAL_BUILDS_MAX で制限されます。", "Mount an archived build", "Mounts a build from its archived manifest so it can be read alongside the live one. A mounted build costs a whole build's worth of memory, so how many may exist at once is capped by HISTORICAL_BUILDS_MAX.", "マウント結果", "The mount result."),
        ["VersionsController.Unload"] = L("アーカイブ済みビルドを解放", "マウント中のビルドを解放してメモリを空けます。マニフェストは残るので、後からもう一度読み込めます。", "Unmount an archived build", "Unmounts a build and frees its memory. Its archived manifest is kept, so it can be mounted again later.", "解放結果", "Whether the build was unmounted."),
        ["VersionsController.DeleteData"] = L("旧バージョンのデータを削除", "そのビルドのマニフェスト・AESキー・チャンクキャッシュを削除します。アップデート時の「古いバージョンを削除する」手順にあたります。記録済みの変更リストは残るため、ファイルは読めなくなっても変更履歴は残ります。", "Delete an old build's data", "Deletes that build's manifest, archived keys and chunk cache — the \"delete the old version\" step of an update. Recorded changelists are kept, so its history stays queryable even though its files no longer are.", "削除結果", "The deletion result."),
        ["ChangesController.GetRecorded"] = L("記録済みの変更リストを一覧", "このインスタンスに記録されているビルド間の変更リストを一覧します。", "List recorded changelists", "Lists the build-to-build changelists recorded on this instance.", "変更リスト一覧", "The recorded changelists."),
        ["ChangesController.GetModifiedPaths"] = L("実際に書き換わったファイルパスを取得", "過去バージョンと最新バージョンの両方に存在し、実際に中身が書き換わったファイルパスだけを1行1件のテキストファイルとして返します。新しく追加されたファイルと削除されたファイルは除外されます。記録が無くても両方のビルドがマウントされていればその場で比較します。format=json でメタデータ付きのJSONにもできます。", "Get the paths that were actually rewritten", "Returns only the paths that exist in both builds and whose content actually differs, as a text file with one path per line. Files added in the newer build and files removed from it are both excluded. format=json returns the same list with its metadata.", "書き換わったファイルパスの一覧", "The list of rewritten file paths."),
        ["ChangesController.GetChangelist"] = L("ビルド間の変更ファイルを取得", "2つのビルド間で追加・削除・変更されたファイルパスを返します。直接記録されていない組み合わせは、記録済みの連鎖から合成します（v40→v41 と v41→v42 から v40→v42 を作る）。どちらの記録も無くても、両方のビルドがマウントされていればその場で比較して返します（ファイルインデックスの突き合わせのみで、ダウンロードは発生しません）。", "Get the files that changed between two builds", "Returns the file paths added, removed or modified between two builds. A pair that was never recorded directly is composed out of the recorded chain, so v40→v41 plus v41→v42 yields v40→v42.", "変更ファイル一覧", "The paginated changelist."),
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
        ["BackupController.Get"] = P(("format", "json（情報）またはfbkp（ファイル）。", "json for metadata or fbkp for the file."), ("includePayloads", "uexpなどのペイロードを含めるか。", "Include package payloads."), ("compress", "fbkpをLZ4圧縮するか。", "Compress the fbkp with LZ4.")),
        ["FilesController.GetFiles"] = P(("prefixes", "ファイル名の接頭辞CSV。省略すると全ファイル。", "Optional name-prefix CSV; omitted means all files."), ("ext", "拡張子。省略すると全拡張子。", "Extension; omitted means all extensions."), ("excludePrefixes", "除外する接頭辞CSV。", "Excluded prefix CSV."), ("excludePaths", "除外するパス部分文字列CSV。", "Excluded path-fragment CSV."), ("page", "ページ番号。", "Page number."), ("pageSize", "1ページの件数。最大10000。", "Page size; maximum 10000.")),
        ["ExportController.Get"] = P(("path", "アセットの仮想パス。", "Asset virtual path."), ("image", "テクスチャをPNGで返すか。", "Return a texture as PNG."), ("audio", "音声をバイナリで返すか。", "Return a sound payload."), ("lang", "FTextの言語コード。例: ja。", "Localization language code, for example ja."), ("hotfix", "cloudstorageのホットフィックス（[AssetHotfix]のDataTable/CurveTable書き換えとFTextのテキスト差し替え）を適用した内容を返すか。既定はfalse。", "Return the asset with the live cloudstorage hotfixes applied — [AssetHotfix] table/curve edits and FText replacements; default false.")),
        ["ExportController.Batch"] = P(("request", "pathsとlangを含むJSONリクエスト。最大100件。", "JSON request containing paths and lang; maximum 100 paths."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["ExportController.GetAudioInfo"] = P(("path", "音声アセットの仮想パス。", "Sound asset virtual path.")),
        ["PakController.GetPaks"] = P(("q", "PAK名・パスの任意の絞り込み。", "Optional archive name/path filter."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大200。", "Items per page; maximum 200."), ("state", "mounted（既定）、unloaded、all。", "mounted (default), unloaded or all.")),
        ["PakController.GetFilesInPak"] = P(("pakName", "PAK名または名前の一部。", "PAK name or name fragment."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大10000。", "Items per page; maximum 10000.")),
        ["CosmeticsController.SearchCosmetics"] = P(("q", "コスメIDまたは名前の一部。", "Cosmetic ID or name fragment."), ("category", "カテゴリ接頭辞。例: Character。", "Category prefix, for example Character."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大200。", "Items per page; maximum 200."), ("lang", "表示名の言語コード。", "Localization language code."), ("pakName", "PAK名またはチャンク番号。省略すると全マウント済みPAK。", "PAK name or chunk number; omitted means all mounted archives."), ("includeOffers", "バンドル表示アセットを含めるか。", "Include bundle display assets.")),
        ["AssetsController.GetDependencies"] = P(("path", "対象アセットの仮想パス。", "Asset virtual path."), ("depth", "再帰深度。0〜3、既定値1。", "Recursion depth, 0-3; default 1."), ("limit", "依存関係の最大件数。最大500。", "Maximum dependency entries; max 500."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["ExportController.GetDataTable"] = P(("path", "DataTable/CurveTableアセットの仮想パス。", "DataTable / CurveTable virtual path."), ("format", "csv（既定）またはjson。", "csv (default) or json."), ("rows", "取得する行名のCSV。省略時は全行。", "Comma-separated row names to keep; every row when omitted."), ("delimiter", "区切り文字。comma（既定）/tab/semicolon/pipe、または1文字。", "Delimiter: comma (default), tab, semicolon, pipe, or a single character."), ("flatten", "ネストしたプロパティをドット区切りの列に展開するか。既定はtrue。", "Flatten nested properties into dotted columns; default true."), ("bom", "ExcelがUTF-8として開けるようBOMを付けるか。既定はtrue。", "Prefix a UTF-8 BOM so Excel reads the file as UTF-8; default true."), ("download", "Content-Dispositionを付けてダウンロードさせるか。既定はtrue。", "Send Content-Disposition so the CSV downloads; default true."), ("hotfix", "cloudstorageの[AssetHotfix]による行・カーブ書き換えを適用するか。", "Apply the live cloudstorage [AssetHotfix] row/curve edits.")),
        ["CosmeticsController.GetCosmeticById"] = P(("id", "コスメIDまたはアセット名。", "Cosmetic ID or asset name."), ("lang", "表示名の言語コード。", "Localization language code.")),
        ["CosmeticsController.GetCosmeticIcon"] = P(("id", "コスメIDまたはアセット名。", "Cosmetic ID or asset name."), ("variant", "large（既定）/small/offercatalog。largeとsmallは他方とOfferCatalogにフォールバックします。", "large (default) / small / offercatalog; large and small fall back to the other icon and then OfferCatalog.")),
        ["LocalizationController.Lookup"] = P(("key", "解決するFTextのKey。", "FText key to resolve."), ("namespace", "Keyの所属namespace。省略時は全namespaceを検索。", "Namespace the key belongs to; every namespace when omitted."), ("text", "逆引きする表示文字列。keyとの併用は不可。", "Localized string to reverse-look-up; cannot be combined with key."), ("mode", "逆引きの一致方法。contains（既定）/exact/prefix/suffix/regex。", "Reverse-lookup match mode: contains (default) / exact / prefix / suffix / regex."), ("lang", "対象言語。テーブルは既定ja、検索は既定で全言語。", "Table language defaults to ja; lookups default to every available language."), ("langs", "対象言語のCSV。allまたは*で全言語。", "Comma-separated languages; all or * selects every language."), ("caseSensitive", "大文字小文字を区別するか。", "Case-sensitive matching."), ("withTranslations", "逆引き結果を全言語の対訳付きで返すか。", "Resolve each reverse-lookup hit into every available language."), ("maxResults", "逆引きの最大件数。既定50、最大500。", "Maximum reverse-lookup entries; default 50, max 500.")),
        ["ConfigController.GetFiles"] = P(("q", "ファイル名またはパスの絞り込み。", "Optional file-name/path filter.")),
        ["ConfigController.Query"] = P(("file", "INIファイル名または仮想パス。", "INI file name or virtual path."), ("section", "セクション名。[]は省略可能。", "Section name; brackets are optional."), ("key", "設定キー名。", "Configuration key name.")),
        ["ItemsController.GetProperties"] = P(("prefixes", "接頭辞のCSV。", "Comma-separated prefixes."), ("excludePrefixes", "除外する接頭辞のCSV。例：WID_Harvest_。prefixesに一致しても除外されます。", "Comma-separated file-name prefixes to exclude, e.g. WID_Harvest_; dropped even when they match prefixes."), ("excludePaths", "パスに含まれていたら除外する文字列のCSV。例：/Juno/。", "Comma-separated path substrings; a file is excluded when its full path contains one, e.g. /Juno/."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。", "Items per page."), ("path", "単一アセットを取得するパス。指定すると一覧用の条件は適用しません。", "Path for a single asset; list filters are ignored when supplied.")),
        ["SearchController.Search"] = P(("q", "検索文字列。", "Search string."), ("mode", "contains/prefix/suffix/exact/wildcard/regex/tokens。", "Match mode."), ("field", "path/name/stem。", "Search field."), ("caseSensitive", "大文字小文字を区別するか。", "Case-sensitive matching."), ("ext", "拡張子CSV。", "Comma-separated extensions."), ("dir", "検索対象ディレクトリ。", "Directory prefix."), ("dedupe", "Cooked重複をまとめるか。", "Collapse cooked duplicates."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。", "Items per page."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state."), ("target", "path（既定）またはcontent。", "path (default) or content."), ("pathContains", "内容検索の候補パスの追加条件。", "Additional content-search path filter."), ("maxScan", "内容検索の最大走査件数。", "Maximum content-search candidates."), ("maxResults", "内容検索で返す最大件数。", "Maximum content-search results."), ("snippetsPerFile", "内容検索で返すスニペット数。", "Snippets per content-search file.")),
        ["MappingsController.Generate"] = P(("dir", "UEFNのインストール先またはBinaries/Win64。省略時はUEFN_BINARIES_DIRまたは標準インストール先。", "UEFN installation or Binaries/Win64 directory; defaults to UEFN_BINARIES_DIR or the standard installation."), ("compression", "zstd（既定）・brotli・oodle・none。", "zstd (default), brotli, oodle or none."), ("level", "圧縮レベル。zstd: 1–22、brotli: 0–11、oodle: 0–9。", "Compression level: zstd 1–22, brotli 0–11, oodle 0–9."), ("oodle", "Oodle DLLのパス。省略時はUEFN内から探索。", "Oodle DLL path; searched in the UEFN directory when omitted."), ("fileName", "出力ファイル名。省略時はUEFNのビルド名と圧縮方式から決定。", "Output file name; defaults to the UEFN build name and compression suffix."), ("timeoutSeconds", "生成の制限時間。1–3600秒、既定120秒。", "Generation timeout: 1–3600 seconds, default 120."), ("load", "生成後に読み込むか。マウント中のビルドとCLが一致する場合のみ使用可能。", "Load the result; requires the installed UEFN build and changelist to match the mounted build."), ("download", "trueで.usmap、falseで統計JSON。", "Return the .usmap binary when true, statistics JSON when false.")),
        ["MappingsController.GetUefnStatus"] = P(("dir", "UEFNのインストール先またはBinaries/Win64。", "UEFN installation or Binaries/Win64 directory.")),
        ["MappingsController.DownloadMapping"] = P(("fileName", "GET /api/v1/mappings が返すファイル名。", "File name as listed by GET /api/v1/mappings.")),
        ["AesController.Aes"] = P(("force", "Common DLLのキャッシュを無視して再取得するか。", "Re-download the Common DLL instead of using its cache."), ("noApi", "外部AES APIを使わずバイナリから候補を選ぶか。", "Skip the external AES API and select from binary candidates."), ("submit", "抽出キーをローカルプロバイダーへ適用するか。", "Submit the extracted key to the local provider."), ("ct", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["AesController.Extract"] = P(("verify", "抽出キーを外部AES APIと照合するか。", "Cross-check extracted keys against the external AES API."), ("force", "バイナリのキャッシュを無視して再取得するか。", "Re-download the binary instead of using its cache."), ("file", "スキャン対象にする別バイナリの名前またはパス末尾。", "Alternate binary name or path suffix to scan."), ("ct", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["AesController.ScanLocal"] = P(("path", "スキャンするローカルファイル。", "Local file to scan."), ("dir", "スキャンするローカルディレクトリ。", "Local directory to scan."), ("verify", "検出キーを外部AES APIと照合するか。", "Cross-check detected keys against the external AES API."), ("ct", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["AesController.Local"] = P(("dir", "インストール先のディレクトリ、または.pak/.utocが置かれたフォルダ。省略時は自動検出します。", "Installation directory, or the folder holding the .pak/.utoc files; auto-detected when omitted."), ("key", "追加で試すキー。hexまたはguid:hex形式。手持ちのキーの検証にも使えます。", "Extra key(s) to try, as hex or guid:hex; also how you verify a key you already have."), ("scan", "インストール先のバイナリを走査してキー候補を集めるか。既定はtrue。", "Scan the installation's own binaries for compiled-in keys; default true."), ("deep", "低速な鍵スケジュール走査も行うか。キーが展開済みで埋め込まれたビルド向けです。", "Also run the slower key-schedule scanner, for builds that store the key expanded."), ("binary", "ファイル名にこの文字列を含むバイナリだけを走査します。", "Only scan binaries whose file name contains this."), ("binaries", "走査するバイナリの数。有力なものから順に、既定は8。", "How many binaries to scan, most promising first; default 8."), ("api", "インストール先から判明しなかったGUIDについて外部AES APIを参照するか。既定はtrue。falseで完全にオフライン。", "Consult the live AES APIs for GUIDs the installation did not answer; default true, false stays entirely offline."), ("mount", "取得後もそのビルドをマウントしたまま保持するか。既定はfalse。", "Keep the installation mounted afterwards for reading assets; default false."), ("submit", "確認できたキーをこのAPI自身のプロバイダーにも適用するか。既定はfalse。", "Submit the verified keys to this API's own provider as well; default false."), ("save", "取得したキーをプロジェクト直下のaes.local.jsonへ書き出すか。既定はfalse。", "Write the keys to aes.local.json in the project root; default false."), ("ct", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["LocalController.Mount"] = P(("dir", "インストール先のディレクトリ、または.pak/.utocが置かれたフォルダ。省略時は自動検出します。", "Installation directory, or the folder holding the .pak/.utoc files; auto-detected when omitted."), ("key", "追加で試すキー。hexまたはguid:hex形式。", "Extra key(s) to try, as hex or guid:hex."), ("scan", "インストール先のバイナリを走査してキー候補を集めるか。既定はtrue。", "Scan the installation's own binaries for compiled-in keys; default true."), ("deep", "低速な鍵スケジュール走査も行うか。", "Also run the slower key-schedule scanner over those binaries."), ("api", "判明しなかったGUIDについて外部AES APIを参照するか。既定はtrue。", "Consult the live AES APIs for GUIDs the installation did not answer; default true."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["LocalController.Unmount"] = P(("dir", "解放するインストール先。省略時は全て解放します。", "The installation to unmount; all of them when omitted.")),
        ["MappingsController.Import"] = P(("path", "取り込む.usmapファイルのローカルパス。", "Local path of the .usmap file to import."), ("fileName", "保管するときのファイル名。既定は元のファイル名。", "Name to store it under; defaults to the source file name."), ("load", "取り込んだマッピングをプロバイダーへ読み込むか。", "Hot-load the imported mapping into the provider."), ("download", "保管した.usmapをバイナリで返すか。既定はfalse。", "Return the stored .usmap binary instead of JSON statistics; default false.")),
        ["UpdateController.GetStatus"] = P(("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["UpdateController.Apply"] = P(("force", "適用に失敗したバージョンの再試行抑止を解除するか。", "Ignore the guard that suppresses retrying a version which previously failed to apply."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["VersionsController.Import"] = P(("file", "アップロードするマニフェストファイル。", "Manifest file to upload."), ("path", "サーバー上にあるマニフェストファイルのパス。fileの代わりに使えます。", "Path of a manifest file already on the server; an alternative to file."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["VersionsController.Load"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["VersionsController.Unload"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest.")),
        ["VersionsController.DeleteData"] = P(("version", "ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest.")),
        ["ChangesController.GetChangelist"] = P(("from", "古い方のビルド。ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Older build. Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("to", "新しい方のビルド。省略時はライブビルド。", "Newer build; defaults to the live build."), ("kind", "変更種別での絞り込み。added、removed、modified。省略時は全件。", "Filter by change type: added, removed or modified; all when omitted."), ("pathFilter", "パスの部分一致による絞り込み。", "Only return paths containing this fragment."), ("page", "ページ番号。", "1-based page number."), ("pageSize", "1ページの件数。最大10000。", "Entries per page; maximum 10000.")),
        ["ChangesController.GetModifiedPaths"] = P(("from", "古い方のビルド。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous も使えます。", "Older build. Accepts the full build string, 42.10, a changelist number, or previous."), ("to", "新しい方のビルド。省略時はライブビルド。", "Newer build; defaults to the live build."), ("pathFilter", "パスの部分一致による絞り込み。", "Only include paths containing this fragment."), ("verifiedOnly", "両ビルドの中身を実際に読んでハッシュ比較したものだけに絞るか。既定はfalse（サイズ変化で検出したものも含む）。", "Include only paths whose rewrite was confirmed by hashing both copies; default false, which also includes rewrites detected by a size change."), ("format", "textで1行1件のテキスト（既定）、jsonでメタデータ付きJSON。", "text for one path per line (default), or json for the list plus its metadata."), ("download", "textのときファイルとして添付するか。既定はtrue。", "Send the text form as a file attachment; default true.")),
        ["ChangesController.GetFileChanges"] = P(("from", "古い方のビルド。ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Older build. Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("to", "新しい方のビルド。省略時はライブビルド。", "Newer build; defaults to the live build."), ("path", "差分を取るファイルの仮想パス。", "Virtual file path to diff."), ("format", "jsonで構造化されたハンク（既定）、patchでunified diffテキスト。", "json for structured hunks (default), or patch for unified-diff text."), ("context", "各ハンクの前後に付ける行数。0〜20、既定は3。", "Context lines around each hunk, 0 to 20; default 3."), ("load", "アーカイブ済みで未読み込みのビルドをその場でマウントするか。既定はtrue。", "Mount the build when it is archived but not loaded. Default true."), ("cancellationToken", "リクエストのキャンセル状態。", "Request cancellation state.")),
        ["ChangesController.Compute"] = P(("from", "古い方のビルド。ビルド指定。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。", "Older build. Build to name. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest."), ("to", "新しい方のビルド。省略時はライブビルド。", "Newer build; defaults to the live build."), ("mode", "quickはパス・サイズ・格納アーカイブのみで内容を読みません。fullは同サイズのファイルを両側でハッシュします。既定はquick。", "quick uses path, size and containing archive without reading content; full hashes same-size files on both sides. Default quick."), ("pathFilter", "この接頭辞で始まるパスだけを比較します。mode=fullでは指定を強く推奨します。", "Restrict the comparison to paths starting with this prefix; strongly recommended with mode=full."), ("maxEntries", "記録する最大件数。1〜500000、既定は200000。", "Maximum entries to record, 1 to 500000; default 200000."), ("maxHashFiles", "fullモードでハッシュする同サイズファイルの上限。1〜200000、既定は5000。", "In full mode, the maximum number of same-size files to hash, 1 to 200000; default 5000."), ("unloadWhenDone", "ジョブがマウントしたビルドを完了時に解放するか。既定はtrue。", "Unmount the builds the job had to mount once it finishes; default true."), ("force", "片方のビルドでしか読めないPAKも除外せず比較するか。既定はfalseで、そのPAKだけを比較対象から外します（中身が丸ごと追加・削除として出てしまうため）。両方で読めないPAKは無害なので対象外です。", "Compare without excluding containers that only one of the builds can open. Default false, which leaves just those containers out because every file in them would otherwise be reported as added or removed. Containers locked in both builds are harmless and are never excluded.")),
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
