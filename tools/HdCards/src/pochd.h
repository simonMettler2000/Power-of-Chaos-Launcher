/* HD card artwork for the Power of Chaos games: shared declarations of the authored provider units. */
#ifndef POCHD_H
#define POCHD_H

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ---- provider call-outs (see patch_provider.py) ---- */
#define POCHD_DD_BLT 1
#define POCHD_DD_BLTFAST 2
#define POCHD_DD_FLIP 3
#define POCHD_DD_LOCK 4
#define POCHD_DD_UNLOCK 5
#define POCHD_DD_GETDC 6
#define POCHD_DD_RELEASEDC 7

struct IDirectDrawSurfaceImpl;

void pochd_attach(void);
DWORD WINAPI pochd_render_main(void);
void pochd_dd_event(int kind, struct IDirectDrawSurfaceImpl* dst, struct IDirectDrawSurfaceImpl* src,
    const RECT* dst_rect, const RECT* src_rect, DWORD flags);

/* ---- configuration and diagnostics (pochd_main.c) ---- */
#define POCHD_FRAME_W 800
#define POCHD_FRAME_H 600
#define POCHD_MAX_LAYERS 255

typedef struct POCHD_CONFIG
{
    wchar_t directory[MAX_PATH];   /* directory of this module, with trailing backslash */
    wchar_t images[MAX_PATH];      /* HD image folder, with trailing backslash */
    wchar_t archive[MAX_PATH];     /* game archive (data.dat) */
    int debug;                     /* command file and verbose log */
    int filter;                    /* base picture: 0 nearest, 1 linear, 2 sharp bilinear */
    int texture_megabytes;         /* HD texture budget */
    int enabled;                   /* HD substitution on/off (diagnostic toggle) */
    int tint;                      /* diagnostic: tint substituted pixels */
} POCHD_CONFIG;

extern POCHD_CONFIG g_pochd;

void pochd_log(const char* format, ...);
#define POCHD_DEBUG(...) do { if (g_pochd.debug) pochd_log(__VA_ARGS__); } while (0)

/* ---- card database (pochd_cards.c) ---- */
#define POCHD_ORIENT_0 0     /* upright */
#define POCHD_ORIENT_90 1    /* card top points right */
#define POCHD_ORIENT_180 2   /* upside down */
#define POCHD_ORIENT_270 3   /* card top points left */

BOOL pochd_cards_start(void);                 /* begins the background index build */
BOOL pochd_cards_ready(void);
int pochd_cards_count(void);
/* Identifies an RGB565 image as one of the game's own card bitmaps. Returns the card index or -1. */
int pochd_cards_identify(const WORD* bits, int pitch_bytes, int width, int height, int* orientation);
const wchar_t* pochd_card_image(int card);   /* full path of the HD picture, or NULL when there is none */
const char* pochd_card_name(int card);

/* ---- draw tracking (pochd_track.c) ---- */
typedef struct POCHD_LAYER
{
    int card;
    int orientation;
    float dst[4];   /* left, top, right, bottom in frame pixels: the area that holds the layer's pixels */
    float uv[4];    /* the part of the oriented source image that is shown, 0..1 */
    float corner[4][2];   /* where the corners of that part lie in the frame: top-left, top-right, bottom-left,
                             bottom-right of the source image. A plain copy fills dst; a turned card does not. */
    /* When the game recoloured the whole card after drawing it (highlight, dimming, fade to a colour),
       these tables give the colour the game shows for each original 5/6/5-bit channel value. */
    BYTE transfer;
    BYTE red[32], green[64], blue[32];
    /* How the game drew the card: shown = card * gain * part + extra. A plain copy is gain 1 with all of
       the card and nothing extra. A fade, a dimmed card, a glow, or an effect laid over the card has a
       gain, and when mixed is set a part and an extra for every pixel in the frame's mix plane. */
    float gain;
    BYTE mixed;
} POCHD_LAYER;

typedef struct POCHD_FRAME
{
    unsigned serial;
    int layer_count;
    int has_mix;                                 /* some layer uses the mix plane */
    POCHD_LAYER layers[POCHD_MAX_LAYERS];
    WORD pixels[POCHD_FRAME_W * POCHD_FRAME_H];
    BYTE owner[POCHD_FRAME_W * POCHD_FRAME_H];   /* 0 = base picture, n = layers[n - 1] */
    DWORD mix[POCHD_FRAME_W * POCHD_FRAME_H];    /* per pixel: extra colour (B, G, R) and the part of the card shown (A) */
} POCHD_FRAME;

BOOL pochd_track_init(void);
/* Called after the game's plain copy routine ran: dst <- src at (x, y). */
void pochd_track_copy(void* dst_object, void* src_object, int x, int y, const RECT* src_rect, const SIZE* dst_size,
    const RECT* clip);
/* The game's mixing draws (fade, dim, glow, effects laid over a card): observed before and after the
   routine runs. Begin returns NULL when the draw neither shows a recognised card nor touches one.
   routine: 0 blend, 1 shade, 2 add, 3 a picture with its own transparency, 4 the same at a strength;
   level: the routine's strength argument (a byte). */
void* pochd_track_effect_begin(void* dst_object, void* src_object, int x, int y, const RECT* src_rect, const SIZE* dst_size,
    const RECT* clip, int routine, int level);
/* The games' quad routines (a picture drawn turned, scaled or squeezed onto four target points).
   level < 0: drawn as it is; 0..255: mixed in at that strength. Begin returns NULL unless a recognised
   card is drawn or a mixed picture touches one; the draw is finished with pochd_track_effect_end. */
void* pochd_track_quad_begin(void* dst_object, void* src_object, const POINT* source_points, const POINT* target_points,
    int count, int level);
void pochd_track_effect_end(void* draw);
/* Called when the game presents its canvas. */
void pochd_track_present(const WORD* canvas, int pitch_bytes, int width, int height);
/* Copies the most recent sealed frame when it is newer than *serial. */
BOOL pochd_track_acquire(POCHD_FRAME* frame, unsigned* serial);
void pochd_track_dump(void);

/* ---- game profiles (pochd_game.c) ---- */
BOOL pochd_game_install(void);

/* ---- renderer diagnostics (pochd_render.cpp) ---- */
void pochd_render_request_capture(const wchar_t* path);
/* Saves the next presented pictures as <prefix>NN.bmp (diagnostic, for looking at animations). */
void pochd_render_request_burst(const wchar_t* prefix, int count);

#ifdef __cplusplus
}
#endif

#endif
