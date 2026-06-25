using System;
using System.IO;

namespace RADADecoder
{
    /// <summary>
    /// Thin CLI wrapper around <see cref="RadaDecoder"/>. Decodes a .rada file to a .wav file.
    /// Usage: RADADecoder -i &lt;input&gt; -o &lt;output&gt; [-v]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string? inputFilePath = null;
            string? outputFilePath = null;
            bool verbose = false;

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "-i" || args[i] == "--input-file") && i + 1 < args.Length) inputFilePath = args[++i];
                else if ((args[i] == "-o" || args[i] == "--output-file") && i + 1 < args.Length) outputFilePath = args[++i];
                else if (args[i] == "-v" || args[i] == "--verbose") verbose = true;
            }

            if (string.IsNullOrEmpty(inputFilePath) || string.IsNullOrEmpty(outputFilePath))
            {
                Console.WriteLine("Usage: RADADecoder -i <Input File> -o <Output File> [-v]");
                return -1;
            }

            if (!File.Exists(inputFilePath))
            {
                Console.WriteLine($"Failed to open input file: {inputFilePath}");
                return -1;
            }

            if (verbose)
            {
                Console.WriteLine($"Input File: {inputFilePath}");
                Console.WriteLine($"Output File: {outputFilePath}");
                Console.WriteLine($"Native decoder available: {RadaDecoder.IsNativeAvailable}");
                if (RadaDecoder.NativeLibraryPath != null)
                {
                    Console.WriteLine($"Native library: {RadaDecoder.NativeLibraryPath}");
                }
            }

            if (!RadaDecoder.IsNativeAvailable)
            {
                Console.WriteLine("The native RAD Audio decode library (rada_decode / radaudio) was not found.");
                Console.WriteLine("Place it next to the executable or in a 'libs' folder, or set RADA_DLL_PATH.");
                return -2;
            }

            byte[] inputDataArray = File.ReadAllBytes(inputFilePath);
            if (inputDataArray.Length == 0)
            {
                Console.WriteLine("Input file is empty.");
                return 0;
            }

            if (!RadaDecoder.TryDecodeToWav(inputDataArray, out var wavData))
            {
                Console.WriteLine("Failed to decode the RADA audio.");
                return -1;
            }

            File.WriteAllBytes(outputFilePath, wavData);

            Console.WriteLine($"WAV bytes written: {wavData.Length}");
            Console.WriteLine("Processing complete.");
            return 0;
        }
    }
}
