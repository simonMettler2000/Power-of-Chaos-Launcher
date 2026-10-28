#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Explicit synthetic-test-build-only exports. No game/component installs use
   these. Caller receives a copy, never a provider-owned mutable buffer. */
typedef struct HdvTestResult {
    uint64_t sequence;
    uint32_t width, height, byte_count, replaced;
} HdvTestResult;
void hdv_test_observe(void* device, int replaced);
/* Synthetic-only real persistent window-HDC readback after the GDI renderer
   completed base/overlay. Same copied-result ABI as the D3D9 observer. */
void hdv_test_observe_gdi(void* dc, uint32_t width, uint32_t height, int replaced);
/* New synthetic-only metadata/control ABI; HdvTestResult/read stays unchanged.
   Controls affect only the synthetic GDI hook and are scoped to one locked
   render branch. They never exist in the production provider. */
enum { HDV_TEST_GDI_FORCE_BNET = 1 };
enum { HDV_TEST_GDI_VHACK = 1, HDV_TEST_GDI_CHILD = 2, HDV_TEST_GDI_BNET = 4 };
typedef struct HdvGdiTestState {
    uint64_t sequence, render_instance, starts, stops;
    int32_t vx, vy, vw, vh, backend_result;
    uint32_t guard_flags;
} HdvGdiTestState;
uint64_t hdv_test_gdi_begin(void);
void hdv_test_gdi_end(uint64_t instance);
uint32_t hdv_test_gdi_controls(void);
int hdv_test_gdi_control(uint32_t flags);
int hdv_test_gdi_read_state(HdvGdiTestState* state);
void hdv_test_observe_gdi_frame(void* dc, uint32_t width, uint32_t height,
    int backend_result, uint64_t instance, int vx, int vy, int vw, int vh, uint32_t flags);
int hdv_test_read(void* destination, uint32_t capacity, HdvTestResult* result);
#ifdef __cplusplus
}
#endif
