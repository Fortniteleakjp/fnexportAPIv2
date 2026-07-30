using System.Collections.Generic;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FortnitePorting.Swagger;

/// <summary>Localizes the controller tag descriptions in the Japanese and English OpenAPI documents.</summary>
public sealed class LocalizedDocumentFilter : IDocumentFilter
{
    private static readonly Dictionary<string, (string Ja, string En)> Tags = new()
    {
        ["Export"] = ("アセットのJSON・画像・音声エクスポート、ローカライズ、PAK内ファイル一覧。", "Asset JSON/image/audio export, localization, and PAK file listing."),
        ["Items"] = ("アイテム接頭辞によるアセット一覧とプロパティ抽出。", "Item asset listing and property extraction by name prefix."),
        ["Debug"] = ("ローカルVFSの診断用ファイル・PAK確認。", "Diagnostics for the local virtual file system and mounted archives."),
        ["Cosmetics"] = ("PAK単位および全マウントPAK横断のコスメ抽出。", "Cosmetic extraction scoped to one PAK or across all mounted PAKs."),
        ["Search"] = ("ファイルパスとアセット・設定内容の検索。", "Search across file paths and asset/config contents."),
        ["Mappings"] = ("マッピングJSONからCUE4Parse用.usmapを生成。", "Generate CUE4Parse .usmap files from mappings JSON."),
        ["Aes"] = ("ローカル実行環境でのAESキー抽出・適用。", "AES key extraction and application in the local process."),
        ["Pak"] = ("現在マウントされているPAK/UTOCの一覧と内容。", "Mounted PAK/UTOC inventory and contents."),
        ["Config"] = ("読み込み済みINIファイルの一覧と設定値検索。", "Loaded INI file listing and configuration lookup."),
        ["Assets"] = ("アセット間のハード参照・ソフト参照の解析。", "Hard and soft reference inspection between assets.")
    };

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        if (document.Tags == null) return;
        var isJa = context.DocumentName == "ja";
        foreach (var tag in document.Tags)
        {
            if (tag is OpenApiTag openApiTag && openApiTag.Name != null && Tags.TryGetValue(openApiTag.Name, out var description))
            {
                openApiTag.Description = isJa ? description.Ja : description.En;
            }
        }
    }
}
