/* New authored DirectDraw host. Only accepts newly source-built test provider.
   The opt-in observer copies the real render target after the actual renderer
   drew its base upload and the HD extension. No original game is involved. */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <ddraw.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <fstream>
#include <string>
#include <vector>
#include <sstream>
#include <cmath>
#include "hd_visible_test_observer.h"
using Microsoft::WRL::ComPtr;
namespace {
bool put_frame(IDirectDrawSurface* primary, const std::vector<unsigned char>& input, uint32_t format) {
    DDSURFACEDESC locked{}; locked.dwSize = sizeof(locked);
    if (FAILED(primary->Lock(nullptr, &locked, DDLOCK_WAIT | DDLOCK_WRITEONLY, nullptr))) return false;
    const bool valid = locked.dwWidth == 800 && locked.dwHeight == 600 && locked.lPitch >= 1600 &&
        locked.ddpfPixelFormat.dwRGBBitCount == 16 && locked.ddpfPixelFormat.dwRBitMask == (format == 1 ? 0xf800u : 0x7c00u) &&
        locked.ddpfPixelFormat.dwGBitMask == (format == 1 ? 0x07e0u : 0x03e0u) && locked.ddpfPixelFormat.dwBBitMask == 0x001f;
    if (valid) for (size_t y = 0; y < 600; ++y)
        std::memcpy(static_cast<unsigned char*>(locked.lpSurface) + y * locked.lPitch, input.data() + y * 1600, 1600);
    const HRESULT result = primary->Unlock(nullptr);
    return valid && SUCCEEDED(result);
}
void messages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
}
std::wstring from_utf8(const std::string& value) {
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring result(static_cast<size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), count);
    return result;
}
bool read_master(const std::wstring& path, std::vector<unsigned char>& pixels) {
    ComPtr<IWICImagingFactory> factory; ComPtr<IWICBitmapDecoder> decoder;
    ComPtr<IWICBitmapFrameDecode> frame; ComPtr<IWICFormatConverter> convert;
    UINT width = 0, height = 0;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.GetAddressOf()))) ||
        FAILED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, decoder.GetAddressOf())) ||
        FAILED(decoder->GetFrame(0, frame.GetAddressOf())) || FAILED(frame->GetSize(&width, &height)) || width != 800 || height != 1160 ||
        FAILED(factory->CreateFormatConverter(convert.GetAddressOf())) ||
        FAILED(convert->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) return false;
    pixels.resize(800 * 1160 * 4);
    return SUCCEEDED(convert->CopyPixels(nullptr, 3200, static_cast<UINT>(pixels.size()), pixels.data()));
}
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 5 && argc != 6) { std::fprintf(stderr, "Usage: SyntheticProviderProbe new-test-provider.dll config.tsv reference.bin fresh-output [--pack]\n"); return 2; }
    const bool base_control = argc == 6 && std::wcscmp(argv[5], L"--base-control") == 0;
    const bool actual_pack = argc == 6 && (std::wcscmp(argv[5], L"--pack") == 0 || base_control);
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return 2;
    uint32_t game = 1, format = 1; std::wstring master_path;
    if (actual_pack) {
        std::ifstream file(argv[2], std::ios::binary); std::string line;
        std::getline(file, line); std::getline(file, line); std::istringstream meta(line); meta >> game >> format;
        std::getline(file, line); if (!line.empty() && line.back() == '\r') line.pop_back();
        const auto first = line.find('\t'), second = line.find('\t', first + 1);
        if (game < 1 || game > 3 || format < 1 || format > 2 || first == std::string::npos || second == std::string::npos) return 2;
        master_path = from_utf8(line.substr(second + 1)); if (master_path.empty()) return 2;
    }
    const uint32_t preview_x = game == 3 ? 2u : 5u, preview_y = game == 3 ? 74u : 72u;
    const std::wstring output = argv[4];
    if (!CreateDirectoryW(output.c_str(), nullptr)) return 3;
    std::ofstream log(output + L"\\host.log");
    auto note = [&](const char* what, HRESULT result) { log << what << " hr=" << std::hex << static_cast<unsigned long>(result) << std::dec << '\n'; log.flush(); return SUCCEEDED(result); };
    const std::wstring ini_path = output + L"\\ddraw.ini";
    { std::ofstream ini(ini_path); ini << "[ddraw]\nwidth=1600\nheight=1200\nwindowed=true\nmaintas=true\nrenderer=direct3d9\nmaxfps=30\nminfps=-1\nsinglecpu=false\nlock_surfaces=true\nadjmouse=false\nnoactivateapp=true\nresizeable=false\nwindowborder=false\nvsync=false\nrgb555=" << (format == 2 ? "true" : "false") << '\n'; }
    SetEnvironmentVariableW(L"POC_HD_CONFIG", argv[2]); SetEnvironmentVariableW(L"CNC_DDRAW_CONFIG_FILE", ini_path.c_str());
    WNDCLASSW wc{}; wc.lpfnWndProc = DefWindowProcW; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"OwnedHdvProviderProbe";
    if (!RegisterClassW(&wc)) return 4;
    HWND window = CreateWindowW(wc.lpszClassName, L"Owned synthetic provider HD test", WS_POPUP, 0, 0, 1600, 1200, nullptr, nullptr, wc.hInstance, nullptr);
    if (!window) return 5;
    HMODULE provider = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!provider) { log << "LoadLibrary error=" << GetLastError(); return 6; }
    auto create = reinterpret_cast<HRESULT(WINAPI*)(GUID*, LPDIRECTDRAW*, IUnknown*)>(GetProcAddress(provider, "DirectDrawCreate"));
    auto observe = reinterpret_cast<int(*)(void*, uint32_t, HdvTestResult*)>(GetProcAddress(provider, "hdv_test_read"));
    if (!create || !observe) return 7;
    IDirectDraw* draw = nullptr; IDirectDrawSurface* primary = nullptr;
    if (!note("DirectDrawCreate", create(nullptr, &draw, nullptr)) ||
        !note("SetCooperativeLevel", draw->SetCooperativeLevel(window, DDSCL_FULLSCREEN | DDSCL_EXCLUSIVE)) ||
        !note("SetDisplayMode800x600x16", draw->SetDisplayMode(800, 600, 16))) return 8;
    DDSURFACEDESC desc{}; desc.dwSize = sizeof(desc); desc.dwFlags = DDSD_CAPS; desc.ddsCaps.dwCaps = DDSCAPS_PRIMARYSURFACE;
    if (!note("CreateSurface primary", draw->CreateSurface(&desc, &primary, nullptr))) return 9;
    std::vector<unsigned char> input(800 * 600 * 2), reference(116000);
    { std::ifstream file(argv[3], std::ios::binary | std::ios::ate); if (!file || file.tellg() != 116000) return 10;
      file.seekg(0); if (!file.read(reinterpret_cast<char*>(reference.data()), static_cast<std::streamsize>(reference.size()))) return 10; }
    for (size_t y = 0; y < 290; ++y) std::memcpy(input.data() + ((preview_y + y) * 800 + preview_x) * 2, reference.data() + y * 400, 400);
    if (!put_frame(primary, input, format)) return 11;
    std::vector<unsigned char> captured(16 * 1024 * 1024);
    HdvTestResult result{};
    auto wait = [&](uint64_t after, bool replacement) {
        const ULONGLONG deadline = GetTickCount64() + 10000;
        while (GetTickCount64() < deadline) {
            messages();
            if (observe(captured.data(), static_cast<uint32_t>(captured.size()), &result) && result.sequence > after &&
                (result.replaced != 0) == replacement) return true;
            Sleep(10);
        }
        return false;
    };
    if (!wait(0, !base_control)) { log << "No expected frame. latest sequence=" << result.sequence << " size=" << result.width << 'x' << result.height << " replaced=" << result.replaced; return 12; }
    auto pixel = [&](uint32_t x, uint32_t y) { uint32_t value = 0; std::memcpy(&value, captured.data() + (size_t(y) * result.width + x) * 4, 4); return value & 0xffffff; };
    const auto first = result;
    /* Tests use the authored red/blue master from SyntheticDisplayProbe. */
    bool ok = result.width == 1600 && result.height == 1200;
    uint32_t outside = 0, red = 0, blue = 0, detail_a = 0, detail_b = 0;
    uint32_t compared_pixels = 0, wrong_pixels = 0;
    if (ok && !actual_pack) {
        outside = pixel(0, 0); red = pixel(30, 200); blue = pixel(390, 200); detail_a = pixel(32, 200); detail_b = pixel(33, 200);
        ok = outside == 0 && (red & 0xff0000) == 0xff0000 && (blue & 0xff) == 0xff && detail_a != detail_b;
    }
    if (ok && actual_pack && !base_control) {
        std::vector<unsigned char> expected;
        ok = read_master(master_path, expected);
        if (ok) for (uint32_t y = 0; y < 580; ++y) for (uint32_t x = 0; x < 400; ++x) {
            const uint32_t actual = pixel(preview_x * 2 + x, preview_y * 2 + y);
            bool wrong = false;
            for (uint32_t channel = 0; channel < 3; ++channel) {
                uint32_t sum = 0;
                for (uint32_t dy = 0; dy < 2; ++dy) for (uint32_t dx = 0; dx < 2; ++dx)
                    sum += expected[((size_t(y) * 2 + dy) * 800 + x * 2 + dx) * 4 + channel];
                const int wanted = static_cast<int>((sum + 2) / 4), got = static_cast<int>((actual >> (channel * 8)) & 255);
                if (std::abs(wanted - got) > 2) wrong = true;
            }
            ++compared_pixels; if (wrong) ++wrong_pixels;
        }
        ok = ok && wrong_pixels == 0 && compared_pixels == 400 * 580;
    }
    { std::ofstream file(output + L"\\matched-frame.bgra", std::ios::binary); file.write(reinterpret_cast<const char*>(captured.data()), first.byte_count); }
    if (!base_control) {
        input[((preview_y + 289) * 800 + preview_x) * 2 + 399] ^= 1;
        if (!put_frame(primary, input, format) || !wait(first.sequence, false)) { log << "No fresh occluded fallback frame"; return 13; }
    }
    { std::ofstream file(output + L"\\occluded-frame.bgra", std::ios::binary); file.write(reinterpret_cast<const char*>(captured.data()), result.byte_count); }
    std::ofstream receipt(output + L"\\result.json");
    receipt << "{\"test\":\"new-authored-directdraw-host-sourcebuilt-provider-fullchain\",\"matchedSequence\":" << first.sequence <<
        ",\"fallbackSequence\":" << result.sequence << ",\"width\":" << first.width << ",\"height\":" << first.height <<
        ",\"outside\":" << outside << ",\"red\":" << red << ",\"blue\":" << blue << ",\"detailA\":" << detail_a <<
        ",\"detailB\":" << detail_b << ",\"game\":" << game << ",\"format\":" << format << ",\"baseControl\":" << (base_control ? "true" : "false") << ",\"firstReplaced\":" << first.replaced << ",\"packMasterPixelsCompared\":" << compared_pixels << ",\"wrongPixels\":" << wrong_pixels << ",\"passed\":" << (ok ? "true" : "false") <<
        ",\"instrumentedTestProvider\":true,\"productionBinaryProven\":false,\"originalGameExecuted\":false}";
    primary->Release(); draw->Release(); DestroyWindow(window);
    /* Allow provider render-thread cleanup; no original process is involved. */
    messages(); FreeLibrary(provider); CoUninitialize();
    std::printf("New authored DirectDraw -> real provider upload -> HD overlay -> render-target readback: %s\n", ok ? "PASS" : "FAIL");
    return ok ? 0 : 14;
}
