using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;

namespace FortnitePorting.Services.Mappings.RowStructInference;

/// <summary>
/// Finds the DataTables of the mounted game whose row struct is missing from a mapping and guesses those structs from
/// the cooked rows. UEFN's DLLs, which the static generator reads, don't contain the game feature plugins that are only
/// compiled into the game client (e.g. DelilahTreasureHuntSharedRuntime in 43.00), so their row structs never make it
/// into a generated mapping otherwise.
/// </summary>
public static class RowStructScanner
{
    public sealed record Inferred(string Struct, string Table, int Rows, int Properties, bool Unproven, string Layout);

    public sealed record Failed(string Struct, string Table, int Rows, string Reason);

    public sealed record Result(IReadOnlyList<Struct> Structs, IReadOnlyList<Inferred> Inferred, IReadOnlyList<Failed> Failed,
        int Candidates, int DataTables, double ElapsedSeconds);

    /// <summary>Tables tried per missing struct, those with the most rows first (more rows, fewer possible layouts)</summary>
    private const int MaxTablesPerStruct = 3;

    /// <summary>
    /// Loading every package to find the DataTables would take hours on Fortnite, so only the ones named or placed like
    /// DataTables are read (about 8k of 2M packages, a few seconds).
    /// </summary>
    public static bool IsCandidate(string path)
    {
        if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(path);
        return name.StartsWith("DT_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("CDT_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("DataTable", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/DataTables/", StringComparison.OrdinalIgnoreCase);
    }

    public static Result Scan(IFileProvider provider, TypeMappings target, Action<string>? log, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        CapturingDataTable.EnsureRegistered();

        var candidates = provider.Files.Keys.Where(IsCandidate).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sink = new RowStructCaptureSink(target);
        Parallel.ForEach(candidates,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            path =>
            {
                CapturingDataTable.Sink = sink;
                try
                {
                    foreach (var _ in provider.LoadPackage(path).GetExports()) { }
                }
                catch (Exception)
                {
                    // unreadable package (missing key, not a package...): not a candidate
                }
                finally
                {
                    CapturingDataTable.Sink = null;
                }
            });

        var groups = sink.Captures.GroupBy(c => c.Struct, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).ToList();
        log?.Invoke($"Scanned {candidates.Count} packages in {timer.Elapsed.TotalSeconds:F1}s: {sink.DataTables} DataTables, " +
                    $"{groups.Count} row structs missing from the mapping");

        // each struct is solved on its own large stack thread, with a wall clock budget: solve several at once
        var outcomes = new (List<Struct> Structs, Inferred? Inferred, Failed? Failed)[groups.Count];
        Parallel.For(0, groups.Count,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            i => outcomes[i] = SolveGroup(groups[i], target));

        var structs = outcomes.SelectMany(o => o.Structs).ToList();
        var inferred = outcomes.Where(o => o.Inferred is not null).Select(o => o.Inferred!).ToList();
        var failed = outcomes.Where(o => o.Failed is not null).Select(o => o.Failed!).ToList();

        foreach (var item in inferred)
            log?.Invoke($"Guessed {item.Struct} from {item.Table} ({item.Rows} rows): {item.Layout}");
        log?.Invoke($"Row structs: {inferred.Count} guessed, {failed.Count} left out, in {timer.Elapsed.TotalSeconds:F1}s");
        return new Result(structs, inferred, failed, candidates.Count, sink.DataTables, timer.Elapsed.TotalSeconds);
    }

    /// <summary>Tries the tables of one missing struct, those with the most rows first, until one gives a layout</summary>
    private static (List<Struct> Structs, Inferred? Inferred, Failed? Failed) SolveGroup(IGrouping<string, RowStructCapture> group, TypeMappings target)
    {
        Failed? lastFailure = null;
        foreach (var capture in group.OrderByDescending(c => c.NumRows).ThenBy(c => c.Table).Take(MaxTablesPerStruct))
        {
            try
            {
                var layout = SolveOnLargeStack(() => UnversionedLayoutSolver.SolveWithFallback(capture.Rows, capture.NumRows, capture.Options));
                if (layout is null)
                {
                    lastFailure = new Failed(capture.Struct, capture.Table, capture.NumRows, "No layout fits the rows");
                    continue;
                }

                var built = RowStructBuilder.Build(layout, target, capture.Struct);
                var row = built[0];
                return (built, new Inferred(capture.Struct, capture.Table, capture.NumRows, row.Properties.Count, layout.Unproven,
                    string.Join(", ", row.Properties.OrderBy(p => p.Key).Select(p => $"{p.Value.Name}:{Describe(p.Value.MappingType)}"))), null);
            }
            catch (InferenceAbortedException e)
            {
                lastFailure = new Failed(capture.Struct, capture.Table, capture.NumRows, e.Message);
            }
            catch (Exception e)
            {
                lastFailure = new Failed(capture.Struct, capture.Table, capture.NumRows, $"{e.GetType().Name}: {e.Message}");
            }
        }

        return ([], null, lastFailure);
    }

    /// <summary>The solver recurses at every guess, so it gets a thread with a large stack</summary>
    private static UnversionedLayoutSolver.SolvedLayout? SolveOnLargeStack(Func<UnversionedLayoutSolver.SolvedLayout?> solve)
    {
        UnversionedLayoutSolver.SolvedLayout? layout = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                layout = solve();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, UnversionedLayoutSolver.RecommendedStackSize)
        {
            IsBackground = true,
            Name = "Row struct inference"
        };
        thread.Start();
        thread.Join();

        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return layout;
    }

    public static string Describe(PropertyType type) => type.Type switch
    {
        "StructProperty" => $"Struct<{type.StructType}>",
        "ArrayProperty" => $"Array<{(type.InnerType is null ? "?" : Describe(type.InnerType))}>",
        _ => type.Type.Replace("Property", "")
    };
}
