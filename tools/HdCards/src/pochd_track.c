/* HD card artwork: draw tracking.

   Every image the game copies is followed here. A copy whose source is one of the game's card bitmaps
   leaves a layer on the destination: which card, where, and the exact pixels the copy produced. Copies
   from a surface that already carries layers (a side panel, a saved background) carry them along.

   When the game presents its canvas, each layer claims only the pixels that still hold exactly what
   the card copy wrote. Anything drawn on top afterwards - cursor, text, effects, other sprites - changed
   those pixels and therefore stays as the game drew it. Nothing is substituted on a guess.

   The game also mixes cards into the picture: a card fades in, an unavailable card is dimmed, a selected
   card glows. Those routines are observed before and after they run. From the three pictures - before,
   card, after - the mix is measured as after = card * gain + before * keep, and the HD picture is mixed
   the same way. A draw that does not fit that description is left as the game drew it.

   A card that is turned, scaled or squeezed while it moves (dealt from the deck, flipped, flying to the
   field) is drawn by the quad routines: plain, with a see-through colour, or mixed in. Such a draw is traced
   back pixel by pixel into the card, and the layer keeps the card's four corners instead of an upright
   rectangle.

   The same routines lay effects over a card that is already there: the shine on a summoned monster, a
   flash, a translucent banner. Such a draw is measured too: how much of what was there still shows
   (keep), and what came on top. The card stays a layer; each of its pixels remembers the part of the card
   still showing and the colour added, so that card * part + extra is exactly the pixel the game made.
   Pictures that carry their own transparency (flashes, rings, glows) are handled the same way, pixel by
   pixel. Only what is drawn opaquely over a card - cursor, text, numbers, panels - takes pixels from it. */
#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "pochd.h"
#include "pochd_game.h"

#define TABLE_SIZE 4096            /* tracked surfaces (power of two) */
#define MAX_SURFACE_LAYERS 320
#define PROBES 12

typedef struct LAYER
{
    int card, orientation;
    RECT dst;          /* in destination surface pixels, inside the surface */
    float uv[4];       /* the part of the oriented source image shown in dst */
    WORD* snap;        /* expected pixels, dst width x height */
    BYTE* valid;       /* 1 where the pixel came from the card */
    unsigned born;     /* presented-frame count when the copy happened */
    float gain;        /* shown pixel = card * gain * part + extra */
    BYTE* mix;         /* per pixel extra blue, green, red and part (0..255); NULL: all of the card, nothing extra */
    BOOL turned;       /* drawn by the quad routine: the picture's corners are not those of dst */
    unsigned checked;  /* presented-frame count + 1 when the layer was last compared with its surface */
    float corner[4][2];/* turned only: surface position of the corners of the uv part (TL, TR, BL, BR) */
} LAYER;

typedef struct SURFACE
{
    const void* bits;
    int width, height;
    int state;                 /* identification: 0 unknown, 1 card, 2 other */
    int card, orientation;
    WORD probes[PROBES];
    LAYER* layers;
    int count, capacity;
} SURFACE;

typedef struct IMAGE { const WORD* bits; int pitch, width, height; } IMAGE;

POCHD_LAYOUT g_pochd_layout;

static SURFACE g_table[TABLE_SIZE];
static int g_used;
static CRITICAL_SECTION g_frame_lock;
static CRITICAL_SECTION g_table_lock;
static POCHD_FRAME* g_frame;
static LONG g_plain_mismatch_logged;

BOOL pochd_track_init(void)
{
    InitializeCriticalSection(&g_frame_lock);
    InitializeCriticalSection(&g_table_lock);
    g_frame = (POCHD_FRAME*)calloc(1, sizeof(POCHD_FRAME));
    return g_frame != NULL;
}

/* ---- surface table ---- */
static void free_layer(LAYER* layer)
{
    free(layer->snap);
    free(layer->mix);
    layer->snap = NULL;
    layer->valid = NULL;
    layer->mix = NULL;
}

/* Gives a layer its per-pixel mix: the whole card, nothing added. */
static BOOL need_mix(LAYER* layer)
{
    int area = (layer->dst.right - layer->dst.left) * (layer->dst.bottom - layer->dst.top), i;
    if (layer->mix) return TRUE;
    layer->mix = (BYTE*)malloc((size_t)area * 4);
    if (!layer->mix) return FALSE;
    for (i = 0; i < area; i++) *(DWORD*)(layer->mix + i * 4) = 0xFF000000u;
    return TRUE;
}

static void clear_layers(SURFACE* surface)
{
    int i;
    for (i = 0; i < surface->count; i++) free_layer(&surface->layers[i]);
    surface->count = 0;
}

static void reset_table(void)
{
    int i;
    for (i = 0; i < TABLE_SIZE; i++)
    {
        clear_layers(&g_table[i]);
        free(g_table[i].layers);
    }
    memset(g_table, 0, sizeof(g_table));
    g_used = 0;
}

static SURFACE* find_surface(const void* bits, BOOL create)
{
    unsigned slot = (((unsigned)(ULONG_PTR)bits >> 12) * 2654435761u) >> 20;
    int tries;
    for (tries = 0; tries < TABLE_SIZE; tries++)
    {
        SURFACE* surface = &g_table[(slot + tries) & (TABLE_SIZE - 1)];
        if (surface->bits == bits) return surface;
        if (!surface->bits)
        {
            if (!create) return NULL;
            if (g_used > TABLE_SIZE * 3 / 4) { reset_table(); return find_surface(bits, TRUE); }
            surface->bits = bits;
            g_used++;
            return surface;
        }
    }
    return NULL;
}

static BOOL read_image(void* object, IMAGE* image)
{
    BOOL ok = FALSE;
    __try
    {
        const BYTE* p = (const BYTE*)object;
        int left = *(const int*)(p + g_pochd_layout.left), top = *(const int*)(p + g_pochd_layout.top);
        image->bits = *(const WORD* const*)(p + g_pochd_layout.bits);
        image->pitch = *(const int*)(p + g_pochd_layout.pitch);
        image->width = *(const int*)(p + g_pochd_layout.right) - left;
        image->height = *(const int*)(p + g_pochd_layout.bottom) - top;
        ok = *(const int*)(p + g_pochd_layout.format) == g_pochd_layout.format_565 && left == 0 && top == 0 && image->bits
            && image->width > 0 && image->width <= 4096 && image->height > 0 && image->height <= 4096
            && image->pitch >= image->width * 2 && image->pitch <= 16384;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { ok = FALSE; }
    return ok;
}

/* The library's pictures with transparency: four bytes a pixel. */
static BOOL read_alpha_image(void* object, IMAGE* image)
{
    BOOL ok = FALSE;
    __try
    {
        const BYTE* p = (const BYTE*)object;
        int left = *(const int*)(p + g_pochd_layout.left), top = *(const int*)(p + g_pochd_layout.top);
        image->bits = *(const WORD* const*)(p + g_pochd_layout.bits);
        image->pitch = *(const int*)(p + g_pochd_layout.pitch);
        image->width = *(const int*)(p + g_pochd_layout.right) - left;
        image->height = *(const int*)(p + g_pochd_layout.bottom) - top;
        ok = *(const int*)(p + g_pochd_layout.format) == g_pochd_layout.format_alpha && left == 0 && top == 0 && image->bits
            && image->width > 0 && image->width <= 4096 && image->height > 0 && image->height <= 4096
            && image->pitch >= image->width * 4 && image->pitch <= 32768;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { ok = FALSE; }
    return ok;
}

static const WORD* pixel_row(const IMAGE* image, int y)
{
    return (const WORD*)((const BYTE*)image->bits + y * image->pitch);
}

/* ---- identification cache ---- */
static void read_probes(const IMAGE* image, WORD* probes)
{
    int k;
    /* All probes lie in the middle of the picture, where cards differ; frames and borders repeat. */
    for (k = 0; k < PROBES; k++)
    {
        int x = image->width * (20 + (k * 37) % 61) / 100, y = image->height * (22 + (k * 23) % 47) / 100;
        probes[k] = pixel_row(image, y)[x];
    }
}

static BOOL card_shaped(int width, int height)
{
    int longer = width > height ? width : height, shorter = width > height ? height : width;
    return shorter >= 24 && longer <= 640 && shorter * 290 >= longer * 200 * 93 / 100 && shorter * 290 <= longer * 200 * 107 / 100;
}

/* Diagnostics write small 24-bit bitmaps beside the log. */
static void save_bmp(const wchar_t* path, const WORD* bits, int pitch_bytes, int width, int height)
{
    int row = (width * 3 + 3) & ~3, x, y;
    BITMAPFILEHEADER header = { 0x4D42, 54 + row * height, 0, 0, 54 };
    BITMAPINFOHEADER info = { 40, width, height, 1, 24, 0, row * height, 0, 0, 0, 0 };
    BYTE* line = (BYTE*)calloc(1, row);
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    DWORD written;
    if (file != INVALID_HANDLE_VALUE && line)
    {
        WriteFile(file, &header, sizeof(header), &written, NULL);
        WriteFile(file, &info, sizeof(info), &written, NULL);
        for (y = height - 1; y >= 0; y--)
        {
            const WORD* source = (const WORD*)((const BYTE*)bits + y * pitch_bytes);
            for (x = 0; x < width; x++)
            {
                WORD v = source[x];
                line[x * 3] = (BYTE)((v << 3) & 0xF8);
                line[x * 3 + 1] = (BYTE)((v >> 3) & 0xFC);
                line[x * 3 + 2] = (BYTE)((v >> 8) & 0xF8);
            }
            WriteFile(file, line, row, &written, NULL);
        }
    }
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    free(line);
}

/* Diagnostic (debug=3): keeps the first card-shaped pictures that were not recognised, for inspection. */
static void save_unrecognised(const IMAGE* image)
{
    static LONG saved;
    wchar_t path[MAX_PATH];
    LONG index = InterlockedIncrement(&saved);
    if (index > 60) return;
    _snwprintf(path, MAX_PATH - 1, L"%sunrecognised-%02ld-%dx%d.bmp", g_pochd.directory, index, image->width, image->height);
    path[MAX_PATH - 1] = 0;
    save_bmp(path, image->bits, image->pitch, image->width, image->height);
}

static int identify(const IMAGE* image, SURFACE** entry, int* orientation)
{
    WORD probes[PROBES];
    SURFACE* surface;
    if (!card_shaped(image->width, image->height)) return -1;
    surface = find_surface(image->bits, TRUE);
    if (!surface) return -1;
    *entry = surface;
    read_probes(image, probes);
    if (!surface->state || surface->width != image->width || surface->height != image->height
        || memcmp(surface->probes, probes, sizeof(probes)) != 0)
    {
        if (!pochd_cards_ready()) return -1;
        surface->card = pochd_cards_identify(image->bits, image->pitch, image->width, image->height, &surface->orientation);
        /* A card that has no HD picture is of no further interest. */
        if (surface->card >= 0 && !pochd_card_image(surface->card)) surface->card = -1;
        surface->state = surface->card >= 0 ? 1 : 2;
        surface->width = image->width;
        surface->height = image->height;
        memcpy(surface->probes, probes, sizeof(probes));
        POCHD_DEBUG("identify %p %dx%d -> %s orientation %d", image->bits, image->width, image->height,
            surface->card >= 0 ? pochd_card_name(surface->card) : "-", surface->orientation);
        if (g_pochd.debug >= 3 && surface->card < 0) save_unrecognised(image);
    }
    *orientation = surface->orientation;
    return surface->state == 1 ? surface->card : -1;
}

/* ---- layers ---- */
/* A card drawn again over exactly the same area replaces what was recorded there: a screen that keeps
   redrawing a card in place must not pile up records until whole cards fall off the end. */
static void drop_same_area(SURFACE* surface, const RECT* dst, const LAYER* except)
{
    int i, kept = 0;
    for (i = 0; i < surface->count; i++)
    {
        if (&surface->layers[i] != except && EqualRect(&surface->layers[i].dst, dst)) free_layer(&surface->layers[i]);
        else surface->layers[kept++] = surface->layers[i];
    }
    surface->count = kept;
}

/* A card drawn one strip at a time (a scene that is put on screen row by row): when the newest layer
   continues an earlier one downwards, the two become one. A screen full of such cards would otherwise
   need more layers than a frame can hold. The strips of several cards side by side arrive in turn, so
   the layer to continue is looked for among the recent ones. */
static void merge_strips(SURFACE* surface)
{
    LAYER* a;
    LAYER* b;
    WORD* snap;
    BYTE* valid;
    BYTE* mix = NULL;
    size_t width, ha, hb;
    int k;
    if (surface->count < 2) return;
    b = &surface->layers[surface->count - 1];
    if (b->dst.bottom - b->dst.top > 8 || b->turned) return;
    for (a = NULL, k = surface->count - 2; k >= 0 && k >= surface->count - 98; k--)
    {
        LAYER* c = &surface->layers[k];
        if (c->card == b->card && c->orientation == b->orientation && !c->turned && c->gain == b->gain
            && (c->mix == NULL) == (b->mix == NULL) && c->dst.left == b->dst.left && c->dst.right == b->dst.right
            && c->dst.bottom == b->dst.top && fabs(c->uv[0] - b->uv[0]) <= 1e-5 && fabs(c->uv[2] - b->uv[2]) <= 1e-5
            && fabs(c->uv[3] - b->uv[1]) <= 0.02)
        {
            a = c;
            break;
        }
    }
    if (!a) return;
    width = (size_t)(a->dst.right - a->dst.left);
    ha = (size_t)(a->dst.bottom - a->dst.top);
    hb = (size_t)(b->dst.bottom - b->dst.top);
    snap = (WORD*)malloc(width * (ha + hb) * 3);
    if (!snap) return;
    if (a->mix && !(mix = (BYTE*)malloc(width * (ha + hb) * 4))) { free(snap); return; }
    valid = (BYTE*)(snap + width * (ha + hb));
    memcpy(snap, a->snap, width * ha * 2);
    memcpy(snap + width * ha, b->snap, width * hb * 2);
    memcpy(valid, a->valid, width * ha);
    memcpy(valid + width * ha, b->valid, width * hb);
    if (mix)
    {
        memcpy(mix, a->mix, width * ha * 4);
        memcpy(mix + width * ha * 4, b->mix, width * hb * 4);
    }
    free_layer(a);
    a->snap = snap;
    a->valid = valid;
    a->mix = mix;
    a->dst.bottom = b->dst.bottom;
    a->uv[3] = b->uv[3];
    a->born = b->born;
    free_layer(b);
    surface->count--;
    drop_same_area(surface, &a->dst, a);
}

static LAYER* add_layer(SURFACE* surface, const RECT* dst)
{
    LAYER* layer;
    int width = dst->right - dst->left, height = dst->bottom - dst->top;
    drop_same_area(surface, dst, NULL);
    if (surface->count >= MAX_SURFACE_LAYERS)
    {
        free_layer(&surface->layers[0]);
        memmove(&surface->layers[0], &surface->layers[1], (surface->count - 1) * sizeof(LAYER));
        surface->count--;
    }
    if (surface->count == surface->capacity)
    {
        int capacity = surface->capacity ? surface->capacity * 2 : 16;
        LAYER* grown = (LAYER*)realloc(surface->layers, capacity * sizeof(LAYER));
        if (!grown) return NULL;
        surface->layers = grown;
        surface->capacity = capacity;
    }
    layer = &surface->layers[surface->count];
    memset(layer, 0, sizeof(*layer));
    layer->snap = (WORD*)malloc((size_t)width * height * 3);
    if (!layer->snap) return NULL;
    layer->valid = (BYTE*)(layer->snap + (size_t)width * height);
    layer->dst = *dst;
    layer->born = g_frame ? g_frame->serial : 0;
    layer->gain = 1.0f;
    surface->count++;
    return layer;
}

/* A plain copy overwrites what it covers: layers that lie completely inside it are gone. */
static void drop_covered(SURFACE* surface, const RECT* covered)
{
    int i, kept = 0;
    for (i = 0; i < surface->count; i++)
    {
        LAYER* layer = &surface->layers[i];
        if (layer->dst.left >= covered->left && layer->dst.top >= covered->top
            && layer->dst.right <= covered->right && layer->dst.bottom <= covered->bottom)
            free_layer(layer);
        else
            surface->layers[kept++] = *layer;
    }
    surface->count = kept;
}

static void snapshot(const IMAGE* image, const RECT* rect, WORD* out)
{
    int y, width = rect->right - rect->left;
    for (y = rect->top; y < rect->bottom; y++, out += width)
        memcpy(out, pixel_row(image, y) + rect->left, (size_t)width * 2);
}

static void track_copy(void* dst_object, void* src_object, int x, int y, const RECT* src_rect, const SIZE* dst_size,
    const RECT* clip);
static void refresh_layers(SURFACE* surface, const IMAGE* image);

/* ---- mixing draws ---- */
typedef struct EFFECT
{
    IMAGE dst, src;
    int card, orientation;         /* card < 0: not a card, but drawn over one */
    int routine, level;
    RECT source, target, covered;
    int source_w, source_h, target_w, target_h;
    WORD* before;
    /* quad draws: source pixel (source.left + a * span_x, source.top + b * span_y) lands on
       target pixel origin + a * across + b * down, for a and b from 0 to 1 */
    BOOL quad;
    BOOL blind;                    /* the source cannot be read: only what it did to the target is known */
    BOOL alpha;                    /* the source carries its own transparency (four bytes a pixel) */
    int span_x, span_y;
    float origin[2], across[2], down[2];
} EFFECT;

static const float g_limit[3] = { 31.0f, 63.0f, 31.0f }, g_tolerance[3] = { 1.6f, 2.6f, 1.6f };

/* What a mixing routine leaves of the picture underneath, by design: the strength counts in sixteen steps
   through a table (measured: level 128 keeps 7/15), and adding takes nothing away. A picture with its own
   transparency is worked out pixel by pixel instead. */
static double keep_by_design(int routine, int level)
{
    return routine >= 2 ? 1.0 : (15 - ((level >> 4) & 15)) / 15.0;
}

static BOOL touches_layer(const SURFACE* surface, const RECT* rect)
{
    RECT part;
    int i;
    for (i = 0; surface && i < surface->count; i++)
        if (IntersectRect(&part, &surface->layers[i].dst, rect)) return TRUE;
    return FALSE;
}

static void channels(WORD value, float* out)
{
    out[0] = (float)(value >> 11);
    out[1] = (float)((value >> 5) & 63);
    out[2] = (float)(value & 31);
}

/* The card pixel the routine read for one destination pixel (nearest source pixel when it scales). */
static WORD effect_source(const EFFECT* effect, int px, int py)
{
    int sx = effect->source.left + (int)(((LONGLONG)(effect->covered.left + px - effect->target.left) * effect->source_w) / effect->target_w);
    int sy = effect->source.top + (int)(((LONGLONG)(effect->covered.top + py - effect->target.top) * effect->source_h) / effect->target_h);
    if (sx < effect->source.left) sx = effect->source.left;
    if (sx >= effect->source.right) sx = effect->source.right - 1;
    if (sy < effect->source.top) sy = effect->source.top;
    if (sy >= effect->source.bottom) sy = effect->source.bottom - 1;
    return pixel_row(&effect->src, sy)[sx];
}

void* pochd_track_effect_begin(void* dst_object, void* src_object, int x, int y, const RECT* src_rect, const SIZE* dst_size,
    const RECT* clip, int routine, int level)
{
    EFFECT local;
    EFFECT* effect;
    SURFACE* src_entry = NULL;
    SURFACE* dst_entry;
    RECT bounds;
    BOOL readable = TRUE;
    int width, height;
    if (!dst_object || !src_object) return NULL;
    EnterCriticalSection(&g_table_lock);
    memset(&local, 0, sizeof(local));
    if (!read_image(dst_object, &local.dst)) goto untracked;
    local.card = -1;
    if (routine >= 3)
    {
        /* A picture with its own transparency: never a card, but often an effect drawn over one. */
        if (!read_alpha_image(src_object, &local.src)) goto untracked;
        local.alpha = TRUE;
    }
    else
    {
        /* A source in another pixel format cannot be a card, but it can be an effect drawn over one. */
        local.blind = !read_image(src_object, &local.src);
        if (!local.blind && local.src.bits == local.dst.bits) goto untracked;
        if (!local.blind) local.card = identify(&local.src, &src_entry, &local.orientation);
    }
    local.routine = routine;
    local.level = level;
    dst_entry = find_surface(local.dst.bits, FALSE);
    /* Not a card: of interest only when it is drawn over one. */
    if (local.card < 0 && !(dst_entry && dst_entry->count)) goto untracked;
    SetRect(&bounds, 0, 0, local.dst.width, local.dst.height);
    if (local.blind && !src_rect && !dst_size) goto untracked;      /* its size is not known either */
    __try
    {
        if (src_rect) local.source = *src_rect;
        else if (local.blind) SetRect(&local.source, 0, 0, dst_size->cx, dst_size->cy);
        else SetRect(&local.source, 0, 0, local.src.width, local.src.height);
        local.source_w = local.source.right - local.source.left;
        local.source_h = local.source.bottom - local.source.top;
        local.target_w = dst_size ? dst_size->cx : local.source_w;
        local.target_h = dst_size ? dst_size->cy : local.source_h;
        if (clip && !IntersectRect(&bounds, &bounds, clip)) readable = FALSE;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { readable = FALSE; }
    if (!readable || local.source_w <= 0 || local.source_h <= 0 || local.target_w <= 0 || local.target_h <= 0) goto untracked;
    if (!local.blind && (local.source.left < 0 || local.source.top < 0 || local.source.right > local.src.width
        || local.source.bottom > local.src.height)) goto untracked;
    SetRect(&local.target, x, y, x + local.target_w, y + local.target_h);
    if (!IntersectRect(&local.covered, &local.target, &bounds)) goto untracked;
    if (local.card < 0 && !touches_layer(dst_entry, &local.covered)) goto untracked;
    width = local.covered.right - local.covered.left;
    height = local.covered.bottom - local.covered.top;
    effect = (EFFECT*)malloc(sizeof(EFFECT));
    local.before = (WORD*)malloc((size_t)width * height * 2);
    if (!effect || !local.before) { free(effect); free(local.before); goto untracked; }
    snapshot(&local.dst, &local.covered, local.before);
    *effect = local;
    return effect;      /* the table stays locked until the matching end */
untracked:
    LeaveCriticalSection(&g_table_lock);
    return NULL;
}

/* Where a pixel of an upright layer lies in its card picture, and how far one surface pixel reaches there. */
static void card_position(const LAYER* layer, int x, int y, float* at, float* reach)
{
    reach[0] = (layer->uv[2] - layer->uv[0]) / (layer->dst.right - layer->dst.left);
    reach[1] = (layer->uv[3] - layer->uv[1]) / (layer->dst.bottom - layer->dst.top);
    at[0] = layer->uv[0] + reach[0] * (x - layer->dst.left + 0.5f);
    at[1] = layer->uv[1] + reach[1] * (y - layer->dst.top + 0.5f);
}

/* The layer of a surface that shows the same spot of the same card, at the same size, as a pixel of another
   layer does, and still holds it. The games mix a scene with a second copy of itself (dimmed, say) to fade
   between the two: the card is then on both sides of the mix, and all of it is still the card. */
static const LAYER* same_card_at(const SURFACE* surface, int x, int y, WORD value, const LAYER* like, int like_x, int like_y,
    int* index, int* why)
{
    float want[2], want_reach[2], at[2], reach[2];
    int i;
    card_position(like, like_x, like_y, want, want_reach);
    *why = 0;                   /* no layer of the source holds this pixel */
    for (i = surface->count - 1; i >= 0; i--)
    {
        const LAYER* layer = &surface->layers[i];
        if (x < layer->dst.left || x >= layer->dst.right || y < layer->dst.top || y >= layer->dst.bottom) continue;
        *index = (y - layer->dst.top) * (layer->dst.right - layer->dst.left) + (x - layer->dst.left);
        /* the topmost layer that holds this pixel decides: what lies under it does not show */
        if (!layer->valid[*index] || layer->snap[*index] != value) { *why = 1; continue; }
        *why = 2;               /* another card, or the same one turned */
        if (layer->turned || layer->card != like->card || layer->orientation != like->orientation) return NULL;
        card_position(layer, x, y, at, reach);
        *why = 3;               /* the same card at another spot or size */
        if (fabs(reach[0] - want_reach[0]) > 0.02 * fabs(want_reach[0]) || fabs(reach[1] - want_reach[1]) > 0.02 * fabs(want_reach[1])
            || fabs(at[0] - want[0]) > 0.25 * fabs(want_reach[0]) || fabs(at[1] - want[1]) > 0.25 * fabs(want_reach[1])) return NULL;
        *why = 4;
        return layer;
    }
    return NULL;
}

/* Something that is not a card was mixed over cards. Works out how much of what was there still shows,
   and keeps every touched card pixel as card * part + extra, which is exactly the pixel the game made. */
static void overlay_end(EFFECT* effect, const WORD* after, int width, int height)
{
    static LONG logged;
    SURFACE* dst_entry = find_surface(effect->dst.bits, FALSE);
    SURFACE* src_entry = NULL;
    int step = 1, px, py, c, i, changed = 0, kept_pixels = 0, lost_pixels = 0, twin_pixels = 0, reasons[6] = { 0 };
    double sbb = 0, sbs = 0, sss = 0, sab = 0, sas = 0, determinant, keep, gain = 0;
    const char* how = "measured";
    BOOL measurable = !effect->quad && !effect->blind && !effect->alpha, twin = FALSE;
    if (!dst_entry) return;
    /* A source that carries card layers itself, drawn unscaled by the blending routines: cards may be on
       both sides. It is as a rule the same scene as the target, so the two cannot be told apart by fitting. */
    if (measurable && effect->routine < 2 && effect->target_w == effect->source_w && effect->target_h == effect->source_h)
    {
        src_entry = find_surface(effect->src.bits, FALSE);
        twin = src_entry && src_entry->count > 0;
        if (twin)
        {
            measurable = FALSE;
            refresh_layers(src_entry, &effect->src);
        }
    }
    while ((width / step) * (height / step) > 4000) step++;
    for (py = 0; py < height; py += step)
    {
        for (px = 0; px < width; px += step)
        {
            WORD b = effect->before[py * width + px], a = after[py * width + px], s;
            float fb[3], fa[3], fs[3];
            if (a == b) continue;                       /* a see-through part of the effect */
            if (!measurable) { changed++; continue; }
            s = effect_source(effect, px, py);
            channels(b, fb); channels(a, fa); channels(s, fs);
            changed++;
            for (c = 0; c < 3; c++)
            {
                double vb = fb[c] / g_limit[c], va = fa[c] / g_limit[c], vs = fs[c] / g_limit[c];
                if (fa[c] >= g_limit[c]) continue;      /* possibly clipped at white */
                sbb += vb * vb; sbs += vb * vs; sss += vs * vs; sab += va * vb; sas += va * vs;
            }
        }
    }
    if (!changed) return;
    determinant = sbb * sss - sbs * sbs;
    if (measurable && sbb > 1e-6 && sss < 1e-6 * sbb)
    {
        keep = sab / sbb;                               /* the effect itself is black: it only darkens */
    }
    else if (measurable && sbb > 1e-6 && determinant > 1e-3 * sbb * sss)
    {
        keep = (sab * sss - sas * sbs) / determinant;
        gain = (sas * sbb - sab * sbs) / determinant;
    }
    else
    {
        /* Nothing to measure from (a turned or unreadable source, or flat colours on both sides): what the
           routine does by design. */
        keep = keep_by_design(effect->routine, effect->level);
        how = "by design";
    }
    if (keep > 1.0) keep = 1.0;
    if (keep >= 0.04 || twin)
    {
        for (i = 0; i < dst_entry->count; i++)
        {
            LAYER* layer = &dst_entry->layers[i];
            RECT part;
            int layer_w = layer->dst.right - layer->dst.left;
            if (!IntersectRect(&part, &layer->dst, &effect->covered)) continue;
            for (py = part.top; py < part.bottom; py++)
            {
                for (px = part.left; px < part.right; px++)
                {
                    int e = (py - effect->covered.top) * width + (px - effect->covered.left);
                    int l = (py - layer->dst.top) * layer_w + (px - layer->dst.left);
                    WORD b = effect->before[e], a = after[e];
                    float fb[3], fa[3], share = (float)keep;
                    BYTE* m;
                    if (a == b || !layer->valid[l] || layer->snap[l] != b) continue;
                    channels(b, fb); channels(a, fa);
                    if (twin && !layer->turned && layer->gain > 0.01f)
                    {
                        /* The same card at the same spot in the source: after = before * keep + source * rest,
                           and both are this card, so its share is the two shares mixed the same way. */
                        int sx = effect->source.left + (px - effect->target.left), sy = effect->source.top + (py - effect->target.top), at, why;
                        WORD s = pixel_row(&effect->src, sy)[sx];
                        const LAYER* other = same_card_at(src_entry, sx, sy, s, layer, px, py, &at, &why);
                        float fs[3], rest = 1.0f - (float)keep, off[3];
                        BOOL fits = other != NULL;
                        channels(s, fs);
                        for (c = 0; fits && c < 3; c++)
                        {
                            off[c] = fa[c] - (fb[c] * (float)keep + fs[c] * rest);
                            fits = fabs(off[c]) <= g_tolerance[c] + 0.6f;
                        }
                        if (other && !fits) why = 5;    /* the routine did something else than mix the two */
                        reasons[why]++;
                        if (fits && need_mix(layer))
                        {
                            const BYTE* theirs = other->mix ? other->mix + at * 4 : NULL;
                            float value = (layer->mix[l * 4 + 3] * (float)keep
                                + (theirs ? theirs[3] : 255.0f) * rest * other->gain / layer->gain);
                            m = layer->mix + l * 4;
                            for (c = 0; c < 3; c++)
                            {
                                float extra = m[2 - c] * (float)keep + (theirs ? theirs[2 - c] : 0.0f) * rest + off[c] * 255.0f / g_limit[c];
                                m[2 - c] = (BYTE)(extra < 0.0f ? 0.0f : extra > 255.0f ? 255.0f : extra + 0.5f);
                            }
                            m[3] = (BYTE)(value > 255.0f ? 255.0f : value + 0.5f);
                            layer->snap[l] = a;
                            kept_pixels++;
                            twin_pixels++;
                            continue;
                        }
                    }
                    if (keep < 0.04) { lost_pixels++; continue; }
                    if (effect->alpha)
                    {
                        /* The picture says how see-through it is here; how much of what was underneath is
                           left follows from the three colours: after = picture + (before - picture) * share. */
                        int sx = effect->source.left + (int)(((LONGLONG)(px - effect->target.left) * effect->source_w) / effect->target_w);
                        int sy = effect->source.top + (int)(((LONGLONG)(py - effect->target.top) * effect->source_h) / effect->target_h);
                        const BYTE* texel;
                        float fs[3], above = 0, below = 0;
                        if (sx < 0 || sy < 0 || sx >= effect->src.width || sy >= effect->src.height) { lost_pixels++; continue; }
                        texel = (const BYTE*)effect->src.bits + (size_t)sy * effect->src.pitch + (size_t)sx * 4;
                        channels(*(const WORD*)texel, fs);
                        for (c = 0; c < 3; c++)
                        {
                            float db = (fb[c] - fs[c]) / g_limit[c], da = (fa[c] - fs[c]) / g_limit[c];
                            above += da * db;
                            below += db * db;
                        }
                        share = below > 0.004f ? above / below : (15 - (texel[2] & 15)) / 15.0f;
                        if (share > 1.0f) share = 1.0f;
                    }
                    /* Darker than that share of the card: less of the card shows at this pixel. */
                    for (c = 0; c < 3; c++)
                        if (fb[c] * share > fa[c] + g_tolerance[c]) share = fa[c] / fb[c];
                    if (share < 0.04f || !need_mix(layer)) { lost_pixels++; continue; }
                    m = layer->mix + l * 4;
                    if (m[3] * share < 8.0f) { lost_pixels++; continue; }
                    for (c = 0; c < 3; c++)
                    {
                        float extra = (fa[c] - fb[c] * share) * 255.0f / g_limit[c];
                        float value = m[2 - c] * share + (extra > 0 ? extra : 0) + 0.5f;
                        m[2 - c] = (BYTE)(value > 255.0f ? 255.0f : value);
                    }
                    m[3] = (BYTE)(m[3] * share + 0.5f);
                    layer->snap[l] = a;
                    kept_pixels++;
                }
            }
        }
    }
    if (g_pochd.debug >= 2 && (kept_pixels || lost_pixels) && InterlockedIncrement(&logged) <= 4000)
        pochd_log("overlay %s%d level %d source %dx%d at %ld,%ld %dx%d: gain %.3f keep %.3f %s, %d changed, card pixels kept %d lost %d%s",
            effect->quad ? "q" : "r", effect->routine, effect->level, effect->src.width, effect->src.height, effect->covered.left, effect->covered.top,
            width, height, gain, keep, how, changed, kept_pixels, lost_pixels,
            twin_pixels == kept_pixels && kept_pixels ? ", the same cards on both sides" : twin_pixels ? ", partly the same cards on both sides" : "");
    if (g_pochd.debug >= 4 && twin && logged <= 4000)
        pochd_log("  source layers %d: no layer %d, not held %d, other card %d, other spot %d, same %d, not a mix %d", src_entry->count,
            reasons[0], reasons[1], reasons[2], reasons[3], reasons[4], reasons[5]);
}

void* pochd_track_quad_begin(void* dst_object, void* src_object, const POINT* source_points, const POINT* target_points,
    int count, int level)
{
    EFFECT local;
    EFFECT* effect;
    SURFACE* src_entry = NULL;
    SURFACE* dst_entry;
    POINT s[4], t[4];
    RECT bounds, box;
    BOOL readable = TRUE, regular;
    float det = 0, edge_a, edge_b, low[2], high[2];
    int width, height, i;
    if (!dst_object || !src_object || !source_points || !target_points || count != 4) return NULL;
    EnterCriticalSection(&g_table_lock);
    memset(&local, 0, sizeof(local));
    if (!read_image(dst_object, &local.dst)) goto untracked;
    /* The game also uses these routines to derive its half-size and turned copies of a card. Those are
       recognised by their content when they are drawn; here only what is drawn into a scene matters. */
    if (card_shaped(local.dst.width, local.dst.height)) goto untracked;
    local.card = -1;
    local.quad = TRUE;
    local.routine = 0;
    local.level = level;
    local.blind = !read_image(src_object, &local.src);
    if (!local.blind && local.src.bits == local.dst.bits) goto untracked;
    if (!local.blind) local.card = identify(&local.src, &src_entry, &local.orientation);
    dst_entry = find_surface(local.dst.bits, FALSE);
    /* Not a card: of interest only when it is mixed over one. Drawn as it is, it simply covers what it covers. */
    if (local.card < 0 && (level < 0 || !(dst_entry && dst_entry->count))) goto untracked;
    __try { memcpy(s, source_points, sizeof(s)); memcpy(t, target_points, sizeof(t)); }
    __except (EXCEPTION_EXECUTE_HANDLER) { readable = FALSE; }
    if (!readable) goto untracked;
    low[0] = low[1] = 1e9f; high[0] = high[1] = -1e9f;
    /* A card must come from an upright rectangle given as top-left, top-right, bottom-right, bottom-left
       and land on a parallelogram: what a turn, a scale and a squeeze make of it. */
    regular = s[0].y == s[1].y && s[1].x == s[2].x && s[2].y == s[3].y && s[3].x == s[0].x && s[1].x > s[0].x && s[3].y > s[0].y
        && abs(t[0].x + t[2].x - t[1].x - t[3].x) <= 2 && abs(t[0].y + t[2].y - t[1].y - t[3].y) <= 2;
    if (local.card >= 0)
    {
        if (!regular || s[0].x < 0 || s[0].y < 0 || s[1].x >= local.src.width || s[3].y >= local.src.height) goto untracked;
        SetRect(&local.source, s[0].x, s[0].y, s[1].x + 1, s[3].y + 1);
        local.span_x = s[1].x - s[0].x;
        local.span_y = s[3].y - s[0].y;
        local.origin[0] = (float)t[0].x; local.origin[1] = (float)t[0].y;
        local.across[0] = (float)(t[1].x - t[0].x); local.across[1] = (float)(t[1].y - t[0].y);
        local.down[0] = (float)(t[3].x - t[0].x); local.down[1] = (float)(t[3].y - t[0].y);
        det = local.across[0] * local.down[1] - local.across[1] * local.down[0];
        if (det > -24.0f && det < 24.0f) goto untracked;    /* seen edge-on: a line, nothing to show */
        /* Bounds of the whole picture on the target: half a source pixel beyond the outermost pixel centres. */
        edge_a = 0.5f / local.span_x;
        edge_b = 0.5f / local.span_y;
        for (i = 0; i < 4; i++)
        {
            float a = (i & 1) ? 1.0f + edge_a : -edge_a, b = (i & 2) ? 1.0f + edge_b : -edge_b;
            float x = local.origin[0] + a * local.across[0] + b * local.down[0], y = local.origin[1] + a * local.across[1] + b * local.down[1];
            if (x < low[0]) low[0] = x; if (x > high[0]) high[0] = x;
            if (y < low[1]) low[1] = y; if (y > high[1]) high[1] = y;
        }
    }
    else
    {
        for (i = 0; i < 4; i++)
        {
            if (t[i].x < low[0]) low[0] = (float)t[i].x; if (t[i].x > high[0]) high[0] = (float)t[i].x;
            if (t[i].y < low[1]) low[1] = (float)t[i].y; if (t[i].y > high[1]) high[1] = (float)t[i].y;
        }
    }
    if (low[0] < -4096.0f || low[1] < -4096.0f || high[0] > 8192.0f || high[1] > 8192.0f) goto untracked;
    SetRect(&box, (int)low[0] - 1, (int)low[1] - 1, (int)high[0] + 3, (int)high[1] + 3);
    SetRect(&bounds, 0, 0, local.dst.width, local.dst.height);
    if (!IntersectRect(&local.covered, &box, &bounds)) goto untracked;
    if (local.card < 0 && !touches_layer(dst_entry, &local.covered)) goto untracked;
    width = local.covered.right - local.covered.left;
    height = local.covered.bottom - local.covered.top;
    effect = (EFFECT*)malloc(sizeof(EFFECT));
    local.before = (WORD*)malloc((size_t)width * height * 2);
    if (!effect || !local.before) { free(effect); free(local.before); goto untracked; }
    snapshot(&local.dst, &local.covered, local.before);
    *effect = local;
    return effect;      /* the table stays locked until the matching end */
untracked:
    LeaveCriticalSection(&g_table_lock);
    return NULL;
}

/* A card was drawn turned, scaled or squeezed. Every target pixel is traced back into the card: pixels the
   routine changed inside the card's outline, and unchanged ones well inside it, belong to the card. The
   draw is believed only when what it left is the card: most pixels equal a source pixel next to the one
   they trace back to - or, when the card was mixed in, that pixel mixed with what was there before. */
static void quad_end(EFFECT* effect, const WORD* after, int width, int height)
{
    float det = effect->across[0] * effect->down[1] - effect->across[1] * effect->down[0];
    float len_a = (float)sqrt(effect->across[0] * effect->across[0] + effect->across[1] * effect->across[1]);
    float len_b = (float)sqrt(effect->down[0] * effect->down[0] + effect->down[1] * effect->down[1]);
    /* one target pixel, measured in source pixels, along each side */
    float step_x = len_a > 0.5f ? effect->span_x / len_a : 1.0f, step_y = len_b > 0.5f ? effect->span_y / len_b : 1.0f;
    BOOL mixed = effect->level >= 0;
    float keep = mixed ? (float)keep_by_design(0, effect->level) : 0.0f;
    float gain = mixed ? (((effect->level >> 4) & 15) + 0.5f) / 16.0f : 1.0f;
    int area = width * height, px, py, c, i, inside = 0, changed_inside = 0, changed_outside = 0, alike = 0, counted = 0;
    BYTE* valid = (BYTE*)calloc(1, (size_t)area);
    SURFACE* dst_entry;
    LAYER* layer;
    if (!valid) return;
    for (py = 0; py < height; py++)
    {
        for (px = 0; px < width; px++)
        {
            float dx = effect->covered.left + px - effect->origin[0], dy = effect->covered.top + py - effect->origin[1];
            float sx = (dx * effect->down[1] - dy * effect->down[0]) / det * effect->span_x;
            float sy = (effect->across[0] * dy - effect->across[1] * dx) / det * effect->span_y;
            WORD a = after[py * width + px], b = effect->before[py * width + px];
            BOOL changed = a != b;
            BOOL within = sx >= -0.5f - 0.5f * step_x && sx <= effect->span_x + 0.5f + 0.5f * step_x
                && sy >= -0.5f - 0.5f * step_y && sy <= effect->span_y + 0.5f + 0.5f * step_y;
            BOOL deep = sx >= -0.5f + 1.5f * step_x && sx <= effect->span_x + 0.5f - 1.5f * step_x
                && sy >= -0.5f + 1.5f * step_y && sy <= effect->span_y + 0.5f - 1.5f * step_y;
            if (!within) { changed_outside += changed; continue; }
            inside++;
            changed_inside += changed;
            /* Mixed in, an unchanged pixel is one the routine skipped: it shows what was there before. */
            if (!changed && (mixed || !deep)) continue;
            valid[py * width + px] = 1;
            if (deep && ((px ^ py) & 1) == 0)
            {
                /* Is this the card? Compare with the source pixels around the traced position. */
                int cx = (int)(sx + 0.5f), cy = (int)(sy + 0.5f), reach_x = (int)(step_x + 1.5f), reach_y = (int)(step_y + 1.5f), ix, iy;
                BOOL found = FALSE;
                float fa[3], fb[3];
                channels(a, fa); channels(b, fb);
                counted++;
                for (iy = cy - reach_y; iy <= cy + reach_y && !found; iy++)
                {
                    if (iy < 0 || iy > effect->span_y) continue;
                    for (ix = cx - reach_x; ix <= cx + reach_x && !found; ix++)
                    {
                        WORD s;
                        if (ix < 0 || ix > effect->span_x) continue;
                        s = pixel_row(&effect->src, effect->source.top + iy)[effect->source.left + ix];
                        if (!mixed) found = s == a;
                        else
                        {
                            float fs[3];
                            channels(s, fs);
                            found = TRUE;
                            for (c = 0; c < 3 && found; c++)
                            {
                                float predicted = fs[c] * gain + fb[c] * keep;
                                found = fa[c] - predicted <= g_tolerance[c] * 1.5f && predicted - fa[c] <= g_tolerance[c] * 1.5f;
                            }
                        }
                    }
                }
                alike += found;
            }
        }
    }
    if (g_pochd.debug >= 2)
        pochd_log("quad %s to %.0f,%.0f across %.0f,%.0f down %.0f,%.0f level %d: %d inside, %d of them changed, %d changed outside, "
            "%d of %d sampled are card pixels", pochd_card_name(effect->card), effect->origin[0], effect->origin[1],
            effect->across[0], effect->across[1], effect->down[0], effect->down[1], effect->level, inside, changed_inside,
            changed_outside, alike, counted);
    if (counted < 24 || alike * 10 < counted * (mixed ? 6 : 8) || changed_outside * 20 > inside) { free(valid); return; }
    dst_entry = find_surface(effect->dst.bits, TRUE);
    layer = dst_entry ? add_layer(dst_entry, &effect->covered) : NULL;
    if (!layer) { free(valid); return; }
    layer->card = effect->card;
    layer->orientation = effect->orientation;
    layer->uv[0] = (float)effect->source.left / effect->src.width;
    layer->uv[1] = (float)effect->source.top / effect->src.height;
    layer->uv[2] = (float)effect->source.right / effect->src.width;
    layer->uv[3] = (float)effect->source.bottom / effect->src.height;
    layer->turned = TRUE;
    for (i = 0; i < 4; i++)
    {
        /* the outer corners of the source rectangle: half a source pixel beyond the corner pixels' centres;
           a pixel's centre lies half a pixel inside it */
        float a = (i & 1) ? 1.0f + 0.5f / effect->span_x : -0.5f / effect->span_x;
        float b = (i & 2) ? 1.0f + 0.5f / effect->span_y : -0.5f / effect->span_y;
        layer->corner[i][0] = effect->origin[0] + 0.5f + a * effect->across[0] + b * effect->down[0];
        layer->corner[i][1] = effect->origin[1] + 0.5f + a * effect->across[1] + b * effect->down[1];
    }
    memcpy(layer->snap, after, (size_t)area * 2);
    memcpy(layer->valid, valid, (size_t)area);
    free(valid);
    if (mixed)
    {
        /* card * gain + what was there before * keep */
        layer->gain = gain;
        if (!need_mix(layer))
        {
            free_layer(layer);
            dst_entry->count--;
            return;
        }
        for (px = 0; px < area; px++)
        {
            float fb[3];
            channels(effect->before[px], fb);
            for (c = 0; c < 3; c++)
            {
                float value = fb[c] * keep * 255.0f / g_limit[c] + 0.5f;
                layer->mix[px * 4 + 2 - c] = (BYTE)(value > 255.0f ? 255.0f : value);
            }
        }
    }
}

void pochd_track_effect_end(void* draw)
{
    EFFECT* effect = (EFFECT*)draw;
    int width = effect->covered.right - effect->covered.left, height = effect->covered.bottom - effect->covered.top;
    int area = width * height, step = 1, px, py, c, samples = 0, same = 0, inliers = 0;
    BOOL scaled = effect->target_w != effect->source_w || effect->target_h != effect->source_h;
    double sbb = 0, sbc = 0, scc = 0, sab = 0, sac = 0, determinant, gain, keep;
    WORD* after = (WORD*)malloc((size_t)area * 3);
    BYTE* valid = after ? (BYTE*)(after + area) : NULL;
    SURFACE* dst_entry;
    LAYER* layer;
    if (!after) goto done;
    snapshot(&effect->dst, &effect->covered, after);
    if (effect->quad && effect->card >= 0)
    {
        quad_end(effect, after, width, height);
        goto done;
    }
    if (effect->card < 0)
    {
        overlay_end(effect, after, width, height);
        goto done;
    }
    while ((width / step) * (height / step) > 4000) step++;
    /* Measure the mix on a sample of pixels. */
    for (py = 0; py < height; py += step)
    {
        for (px = 0; px < width; px += step)
        {
            WORD b = effect->before[py * width + px], a = after[py * width + px], s = effect_source(effect, px, py);
            float fb[3], fa[3], fs[3];
            channels(b, fb); channels(a, fa); channels(s, fs);
            samples++;
            same += b == s;
            for (c = 0; c < 3; c++)
            {
                double vb = fb[c] / g_limit[c], va = fa[c] / g_limit[c], vs = fs[c] / g_limit[c];
                if (fa[c] >= g_limit[c]) continue;      /* possibly clipped at white */
                sbb += vb * vb; sbc += vb * vs; scc += vs * vs; sab += va * vb; sac += va * vs;
            }
        }
    }
    determinant = sbb * scc - sbc * sbc;
    if (scc < 1e-6) goto done;
    if (same * 5 >= samples * 4 || determinant < 1e-4 * sbb * scc + 1e-9)
    {
        /* The card was mixed over itself, or over nothing: one number describes it. */
        keep = 0;
        gain = sac / scc;
    }
    else
    {
        keep = (sab * scc - sac * sbc) / determinant;
        gain = (sac * sbb - sab * sbc) / determinant;
    }
    if (keep < 0.03) keep = 0;
    if (keep > 1.0) keep = 1.0;
    if (gain < 0.03 || gain > 4.0) goto done;
    /* Which pixels does that mix explain? */
    for (py = 0; py < height; py++)
    {
        for (px = 0; px < width; px++)
        {
            WORD b = effect->before[py * width + px], a = after[py * width + px], s = effect_source(effect, px, py);
            float fb[3], fa[3], fs[3];
            BOOL fits = TRUE;
            channels(b, fb); channels(a, fa); channels(s, fs);
            for (c = 0; c < 3 && fits; c++)
            {
                float predicted = (float)(fs[c] * gain + fb[c] * keep);
                if (predicted > g_limit[c]) predicted = g_limit[c];
                fits = fa[c] - predicted <= g_tolerance[c] && predicted - fa[c] <= g_tolerance[c];
            }
            valid[py * width + px] = (BYTE)fits;
            inliers += fits;
        }
    }
    if (g_pochd.debug >= 2)
        pochd_log("mix %s at %ld,%ld %dx%d%s: gain %.3f keep %.3f, explains %d of %d", pochd_card_name(effect->card),
            effect->covered.left, effect->covered.top, width, height, scaled ? " scaled" : "", gain, keep, inliers, area);
    if (inliers * 10 < area * (scaled ? 5 : 7)) goto done;
    /* Specks of a card (an effect that scatters it in pieces) stay as drawn: hundreds of them would crowd
       the whole cards out of the frame's layers. */
    if (area < 96) goto done;
    /* A scaled draw is trusted as a whole: single pixels differ only by which source pixel was picked. */
    if (scaled || inliers * 10 >= area * 9) memset(valid, 1, (size_t)area);
    dst_entry = find_surface(effect->dst.bits, TRUE);
    if (!dst_entry) goto done;
    if (keep == 0) drop_covered(dst_entry, &effect->covered);
    layer = add_layer(dst_entry, &effect->covered);
    if (!layer) goto done;
    layer->card = effect->card;
    layer->orientation = effect->orientation;
    layer->uv[0] = (effect->source.left + (effect->covered.left - effect->target.left) * (float)effect->source_w / effect->target_w) / effect->src.width;
    layer->uv[1] = (effect->source.top + (effect->covered.top - effect->target.top) * (float)effect->source_h / effect->target_h) / effect->src.height;
    layer->uv[2] = (effect->source.left + (effect->covered.right - effect->target.left) * (float)effect->source_w / effect->target_w) / effect->src.width;
    layer->uv[3] = (effect->source.top + (effect->covered.bottom - effect->target.top) * (float)effect->source_h / effect->target_h) / effect->src.height;
    memcpy(layer->snap, after, (size_t)area * 2);
    memcpy(layer->valid, valid, (size_t)area);
    layer->gain = (float)gain;
    if (keep != 0)
    {
        /* What was underneath stays in the picture: card * gain + before * keep. */
        if (!need_mix(layer))
        {
            free_layer(layer);
            dst_entry->count--;
            goto done;
        }
        for (px = 0; px < area; px++)
        {
            float fb[3];
            channels(effect->before[px], fb);
            for (c = 0; c < 3; c++)
            {
                float value = fb[c] * (float)keep * 255.0f / g_limit[c] + 0.5f;
                layer->mix[px * 4 + 2 - c] = (BYTE)(value > 255.0f ? 255.0f : value);
            }
        }
    }
done:
    free(after);
    free(effect->before);
    free(effect);
    LeaveCriticalSection(&g_table_lock);
}

void pochd_track_copy(void* dst_object, void* src_object, int x, int y, const RECT* src_rect, const SIZE* dst_size,
    const RECT* clip)
{
    EnterCriticalSection(&g_table_lock);
    track_copy(dst_object, src_object, x, y, src_rect, dst_size, clip);
    LeaveCriticalSection(&g_table_lock);
}

static void track_copy(void* dst_object, void* src_object, int x, int y, const RECT* src_rect, const SIZE* dst_size,
    const RECT* clip)
{
    IMAGE dst, src;
    SURFACE* src_entry = NULL;
    SURFACE* dst_entry;
    RECT source, target, bounds, covered, copied;
    int card, orientation = 0, source_w, source_h, target_w, target_h;
    BOOL scaled;
    if (!dst_object || !src_object || !read_image(dst_object, &dst) || !read_image(src_object, &src)) return;
    card = identify(&src, &src_entry, &orientation);
    if (!src_entry) src_entry = find_surface(src.bits, FALSE);
    dst_entry = find_surface(dst.bits, FALSE);
    if (card < 0 && !(src_entry && src_entry->count) && !(dst_entry && dst_entry->count)) return;

    __try
    {
        if (src_rect) source = *src_rect; else SetRect(&source, 0, 0, src.width, src.height);
        source_w = source.right - source.left;
        source_h = source.bottom - source.top;
        target_w = dst_size ? dst_size->cx : source_w;
        target_h = dst_size ? dst_size->cy : source_h;
        SetRect(&bounds, 0, 0, dst.width, dst.height);
        if (clip && !IntersectRect(&bounds, &bounds, clip)) return;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    if (source_w <= 0 || source_h <= 0 || target_w <= 0 || target_h <= 0 || source.left < 0 || source.top < 0
        || source.right > src.width || source.bottom > src.height) return;
    scaled = target_w != source_w || target_h != source_h;
    SetRect(&target, x, y, x + target_w, y + target_h);
    if (!IntersectRect(&covered, &target, &bounds)) return;

    if (dst_entry && dst_entry->count) drop_covered(dst_entry, &covered);
    if (card < 0 && !(src_entry && src_entry->count)) return;
    if (src.bits == dst.bits) return;
    if (!dst_entry) dst_entry = find_surface(dst.bits, TRUE);
    if (!dst_entry) return;
    /* The part of the source that landed inside the destination (unscaled copies). */
    SetRect(&copied, source.left + covered.left - target.left, source.top + covered.top - target.top,
        source.left + covered.right - target.left, source.top + covered.bottom - target.top);

    if (card >= 0)
    {
        LAYER* layer;
        float uv[4];
        int width = covered.right - covered.left, height = covered.bottom - covered.top;
        if (!scaled)
        {
            /* Prove the routine did what this code believes: sampled pixels must be an exact copy. */
            int k;
            for (k = 0; k < 9; k++)
            {
                int px = width * (k % 3 * 2 + 1) / 6, py = height * (k / 3 * 2 + 1) / 6;
                if (pixel_row(&dst, covered.top + py)[covered.left + px] != pixel_row(&src, copied.top + py)[copied.left + px])
                {
                    if (InterlockedIncrement(&g_plain_mismatch_logged) <= 20)
                        pochd_log("copy of %s to %d,%d is not a plain copy; left as drawn", pochd_card_name(card), x, y);
                    return;
                }
            }
        }
        uv[0] = (source.left + (covered.left - target.left) * (float)source_w / target_w) / src.width;
        uv[1] = (source.top + (covered.top - target.top) * (float)source_h / target_h) / src.height;
        uv[2] = (source.left + (covered.right - target.left) * (float)source_w / target_w) / src.width;
        uv[3] = (source.top + (covered.bottom - target.top) * (float)source_h / target_h) / src.height;
        layer = add_layer(dst_entry, &covered);
        if (!layer) return;
        layer->card = card;
        layer->orientation = orientation;
        memcpy(layer->uv, uv, sizeof(uv));
        snapshot(&dst, &covered, layer->snap);
        memset(layer->valid, 1, (size_t)width * height);
        merge_strips(dst_entry);
        if (g_pochd.debug >= 4)
            pochd_log("copy f%u %s to %ld,%ld %dx%d%s on %dx%d", g_frame->serial, pochd_card_name(card), covered.left, covered.top,
                width, height, scaled ? " scaled" : "", dst.width, dst.height);
        return;
    }

    if (!scaled)
    {
        /* Carry the source surface's layers along, keeping only pixels that still show the card there. */
        int i, count, dx = covered.left - copied.left, dy = covered.top - copied.top;
        refresh_layers(src_entry, &src);
        count = src_entry->count;
        for (i = 0; i < count; i++)
        {
            LAYER inherited = src_entry->layers[i];
            LAYER* layer;
            RECT part, placed;
            int width, height, px, py, matched = 0, was = 0, layer_w = inherited.dst.right - inherited.dst.left,
                layer_h = inherited.dst.bottom - inherited.dst.top;
            if (!IntersectRect(&part, &inherited.dst, &copied)) continue;
            width = part.right - part.left;
            height = part.bottom - part.top;
            SetRect(&placed, part.left + dx, part.top + dy, part.right + dx, part.bottom + dy);
            layer = add_layer(dst_entry, &placed);
            if (!layer) return;
            /* add_layer may have moved the array when source and destination share a table entry. */
            layer->card = inherited.card;
            layer->orientation = inherited.orientation;
            layer->gain = inherited.gain;
            layer->uv[0] = inherited.uv[0] + (inherited.uv[2] - inherited.uv[0]) * (part.left - inherited.dst.left) / layer_w;
            layer->uv[1] = inherited.uv[1] + (inherited.uv[3] - inherited.uv[1]) * (part.top - inherited.dst.top) / layer_h;
            layer->uv[2] = inherited.uv[0] + (inherited.uv[2] - inherited.uv[0]) * (part.right - inherited.dst.left) / layer_w;
            layer->uv[3] = inherited.uv[1] + (inherited.uv[3] - inherited.uv[1]) * (part.bottom - inherited.dst.top) / layer_h;
            if (inherited.turned)
            {
                /* A turned card keeps its outline; only the area holding its pixels was cut. */
                int c;
                layer->turned = TRUE;
                memcpy(layer->uv, inherited.uv, sizeof(layer->uv));
                for (c = 0; c < 4; c++)
                {
                    layer->corner[c][0] = inherited.corner[c][0] + dx;
                    layer->corner[c][1] = inherited.corner[c][1] + dy;
                }
            }
            for (py = 0; py < height; py++)
            {
                const WORD* now = pixel_row(&src, part.top + py) + part.left;
                int offset = (part.top + py - inherited.dst.top) * layer_w + (part.left - inherited.dst.left);
                const WORD* expected = inherited.snap + offset;
                const BYTE* was_valid = inherited.valid + offset;
                BYTE* valid = layer->valid + py * width;
                for (px = 0; px < width; px++)
                {
                    valid[px] = (BYTE)(was_valid[px] && now[px] == expected[px]);
                    matched += valid[px];
                    was += was_valid[px];
                }
            }
            /* Diagnostic: a card drawn on a hidden surface a moment ago of which part is no longer held when the
               surface is copied on. Something was drawn over it there, or it was changed in a way that was not
               followed; the loss check at presentation only sees the pixels that are still held. */
            if (g_pochd.debug >= 2 && was >= 400 && matched * 10 < was * 9 && g_frame->serial - inherited.born <= 150)
            {
                static LONG stale_logged;
                if (InterlockedIncrement(&stale_logged) <= 4000)
                    pochd_log("stale f%u %s at %ld,%ld %dx%d: %d of %d card pixels still held when copied on", g_frame->serial,
                        pochd_card_name(inherited.card), placed.left, placed.top, width, height, matched, was);
            }
            if (matched * 8 < width * height || (inherited.mix && !need_mix(layer)))
            {
                free_layer(layer);
                dst_entry->count--;
                continue;
            }
            for (py = 0; inherited.mix && py < height; py++)
                memcpy(layer->mix + (size_t)py * width * 4, inherited.mix + ((size_t)(part.top + py - inherited.dst.top) * layer_w
                    + (part.left - inherited.dst.left)) * 4, (size_t)width * 4);
            snapshot(&dst, &placed, layer->snap);
            merge_strips(dst_entry);
        }
    }
}

/* Picks, for every original channel value, the value it most often shows as now. Values the card never
   used take a neighbour's mapping. Whether the mapping means anything is decided by how many pixels it
   explains, not here. "reversed" gathers the votes behind neighbouring used values that came out in the
   wrong order: a dimming, a highlight or a fade never swaps two shades, a different picture does. */
static BOOL pick_mapping(const unsigned short* votes, int size, BYTE* out, int* reversed)
{
    int i, j, last, previous = -1, previous_votes = 0;
    for (i = 0; i < size; i++)
    {
        int best = -1, best_votes = 0;
        for (j = 0; j < size; j++)
            if (votes[i * size + j] > best_votes) { best_votes = votes[i * size + j]; best = j; }
        out[i] = (BYTE)(best < 0 ? 255 : best);
        if (best < 0) continue;
        if (previous >= 0 && best < previous - 1) *reversed += best_votes < previous_votes ? best_votes : previous_votes;
        previous = best;
        previous_votes = best_votes;
    }
    for (last = -1, i = 0; i < size; i++) { if (out[i] != 255) last = out[i]; else if (last >= 0) out[i] = (BYTE)last; }
    for (last = -1, i = size - 1; i >= 0; i--) { if (out[i] != 255) last = out[i]; else if (last >= 0) out[i] = (BYTE)last; }
    return out[0] != 255;
}

/* Looks for one colour mapping that explains the layer: every original channel value shows as one
   particular value now. A highlight, a dimmed screen or a fade to a colour all have this shape; a
   different picture drawn over the card does not. Returns the number of pixels the mapping explains.
   With changed_only the pixels that still hold what was drawn stay out of it, so that a card of which
   only a part changed (every other line of a wipe, the half under a shadow) can be asked about that part.
   "exact" counts the explained pixels that did not change at all, "reversed" the votes behind shades the
   mapping puts in the wrong order, over the three channels. */
static int infer_transfer(const LAYER* layer, const IMAGE* image, BOOL changed_only, BYTE* red, BYTE* green, BYTE* blue,
    int* exact, int* reversed)
{
    static unsigned short votes_r[32 * 32], votes_g[64 * 64], votes_b[32 * 32];
    int layer_w = layer->dst.right - layer->dst.left, layer_h = layer->dst.bottom - layer->dst.top, px, py, explained = 0;
    *exact = 0;
    *reversed = 0;
    memset(votes_r, 0, sizeof(votes_r));
    memset(votes_g, 0, sizeof(votes_g));
    memset(votes_b, 0, sizeof(votes_b));
    for (py = 0; py < layer_h; py++)
    {
        const WORD* now = pixel_row(image, layer->dst.top + py) + layer->dst.left;
        const WORD* expected = layer->snap + py * layer_w;
        const BYTE* valid = layer->valid + py * layer_w;
        for (px = 0; px < layer_w; px++)
        {
            WORD e = expected[px], n = now[px];
            if (!valid[px] || (changed_only && n == e)) continue;
            votes_r[(e >> 11) * 32 + (n >> 11)]++;
            votes_g[((e >> 5) & 63) * 64 + ((n >> 5) & 63)]++;
            votes_b[(e & 31) * 32 + (n & 31)]++;
        }
    }
    if (!pick_mapping(votes_r, 32, red, reversed) || !pick_mapping(votes_g, 64, green, reversed)
        || !pick_mapping(votes_b, 32, blue, reversed)) return 0;
    for (py = 0; py < layer_h; py++)
    {
        const WORD* now = pixel_row(image, layer->dst.top + py) + layer->dst.left;
        const WORD* expected = layer->snap + py * layer_w;
        const BYTE* valid = layer->valid + py * layer_w;
        for (px = 0; px < layer_w; px++)
        {
            WORD e = expected[px], n = now[px];
            if (!valid[px] || (changed_only && n == e)) continue;
            if (red[e >> 11] != (n >> 11) || green[(e >> 5) & 63] != ((n >> 5) & 63) || blue[e & 31] != (n & 31)) continue;
            explained++;
            *exact += n == e;
        }
    }
    return explained;
}

/* Hands pixels of a layer to one layer of the frame. Without a mapping: the pixels that still hold exactly
   what was drawn. With one: the pixels it explains, or with changed_only those of them that did change. */
static void claim(POCHD_FRAME* frame, const LAYER* layer, const BYTE* red, const BYTE* green, const BYTE* blue, BOOL changed_only)
{
    int layer_w = layer->dst.right - layer->dst.left, layer_h = layer->dst.bottom - layer->dst.top, px, py;
    POCHD_LAYER* out;
    BYTE id;
    if (frame->layer_count >= POCHD_MAX_LAYERS) return;
    out = &frame->layers[frame->layer_count++];
    id = (BYTE)frame->layer_count;
    out->card = layer->card;
    out->orientation = layer->orientation;
    out->dst[0] = (float)layer->dst.left; out->dst[1] = (float)layer->dst.top;
    out->dst[2] = (float)layer->dst.right; out->dst[3] = (float)layer->dst.bottom;
    memcpy(out->uv, layer->uv, sizeof(out->uv));
    for (px = 0; px < 4; px++)
    {
        out->corner[px][0] = layer->turned ? layer->corner[px][0] : (float)((px & 1) ? layer->dst.right : layer->dst.left);
        out->corner[px][1] = layer->turned ? layer->corner[px][1] : (float)((px & 2) ? layer->dst.bottom : layer->dst.top);
    }
    out->gain = layer->gain;
    out->mixed = (BYTE)(layer->mix != NULL);
    out->transfer = (BYTE)(red != NULL);
    if (red)
    {
        memcpy(out->red, red, sizeof(out->red));
        memcpy(out->green, green, sizeof(out->green));
        memcpy(out->blue, blue, sizeof(out->blue));
    }
    for (py = 0; py < layer_h; py++)
    {
        int row = (layer->dst.top + py) * POCHD_FRAME_W + layer->dst.left;
        const WORD* now = frame->pixels + row;
        const WORD* expected = layer->snap + py * layer_w;
        const BYTE* valid = layer->valid + py * layer_w;
        BYTE* owner = frame->owner + row;
        const DWORD* mixed = layer->mix ? (const DWORD*)layer->mix + py * layer_w : NULL;
        DWORD* mix = frame->mix + row;
        for (px = 0; px < layer_w; px++)
        {
            WORD e = expected[px], n = now[px];
            if (!valid[px]) continue;
            if (!red) { if (n != e) continue; }
            else if ((changed_only && n == e) || red[e >> 11] != (n >> 11) || green[(e >> 5) & 63] != ((n >> 5) & 63)
                || blue[e & 31] != (n & 31)) continue;
            owner[px] = id;
            /* A later plain layer shows nothing of what an earlier mixed one left here. */
            if (frame->has_mix) mix[px] = mixed ? mixed[px] : 0xFF000000u;
        }
    }
}

/* Cards on a surface that something this code does not follow recoloured as a whole: the games dim a
   hidden copy of the scene and then fade to it or copy from it. The layer takes the change in as
   card * part + extra, so that it holds its pixels again and can be carried on or mixed. The same proof
   is asked as at presentation, one consistent mapping for the card, and here also that the mapping keeps
   the order of the shades. */
static void refresh_layers(SURFACE* surface, const IMAGE* image)
{
    static LONG logged;
    int i;
    for (i = 0; i < surface->count; i++)
    {
        LAYER* layer = &surface->layers[i];
        int layer_w = layer->dst.right - layer->dst.left, layer_h = layer->dst.bottom - layer->dst.top, px, py, c;
        int matched = 0, usable = 0, explained, exact, reversed, taken = 0;
        double count = 0, se[3] = { 0 }, sn[3] = { 0 }, see[3] = { 0 }, sen[3] = { 0 }, keep = 1.0;
        BYTE red[32], green[64], blue[32];
        if (layer->checked == g_frame->serial + 1) continue;
        layer->checked = g_frame->serial + 1;
        if (layer->dst.right > image->width || layer->dst.bottom > image->height) continue;
        for (py = 0; py < layer_h; py++)
        {
            const WORD* now = pixel_row(image, layer->dst.top + py) + layer->dst.left;
            const WORD* expected = layer->snap + py * layer_w;
            const BYTE* valid = layer->valid + py * layer_w;
            for (px = 0; px < layer_w; px++)
            {
                usable += valid[px];
                matched += valid[px] && now[px] == expected[px];
            }
        }
        if (usable < 400 || matched * 10 >= usable * 9) continue;
        explained = infer_transfer(layer, image, FALSE, red, green, blue, &exact, &reversed);
        if (explained * 10 < usable * 6 || explained * 20 < matched * 20 + usable * 3 || (matched - exact) * 20 > usable
            || reversed * 20 > usable * 3)
        {
            /* Something else was drawn there. With next to nothing of the card left the record is dead, as it
               would be on the screen; it must not be compared again for every copy from this surface. */
            if (matched * 50 < layer_w * layer_h)
            {
                free_layer(layer);
                memmove(layer, layer + 1, (surface->count - i - 1) * sizeof(LAYER));
                surface->count--;
                i--;
            }
            continue;
        }
        /* How much of the card the mapping leaves: the slope of now against before, the least of the three colours. */
        for (py = 0; py < layer_h; py++)
        {
            const WORD* now = pixel_row(image, layer->dst.top + py) + layer->dst.left;
            const WORD* expected = layer->snap + py * layer_w;
            const BYTE* valid = layer->valid + py * layer_w;
            for (px = 0; px < layer_w; px++)
            {
                WORD e = expected[px], n = now[px];
                float fe[3], fn[3];
                if (!valid[px] || red[e >> 11] != (n >> 11) || green[(e >> 5) & 63] != ((n >> 5) & 63) || blue[e & 31] != (n & 31)) continue;
                channels(e, fe); channels(n, fn);
                count++;
                for (c = 0; c < 3; c++)
                {
                    double ve = fe[c] / g_limit[c], vn = fn[c] / g_limit[c];
                    se[c] += ve; sn[c] += vn; see[c] += ve * ve; sen[c] += ve * vn;
                }
            }
        }
        for (c = 0; c < 3; c++)
        {
            double spread = count * see[c] - se[c] * se[c];
            double slope = spread > 1e-3 * count * count ? (count * sen[c] - se[c] * sn[c]) / spread : 1.0;
            if (slope < keep) keep = slope;
        }
        if (keep < 0.04 || !need_mix(layer)) continue;
        for (py = 0; py < layer_h; py++)
        {
            const WORD* now = pixel_row(image, layer->dst.top + py) + layer->dst.left;
            WORD* expected = layer->snap + py * layer_w;
            const BYTE* valid = layer->valid + py * layer_w;
            for (px = 0; px < layer_w; px++)
            {
                WORD e = expected[px], n = now[px];
                float fe[3], fn[3], share = (float)keep;
                BYTE* m = layer->mix + (py * layer_w + px) * 4;
                if (!valid[px] || n == e) continue;
                if (red[e >> 11] != (n >> 11) || green[(e >> 5) & 63] != ((n >> 5) & 63) || blue[e & 31] != (n & 31)) continue;
                channels(e, fe); channels(n, fn);
                for (c = 0; c < 3; c++)
                    if (fe[c] * share > fn[c] + g_tolerance[c]) share = fn[c] / fe[c];
                if (share < 0.04f || m[3] * share < 8.0f) continue;
                for (c = 0; c < 3; c++)
                {
                    float extra = (fn[c] - fe[c] * share) * 255.0f / g_limit[c];
                    float value = m[2 - c] * share + (extra > 0 ? extra : 0) + 0.5f;
                    m[2 - c] = (BYTE)(value > 255.0f ? 255.0f : value);
                }
                m[3] = (BYTE)(m[3] * share + 0.5f);
                expected[px] = n;
                taken++;
            }
        }
        if (g_pochd.debug >= 2 && InterlockedIncrement(&logged) <= 2000)
            pochd_log("recoloured f%u %s at %ld,%ld %dx%d on a %dx%d surface: %d of %d held, %d more taken in at %.3f of the card",
                g_frame->serial, pochd_card_name(layer->card), layer->dst.left, layer->dst.top, layer_w, layer_h, image->width,
                image->height, matched, usable, taken, keep);
    }
}

/* Diagnostic (debug=2): a card drawn for this very frame of which part is shown from no HD picture, its own
   or that of a card lying over it. The cursor, text, counters and opaque panels over a card belong here
   too; debug=3 also saves what the game drew. */
static void note_loss(const POCHD_FRAME* frame, const LAYER* layer, int remaining, int usable)
{
    static LONG logged, saved;
    static unsigned recent[64];
    int layer_w = layer->dst.right - layer->dst.left, layer_h = layer->dst.bottom - layer->dst.top;
    unsigned slot = ((unsigned)layer->dst.left * 31u + (unsigned)layer->dst.top) & 63u;
    if (InterlockedIncrement(&logged) <= 20000)
        pochd_log("loss f%u %s at %ld,%ld %dx%d: %d of %d shown", frame->serial, pochd_card_name(layer->card), layer->dst.left,
            layer->dst.top, layer_w, layer_h, remaining, usable);
    if (g_pochd.debug >= 3 && frame->serial - recent[slot] >= 10 && saved < 240)
    {
        wchar_t path[MAX_PATH];
        RECT crop;
        WORD* picture;
        int x, y;
        SetRect(&crop, layer->dst.left - 12, layer->dst.top - 12, layer->dst.right + 12, layer->dst.bottom + 12);
        if (crop.left < 0) crop.left = 0;
        if (crop.top < 0) crop.top = 0;
        if (crop.right > POCHD_FRAME_W) crop.right = POCHD_FRAME_W;
        if (crop.bottom > POCHD_FRAME_H) crop.bottom = POCHD_FRAME_H;
        recent[slot] = frame->serial;
        saved++;
        _snwprintf(path, MAX_PATH - 1, L"%sloss-%06u-%ld-%ld.bmp", g_pochd.directory, frame->serial, layer->dst.left, layer->dst.top);
        path[MAX_PATH - 1] = 0;
        /* what the game drew, with everything that is shown from an HD picture tinted green */
        picture = (WORD*)malloc((size_t)(crop.right - crop.left) * (crop.bottom - crop.top) * 2);
        if (!picture) return;
        for (y = crop.top; y < crop.bottom; y++)
        {
            for (x = crop.left; x < crop.right; x++)
            {
                WORD v = frame->pixels[y * POCHD_FRAME_W + x];
                if (frame->owner[y * POCHD_FRAME_W + x]) v = (WORD)(((v >> 1) & 0x7BEF) + 0x03E0);
                picture[(y - crop.top) * (crop.right - crop.left) + (x - crop.left)] = v;
            }
        }
        save_bmp(path, picture, (crop.right - crop.left) * 2, crop.right - crop.left, crop.bottom - crop.top);
        free(picture);
    }
}

static void check_loss(const POCHD_FRAME* frame, const LAYER* layer)
{
    int layer_w = layer->dst.right - layer->dst.left, layer_h = layer->dst.bottom - layer->dst.top, px, py, usable = 0, shown = 0;
    for (py = 0; py < layer_h; py++)
    {
        const BYTE* owner = frame->owner + (layer->dst.top + py) * POCHD_FRAME_W + layer->dst.left;
        const BYTE* valid = layer->valid + py * layer_w;
        for (px = 0; px < layer_w; px++)
        {
            usable += valid[px];
            shown += valid[px] && owner[px];
        }
    }
    if (usable >= 400 && shown * 10 < usable * 9) note_loss(frame, layer, shown, usable);
}

void pochd_track_present(const WORD* canvas, int pitch_bytes, int width, int height)
{
    static LAYER gone[96];
    int gone_count = 0;
    static LONGLONG spent;
    static int presented;
    LARGE_INTEGER started, finished, frequency;
    SURFACE* surface;
    POCHD_FRAME* frame = g_frame;
    IMAGE shown;
    int y, i, kept = 0, first = 0;
    if (!frame || width != POCHD_FRAME_W || height != POCHD_FRAME_H) return;
    shown.bits = frame->pixels;
    shown.pitch = POCHD_FRAME_W * 2;
    shown.width = POCHD_FRAME_W;
    shown.height = POCHD_FRAME_H;
    QueryPerformanceCounter(&started);
    EnterCriticalSection(&g_table_lock);
    surface = find_surface(canvas, FALSE);
    EnterCriticalSection(&g_frame_lock);
    for (y = 0; y < POCHD_FRAME_H; y++)
        memcpy(frame->pixels + y * POCHD_FRAME_W, (const BYTE*)canvas + y * pitch_bytes, POCHD_FRAME_W * 2);
    memset(frame->owner, 0, sizeof(frame->owner));
    frame->layer_count = 0;
    frame->has_mix = 0;
    if (surface && g_pochd.enabled)
    {
        for (i = 0; i < surface->count && !frame->has_mix; i++) frame->has_mix = surface->layers[i].mix != NULL;
        for (i = 0; frame->has_mix && i < POCHD_FRAME_W * POCHD_FRAME_H; i++) frame->mix[i] = 0xFF000000u;
        /* More copies than one frame can name: the most recent ones are the ones on top. */
        if (surface->count > POCHD_MAX_LAYERS) first = surface->count - POCHD_MAX_LAYERS;
        for (i = 0; i < first; i++) free_layer(&surface->layers[i]);
        for (i = first; i < surface->count; i++)
        {
            LAYER* layer = &surface->layers[i];
            int layer_w = layer->dst.right - layer->dst.left, layer_h = layer->dst.bottom - layer->dst.top, px, py;
            int matched = 0, usable = 0, explained = 0, remaining;
            BYTE red[32], green[64], blue[32];
            BOOL transfer = FALSE, partly = FALSE;
            /* First count what is left of the card on screen. */
            for (py = 0; py < layer_h; py++)
            {
                const WORD* now = frame->pixels + (layer->dst.top + py) * POCHD_FRAME_W + layer->dst.left;
                const WORD* expected = layer->snap + py * layer_w;
                const BYTE* valid = layer->valid + py * layer_w;
                for (px = 0; px < layer_w; px++)
                {
                    usable += valid[px];
                    matched += valid[px] && now[px] == expected[px];
                }
            }
            /* Mostly changed: perhaps the game recoloured the whole card instead of covering it. */
            if (matched * 10 < usable * 9 && usable >= 400)
            {
                int exact, reversed, changed = usable - matched;
                explained = infer_transfer(layer, &shown, FALSE, red, green, blue, &exact, &reversed);
                /* One mapping for the whole card has to hold for the pixels that did not change as well.
                   Where it does not, two things were voted into one table: untouched pixels and changed
                   ones, as when a wipe blacks out every other line. */
                transfer = explained * 10 >= usable * 6 && explained * 20 >= matched * 20 + usable * 3
                    && (matched - exact) * 20 <= usable;
                if (!transfer && surface->count - i + frame->layer_count < POCHD_MAX_LAYERS)
                {
                    /* Part of the card untouched, the rest changed in one consistent way: the untouched
                       pixels stay with the card as drawn, the others get the mapping as a layer of their own. */
                    int part = infer_transfer(layer, &shown, TRUE, red, green, blue, &exact, &reversed);
                    partly = part * 20 >= changed * 19 && reversed * 20 <= changed * 3;
                    if (partly) explained = part;
                }
                if (g_pochd.debug > 1 && (frame->serial % 30) == 0)
                    pochd_log("changed %s at %ld,%ld: %d of %d exact, %d explained by a recolouring%s", pochd_card_name(layer->card),
                        layer->dst.left, layer->dst.top, matched, usable, explained,
                        transfer ? " of the card" : partly ? " of the changed part" : ", not used");
            }
            /* A copy made for this very frame counts with whatever is left of it. An older one stays only
               while most of it is still on screen: the game draws its cards anew for each frame, so an old
               record with little left is a card that has moved on, not one that is partly covered. */
            remaining = transfer ? explained : partly ? matched + explained : matched;
            if (remaining * 50 < layer_w * layer_h || (layer->born != frame->serial && remaining * 2 < usable))
            {
                /* The diagnostics look at a card drawn for this frame once every layer has claimed its pixels. */
                if (g_pochd.debug >= 4 && layer->born == frame->serial)
                    pochd_log("gone f%u %s at %ld,%ld %dx%d: %d exact, %d explained of %d", frame->serial, pochd_card_name(layer->card),
                        layer->dst.left, layer->dst.top, layer_w, layer_h, matched, explained, usable);
                if (g_pochd.debug >= 2 && layer->born == frame->serial && gone_count < 96) gone[gone_count++] = *layer;
                else free_layer(layer);
                continue;
            }
            if (transfer) claim(frame, layer, red, green, blue, FALSE);
            else
            {
                claim(frame, layer, NULL, NULL, NULL, FALSE);
                if (partly) claim(frame, layer, red, green, blue, TRUE);
            }
            surface->layers[kept++] = *layer;
        }
        surface->count = kept;
        if (g_pochd.debug >= 2)
        {
            for (i = 0; i < kept; i++)
                if (surface->layers[i].born == frame->serial) check_loss(frame, &surface->layers[i]);
            for (i = 0; i < gone_count; i++)
            {
                check_loss(frame, &gone[i]);
                free_layer(&gone[i]);
            }
        }
    }
    frame->serial++;
    LeaveCriticalSection(&g_frame_lock);
    LeaveCriticalSection(&g_table_lock);
    QueryPerformanceCounter(&finished);
    spent += finished.QuadPart - started.QuadPart;
    if (++presented == 600)
    {
        QueryPerformanceFrequency(&frequency);
        POCHD_DEBUG("tracking: %.3f ms per frame at present, %d layers, %d surfaces", spent * 1000.0 / frequency.QuadPart / presented,
            frame->layer_count, g_used);
        spent = 0;
        presented = 0;
    }
}

BOOL pochd_track_acquire(POCHD_FRAME* frame, unsigned* serial)
{
    BOOL fresh = FALSE;
    if (!g_frame) return FALSE;
    EnterCriticalSection(&g_frame_lock);
    if (g_frame->serial != *serial)
    {
        memcpy(frame, g_frame, sizeof(POCHD_FRAME));
        *serial = g_frame->serial;
        fresh = TRUE;
    }
    LeaveCriticalSection(&g_frame_lock);
    return fresh;
}

void pochd_track_dump(void)
{
    int i;
    if (!g_frame) return;
    EnterCriticalSection(&g_frame_lock);
    pochd_log("frame %u: %d layers, %d tracked surfaces", g_frame->serial, g_frame->layer_count, g_used);
    for (i = 0; i < g_frame->layer_count; i++)
    {
        const POCHD_LAYER* layer = &g_frame->layers[i];
        pochd_log("  %3d %-10s o%d dst=%.0f,%.0f,%.0f,%.0f uv=%.3f,%.3f,%.3f,%.3f%s", i + 1, pochd_card_name(layer->card),
            layer->orientation, layer->dst[0], layer->dst[1], layer->dst[2], layer->dst[3], layer->uv[0], layer->uv[1],
            layer->uv[2], layer->uv[3], layer->transfer ? " recoloured" : "");
        if (layer->corner[0][0] != layer->dst[0] || layer->corner[3][1] != layer->dst[3])
            pochd_log("      turned: corners %.1f,%.1f %.1f,%.1f %.1f,%.1f %.1f,%.1f", layer->corner[0][0], layer->corner[0][1],
                layer->corner[1][0], layer->corner[1][1], layer->corner[2][0], layer->corner[2][1], layer->corner[3][0],
                layer->corner[3][1]);
        if (layer->gain != 1.0f || layer->mixed)
            pochd_log("      mixed: gain %.3f%s", layer->gain, layer->mixed ? ", part and extra per pixel" : "");
        if (layer->transfer)
            pochd_log("      red %d..%d green %d..%d blue %d..%d", layer->red[0], layer->red[31], layer->green[0],
                layer->green[63], layer->blue[0], layer->blue[31]);
    }
    LeaveCriticalSection(&g_frame_lock);
}
