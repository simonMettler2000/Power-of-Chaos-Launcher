#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Independent single-render-thread context. Create, configure, present,
   shutdown and destroy on that same thread. No HDC or source pointer retained.
   Caller must keep the source allocation, pointer and readable extent valid
   during copying. Pixel bytes may be changing concurrently: capture is a rowwise
   copy, not an atomic original-frame acquisition. It owns the copied bytes
   before any file decode or DC draw. No other thread may touch this context. */
typedef struct HdvGdi HdvGdi;
HdvGdi* hdvg_create(void);
/* Same strict POC_HD_VISIBLE_V1 grammar as the D3D9 adapter. RGB565 only.
   A failed configuration disables the previous configuration and clears cache.
   Null explicitly disables. Configuration never paints or opens a window. */
int hdvg_configure(HdvGdi*, const wchar_t* config_path);
void hdvg_shutdown(HdvGdi*);
void hdvg_destroy(HdvGdi*);
/* dc is the current destination HDC; vx/vy/vw/vh are physical client pixels.
   Requires untransformed MM_TEXT DC and positive 4:3 viewport (rounding <=1px),
   exact top-down 800x600 RGB565, pitch 1600..65536, eligible!=0.
   Return 0: unsupported or failed; caller executes its original fallback,
   including repaint after any partial failed operation. Return 1: owned base
   painted (scaled or successful native-size fallback).
   Return 2: base and uniquely proven HD overlay painted. Return 3: known empty
   caller clip, no drawing required. Unknown zero/error draw returns fail closed;
   failed overlay restores the scaled original base, or returns 0 if restoration fails.
   After any potentially partial visible paint, native-size0,0 is never counted
   as a scaled repair. The native fallback remains available before painting. Missing/corrupt masters,
   no match and ambiguity paint the owned original base. Existing clip is honored;
   all DC state is saved/restored. No renderer global or device cache is shared. */
int hdvg_present(HdvGdi*, void* dc, int vx, int vy, int vw, int vh,
    const void* source, uint32_t width, uint32_t height, uint32_t pitch,
    uint32_t format, int eligible);
#ifdef __cplusplus
}
#endif
