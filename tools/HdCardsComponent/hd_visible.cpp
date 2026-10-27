#include "hd_visible.h"
#include <algorithm>
#include <cstring>
#include <cwchar>
#include <new>
#include <memory>
#include <string>
#include <vector>

namespace {
constexpr uint32_t Width = 800, Height = 600, CardWidth = 200, CardHeight = 290;
constexpr size_t ReferenceBytes = size_t(CardWidth) * CardHeight * 2;
constexpr size_t MaximumEntries = 1024;
uint64_t hash_bytes(const unsigned char* bytes, size_t count) {
    uint64_t value = UINT64_C(14695981039346656037);
    for (size_t i = 0; i < count; ++i) {
        value ^= bytes[i]; value *= UINT64_C(1099511628211);
    }
    return value;
}
bool absolute_path(const wchar_t* path) {
    if (!path || !path[0] || !path[1] || !path[2]) return false;
    const size_t length = std::wcslen(path);
    return length < 32000 && ((path[1] == L':' && (path[2] == L'\\' || path[2] == L'/')) ||
        (path[0] == L'\\' && path[1] == L'\\'));
}
struct Reference {
    uint32_t key;
    uint64_t hash;
    std::vector<unsigned char> bytes;
    std::wstring master;
};
}
struct HdvEngine {
    uint32_t game = 0, format = 0, x = 0, y = 0;
    uint64_t generation = 0;
    bool sealed = false, valid = false, failed = false;
    std::vector<Reference> references;
    std::vector<unsigned char> snapshot;
    std::vector<unsigned char> region;
    const Reference* match = nullptr;
};
extern "C" HdvEngine* hdv_create(uint32_t game, uint32_t format) {
    if (game < HDV_YUGI || game > HDV_JOEY || format < HDV_RGB565 || format > HDV_RGB555) return nullptr;
    try {
        auto engine = std::make_unique<HdvEngine>();
        engine->game = game; engine->format = format;
        engine->x = game == HDV_JOEY ? 2u : 5u;
        engine->y = game == HDV_JOEY ? 74u : 72u;
        engine->snapshot.resize(size_t(Width) * Height * 2);
        engine->region.resize(ReferenceBytes);
        return engine.release();
    } catch (...) { return nullptr; }
}
extern "C" void hdv_destroy(HdvEngine* engine) { delete engine; }
extern "C" int hdv_add(HdvEngine* engine, uint32_t key, const void* bytes,
    size_t count, const wchar_t* master) {
    if (!engine || engine->sealed || engine->failed) return 0;
    if (!key || !bytes || count != ReferenceBytes || !absolute_path(master) ||
        engine->references.size() >= MaximumEntries ||
        std::any_of(engine->references.begin(), engine->references.end(),
            [key](const Reference& entry) { return entry.key == key; })) {
        engine->failed = true; return 0;
    }
    try {
        const auto* first = static_cast<const unsigned char*>(bytes);
        Reference entry{key, hash_bytes(first, count), {first, first + count}, master};
        engine->references.push_back(std::move(entry)); return 1;
    } catch (...) { engine->failed = true; return 0; }
}
extern "C" int hdv_seal(HdvEngine* engine) {
    if (!engine || engine->failed || engine->references.empty()) return 0;
    engine->sealed = true; return 1;
}
extern "C" void hdv_invalidate(HdvEngine* engine) {
    if (engine) { engine->valid = false; engine->match = nullptr; ++engine->generation; }
}
extern "C" int hdv_capture(HdvEngine* engine, const void* pixels, uint32_t width,
    uint32_t height, uint32_t pitch, uint32_t format) {
    hdv_invalidate(engine);
    if (!engine || !engine->sealed || engine->failed || !pixels ||
        width != Width || height != Height || pitch < Width * 2 || pitch > 65536 ||
        format != engine->format) return 0;
    const auto* input = static_cast<const unsigned char*>(pixels);
    for (uint32_t y = 0; y < Height; ++y)
        std::memcpy(engine->snapshot.data() + size_t(y) * Width * 2,
            input + size_t(y) * pitch, Width * 2);
    for (uint32_t y = 0; y < CardHeight; ++y)
        std::memcpy(engine->region.data() + size_t(y) * CardWidth * 2,
            engine->snapshot.data() + (size_t(y + engine->y) * Width + engine->x) * 2,
            CardWidth * 2);
    const uint64_t hash = hash_bytes(engine->region.data(), ReferenceBytes);
    size_t matches = 0;
    for (const auto& entry : engine->references) {
        if (entry.hash == hash && std::memcmp(entry.bytes.data(), engine->region.data(), ReferenceBytes) == 0) {
            engine->match = &entry; ++matches;
        }
    }
    if (matches != 1) engine->match = nullptr;
    engine->valid = true; return 1;
}
extern "C" int hdv_snapshot(const HdvEngine* engine, HdvFrame* frame) {
    if (!engine || !frame || !engine->valid) return 0;
    *frame = {engine->snapshot.data(), Width, Height, Width * 2, engine->format, engine->generation}; return 1;
}
extern "C" int hdv_match(const HdvEngine* engine, HdvMatch* match) {
    if (!engine || !match || !engine->valid || !engine->match) return 0;
    *match = {engine->match->key, engine->x, engine->y, CardWidth, CardHeight,
        engine->generation, engine->match->master.c_str()}; return 1;
}
