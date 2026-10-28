#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d9.h>
#include <wrl/client.h>
#include <cstring>
#include <vector>
#include <atomic>
#include "hd_visible_test_observer.h"
using Microsoft::WRL::ComPtr;
namespace {
SRWLOCK data_lock = SRWLOCK_INIT;
HdvTestResult latest{};
std::vector<unsigned char> pixels;
std::atomic<uint32_t> controls{0};
std::atomic<uint64_t> starts{0}, stops{0};
HdvGdiTestState gdi_state{};
void publish(std::vector<unsigned char>& owned, uint32_t width, uint32_t height, int replaced, const HdvGdiTestState* state = nullptr) {
    AcquireSRWLockExclusive(&data_lock);
    pixels.swap(owned); ++latest.sequence; latest.width = width; latest.height = height;
    latest.byte_count = static_cast<uint32_t>(pixels.size()); latest.replaced = replaced ? 1u : 0u;
    gdi_state = state ? *state : HdvGdiTestState{};
    if (state) gdi_state.sequence = latest.sequence;
    ReleaseSRWLockExclusive(&data_lock);
}
}
extern "C" void hdv_test_observe(void* opaque, int replaced) {
    try {
        auto* device = static_cast<IDirect3DDevice9*>(opaque);
        if (!device) return;
        ComPtr<IDirect3DSurface9> target, storage;
        D3DSURFACE_DESC desc{};
        if (FAILED(device->GetRenderTarget(0, target.GetAddressOf())) || FAILED(target->GetDesc(&desc)) ||
            desc.Format != D3DFMT_X8R8G8B8 || !desc.Width || !desc.Height ||
            uint64_t(desc.Width) * desc.Height * 4 > 16 * 1024 * 1024 ||
            FAILED(device->CreateOffscreenPlainSurface(desc.Width, desc.Height, desc.Format, D3DPOOL_SYSTEMMEM, storage.GetAddressOf(), nullptr)) ||
            FAILED(device->GetRenderTargetData(target.Get(), storage.Get()))) return;
        std::vector<unsigned char> owned(size_t(desc.Width) * desc.Height * 4);
        D3DLOCKED_RECT locked{};
        if (FAILED(storage->LockRect(&locked, nullptr, D3DLOCK_READONLY))) return;
        for (uint32_t y = 0; y < desc.Height; ++y)
            std::memcpy(owned.data() + size_t(y) * desc.Width * 4,
                static_cast<unsigned char*>(locked.pBits) + size_t(y) * locked.Pitch, size_t(desc.Width) * 4);
        storage->UnlockRect();
        publish(owned, desc.Width, desc.Height, replaced);
    } catch (...) { }
}
extern "C" void hdv_test_observe_gdi(void* opaque, uint32_t width, uint32_t height, int replaced) {
    hdv_test_observe_gdi_frame(opaque, width, height, replaced ? 2 : 0, 0, 0, 0,
        static_cast<int>(width), static_cast<int>(height), 0);
}
extern "C" void hdv_test_observe_gdi_frame(void* opaque, uint32_t width, uint32_t height,
    int backend_result, uint64_t instance, int vx, int vy, int vw, int vh, uint32_t flags) {
    HDC dc = static_cast<HDC>(opaque);
    if (!dc || !width || !height || width > 4096 || height > 4096 ||
        uint64_t(width) * height * 4 > 16 * 1024 * 1024) return;
    HDC memory = nullptr; HBITMAP bitmap = nullptr; HGDIOBJ previous = nullptr;
    try {
        std::vector<unsigned char> owned(size_t(width) * height * 4);
        BITMAPINFO info{}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = static_cast<LONG>(width); info.bmiHeader.biHeight = -static_cast<LONG>(height);
        info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        memory = CreateCompatibleDC(dc);
        if (memory) bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (bitmap && bits) previous = SelectObject(memory, bitmap);
        if (previous && previous != HGDI_ERROR && GdiFlush() &&
            BitBlt(memory, 0, 0, static_cast<int>(width), static_cast<int>(height), dc, 0, 0, SRCCOPY) && GdiFlush()) {
            std::memcpy(owned.data(), bits, owned.size());
            HdvGdiTestState state{}; state.render_instance=instance;
            state.vx=vx;state.vy=vy;state.vw=vw;state.vh=vh;
            state.backend_result=backend_result;state.guard_flags=flags;
            publish(owned, width, height, backend_result==2, &state);
        }
    } catch (...) { }
    if (previous && previous != HGDI_ERROR) SelectObject(memory, previous);
    if (bitmap) DeleteObject(bitmap);
    if (memory) DeleteDC(memory);
}
extern "C" uint64_t hdv_test_gdi_begin(void) { return starts.fetch_add(1)+1; }
extern "C" void hdv_test_gdi_end(uint64_t instance) { if(instance) stops.fetch_add(1); }
extern "C" uint32_t hdv_test_gdi_controls(void) { return controls.load(); }
extern "C" int hdv_test_gdi_control(uint32_t flags) {
    if(flags & ~uint32_t(HDV_TEST_GDI_FORCE_BNET)) return 0;
    controls.store(flags);return 1;
}
extern "C" int hdv_test_gdi_read_state(HdvGdiTestState* state) {
    if(!state)return 0;
    AcquireSRWLockShared(&data_lock);*state=gdi_state;ReleaseSRWLockShared(&data_lock);
    state->starts=starts.load();state->stops=stops.load();return state->sequence?1:0;
}
extern "C" int hdv_test_read(void* destination, uint32_t capacity, HdvTestResult* result) {
    if (!result) return 0;
    AcquireSRWLockShared(&data_lock);
    *result = latest;
    const bool ok = latest.sequence && destination && capacity >= pixels.size();
    if (ok) std::memcpy(destination, pixels.data(), pixels.size());
    ReleaseSRWLockShared(&data_lock);
    return ok ? 1 : 0;
}
