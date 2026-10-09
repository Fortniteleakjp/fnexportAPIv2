using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse_Conversion.Textures;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Security.Cryptography;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Options;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RADADecoder;
using System.Text.RegularExpressions;
using FortnitePorting.Models;
using FortnitePorting.Services;

namespace FortnitePorting.Controllers
{
    public partial class ExportController
    {

        /// <summary>
        /// Resolves a request path to a loaded export with the same fallbacks as the main export
        /// endpoint: the package path, the package path plus its trailing object name, then the raw
        /// virtual file path.
        /// </summary>
        private bool TryResolveAsset(string path, out UObject? asset, out string processedPath)
        {
            try
            {
                processedPath = ConvertToPackagePath(Uri.UnescapeDataString(path).Trim());
            }
            catch
            {
                processedPath = ConvertToPackagePath(path.Trim());
            }

            if (_provider.TryLoadPackageObject(processedPath, out asset) && asset != null)
            {
                return true;
            }

            var lastPart = processedPath.Split('/').Last();
            if (_provider.TryLoadPackageObject($"{processedPath}.{lastPart}", out asset) && asset != null)
            {
                return true;
            }

            var normalized = path.Replace('\\', '/').Trim();
            if (!normalized.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".uasset";
            }

            if (FileIndex.For(_provider).TryGetFile(normalized, out var gameFile))
            {
                var package = _provider.LoadPackage(gameFile);
                asset = package.GetExportOrNull(Path.GetFileNameWithoutExtension(normalized), StringComparison.OrdinalIgnoreCase)
                        ?? package.GetExports().FirstOrDefault();
            }

            return asset != null;
        }

        /// <summary>
        /// Resolves any of the forms a caller addresses an asset by to the mounted file, through the path
        /// index: the stored virtual path, that path without its extension, an object path
        /// (<c>.../T_Foo.T_Foo</c>), and the <c>/Game/...</c> or <c>/PluginName/...</c> package paths.
        /// <para>
        /// The package loader above already resolves most of these, but it cannot when a plugin's mount
        /// point is missing, or when the export it is asked for is not named after its asset. The index
        /// knows the plugin assets by their stable "/PluginName/Content/..." tail, so the file is found
        /// regardless of how many feature folders the virtual mount puts in front of the plugin name.
        /// </para>
        /// </summary>
        /// <param name="exportName">The export to prefer once the package is loaded.</param>
        /// <returns>The mounted file, or null when this build has none of the forms.</returns>
        private CUE4Parse.FileProvider.Objects.GameFile? ResolveMountedAsset(string requested, out string exportName)
        {
            exportName = string.Empty;

            var value = (requested ?? string.Empty).Replace('\\', '/').Trim();
            if (value.Length == 0) return null;

            var index = FileIndex.For(_provider);

            // Split off a trailing object name: "Package.Object" addresses an export, and the ".Object"
            // half never appears in a stored path.
            var lastSlash = value.LastIndexOf('/');
            var lastDot = value.LastIndexOf('.');
            if (lastDot > lastSlash + 1 &&
                !value.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                !value.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                exportName = value[(lastDot + 1)..];
                value = value[..lastDot];
            }

            if (exportName.Length == 0)
            {
                exportName = Path.GetFileNameWithoutExtension(value);
            }

            // The path as it is stored, and the same path with the extension the caller left out.
            if (TryGetPackageFile(index, value, out var file)) return file;

            if (value.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                // /Game/Athena/... is FortniteGame/Content/Athena/...
                if (TryGetPackageFile(index, "FortniteGame/Content/" + value[6..], out file)) return file;
            }
            else if (value.StartsWith('/'))
            {
                // /PluginName/Path/Asset — the mount can carry feature folders before the plugin name,
                // so the asset is looked up by the "/PluginName/Content/Path/Asset" tail it always has.
                var relative = value[1..];
                var slash = relative.IndexOf('/');
                if (slash > 0)
                {
                    var tail = $"/{relative[..slash]}/Content/{relative[(slash + 1)..]}";
                    var resolved = index.TryResolvePluginAsset(tail + ".uasset")
                                   ?? index.TryResolvePluginAsset(tail + ".umap");
                    if (resolved != null && index.TryGetFile(resolved, out file)) return file;
                }
            }

            return null;
        }

        /// <summary>Looks a package path up as stored, then as .uasset and .umap.</summary>
        private static bool TryGetPackageFile(FileIndex index, string path,
            out CUE4Parse.FileProvider.Objects.GameFile? file)
        {
            if (index.TryGetFile(path, out var found) ||
                index.TryGetFile(path + ".uasset", out found) ||
                index.TryGetFile(path + ".umap", out found))
            {
                file = found;
                return true;
            }

            file = null;
            return false;
        }

        private string ConvertToPackagePath(string filePath)
        {
            filePath = filePath.Replace('\\', '/');

            // Remove the extension
            var ext = Path.GetExtension(filePath);
            if (ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) || ext.Equals(".umap", StringComparison.OrdinalIgnoreCase))
            {
                filePath = filePath.Substring(0, filePath.Length - ext.Length);
            }

            const string contentStr = "/Content/";
            var contentIndex = filePath.IndexOf(contentStr, StringComparison.OrdinalIgnoreCase);
            if (contentIndex == -1)
            {
                // If it is already a package path or a path that cannot be processed, return it as is
                return filePath;
            }

            const string pluginsStr = "/Plugins/";
            var pluginsIndex = filePath.IndexOf(pluginsStr, StringComparison.OrdinalIgnoreCase);

            if (pluginsIndex != -1 && pluginsIndex < contentIndex)
            {
                // For plugin assets
                // Example: FortniteGame/Plugins/GameFeatures/Figment/Content/Widgets/WBP_Figment.uasset
                var pathAfterPlugins = filePath.Substring(pluginsIndex + pluginsStr.Length);
                var contentInPluginIndex = pathAfterPlugins.IndexOf(contentStr, StringComparison.OrdinalIgnoreCase);
                var pluginNamePath = pathAfterPlugins.Substring(0, contentInPluginIndex);
                var pluginName = pluginNamePath.Split('/').Last();
                var assetPath = pathAfterPlugins.Substring(contentInPluginIndex + contentStr.Length);
                return $"/{pluginName}/{assetPath}";
            }

            // For base game assets
            // Example: FortniteGame/Content/Athena/Items/Cosmetics/Characters/CID_001.uasset
            var pathAfterContent = filePath.Substring(contentIndex + contentStr.Length);
            return $"/Game/{pathAfterContent}";
        }





        private string GetMountSnapshot()
        {
            if (_provider is AbstractVfsFileProvider vfsProvider)
            {
                return $"vfs={vfsProvider.MountedVfs.Count};files={_provider.Files.Count}";
            }

            return $"files={_provider.Files.Count}";
        }
    }
}
