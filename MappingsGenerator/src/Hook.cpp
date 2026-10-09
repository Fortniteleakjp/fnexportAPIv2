#include "Hook.h"

#include <cstring>

namespace
{
    constexpr size_t StubSize = 16;

    uint8* StubPage = nullptr;
    size_t StubPageUsed = 0;
    size_t StubPageSize = 0;

    uint8* AllocateStubNear(uintptr_t Target)
    {
        if (StubPage)
        {
            const intptr_t Distance = reinterpret_cast<intptr_t>(StubPage) - static_cast<intptr_t>(Target);
            if (StubPageUsed + StubSize <= StubPageSize && Distance > INT32_MIN / 2 && Distance < INT32_MAX / 2)
            {
                uint8* Stub = StubPage + StubPageUsed;
                StubPageUsed += StubSize;
                return Stub;
            }
        }

        SYSTEM_INFO Info;
        GetSystemInfo(&Info);
        const uintptr_t Granularity = Info.dwAllocationGranularity;

        /* Target の前後 1GB を allocation granularity 刻みで探す */
        for (uintptr_t Offset = Granularity; Offset < 0x40000000; Offset += Granularity)
        {
            for (const intptr_t Sign : { -1, 1 })
            {
                const uintptr_t Candidate = ((Target & ~(Granularity - 1)) + Sign * static_cast<intptr_t>(Offset));
                void* Page = VirtualAlloc(reinterpret_cast<void*>(Candidate), Info.dwPageSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                if (Page)
                {
                    StubPage = static_cast<uint8*>(Page);
                    StubPageSize = Info.dwPageSize;
                    StubPageUsed = StubSize;
                    return StubPage;
                }
            }
        }
        return nullptr;
    }
}

bool Hook::Install(void* Target, void* Detour)
{
    const uintptr_t TargetAddress = reinterpret_cast<uintptr_t>(Target);

    uint8* Stub = AllocateStubNear(TargetAddress);
    if (!Stub)
        return false;

    /* jmp qword ptr [rip+0] ; dq Detour */
    const uint8 AbsJmp[6] = { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
    memcpy(Stub, AbsJmp, sizeof(AbsJmp));
    memcpy(Stub + sizeof(AbsJmp), &Detour, sizeof(Detour));

    const int64 Rel = reinterpret_cast<intptr_t>(Stub) - static_cast<intptr_t>(TargetAddress + 5);
    if (Rel < INT32_MIN || Rel > INT32_MAX)
        return false;

    uint8 Patch[5] = { 0xE9 };
    const int32 Rel32 = static_cast<int32>(Rel);
    memcpy(Patch + 1, &Rel32, sizeof(Rel32));

    DWORD OldProtect;
    if (!VirtualProtect(Target, sizeof(Patch), PAGE_EXECUTE_READWRITE, &OldProtect))
        return false;

    memcpy(Target, Patch, sizeof(Patch));
    VirtualProtect(Target, sizeof(Patch), OldProtect, &OldProtect);
    FlushInstructionCache(GetCurrentProcess(), Target, sizeof(Patch));
    return true;
}
