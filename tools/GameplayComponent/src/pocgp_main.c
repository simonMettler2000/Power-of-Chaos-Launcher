/* Gameplay component: start-up, settings file and log.

   Everything here happens once, when the game process loads the component and before any game code
   runs: read pocgp.ini beside the component, recognise the executable, and switch the requested
   features on. The component reads no game file, writes no save and creates no thread. Its only
   output is the small pocgp.log beside itself, in the launcher's private cache. */
#include <stdio.h>
#include <stdarg.h>
#include "pocgp.h"

#define MAXIMUM_SETTINGS_BYTES (256 * 1024)
#define MAXIMUM_LOG_BYTES (256 * 1024)

static POCGP_SETTINGS g_settings;
static HANDLE g_log = INVALID_HANDLE_VALUE;
static LONG g_log_bytes;
static CRITICAL_SECTION g_log_lock;
static wchar_t g_directory[MAX_PATH];

void pocgp_log(const char* format, ...)
{
    char line[512];
    va_list arguments;
    int length;
    DWORD written;
    if (g_log == INVALID_HANDLE_VALUE) return;
    va_start(arguments, format);
    length = _vsnprintf(line, sizeof(line) - 2, format, arguments);
    va_end(arguments);
    if (length < 0 || length > (int)sizeof(line) - 2) length = sizeof(line) - 2;
    line[length++] = '\r';
    line[length++] = '\n';
    EnterCriticalSection(&g_log_lock);
    /* Diagnostic only: a long session can never fill the disk. */
    if (g_log_bytes + length <= MAXIMUM_LOG_BYTES)
    {
        WriteFile(g_log, line, (DWORD)length, &written, NULL);
        g_log_bytes += length;
    }
    LeaveCriticalSection(&g_log_lock);
}

/* Small generator for choices that need no secrecy: which owned card fills a reward, which deck file
   a duel uses. The game's own random numbers are left alone. */
unsigned pocgp_random(void)
{
    static LONG state;
    LONG value = state;
    if (value == 0)
    {
        LARGE_INTEGER counter;
        QueryPerformanceCounter(&counter);
        value = (LONG)(counter.LowPart ^ GetTickCount() ^ (GetCurrentProcessId() << 16));
        if (value == 0) value = 0x2545F491;
    }
    value ^= value << 13;
    value ^= (LONG)((unsigned)value >> 17);
    value ^= value << 5;
    state = value;
    return (unsigned)value >> 1;
}

static BOOL beside(const wchar_t* name, wchar_t* path)
{
    if (wcslen(g_directory) + wcslen(name) >= MAX_PATH) return FALSE;
    wcscpy(path, g_directory);
    wcscat(path, name);
    return TRUE;
}

static void load_settings(void)
{
    wchar_t path[MAX_PATH];
    HANDLE file;
    DWORD size, read = 0;
    char* text;
    if (!beside(L"pocgp.ini", path)) return;
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    size = GetFileSize(file, NULL);
    if (size != INVALID_FILE_SIZE && size > 0 && size <= MAXIMUM_SETTINGS_BYTES
        && (text = (char*)HeapAlloc(GetProcessHeap(), 0, size)) != NULL)
    {
        if (ReadFile(file, text, size, &read, NULL) && read == size)
            pocgp_parse_settings(text, size, g_directory, &g_settings);
        HeapFree(GetProcessHeap(), 0, text);
    }
    CloseHandle(file);
}

static void start(HMODULE self)
{
    wchar_t path[MAX_PATH];
    char executable[MAX_PATH];
    const char* name;
    DWORD length = GetModuleFileNameW(self, g_directory, MAX_PATH);
    wchar_t* slash;
    if (length == 0 || length >= MAX_PATH || (slash = wcsrchr(g_directory, L'\\')) == NULL) { g_directory[0] = 0; return; }
    slash[1] = 0;
    InitializeCriticalSection(&g_log_lock);
    if (beside(L"pocgp.log", path))
        g_log = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    load_settings();
    length = GetModuleFileNameA(NULL, executable, sizeof(executable));
    if (length == 0 || length >= sizeof(executable)) return;
    name = strrchr(executable, '\\') ? strrchr(executable, '\\') + 1 : executable;
    pocgp_log("gameplay component 1: rewards %d, deck files %d", g_settings.new_card_rewards, g_settings.deck_count);
    if (g_settings.new_card_rewards || g_settings.deck_count > 0) pocgp_games_install(name, &g_settings);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        pocgp_proxy_bind(instance);
        start(instance);
    }
    return TRUE;
}
