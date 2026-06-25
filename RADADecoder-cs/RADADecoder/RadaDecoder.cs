using System;
using System.Runtime.InteropServices;

namespace RADADecoder;

/// <summary>
/// High-level, in-memory RADA -> WAV decoder used by the API. The heavy lifting (the actual
/// RAD Audio decode loop) lives in the native shim (rada_decode.dll); this class marshals the
/// bytes and wraps the decoded PCM in a WAV container.
/// Never throws for the common failure modes (missing native library, corrupt input); it
/// returns <c>false</c> so callers can degrade gracefully.
/// </summary>
public static unsafe class RadaDecoder
{
    /// <summary>
    /// Whether the native RAD Audio decode library is present and loadable.
    /// When false, <see cref="TryDecodeToWav"/> always returns false.
    /// </summary>
    public static bool IsNativeAvailable => RadaNativeLibrary.IsAvailable;

    /// <summary>
    /// The resolved native library path (or bare name), or null if unavailable.
    /// </summary>
    public static string? NativeLibraryPath => RadaNativeLibrary.ResolvedPath;

    /// <summary>
    /// Decodes a RADA-encoded buffer into a 16-bit PCM WAV byte array.
    /// </summary>
    /// <returns>True on success; false if the native library is missing or the data could not be decoded.</returns>
    public static bool TryDecodeToWav(byte[] inputDataArray, out byte[] wavData)
    {
        wavData = Array.Empty<byte>();

        if (inputDataArray == null || inputDataArray.Length == 0)
        {
            return false;
        }

        if (!IsNativeAvailable)
        {
            return false;
        }

        try
        {
            int sampleRate;
            int channels;
            IntPtr pcmPtr;
            uint pcmBytes;
            int rc;

            fixed (byte* pInput = inputDataArray)
            {
                rc = NativeMethods.Rada_DecodeToPcm(
                    pInput, (uint)inputDataArray.Length,
                    out sampleRate, out channels, out pcmPtr, out pcmBytes);
            }

            if (rc != 0 || pcmPtr == IntPtr.Zero || pcmBytes == 0)
            {
                if (pcmPtr != IntPtr.Zero)
                {
                    NativeMethods.Rada_Free(pcmPtr);
                }
                return false;
            }

            try
            {
                var pcm = new byte[pcmBytes];
                Marshal.Copy(pcmPtr, pcm, 0, (int)pcmBytes);
                wavData = BuildWav(pcm, (int)pcmBytes, (uint)sampleRate, (short)channels);
                return true;
            }
            finally
            {
                NativeMethods.Rada_Free(pcmPtr);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            // Corrupt input or unexpected native behavior: fail closed.
            return false;
        }
    }

    private static byte[] BuildWav(byte[] pcmData, int dataLength, uint sampleRate, short channels)
    {
        if (channels <= 0) channels = 1;
        var header = new WaveHeader();

        SetTag(header.riff_tag, "RIFF");
        SetTag(header.wave_tag, "WAVE");
        SetTag(header.fmt_tag, "fmt ");
        SetTag(header.data_tag, "data");

        const short bitsPerSample = 16;

        header.sample_rate = (int)sampleRate;
        header.num_channels = channels;
        header.fmt_length = 16;
        header.audio_format = 1; // PCM
        header.byte_rate = header.num_channels * header.sample_rate * bitsPerSample / 8;
        header.block_align = (short)(header.num_channels * bitsPerSample / 8);
        header.bits_per_sample = bitsPerSample;
        header.data_length = dataLength;
        header.riff_length = dataLength + sizeof(WaveHeader) - 8;

        var wavBytes = new byte[sizeof(WaveHeader) + dataLength];

        fixed (byte* pWav = wavBytes)
        {
            Buffer.MemoryCopy(&header, pWav, sizeof(WaveHeader), sizeof(WaveHeader));
        }

        Buffer.BlockCopy(pcmData, 0, wavBytes, sizeof(WaveHeader), dataLength);
        return wavBytes;
    }

    private static void SetTag(byte* tag, string value)
    {
        tag[0] = (byte)value[0];
        tag[1] = (byte)value[1];
        tag[2] = (byte)value[2];
        tag[3] = (byte)value[3];
    }
}
