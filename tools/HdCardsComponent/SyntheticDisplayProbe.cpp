/* Authored synthetic graphics only. Never opens a game or existing provider. */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d9.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>
#include <atomic>
#include <thread>
using Microsoft::WRL::ComPtr;
namespace {
bool write_png(const std::wstring& path, const std::vector<unsigned char>& bgra) {
    ComPtr<IWICImagingFactory> factory; ComPtr<IWICStream> stream;
    ComPtr<IWICBitmapEncoder> encoder; ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> properties;
    WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
    return SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.GetAddressOf()))) &&
        SUCCEEDED(factory->CreateStream(stream.GetAddressOf())) &&
        SUCCEEDED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE)) &&
        SUCCEEDED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, encoder.GetAddressOf())) &&
        SUCCEEDED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache)) &&
        SUCCEEDED(encoder->CreateNewFrame(frame.GetAddressOf(), properties.GetAddressOf())) &&
        SUCCEEDED(frame->Initialize(properties.Get())) && SUCCEEDED(frame->SetSize(800, 1160)) &&
        SUCCEEDED(frame->SetPixelFormat(&format)) && format == GUID_WICPixelFormat32bppBGRA &&
        SUCCEEDED(frame->WritePixels(1160, 3200, static_cast<UINT>(bgra.size()), const_cast<BYTE*>(bgra.data()))) &&
        SUCCEEDED(frame->Commit()) && SUCCEEDED(encoder->Commit());
}
std::string utf8(const std::wstring& value) {
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), count, nullptr, nullptr);
    return result;
}
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) { std::fprintf(stderr, "Usage: SyntheticDisplayProbe built-adapter.dll fresh-output-directory\n"); return 2; }
    const std::wstring directory = argv[2];
    if (!CreateDirectoryW(directory.c_str(), nullptr)) return 3;
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) return 4;
    std::vector<unsigned char> reference(116000), input(800 * 600 * 2, 0), master(800 * 1160 * 4, 255);
    for (size_t i = 0; i < reference.size(); ++i) reference[i] = static_cast<unsigned char>((i * 37 + i / 400) % 253);
    for (size_t row = 0; row < 290; ++row) std::memcpy(input.data() + ((72 + row) * 800 + 5) * 2, reference.data() + row * 400, 400);
    /* Actual new high-frequency master pixels, unrelated to low-res reference. */
    for (size_t y = 0; y < 1160; ++y) for (size_t x = 0; x < 800; ++x) {
        auto* p = master.data() + (y * 800 + x) * 4;
        p[0] = x < 400 ? 0 : 255; p[1] = static_cast<unsigned char>(((x / 2) ^ (y / 2)) & 1 ? 240 : 16); p[2] = x < 400 ? 255 : 0;
    }
    const std::wstring ref_path = directory + L"\\reference.bin", png_path = directory + L"\\master.png", config_path = directory + L"\\visible.tsv";
    { std::ofstream file(ref_path, std::ios::binary); file.write(reinterpret_cast<const char*>(reference.data()), static_cast<std::streamsize>(reference.size())); }
    if (!write_png(png_path, master)) return 5;
    { std::ofstream file(config_path, std::ios::binary); file << "POC_HD_VISIBLE_V1\n1\t1\n42\t" << utf8(ref_path) << '\t' << utf8(png_path) << '\n'; }
    HMODULE component = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!component) return 6;
    auto configure = reinterpret_cast<int(*)(const wchar_t*)>(GetProcAddress(component, "hdv_provider_configure"));
    auto capture = reinterpret_cast<void(*)(const void*, uint32_t, uint32_t, uint32_t, uint32_t)>(GetProcAddress(component, "hdv_provider_capture"));
    auto bind_device = reinterpret_cast<int(*)(void*)>(GetProcAddress(component, "hdv_provider_bind_device"));
    auto invalidate = reinterpret_cast<void(*)()>(GetProcAddress(component, "hdv_provider_invalidate"));
    auto shutdown = reinterpret_cast<void(*)()>(GetProcAddress(component, "hdv_provider_shutdown"));
    auto draw = reinterpret_cast<int(*)(void*, int, int, int, int, int)>(GetProcAddress(component, "hdv_provider_draw"));
    if (!configure || !capture || !invalidate || !shutdown || !draw || !bind_device || !configure(config_path.c_str())) return 7;
    WNDCLASSW wc{}; wc.lpfnWndProc = DefWindowProcW; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"HdvOwnedSyntheticProbe";
    if (!RegisterClassW(&wc)) return 8;
    HWND window = CreateWindowW(wc.lpszClassName, L"Owned synthetic HD test", WS_OVERLAPPED, 0, 0, 1600, 1200, nullptr, nullptr, wc.hInstance, nullptr);
    ComPtr<IDirect3D9> d3d; d3d.Attach(Direct3DCreate9(D3D_SDK_VERSION));
    ComPtr<IDirect3DDevice9> device;
    D3DPRESENT_PARAMETERS params{}; params.Windowed = TRUE; params.SwapEffect = D3DSWAPEFFECT_DISCARD;
    params.hDeviceWindow = window; params.BackBufferWidth = 1600; params.BackBufferHeight = 1200; params.BackBufferFormat = D3DFMT_X8R8G8B8;
    if (!window || !d3d || FAILED(d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window, D3DCREATE_SOFTWARE_VERTEXPROCESSING,
        &params, device.GetAddressOf()))) return 9;
    bool ok = true;
    if (!bind_device(device.Get())) return 9;
    capture(input.data(), 800, 600, 1600, 1);
    device->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1, 0);
    device->BeginScene(); const int drawn = draw(device.Get(), 0, 0, 1600, 1200, 1); device->EndScene();
    ComPtr<IDirect3DSurface9> target, readback;
    if (FAILED(device->GetRenderTarget(0, target.GetAddressOf())) ||
        FAILED(device->CreateOffscreenPlainSurface(1600, 1200, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, readback.GetAddressOf(), nullptr)) ||
        FAILED(device->GetRenderTargetData(target.Get(), readback.Get()))) return 10;
    D3DLOCKED_RECT locked{};
    if (FAILED(readback->LockRect(&locked, nullptr, D3DLOCK_READONLY))) return 11;
    auto pixel = [&](int x, int y) { uint32_t value = 0; std::memcpy(&value, static_cast<const unsigned char*>(locked.pBits) + size_t(y) * locked.Pitch + size_t(x) * 4, 4); return value & 0x00ffffff; };
    /* Region maps to (10,144)-(410,724). Two halves must contain red/blue,
       and samples at physical-pixel spacing must retain master detail. */
    const auto outside = pixel(9, 200), red = pixel(30, 200), blue = pixel(390, 200);
    const auto detail_a = pixel(32, 200), detail_b = pixel(33, 200);
    ok = drawn == 1 && outside == 0 && (red & 0xff0000) == 0xff0000 && (blue & 0xff) == 0xff && detail_a != detail_b;
    readback->UnlockRect();
    device->BeginScene();
    ok = draw(device.Get(), 0, 0, 1600, 1200, 0) == 0 && ok;
    ok = draw(device.Get(), 0, 0, 800, 600, 1) == 0 && ok;
    invalidate(); ok = draw(device.Get(), 0, 0, 1600, 1200, 1) == 0 && ok;
    input[((72 + 289) * 800 + 5) * 2 + 399] ^= 1;
    capture(input.data(), 800, 600, 1600, 1);
    ok = draw(device.Get(), 0, 0, 1600, 1200, 1) == 0 && ok;
    device->EndScene();
    /* Deterministic foreign-thread invalidation and ownership rejection. */
    input[((72 + 289) * 800 + 5) * 2 + 399] ^= 1;
    int foreign_configure = 1, foreign_bind = 1;
    capture(input.data(), 800, 600, 1600, 1);
    std::thread foreign([&] {
        foreign_configure = configure(config_path.c_str());
        foreign_bind = bind_device(device.Get());
        capture(input.data(), 800, 600, 1600, 1); shutdown(); invalidate();
    });
    foreign.join();
    device->BeginScene();
    ok = foreign_configure == 0 && foreign_bind == 0 && draw(device.Get(), 0, 0, 1600, 1200, 1) == 0 && ok;
    capture(input.data(), 800, 600, 1600, 1);
    ok = draw(device.Get(), 0, 0, 1600, 1200, 1) == 1 && ok;
    device->EndScene();
    std::atomic<bool> stop{false}; std::atomic<uint32_t> requests{0};
    std::thread invalidator([&] { while (!stop.load()) { invalidate(); ++requests; } });
    for (int i = 0; i < 64; ++i) {
        capture(input.data(), 800, 600, 1600, 1);
        device->BeginScene(); draw(device.Get(), 0, 0, 1600, 1200, 1); device->EndScene();
    }
    stop.store(true); invalidator.join(); invalidate();
    device->BeginScene();
    ok = requests.load() > 0 && draw(device.Get(), 0, 0, 1600, 1200, 1) == 0 && ok;
    device->EndScene();
    /* Actual Reset/resize on the device owner; request arrives from a foreign
       thread first. There is no lock across Reset or a window message. */
    target.Reset(); readback.Reset();
    invalidate();
    const HRESULT reset_result = device->Reset(&params);
    device->BeginScene(); ok = SUCCEEDED(reset_result) && draw(device.Get(), 0, 0, 1600, 1200, 1) == 0 && ok; device->EndScene();
    capture(input.data(), 800, 600, 1600, 1);
    device->BeginScene(); ok = draw(device.Get(), 0, 0, 1600, 1200, 1) == 1 && ok; device->EndScene();
    invalidate(); params.BackBufferWidth = 1200; params.BackBufferHeight = 900;
    const HRESULT resize_result = device->Reset(&params);
    capture(input.data(), 800, 600, 1600, 1);
    device->BeginScene(); ok = SUCCEEDED(resize_result) && draw(device.Get(), 0, 0, 1200, 900, 1) == 1 && ok; device->EndScene();
    ComPtr<IDirect3DDevice9> second_device;
    const HRESULT second_result = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window,
        D3DCREATE_SOFTWARE_VERTEXPROCESSING, &params, second_device.GetAddressOf());
    if (SUCCEEDED(second_result)) {
        second_device->BeginScene(); ok = draw(second_device.Get(), 0, 0, 1200, 900, 1) == 0 && ok; second_device->EndScene();
        capture(input.data(), 800, 600, 1600, 1);
        second_device->BeginScene(); ok = draw(second_device.Get(), 0, 0, 1200, 900, 1) == 1 && ok; second_device->EndScene();
    } else ok = false;
    std::ofstream receipt(directory + L"\\result.json");
    receipt << "{\"test\":\"authored-offscreen-d3d9-adapter\",\"drawn\":" << drawn << ",\"outside\":" << outside <<
        ",\"red\":" << red << ",\"blue\":" << blue << ",\"detailA\":" << detail_a << ",\"detailB\":" << detail_b <<
        ",\"foreignConfigure\":" << foreign_configure << ",\"foreignBind\":" << foreign_bind << ",\"concurrentInvalidations\":" << requests.load() <<
        ",\"resetHr\":" << reset_result << ",\"resizeHr\":" << resize_result << ",\"secondDeviceHr\":" << second_result <<
        ",\"passed\":" << (ok ? "true" : "false") << ",\"originalGameExecuted\":false,\"providerSeamRuntimeTested\":false}";
    shutdown(); second_device.Reset(); device.Reset(); d3d.Reset(); DestroyWindow(window); FreeLibrary(component); CoUninitialize();
    std::printf("Authored synthetic D3D9 physical-pixel replacement: %s\n", ok ? "PASS" : "FAIL");
    return ok ? 0 : 12;
}
