using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using FortnitePorting.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Controllers
{
    public partial class SearchController
    {


        /// <summary>
        /// Matches one indexed path, or the name/stem slice of it. Takes a span so the scan can run
        /// straight over the index without cutting a substring per file.
        /// </summary>
        private delegate bool PathMatcher(ReadOnlySpan<char> value);

        /// <summary>
        /// Builds a predicate for the requested match mode. Throws <see cref="ArgumentException"/>
        /// for an unknown mode or an invalid regular expression.
        /// </summary>
        private static PathMatcher BuildMatcher(string q, string mode, bool caseSensitive)
        {
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var regexOptions = (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Compiled | RegexOptions.CultureInvariant;

            switch (mode)
            {
                case "contains":
                    return v => v.IndexOf(q, cmp) >= 0;
                case "prefix":
                    return v => v.StartsWith(q, cmp);
                case "suffix":
                    return v => v.EndsWith(q, cmp);
                case "exact":
                    return v => v.Equals(q, cmp);
                case "tokens":
                {
                    // Whitespace-separated words; every word must be present (AND).
                    var tokens = q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length == 0)
                    {
                        throw new ArgumentException("The 'q' parameter contains no search tokens.");
                    }
                    return v =>
                    {
                        foreach (var token in tokens)
                        {
                            if (v.IndexOf(token, cmp) < 0) return false;
                        }
                        return true;
                    };
                }
                case "wildcard":
                {
                    // Flat wildcard: * matches any run of characters (including '/'), ? a single one. Anchored.
                    var pattern = "^" + Regex.Escape(q).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    var rx = new Regex(pattern, regexOptions, RegexTimeout);
                    return v => SafeIsMatch(rx, v);
                }
                case "glob":
                {
                    // Path-aware glob, so a pattern can address one directory level at a time.
                    var globPattern = GlobToRegex(q);
                    Regex globRegex;
                    try
                    {
                        globRegex = new Regex(globPattern, regexOptions, RegexTimeout);
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException($"Invalid glob pattern: {ex.Message}");
                    }
                    return v => SafeIsMatch(globRegex, v);
                }
                case "regex":
                {
                    Regex rx;
                    try
                    {
                        rx = new Regex(q, regexOptions, RegexTimeout);
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException($"Invalid regular expression: {ex.Message}");
                    }
                    return v => SafeIsMatch(rx, v);
                }
                default:
                    throw new ArgumentException("The 'mode' parameter must be one of: contains, prefix, suffix, exact, wildcard, glob, regex, tokens.");
            }
        }

        private static bool SafeIsMatch(Regex rx, ReadOnlySpan<char> value)
        {
            try
            {
                return rx.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// Translates a path-aware glob into an anchored regular expression: <c>*</c> and <c>?</c> stop at
        /// '/', <c>**</c> crosses directory separators (<c>**/</c> also matches zero directories),
        /// <c>[abc]</c> / <c>[a-z]</c> / <c>[!abc]</c> are character classes and <c>{a,b}</c> is an
        /// alternation. Every other character matches literally.
        /// </summary>
        private static string GlobToRegex(string glob)
        {
            var sb = new StringBuilder(glob.Length * 2 + 2).Append('^');
            var braceDepth = 0;

            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                switch (c)
                {
                    case '*':
                        if (i + 1 < glob.Length && glob[i + 1] == '*')
                        {
                            i++;
                            // Collapse a longer run so '***' behaves like '**'.
                            while (i + 1 < glob.Length && glob[i + 1] == '*') i++;
                            if (i + 1 < glob.Length && glob[i + 1] == '/')
                            {
                                // '**/' spans any number of directories, including none at all.
                                i++;
                                sb.Append("(?:.*/)?");
                            }
                            else
                            {
                                sb.Append(".*");
                            }
                        }
                        else
                        {
                            sb.Append("[^/]*");
                        }
                        break;
                    case '?':
                        sb.Append("[^/]");
                        break;
                    case '[':
                    {
                        var end = FindGlobClassEnd(glob, i);
                        if (end < 0)
                        {
                            // An unterminated '[' matches literally rather than failing the request.
                            sb.Append("\\[");
                            break;
                        }
                        sb.Append(TranslateGlobClass(glob.Substring(i + 1, end - i - 1)));
                        i = end;
                        break;
                    }
                    case '{':
                        braceDepth++;
                        sb.Append("(?:");
                        break;
                    case '}':
                        if (braceDepth > 0)
                        {
                            braceDepth--;
                            sb.Append(')');
                        }
                        else
                        {
                            sb.Append("\\}");
                        }
                        break;
                    case ',':
                        // A comma separates alternatives only inside braces; elsewhere it is literal.
                        sb.Append(braceDepth > 0 ? '|' : ',');
                        break;
                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            if (braceDepth != 0)
            {
                throw new ArgumentException("The glob pattern has an unbalanced '{'.");
            }

            return sb.Append('$').ToString();
        }

        /// <summary>
        /// Returns the index of the ']' closing the character class opened at <paramref name="start"/>,
        /// or -1 when that class is never closed.
        /// </summary>
        private static int FindGlobClassEnd(string glob, int start)
        {
            var i = start + 1;
            if (i < glob.Length && (glob[i] == '!' || glob[i] == '^')) i++;
            // A ']' directly after the opening bracket is a member of the class, not its end.
            if (i < glob.Length && glob[i] == ']') i++;
            return i >= glob.Length ? -1 : glob.IndexOf(']', i);
        }

        /// <summary>
        /// Converts the body of a glob character class into a regex class. '-' keeps its range meaning;
        /// the characters that could close or reopen the class are escaped so a pattern cannot break out
        /// of it.
        /// </summary>
        private static string TranslateGlobClass(string body)
        {
            var negate = body.Length > 0 && (body[0] == '!' || body[0] == '^');
            var members = negate ? body.Substring(1) : body;
            if (members.Length == 0)
            {
                // '[]' / '[!]' hold no members, so the brackets themselves are the pattern.
                return Regex.Escape("[" + body + "]");
            }

            var sb = new StringBuilder(members.Length + 4).Append('[');
            if (negate) sb.Append('^');
            foreach (var c in members)
            {
                if (c is '\\' or ']' or '[' or '^') sb.Append('\\');
                sb.Append(c);
            }
            return sb.Append(']').ToString();
        }

        private static List<string> ParseExtensions(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext))
            {
                return new List<string>();
            }

            return ext
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? NormalizeDirPrefix(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            return dir.Replace('\\', '/').Trim().TrimEnd('/') + "/";
        }

        // The provider keys are forward-slash-delimited virtual paths, so slice on '/' directly
        // (avoids Path.* which also scans for '\\' on Windows and allocates more eagerly).
        private static string GetFileName(string key)
        {
            var slash = key.LastIndexOf('/');
            return slash >= 0 ? key.Substring(slash + 1) : key;
        }

        private static string GetExtension(string key)
        {
            var slash = key.LastIndexOf('/');
            var dot = key.LastIndexOf('.');
            return dot > slash ? key.Substring(dot) : string.Empty;
        }

        private static string GetFileStem(string key)
        {
            var slash = key.LastIndexOf('/');
            var start = slash + 1;
            var dot = key.LastIndexOf('.');
            var end = dot > slash ? dot : key.Length;
            return key.Substring(start, end - start);
        }

        private static int CanonicalRank(string path)
        {
            var ext = GetExtension(path);
            return (ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".umap", StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
        }

        /// <summary>
        /// Returns a "neighbourhood" prefix for a path: the GameFeature/plugin root when the path is
        /// under one, otherwise the immediate parent directory. Used to pull in content-only assets
        /// that sit beside a path match.
        /// </summary>
        private static string GetRelatedScope(string path)
        {
            const string gf = "/Plugins/GameFeatures/";
            const string pl = "/Plugins/";

            var i = path.IndexOf(gf, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var after = i + gf.Length;
                var slash = path.IndexOf('/', after);
                if (slash > 0) return path.Substring(0, slash + 1);
            }

            i = path.IndexOf(pl, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var after = i + pl.Length;
                var slash = path.IndexOf('/', after);
                if (slash > 0) return path.Substring(0, slash + 1);
            }

            var last = path.LastIndexOf('/');
            return last >= 0 ? path.Substring(0, last + 1) : string.Empty;
        }

        private static string RemoveCookedExtension(string path)
        {
            var ext = GetExtension(path);
            if (ext.Length > 0 && CookedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            {
                return path.Substring(0, path.Length - ext.Length);
            }
            return path;
        }

    }
}
