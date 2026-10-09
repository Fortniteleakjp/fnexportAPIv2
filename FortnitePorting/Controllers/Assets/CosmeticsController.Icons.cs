using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Controllers
{
    public partial class CosmeticsController
    {

        /// <summary>
        /// Returns the cosmetic's icon as a PNG, so an ID can be turned into an image in one call.
        /// </summary>
        /// <param name="id">Cosmetic ID or asset name.</param>
        /// <param name="variant">large (default) uses LargeIcon, small uses Icon, offercatalog uses the
        /// OfferCatalog texture. large and small fall back to the other icon and then to OfferCatalog.</param>
        [HttpGet("~/api/v1/cosmetics/{id}/icon")]
        public IActionResult GetCosmeticIcon(string id, [FromQuery] string variant = "large")
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest(new { message = "The 'id' parameter is required." });
            }

            variant = (variant ?? "large").Trim().ToLowerInvariant();
            if (variant != "large" && variant != "small" && variant != "offercatalog")
            {
                return BadRequest(new { message = "The 'variant' parameter must be large, small, or offercatalog." });
            }

            if (_provider is not AbstractVfsFileProvider vfsProvider)
            {
                return BadRequest(new { message = "The provider is not a VFS provider." });
            }

            var cacheKey = $"{_scope}cosmeticicon::{id}::{variant}::vfs={vfsProvider.MountedVfs.Count};files={_provider.Files.Count}";
            if (_cache.TryGetValue(cacheKey, out CachedIcon? cachedIcon) && cachedIcon != null)
            {
                return SendIcon(cachedIcon);
            }

            var match = FindCosmeticById(vfsProvider, id);
            if (match == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Cosmetic Not Found",
                    Detail = $"No cosmetic matched the ID '{id}'.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            ReadIconPaths(match.Path, out var largeIcon, out var icon);
            var offerCatalog = MatchOfferCatalog(match.Path, BuildOfferCatalogIndex(vfsProvider.MountedVfs));

            // The requested icon first, then the other one, then the OfferCatalog texture: a cosmetic
            // that only ships one of them still answers with an image.
            var candidates = variant switch
            {
                "small" => new[] { icon, largeIcon, offerCatalog },
                "offercatalog" => new[] { offerCatalog },
                _ => new[] { largeIcon, icon, offerCatalog }
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !TryLoadTexture(candidate, out var texture) || texture == null)
                {
                    continue;
                }

                var decoded = texture.Decode();
                if (decoded == null)
                {
                    _logger.LogWarning("Could not decode the texture {Texture} for cosmetic {Id}", candidate, id);
                    continue;
                }

                var png = decoded.Encode(ETextureFormat.Png, false, out _);
                var result = new CachedIcon(png, candidate, texture.Name);
                _cache.Set(cacheKey, result, TimeSpan.FromMinutes(30));
                return SendIcon(result);
            }

            return NotFound(new ProblemDetails
            {
                Title = "Icon Not Found",
                Detail = $"No {variant} icon could be decoded for '{id}'.",
                Status = StatusCodes.Status404NotFound,
                Extensions =
                {
                    { "cosmeticPath", match.Path },
                    { "largeIcon", largeIcon },
                    { "icon", icon },
                    { "offerCatalog", offerCatalog }
                }
            });
        }

        /// <summary>A decoded cosmetic icon plus where it came from.</summary>
        private sealed record CachedIcon(byte[] Png, string SourcePath, string TextureName);

        /// <summary>Sends the PNG, reporting the texture it was decoded from in the response headers.</summary>
        private IActionResult SendIcon(CachedIcon icon)
        {
            Response.Headers["X-Icon-Source"] = icon.SourcePath;
            Response.Headers["X-Icon-Name"] = icon.TextureName;
            return File(icon.Png, "image/png");
        }

        /// <summary>One cosmetic resolved from an ID, with every candidate that matched as strongly.</summary>
        private sealed record CosmeticMatch(string Path, string MatchType, List<string> Candidates);

        /// <summary>
        /// Resolves a cosmetic ID against every mounted PAK. Matching runs from strongest to weakest:
        /// the exact asset name, then Prefix_ID (so HonestWasp finds Character_HonestWasp), then a
        /// substring. The first tier that matches wins, and its candidates are returned in path order.
        /// </summary>
        private CosmeticMatch? FindCosmeticById(AbstractVfsFileProvider vfsProvider, string id)
        {
            var needle = id.Trim();
            if (needle.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            {
                needle = needle[..^".uasset".Length];
            }

            // An ID given as a full path is reduced to its asset name.
            var slash = needle.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0)
            {
                needle = needle[(slash + 1)..];
            }

            if (needle.Length == 0)
            {
                return null;
            }

            var exact = new List<string>();
            var suffix = new List<string>();
            var contains = new List<string>();

            foreach (var path in EnumerateUassetFiles(vfsProvider.MountedVfs, CosmeticsDir))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Equals(needle, StringComparison.OrdinalIgnoreCase))
                {
                    exact.Add(path);
                }
                else if (name.EndsWith("_" + needle, StringComparison.OrdinalIgnoreCase))
                {
                    suffix.Add(path);
                }
                else if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    contains.Add(path);
                }
            }

            if (exact.Count > 0) return new CosmeticMatch(exact[0], "exact", exact);
            if (suffix.Count > 0) return new CosmeticMatch(suffix[0], "suffix", suffix);
            if (contains.Count > 0) return new CosmeticMatch(contains[0], "contains", contains);
            return null;
        }

        /// <summary>Reads the LargeIcon / Icon asset paths out of a cosmetic definition.</summary>
        private void ReadIconPaths(string path, out string? largeIcon, out string? icon)
        {
            largeIcon = null;
            icon = null;

            try
            {
                if (!FileIndex.For(_provider).TryGetFile(path, out var gameFile))
                {
                    return;
                }

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });

                foreach (var export in _provider.LoadPackage(gameFile).GetExports())
                {
                    JToken token;
                    try
                    {
                        token = JToken.FromObject(export, serializer);
                    }
                    catch
                    {
                        continue;
                    }

                    var props = GetChild(token, "Properties") ?? token;
                    var dataList = GetChild(props, "DataList");
                    largeIcon ??= GetAssetPath(dataList, props, token, "LargeIcon");
                    icon ??= GetAssetPath(dataList, props, token, "Icon");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read the icon paths of {Path}", path);
            }
        }

        /// <summary>
        /// Loads a texture from either an object path (/Game/...T_Foo.T_Foo, as icons are stored) or a
        /// virtual file path (as the OfferCatalog index holds them).
        /// </summary>
        private bool TryLoadTexture(string assetPath, out UTexture2D? texture)
        {
            texture = null;

            try
            {
                if (FileIndex.For(_provider).TryGetFile(assetPath, out var gameFile))
                {
                    texture = _provider.LoadPackage(gameFile).GetExports().OfType<UTexture2D>().FirstOrDefault();
                    if (texture != null) return true;
                }

                if (_provider.TryLoadPackageObject(assetPath, out var loaded) && loaded is UTexture2D direct)
                {
                    texture = direct;
                    return true;
                }

                // /Game/Path/T_Foo.T_Foo -> /Game/Path/T_Foo, for providers that want the package path.
                var dot = assetPath.LastIndexOf('.');
                if (dot > 0 &&
                    _provider.TryLoadPackageObject(assetPath[..dot], out var fallback) &&
                    fallback is UTexture2D fallbackTexture)
                {
                    texture = fallbackTexture;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load the texture {AssetPath}", assetPath);
            }

            return texture != null;
        }

    }
}
