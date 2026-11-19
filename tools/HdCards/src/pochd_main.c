/* HD card artwork: configuration, logging and the provider call-outs. */
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <stdlib.h>
#include "dd.h"
#include "ddsurface.h"
#include "dllmain.h"
#include "render_gdi.h"
#include "pochd.h"

POCHD_CONFIG g_pochd;

static CRITICAL_SECTION g_log_lock;
static HANDLE g_log = INVALID_HANDLE_VALUE;
static LONG g_log_bytes;

void pochd_log(const char* format, ...)
{
    char line[1024];
    va_list args;
    int length;
    DWORD written;
    if (g_log == INVALID_HANDLE_VALUE) return;
    /* The log is diagnostic only; a runaway message can never fill the disk. */
    if (g_log_bytes > (g_pochd.debug ? 64 : 1) * 1024 * 1024) return;
    va_start(args, format);
    length = _vsnprintf(line, sizeof(line) - 2, format, args);
    va_end(args);
    if (length < 0) length = sizeof(line) - 2;
    line[length++] = '\r';
    line[length++] = '\n';
    EnterCriticalSection(&g_log_lock);
    WriteFile(g_log, line, (DWORD)length, &written, NULL);
    g_log_bytes += length;
    LeaveCriticalSection(&g_log_lock);
}

static void trim(char* text)
{
    size_t length = strlen(text);
    while (length && (text[length - 1] == '\r' || text[length - 1] == '\n' || text[length - 1] == ' ' || text[length - 1] == '\t'))
        text[--length] = 0;
}

static void set_path(wchar_t* target, const char* utf8, BOOL directory)
{
    int length = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, target, MAX_PATH - 2);
    if (length <= 0) { target[0] = 0; return; }
    length = (int)wcslen(target);
    if (directory && length && target[length - 1] != L'\\' && target[length - 1] != L'/')
    {
        target[length] = L'\\';
        target[length + 1] = 0;
    }
}

static void load_config(void)
{
    wchar_t path[MAX_PATH];
    char line[2048];
    FILE* file;
    g_pochd.filter = 2;
    g_pochd.texture_megabytes = 384;
    g_pochd.enabled = 1;
    wcscpy(g_pochd.archive, L"data.dat");
    _snwprintf(path, MAX_PATH - 1, L"%spochd.ini", g_pochd.directory);
    path[MAX_PATH - 1] = 0;
    file = _wfopen(path, L"rb");
    while (file && fgets(line, sizeof(line), file))
    {
        char* value;
        char* key = line;
        if ((unsigned char)key[0] == 0xEF && (unsigned char)key[1] == 0xBB && (unsigned char)key[2] == 0xBF) key += 3;
        trim(key);
        value = strchr(key, '=');
        if (!value || key[0] == ';' || key[0] == '#') continue;
        *value++ = 0;
        if (strcmp(key, "images") == 0) set_path(g_pochd.images, value, TRUE);
        else if (strcmp(key, "archive") == 0) set_path(g_pochd.archive, value, FALSE);
        else if (strcmp(key, "debug") == 0) g_pochd.debug = atoi(value);
        else if (strcmp(key, "filter") == 0) g_pochd.filter = atoi(value);
        else if (strcmp(key, "texture_megabytes") == 0) g_pochd.texture_megabytes = atoi(value);
    }
    if (file) fclose(file);
    {
        /* Diagnostics can also be switched on for one run from the environment, without touching the cache. */
        char value[16];
        DWORD length = GetEnvironmentVariableA("POCHD_DEBUG", value, sizeof(value));
        if (length > 0 && length < sizeof(value)) g_pochd.debug = atoi(value);
    }
    if (g_pochd.filter < 0 || g_pochd.filter > 2) g_pochd.filter = 2;
    if (g_pochd.texture_megabytes < 64) g_pochd.texture_megabytes = 64;
    if (g_pochd.texture_megabytes > 2048) g_pochd.texture_megabytes = 2048;
}

/* ---- diagnostics: a command file next to the module, read only when debug=1 ---- */
static void save_frame(const wchar_t* path)
{
    static POCHD_FRAME frame;
    unsigned serial = 0;
    int row = (POCHD_FRAME_W * 3 + 3) & ~3, x, y;
    BITMAPFILEHEADER file_header = { 0x4D42, 54 + row * POCHD_FRAME_H, 0, 0, 54 };
    BITMAPINFOHEADER info = { 40, POCHD_FRAME_W, POCHD_FRAME_H, 1, 24, 0, row * POCHD_FRAME_H, 0, 0, 0, 0 };
    BYTE* line;
    FILE* file;
    if (!pochd_track_acquire(&frame, &serial)) return;
    file = _wfopen(path, L"wb");
    line = (BYTE*)calloc(1, row);
    if (file && line)
    {
        fwrite(&file_header, 1, sizeof(file_header), file);
        fwrite(&info, 1, sizeof(info), file);
        for (y = POCHD_FRAME_H - 1; y >= 0; y--)
        {
            for (x = 0; x < POCHD_FRAME_W; x++)
            {
                WORD v = frame.pixels[y * POCHD_FRAME_W + x];
                BOOL owned = frame.owner[y * POCHD_FRAME_W + x] != 0;
                line[x * 3 + 0] = (BYTE)(((v & 0x1F) * 527 + 23) >> 6);
                line[x * 3 + 1] = (BYTE)((((v >> 5) & 0x3F) * 259 + 33) >> 6);
                line[x * 3 + 2] = (BYTE)((((v >> 11) & 0x1F) * 527 + 23) >> 6);
                /* owner=1 in the name requests the coverage view: substituted pixels are tinted. */
                if (owned && wcsstr(path, L"owner")) line[x * 3 + 1] = (BYTE)(line[x * 3 + 1] / 2 + 127);
            }
            fwrite(line, 1, row, file);
        }
    }
    if (file) fclose(file);
    free(line);
}

static DWORD WINAPI command_thread(LPVOID unused)
{
    wchar_t path[MAX_PATH], argument[MAX_PATH];
    char line[1024];
    (void)unused;
    _snwprintf(path, MAX_PATH - 1, L"%spochd.cmd", g_pochd.directory);
    path[MAX_PATH - 1] = 0;
    for (;;)
    {
        FILE* file;
        char* space;
        Sleep(50);
        file = _wfopen(path, L"rb");
        if (!file) continue;
        line[0] = 0;
        fgets(line, sizeof(line), file);
        fclose(file);
        DeleteFileW(path);
        trim(line);
        space = strchr(line, ' ');
        argument[0] = 0;
        if (space) { *space = 0; MultiByteToWideChar(CP_UTF8, 0, space + 1, -1, argument, MAX_PATH - 1); }
        pochd_log("command: %s", line);
        if (strcmp(line, "shot") == 0) pochd_render_request_capture(argument);
        else if (strncmp(line, "burst", 5) == 0 && atoi(line + 5) > 0) pochd_render_request_burst(argument, atoi(line + 5));
        else if (strcmp(line, "frame") == 0) save_frame(argument);
        else if (strcmp(line, "layers") == 0) pochd_track_dump();
        else if (strcmp(line, "hd") == 0) g_pochd.enabled = argument[0] != L'0';
        else if (strcmp(line, "tint") == 0) g_pochd.tint = argument[0] != L'0';
        else if (strcmp(line, "filter") == 0) g_pochd.filter = argument[0] - L'0';
    }
}

void pochd_dd_event(int kind, struct IDirectDrawSurfaceImpl* dst, struct IDirectDrawSurfaceImpl* src,
    const RECT* dst_rect, const RECT* src_rect, DWORD flags)
{
    (void)dst_rect; (void)src_rect; (void)flags;
    /* The games present by copying their complete 16-bit canvas to the primary surface. */
    if ((kind == POCHD_DD_BLTFAST || kind == POCHD_DD_BLT) && dst && src && (dst->caps & DDSCAPS_PRIMARYSURFACE)
        && src->bpp == 16 && src->width == POCHD_FRAME_W && src->height == POCHD_FRAME_H && src->surface)
    {
        pochd_track_present((const WORD*)src->surface, (int)src->pitch, (int)src->width, (int)src->height);
    }
}

/* ---- what the presenter needs to know about its host ---- */
DWORD pochd_render_run(void);

HWND pochd_host_window(void) { return g_ddraw.hwnd; }
BOOL pochd_host_running(void) { return g_ddraw.render.run; }
HANDLE pochd_host_semaphore(void) { return g_ddraw.render.sem; }
DWORD pochd_host_fallback(void) { return gdi_render_main(); }
DWORD WINAPI pochd_render_main(void) { return pochd_render_run(); }

/* Fallback picture for the presenter when the game shows something without the usual canvas copy. */
BOOL pochd_host_primary(WORD* pixels)
{
    BOOL copied = FALSE;
    EnterCriticalSection(&g_ddraw.cs);
    if (g_ddraw.primary && g_ddraw.primary->surface && g_ddraw.primary->bpp == 16
        && g_ddraw.primary->width == POCHD_FRAME_W && g_ddraw.primary->height == POCHD_FRAME_H)
    {
        int y;
        for (y = 0; y < POCHD_FRAME_H; y++)
            memcpy(pixels + y * POCHD_FRAME_W, (const BYTE*)g_ddraw.primary->surface + y * g_ddraw.primary->pitch,
                POCHD_FRAME_W * 2);
        copied = TRUE;
    }
    LeaveCriticalSection(&g_ddraw.cs);
    return copied;
}

void pochd_host_viewport(int* values)
{
    values[0] = g_ddraw.render.width;
    values[1] = g_ddraw.render.height;
    values[2] = g_ddraw.render.viewport.x;
    values[3] = g_ddraw.render.viewport.y;
    values[4] = g_ddraw.render.viewport.width;
    values[5] = g_ddraw.render.viewport.height;
}

void pochd_attach(void)
{
    wchar_t path[MAX_PATH];
    wchar_t* slash;
    InitializeCriticalSection(&g_log_lock);
    GetModuleFileNameW(g_ddraw_module, g_pochd.directory, MAX_PATH - 1);
    slash = wcsrchr(g_pochd.directory, L'\\');
    if (slash) slash[1] = 0;
    load_config();
    _snwprintf(path, MAX_PATH - 1, L"%spochd.log", g_pochd.directory);
    path[MAX_PATH - 1] = 0;
    g_log = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    pochd_log("HD cards: attach, debug=%d filter=%d budget=%dMB images=%ls", g_pochd.debug, g_pochd.filter,
        g_pochd.texture_megabytes, g_pochd.images);
    if (!pochd_track_init()) { pochd_log("tracking unavailable"); return; }
    if (!pochd_game_install()) { pochd_log("unsupported executable: HD substitution is off, display scaling still works"); return; }
    pochd_cards_start();
    if (g_pochd.debug) CloseHandle(CreateThread(NULL, 0, command_thread, NULL, 0, NULL));
}
