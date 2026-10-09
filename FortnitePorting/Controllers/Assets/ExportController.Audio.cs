using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse_Conversion.Textures;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Security.Cryptography;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Options;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RADADecoder;
using System.Text.RegularExpressions;
using FortnitePorting.Models;
using FortnitePorting.Services;

namespace FortnitePorting.Controllers
{
    public partial class ExportController
    {

        /// <summary>
        /// Reports audio metadata for a sound asset (format, whether it can be decoded to WAV)
        /// without returning the binary payload. Useful for deciding how to request the audio.
        /// </summary>
        /// <param name="path">The path of the sound asset.</param>
        [HttpGet("audioinfo")]
        public IActionResult GetAudioInfo([FromQuery] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("The 'path' parameter is required.");
            }

            string processedPath;
            try
            {
                processedPath = ConvertToPackagePath(Uri.UnescapeDataString(path).Trim());
            }
            catch
            {
                processedPath = ConvertToPackagePath(path.Trim());
            }

            if (!_provider.TryLoadPackageObject(processedPath, out var asset))
            {
                var lastPart = processedPath.Split('/').Last();
                _provider.TryLoadPackageObject($"{processedPath}.{lastPart}", out asset);
            }

            if (asset == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Asset Not Found",
                    Detail = $"The requested asset '{path}' could not be found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            if (asset is not (USoundWave or UAkMediaAssetData))
            {
                return Ok(new { path, name = asset.Name, exportType = asset.ExportType, isAudio = false });
            }

            // shouldDecompress=false: report the raw container format without running the
            // decompression step. (The encoded payload is still assembled, so this is not free;
            // for PCM the reported format stays "PCM" rather than being promoted to "WAV".)
            asset.Decode(false, out var format, out var raw);
            var fmt = string.IsNullOrEmpty(format) ? "WAV" : format.ToUpperInvariant();
            var radaDecodable = fmt == "RADA" && RadaDecoder.IsNativeAvailable;
            var canDecodeToWav = fmt is "WAV" or "PCM" or "ADPCM" || radaDecodable;
            var (contentType, extension) = MapAudioContentType(radaDecodable ? "WAV" : fmt);

            return Ok(new
            {
                path,
                name = asset.Name,
                exportType = asset.ExportType,
                isAudio = true,
                audioFormat = fmt,
                encodedSizeBytes = raw?.Length ?? 0,
                canDecodeToWav,
                nativeRadaDecoderAvailable = RadaDecoder.IsNativeAvailable,
                nativeRadaLibraryPath = RadaDecoder.NativeLibraryPath,
                suggestedContentType = contentType,
                suggestedExtension = extension,
                hint = "Call /api/v1/export?path=...&audio=true to download. RADA decodes to WAV only when the native RAD Audio library is present."
            });
        }


        private byte[]? DecodeRada(byte[] radaData)
        {
            try
            {
                if (RadaDecoder.TryDecodeToWav(radaData, out var wavData))
                {
                    return wavData;
                }

                _logger.LogWarning("RADA decode failed in managed decoder. Returning null.");
                return null;
            }
            catch (Exception ex)
            {
                if (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    _logger.LogWarning(ex, "RADA native library is not available. Returning raw RADA stream.");
                    return null;
                }

                _logger.LogError(ex, "Error decoding RADA");
                return null;
            }
        }

        /// <summary>
        /// Maps a CUE4Parse audio format string to an HTTP content type and file extension.
        /// </summary>
        private static (string contentType, string extension) MapAudioContentType(string? format)
        {
            var f = (format ?? string.Empty).ToUpperInvariant();
            return f switch
            {
                "" or "WAV" or "PCM" or "ADPCM" => ("audio/wav", "wav"),
                "RADA" => ("audio/x-rada", "rada"),     // raw (undecoded) RAD Audio
                "BINKA" => ("audio/x-binka", "binka"),
                "OPUS" => ("audio/opus", "opus"),
                "OGG" => ("audio/ogg", "ogg"),
                "WEM" => ("audio/x-wwise", "wem"),
                "AT9" => ("audio/x-at9", "at9"),
                _ => ("application/octet-stream", f.Length > 0 ? f.ToLowerInvariant() : "bin"),
            };
        }

    }
}
