// rada_shim.cpp
// -----------------------------------------------------------------------------
// A thin, plain-C ("extern C") DLL wrapper around Unreal Engine's RAD Audio
// decoder. It links the engine-shipped static lib (radaudio_decoder_win64.lib)
// and exposes a single, ABI-stable entry point for the C# side:
//
//     int32_t Rada_DecodeToPcm(fileData, fileSize, &rate, &channels, &pcm, &pcmBytes)
//     void    Rada_Free(pcm)
//
// The decode loop is a faithful port of UE's FRadAudioInfo::Decode
// (Engine/Source/Runtime/RadAudioCodec/Module/Private/RadAudioInfo.cpp).
//
// Build (see build.bat): cl /MT /LD, link radaudio_decoder_win64.lib.
// The RADA_WRAP must match the lib (UE ships it as UERA).
// -----------------------------------------------------------------------------

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <vector>

static bool ShimDebug() { return getenv("RADA_SHIM_DEBUG") != nullptr; }
#define DBG(...) do { if (ShimDebug()) { fprintf(stderr, "[rada_shim] " __VA_ARGS__); fflush(stderr); } } while(0)

// Cross-platform symbol export (MSVC on Windows, GCC/Clang visibility on Linux/macOS).
#if defined(_WIN32)
  #define RADA_EXPORT extern "C" __declspec(dllexport)
#else
  #define RADA_EXPORT extern "C" __attribute__((visibility("default")))
#endif

#define RADA_WRAP UERA
#include "rada_decode.h" // brings in rada_file_header.h, the (UERA-mangled) RadA* decls,
                         // the inline RadAGetBytesToOpen / RadASampleRateFromEnum, and constants.

// Mirrors UE's RadAudioDecoder prefix struct: it sits immediately before the
// RadAContainer in one contiguous allocation.
struct ShimDecoder
{
    uint32_t SeekTableByteCount;
    int32_t  ConsumeFrameCount;
    uint16_t OutputReservoirValidFrames;
    uint16_t OutputReservoirReadFrames;

    RadAContainer* Container() { return (RadAContainer*)(this + 1); }
};

RADA_EXPORT
int32_t Rada_DecodeToPcm(const uint8_t* fileData, uint32_t fileSize,
                         int32_t* outSampleRate, int32_t* outChannels,
                         int16_t** outPcm, uint32_t* outPcmBytes)
{
    if (outPcm) *outPcm = nullptr;
    if (outPcmBytes) *outPcmBytes = 0;
    if (outSampleRate) *outSampleRate = 0;
    if (outChannels) *outChannels = 0;

    if (!fileData || !outPcm || !outPcmBytes || fileSize < sizeof(RadAFileHeader))
        return -1;

    const RadAFileHeader* header = RadAGetFileHeader(fileData, fileSize);
    if (header == nullptr)
        return -2;

    const uint32_t numChannels = header->channels;
    const uint32_t sampleRate = RadASampleRateFromEnum(header->sample_rate);
    if (numChannels == 0 || numChannels > RADA_MAX_CHANNELS || sampleRate == 0)
        return -3;

    const uint32_t sampleStride = numChannels * (uint32_t)sizeof(int16_t);
    const uint64_t totalPcm64 = header->frame_count * (uint64_t)sampleStride;
    if (totalPcm64 == 0 || totalPcm64 > 0x7FFFFFFFull)
        return -4;
    const uint32_t totalPcmBytes = (uint32_t)totalPcm64;

    const uint32_t audioDataOffset = RadAGetBytesToOpen(header);
    if (audioDataOffset > fileSize)
        return -5;

    DBG("channels=%u rate=%u frames=%llu audioDataOffset=%u fileSize=%u\n",
        numChannels, sampleRate, (unsigned long long)header->frame_count, audioDataOffset, fileSize);

    uint32_t decoderMem = 0;
    int32_t memRc = RadAGetMemoryNeededToOpen(fileData, fileSize, &decoderMem);
    DBG("RadAGetMemoryNeededToOpen rc=%d mem=%u\n", memRc, decoderMem);
    if (memRc != 0)
        return -6;

    std::vector<uint8_t> raw(decoderMem + sizeof(ShimDecoder), 0);
    ShimDecoder* dec = (ShimDecoder*)raw.data();
    if (RadAOpenDecoder(fileData, fileSize, dec->Container(), decoderMem) == 0)
        return -7;

    int16_t* outBuf = (int16_t*)malloc(totalPcmBytes);
    if (outBuf == nullptr)
        return -8;
    memset(outBuf, 0, totalPcmBytes);

    // ---- Strip inline "SEEK" chunks ------------------------------------------------
    // Fortnite's RADA files carry inline "SEEK" chunks inside the block stream that the
    // stock UE RadAGetBytesToOpen does not account for (the header reports
    // seek_table_entry_count == 0). RadAExamineBlock rejects them, so we remove each
    // "SEEK" chunk by skipping forward to the next 0x55 block sync byte. For files with
    // no such chunks this is a straight copy.
    std::vector<uint8_t> clean;
    clean.reserve(fileSize - audioDataOffset);
    {
        bool inSkip = false;
        for (uint32_t i = audioDataOffset; i < fileSize; i++)
        {
            if (!inSkip && i + 4 < fileSize &&
                fileData[i] == 'S' && fileData[i + 1] == 'E' && fileData[i + 2] == 'E' && fileData[i + 3] == 'K')
            {
                inSkip = true;
                i += 1; // skip 'S'; the loop increment skips 'E', matching the reference stripper
            }
            else if (inSkip && fileData[i] == RADA_SYNC_BYTE)
            {
                inSkip = false;
                clean.push_back(RADA_SYNC_BYTE);
            }
            else if (!inSkip)
            {
                clean.push_back(fileData[i]);
            }
        }
    }
    DBG("stripped: rawAudio=%u cleanAudio=%zu firstByte=0x%02x\n",
        fileSize - audioDataOffset, clean.size(), clean.empty() ? 0 : clean[0]);

    // ---- Decode loop (port of UE FRadAudioInfo::Decode) -------------------------
    const uint8_t* compressedData = clean.data();
    uint32_t remnCompressed = (uint32_t)clean.size();
    uint8_t* outPCMData = (uint8_t*)outBuf;
    uint32_t remnOutputFrames = totalPcmBytes / sampleStride;

    std::vector<float>   deint;      // RadADecodeBlock_MaxOutputFrames * channels
    std::vector<uint8_t> reservoir;  // RadADecodeBlock_MaxOutputFrames * sampleStride

    int dbgIter = 0;
    while (remnOutputFrames)
    {
        // Drain the output reservoir first.
        if (dec->OutputReservoirReadFrames < dec->OutputReservoirValidFrames)
        {
            uint32_t available = dec->OutputReservoirValidFrames - dec->OutputReservoirReadFrames;
            uint32_t copyFrames = available < remnOutputFrames ? available : remnOutputFrames;
            uint32_t copyBytes = sampleStride * copyFrames;
            uint32_t copyOffset = sampleStride * dec->OutputReservoirReadFrames;

            memcpy(outPCMData, reservoir.data() + copyOffset, copyBytes);

            dec->OutputReservoirReadFrames += (uint16_t)copyFrames;
            remnOutputFrames -= copyFrames;
            outPCMData += copyBytes;

            if (remnOutputFrames == 0)
                break;
        }

        if (remnCompressed == 0)
            break;

        uint32_t bytesNeeded = 0;
        RadAExamineBlockResult blockResult = RadAExamineBlock(dec->Container(), compressedData, remnCompressed, &bytesNeeded);
        if (dbgIter < 4) DBG("iter=%d examine=%d needed=%u remnComp=%u remnOut=%u\n",
                             dbgIter, (int)blockResult, bytesNeeded, remnCompressed, remnOutputFrames);
        if (blockResult != RadAExamineBlockResult::Valid)
            break;

        if (deint.empty())
            deint.resize((size_t)RadADecodeBlock_MaxOutputFrames * numChannels);

        size_t consumed = 0;
        int16_t decoded = RadADecodeBlock(dec->Container(), compressedData, remnCompressed,
                                          deint.data(), RadADecodeBlock_MaxOutputFrames, &consumed);
        if (dbgIter < 4) DBG("iter=%d decoded=%d consumed=%llu\n", dbgIter, (int)decoded, (unsigned long long)consumed);
        dbgIter++;
        if (decoded == RadADecodeBlock_Error)
        {
            free(outBuf);
            return -9;
        }
        if (decoded == RadADecodeBlock_Done)
            break;

        compressedData += consumed;
        remnCompressed -= (uint32_t)consumed;

        // Sample-accurate seek trimming (ConsumeFrameCount is 0 for a from-start decode).
        int16_t decodeOffset = 0;
        if (dec->ConsumeFrameCount)
        {
            int16_t consumedThisTime = decoded;
            if (dec->ConsumeFrameCount < decoded)
                consumedThisTime = (int16_t)dec->ConsumeFrameCount;
            if (consumedThisTime)
            {
                decodeOffset = consumedThisTime;
                decoded -= consumedThisTime;
            }
            dec->ConsumeFrameCount -= consumedThisTime;
        }

        if (decoded == 0)
            continue;

        int16_t* dest = (int16_t*)outPCMData;
        bool useReservoir = remnOutputFrames < RadADecodeBlock_MaxOutputFrames;
        if (useReservoir)
        {
            if (reservoir.empty())
                reservoir.resize((size_t)RadADecodeBlock_MaxOutputFrames * sampleStride);
            dest = (int16_t*)reservoir.data();
        }

        // Interleave deinterleaved float channels into S16, with clamping.
        for (uint32_t ch = 0; ch < numChannels; ch++)
        {
            const float* in = deint.data() + (size_t)RadADecodeBlock_MaxOutputFrames * ch + decodeOffset;
            for (int32_t s = 0; s < decoded; s++)
            {
                float v = in[s] * 32768.0f;
                if (v > 32767.0f) v = 32767.0f;
                else if (v < -32768.0f) v = -32768.0f;
                dest[ch + (size_t)s * numChannels] = (int16_t)v;
            }
        }

        if (useReservoir)
        {
            dec->OutputReservoirValidFrames = (uint16_t)decoded;
            dec->OutputReservoirReadFrames = 0;
            // Next loop iteration drains the reservoir into the output.
        }
        else
        {
            remnOutputFrames -= decoded;
            outPCMData += decoded * sampleStride;
        }
    }

    const uint32_t producedBytes = totalPcmBytes - (remnOutputFrames * sampleStride);

    *outPcm = outBuf;
    *outPcmBytes = producedBytes;
    if (outSampleRate) *outSampleRate = (int32_t)sampleRate;
    if (outChannels) *outChannels = (int32_t)numChannels;
    return 0;
}

RADA_EXPORT
void Rada_Free(int16_t* pcm)
{
    if (pcm)
        free(pcm);
}
