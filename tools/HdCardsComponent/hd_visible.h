#pragma once
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

/* Project-authored visible-pixel recognizer. All inputs are copied. One render
   thread owns an instance; no game memory, handles, IDs or callbacks are read.
   Only explicitly supported preview regions are tested. A match requires one
   unique complete reference/placement pair; HdvMatch reports that placement. */
typedef struct HdvEngine HdvEngine;
enum HdvGame { HDV_YUGI = 1, HDV_KAIBA = 2, HDV_JOEY = 3 };
enum HdvFormat { HDV_RGB565 = 1, HDV_RGB555 = 2 };
typedef struct HdvFrame {
    const void* pixels;
    uint32_t width, height, pitch, format;
    uint64_t generation;
} HdvFrame;
typedef struct HdvMatch {
    uint32_t key, x, y, width, height;
    uint64_t generation;
    const wchar_t* master_path;
} HdvMatch;

HdvEngine* hdv_create(uint32_t game, uint32_t format);
void hdv_destroy(HdvEngine* engine);
int hdv_add(HdvEngine* engine, uint32_t key, const void* packed_reference,
    size_t reference_bytes, const wchar_t* master_png_absolute_path);
/* Optional GDI-only normal foreground bank. Exactly two owned64x64 BGRA
   sprites must be installed before sealing. RGB565 admission uses alpha>>4. */
int hdv_add_cursor(HdvEngine* engine, uint32_t state, const void* bgra, size_t bytes);
/* Optional V3 Deck flip profile. Inputs are copied before seal. The seven
   explicit observable poses are supported hypotheses, not a phase clock. */
typedef struct HdvFlipPose { uint32_t width, x, q; } HdvFlipPose;
int hdv_add_flip_background(HdvEngine*, const void* rgb565, size_t bytes);
int hdv_enable_flip(HdvEngine*, const HdvFlipPose* poses, size_t count);
typedef struct HdvFlipPlan {
    uint32_t key, x, y, width, height, face_x, face_width, q;
    uint64_t generation;
    const wchar_t* master_path;
    const void* background565;
} HdvFlipPlan;
int hdv_flip(const HdvEngine*, HdvFlipPlan*);
typedef struct HdvCursor {
    const void* bgra;
    int32_t x, y;
    uint32_t state;
    uint64_t generation;
} HdvCursor;
int hdv_cursor(const HdvEngine* engine, HdvCursor* cursor);
/* Successful repeats return1 without allocation/mutation and preserve capture
   pointers/generation. A failed seal is terminal for that engine instance. */
int hdv_seal(HdvEngine* engine);
void hdv_invalidate(HdvEngine* engine);
/* Capture must receive the exact locked GPU upload storage after base copy,
   before UnlockRect, with no intervening write. Owns a tightly packed copy. */
int hdv_capture(HdvEngine* engine, const void* uploaded_pixels, uint32_t width,
    uint32_t height, uint32_t pitch, uint32_t format);
int hdv_snapshot(const HdvEngine* engine, HdvFrame* frame);
int hdv_match(const HdvEngine* engine, HdvMatch* match);

/* Optional Windows/D3D9 adapter. Config explicitly supplies local files.
   File grammar is UTF-8, tab-separated: first line POC_HD_VISIBLE_V1,
   second line game (1..3) TAB format (1..2), then key TAB reference TAB PNG.
   All file paths must be absolute. No inferred card/resource mapping. */
int hdv_provider_configure(const wchar_t* config_path);
void hdv_provider_shutdown(void);
void hdv_provider_invalidate(void);
/* Owner-thread only: bind actual device identity before capture. Identity
   changes clear decoded cache and invalidate captured state. */
int hdv_provider_bind_device(void* device);
void hdv_provider_capture(const void* pixels, uint32_t width, uint32_t height,
    uint32_t pitch, uint32_t format);
/* Device is IDirect3DDevice9*. Call inside the provider's scene, after its
   base draw, before EndScene/Present. Unsupported transforms must be eligible=0. */
int hdv_provider_draw(void* device, int vx, int vy, int vw, int vh, int eligible);
#ifdef __cplusplus
}
#endif
