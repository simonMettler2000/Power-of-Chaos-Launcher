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
int hdv_test_read(void* destination, uint32_t capacity, HdvTestResult* result);
#ifdef __cplusplus
}
#endif
