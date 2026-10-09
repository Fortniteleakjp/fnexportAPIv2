using System.Text;

namespace FortnitePorting.Services;

/// <summary>One hunk of a unified diff.</summary>
public sealed class DiffHunk
{
    /// <summary>1-based first line of the hunk in the old file.</summary>
    public int OldStart { get; set; }

    public int OldLines { get; set; }

    /// <summary>1-based first line of the hunk in the new file.</summary>
    public int NewStart { get; set; }

    public int NewLines { get; set; }

    /// <summary>The hunk's lines, each prefixed with <c>' '</c>, <c>'-'</c> or <c>'+'</c>.</summary>
    public List<string> Lines { get; set; } = [];

    /// <summary>The <c>@@ -a,b +c,d @@</c> header for this hunk.</summary>
    public string Header => $"@@ -{OldStart},{OldLines} +{NewStart},{NewLines} @@";
}

/// <summary>Result of diffing two texts line by line.</summary>
public sealed class LineDiffResult
{
    public int OldLineCount { get; set; }
    public int NewLineCount { get; set; }
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }

    /// <summary>True when the two texts are identical line for line.</summary>
    public bool Identical => AddedLines == 0 && RemovedLines == 0;

    /// <summary>True when the hunk list was cut short because the file changed too much.</summary>
    public bool Truncated { get; set; }

    public List<DiffHunk> Hunks { get; set; } = [];

    /// <summary>The whole diff rendered as unified-diff text.</summary>
    public string ToUnifiedDiff(string oldLabel, string newLabel)
    {
        var sb = new StringBuilder();
        sb.Append("--- ").AppendLine(oldLabel);
        sb.Append("+++ ").AppendLine(newLabel);
        foreach (var hunk in Hunks)
        {
            sb.AppendLine(hunk.Header);
            foreach (var line in hunk.Lines)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Line-level diffing used by the changelist endpoints.
/// <para>
/// The matching itself is a plain LCS over line hashes. That is O(n·m) in the worst case, so the two
/// cheap reductions a real diff tool also makes are applied first: identical head/tail lines are
/// trimmed off, and inputs that are still too large after trimming fall back to a coarse
/// "everything replaced" hunk rather than allocating a multi-gigabyte matrix.
/// </para>
/// </summary>
public static class TextDiff
{
    /// <summary>Largest trimmed line count either side may have before the LCS is abandoned.</summary>
    private const int MaxLcsLines = 6000;

    public static LineDiffResult Compute(string oldText, string newText, int contextLines = 3, int maxHunks = 500)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        return Compute(oldLines, newLines, contextLines, maxHunks);
    }

    public static LineDiffResult Compute(string[] oldLines, string[] newLines, int contextLines = 3, int maxHunks = 500)
    {
        var result = new LineDiffResult
        {
            OldLineCount = oldLines.Length,
            NewLineCount = newLines.Length
        };

        // Trim the identical head and tail: an asset export usually differs in a handful of places.
        var prefix = 0;
        var maxPrefix = Math.Min(oldLines.Length, newLines.Length);
        while (prefix < maxPrefix && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        var maxSuffix = Math.Min(oldLines.Length, newLines.Length) - prefix;
        while (suffix < maxSuffix &&
               oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
        {
            suffix++;
        }

        var oldMid = oldLines[prefix..(oldLines.Length - suffix)];
        var newMid = newLines[prefix..(newLines.Length - suffix)];

        if (oldMid.Length == 0 && newMid.Length == 0)
        {
            return result; // identical
        }

        List<(char Op, string Line)> script;
        if ((long)oldMid.Length * newMid.Length > (long)MaxLcsLines * MaxLcsLines)
        {
            // Too different to align line by line in reasonable time/memory: report the changed
            // region wholesale instead of pretending to a precision we did not compute.
            result.Truncated = true;
            script = [];
            script.AddRange(oldMid.Select(l => ('-', l)));
            script.AddRange(newMid.Select(l => ('+', l)));
        }
        else
        {
            script = BuildScript(oldMid, newMid);
        }

        result.AddedLines = script.Count(x => x.Op == '+');
        result.RemovedLines = script.Count(x => x.Op == '-');

        if (result.AddedLines == 0 && result.RemovedLines == 0)
        {
            return result;
        }

        // Re-attach the trimmed head/tail as context so hunk line numbers are absolute.
        var full = new List<(char Op, string Line)>(script.Count + prefix + suffix);
        for (var i = 0; i < prefix; i++)
        {
            full.Add((' ', oldLines[i]));
        }

        full.AddRange(script);
        for (var i = suffix; i > 0; i--)
        {
            full.Add((' ', oldLines[^i]));
        }

        result.Hunks = BuildHunks(full, contextLines, maxHunks, out var truncatedHunks);
        result.Truncated |= truncatedHunks;
        return result;
    }

    /// <summary>Splits text into lines, tolerating CRLF, LF and CR endings.</summary>
    public static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Classic LCS backtrack producing a ' '/'-'/'+' edit script.</summary>
    private static List<(char Op, string Line)> BuildScript(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;

        // lcs[i, j] = length of the longest common subsequence of a[i..] and b[j..].
        var lcs = new ushort[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? (ushort)(lcs[i + 1, j + 1] + 1)
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var script = new List<(char, string)>(n + m);
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                script.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                script.Add(('-', a[x]));
                x++;
            }
            else
            {
                script.Add(('+', b[y]));
                y++;
            }
        }

        while (x < n)
        {
            script.Add(('-', a[x++]));
        }

        while (y < m)
        {
            script.Add(('+', b[y++]));
        }

        return script;
    }

    /// <summary>Groups an edit script into unified-diff hunks with the requested context.</summary>
    private static List<DiffHunk> BuildHunks(List<(char Op, string Line)> script, int contextLines,
        int maxHunks, out bool truncated)
    {
        truncated = false;
        var hunks = new List<DiffHunk>();

        // Line number of each script entry in the old and the new file (1-based).
        var oldNo = new int[script.Count];
        var newNo = new int[script.Count];
        var oldCursor = 0;
        var newCursor = 0;
        for (var i = 0; i < script.Count; i++)
        {
            var op = script[i].Op;
            oldNo[i] = op == '+' ? oldCursor : ++oldCursor;
            newNo[i] = op == '-' ? newCursor : ++newCursor;
        }

        var index = 0;
        while (index < script.Count)
        {
            if (script[index].Op == ' ')
            {
                index++;
                continue;
            }

            if (hunks.Count >= maxHunks)
            {
                truncated = true;
                break;
            }

            var start = Math.Max(0, index - contextLines);
            var end = index;

            // Extend while the next change is close enough to share this hunk's context.
            while (end < script.Count)
            {
                var nextChange = -1;
                for (var probe = end + 1; probe < script.Count && probe <= end + (contextLines * 2) + 1; probe++)
                {
                    if (script[probe].Op != ' ')
                    {
                        nextChange = probe;
                        break;
                    }
                }

                if (nextChange < 0)
                {
                    break;
                }

                end = nextChange;
            }

            var stop = Math.Min(script.Count - 1, end + contextLines);

            var hunk = new DiffHunk
            {
                OldStart = FirstLineNumber(script, oldNo, start, stop, '+'),
                NewStart = FirstLineNumber(script, newNo, start, stop, '-')
            };

            for (var i = start; i <= stop; i++)
            {
                var (op, line) = script[i];
                hunk.Lines.Add(op + line);
                if (op != '+')
                {
                    hunk.OldLines++;
                }

                if (op != '-')
                {
                    hunk.NewLines++;
                }
            }

            hunks.Add(hunk);
            index = stop + 1;
        }

        return hunks;
    }

    /// <summary>First line number in the range that exists on the requested side (skipping <paramref name="skipOp"/>).</summary>
    private static int FirstLineNumber(List<(char Op, string Line)> script, int[] numbers, int start, int stop, char skipOp)
    {
        for (var i = start; i <= stop; i++)
        {
            if (script[i].Op != skipOp)
            {
                return numbers[i];
            }
        }

        // The whole hunk is made of lines that do not exist on this side; point just past the last one.
        return start > 0 ? numbers[start - 1] + 1 : 1;
    }
}
