/* HD card artwork: per-game hook points.

   Each supported executable is recognised by its file name and by a hash of the start of each image
   library routine that puts cards on screen. Only existing game code is called; the hooks observe the
   routines around their unchanged execution and change nothing the game sees. */
#include <windows.h>
#include <string.h>
#include "detours.h"
#include "pochd.h"
#include "pochd_game.h"

/* int Image::Copy(Image* source, int x, int y, const RECT* sourceRect, const SIZE* targetSize, const RECT* clip)
   (thiscall, callee cleans six arguments) */
typedef int (__fastcall* COPY_ROUTINE)(void* self, void* unused, void* source, int x, int y, const RECT* source_rect,
    const SIZE* target_size, const RECT* clip);

/* int Image::Mix(Image* source, int x, int y, int level, const RECT* sourceRect, const SIZE* targetSize, const RECT* clip)
   The library has several of these: blend by level, shade by level, add by level. The level is a byte. */
typedef int (__fastcall* MIX_ROUTINE)(void* self, void* unused, void* source, int x, int y, int level,
    const RECT* source_rect, const SIZE* target_size, const RECT* clip);

/* int Image::Quad(Image* source, const POINT* sourcePoints, const POINT* targetPoints, int, int, int count)
   Draws the source quadrilateral onto the target one: the games use it for every card that is turned,
   scaled or squeezed while it moves (dealing, flipping, flying to the field). */
typedef int (__fastcall* QUAD_ROUTINE)(void* self, void* unused, void* source, const POINT* source_points,
    const POINT* target_points, int option_a, int option_b, int count);

/* int MixQuad(Image* target, Image* source, int level, const POINT* sourcePoints, const POINT* targetPoints, int, int, int count)
   (cdecl) The same drawn translucent; from level 256 on it hands over to the see-through quad routine. */
typedef int (__cdecl* MIX_QUAD_ROUTINE)(void* target, void* source, int level, const POINT* source_points,
    const POINT* target_points, int option_a, int option_b, int count);

#define MIX_ROUTINES 3

/* A routine is recognised by where it is and by a hash of how it begins: FNV-1a (64 bit) over its first
   ROUTINE_BYTES bytes in the loaded image. No game code is stored here. */
#define ROUTINE_BYTES 48

typedef struct ROUTINE { DWORD address; unsigned __int64 hash; } ROUTINE;

typedef struct PROFILE
{
    const char* executable;
    ROUTINE copy;
    ROUTINE mix[MIX_ROUTINES];     /* blend, shade, add */
    ROUTINE alpha[2];              /* pictures with their own transparency: as they are, at a strength */
    ROUTINE quad[2];               /* plain, with see-through colour */
    ROUTINE mix_quad;
    POCHD_LAYOUT layout;
} PROFILE;

/* Kaiba and Yugi carry an earlier build of the library than Joey: same interface, different code and
   a pixel format tag twelve bytes further into the image object. */
static const PROFILE g_profiles[] =
{
    { "yugi_pc.exe", { 0x0049C770, 0xC12C75935E9BF620ui64 },
        { { 0x004C0730, 0xB6494348DA906E98ui64 }, { 0x004C0230, 0x625D890C0E95A9C8ui64 }, { 0x004F34C0, 0x1EBEFF329105120Dui64 } },
        { { 0x004C19A0, 0x96C9CA6A181D4E6Fui64 }, { 0x004C1CF0, 0x6B5BD6CB005CE796ui64 } },
        { { 0x00517550, 0x363E6F98B8C6D12Eui64 }, { 0x00516C70, 0xD908D437ABB93376ui64 } }, { 0x0042C6D0, 0xFF3EF63069DE50F3ui64 },
        { 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x80, 3, 10 } },
    { "kaiba_pc.exe", { 0x0049EB70, 0xAF6144B1A254C2C0ui64 },
        { { 0x004C2B30, 0x882B20489B229791ui64 }, { 0x004C2630, 0xDF4BD93FA2102D5Dui64 }, { 0x004F58C0, 0x1EBEFF329105120Dui64 } },
        { { 0x004C3DA0, 0xE4C6BE1FCA9A44DFui64 }, { 0x004C40F0, 0x6F6B5743251B9546ui64 } },
        { { 0x00519950, 0xFA1C4991C626253Eui64 }, { 0x00519070, 0x3B0B796CDA2E591Fui64 } }, { 0x0042C6B0, 0xFF3EF63069DE50F3ui64 },
        { 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x80, 3, 10 } },
    { "joey_pc.exe", { 0x004ADC10, 0xEDC9107FDD468B0Cui64 },
        { { 0x004C84B0, 0x030F45F43E7EED5Dui64 }, { 0x004C77E0, 0x1262DA0427588110ui64 }, { 0x004F0810, 0x9A5A539C8F3C4344ui64 } },
        { { 0x004CB6F0, 0x33FA44FE6107B82Fui64 }, { 0x004CBDD0, 0x120411E6ABEC7D76ui64 } },
        { { 0x00520370, 0xF4AE33AA4AE89469ui64 }, { 0x0051EB80, 0x6144A786F9B80B51ui64 } }, { 0x004231E0, 0x1CC6E1F5086E5721ui64 },
        { 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x74, 3, 10 } },
};

static BOOL matches(const ROUTINE* routine)
{
    MEMORY_BASIC_INFORMATION info;
    const BYTE* code = (const BYTE*)routine->address;
    unsigned __int64 hash = 0xCBF29CE484222325ui64;
    int i;
    if (!VirtualQuery(code, &info, sizeof(info)) || info.State != MEM_COMMIT
        || (const BYTE*)info.BaseAddress + info.RegionSize < code + ROUTINE_BYTES) return FALSE;
    for (i = 0; i < ROUTINE_BYTES; i++) hash = (hash ^ code[i]) * 0x100000001B3ui64;
    return hash == routine->hash;
}

static COPY_ROUTINE g_real_copy;
static MIX_ROUTINE g_real_mix[MIX_ROUTINES];
static COPY_ROUTINE g_real_alpha;
static MIX_ROUTINE g_real_alpha_level;
static QUAD_ROUTINE g_real_quad[2];
static MIX_QUAD_ROUTINE g_real_mix_quad;

static int __fastcall hooked_copy(void* self, void* unused, void* source, int x, int y, const RECT* source_rect,
    const SIZE* target_size, const RECT* clip)
{
    int result = g_real_copy(self, unused, source, x, y, source_rect, target_size, clip);
    pochd_track_copy(self, source, x, y, source_rect, target_size, clip);
    return result;
}

#define HOOKED_MIX(index) \
    static int __fastcall hooked_mix_##index(void* self, void* unused, void* source, int x, int y, int level, \
        const RECT* source_rect, const SIZE* target_size, const RECT* clip) \
    { \
        void* draw = pochd_track_effect_begin(self, source, x, y, source_rect, target_size, clip, index, level & 255); \
        int result = g_real_mix[index](self, unused, source, x, y, level, source_rect, target_size, clip); \
        if (draw) pochd_track_effect_end(draw); \
        return result; \
    }

HOOKED_MIX(0)
HOOKED_MIX(1)
HOOKED_MIX(2)

static const MIX_ROUTINE g_hooked_mix[MIX_ROUTINES] = { hooked_mix_0, hooked_mix_1, hooked_mix_2 };

static int __fastcall hooked_alpha(void* self, void* unused, void* source, int x, int y, const RECT* source_rect,
    const SIZE* target_size, const RECT* clip)
{
    void* draw = pochd_track_effect_begin(self, source, x, y, source_rect, target_size, clip, 3, 255);
    int result = g_real_alpha(self, unused, source, x, y, source_rect, target_size, clip);
    if (draw) pochd_track_effect_end(draw);
    return result;
}

static int __fastcall hooked_alpha_level(void* self, void* unused, void* source, int x, int y, int level,
    const RECT* source_rect, const SIZE* target_size, const RECT* clip)
{
    void* draw = pochd_track_effect_begin(self, source, x, y, source_rect, target_size, clip, 4, level & 255);
    int result = g_real_alpha_level(self, unused, source, x, y, level, source_rect, target_size, clip);
    if (draw) pochd_track_effect_end(draw);
    return result;
}

#define HOOKED_QUAD(index) \
    static int __fastcall hooked_quad_##index(void* self, void* unused, void* source, const POINT* source_points, \
        const POINT* target_points, int option_a, int option_b, int count) \
    { \
        void* draw = pochd_track_quad_begin(self, source, source_points, target_points, count, -1); \
        int result = g_real_quad[index](self, unused, source, source_points, target_points, option_a, option_b, count); \
        if (draw) pochd_track_effect_end(draw); \
        return result; \
    }

HOOKED_QUAD(0)
HOOKED_QUAD(1)

static const QUAD_ROUTINE g_hooked_quad[2] = { hooked_quad_0, hooked_quad_1 };

static int __cdecl hooked_mix_quad(void* target, void* source, int level, const POINT* source_points,
    const POINT* target_points, int option_a, int option_b, int count)
{
    /* At full strength the routine calls the see-through quad routine, which is followed there. */
    void* draw = level >= 0 && level < 256 ? pochd_track_quad_begin(target, source, source_points, target_points, count, level) : NULL;
    int result = g_real_mix_quad(target, source, level, source_points, target_points, option_a, option_b, count);
    if (draw) pochd_track_effect_end(draw);
    return result;
}

BOOL pochd_game_install(void)
{
    char path[MAX_PATH];
    const char* name;
    int i, k;
    if (!GetModuleFileNameA(NULL, path, sizeof(path))) return FALSE;
    name = strrchr(path, '\\') ? strrchr(path, '\\') + 1 : path;
    for (i = 0; i < (int)(sizeof(g_profiles) / sizeof(g_profiles[0])); i++)
    {
        const PROFILE* profile = &g_profiles[i];
        LONG error;
        if (_stricmp(name, profile->executable) != 0) continue;
        if (!matches(&profile->copy) || !matches(&profile->mix[0]) || !matches(&profile->mix[1]) || !matches(&profile->mix[2])
            || !matches(&profile->alpha[0]) || !matches(&profile->alpha[1])
            || !matches(&profile->quad[0]) || !matches(&profile->quad[1]) || !matches(&profile->mix_quad))
        {
            pochd_log("%s is a different revision than the supported one", name);
            return FALSE;
        }
        g_pochd_layout = profile->layout;
        g_real_copy = (COPY_ROUTINE)profile->copy.address;
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach((PVOID*)&g_real_copy, (PVOID)hooked_copy);
        for (k = 0; k < MIX_ROUTINES; k++)
        {
            g_real_mix[k] = (MIX_ROUTINE)profile->mix[k].address;
            DetourAttach((PVOID*)&g_real_mix[k], (PVOID)g_hooked_mix[k]);
        }
        for (k = 0; k < 2; k++)
        {
            g_real_quad[k] = (QUAD_ROUTINE)profile->quad[k].address;
            DetourAttach((PVOID*)&g_real_quad[k], (PVOID)g_hooked_quad[k]);
        }
        g_real_alpha = (COPY_ROUTINE)profile->alpha[0].address;
        DetourAttach((PVOID*)&g_real_alpha, (PVOID)hooked_alpha);
        g_real_alpha_level = (MIX_ROUTINE)profile->alpha[1].address;
        DetourAttach((PVOID*)&g_real_alpha_level, (PVOID)hooked_alpha_level);
        g_real_mix_quad = (MIX_QUAD_ROUTINE)profile->mix_quad.address;
        DetourAttach((PVOID*)&g_real_mix_quad, (PVOID)hooked_mix_quad);
        error = DetourTransactionCommit();
        pochd_log("%s: copy routine at %08lX, five mixing routines and three quad routines hooked (%ld)", name,
            profile->copy.address, error);
        return error == NO_ERROR;
    }
    return FALSE;
}
