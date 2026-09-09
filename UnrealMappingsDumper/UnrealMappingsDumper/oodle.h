#pragma once

#include "hostConfig.h"

// fnexportAPI local patch: upstream looked for "Oodle.dll" in the working directory and, failing
// that, downloaded one from a Discord attachment link. Injected into someone's editor that is not
// something to do — and the link is long dead, so the load failed, GetProcAddress was called on a
// null module, and compressing the mapping crashed the editor on a null function pointer.
//
// The game is an Unreal application, so it has usually loaded Oodle already; that copy is used when
// it is there. Otherwise the host names the one it ships alongside the dumper. Nothing is fetched,
// and a missing library is reported instead of being called.
constexpr const char* OodleModuleNames[] =
{
	"oo2core_9_win64.dll",
	"oo2core_8_win64.dll",
	"oo2core_5_win64.dll",
};

enum class OodleFormat
{
	Invalid = -1,
	None = 3,
	Kraken = 8,
	Leviathan = 13,
	Mermaid = 9,
	Selkie = 11,
	Hydra = 12,
	Count = 14,
	Force32 = 0x40000000
};

enum class OodleCompressionLevel : uint32_t
{
	None,
	SuperFast,
	VeryFast,
	Fast,
	Normal,
	Optimal1,
	Optimal2,
	Optimal3,
	Optimal4,
	Optimal5
};

// fnexportAPI local patch: OodleLZ_Compress takes ten arguments, not eight.
//
// After the compressor, buffer, length, output and level come pOptions, dictionaryBase and lrm —
// which upstream passed as zero — and then scratchMem and scratchSize, which it left off entirely.
// The callee still reads them, so it picked up whatever happened to be on the stack and tried to
// use it as a scratch buffer. Passing null for both is the documented way to say "allocate your
// own", and is the difference between a compressed mapping and a crashed editor.
typedef int64_t(*_OodleCompressFunc)(
	OodleFormat Compressor,
	const void* RawBuffer,
	int64_t RawLength,
	void* CompressedBuffer,
	OodleCompressionLevel Level,
	const void* Options,
	const void* DictionaryBase,
	const void* Lrm,
	void* ScratchMemory,
	int64_t ScratchSize);

inline _OodleCompressFunc OodleLZ_Compress;

class Oodle
{
	Oodle()
	{
		HMODULE Handle = nullptr;

		// Already in the process, which is the usual case inside a game.
		for (auto Name : OodleModuleNames)
		{
			Handle = GetModuleHandleA(Name);
			if (Handle) break;
		}

		if (!Handle && !HostConfig::OodlePath.empty())
		{
			Handle = LoadLibraryA(HostConfig::OodlePath.c_str());
		}

		OodleLZ_Compress = Handle
			? (_OodleCompressFunc)GetProcAddress(Handle, "OodleLZ_Compress")
			: nullptr;
	}

	static void EnsureInitialized()
	{
		static Oodle _;
	}

	static __forceinline uint32_t GetCompressionBound(uint32_t uncompressedSize)
	{
		return uncompressedSize + 274 * ((uncompressedSize + 0x3FFFF) / 0x40000);
	}

public:
	static std::vector<uint8_t> Compress(std::stringstream& uncompressedStream, OodleFormat format = OodleFormat::Kraken, OodleCompressionLevel level = OodleCompressionLevel::Optimal5)
	{
		auto streamData = uncompressedStream.str();
		return Compress((void*)streamData.c_str(), streamData.size(), format, level);
	}

	static std::vector<uint8_t> Compress(void* uncompressedBuf, int64_t bufSize, OodleFormat format = OodleFormat::Kraken, OodleCompressionLevel level = OodleCompressionLevel::Optimal5)
	{
		EnsureInitialized();

		if (!OodleLZ_Compress)
		{
			throw std::runtime_error(
				"Oodle is not available in this process and no usable library was supplied, so the "
				"mapping cannot be compressed; ask for compression=none instead");
		}

		auto compressedBuf = std::make_unique<uint8_t[]>(GetCompressionBound(bufSize));

		auto compressedSize = OodleLZ_Compress(
			format,
			uncompressedBuf,
			bufSize,
			compressedBuf.get(),
			level,
			nullptr,   // default options
			nullptr,   // no dictionary
			nullptr,   // no long-range matcher
			nullptr,   // let it allocate its own scratch
			0);

		// Oodle reports failure as a zero-length result; writing that would produce a file whose
		// header promises data it does not contain.
		if (compressedSize <= 0 || compressedSize > (int64_t)GetCompressionBound((uint32_t)bufSize))
		{
			throw std::runtime_error("Oodle refused to compress the mapping");
		}

		return std::vector<uint8_t>(compressedBuf.get(), compressedBuf.get() + compressedSize);
	}
};