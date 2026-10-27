#include "hd_visible.h"
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
bool test_game(uint32_t game, uint32_t format) {
    bool ok = true;
    Engine engine(hdv_create(game, format));
    const uint32_t x = game == HDV_JOEY ? 2u : 5u, y = game == HDV_JOEY ? 74u : 72u;
    const uint32_t pitch = 1632;
    std::vector<unsigned char> reference(200 * 290 * 2), frame(size_t(pitch) * 600, 17);
    for (size_t i = 0; i < reference.size(); ++i) reference[i] = static_cast<unsigned char>((i * 31 + i / 400) % 251);
    for (uint32_t row = 0; row < 290; ++row)
        std::memcpy(frame.data() + size_t(y + row) * pitch + x * 2, reference.data() + size_t(row) * 400, 400);
    ok &= expect(hdv_add(engine.get(), 42, reference.data(), reference.size(), L"C:\\authored-fixture.png") == 1, "add owned complete reference");
    ok &= expect(hdv_seal(engine.get()) == 1, "seal reference table");
    HdvMatch match{}; HdvFrame snapshot{};
    ok &= expect(hdv_capture(engine.get(), frame.data(), 800, 600, pitch, format) == 1, "padded-pitch capture");
    ok &= expect(hdv_match(engine.get(), &match) == 1 && match.key == 42 && match.x == x && match.y == y, "correct unique game region");
    ok &= expect(hdv_snapshot(engine.get(), &snapshot) == 1 && snapshot.pitch == 1600 && snapshot.generation == match.generation, "owned snapshot generation");
    const auto generation = snapshot.generation;
    const size_t last = size_t(y + 289) * pitch + x * 2 + 399;
    frame[last] ^= 1;
    ok &= expect(hdv_match(engine.get(), &match) == 1, "caller mutation cannot change captured frame");
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
    return ok;
}
}
int main() {
    bool ok = true;
    for (uint32_t game = 1; game <= 3; ++game)
        for (uint32_t format = 1; format <= 2; ++format) ok &= test_game(game, format);
    ok &= expect(hdv_create(0, 1) == nullptr && hdv_create(1, 3) == nullptr, "reject unsupported creation");
    std::printf("%d checks, %s\n", checks, ok ? "PASS" : "FAIL");
    return ok ? 0 : 1;
}
