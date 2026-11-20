/* Gameplay component: recognising game code and changing where a call instruction leads.

   Nothing but the four-byte distance of an existing relative call is ever written, and only after the
   surrounding code was recognised by its hash and the call still leads where the supported revision's
   call leads. */
#include "pocgp.h"

#define CALL_OPCODE 0xE8

unsigned __int64 pocgp_hash(const BYTE* bytes, DWORD length)
{
    unsigned __int64 hash = 0xCBF29CE484222325ui64;
    DWORD i;
    for (i = 0; i < length; i++) hash = (hash ^ bytes[i]) * 0x100000001B3ui64;
    return hash;
}

/* True when the whole range is committed memory that may be read. */
static BOOL readable(const BYTE* start, DWORD length)
{
    const BYTE* end = start + length;
    if (length == 0 || end < start) return FALSE;
    while (start < end)
    {
        MEMORY_BASIC_INFORMATION info;
        const BYTE* next;
        if (!VirtualQuery(start, &info, sizeof(info)) || info.State != MEM_COMMIT
            || (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0 || info.Protect == 0) return FALSE;
        next = (const BYTE*)info.BaseAddress + info.RegionSize;
        if (next <= start) return FALSE;
        start = next;
    }
    return TRUE;
}

BOOL pocgp_region_matches(const POCGP_REGION* region)
{
    const BYTE* code = (const BYTE*)(DWORD_PTR)region->address;
    return readable(code, region->length) && pocgp_hash(code, region->length) == region->hash;
}

BOOL pocgp_call_matches(const POCGP_CALL* call)
{
    const BYTE* code = (const BYTE*)(DWORD_PTR)call->site;
    DWORD distance;
    if (!readable(code, 5) || code[0] != CALL_OPCODE) return FALSE;
    memcpy(&distance, code + 1, sizeof(distance));
    return call->site + 5 + distance == call->target;
}

BOOL pocgp_call_retarget(const POCGP_CALL* call, const void* replacement)
{
    BYTE* code = (BYTE*)(DWORD_PTR)call->site;
    DWORD distance = (DWORD)(DWORD_PTR)replacement - (call->site + 5);
    DWORD previous, ignored;
    if (!pocgp_call_matches(call)) return FALSE;
    if (!VirtualProtect(code + 1, sizeof(distance), PAGE_EXECUTE_READWRITE, &previous)) return FALSE;
    memcpy(code + 1, &distance, sizeof(distance));
    VirtualProtect(code + 1, sizeof(distance), previous, &ignored);
    FlushInstructionCache(GetCurrentProcess(), code, 5);
    return TRUE;
}
