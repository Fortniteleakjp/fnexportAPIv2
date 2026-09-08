using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FortnitePorting.Controllers;

/// <summary>
/// Lookups over the mounted build's .locres tables: resolve an FText key into every language, or
/// find the namespace/key behind a string that is visible in game.
/// </summary>
[ApiController]
[Route("api/v1/localization")]
public sealed class LocalizationController : ControllerBase
{
    private readonly IFileProvider _provider;

    // Cap regex evaluation per entry so one pathological pattern cannot dominate a scan.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    // Reject overly long patterns (compilation cost is attacker-amplifiable).
    private const int MaxPatternLength = 1000;
    // Ceiling on results a caller may ask for in one response.
    private const int MaxResultLimit = 500;
    // Ceiling on matches collected before sorting, so a query like text=e cannot exhaust memory.
    private const int MaxCollectedMatches = 10_000;

    public LocalizationController(IFileProvider provider)
    {
        _provider = provider;
    }

    /// <summary>Lists the language codes the mounted build ships .locres files for.</summary>
    [HttpGet("languages")]
    public IActionResult GetLanguages()
    {
        return Ok(new { languages = LocalizationService.GetAvailableLanguages(_provider) });
    }

    /// <summary>
    /// Resolves a localization key, or finds the key behind a string. Pass key (optionally with
    /// namespace) to get that entry in every language, or text to search the translations themselves.
    /// </summary>
    /// <param name="key">FText key to resolve, for example 5A6C1F0E4B2D...</param>
    /// <param name="ns">Optional namespace the key belongs to. Every namespace is searched when omitted.</param>
    /// <param name="text">Localized string to search for instead of a key (reverse lookup).</param>
    /// <param name="mode">Reverse-lookup match mode: contains (default) / exact / prefix / suffix / regex.</param>
    /// <param name="lang">Single language to use. Defaults to every available language.</param>
    /// <param name="langs">Comma-separated languages to use. all or * means every available language.</param>
    /// <param name="caseSensitive">Match the text case-sensitively (default false).</param>
    /// <param name="withTranslations">For a reverse lookup, also resolve each hit into every available language.</param>
    /// <param name="maxResults">Maximum reverse-lookup entries to return (default 50, max 500).</param>
    [HttpGet("lookup")]
    public IActionResult Lookup(
        [FromQuery] string? key = null,
        [FromQuery(Name = "namespace")] string? ns = null,
        [FromQuery] string? text = null,
        [FromQuery] string mode = "contains",
        [FromQuery] string? lang = null,
        [FromQuery] string? langs = null,
        [FromQuery] bool caseSensitive = false,
        [FromQuery] bool withTranslations = false,
        [FromQuery] int maxResults = 50)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        ns = string.IsNullOrWhiteSpace(ns) ? null : ns.Trim();
        // The searched text keeps its surrounding whitespace: it is part of the string being matched.
        text = string.IsNullOrEmpty(text) ? null : text;

        if (key == null && text == null)
        {
            return BadRequest(new { message = "Either 'key' (forward lookup) or 'text' (reverse lookup) is required." });
        }

        if (key != null && text != null)
        {
            return BadRequest(new { message = "Specify either 'key' or 'text', not both." });
        }

        var available = LocalizationService.GetAvailableLanguages(_provider);
        if (available.Count == 0)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Localization Not Found",
                Detail = "The mounted build exposes no .locres files.",
                Status = StatusCodes.Status404NotFound
            });
        }

        var languages = ResolveLanguages(langs ?? lang, available, out var unknownLanguages);
        if (languages.Count == 0)
        {
            return BadRequest(new
            {
                message = "None of the requested languages are available in this build.",
                requested = unknownLanguages,
                availableLanguages = available
            });
        }

        return key != null
            ? LookupByKey(key, ns, languages, unknownLanguages)
            : LookupByText(text!, mode, languages, unknownLanguages, available, caseSensitive, withTranslations, maxResults);
    }

    /// <summary>
    /// Forward lookup: collects the key's value in each requested language, grouped by the namespace
    /// it was found in (the same key can exist in more than one namespace).
    /// </summary>
    private IActionResult LookupByKey(string key, string? ns, List<string> languages, List<string> unknownLanguages)
    {
        var byNamespace = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var language in languages)
        {
            foreach (var entry in LocalizationService.Load(_provider, language))
            {
                if (ns != null && !entry.Key.Equals(ns, StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.Value.TryGetValue(key, out var value)) continue;

                if (!byNamespace.TryGetValue(entry.Key, out var translations))
                {
                    translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    byNamespace[entry.Key] = translations;
                }

                translations[language] = value;
            }
        }

        return Ok(new
        {
            key,
            @namespace = ns,
            languages,
            unknownLanguages,
            found = byNamespace.Count > 0,
            totalMatches = byNamespace.Count,
            results = byNamespace
                .Select(entry => new { @namespace = entry.Key, key, translations = entry.Value })
                .ToList()
        });
    }

    /// <summary>
    /// Reverse lookup: scans the translations of the requested languages for a string and reports the
    /// namespace/key behind each hit, so a string seen in game can be traced back to its FText.
    /// </summary>
    private IActionResult LookupByText(
        string text, string mode, List<string> languages, List<string> unknownLanguages,
        List<string> available, bool caseSensitive, bool withTranslations, int maxResults)
    {
        mode = (mode ?? "contains").Trim().ToLowerInvariant();
        maxResults = Math.Clamp(maxResults, 1, MaxResultLimit);

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Func<string, bool> matcher;

        switch (mode)
        {
            case "contains":
                matcher = value => value.Contains(text, comparison);
                break;
            case "exact":
                matcher = value => value.Equals(text, comparison);
                break;
            case "prefix":
                matcher = value => value.StartsWith(text, comparison);
                break;
            case "suffix":
                matcher = value => value.EndsWith(text, comparison);
                break;
            case "regex":
            {
                if (text.Length > MaxPatternLength)
                {
                    return BadRequest(new { message = $"The pattern is too long (max {MaxPatternLength} characters)." });
                }

                Regex regex;
                try
                {
                    var options = (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) |
                                  RegexOptions.Compiled | RegexOptions.CultureInvariant;
                    regex = new Regex(text, options, RegexTimeout);
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = $"Invalid regular expression: {ex.Message}" });
                }

                matcher = value =>
                {
                    try
                    {
                        return regex.IsMatch(value);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return false;
                    }
                };
                break;
            }
            default:
                return BadRequest(new { message = "The 'mode' parameter must be one of: contains, exact, prefix, suffix, regex." });
        }

        var matches = new List<(string Namespace, string Key, string Language, string Value)>();
        var truncated = false;

        foreach (var language in languages)
        {
            foreach (var entry in LocalizationService.Load(_provider, language))
            {
                foreach (var pair in entry.Value)
                {
                    if (!matcher(pair.Value)) continue;

                    matches.Add((entry.Key, pair.Key, language, pair.Value));
                    if (matches.Count >= MaxCollectedMatches)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (truncated) break;
            }

            if (truncated) break;
        }

        // The tables are hash maps, so a stable order has to be imposed before the result is cut down.
        matches.Sort((left, right) =>
        {
            var byNamespace = string.CompareOrdinal(left.Namespace, right.Namespace);
            if (byNamespace != 0) return byNamespace;
            var byKey = string.CompareOrdinal(left.Key, right.Key);
            return byKey != 0 ? byKey : string.CompareOrdinal(left.Language, right.Language);
        });

        var results = matches
            .Take(maxResults)
            .Select(match => new
            {
                @namespace = match.Namespace,
                key = match.Key,
                lang = match.Language,
                value = match.Value,
                translations = withTranslations ? ResolveTranslations(match.Namespace, match.Key, available) : null
            })
            .ToList();

        return Ok(new
        {
            text,
            mode,
            caseSensitive,
            languages,
            unknownLanguages,
            totalMatches = matches.Count,
            // True when the scan stopped at the collection cap, so further matches exist.
            truncated,
            returned = results.Count,
            results
        });
    }

    /// <summary>Resolves one (namespace, key) into every given language.</summary>
    private Dictionary<string, string> ResolveTranslations(string ns, string key, IEnumerable<string> languages)
    {
        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
        {
            var table = LocalizationService.Load(_provider, language);
            if (table.TryGetValue(ns, out var entries) && entries.TryGetValue(key, out var value))
            {
                translations[language] = value;
            }
        }

        return translations;
    }

    /// <summary>
    /// Resolves the requested language codes against the ones this build ships. An empty value, all,
    /// or * selects every available language; codes that do not exist are reported back to the caller.
    /// </summary>
    private static List<string> ResolveLanguages(string? requested, List<string> available, out List<string> unknown)
    {
        unknown = new List<string>();

        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Trim() is "all" or "*")
        {
            return available;
        }

        var resolved = new List<string>();
        foreach (var candidate in requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = available.FirstOrDefault(x => x.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                unknown.Add(candidate);
            }
            else if (!resolved.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                resolved.Add(match);
            }
        }

        return resolved;
    }
}
