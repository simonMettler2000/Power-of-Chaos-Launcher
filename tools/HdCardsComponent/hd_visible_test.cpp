#include "hd_visible.h"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <memory>
#include <vector>
namespace {
int checks = 0;
bool expect(bool result, const char* message) {
    ++checks;
    if (!result) std::fprintf(stderr, "FAIL: %s\n", message);
    return result;
}
struct Delete { void operator()(HdvEngine* engine) const { hdv_destroy(engine); } };
using Engine = std::unique_ptr<HdvEngine, Delete>;
struct Region { uint32_t game, x, y; };
const Region ExpectedRegions[] = {
    {HDV_YUGI, 5, 72}, {HDV_YUGI, 11, 88}, {HDV_YUGI, 7, 89},
    {HDV_KAIBA, 5, 72}, {HDV_KAIBA, 16, 86}, {HDV_KAIBA, 7, 89},
    {HDV_JOEY, 2, 74}, {HDV_JOEY, 6, 87}, {HDV_JOEY, 7, 89}
};
bool test_game(uint32_t game, uint32_t format, uint32_t x, uint32_t y) {
    bool ok = true;
    Engine engine(hdv_create(game, format));
    const uint32_t pitch = 1632;
    std::vector<unsigned char> reference(200 * 290 * 2), frame(size_t(pitch) * 600, 17);
    for (size_t i = 0; i < reference.size(); ++i) reference[i] = static_cast<unsigned char>((i * 31 + i / 400) % 251);
    for (uint32_t row = 0; row < 290; ++row)
        std::memcpy(frame.data() + size_t(y + row) * pitch + x * 2, reference.data() + size_t(row) * 400, 400);
    ok &= expect(hdv_add(engine.get(), 42, reference.data(), reference.size(), L"C:\\authored-fixture.png") == 1, "add owned complete reference");
    reference[37] ^= 1;
    ok &= expect(hdv_seal(engine.get()) == 1, "seal reference table");
    HdvMatch match{}; HdvFrame snapshot{};
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format) == 1, "padded-pitch capture");
    ok &= expect(hdv_match(engine.get(), &match) == 1 && match.key == 42 && match.x == x && match.y == y &&
        match.width == 200 && match.height == 290, "correct unique region and complete extent, with copied reference");
    reference[37] ^= 1;
    ok &= expect(hdv_snapshot(engine.get(), &snapshot) == 1 && snapshot.width == 800 && snapshot.height == 600 &&
        snapshot.pitch == 1600 && snapshot.format == format && snapshot.generation == match.generation, "owned snapshot geometry and generation");
    bool whole_snapshot = snapshot.pixels != frame.data();
    if (snapshot.pixels)
        for (uint32_t row = 0; row < 600; ++row)
            whole_snapshot &= std::memcmp(static_cast<const unsigned char*>(snapshot.pixels) + size_t(row) * 1600,
                frame.data() + size_t(row) * pitch, 1600) == 0;
    ok &= expect(whole_snapshot, "every full-frame byte copied without caller pitch padding");
    const auto generation = snapshot.generation;
    const size_t last = size_t(y + 289) * pitch + x * 2 + 399;
    frame[last] ^= 1;
    ok &= expect(hdv_match(engine.get(), &match) == 1, "caller mutation cannot change captured frame");
    ok &= expect(snapshot.pixels && static_cast<const unsigned char*>(snapshot.pixels)[size_t(y + 289) * 1600 + x * 2 + 399] != frame[last],
        "owned snapshot content survives caller mutation");
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format) == 1 && hdv_match(engine.get(), &match) == 0, "last-byte occlusion rejects full raster");
    frame[last] ^= 1;
    frame[size_t(y) * pitch + x * 2] ^= 1;
    hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format);
    ok &= expect(hdv_match(engine.get(), &match) == 0, "first-byte fade rejects");
    frame[size_t(y) * pitch + x * 2] ^= 1;
    hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format);
    hdv_snapshot(engine.get(), &snapshot);
    ok &= expect(snapshot.generation > generation, "monotonic snapshot generation");
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format == 1 ? 2u : 1u) == 0 && hdv_match(engine.get(), &match) == 0, "unknown pixel format invalidates previous match");
    ok &= expect(hdv_capture(engine.get(), frame.data(), 799, 600, pitch, format) == 0, "unknown layout fails closed");
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 600, 1599, format) == 0, "short pitch fails closed");
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 600, 65537, format) == 0, "oversized pitch fails closed");
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 599, pitch, format) == 0, "unknown height fails closed");
    ok &= expect(hdv_capture(engine.get(), nullptr, 800, 600, pitch, format) == 0 &&
        hdv_snapshot(engine.get(), &snapshot) == 0, "null capture invalidates the full snapshot");
    hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format);
    hdv_invalidate(engine.get());
    ok &= expect(hdv_snapshot(engine.get(), &snapshot) == 0 && hdv_match(engine.get(), &match) == 0, "device/lifecycle invalidation");
    Engine ambiguous(hdv_create(game, format));
    ok &= expect(hdv_add(ambiguous.get(), 1, reference.data(), reference.size(), L"C:\\first.png") == 1 &&
        hdv_add(ambiguous.get(), 2, reference.data(), reference.size(), L"C:\\second.png") == 1 && hdv_seal(ambiguous.get()) == 1, "allow references but defer ambiguity to full match");
    hdv_capture(ambiguous.get(), frame.data(), 800, 600, pitch, format);
    ok &= expect(hdv_match(ambiguous.get(), &match) == 0, "identical visible references are ambiguous");
    Engine malformed(hdv_create(game, format));
    ok &= expect(hdv_add(malformed.get(), 1, reference.data(), reference.size() - 1, L"C:\\first.png") == 0 && hdv_seal(malformed.get()) == 0, "malformed pack cannot partially activate");
    std::fill(frame.begin(), frame.end(), static_cast<unsigned char>(17));
    for (uint32_t row = 0; row < 290; ++row)
        std::memcpy(frame.data() + size_t(155 + row) * pitch + 300 * 2,
            reference.data() + size_t(row) * 400, 400);
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format) == 1 &&
        hdv_match(engine.get(), &match) == 0 && hdv_snapshot(engine.get(), &snapshot) == 1,
        "exact reference at an unsupported position keeps owned base frame and falls back");
    return ok;
}
bool test_placements(uint32_t game, uint32_t format) {
    bool ok = true;
    size_t count = 0;
    for (const auto& region : ExpectedRegions) if (region.game == game) ++count;
    if (count < 2) return true;
    std::vector<unsigned char> solid(200 * 290 * 2, 85), frame(800 * 600 * 2, 85);
    Engine same(hdv_create(game, format));
    HdvMatch match{}; HdvFrame snapshot{};
    ok &= expect(hdv_add(same.get(), 1, solid.data(), solid.size(), L"C:\\solid.png") == 1 && hdv_seal(same.get()) == 1,
        "seal one authored reference for multi-placement test");
    const Region* first = nullptr;
    for (const auto& region : ExpectedRegions) if (region.game == game && !first) first = &region;
    std::fill(frame.begin(), frame.end(), static_cast<unsigned char>(17));
    if (first)
        for (uint32_t row = 0; row < 290; ++row)
            std::memcpy(frame.data() + size_t(first->y + row) * 1600 + first->x * 2,
                solid.data() + size_t(row) * 400, 400);
    ok &= expect(hdv_capture(same.get(), frame.data(), 800, 600, 1600, format) == 1 &&
        hdv_match(same.get(), &match) == 1, "a single supported solid placement is unambiguous");
    const auto prior_generation = match.generation;
    const auto single_frame = frame;
    std::fill(frame.begin(), frame.end(), static_cast<unsigned char>(85));
    ok &= expect(hdv_capture(same.get(), frame.data(), 800, 600, 1600, format) == 1,
        "one reference can occur at multiple overlapping supported positions");
    ok &= expect(hdv_match(same.get(), &match) == 0 && hdv_snapshot(same.get(), &snapshot) == 1,
        "multiple placements of one reference are ambiguous and preserve base snapshot");
    ok &= expect(snapshot.generation > prior_generation, "ambiguous capture replaces the preceding valid generation");
    ok &= expect(hdv_capture(same.get(), single_frame.data(), 800, 600, 1600, format) == 1 &&
        hdv_match(same.get(), &match) == 1 && first && match.x == first->x && match.y == first->y,
        "unique placement recovers after an ambiguous frame");

    /* Absolute-coordinate authored bytes produce distinct, overlap-consistent
       full rasters at every supported position. No original artwork is used. */
    for (uint32_t y = 0; y < 600; ++y)
        for (uint32_t x = 0; x < 1600; ++x)
            frame[size_t(y) * 1600 + x] = static_cast<unsigned char>((x * 17 + y * 31 + (x * y) % 251) % 256);
    Engine different(hdv_create(game, format));
    uint32_t key = 1;
    for (const auto& region : ExpectedRegions) {
        if (region.game != game) continue;
        for (uint32_t row = 0; row < 290; ++row)
            std::memcpy(solid.data() + size_t(row) * 400,
                frame.data() + size_t(region.y + row) * 1600 + region.x * 2, 400);
        ok &= expect(hdv_add(different.get(), key++, solid.data(), solid.size(), L"C:\\pattern.png") == 1,
            "add distinct complete reference for overlapping supported placement");
    }
    ok &= expect(hdv_seal(different.get()) == 1 && hdv_capture(different.get(), frame.data(), 800, 600, 1600, format) == 1 &&
        hdv_match(different.get(), &match) == 0, "different references at multiple positions are ambiguous");

    // Keep one supported placement exact; a different crop with one corrupt
    // byte must not count as a second match merely because its layout exists.
    Engine unique(hdv_create(game, format));
    key = 1;
    for (const auto& region : ExpectedRegions) {
        if (region.game != game) continue;
        for (uint32_t row = 0; row < 290; ++row)
            std::memcpy(solid.data() + size_t(row) * 400,
                frame.data() + size_t(region.y + row) * 1600 + region.x * 2, 400);
        if (key != 1) solid.back() ^= 1;
        ok &= expect(hdv_add(unique.get(), key++, solid.data(), solid.size(), L"C:\\pattern.png") == 1,
            "add exact first reference and partial other-layout references");
    }
    ok &= expect(hdv_seal(unique.get()) == 1 && hdv_capture(unique.get(), frame.data(), 800, 600, 1600, format) == 1 &&
        hdv_match(unique.get(), &match) == 1 && first && match.x == first->x && match.y == first->y,
        "partial references at other supported placements cannot hide a unique complete hit");
    return ok;
}
}
int main() {
    bool ok = true;
    for (const auto& region : ExpectedRegions)
        for (uint32_t format = 1; format <= 2; ++format)
            ok &= test_game(region.game, format, region.x, region.y);
    for (uint32_t game = 1; game <= 3; ++game)
        for (uint32_t format = 1; format <= 2; ++format) ok &= test_placements(game, format);
    ok &= expect(hdv_create(0, 1) == nullptr && hdv_create(1, 3) == nullptr, "reject unsupported creation");
    std::printf("%d checks, %s\n", checks, ok ? "PASS" : "FAIL");
    return ok ? 0 : 1;
}
