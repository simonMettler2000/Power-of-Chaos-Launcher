/* Gameplay component: checks of the built library as a stand-in for msacm32.dll. No game is involved.

   usage: pocgp_proxy_test <path of the built msacm32.dll>

   Loads the built component and the system's own library side by side and checks that every function
   the system library exports is exported by the component under the same name and ordinal, and that
   calls made through the component arrive at the system library and come back intact. */
#include <stdio.h>
#include <string.h>
#include "pocgp.h"

static int g_failures;

static void check(BOOL condition, const char* what)
{
    if (!condition) { g_failures++; printf("FAILED: %s\n", what); }
}

static const IMAGE_EXPORT_DIRECTORY* exports(HMODULE module)
{
    const BYTE* base = (const BYTE*)module;
    const IMAGE_NT_HEADERS* headers = (const IMAGE_NT_HEADERS*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
    DWORD address = headers->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
    return address ? (const IMAGE_EXPORT_DIRECTORY*)(base + address) : NULL;
}

/* The ordinal a module exports a name under, or zero. */
static DWORD ordinal_of(HMODULE module, const char* name)
{
    const BYTE* base = (const BYTE*)module;
    const IMAGE_EXPORT_DIRECTORY* directory = exports(module);
    const DWORD* names;
    const WORD* ordinals;
    DWORD i;
    if (!directory) return 0;
    names = (const DWORD*)(base + directory->AddressOfNames);
    ordinals = (const WORD*)(base + directory->AddressOfNameOrdinals);
    for (i = 0; i < directory->NumberOfNames; i++)
        if (strcmp((const char*)(base + names[i]), name) == 0) return directory->Base + ordinals[i];
    return 0;
}

typedef DWORD (WINAPI* GET_VERSION)(void);
typedef UINT (WINAPI* METRICS)(HANDLE object, UINT metric, void* value);
#define METRIC_COUNT_DRIVERS 1
#define METRIC_MAX_SIZE_FORMAT 50

int wmain(int argc, wchar_t** argv)
{
    wchar_t path[MAX_PATH];
    HMODULE component, real;
    const IMAGE_EXPORT_DIRECTORY* directory;
    const BYTE* base;
    const DWORD* names;
    DWORD i, round;
    if (argc != 2) { printf("usage: pocgp_proxy_test <built msacm32.dll>\n"); return 2; }
    component = LoadLibraryW(argv[1]);
    if (!GetSystemDirectoryW(path, MAX_PATH - 16)) return 2;
    wcscat(path, L"\\msacm32.dll");
    real = LoadLibraryW(path);
    if (!component || !real || component == real) { printf("the two libraries could not be loaded side by side\n"); return 2; }
    base = (const BYTE*)real;
    directory = exports(real);
    if (!directory) return 2;
    names = (const DWORD*)(base + directory->AddressOfNames);
    check(exports(component) && exports(component)->NumberOfNames == directory->NumberOfNames
        && exports(component)->NumberOfFunctions == directory->NumberOfFunctions, "the component exports as many functions as the system library");
    for (i = 0; i < directory->NumberOfNames; i++)
    {
        const char* name = (const char*)(base + names[i]);
        DWORD ordinal = ordinal_of(real, name);
        FARPROC by_name = GetProcAddress(component, name);
        char what[160];
        sprintf(what, "%s is exported under ordinal %lu", name, ordinal);
        check(by_name != NULL && ordinal_of(component, name) == ordinal
            && GetProcAddress(component, (const char*)(DWORD_PTR)ordinal) == by_name && by_name != GetProcAddress(real, name), what);
    }
    /* Twice each: the first call finds the system function, the second goes straight through. */
    for (round = 0; round < 2; round++)
    {
        GET_VERSION version = (GET_VERSION)GetProcAddress(component, "acmGetVersion");
        METRICS metrics = (METRICS)GetProcAddress(component, "acmMetrics");
        METRICS real_metrics = (METRICS)GetProcAddress(real, "acmMetrics");
        DWORD drivers = 0xAAAAAAAA, real_drivers = 0x55555555, size = 0xAAAAAAAA, real_size = 0x55555555;
        volatile DWORD guard_before = 0x13572468, guard_after = 0x24681357;
        if (!version || !metrics || !real_metrics) { check(FALSE, "the functions to call are there"); break; }
        check(version() != 0 && version() == ((GET_VERSION)GetProcAddress(real, "acmGetVersion"))(), "a call without arguments returns the system library's answer");
        check(metrics(NULL, METRIC_COUNT_DRIVERS, &drivers) == real_metrics(NULL, METRIC_COUNT_DRIVERS, &real_drivers) && drivers == real_drivers,
            "a call with arguments returns the system library's answer");
        check(metrics(NULL, METRIC_MAX_SIZE_FORMAT, &size) == real_metrics(NULL, METRIC_MAX_SIZE_FORMAT, &real_size) && size == real_size && size != 0,
            "a second kind of call agrees as well");
        check(metrics(NULL, 0xFFFF, &size) == real_metrics(NULL, 0xFFFF, &real_size), "an error answer is passed back too");
        check(guard_before == 0x13572468 && guard_after == 0x24681357, "the caller's stack is intact");
    }
    if (g_failures) { printf("%d check(s) failed\n", g_failures); return 1; }
    printf("stand-in checks passed: %lu functions, calls pass through\n", directory->NumberOfNames);
    return 0;
}
