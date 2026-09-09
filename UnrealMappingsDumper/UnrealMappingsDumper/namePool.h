#pragma once

// ---------------------------------------------------------------------------
// fnexportAPI local patch: read names out of the engine's name pool.
//
// Upstream resolves a name by calling FName::ToString inside the game. That is the one thing here
// that cannot be checked before it is used: a function address is just a number, and the only way
// to find out whether it is the right function is to call it — which, when it is not, takes the
// editor down. It also cannot be found by signature on UE6, so a working address had to come from
// somewhere else entirely.
//
// The pool is data, so it can be recognised the same way GObjects is: by its shape, read-only, with
// a check that proves it. Nothing is executed, and there is nothing left to seed.
//
// Layout (UE6, from Core/Private/UObject/UnrealNames.cpp):
//
//   FNameEntryAllocator
//     +0x00  FRWLock Lock                 one pointer-sized word
//     +0x08  uint32  CurrentBlock
//     +0x0C  uint32  CurrentByteCursor
//     +0x10  uint8*  Blocks[8192]
//
//   FNameEntry (WITH_CASE_PRESERVING_NAME, which an editor build has)
//     +0x00  FNameEntryId ComparisonId
//     +0x04  uint16 Header                bIsWide : 1, Len : 15
//     +0x06  name data, ANSI or WIDE
//
// An entry is addressed as Blocks[Id >> 16] + 8 * (Id & 0xFFFF).
// ---------------------------------------------------------------------------
namespace NamePool
{
	// Core/Private/UObject/UnrealNames.cpp
	constexpr uint32_t BlockOffsetBits = 16;
	constexpr uint32_t BlockOffsets = 1u << BlockOffsetBits;
	constexpr uint32_t MaxBlocks = 1u << 13;

	// alignof(FNameEntry), which UE_FNAME_ENTRY_ALIGNMENT pins to 8 for editor builds.
	constexpr uint32_t EntryStride = 8;

	constexpr uint32_t BlockSizeBytes = EntryStride * BlockOffsets;

	constexpr int CurrentBlockOffset = 0x08;
	constexpr int ByteCursorOffset = 0x0C;
	constexpr int BlocksOffset = 0x10;

	constexpr int EntryHeaderOffset = 0x04;
	constexpr int EntryDataOffset = 0x06;

	// NAME_SIZE
	constexpr int MaxNameLength = 1024;

	/// <summary>The allocator, once found. Zero means names are read through FNameToString instead.</summary>
	inline uintptr_t Allocator = 0;

	FORCEINLINE uint8_t** Blocks()
	{
		return (uint8_t**)(Allocator + BlocksOffset);
	}

	FORCEINLINE uint32_t CurrentBlock()
	{
		return *(uint32_t*)(Allocator + CurrentBlockOffset);
	}

	/// <summary>Address of one entry, or 0 when the id does not name a live block.</summary>
	inline uint8_t* EntryAt(uint32_t EntryId)
	{
		if (!Allocator)
			return nullptr;

		auto Block = EntryId >> BlockOffsetBits;
		auto Offset = EntryId & (BlockOffsets - 1);

		if (Block > CurrentBlock() || Block >= MaxBlocks)
			return nullptr;

		auto Base = Blocks()[Block];
		return Base ? Base + (size_t)EntryStride * Offset : nullptr;
	}

	/// <summary>Reads one entry's text. Returns false when the entry does not look like one.</summary>
	inline bool Read(uint32_t EntryId, std::wstring& Out)
	{
		auto Entry = EntryAt(EntryId);
		if (!Entry)
			return false;

		auto Header = *(uint16_t*)(Entry + EntryHeaderOffset);
		bool bIsWide = (Header & 1) != 0;
		auto Length = (int)(Header >> 1);

		if (Length <= 0 || Length > MaxNameLength)
			return false;

		auto Data = Entry + EntryDataOffset;

		if (bIsWide)
		{
			Out.assign((const wchar_t*)Data, Length);
		}
		else
		{
			Out.resize(Length);
			for (int Index = 0; Index < Length; Index++)
			{
				Out[Index] = (wchar_t)(uint8_t)Data[Index];
			}
		}

		return true;
	}

	/// <summary>
	/// Whether this address holds the allocator. Proven rather than guessed: the first entry of the
	/// first block is always "None", so a candidate that decodes to anything else is not it.
	/// </summary>
	inline bool Verify(uintptr_t Candidate)
	{
		auto Previous = Allocator;
		Allocator = Candidate;

		std::wstring First;
		bool bOk = Read(0, First) && First == L"None";

		if (!bOk)
			Allocator = Previous;

		return bOk;
	}
}
