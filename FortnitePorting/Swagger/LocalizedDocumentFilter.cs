using System.Collections.Generic;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FortnitePorting.Swagger
{
    /// <summary>
    /// Localizes the document-level tag descriptions (which come from the controllers'
    /// class-level XML summaries) for the active Swagger document ("ja" or "en").
    /// Registered after IncludeXmlComments so it overrides the XML text.
    /// </summary>
    public sealed class LocalizedDocumentFilter : IDocumentFilter
    {
        // Keyed by tag name (= controller name without the "Controller" suffix). (Ja, En).
        private static readonly Dictionary<string, (string Ja, string En)> TagDescriptions = new()
        {
            ["Export"] = (
                "アセットエクスポート用エンドポイント。アセットを JSON／画像（PNG）／音声で取得するほか、ローカライズ（locres）データや PAK 内ファイル一覧を提供します。",
                "Asset export endpoints: retrieve assets as JSON, image (PNG), or audio, plus localization (locres) data and the file listing inside PAK archives."),
            ["Items"] = (
                "特定の接頭辞（WID_ / AGID_ / Athena_ / Figment_Athena_）で始まるアセットを一覧・抽出するためのエンドポイント群。",
                "A set of endpoints for listing and extracting assets whose names start with a specific prefix (WID_ / AGID_ / Athena_ / Figment_Athena_)."),
            ["Debug"] = (
                "読み込まれた仮想ファイルシステムを調査するための診断用エンドポイント（ファイル一覧・部分一致検索・マウント済み PAK／UTOC）。",
                "Diagnostic endpoints for inspecting the loaded virtual file system: file listing, substring search, and the mounted PAK / UTOC archives."),
            ["Cosmetics"] = (
                "指定した PAK／チャンク内のコスメ（BRCosmetics）アイテム定義を読み取るエンドポイント。",
                "Endpoints that read cosmetic (BRCosmetics) item definitions out of a specific PAK / chunk."),
            ["Search"] = (
                "全ファイルを対象とした文字列検索エンドポイント。ファイルパス／名の高速検索（部分一致・接頭辞・正規表現・ワイルドカード等）と、アセット内容（プロパティ）への限定的な全文検索を提供します。",
                "Full-text search endpoints over all files: fast path/name search (substring, prefix, regex, wildcard, etc.) and a bounded content search inside parsed asset properties."),
            ["Mappings"] = (
                "マッピング生成エンドポイント。マッピングの JSON 版（{ Version, Enums, Structs, Classes }）から CUE4Parse 用の .usmap バイナリを生成・適用します。",
                "Mapping generation endpoints: build a CUE4Parse .usmap from a JSON mappings dump and optionally apply it."),
            ["Aes"] = (
                "AES 鍵取得エンドポイント。UEFN（Fortnite_Studio）の Common DLL をマニフェストからダウンロードし、外部 AesFinder ツールで MainAES 鍵を抽出して provider に投入・マウントします（起動・注入なし）。補助としてビルトインのスケジュール走査・ローカル走査・自己テストも提供します。",
                "AES key endpoints: download the UEFN (Fortnite_Studio) Common DLL from the manifest, extract the MainAES key with the external AesFinder tool, and submit/mount it on the provider (no launch, no injection). Built-in schedule scanning, local scanning, and a self-test are also provided as helpers."),
        };

        public void Apply(OpenApiDocument document, DocumentFilterContext context)
        {
            if (document.Tags == null)
            {
                return;
            }

            var isJa = context.DocumentName == "ja";
            foreach (var tag in document.Tags)
            {
                if (tag is OpenApiTag concreteTag &&
                    concreteTag.Name != null &&
                    TagDescriptions.TryGetValue(concreteTag.Name, out var text))
                {
                    concreteTag.Description = isJa ? text.Ja : text.En;
                }
            }
        }
    }
}
