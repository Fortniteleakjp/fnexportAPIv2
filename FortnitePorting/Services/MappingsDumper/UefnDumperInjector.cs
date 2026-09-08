using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    // UEFN ships as UnrealEditorFortnite-Win64-Shipping.exe. Only the editor is targeted: it is the
    // Unreal process this project already works against (the AES key is read out of its Common DLL).
    private const string ProcessNamePrefix = "UnrealEditorFortnite-Win64-";

    // How long the injected LoadLibrary call itself may take. The dump runs on its own thread
    // afterwards, so this only covers getting the module loaded.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    // How often the dumper's log is checked for its terminal HOST_RESULT line.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Log lines returned with a result, newest last.
    private const int LogTailLines = 40;

    public sealed class DumpRequest
    {
        /// <summary>Target process id. When unset the single running UEFN process is used.</summary>
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

        File.Copy(dll, stagedDll, overwrite: true);
        WriteConfig(stagedDll, output, request.Oodle, request.Console);

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
        }

        CleanStaging(staging, stagedDll, output, log);
        return result;
    }

    /// <summary>Picks the process to inject into, by id when one was given.</summary>
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

        return targets.Count switch
        {
            0 => throw new InvalidOperationException(
                "No running UEFN process was found. Start Unreal Editor for Fortnite and let it finish loading, then retry."),
            1 => targets[0],
            _ => throw new InvalidOperationException(
                "More than one UEFN process is running; pass 'pid' to choose one: " +
                string.Join(", ", targets.Select(p => $"{p.ProcessName}:{p.Id}")))
        };
    }

    /// <summary>
    /// Writes the key=value file the DLL reads from next to itself. Paths stay ASCII because the
    /// dumper opens them through the ANSI CRT.
    /// </summary>
    private static void WriteConfig(string stagedDll, string output, bool oodle, bool console)
    {
        var config = new StringBuilder()
            .Append("output=").Append(output).Append('\n')
            .Append("compression=").Append(oodle ? "oodle" : "none").Append('\n')
            .Append("console=").Append(console ? "true" : "false").Append('\n')
            .ToString();

        File.WriteAllText(Path.ChangeExtension(stagedDll, ".cfg"), config, new UTF8Encoding(false));
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
