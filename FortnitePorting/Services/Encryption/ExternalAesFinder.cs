using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Services;

/// <summary>
/// Runs the external <c>AesFinder</c> tool (https://github.com/.../AesFinder) on a binary already on disk
/// and parses its <c>--json</c> output. The tool recovers the Fortnite MainAES key from the
/// <c>*-Common-Win64-Shipping.dll</c> where it is stored in plaintext as <c>mov [rbp+d], imm32</c>
/// instruction immediates (the AESDumpster pattern) — no game launch or process injection.
/// </summary>
public static class ExternalAesFinder
{
    // Default to the location the user provided; override with AESFINDER_PATH (a file or a directory).
    private const string DefaultToolPath = @"D:\AesFinder-main\AesFinder-main\bin\Release\net8.0\AesFinder.exe";

    // A 256-bit key as the tool prints it in plain-text mode (0x + 64 hex digits).
    private static readonly Regex KeyPattern = new(@"\b0x[0-9A-Fa-f]{64}\b", RegexOptions.Compiled);

    public sealed class Result
    {
        public string? MainKey { get; set; }
        public string? Version { get; set; }
        public string? Build { get; set; }
        public string? FullVersion { get; set; }
        public string? ToolPath { get; set; }
        public string RawOutput { get; set; } = "";

        /// <summary>Every key the tool reported, main key first. Only filled from plain-text output.</summary>
        public List<string> Candidates { get; set; } = new();
    }

    /// <summary>
    /// Resolves the AesFinder executable. Accepts AESFINDER_PATH pointing at the .exe, the .dll, or a
    /// directory containing the built tool. Without it, the copy next to the application (the one the
    /// self-updater replaces on every update) wins over the development default. Returns null if
    /// nothing usable is found.
    /// </summary>
    public static string? ResolveToolPath()
    {
        var configured = Environment.GetEnvironmentVariable("AESFINDER_PATH");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var bundled = Path.Combine(SelfUpdateService.ApplicationDirectory, SelfUpdateService.AesFinderExecutableName());
            if (File.Exists(bundled)) return bundled;
        }

        var candidate = string.IsNullOrWhiteSpace(configured) ? DefaultToolPath : configured.Trim();

        if (Directory.Exists(candidate))
        {
            // Prefer a published/built AesFinder.exe, then AesFinder.dll, anywhere under the directory.
            var exe = Directory.EnumerateFiles(candidate, "AesFinder.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe != null) return exe;
            var dll = Directory.EnumerateFiles(candidate, "AesFinder.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (dll != null) return dll;
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Runs the tool on <paramref name="targetFile"/>. <c>--no-api</c> is always passed: some builds of the
    /// tool otherwise ask fortnite-api.com which candidate is the main key, and the key must come from the
    /// binary alone. Picking the right candidate is <see cref="AesKeyPicker"/>'s job.
    /// </summary>
    public static async Task<Result> RunAsync(string toolPath, string targetFile, CancellationToken ct = default)
    {
        if (!File.Exists(targetFile))
            throw new FileNotFoundException("Target binary not found.", targetFile);

        var isDll = toolPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = isDll ? "dotnet" : toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Directory.GetCurrentDirectory()
        };
        if (isDll) psi.ArgumentList.Add(toolPath);
        psi.ArgumentList.Add(targetFile);
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--no-api");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start the AesFinder process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* ignore */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"AesFinder exited with code {process.ExitCode}. {stderr.Trim()} {stdout.Trim()}".Trim());
        }

        var braceIndex = stdout.IndexOf('{');
        if (braceIndex >= 0)
        {
            JObject? obj = null;
            try { obj = JObject.Parse(stdout[braceIndex..]); }
            catch (Newtonsoft.Json.JsonException) { /* not JSON after all; try the plain-text form below */ }

            if (obj != null)
            {
                return new Result
                {
                    MainKey = (string?)obj["mainKey"],
                    Version = (string?)obj["version"],
                    Build = (string?)obj["build"],
                    FullVersion = (string?)obj["fullVersion"],
                    ToolPath = toolPath,
                    RawOutput = stdout.Trim()
                };
            }
        }

        // Older builds of the tool don't know --json and just print the candidate keys one per line
        // (main key first). Take those as candidates; AesKeyPicker decides which one is real.
        var keys = KeyPattern.Matches(stdout)
            .Select(m => m.Value.ToUpperInvariant().Replace("0X", "0x"))
            .Distinct()
            .ToList();
        if (keys.Count == 0)
            throw new InvalidOperationException($"AesFinder produced no JSON or keys. Output: {stdout.Trim()} {stderr.Trim()}".Trim());

        return new Result
        {
            MainKey = keys[0],
            Candidates = keys,
            ToolPath = toolPath,
            RawOutput = stdout.Trim()
        };
    }
}
