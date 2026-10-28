#pragma once
#include <stdint.h>
/* Authored test-process protocol. Contains geometry only; no observed pixels. */
struct HdvGdiOracleRequest {
    uint32_t magic, version, width, height, format;
    int32_t vx, vy, vw, vh, rx, ry;
    uint32_t overlay, native_base, black_only, backend_base;
    uint64_t owner_window;
};
