using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using CUE4Parse.MappingsProvider.Usmap;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// Produces a complete <c>.usmap</c> by running the vendored UnrealMappingsDumper inside UEFN.
/// </summary>
/// <remarks>
/// The pak-side dumper (<see cref="MappingsDumperService"/>) only sees the Blueprint types that are
/// serialized into the archives, so it has to borrow native <c>/Script</c> types from an existing
/// mapping. The upstream dumper instead walks the engine's own reflection data, which means it has
/// to run inside a live Unreal process — hence the DLL and this host.
///
/// The DLL is injected into a running UEFN (Unreal Editor for Fortnite) process. There is no channel
/// back from an injected module, so the exchange is done with files: a <c>.cfg</c> written next to the
/// DLL tells it where to write, and the run ends with a <c>HOST_RESULT</c> line in the log this class
/// polls (see <c>UnrealMappingsDumper/UnrealMappingsDumper/hostConfig.h</c>).
/// </remarks>
public sealed class UefnDumperInjector
{
    /// <summary>File name of the built dumper DLL, as produced by UnrealMappingsDumper/build.bat.</summary>
    public const string DllFileName = "UnrealMappingsDumper.dll";

    /// <summary>Overrides where the DLL is loaded from; otherwise the usual native-library search runs.</summary>
    public const string DllPathVariable = "USMAP_DUMPER_DLL";

    // The Oodle library this project already ships for reading paks. The dumper needs one only when
    // compression is asked for, and the editor has usually loaded its own by then; this is the
    // fallback. Upstream downloaded one from a link that has long since died.
    private const string OodleFileName = "oo2core_9_win64.dll";

    // UEFN ships as UnrealEditorFortnite-Win64-Shipping.exe. Only the editor is targeted: it is the
    // Unreal process this project already works against (the AES key is read out of its Common DLL).
    private const string ProcessNamePrefix = "UnrealEditorFortnite-Win64-";

    // The shipping build is the one users actually run; other configurations (Debug, DebugGame,
    // Development) only appear on developer machines and are never preferred over it.
    private const string ShippingProcessName = "UnrealEditorFortnite-Win64-Shipping";

    // A UEFN that has only just started has not built its reflection data yet, so injecting into it
    // dumps an incomplete mapping. The editor grows well past this while loading; the crash handler
    // and other helpers sharing the name never do, which is what makes this a usable tiebreaker.
    private const long MinimumWorkingSetBytes = 512L * 1024 * 1024;

    // How long the injected LoadLibrary call itself may take. The dump runs on its own thread
    // afterwards, so this only covers getting the module loaded.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    // How often the dumper's log is checked for its terminal HOST_RESULT line.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Log lines returned with a result, newest last.
    private const int LogTailLines = 40;

    public sealed class DumpRequest
    {
        /// <summary>Target process id. When unset the editor is identified automatically.</summary>
        public int? ProcessId;

        /// <summary>Output file name inside mappings/; defaults to {build}_uefn.usmap.</summary>
        public string? FileName;

        /// <summary>Build name used for the default output file name.</summary>
        public string? Build;

        /// <summary>Compress the usmap with the game's own Oodle instead of writing it uncompressed.</summary>
        public bool Oodle;

        /// <summary>Let the dumper open a console window inside UEFN (useful when debugging a failure).</summary>
        public bool Console;

        /// <summary>How long to wait for the dump to finish after the DLL is loaded.</summary>
        public TimeSpan Timeout = TimeSpan.FromMinutes(2);

        /// <summary>Parse the written file back and report what it contains.</summary>
        public bool Verify = true;

        /// <summary>
        /// Module-relative address of GObjects, used when the dumper's signature scan cannot find it
        /// on this build. Zero keeps the scan.
        /// </summary>
        public ulong GObjectsRva;

        /// <summary>Module-relative address of FNameToString; same fallback as <see cref="GObjectsRva"/>.</summary>
        public ulong FNameToStringRva;

        /// <summary>
        /// Let the dumper call the addresses its signature scan turns up, to find out whether one of
        /// them is FNameToString. That function cannot be recognised any other way, and calling the
        /// wrong one can crash the editor, so this is off unless asked for.
        /// </summary>
        public bool ProbeSignatures;

        /// <summary>
        /// Module the pinned addresses belong to. UEFN is a modular build, so an address means
        /// nothing until the module holding it is named.
        /// </summary>
        public string? Module;
    }

    public sealed class DumpResult
    {
        public string FileName = string.Empty;
        public string FilePath = string.Empty;
        public byte[] Usmap = [];
        public int ProcessId;
        public string ProcessName = string.Empty;
        public string DllPath = string.Empty;
        public string LogPath = string.Empty;
        public double ElapsedSeconds;
        public int? VerifiedStructs;
        public int? VerifiedEnums;
        public string? VerifyError;
        public List<string> Log = [];
    }

    /// <summary>Raised when the dumper ran but produced no usable mapping; carries its log.</summary>
    public sealed class DumpFailedException : Exception
    {
        public DumpFailedException(string message, List<string> log) : base(message) => Log = log;
        public List<string> Log { get; }
    }

    /// <summary>
    /// Locates the built dumper DLL, or null when it has not been built yet.
    /// </summary>
    public static string? FindDll()
        => LibraryDownloader.FindLibrary(DllFileName, Environment.GetEnvironmentVariable(DllPathVariable));

    /// <summary>Lists the UEFN processes that can currently be dumped.</summary>
    public static List<Process> FindTargets()
        => Process.GetProcesses()
            .Where(p =>
            {
                try
                {
                    return p.ProcessName.StartsWith(ProcessNamePrefix, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    // A process that exited between enumeration and inspection.
                    return false;
                }
            })
            .OrderBy(p => p.Id)
            .ToList();

    /// <summary>
    /// The UEFN process a dump would inject into right now, or null when none is usable. Never
    /// throws: the status endpoint reports what it finds rather than failing.
    /// </summary>
    public static Process? FindEditor()
    {
        try
        {
            var targets = FindTargets();
            return targets.Count == 0 ? null : PickEditor(targets);
        }
        catch (InvalidOperationException)
        {
            // UEFN is running but not loaded far enough to be injectable yet.
            return null;
        }
    }

    /// <summary>
    /// Injects the dumper into UEFN and stores the mapping it writes in the mappings/ directory.
    /// </summary>
    public DumpResult Dump(DumpRequest request, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Dumping from UEFN needs Windows: the dumper is a Windows DLL injected into the editor.");
        }

        var dll = FindDll()
                  ?? throw new FileNotFoundException(
                      $"{DllFileName} was not found. Build it with UnrealMappingsDumper\\build.bat, or point " +
                      $"{DllPathVariable} at an existing copy.");

        var target = ResolveTarget(request.ProcessId);
        var staging = CreateStagingDirectory();

        // The dumper writes through the ANSI C runtime, so it can only be pointed at a path the
        // system code page can represent. The staged copy is what gets injected, under a unique name
        // so a previous run still loaded in the editor cannot lock it.
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var stagedDll = Path.Combine(staging, $"UnrealMappingsDumper_{runId}.dll");
        var output = Path.Combine(staging, $"dump_{runId}.usmap");
        var log = output + ".log";

        // FNameToString cannot be found by signature on UE6, and the dumper refuses an address that
        // does not resolve names, so a run with nothing to go on fails outright. The address is the
        // same for the life of a build, so a known-good one is reused and only has to be found once.
        var known = OffsetStore.Load(request.Build);

        request.Module ??= known?.Module;

        if (request.FNameToStringRva == 0)
        {
            request.FNameToStringRva = known?.FNameToStringRva ?? 0;
        }

        // GObjects can be found by walking memory, but that means reading gigabytes of a live
        // editor for minutes. A known address skips it, which is faster and far less intrusive.
        if (request.GObjectsRva == 0)
        {
            request.GObjectsRva = known?.GObjectsRva ?? 0;
        }

        if (request.FNameToStringRva == 0)
        {
            request.FNameToStringRva = Dumper7Offsets.FindFNameToString(request.Build);
        }

        if (request.GObjectsRva == 0)
        {
            request.GObjectsRva = Dumper7Offsets.FindGObjects(request.Build);
        }

        // A pinned address is meaningless without the module it belongs to, so it is dropped when
        // the module is unknown and the dumper searches as before.
        if (string.IsNullOrWhiteSpace(request.Module))
        {
            request.GObjectsRva = 0;
        }

        File.Copy(dll, stagedDll, overwrite: true);
        WriteConfig(stagedDll, output, request);

        var stopwatch = Stopwatch.StartNew();
        Inject(target, stagedDll);

        var lines = WaitForResult(log, output, request.Timeout, stopwatch, cancellationToken);
        stopwatch.Stop();

        var result = new DumpResult
        {
            ProcessId = target.Id,
            ProcessName = target.ProcessName,
            DllPath = dll,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            Log = Tail(lines),
            FileName = ResolveFileName(request),
            Usmap = File.ReadAllBytes(output)
        };

        result.FilePath = Path.Combine(MappingsDumperService.MappingsDirectory, result.FileName);
        File.WriteAllBytes(result.FilePath, result.Usmap);

        // The log is kept next to the mapping it explains; ListMappings only looks at *.usmap.
        result.LogPath = result.FilePath + ".log";
        TryCopy(log, result.LogPath);

        // The dumper reports the address it settled on; keeping it turns the search into a one-off.
        // It is only ever written after a dump that produced a mapping, so a bad value cannot stick.
        var (module, gObjects) = ParseResolvedModule(lines);
        OffsetStore.Save(request.Build, module ?? request.Module, gObjects, ParseResolvedFNameToString(lines));

        if (request.Verify)
        {
            try
            {
                var mappings = new UsmapParser(result.Usmap, result.FileName).Mappings;
                result.VerifiedStructs = mappings?.Types.Count ?? 0;
                result.VerifiedEnums = mappings?.Enums.Count ?? 0;
            }
            catch (Exception ex)
            {
                result.VerifyError = ex.Message;
            }

            // A mapping with nothing in it parses cleanly, so the parse alone does not mean the dump
            // worked. Serving it would quietly replace a good mapping with an empty one.
            if (result.VerifiedStructs == 0 && result.VerifiedEnums == 0)
            {
                throw new DumpFailedException(
                    "The dumper wrote an empty mapping: it found the object array but collected no types, " +
                    "which means its offsets do not match this build.",
                    result.Log);
            }
        }

        CleanStaging(staging, stagedDll, output, log);
        return result;
    }

    /// <summary>
    /// Reads back the module the dumper settled on and where GObjects sat inside it, from the line
    /// it logs after retargeting: "Scanning &lt;module&gt; from here on (+0x... for GObjects)".
    /// </summary>
    private static (string? Module, ulong GObjectsRva) ParseResolvedModule(List<string> lines)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"^Scanning (\S+) from here on \(\+0x([0-9A-Fa-f]+) for GObjects\)");
            if (!match.Success) continue;

            if (ulong.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rva))
            {
                return (match.Groups[1].Value, rva);
            }
        }

        return (null, 0);
    }

    /// <summary>
    /// Reads back the FNameToString address the dumper accepted, as a module-relative value. Both
    /// the pinned and the scanned line carry it, and a rejected candidate's line does not.
    /// </summary>
    private static ulong ParseResolvedFNameToString(List<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.Contains("FNameToString", StringComparison.Ordinal)) continue;
            if (!line.StartsWith("Using the pinned address", StringComparison.Ordinal) &&
                !line.StartsWith("Found FNameToString", StringComparison.Ordinal)) continue;

            var start = line.IndexOf("+0x", StringComparison.Ordinal);
            if (start < 0) continue;

            var end = start + 3;
            while (end < line.Length && Uri.IsHexDigit(line[end])) end++;

            if (ulong.TryParse(line.AsSpan(start + 3, end - start - 3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rva))
            {
                return rva;
            }
        }

        return 0;
    }

    /// <summary>
    /// Picks the process to inject into. An explicit id wins; otherwise the editor is identified
    /// automatically, so the caller never has to look a pid up.
    /// </summary>
    private static Process ResolveTarget(int? processId)
    {
        var targets = FindTargets();

        if (processId is { } id)
        {
            return targets.FirstOrDefault(p => p.Id == id)
                   ?? throw new InvalidOperationException(
                       $"Process {id} is not a running UEFN process. Running UEFN processes: " +
                       (targets.Count == 0 ? "(none)" : string.Join(", ", targets.Select(p => $"{p.ProcessName}:{p.Id}"))));
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException(
                "No running UEFN process was found. Start Unreal Editor for Fortnite and let it finish loading, then retry.");
        }

        return PickEditor(targets);
    }

    /// <summary>
    /// Chooses the editor out of the UEFN processes that are running. UEFN can have more than one
    /// process under the same name (the crash reporter and other helpers are launched from the same
    /// image), so the shipping build is preferred, then the one that actually holds the loaded
    /// editor: the helpers stay small, while the editor itself is several gigabytes.
    /// </summary>
    private static Process PickEditor(List<Process> targets)
    {
        var shipping = targets
            .Where(p => p.ProcessName.Equals(ShippingProcessName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = shipping.Count > 0 ? shipping : targets;
        if (candidates.Count == 1) return candidates[0];

        // Largest first, so the fully loaded editor wins over anything still starting up.
        var ranked = candidates
            .Select(p => (Process: p, WorkingSet: WorkingSet(p)))
            .OrderByDescending(x => x.WorkingSet)
            .ToList();

        var best = ranked[0];
        if (best.WorkingSet < MinimumWorkingSetBytes)
        {
            throw new InvalidOperationException(
                "UEFN is running but does not look loaded yet " +
                $"({string.Join(", ", ranked.Select(x => $"{x.Process.ProcessName}:{x.Process.Id} {x.WorkingSet / (1024 * 1024)}MB"))}). " +
                "Wait for the editor to finish loading and retry, or pass 'pid' to inject anyway.");
        }

        return best.Process;
    }

    /// <summary>Resident memory of a process, or 0 when it can no longer be inspected.</summary>
    private static long WorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            // The process exited between enumeration and inspection.
            return 0;
        }
        catch (Win32Exception)
        {
            // Running as a different user; it is not injectable either, so rank it last.
            return 0;
        }
    }

    /// <summary>
    /// Writes the key=value file the DLL reads from next to itself. Paths stay ASCII because the
    /// dumper opens them through the ANSI CRT.
    /// </summary>
    private static void WriteConfig(string stagedDll, string output, DumpRequest request)
    {
        var config = new StringBuilder()
            .Append("output=").Append(output).Append('\n')
            .Append("compression=").Append(request.Oodle ? "oodle" : "none").Append('\n')
            .Append("console=").Append(request.Console ? "true" : "false").Append('\n');

        // Only written when pinned; a key the dumper is not given falls back to its signature scan.
        if (!string.IsNullOrWhiteSpace(request.Module))
        {
            config.Append("module=").Append(request.Module).Append('\n');
        }

        if (request.GObjectsRva != 0)
        {
            config.Append("gobjects=").Append(request.GObjectsRva.ToString("x")).Append('\n');
        }

        if (request.FNameToStringRva != 0)
        {
            config.Append("fnametostring=").Append(request.FNameToStringRva.ToString("x")).Append('\n');
        }

        if (request.ProbeSignatures)
        {
            config.Append("probesignatures=true").Append('\n');
        }

        // Only relevant when compression is asked for, and only as a fallback: the editor has
        // normally loaded Oodle itself long before the dumper needs it.
        if (request.Oodle && LibraryDownloader.FindLibrary(OodleFileName) is { } oodle)
        {
            config.Append("oodle=").Append(oodle).Append('\n');
        }

        File.WriteAllText(Path.ChangeExtension(stagedDll, ".cfg"), config.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Loads the DLL in the target process with the usual CreateRemoteThread(LoadLibraryW) sequence.
    /// The dumper runs on its own thread from DllMain, so this returns as soon as the module is in.
    /// </summary>
    private static void Inject(Process target, string dllPath)
    {
        const uint ProcessCreateThread = 0x0002;
        const uint ProcessQueryInformation = 0x0400;
        const uint ProcessVmOperation = 0x0008;
        const uint ProcessVmWrite = 0x0020;
        const uint ProcessVmRead = 0x0010;
        const uint MemCommit = 0x1000;
        const uint MemReserve = 0x2000;
        const uint MemRelease = 0x8000;
        const uint PageReadWrite = 0x04;

        var kernel32 = GetModuleHandleW("kernel32.dll");
        var loadLibrary = kernel32 == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(kernel32, "LoadLibraryW");
        if (loadLibrary == IntPtr.Zero)
        {
            throw new InvalidOperationException("LoadLibraryW could not be resolved in this process.");
        }

        var access = ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead;
        var process = OpenProcess(access, false, target.Id);
        if (process == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Could not open UEFN process {target.Id}. Run the API as the same user as UEFN (or elevated).");
        }

        var remote = IntPtr.Zero;
        var thread = IntPtr.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remote = VirtualAllocEx(process, IntPtr.Zero, (uint) bytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate memory in the UEFN process.");
            }

            if (!WriteProcessMemory(process, remote, bytes, (uint) bytes.Length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write the DLL path into the UEFN process.");
            }

            thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the loader thread in the UEFN process.");
            }

            if (WaitForSingleObject(thread, (uint) LoadTimeout.TotalMilliseconds) != 0)
            {
                throw new TimeoutException("The UEFN process did not finish loading the dumper DLL.");
            }

            // LoadLibraryW returns the module handle; zero means the load itself failed.
            if (GetExitCodeThread(thread, out var moduleHandle) && moduleHandle == 0)
            {
                throw new InvalidOperationException(
                    $"UEFN refused to load {Path.GetFileName(dllPath)}. Check that the DLL is x64 and readable by that process.");
            }
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, 0, MemRelease);
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Waits for the dumper's terminal HOST_RESULT line, so a failure inside the game is reported as
    /// its reason rather than as a timeout.
    /// </summary>
    private static List<string> WaitForResult(
        string logPath, string output, TimeSpan timeout, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lines = ReadLog(logPath);
            var result = lines.LastOrDefault(line => line.StartsWith("HOST_RESULT", StringComparison.Ordinal));

            if (result != null)
            {
                if (!result.StartsWith("HOST_RESULT ok", StringComparison.Ordinal))
                {
                    var reason = result["HOST_RESULT failed".Length..].Trim();
                    throw new DumpFailedException(
                        reason.Length > 0 ? $"The dumper failed inside UEFN: {reason}" : "The dumper failed inside UEFN.",
                        Tail(lines));
                }

                if (!File.Exists(output))
                {
                    throw new DumpFailedException(
                        "The dumper reported success but wrote no mapping file. Check that UEFN can write to the output directory.",
                        Tail(lines));
                }

                return lines;
            }

            Thread.Sleep(PollInterval);
        }

        var tail = Tail(ReadLog(logPath));
        throw new TimeoutException(
            tail.Count == 0
                ? $"The dumper produced no output within {timeout.TotalSeconds:F0}s. It may not have run: check that UEFN is fully loaded."
                : $"The dumper did not finish within {timeout.TotalSeconds:F0}s. Last log line: {tail[^1]}");
    }

    /// <summary>Reads the dumper log, which the injected module keeps appending to.</summary>
    private static List<string> ReadLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Tail(List<string> lines)
        => lines.Count <= LogTailLines ? lines : lines[^LogTailLines..];

    /// <summary>
    /// Returns a staging directory the game's ANSI file APIs can address. The mappings directory is
    /// used when its path is ASCII, otherwise its 8.3 form, otherwise the temp directory.
    /// </summary>
    private static string CreateStagingDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(MappingsDumperService.MappingsDirectory, "dumper"),
            Path.Combine(Path.GetTempPath(), "fnexportAPI", "usmap-dumper")
        };

        foreach (var candidate in candidates)
        {
            Directory.CreateDirectory(candidate);
            if (IsAscii(candidate)) return candidate;

            // A non-ASCII path still works when the volume keeps 8.3 names around.
            var shortPath = GetShortPath(candidate);
            if (shortPath != null && IsAscii(shortPath)) return shortPath;
        }

        throw new InvalidOperationException(
            "No ASCII-safe working directory was found. The dumper writes its files through the game's ANSI file " +
            "APIs, so move the project (or set PROJECT_ROOT) to a path without non-ASCII characters.");
    }

    private static bool IsAscii(string value) => value.All(c => c < 128);

    private static string? GetShortPath(string path)
    {
        var buffer = new StringBuilder(512);
        var length = GetShortPathNameW(path, buffer, (uint) buffer.Capacity);
        return length == 0 || length > buffer.Capacity ? null : buffer.ToString();
    }

    private static string ResolveFileName(DumpRequest request)
    {
        var name = request.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            var build = string.IsNullOrWhiteSpace(request.Build) ? "FortniteGame" : request.Build!.Trim();
            name = $"{build}_uefn.usmap";
        }

        name = Path.GetFileName(name.Trim());
        return name.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ? name : name + ".usmap";
    }

    private static void TryCopy(string source, string destination)
    {
        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            // The log is a diagnostic extra; failing to keep a copy must not fail the dump.
        }
    }

    /// <summary>
    /// Removes this run's files plus anything a previous run left behind. A staged DLL that is still
    /// loaded in the editor cannot be deleted yet, which is why every run gets a unique name.
    /// </summary>
    private static void CleanStaging(string staging, string stagedDll, string output, string log)
    {
        foreach (var path in new[] { output, log, stagedDll, Path.ChangeExtension(stagedDll, ".cfg") })
        {
            TryDelete(path);
        }

        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in new DirectoryInfo(staging).GetFiles().Where(f => f.LastWriteTimeUtc < cutoff))
            {
                TryDelete(file.FullName);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to sweep.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still locked by the editor; the next run sweeps it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, uint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, uint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, uint size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr process, IntPtr threadAttributes, uint stackSize, IntPtr startAddress, IntPtr parameter,
        uint creationFlags, out IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint buffer);
}
