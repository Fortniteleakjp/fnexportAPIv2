using System.Diagnostics;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using FortnitePorting.Services.Mappings.RowStructInference;

namespace FortnitePorting.Services.Mappings;

public sealed class StaticMappingsGenerator
{
    public const string ExecutableName = "UEFNStaticMappingsGenerator.exe";
    public const string EngineModule = "UnrealEditorFortnite-Engine-Win64-Shipping.dll";
    public const string CommonModule = "UnrealEditorFortnite-Common-Win64-Shipping.dll";
    public const string VersionFile = "UnrealEditorFortnite-Win64-Shipping.version";
    private static readonly SemaphoreSlim GenerationLock = new(1, 1);

    /// <summary>Provider: the mounted game, whose DataTables fill in the row structs the DLLs don't have (null: none)</summary>
    public sealed record Request(string? Directory, string Compression, int? Level, string? Oodle,
        string? FileName, int TimeoutSeconds, IFileProvider? Provider = null, bool InferRowStructs = true);

    public sealed record Result(string FileName, string FilePath, byte[] Usmap, string Source,
        string Build, int Structs, int Enums, double ElapsedSeconds, RowStructScanner.Result? RowStructs = null,
        string? RowStructsError = null);

    public sealed class GenerationException(string message, string log) : Exception(message)
    {
        public string Log { get; } = log;
    }

    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("USMAP_GENERATOR_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var root = AppPaths.ProjectRoot;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, ExecutableName),
            Path.Combine(root, "libs", ExecutableName),
            Path.Combine(root, "MappingsGenerator", "build", ExecutableName)
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string BinariesDirectory(string? directory)
    {
        directory = string.IsNullOrWhiteSpace(directory)
            ? Environment.GetEnvironmentVariable("UEFN_BINARIES_DIR") : directory;
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Epic Games", "Fortnite", "FortniteGame", "Binaries", "Win64") : directory;

        var root = Path.GetFullPath(directory);
        string[] candidates =
        [root, Path.Combine(root, "FortniteGame", "Binaries", "Win64"), Path.Combine(root, "Binaries", "Win64")];
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, EngineModule))) ?? root;
    }

    public static string[] MissingFiles(string directory)
        => new[] { EngineModule, CommonModule, VersionFile }
            .Where(file => !File.Exists(Path.Combine(directory, file))).ToArray();

    public static string ReadBuild(string directory)
    {
        using var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, VersionFile)));
        if (version.RootElement.ValueKind != JsonValueKind.Object ||
            !version.RootElement.TryGetProperty("BranchName", out var branchValue) ||
            branchValue.ValueKind != JsonValueKind.String ||
            !version.RootElement.TryGetProperty("Changelist", out var clValue) || clValue.ValueKind != JsonValueKind.Number ||
            !clValue.TryGetInt64(out var changelist) || changelist <= 0)
            throw new InvalidDataException("The UEFN version file has no valid branch or changelist.");
        var branch = branchValue.GetString();
        if (string.IsNullOrWhiteSpace(branch))
            throw new InvalidDataException("The UEFN version file has no valid branch.");
        return $"{branch}-CL-{changelist}";
    }

    public async Task<Result> GenerateAsync(Request request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Mapping generation requires 64-bit Windows.");
        if (request.Compression is not ("none" or "zstd" or "brotli" or "oodle"))
            throw new ArgumentException("'compression' must be none, zstd, brotli or oodle.");
        var (minimum, maximum) = request.Compression switch
        {
            "zstd" => (1, 22), "brotli" => (0, 11), "oodle" => (0, 9), _ => (0, 0)
        };
        if (request.Level.HasValue && (request.Compression == "none" || request.Level < minimum || request.Level > maximum))
            throw new ArgumentException($"'level' must be between {minimum} and {maximum} for {request.Compression}; omit it for none.");
        if (request.TimeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("'timeoutSeconds' must be between 1 and 3600.");

        var directory = BinariesDirectory(request.Directory);
        var missing = MissingFiles(directory);
        if (missing.Length > 0)
            throw new FileNotFoundException($"UEFN files missing from '{directory}': {string.Join(", ", missing)}.");
        var executable = FindExecutable()
            ?? throw new FileNotFoundException($"Build MappingsGenerator\\build.bat or set USMAP_GENERATOR_PATH to {ExecutableName}.");
        var oodle = string.IsNullOrWhiteSpace(request.Oodle) ? null : Path.GetFullPath(request.Oodle);
        if (oodle != null && !File.Exists(oodle))
            throw new FileNotFoundException($"Oodle library not found: {oodle}.");

        var build = ReadBuild(directory);
        var suffix = request.Compression switch { "zstd" => "_zs", "brotli" => "_br", "oodle" => "_oo", _ => "" };
        var name = MappingStore.FileName(request.FileName ?? build + suffix + ".usmap");
        if (!await GenerationLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A mapping is already being generated. Retry after it finishes.");

        var temporary = Path.Combine(Path.GetTempPath(), $"fnexport-mapping-{Guid.NewGuid():N}.usmap");
        var extra = Path.ChangeExtension(temporary, ".extra.usmap");
        var timer = Stopwatch.StartNew();
        try
        {
            var log = await RunAsync(executable, directory, temporary, request, oodle, null, cancellationToken);
            if (!string.Equals(build, ReadBuild(directory), StringComparison.Ordinal))
                throw new GenerationException("UEFN was updated during generation. Retry with the updated installation.", log);

            var data = await File.ReadAllBytesAsync(temporary, cancellationToken);
            int structs, enums;
            try { (structs, enums) = MappingStore.Verify(data, name); }
            catch (Exception ex) { throw new GenerationException($"The generated mapping could not be verified: {ex.Message}", log); }

            RowStructScanner.Result? rowStructs = null;
            string? rowStructsError = null;
            if (request.InferRowStructs && request.Provider is { Files.Count: > 0 } provider)
            {
                using var stream = new MemoryStream(data, writable: false);
                var mappings = new UsmapParser(stream, name).Mappings
                    ?? throw new GenerationException("The generated mapping could not be read back.", log);
                rowStructs = await Task.Run(() => RowStructScanner.Scan(provider, mappings, Console.WriteLine, cancellationToken), cancellationToken);

                if (rowStructs.Structs.Count > 0)
                {
                    // the mapping without them stays the result if adding them fails (e.g. a generator built before --extra)
                    try
                    {
                        await File.WriteAllBytesAsync(extra, ExtraUsmapWriter.Write(rowStructs.Structs), cancellationToken);
                        var extraLog = await RunAsync(executable, directory, temporary, request, oodle, extra, cancellationToken);
                        log += extraLog;
                        if (!string.Equals(build, ReadBuild(directory), StringComparison.Ordinal))
                            throw new GenerationException("UEFN was updated during generation. Retry with the updated installation.", log);

                        var withRows = await File.ReadAllBytesAsync(temporary, cancellationToken);
                        var (withExtra, withExtraEnums) = MappingStore.Verify(withRows, name);
                        if (withExtra != structs + rowStructs.Structs.Count)
                            throw new InvalidDataException($"only {withExtra - structs} of the {rowStructs.Structs.Count} guessed structs made it into the mapping");
                        (data, structs, enums) = (withRows, withExtra, withExtraEnums);
                    }
                    catch (GenerationException ex) when (ex.Message.StartsWith("Mapping generator failed", StringComparison.Ordinal))
                    {
                        rowStructsError = $"The generator could not add the guessed row structs ({ex.Message}). Rebuild it with MappingsGenerator\\build.bat libs.";
                    }
                    catch (InvalidDataException ex)
                    {
                        rowStructsError = $"The mapping with the guessed row structs was rejected: {ex.Message}";
                    }
                    if (rowStructsError != null) Console.WriteLine("✗ " + rowStructsError);
                }
            }

            var output = await MappingStore.SaveAsync(data, name, cancellationToken);
            return new Result(name, output, data, directory, build, structs, enums, timer.Elapsed.TotalSeconds, rowStructs, rowStructsError);
        }
        finally
        {
            GenerationLock.Release();
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(extra)) File.Delete(extra);
        }
    }

    /// <returns>the generator's output</returns>
    private static async Task<string> RunAsync(string executable, string directory, string output, Request request,
        string? oodle, string? extra, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory
        };
        // the row structs are guessed here, from the API's own provider, and handed over with --extra
        foreach (var argument in new[] { "--dir", directory, "--out", output,
            "--compression", request.Compression, "--no-wait", "--no-infer" })
            start.ArgumentList.Add(argument);
        if (request.Level.HasValue)
        {
            start.ArgumentList.Add("--level");
            start.ArgumentList.Add(request.Level.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (oodle != null)
        {
            start.ArgumentList.Add("--oodle");
            start.ArgumentList.Add(oodle);
        }
        if (extra != null)
        {
            start.ArgumentList.Add("--extra");
            start.ArgumentList.Add(extra);
        }

        if (File.Exists(output)) File.Delete(output);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new GenerationException("Could not start the mapping generator.", "");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Mapping generation exceeded {request.TimeoutSeconds} seconds.");
        }

        var log = (await stdout) + (await stderr);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new GenerationException($"Mapping generator failed (exit code {process.ExitCode}).", log);
        return log;
    }
}
