using System.Collections.Generic;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FortnitePorting.Swagger
{
    /// <summary>
    /// Fully localizes each operation for the active Swagger document ("ja" or "en"):
    /// the Summary, the Description, every parameter Description, and the success-response
    /// Description. Registered after IncludeXmlComments so it takes precedence over the XML text,
    /// guaranteeing the "ja" document contains no leftover English.
    /// </summary>
    public sealed class LocalizedOperationFilter : IOperationFilter
    {
        private sealed record Loc(string Summary, string Description, string Returns);

        // Operation text, keyed by "{ControllerTypeName}.{ActionMethodName}".
        private static readonly Dictionary<string, (Loc Ja, Loc En)> Operations = new()
        {
            ["ExportController.Get"] = (
                new("アセットをエクスポートします（既定は JSON）。",
                    "アセットを JSON で返します。image=true でテクスチャを PNG、audio=true でサウンドを音声として返し、lang でローカライズを適用します（例: ja）。",
                    "アセット（JSON／PNG／音声）。"),
                new("Export an asset (JSON by default).",
                    "Returns the asset as JSON. With image=true a texture is returned as PNG; with audio=true a sound is decoded/served as audio; lang applies localization (e.g. ja).",
                    "The asset (JSON / PNG / audio).")),

            ["ExportController.GetAudioInfo"] = (
                new("サウンドアセットの音声情報を取得します。",
                    "音声形式・WAV へ変換可能か・ネイティブ RAD Audio デコーダの有無を、バイナリを返さずに報告します。",
                    "音声のメタ情報。"),
                new("Report audio metadata for a sound asset.",
                    "Returns the audio format, whether it can be decoded to WAV, and native RAD Audio decoder availability — without downloading the binary payload.",
                    "The audio metadata.")),

            ["ExportController.GetLocres"] = (
                new("指定言語の結合済みローカライズテーブルを取得します。",
                    "指定した言語コードの全 .locres エントリを読み込み、名前空間／キー／値のマップに結合して返します。",
                    "結合済みのローカライズテーブル。"),
                new("Get the merged localization table for a language.",
                    "Loads and merges every .locres entry for the given language code into a single namespace/key/value map.",
                    "The merged localization table.")),

            ["ExportController.GetLocresLanguages"] = (
                new("利用可能なローカライズ言語の一覧を取得します。",
                    "読み込み済みファイルに .locres データが存在する言語コードの一覧を返します。",
                    "利用可能な言語コードの一覧。"),
                new("List available localization languages.",
                    "Returns the language codes for which .locres data is present in the loaded files.",
                    "The list of available language codes.")),

            ["ExportController.GetFilePathsInPak"] = (
                new("指定した PAK／チャンク内のファイルパス一覧を取得します。",
                    "指定名に一致する PAK アーカイブ（またはチャンク番号）に含まれる全ファイルパスを返します。",
                    "ファイルパスの一覧。"),
                new("List file paths inside a PAK / chunk.",
                    "Returns every file path contained in the PAK archive (or chunk number) that matches the given name.",
                    "The list of file paths.")),

            ["DebugController.GetStats"] = (
                new("読み込まれた全ファイルパスを取得します（ページング）。",
                    "現在マウントされている全仮想ファイルパスを 1000 件ずつ返します。",
                    "ファイルパスの一覧と総数。"),
                new("List all loaded file paths (paginated).",
                    "Returns all currently mounted virtual file paths, 1000 per page.",
                    "The file paths and total count.")),

            ["DebugController.SearchFiles"] = (
                new("ファイルパスを部分一致で検索します。",
                    "指定キーワードを含むマウント済みファイルパスを、大文字小文字を無視して返します。",
                    "検索結果のファイルパス一覧。"),
                new("Search loaded file paths by substring.",
                    "Returns every mounted file path that contains the given query (case-insensitive).",
                    "The matching file paths.")),

            ["DebugController.GetMountedPaks"] = (
                new("マウント済みの PAK／UTOC ファイル一覧を取得します。",
                    "マウント済み各 VFS（PAK／UTOC）の名前・ファイル数・パスを返します。",
                    "PAK／UTOC の一覧。"),
                new("List mounted PAK / UTOC files.",
                    "Returns the name, file count, and path of each mounted VFS (PAK/UTOC) archive.",
                    "The list of PAK/UTOC files.")),

            ["DebugController.GetFilesInPak"] = (
                new("マウント済み PAK 内のファイル一覧を取得します。",
                    "指定名のマウント済み PAK アーカイブに含まれるファイルパスを返します。",
                    "ファイルパスの一覧。"),
                new("List files inside a mounted PAK.",
                    "Returns the file paths contained in the named mounted PAK archive.",
                    "The list of file paths.")),

            ["ItemsController.GetFiles"] = (
                new("指定した接頭辞で始まるファイルのパス一覧を返します。",
                    "ファイル名が接頭辞（既定 WID_, AGID_, Athena_, Figment_Athena_）のいずれかで始まるパスを、拡張子（既定 .uasset）で絞り込み、ページングして返します。",
                    "一致したファイルパスの一覧と総数。"),
                new("List file paths matching the given prefixes.",
                    "Returns paths whose file name starts with one of the prefixes (default WID_, AGID_, Athena_, Figment_Athena_), filtered by extension (.uasset by default), paginated.",
                    "The matching file paths and total count.")),

            ["ItemsController.GetProperties"] = (
                new("接頭辞に一致する各アセットから ItemName／Traits／LargeIcon を抽出します。",
                    "接頭辞に一致する各アセットについて、Properties.ItemName.SourceString、DataList 配下の Traits、LargeIcon.AssetPathName を抽出します。ページング対応。解析が重いため pageSize は小さめを推奨します。",
                    "各ファイルの抽出結果の一覧。"),
                new("Extract ItemName/Traits/LargeIcon for matching assets.",
                    "For each asset matching the prefixes, extracts Properties.ItemName.SourceString, the Traits under DataList, and LargeIcon.AssetPathName. Paginated; parsing is expensive so keep pageSize small.",
                    "The extraction results for each file.")),

            ["ItemsController.GetSingleProperties"] = (
                new("単一アセットから ItemName／Traits／LargeIcon を抽出します。",
                    "/properties と同じ抽出を、単一のアセットパスに対して実行します。",
                    "抽出結果。"),
                new("Extract ItemName/Traits/LargeIcon for a single asset.",
                    "Runs the same extraction as /properties for a single asset path.",
                    "The extraction result.")),

            ["CosmeticsController.GetCosmetics"] = (
                new("指定 PAK／チャンク内の BRCosmetics コスメ情報を抽出します。",
                    "BRCosmetics の Cosmetics ディレクトリ配下の各コスメから、ItemName／ItemDescription／ItemShortDescription の Key と（lang 指定時は）ローカライズ済みテキスト、LargeIcon と Icon の AssetPathName、Tags を抽出します（ページング対応、解析が重いため pageSize は小さめ推奨）。",
                    "各コスメの抽出結果の一覧。"),
                new("Extract BRCosmetics cosmetic info from a PAK / chunk.",
                    "For each cosmetic under the BRCosmetics Cosmetics directory, extracts the ItemName/ItemDescription/ItemShortDescription Keys (and the localized text when lang is given), the LargeIcon and Icon AssetPathName values, and the Tags. Paginated; parsing is expensive so keep pageSize small.",
                    "The extraction results for each cosmetic.")),

            ["SearchController.Search"] = (
                new("全ファイルを対象に文字列検索します。",
                    "読み込み済みの全ファイルのパス／ファイル名を対象に、単語・文字列・コードネームを検索します。mode で contains（部分一致・既定）／prefix／suffix／exact／wildcard（*?）／regex（正規表現）／tokens（空白区切りの全語一致）を選択。field で path（フルパス・既定）／name（ファイル名）／stem（拡張子なし）を切り替え、ext で拡張子、dir でディレクトリを絞り込み、dedupe で .uasset/.uexp などの重複を1件にまとめられます。ページング対応。",
                    "一致したファイル（path／name／ext）の一覧と総数。"),
                new("Search all files by string.",
                    "Searches the paths/file names of every loaded file for a word, string, or codename. mode selects contains (default) / prefix / suffix / exact / wildcard (*?) / regex / tokens (all whitespace-separated words must match). field switches between path (default) / name / stem (name without extension); ext filters by extension, dir restricts to a directory, and dedupe collapses cooked-asset variants (.uasset/.uexp/...). Paginated.",
                    "The matching files (path / name / ext) and the total count.")),

            ["SearchController.SearchContent"] = (
                new("ファイルの内容を対象に文字列検索します（アセット＋設定/テキスト）。",
                    "候補ファイルを実際に読み取り、内容に対して文字列を検索します。アセット（.uasset/.umap）はエクスポートを JSON 化して検索し、設定/テキスト/バイナリ（.ini/.bin/.json 等）は生バイトを復号して検索します（ItemName の原文・コードネーム・参照パス・設定値などを発見できます）。既定の対象はアセット＋設定/テキスト、ext=* で全ファイル。既定では全ファイル（約165万件・約11GB）を走査します。検出は文字列確保なしのバイト走査＋マルチコア並列のため全走査でも約40秒です。走査順は (1) パスにクエリを含むファイル → (2) その近傍アセット（同一プラグイン/フォルダ）→ (3) 設定/テキスト → (4) その他アセット。速度優先なら maxScan に小さい値を指定すると先頭から部分的に走査します。一致ファイルごとに該当箇所スニペットを返します。",
                    "一致したファイルと該当スニペットの一覧。"),
                new("Search inside file contents (assets + config/text).",
                    "Reads candidate files and searches their contents. Assets (.uasset/.umap) are parsed and their exports serialized to JSON; config/text/binary files (.ini/.bin/.json, etc.) are decoded from raw bytes (find ItemName source strings, codenames, referenced paths, config values, etc.). The default set is assets + text/config; ext=* searches every file. By default it scans every file (~1.65M, ~11 GB). Detection is an allocation-free byte scan run in parallel across cores, so a full scan still takes only ~40 s. Scan order: (1) path contains the query, (2) its neighbour assets (same plugin/folder), (3) text/config, (4) other assets. Pass a smaller maxScan for a faster partial scan from the top. Returns matching files with snippet lines.",
                    "The matching files and their snippet lines.")),

            ["MappingsController.Generate"] = (
                new("マッピングJSONから .usmap を生成して返します。",
                    "マッピングの JSON 版（{ Version, Enums, Structs, Classes } 形式。例: StormForge の …/Windows/<build>.json）から、CUE4Parse が読み込めるバイナリ .usmap（非圧縮・version ExplicitEnumValues）を生成し mappings/ に保存します。url（または path）から取得し、verify=true で生成物を再パースして件数・サンプルを検証、load=true で即座にホットスワップ適用します。クック済み pak から型を導出するのではなく、JSON 内の型レイアウトを .usmap 形式に変換するものです。生成した .usmap はその JSON のビルド用です。**既定では生成した .usmap 本体をレスポンスで返します**（download=false で JSON 統計のみ）。統計は X-Usmap-* ヘッダにも含まれます。",
                    "生成した .usmap バイナリ（download=false 時は生成結果のJSON）。"),
                new("Generate a .usmap from a mappings JSON and return it.",
                    "Converts a JSON mappings dump ({ Version, Enums, Structs, Classes } — e.g. a StormForge …/Windows/<build>.json) into a binary .usmap (uncompressed, version ExplicitEnumValues) that CUE4Parse can load, saving it under mappings/. Fetches from url (or a local path); verify=true parses it back and reports counts/samples; load=true hot-swaps it into the provider. It converts the type layouts already in the JSON — it does not derive them from cooked paks. A generated .usmap is specific to that JSON's build. **By default the generated .usmap is returned as the response body** (download=false returns JSON stats instead); the stats are also in X-Usmap-* headers.",
                    "The generated .usmap binary (or, when download=false, the generation-result JSON).")),

            ["AesController.Aes"] = (
                new("Common DLL から MainAES 鍵を取得して投入します。",
                    "ライブの Fortnite_Studio（UEFN）マニフェストから UnrealEditorFortnite-Common-Win64-Shipping.dll をダウンロードし、外部 AesFinder ツールで MainAES 鍵を抽出して返します（ゲーム起動・注入なし）。鍵は Common DLL 内に mov 命令の即値（AESDumpster パターン）として平文で格納されています。submit=true（既定）で抽出鍵をそのまま provider に投入し、対応する pak を自動マウントします。Common DLL は初回のみDL→以降キャッシュ、新ビルドは自動取得。ツールのパスは環境変数 AESFINDER_PATH で指定します。",
                    "MainAES 鍵・version・build と、投入／マウント結果。"),
                new("Extract and submit the MainAES key from the Common DLL.",
                    "Downloads UnrealEditorFortnite-Common-Win64-Shipping.dll from the live Fortnite_Studio (UEFN) manifest and runs the external AesFinder tool to extract the MainAES key (no game launch, no injection). The key is stored in the Common DLL as plaintext mov-instruction immediates (the AESDumpster pattern). With submit=true (default) the extracted key is submitted to the provider and matching paks are mounted. The Common DLL is downloaded once then cached; a new build is fetched automatically. Set the tool path with the AESFINDER_PATH environment variable.",
                    "The MainAES key, version and build, plus the submit/mount result.")),

            ["AesController.Extract"] = (
                new("exe をマニフェストからDLし、ビルトインのスケジュール走査で AES を探します。",
                    "Fortnite_Studio マニフェストから UnrealEditorFortnite-Win64-Shipping.exe（既定）をダウンロードし、ビルトインの AES-256 鍵スケジュール走査で鍵を探します（起動・注入なし）。verify=true でライブ AES API と照合、file= で別モジュールを指定可。注: 現行ビルドの MainAES は命令の即値として格納されるため本スケジュール走査では検出されません（その用途は /aes を使用してください）。",
                    "走査結果（検出鍵・サイズ・照合結果）。"),
                new("Download an exe from the manifest and scan it with the built-in schedule scanner.",
                    "Downloads UnrealEditorFortnite-Win64-Shipping.exe (default) from the Fortnite_Studio manifest and scans it for an AES-256 key schedule with the built-in scanner (no launch, no injection). verify=true cross-checks against the live AES API; file= targets a different module. Note: the current build's MainAES is stored as instruction immediates and is NOT found by this schedule scan — use /aes for that.",
                    "The scan result (found keys, size, verification).")),

            ["AesController.ScanLocal"] = (
                new("ローカルのファイル/ディレクトリを AesFinder で走査します。",
                    "ディスク上の既存ファイル（例: インストール済み Fortnite）を、ビルトインのスケジュール走査で検査します（ダウンロードなし）。path で単一ファイル、dir でディレクトリ内の全 exe/dll を走査（サイズ降順）。verify=true でライブ AES API と照合します。",
                    "ファイルごとの走査結果と検出鍵。"),
                new("Scan local file(s) / a directory with AesFinder.",
                    "Scans files already on disk (e.g. an installed Fortnite) with the built-in schedule scanner (no download). path scans a single file; dir scans every .exe/.dll inside a directory (largest first). verify=true cross-checks against the live AES API.",
                    "Per-file scan results and any found keys.")),

            ["AesController.Binaries"] = (
                new("Fortnite_Studio ビルドの Win64 バイナリ一覧を取得します。",
                    "マニフェスト内の Binaries/Win64 配下の exe/dll を、サイズ降順で一覧します（どのモジュールが鍵を持つかの診断用）。",
                    "Win64 バイナリ（名前・サイズ）の一覧。"),
                new("List the Fortnite_Studio build's Win64 binaries.",
                    "Lists the exe/dll files under Binaries/Win64 in the manifest, sorted by size (diagnostic for which module carries the key).",
                    "The Win64 binaries (name and size).")),

            ["AesController.SelfTest"] = (
                new("AesFinder スケジュール走査の自己テスト。",
                    "既知の鍵の展開済みスケジュールをバッファに埋め込み、走査がその鍵を正しく復元できるかを確認します（ダウンロード不要）。",
                    "自己テストの合否。"),
                new("Self-test for the AesFinder schedule scanner.",
                    "Embeds a known key's expanded schedule in a buffer and confirms the scanner recovers exactly that key (no download).",
                    "The self-test pass/fail result.")),
        };

        // Parameter descriptions, keyed by operation key then parameter name. (Ja, En).
        private static readonly Dictionary<string, Dictionary<string, (string Ja, string En)>> Parameters = new()
        {
            ["ExportController.Get"] = new()
            {
                ["path"] = ("エクスポートするアセットのパス。", "The path of the asset to export."),
                ["image"] = ("アセットがテクスチャの場合に PNG で返します。", "Return a PNG when the asset is a texture."),
                ["audio"] = ("アセットがサウンドの場合に音声で返します。", "Return audio when the asset is a sound."),
                ["lang"] = ("ローカライズ言語コード（例: ja）。en は適用しません。", "Localization language code (e.g. ja); en applies none."),
            },
            ["ExportController.GetAudioInfo"] = new()
            {
                ["path"] = ("サウンドアセットのパス。", "The path of the sound asset."),
            },
            ["ExportController.GetLocres"] = new()
            {
                ["lang"] = ("言語コード（例: ja）。", "The language code (e.g. ja)."),
            },
            ["ExportController.GetFilePathsInPak"] = new()
            {
                ["pakName"] = ("PAK 名、またはチャンク番号（例: 1051）。", "The PAK name, or chunk number (e.g. 1051)."),
            },
            ["DebugController.GetStats"] = new()
            {
                ["page"] = ("ページ番号（1から開始）。", "The page number (1-based)."),
            },
            ["DebugController.SearchFiles"] = new()
            {
                ["query"] = ("検索キーワード。", "The search keyword."),
            },
            ["DebugController.GetFilesInPak"] = new()
            {
                ["pakName"] = ("PAK ファイル名 (pakchunk1032-WindowsClient.pak)", "The PAK file name (pakchunk1032-WindowsClient.pak)"),
            },
            ["ItemsController.GetFiles"] = new()
            {
                ["prefixes"] = ("対象とする接頭辞のカンマ区切りリスト（省略時は WID_, AGID_, Athena_, Figment_Athena_）。",
                                "A comma-separated list of target prefixes (defaults to WID_, AGID_, Athena_, Figment_Athena_ when omitted)."),
                ["page"] = ("ページ番号（1から開始）。", "The page number (1-based)."),
                ["pageSize"] = ("1ページあたりの件数（最大 10000）。", "The number of items per page (maximum 10000)."),
                ["ext"] = ("対象の拡張子（既定は .uasset のみ。空文字ですべての拡張子）。", "The target file extension (defaults to .uasset only; an empty string matches all extensions)."),
            },
            ["ItemsController.GetProperties"] = new()
            {
                ["prefixes"] = ("対象とする接頭辞のカンマ区切りリスト（省略時は WID_, AGID_, Athena_, Figment_Athena_）。",
                                "A comma-separated list of target prefixes (defaults to WID_, AGID_, Athena_, Figment_Athena_ when omitted)."),
                ["page"] = ("ページ番号（1から開始）。", "The page number (1-based)."),
                ["pageSize"] = ("1ページあたりの件数（最大 500。アセット解析は重いため小さめを推奨）。",
                                "The number of items per page (maximum 500; a small value is recommended because asset parsing is expensive)."),
            },
            ["ItemsController.GetSingleProperties"] = new()
            {
                ["path"] = ("対象アセットのパス（例: FortniteGame/Content/.../WID_xxx.uasset）。",
                            "The path of the target asset (e.g. FortniteGame/Content/.../WID_xxx.uasset)."),
            },
            ["CosmeticsController.GetCosmetics"] = new()
            {
                ["pakName"] = ("PAK 名、またはチャンク番号（例: 1051）。", "The PAK name, or chunk number (e.g. 1051)."),
                ["page"] = ("ページ番号（1から開始）。", "The page number (1-based)."),
                ["pageSize"] = ("1ページあたりの件数（最大 200。解析が重いため小さめを推奨）。",
                                "The number of items per page (maximum 200; parsing is expensive so keep it small)."),
                ["lang"] = ("ローカライズ言語コード（例: ja）。ItemName/Description/ShortDescription の Key をこの言語に解決します。省略や en で英語の原文。",
                            "Localization language code (e.g. ja). Resolves the ItemName/Description/ShortDescription keys to this language; omit or use en for the English source."),
            },
            ["SearchController.Search"] = new()
            {
                ["q"] = ("検索する単語・文字列・コードネーム（必須）。", "The word, string, or codename to search for (required)."),
                ["mode"] = ("照合方法: contains（既定）／prefix／suffix／exact／wildcard／regex／tokens。",
                            "Match mode: contains (default) / prefix / suffix / exact / wildcard / regex / tokens."),
                ["field"] = ("照合対象: path（フルパス・既定）／name（ファイル名）／stem（拡張子なし）。",
                             "Match target: path (default) / name / stem (without extension)."),
                ["caseSensitive"] = ("大文字小文字を区別する（既定 false）。", "Match case-sensitively (default false)."),
                ["ext"] = ("拡張子で絞り込み（カンマ区切り可。例: .uasset,.umap。空ですべて）。",
                           "Filter by extension (comma-separated, e.g. .uasset,.umap; empty matches all)."),
                ["dir"] = ("このディレクトリ配下に限定（例: FortniteGame/Content/Athena）。",
                           "Restrict to paths under this directory (e.g. FortniteGame/Content/Athena)."),
                ["dedupe"] = (".uasset/.uexp/.ubulk などクック由来の重複を1件にまとめる（既定 false）。",
                              "Collapse cooked-asset duplicates (.uasset/.uexp/.ubulk, etc.) into one (default false)."),
                ["page"] = ("ページ番号（1から開始）。", "The page number (1-based)."),
                ["pageSize"] = ("1ページあたりの件数（最大 10000）。", "The number of items per page (maximum 10000)."),
            },
            ["SearchController.SearchContent"] = new()
            {
                ["q"] = ("アセット内容から検索する文字列（必須）。", "The string to find inside asset contents (required)."),
                ["dir"] = ("このディレクトリ配下の候補に限定。", "Restrict candidate assets to this directory."),
                ["pathContains"] = ("候補をパスの部分一致でさらに絞り込み。", "Further narrow candidates whose path contains this text."),
                ["ext"] = ("候補の拡張子（カンマ区切り可）。空＝既定セット（アセット＋.ini/.bin/.json 等の設定/テキスト）、* または all で全ファイル。",
                           "Candidate extensions (comma-separated). Empty = default set (assets + text/config such as .ini/.bin/.json); * or all = every file."),
                ["caseSensitive"] = ("大文字小文字を区別する（既定 false）。", "Match case-sensitively (default false)."),
                ["maxScan"] = ("走査する候補ファイルの最大数。既定 3000000＝全ファイル走査（約40秒）。速度優先なら小さい値を指定。",
                               "Maximum number of candidate files to scan. Default 3000000 = scan the whole game (~40 s); pass a smaller value for a faster partial scan."),
                ["maxResults"] = ("返す一致ファイルの最大数（既定 50・最大 2000）。",
                                  "Maximum number of matching files to return (default 50, max 2000)."),
                ["snippetsPerFile"] = ("1ファイルあたりに返す該当行スニペット数（既定 3・最大 20）。",
                                       "Number of snippet lines returned per file (default 3, max 20)."),
            },
            ["MappingsController.Generate"] = new()
            {
                ["url"] = ("マッピングJSONのURL（例: StormForge の …/Windows/<build>.json）。",
                           "URL of the mappings JSON (e.g. a StormForge …/Windows/<build>.json)."),
                ["path"] = ("ローカルのマッピングJSONパス（url の代わり）。", "Local path of a mappings JSON (alternative to url)."),
                ["fileName"] = ("出力する .usmap のファイル名（省略時はソース名）。", "Output .usmap file name (defaults to the source name)."),
                ["load"] = ("生成したマッピングをプロバイダに即適用（ホットスワップ。既定 false）。",
                            "Hot-load the generated mapping into the provider (default false)."),
                ["verify"] = ("生成した .usmap を再パースして件数・サンプルを検証（既定 true）。",
                              "Parse the generated .usmap back and report counts/samples (default true)."),
                ["download"] = ("生成した .usmap 本体をレスポンスで返す（既定 true）。false で JSON 統計のみ返す。",
                                "Return the generated .usmap as the response body (default true); false returns JSON stats only."),
            },
            ["AesController.Aes"] = new()
            {
                ["force"] = ("キャッシュを無視して Common DLL を再ダウンロード（既定 false）。",
                             "Re-download the Common DLL even if a cached copy exists (default false)."),
                ["noApi"] = ("fortnite-api を参照せず、バイナリ内の最高エントロピー候補を採用（既定 false）。",
                             "Don't consult fortnite-api; take the highest-entropy candidate from the binary (default false)."),
                ["submit"] = ("抽出鍵を provider に投入して pak をマウント（既定 true）。false で鍵のみ返す。",
                              "Submit the extracted key to the provider and mount paks (default true); false returns the key only."),
            },
            ["AesController.Extract"] = new()
            {
                ["verify"] = ("抽出鍵をライブ AES API と照合（既定 true）。",
                              "Cross-check the extracted key(s) against the live AES API (default true)."),
                ["force"] = ("キャッシュを無視して exe を再ダウンロード（既定 false）。",
                             "Re-download the exe even if a cached copy exists (default false)."),
                ["file"] = ("走査する別モジュール（ファイル名またはパス末尾）。既定の exe の代わりに指定。",
                            "A different manifest binary to scan (file name or path suffix) instead of the default exe."),
            },
            ["AesController.ScanLocal"] = new()
            {
                ["path"] = ("走査する単一ローカルファイルのパス。", "A single local file to scan."),
                ["dir"] = ("ローカルディレクトリ。内部の全 .exe/.dll を走査（サイズ降順）。",
                           "A local directory; every .exe/.dll inside is scanned (largest first)."),
                ["verify"] = ("検出鍵をライブ AES API と照合（既定 true）。",
                              "Cross-check found keys against the live AES API (default true)."),
            },
        };

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var key = $"{context.MethodInfo.DeclaringType?.Name}.{context.MethodInfo.Name}";
            var isJa = context.DocumentName == "ja";

            if (Operations.TryGetValue(key, out var loc))
            {
                var selected = isJa ? loc.Ja : loc.En;
                operation.Summary = selected.Summary;
                operation.Description = selected.Description;

                // Localize the success (2xx) response description(s).
                if (!string.IsNullOrEmpty(selected.Returns) && operation.Responses != null)
                {
                    foreach (var response in operation.Responses)
                    {
                        if (response.Key.StartsWith("2") && response.Value is OpenApiResponse concreteResponse)
                        {
                            concreteResponse.Description = selected.Returns;
                        }
                    }
                }
            }

            // Localize parameter descriptions.
            if (Parameters.TryGetValue(key, out var paramMap) && operation.Parameters != null)
            {
                foreach (var parameter in operation.Parameters)
                {
                    if (parameter is OpenApiParameter concreteParam &&
                        concreteParam.Name != null &&
                        paramMap.TryGetValue(concreteParam.Name, out var text))
                    {
                        concreteParam.Description = isJa ? text.Ja : text.En;
                    }
                }
            }
        }
    }
}
