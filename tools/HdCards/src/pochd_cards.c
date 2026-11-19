/* HD card artwork: card database.

   The game's own archive is the reference: every card bitmap it contains is reduced to a small
   colour grid. An image the game draws is recognised by comparing its grid with those references,
   so no card identifiers have to be recovered from game memory. The archive's card list maps each
   bitmap name to the card number that names the HD picture file. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "pochd.h"

#define GRID_X 6
#define GRID_Y 8
#define GRID_CELLS (GRID_X * GRID_Y)
#define SIGNATURE_BYTES (GRID_CELLS * 3)
#define MAX_CARDS 2048
#define MAX_ENTRIES 100000
#define ENTRY_BYTES 268
#define BIG_W 200
#define BIG_H 290
#define MINI_W 50
#define MINI_H 72

typedef struct CARD
{
    char name[24];                 /* bitmap name without extension, lower case */
    int number;                    /* card number from the archive list, -1 for the shared back */
    BOOL shared;                   /* several card numbers use this bitmap (placeholder art) */
    BOOL has_big, has_mini;
    BYTE big[SIGNATURE_BYTES];
    BYTE mini[SIGNATURE_BYTES];
    wchar_t* image;                /* HD picture path */
} CARD;

typedef struct ENTRY { char name[64]; DWORD offset, expanded, stored; } ENTRY;

static CARD* g_cards;
static int g_card_count;
static volatile LONG g_ready;

BOOL pochd_cards_ready(void) { return g_ready != 0; }
int pochd_cards_count(void) { return g_ready ? g_card_count : 0; }
const wchar_t* pochd_card_image(int card) { return card >= 0 && card < g_card_count ? g_cards[card].image : NULL; }
const char* pochd_card_name(int card) { return card >= 0 && card < g_card_count ? g_cards[card].name : "?"; }

/* ---- archive ---- */
static BOOL read_at(HANDLE file, DWORD offset, void* buffer, DWORD bytes)
{
    DWORD done = 0;
    if (SetFilePointer(file, (LONG)offset, NULL, FILE_BEGIN) == INVALID_SET_FILE_POINTER && GetLastError() != NO_ERROR) return FALSE;
    return ReadFile(file, buffer, bytes, &done, NULL) && done == bytes;
}

static BOOL lzss(const BYTE* in, DWORD in_size, BYTE* out, DWORD out_size)
{
    BYTE window[4096];
    DWORD ip = 0, op = 0, cursor = 0xFEE, flags = 0;
    memset(window, 0, sizeof(window));
    while (op < out_size)
    {
        flags >>= 1;
        if (!(flags & 0x100))
        {
            if (ip >= in_size) return FALSE;
            flags = in[ip++] | 0xFF00;
        }
        if (flags & 1)
        {
            if (ip >= in_size) return FALSE;
            out[op++] = window[cursor] = in[ip++];
            cursor = (cursor + 1) & 4095;
        }
        else
        {
            DWORD start, length, k;
            if (in_size - ip < 2) return FALSE;
            start = in[ip] | ((in[ip + 1] & 0xF0) << 4);
            length = (in[ip + 1] & 15) + 3;
            ip += 2;
            for (k = 0; k < length && op < out_size; k++)
            {
                BYTE value = window[(start + k) & 4095];
                out[op++] = window[cursor] = value;
                cursor = (cursor + 1) & 4095;
            }
        }
    }
    return TRUE;
}

static BYTE* read_entry(HANDLE file, const ENTRY* entry, DWORD limit)
{
    BYTE* stored;
    BYTE* expanded;
    if (!entry->expanded || entry->expanded > limit || !entry->stored || entry->stored > limit) return NULL;
    stored = (BYTE*)malloc(entry->stored);
    if (!stored || !read_at(file, entry->offset, stored, entry->stored)) { free(stored); return NULL; }
    if (entry->stored == entry->expanded) return stored;
    expanded = (BYTE*)malloc(entry->expanded);
    if (!expanded || !lzss(stored, entry->stored, expanded, entry->expanded)) { free(expanded); expanded = NULL; }
    free(stored);
    return expanded;
}

/* ---- signatures ---- */
/* Average colour per grid cell of a 24-bit bottom-up bitmap, quantised the way a 16-bit surface holds it. */
static BOOL signature_from_bmp(const BYTE* bmp, DWORD size, int width, int height, BYTE* signature)
{
    DWORD sums[GRID_CELLS][3];
    DWORD counts[GRID_CELLS];
    DWORD data_offset;
    int row_bytes = (width * 3 + 3) & ~3, x, y, c;
    if (size < 54 || bmp[0] != 'B' || bmp[1] != 'M') return FALSE;
    data_offset = *(const DWORD*)(bmp + 10);
    if (*(const LONG*)(bmp + 18) != width || *(const LONG*)(bmp + 22) != height || *(const WORD*)(bmp + 28) != 24
        || *(const DWORD*)(bmp + 30) != 0 || data_offset > size || size - data_offset < (DWORD)(row_bytes * height)) return FALSE;
    memset(sums, 0, sizeof(sums));
    memset(counts, 0, sizeof(counts));
    for (y = 0; y < height; y++)
    {
        const BYTE* row = bmp + data_offset + (height - 1 - y) * row_bytes;
        int cell_y = y * GRID_Y / height;
        for (x = 0; x < width; x++)
        {
            int cell = cell_y * GRID_X + x * GRID_X / width;
            BYTE b = row[x * 3] & 0xF8, g = row[x * 3 + 1] & 0xFC, r = row[x * 3 + 2] & 0xF8;
            sums[cell][0] += r | (r >> 5);
            sums[cell][1] += g | (g >> 6);
            sums[cell][2] += b | (b >> 5);
            counts[cell]++;
        }
    }
    for (c = 0; c < GRID_CELLS; c++)
    {
        if (!counts[c]) return FALSE;
        signature[c * 3] = (BYTE)(sums[c][0] / counts[c]);
        signature[c * 3 + 1] = (BYTE)(sums[c][1] / counts[c]);
        signature[c * 3 + 2] = (BYTE)(sums[c][2] / counts[c]);
    }
    return TRUE;
}

static int find_card(const char* name)
{
    int i;
    for (i = 0; i < g_card_count; i++)
        if (strcmp(g_cards[i].name, name) == 0) return i;
    return -1;
}

static void base_name(const char* file, char* out, size_t out_size)
{
    size_t length = 0;
    const char* dot = strrchr(file, '.');
    size_t limit = dot ? (size_t)(dot - file) : strlen(file);
    for (; length < limit && length + 1 < out_size; length++)
        out[length] = (char)((file[length] >= 'A' && file[length] <= 'Z') ? file[length] + 32 : file[length]);
    out[length] = 0;
}

/* The archive list pairs each bitmap with the card number printed in brackets on the comment line above it. */
static void parse_list(const BYTE* data, DWORD size)
{
    DWORD position = 0;
    int pending = -2;
    char* text = (char*)malloc(size + 1);
    if (!text) return;
    memcpy(text, data, size);
    text[size] = 0;
    while (position < size)
    {
        char* line = text + position;
        DWORD length = 0;
        while (position + length < size && line[length] != '\n' && line[length] != '\r') length++;
        position += length;
        while (position < size && (text[position] == '\n' || text[position] == '\r')) text[position++] = 0;
        if (length >= 2 && line[0] == '/' && line[1] == '/')
        {
            char* bracket = strstr(line, ":[");
            if (bracket && bracket[2] >= '0' && bracket[2] <= '9') pending = atoi(bracket + 2);
        }
        else if (length > 4 && pending != -2)
        {
            char name[24];
            int index;
            base_name(line, name, sizeof(name));
            index = find_card(name);
            if (index < 0 && g_card_count < MAX_CARDS)
            {
                index = g_card_count++;
                memset(&g_cards[index], 0, sizeof(CARD));
                strcpy(g_cards[index].name, name);
                g_cards[index].number = pending;
            }
            else if (index >= 0 && g_cards[index].number != pending) g_cards[index].shared = TRUE;
            pending = -2;
        }
    }
    free(text);
}

static void index_images(void)
{
    WIN32_FIND_DATAW found;
    wchar_t pattern[MAX_PATH];
    HANDLE search;
    int matched = 0;
    if (!g_pochd.images[0]) return;
    _snwprintf(pattern, MAX_PATH - 1, L"%s*.png", g_pochd.images);
    pattern[MAX_PATH - 1] = 0;
    search = FindFirstFileW(pattern, &found);
    if (search == INVALID_HANDLE_VALUE) { pochd_log("no HD pictures found in %ls", g_pochd.images); return; }
    do
    {
        const wchar_t* name = found.cFileName;
        int number = -2, i;
        size_t path_chars;
        if (_wcsicmp(name, L"card-back-shared.png") == 0) number = -1;
        else if (name[0] >= L'0' && name[0] <= L'9' && name[1] >= L'0' && name[1] <= L'9' && name[2] >= L'0' && name[2] <= L'9'
            && name[3] >= L'0' && name[3] <= L'9' && (name[4] == L' ' || name[4] == L'.'))
            number = (name[0] - L'0') * 1000 + (name[1] - L'0') * 100 + (name[2] - L'0') * 10 + (name[3] - L'0');
        if (number == -2) continue;
        path_chars = wcslen(g_pochd.images) + wcslen(name) + 1;
        for (i = 0; i < g_card_count; i++)
        {
            CARD* card = &g_cards[i];
            BOOL back = strcmp(card->name, "card_ura") == 0;
            if (card->image || (number == -1 ? !back : (back || card->shared || card->number != number))) continue;
            card->image = (wchar_t*)malloc(path_chars * sizeof(wchar_t));
            if (card->image) { wcscpy(card->image, g_pochd.images); wcscat(card->image, name); matched++; }
        }
    } while (FindNextFileW(search, &found));
    FindClose(search);
    pochd_log("HD pictures matched to %d of %d card bitmaps", matched, g_card_count);
}

static DWORD WINAPI build_thread(LPVOID unused)
{
    HANDLE file;
    BYTE header[12];
    BYTE* table = NULL;
    ENTRY* entries = NULL;
    DWORD count, i, start = GetTickCount();
    int big = 0, mini = 0, list = -1;
    (void)unused;
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    file = CreateFileW(g_pochd.archive, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (file == INVALID_HANDLE_VALUE) { pochd_log("cannot open game archive %ls (error %lu)", g_pochd.archive, GetLastError()); return 0; }
    if (!read_at(file, 0, header, 12) || memcmp(header, "KCEJYUGI", 8) != 0) goto done;
    count = *(DWORD*)(header + 8);
    if (!count || count > MAX_ENTRIES) goto done;
    table = (BYTE*)malloc(count * ENTRY_BYTES);
    entries = (ENTRY*)calloc(count, sizeof(ENTRY));
    g_cards = (CARD*)calloc(MAX_CARDS, sizeof(CARD));
    if (!table || !entries || !g_cards || !read_at(file, 12, table, count * ENTRY_BYTES)) goto done;
    for (i = 0; i < count; i++)
    {
        const BYTE* row = table + i * ENTRY_BYTES;
        int k;
        for (k = 0; k < 63; k++)
        {
            BYTE value = (BYTE)((row[k] << 4) | (row[k] >> 4));
            if (!value) break;
            entries[i].name[k] = (char)((value >= 'A' && value <= 'Z') ? value + 32 : value);
        }
        entries[i].offset = *(const DWORD*)(row + 256);
        entries[i].expanded = *(const DWORD*)(row + 260);
        entries[i].stored = *(const DWORD*)(row + 264);
        if (strcmp(entries[i].name, "card\\list_card.txt") == 0) list = (int)i;
    }
    if (list < 0) { pochd_log("the archive has no card list"); goto done; }
    {
        BYTE* text = read_entry(file, &entries[list], 4 * 1024 * 1024);
        if (!text) goto done;
        parse_list(text, entries[list].expanded);
        free(text);
    }
    for (i = 0; i < count; i++)
    {
        const char* name = entries[i].name;
        BOOL is_big = strncmp(name, "card\\", 5) == 0, is_mini = strncmp(name, "mini\\", 5) == 0;
        char base[24];
        int index;
        BYTE* data;
        if ((!is_big && !is_mini) || !strstr(name, ".bmp")) continue;
        base_name(name + 5, base, sizeof(base));
        index = find_card(base);
        if (index < 0) continue;
        data = read_entry(file, &entries[i], 1024 * 1024);
        if (!data) continue;
        if (is_big && signature_from_bmp(data, entries[i].expanded, BIG_W, BIG_H, g_cards[index].big)) { g_cards[index].has_big = TRUE; big++; }
        if (is_mini && signature_from_bmp(data, entries[i].expanded, MINI_W, MINI_H, g_cards[index].mini)) { g_cards[index].has_mini = TRUE; mini++; }
        free(data);
    }
    index_images();
    InterlockedExchange(&g_ready, 1);
    pochd_log("card index ready: %d bitmaps (%d large, %d small) in %lu ms", g_card_count, big, mini, GetTickCount() - start);
done:
    if (!g_ready) pochd_log("card index could not be built from %ls", g_pochd.archive);
    free(table);
    free(entries);
    CloseHandle(file);
    return 0;
}

BOOL pochd_cards_start(void)
{
    HANDLE thread = CreateThread(NULL, 0, build_thread, NULL, 0, NULL);
    if (!thread) return FALSE;
    CloseHandle(thread);
    return TRUE;
}

/* ---- identification ---- */
static void grid_from_surface(const WORD* bits, int pitch, int width, int height, int cells_x, int cells_y, BYTE* grid)
{
    DWORD sums[GRID_CELLS][3];
    DWORD counts[GRID_CELLS];
    int step = (width < height ? width : height) / 50, x, y, c;
    if (step < 1) step = 1;
    memset(sums, 0, sizeof(sums));
    memset(counts, 0, sizeof(counts));
    for (y = 0; y < height; y += step)
    {
        const WORD* row = (const WORD*)((const BYTE*)bits + y * pitch);
        int cell_y = y * cells_y / height;
        for (x = 0; x < width; x += step)
        {
            WORD v = row[x];
            int cell = cell_y * cells_x + x * cells_x / width;
            BYTE r = (BYTE)((v >> 8) & 0xF8), g = (BYTE)((v >> 3) & 0xFC), b = (BYTE)((v << 3) & 0xF8);
            sums[cell][0] += r | (r >> 5);
            sums[cell][1] += g | (g >> 6);
            sums[cell][2] += b | (b >> 5);
            counts[cell]++;
        }
    }
    for (c = 0; c < GRID_CELLS; c++)
    {
        DWORD n = counts[c] ? counts[c] : 1;
        grid[c * 3] = (BYTE)(sums[c][0] / n);
        grid[c * 3 + 1] = (BYTE)(sums[c][1] / n);
        grid[c * 3 + 2] = (BYTE)(sums[c][2] / n);
    }
}

/* Rearranges a grid measured in screen layout into card layout for one orientation. */
static void to_card_space(const BYTE* natural, int orientation, BYTE* card)
{
    int i, j;
    for (j = 0; j < GRID_Y; j++)
    {
        for (i = 0; i < GRID_X; i++)
        {
            int source;
            switch (orientation)
            {
            case POCHD_ORIENT_0: source = j * GRID_X + i; break;
            case POCHD_ORIENT_180: source = (GRID_Y - 1 - j) * GRID_X + (GRID_X - 1 - i); break;
            case POCHD_ORIENT_90: source = i * GRID_Y + (GRID_Y - 1 - j); break;      /* x = 1 - v, y = u */
            default: source = (GRID_X - 1 - i) * GRID_Y + j; break;                   /* x = v, y = 1 - u */
            }
            memcpy(card + (j * GRID_X + i) * 3, natural + source * 3, 3);
        }
    }
}

static int distance(const BYTE* a, const BYTE* b, int limit)
{
    int total = 0, k;
    for (k = 0; k < SIGNATURE_BYTES; k++)
    {
        int d = (int)a[k] - (int)b[k];
        total += d < 0 ? -d : d;
        if (total > limit) break;
    }
    return total;
}

int pochd_cards_identify(const WORD* bits, int pitch_bytes, int width, int height, int* orientation)
{
    BYTE natural[SIGNATURE_BYTES], oriented[2][SIGNATURE_BYTES];
    int orientations[2], best = -1, best_distance = 0x7FFFFFFF, second = 0x7FFFFFFF, best_orientation = 0, pass, o, i;
    BOOL portrait = height > width, prefer_mini;
    int longer = portrait ? height : width, shorter = portrait ? width : height;
    if (!g_ready || shorter < 24 || longer > 640) return -1;
    /* Card images keep the 200:290 shape; anything else is not worth measuring. */
    if (shorter * 290 < longer * 200 * 93 / 100 || shorter * 290 > longer * 200 * 107 / 100) return -1;
    grid_from_surface(bits, pitch_bytes, width, height, portrait ? GRID_X : GRID_Y, portrait ? GRID_Y : GRID_X, natural);
    orientations[0] = portrait ? POCHD_ORIENT_0 : POCHD_ORIENT_90;
    orientations[1] = portrait ? POCHD_ORIENT_180 : POCHD_ORIENT_270;
    to_card_space(natural, orientations[0], oriented[0]);
    to_card_space(natural, orientations[1], oriented[1]);
    prefer_mini = shorter <= 64;
    for (pass = 0; pass < 2 && (pass == 0 || best_distance > SIGNATURE_BYTES * 6); pass++)
    {
        BOOL use_mini = pass == 0 ? prefer_mini : !prefer_mini;
        for (i = 0; i < g_card_count; i++)
        {
            const CARD* card = &g_cards[i];
            if (!(use_mini ? card->has_mini : card->has_big)) continue;
            for (o = 0; o < 2; o++)
            {
                int d = distance(use_mini ? card->mini : card->big, oriented[o], second);
                if (d < best_distance)
                {
                    if (best != i) second = best_distance;
                    best_distance = d; best = i; best_orientation = orientations[o];
                }
                else if (d < second && i != best) second = d;
            }
        }
    }
    /* Mean error per channel: an exact copy measures about 0-2, a scaled copy a little more, another card far more. */
    if (best < 0 || best_distance > SIGNATURE_BYTES * 9) return -1;
    if (second < best_distance * 3 / 2 + SIGNATURE_BYTES * 2) return -1;
    if (orientation) *orientation = best_orientation;
    return best;
}
