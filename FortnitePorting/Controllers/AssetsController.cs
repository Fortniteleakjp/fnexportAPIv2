using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Controllers;

/// <summary>
/// Inspects relationships between loaded Unreal asset packages.
/// </summary>
[ApiController]
[Route("api/v1/assets")]
public sealed class AssetsController : ControllerBase
{
    private readonly IFileProvider _provider;
    private readonly JsonSerializer _serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
    });

    public AssetsController(IFileProvider provider)
    {
        _provider = provider;
    }

    /// <summary>Returns direct and shallow recursive references from an asset package.</summary>
    /// <param name="path">Asset path, for example FortniteGame/Content/.../Asset.uasset.</param>
    /// <param name="depth">Recursive depth from 0 to 3. The default is 1.</param>
    /// <param name="limit">Maximum number of dependency entries, from 1 to 500.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("dependencies")]
    public IActionResult GetDependencies(
        [FromQuery] string? path,
        [FromQuery] int depth = 1,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { message = "The 'path' parameter is required." });
        }

        depth = Math.Clamp(depth, 0, 3);
        limit = Math.Clamp(limit, 1, 500);
        var normalized = NormalizeInputPath(path);

        if (!TryLoadPackage(normalized, out var package))
        {
            return NotFound(new ProblemDetails
            {
                Title = "アセットが見つかりません",
                Detail = $"指定されたアセット '{path}' を読み込めませんでした。",
                Status = StatusCodes.Status404NotFound
            });
        }

        var dependencies = new List<DependencyEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        CollectDependencies(
            normalized,
            package,
            0,
            depth,
            limit,
            dependencies,
            keys,
            visitedPackages,
            ref truncated,
            cancellationToken);

        return Ok(new
        {
            root = new
            {
                requestedPath = path,
                package = package.Name,
                exportCount = package.ExportMapLength,
                canDeserialize = package.CanDeserialize
            },
            maxDepth = depth,
            limit,
            truncated,
            dependencies
        });
    }

    private void CollectDependencies(
        string packagePath,
        IPackage package,
        int currentDepth,
        int maxDepth,
        int limit,
        List<DependencyEntry> dependencies,
        HashSet<string> keys,
        HashSet<string> visitedPackages,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !visitedPackages.Add(packagePath)) return;

        foreach (var reference in ExtractReferences(package))
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (dependencies.Count >= limit)
            {
                truncated = true;
                return;
            }

            var resolvedPath = ResolveVirtualPath(reference.Raw);
            var key = $"{reference.Kind}:{resolvedPath ?? reference.Raw}";
            if (string.IsNullOrWhiteSpace(reference.Raw) || !keys.Add(key)) continue;
            if (string.Equals(resolvedPath, packagePath, StringComparison.OrdinalIgnoreCase)) continue;

            IPackage? childPackage = null;
            string? assetType = null;
            if (resolvedPath != null && TryLoadPackage(resolvedPath, out childPackage))
            {
                assetType = GetPrimaryAssetType(childPackage);
            }

            dependencies.Add(new DependencyEntry
            {
                Reference = reference.Raw,
                Kind = reference.Kind,
                Path = resolvedPath,
                AssetType = assetType,
                Depth = currentDepth + 1
            });

            if (currentDepth >= maxDepth || resolvedPath == null) continue;
            if (childPackage != null)
            {
                CollectDependencies(
                    resolvedPath,
                    childPackage,
                    currentDepth + 1,
                    maxDepth,
                    limit,
                    dependencies,
                    keys,
                    visitedPackages,
                    ref truncated,
                    cancellationToken);
            }
        }
    }

    private IEnumerable<ReferenceCandidate> ExtractReferences(IPackage package)
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Package-level soft references are cheap and available on classic .uasset packages.
        if (package is Package parsedPackage)
        {
            foreach (var softPath in parsedPackage.SoftObjectPaths)
            {
                AddCandidate(candidates, softPath.ToString(), "soft");
            }

            // Imports provide hard references. Resolution is intentionally best-effort because
            // some imports point to native / script classes rather than cooked assets.
            for (var i = 0; i < parsedPackage.ImportMapLength; i++)
            {
                try
                {
                    var resolved = new FPackageIndex(package, -i - 1).ResolvedObject;
                    AddCandidate(candidates, resolved?.GetPathName(), "hard");
                }
                catch
                {
                    // A broken optional import should not prevent the rest of the graph from returning.
                }
            }
        }

        // This fallback also covers IoStore packages and references embedded in custom Fortnite structs.
        foreach (var export in package.GetExports())
        {
            try
            {
                WalkSerializedReferences(JToken.FromObject(export, _serializer), candidates);
            }
            catch
            {
                // Continue with references from the remaining exports.
            }
        }

        return candidates.Select(x => new ReferenceCandidate(x.Key, x.Value));
    }

    private static string? GetPrimaryAssetType(IPackage package)
    {
        try
        {
            return package.GetExports().FirstOrDefault()?.ExportType;
        }
        catch
        {
            // The reference can still be returned when the target package has incomplete mappings.
            return null;
        }
    }

    private static void WalkSerializedReferences(JToken token, IDictionary<string, string> candidates)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (property.Name.Equals("AssetPathName", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("ObjectPath", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("PackageName", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var value in ExtractReferenceStrings(property.Value))
                    {
                        AddCandidate(candidates, value, property.Name.Equals("AssetPathName", StringComparison.OrdinalIgnoreCase) ? "soft" : "hard");
                    }
                }

                WalkSerializedReferences(property.Value, candidates);
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array) WalkSerializedReferences(item, candidates);
        }
    }

    private static IEnumerable<string> ExtractReferenceStrings(JToken token)
    {
        if (token.Type == JTokenType.String)
        {
            var value = token.ToString();
            if (LooksLikeAssetReference(value)) yield return value;
            yield break;
        }

        if (token is JObject obj)
        {
            var packageName = obj.Properties().FirstOrDefault(x => x.Name.Equals("PackageName", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
            var assetName = obj.Properties().FirstOrDefault(x => x.Name.Equals("AssetName", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(packageName) && LooksLikeAssetReference(packageName))
            {
                yield return string.IsNullOrWhiteSpace(assetName) ? packageName : $"{packageName}.{assetName}";
            }
        }
    }

    private static void AddCandidate(IDictionary<string, string> candidates, string? value, string kind)
    {
        var cleaned = CleanReference(value);
        if (cleaned == null || !LooksLikeAssetReference(cleaned) || candidates.ContainsKey(cleaned)) return;
        candidates[cleaned] = kind;
    }

    private static string? CleanReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value.Trim().Trim('\'', '"');
        var quote = result.IndexOf('\'');
        if (quote >= 0) result = result[(quote + 1)..].TrimEnd('\'');
        var colon = result.IndexOf(':');
        if (colon >= 0) result = result[..colon];
        return result;
    }

    private static bool LooksLikeAssetReference(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ||
                (value.StartsWith('/') && !value.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) && value.Contains('/')));
    }

    private bool TryLoadPackage(string path, out IPackage package)
    {
        var candidates = new[] { path, NormalizeInputPath(path) }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (_provider.TryLoadPackage(candidate, out package!)) return true;
            var withoutExtension = candidate.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                                   candidate.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
                ? candidate[..candidate.LastIndexOf('.')]
                : candidate;
            if (!string.Equals(withoutExtension, candidate, StringComparison.OrdinalIgnoreCase) &&
                _provider.TryLoadPackage(withoutExtension, out package!)) return true;
        }

        package = null!;
        return false;
    }

    private string? ResolveVirtualPath(string raw)
    {
        var cleaned = CleanReference(raw);
        if (!LooksLikeAssetReference(cleaned)) return null;

        var packageName = cleaned!;
        var dot = packageName.LastIndexOf('.');
        if (dot > 0) packageName = packageName[..dot];

        string prefix;
        if (packageName.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            prefix = $"FortniteGame/Content/{packageName[6..]}";
        }
        else
        {
            var relative = packageName.TrimStart('/');
            var slash = relative.IndexOf('/');
            if (slash <= 0) return null;
            var plugin = relative[..slash];
            var pluginAssetPath = relative[(slash + 1)..];
            var pluginSuffix = $"/{plugin}/Content/{pluginAssetPath}";

            // The virtual mount can contain feature folders before the plugin name, for example
            // FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/.... Resolve by the stable
            // '/PluginName/Content/...' suffix rather than assuming Plugins/{plugin}/Content/.
            return _provider.Files.Keys.FirstOrDefault(x =>
            {
                var normalized = x.Replace('\\', '/');
                return normalized.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase) &&
                       (normalized.EndsWith($"{pluginSuffix}.uasset", StringComparison.OrdinalIgnoreCase) ||
                        normalized.EndsWith($"{pluginSuffix}.umap", StringComparison.OrdinalIgnoreCase));
            });
        }

        return _provider.Files.Keys.FirstOrDefault(x =>
            x.Equals(prefix + ".uasset", StringComparison.OrdinalIgnoreCase) ||
            x.Equals(prefix + ".umap", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeInputPath(string path)
    {
        var value = Uri.UnescapeDataString(path.Trim()).Replace('\\', '/');
        var query = value.IndexOf('?');
        if (query >= 0) value = value[..query];
        if (value.StartsWith("FortniteGame/Content/", StringComparison.OrdinalIgnoreCase))
        {
            value = "/Game/" + value[21..];
        }
        else if (value.StartsWith("FortniteGame/Plugins/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[21..];
            var content = rest.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (content > 0)
            {
                // A Fortnite plugin path may contain feature folders before the plugin name,
                // for example GameFeatures/BRCosmetics/Content/.... CUE4Parse's package path
                // uses only the plugin mount name: /BRCosmetics/....
                var pluginPath = rest[..content];
                var pluginName = pluginPath[(pluginPath.LastIndexOf('/') + 1)..];
                value = "/" + pluginName + "/" + rest[(content + 9)..];
            }
        }
        return value;
    }

    private sealed record ReferenceCandidate(string Raw, string Kind);

    private sealed class DependencyEntry
    {
        public string Reference { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string? Path { get; init; }
        public string? AssetType { get; init; }
        public int Depth { get; init; }
    }
}
