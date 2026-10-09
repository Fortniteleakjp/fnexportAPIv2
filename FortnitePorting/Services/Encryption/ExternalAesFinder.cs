using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public sealed class Result
    {
        public string? MainKey { get; set; }
        public string? Version { get; set; }
        public string? Build { get; set; }
        public string? FullVersion { get; set; }
        public string? ToolPath { get; set; }
        public string RawOutput { get; set; } = "";
    }

    /// <summary>
    /// Resolves the AesFinder executable. Accepts AESFINDER_PATH pointing at the .exe, the .dll, or a
    /// directory containing the built tool. Returns null if nothing usable is found.
    /// </summary>
    public static string? ResolveToolPath()
    {
        var configured = Environment.GetEnvironmentVariable("AESFINDER_PATH");
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

    public static async Task<Result> RunAsync(string toolPath, string targetFile, bool noApi = false,
        CancellationToken ct = default)
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
        if (noApi) psi.ArgumentList.Add("--no-api");

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
        if (braceIndex < 0)
            throw new InvalidOperationException($"AesFinder produced no JSON. Output: {stdout.Trim()} {stderr.Trim()}".Trim());

        var json = stdout[braceIndex..];
        var obj = JObject.Parse(json);

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
