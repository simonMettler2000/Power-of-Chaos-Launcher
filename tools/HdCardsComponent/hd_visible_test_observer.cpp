#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d9.h>
#include <wrl/client.h>
#include <cstring>
#include <vector>
#include "hd_visible_test_observer.h"
using Microsoft::WRL::ComPtr;
namespace {
SRWLOCK data_lock = SRWLOCK_INIT;
HdvTestResult latest{};
std::vector<unsigned char> pixels;
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
        AcquireSRWLockExclusive(&data_lock);
        pixels.swap(owned); ++latest.sequence; latest.width = desc.Width; latest.height = desc.Height;
        latest.byte_count = static_cast<uint32_t>(pixels.size()); latest.replaced = replaced ? 1u : 0u;
        ReleaseSRWLockExclusive(&data_lock);
    } catch (...) { }
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
