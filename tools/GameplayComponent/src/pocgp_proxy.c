/* Gameplay component: standing in for msacm32.dll.

   The games import seven functions of the system's audio compression library, and system audio
   modules loaded later import more. This unit exports every function of that library by name and
   ordinal and passes each call on to the real library in the system directory, untouched: same
   arguments, same registers, same return. The real library is loaded when the first call arrives. */
#include "pocgp.h"

#define FORWARDS 44

static const char* const g_names[FORWARDS] =
{
    "XRegThunkEntry", "acmDriverAddA", "acmDriverAddW", "acmDriverClose",
    "acmDriverDetailsA", "acmDriverDetailsW", "acmDriverEnum", "acmDriverID",
    "acmDriverMessage", "acmDriverOpen", "acmDriverPriority", "acmDriverRemove",
    "acmFilterChooseA", "acmFilterChooseW", "acmFilterDetailsA", "acmFilterDetailsW",
    "acmFilterEnumA", "acmFilterEnumW", "acmFilterTagDetailsA", "acmFilterTagDetailsW",
    "acmFilterTagEnumA", "acmFilterTagEnumW", "acmFormatChooseA", "acmFormatChooseW",
    "acmFormatDetailsA", "acmFormatDetailsW", "acmFormatEnumA", "acmFormatEnumW",
    "acmFormatSuggest", "acmFormatTagDetailsA", "acmFormatTagDetailsW", "acmFormatTagEnumA",
    "acmFormatTagEnumW", "acmGetVersion", "acmMessage32", "acmMetrics",
    "acmStreamClose", "acmStreamConvert", "acmStreamMessage", "acmStreamOpen",
    "acmStreamPrepareHeader", "acmStreamReset", "acmStreamSize", "acmStreamUnprepareHeader",
};

FARPROC g_pocgp_forward[FORWARDS];
static HMODULE g_self;

void pocgp_proxy_bind(HMODULE self)
{
    g_self = self;
}

/* Reached only if the system library lacks a function it has had since Windows 95. */
static void __stdcall missing(void)
{
    pocgp_log("the system audio compression library lacks a function the game asked for");
    TerminateProcess(GetCurrentProcess(), 3);
}

void __cdecl pocgp_proxy_resolve(int index)
{
    wchar_t path[MAX_PATH];
    HMODULE real = NULL;
    FARPROC function = NULL;
    UINT length = GetSystemDirectoryW(path, MAX_PATH);
    if (length > 0 && length < MAX_PATH - 16)
    {
        wcscat(path, L"\\msacm32.dll");
        real = LoadLibraryW(path);
    }
    /* Loading by full path gives the system's library, never this one. */
    if (real && real != g_self) function = GetProcAddress(real, g_names[index]);
    /* One line for each function the first time it is asked for. */
    pocgp_log(function ? "%s is passed on to the system library" : "%s is not in the system library", g_names[index]);
    g_pocgp_forward[index] = function ? function : (FARPROC)missing;
}

/* Every register and flag reaches the real function as the caller left it. */
#define FORWARD(index) \
    __declspec(naked) void pocgp_forward_##index(void) \
    { \
        __asm cmp dword ptr [g_pocgp_forward + index * 4], 0 \
        __asm jne ready \
        __asm pushad \
        __asm pushfd \
        __asm push index \
        __asm call pocgp_proxy_resolve \
        __asm add esp, 4 \
        __asm popfd \
        __asm popad \
        __asm ready: \
        __asm jmp dword ptr [g_pocgp_forward + index * 4] \
    }

FORWARD(0) FORWARD(1) FORWARD(2) FORWARD(3) FORWARD(4) FORWARD(5) FORWARD(6) FORWARD(7)
FORWARD(8) FORWARD(9) FORWARD(10) FORWARD(11) FORWARD(12) FORWARD(13) FORWARD(14) FORWARD(15)
FORWARD(16) FORWARD(17) FORWARD(18) FORWARD(19) FORWARD(20) FORWARD(21) FORWARD(22) FORWARD(23)
FORWARD(24) FORWARD(25) FORWARD(26) FORWARD(27) FORWARD(28) FORWARD(29) FORWARD(30) FORWARD(31)
FORWARD(32) FORWARD(33) FORWARD(34) FORWARD(35) FORWARD(36) FORWARD(37) FORWARD(38) FORWARD(39)
FORWARD(40) FORWARD(41) FORWARD(42) FORWARD(43)
