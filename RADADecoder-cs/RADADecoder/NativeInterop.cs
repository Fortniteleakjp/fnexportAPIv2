using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RADADecoder
{
    /// <summary>
    /// P/Invoke bindings for the native RAD Audio decode shim (rada_decode.dll), which wraps
    /// Unreal Engine's RAD Audio decoder and exposes a single ABI-stable decode entry point.
    /// The library is resolved at runtime by <see cref="RadaNativeLibrary"/>; if it cannot be
    /// found the calls throw DllNotFoundException, which the managed wrapper translates into a
    /// graceful "native decoder unavailable" result rather than crashing the host.
    /// </summary>
    public static unsafe class NativeMethods
    {
        internal const string DllName = "rada_decode";

        static NativeMethods()
        {
            RadaNativeLibrary.EnsureResolverRegistered();
        }

        /// <summary>
        /// Decodes a complete RADA file buffer into interleaved 16-bit PCM.
        /// Returns 0 on success; on success the caller must release the buffer with <see cref="Rada_Free"/>.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Rada_DecodeToPcm(
            byte* fileData, uint fileSize,
            out int outSampleRate, out int outChannels,
            out IntPtr outPcm, out uint outPcmBytes);

        /// <summary>Frees a PCM buffer returned by <see cref="Rada_DecodeToPcm"/>.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Rada_Free(IntPtr pcm);
    }

    /// <summary>
    /// Locates and loads the native RAD Audio decode library from a flexible set of locations,
    /// mirroring how the project resolves the Oodle native library. Drop the DLL/SO into the
    /// app folder or <c>libs/</c>, or point <c>RADA_DLL_PATH</c> at it, and decoding "just works".
    /// </summary>
    public static class RadaNativeLibrary
    {
        // Logical names the SDK ships under, across forks/platforms.
        private static readonly string[] CandidateNames =
        {
            "rada_decode", "radaudio", "radaudio_decoder", "radadecode"
        };

        private static readonly object Gate = new();
        private static bool _resolverRegistered;
        private static bool _probed;
        private static IntPtr _handle = IntPtr.Zero;
        private static string? _resolvedPath;

        /// <summary>
        /// True if the native decode library could be located and loaded. Result is cached.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureLoaded();
                lock (Gate)
                {
                    return _handle != IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// The resolved native library path, or the bare name if it was loaded via the OS default search.
        /// </summary>
        public static string? ResolvedPath
        {
            get { lock (Gate) { return _resolvedPath; } }
        }

        internal static void EnsureResolverRegistered()
        {
            lock (Gate)
            {
                if (_resolverRegistered) return;
                try
                {
                    NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
                }
                catch (InvalidOperationException)
                {
                    // A resolver was already set for this assembly; ignore.
                }
                _resolverRegistered = true;
            }
        }

        /// <summary>
        /// Probes for and loads the native library exactly once, caching the handle and path
        /// under <see cref="Gate"/>. Both <see cref="IsAvailable"/> and the DllImport resolver
        /// share this single handle, so the library is loaded at most once.
        /// </summary>
        private static void EnsureLoaded()
        {
            EnsureResolverRegistered();
            lock (Gate)
            {
                if (_probed) return;
                _probed = true;

                foreach (var path in EnumerateCandidatePaths())
                {
                    if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                    {
                        _handle = handle;
                        _resolvedPath = path;
                        return;
                    }
                }

                // Last resort: let the OS search its default paths by bare name.
                foreach (var name in CandidateNames)
                {
                    if (NativeLibrary.TryLoad(name, typeof(NativeMethods).Assembly, DllImportSearchPath.SafeDirectories, out var handle))
                    {
                        _handle = handle;
                        _resolvedPath = name;
                        return;
                    }
                }
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, NativeMethods.DllName, StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero; // not ours; let the default resolver handle it
            }

            EnsureLoaded();
            lock (Gate)
            {
                return _handle; // shared single handle; IntPtr.Zero falls through to default resolution
            }
        }

        private static IEnumerable<string> EnumerateCandidatePaths()
        {
            // 1. Explicit override.
            var explicitPath = Environment.GetEnvironmentVariable("RADA_DLL_PATH");
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                yield return explicitPath;
            }

            foreach (var dir in SearchDirectories())
            {
                foreach (var file in CandidateFileNames())
                {
                    yield return Path.Combine(dir, file);
                }
            }
        }

        private static IEnumerable<string> SearchDirectories()
        {
            var dirs = new List<string>();

            void Add(string? d)
            {
                if (!string.IsNullOrWhiteSpace(d) && !dirs.Contains(d!)) dirs.Add(d!);
            }

            var baseDir = AppContext.BaseDirectory;
            Add(baseDir);
            Add(Path.Combine(baseDir, "libs"));

            // The real executable directory. Important for single-file publishes, where
            // AppContext.BaseDirectory may point at the bundle extraction directory instead.
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                var exeDir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
                Add(exeDir);
                if (!string.IsNullOrEmpty(exeDir)) Add(Path.Combine(exeDir!, "libs"));
            }
            catch
            {
                // MainModule can be inaccessible in some hosts; ignore.
            }

            var projectRoot = Environment.GetEnvironmentVariable("PROJECT_ROOT");
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                Add(projectRoot);
                Add(Path.Combine(projectRoot, "libs"));
            }

            // Repo-root/libs for local `dotnet run` (mirrors FileProviderFactory's rootDir:
            // four levels up from bin/<cfg>/<tfm> lands on the solution root).
            try
            {
                var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
                Add(Path.Combine(repoRoot, "libs"));
            }
            catch
            {
                // ignore malformed paths
            }

            Add(Directory.GetCurrentDirectory());
            Add(Path.Combine(Directory.GetCurrentDirectory(), "libs"));

            return dirs;
        }

        private static IEnumerable<string> CandidateFileNames()
        {
            foreach (var name in CandidateNames)
            {
                if (OperatingSystem.IsWindows())
                {
                    yield return name + ".dll";
                }
                else
                {
                    yield return "lib" + name + ".so";
                    yield return name + ".so";
                }
            }
        }
    }
}
