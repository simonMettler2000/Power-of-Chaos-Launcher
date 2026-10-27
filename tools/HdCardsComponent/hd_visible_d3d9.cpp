#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d9.h>
#include <wincodec.h>
#include <wrl/client.h>
#include "hd_visible.h"
#include <algorithm>
#include <atomic>
#include <cstring>
#include <fstream>
#include <memory>
#include <sstream>
#include <string>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace {
struct EngineDeleter { void operator()(HdvEngine* value) const { hdv_destroy(value); } };
std::unique_ptr<HdvEngine, EngineDeleter> engine;
std::atomic<DWORD> owner_thread{0};
/* UINT64_MAX is terminal: saturation disables the overlay permanently. */
std::atomic<uint64_t> requested_epoch{1};
uint64_t captured_epoch = 0;
ComPtr<IUnknown> device_identity;
bool is_owner() { return owner_thread.load(std::memory_order_acquire) == GetCurrentThreadId(); }
bool claim_owner() {
    const DWORD current = GetCurrentThreadId(); DWORD empty = 0;
    return owner_thread.compare_exchange_strong(empty, current, std::memory_order_acq_rel) || empty == current;
}
bool current_capture() {
    const uint64_t request = requested_epoch.load(std::memory_order_acquire);
    return captured_epoch != 0 && request != UINT64_MAX && captured_epoch == request;
}
struct Image {
    uint32_t key = 0;
    uint64_t age = 0;
    std::vector<unsigned char> bgra;
};
std::vector<Image> images;
uint64_t cache_age = 0;
bool com_initialized = false;
bool com_usable = false;
constexpr uint32_t MasterWidth = 800, MasterHeight = 1160;
std::wstring from_utf8(const std::string& input) {
    if (input.empty() || input.size() > 32767 || input.find('\0') != std::string::npos) return {};
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring result(static_cast<size_t>(count), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), result.data(), count) != count) return {};
    return result;
}
bool absolute_local(const std::wstring& path) {
    /* Only drive-rooted local paths: refuse device, network and relative paths. */
    if (path.size() < 3 || path.size() > 32000 || path[1] != L':' ||
        (path[2] != L'\\' && path[2] != L'/') ||
        !((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z'))) return false;
    if (path.find(L':', 2) != std::wstring::npos) return false;
    size_t start = 3;
    while (start < path.size()) {
        const size_t end = path.find_first_of(L"\\/", start);
        const auto part = path.substr(start, end == std::wstring::npos ? path.size() - start : end - start);
        if (part.empty() || part.size() > 255 || part == L"." || part == L".." || part.back() == L'.' || part.back() == L' ') return false;
        for (wchar_t value : part) if (value < 32 || value == L'"' || value == L'<' || value == L'>' || value == L'|' || value == L'*' || value == L'?') return false;
        auto name = part.substr(0, part.find(L'.'));
        while (!name.empty() && name.back() == L' ') name.pop_back();
        for (auto& value : name) if (value >= L'a' && value <= L'z') value = static_cast<wchar_t>(value - L'a' + L'A');
        if (name == L"CON" || name == L"PRN" || name == L"AUX" || name == L"NUL" || name == L"CONIN$" || name == L"CONOUT$" ||
            (name.size() == 4 && (name.substr(0, 3) == L"COM" || name.substr(0, 3) == L"LPT") &&
                ((name[3] >= L'1' && name[3] <= L'9') || name[3] == L'\u00b9' || name[3] == L'\u00b2' || name[3] == L'\u00b3'))) return false;
        if (end == std::wstring::npos) break;
        start = end + 1;
        if (start == path.size()) return false;
    }
    /* Reject all linked ancestors before any content open; caller-supplied
       local packs must consist of concrete files in concrete directories. */
    for (size_t i = 3; i <= path.size(); ++i) {
        if (i != path.size() && path[i] != L'\\' && path[i] != L'/') continue;
        const DWORD attrs = GetFileAttributesW(path.substr(0, i).c_str());
        if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
    }
    return true;
}
bool bounded_file(const std::wstring& path, size_t max_bytes, std::vector<unsigned char>& output) {
    if (!absolute_local(path)) return false;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    BY_HANDLE_FILE_INFORMATION information{};
    const bool valid = GetFileType(file) == FILE_TYPE_DISK && GetFileInformationByHandle(file, &information) &&
        !(information.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) &&
        information.nFileSizeHigh == 0 && information.nFileSizeLow > 0 && information.nFileSizeLow <= max_bytes;
    bool success = false;
    if (valid) {
        try {
            output.resize(information.nFileSizeLow);
            DWORD read = 0;
            success = ReadFile(file, output.data(), information.nFileSizeLow, &read, nullptr) && read == information.nFileSizeLow;
        } catch (...) { CloseHandle(file); throw; }
    }
    CloseHandle(file); return success;
}
bool parse_uint(const std::string& text, uint32_t& value) {
    if (text.empty() || text.size() > 10) return false;
    uint64_t number = 0;
    for (char c : text) { if (c < '0' || c > '9') return false; number = number * 10 + static_cast<unsigned>(c - '0'); }
    if (!number || number > UINT32_MAX) return false;
    value = static_cast<uint32_t>(number); return true;
}
bool read_config(const wchar_t* path) {
    if (!path) return false;
    std::vector<unsigned char> data;
    if (!bounded_file(path, 1024 * 1024, data)) return false;
    std::string text(data.begin(), data.end());
    if (text.find('\0') != std::string::npos) return false;
    std::istringstream lines(text); std::string line;
    auto next = [&]() { if (!std::getline(lines, line)) return false; if (!line.empty() && line.back() == '\r') line.pop_back(); return true; };
    if (!next() || line != "POC_HD_VISIBLE_V1" || !next()) return false;
    auto split = line.find('\t'); uint32_t game = 0, format = 0;
    if (split == std::string::npos || !parse_uint(line.substr(0, split), game) || !parse_uint(line.substr(split + 1), format)) return false;
    std::unique_ptr<HdvEngine, EngineDeleter> candidate(hdv_create(game, format));
    if (!candidate) return false;
    size_t count = 0;
    while (next()) {
        if (line.empty()) return false;
        const auto first = line.find('\t'), second = first == std::string::npos ? first : line.find('\t', first + 1);
        if (first == std::string::npos || second == std::string::npos || line.find('\t', second + 1) != std::string::npos) return false;
        uint32_t key = 0;
        const auto reference = from_utf8(line.substr(first + 1, second - first - 1));
        const auto master = from_utf8(line.substr(second + 1));
        std::vector<unsigned char> baseline;
        if (++count > 1024 || !parse_uint(line.substr(0, first), key) || !absolute_local(master) ||
            !bounded_file(reference, 200 * 290 * 2, baseline) || baseline.size() != 200 * 290 * 2 ||
            !hdv_add(candidate.get(), key, baseline.data(), baseline.size(), master.c_str())) return false;
    }
    if (!hdv_seal(candidate.get())) return false;
    engine = std::move(candidate); return true;
}
bool initialize_com() {
    if (!com_usable) {
        const HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        com_initialized = SUCCEEDED(result);
        com_usable = com_initialized || result == RPC_E_CHANGED_MODE;
    }
    return com_usable;
}
const Image* image_for(const HdvMatch& match) {
    for (auto& entry : images) if (entry.key == match.key) { entry.age = ++cache_age; return &entry; }
    if (!initialize_com()) return nullptr;
    /* Read bytes once with a bound, then WIC decodes the same owned bytes. */
    std::vector<unsigned char> png;
    if (!bounded_file(match.master_path, 16 * 1024 * 1024, png)) return nullptr;
    ComPtr<IWICImagingFactory> factory;
    ComPtr<IWICStream> stream;
    ComPtr<IWICBitmapDecoder> decoder;
    ComPtr<IWICBitmapFrameDecode> frame;
    ComPtr<IWICFormatConverter> converter;
    GUID container{}; UINT width = 0, height = 0, frames = 0;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.GetAddressOf()))) ||
        FAILED(factory->CreateStream(stream.GetAddressOf())) ||
        FAILED(stream->InitializeFromMemory(png.data(), static_cast<DWORD>(png.size()))) ||
        FAILED(factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad, decoder.GetAddressOf())) ||
        FAILED(decoder->GetContainerFormat(&container)) || container != GUID_ContainerFormatPng ||
        FAILED(decoder->GetFrameCount(&frames)) || frames != 1 ||
        FAILED(decoder->GetFrame(0, frame.GetAddressOf())) ||
        FAILED(frame->GetSize(&width, &height)) || width != MasterWidth || height != MasterHeight ||
        FAILED(factory->CreateFormatConverter(converter.GetAddressOf())) ||
        FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) return nullptr;
    Image image; image.key = match.key; image.age = ++cache_age;
    image.bgra.resize(size_t(MasterWidth) * MasterHeight * 4);
    if (FAILED(converter->CopyPixels(nullptr, MasterWidth * 4, static_cast<UINT>(image.bgra.size()), image.bgra.data()))) return nullptr;
    for (size_t i = 3; i < image.bgra.size(); i += 4) if (image.bgra[i] != 255) return nullptr;
    if (images.size() == 4) images.erase(std::min_element(images.begin(), images.end(), [](const Image& a, const Image& b) { return a.age < b.age; }));
    images.push_back(std::move(image)); return &images.back();
}
struct Vertex { float x, y, z, rhw, u, v; };
void clear_owner_state() {
    captured_epoch = 0; engine.reset(); images.clear(); cache_age = 0; device_identity.Reset();
    if (com_initialized) CoUninitialize();
    com_initialized = false; com_usable = false;
}
}
extern "C" int hdv_provider_configure(const wchar_t* path) {
    if (!claim_owner()) return 0;
    clear_owner_state();
    try { return read_config(path) ? 1 : 0; } catch (...) { engine.reset(); return 0; }
}
extern "C" void hdv_provider_shutdown() {
    if (!is_owner()) return;
    clear_owner_state();
    owner_thread.store(0, std::memory_order_release);
}
extern "C" void hdv_provider_invalidate() {
    uint64_t epoch = requested_epoch.load(std::memory_order_relaxed);
    while (epoch != UINT64_MAX && !requested_epoch.compare_exchange_weak(epoch, epoch + 1,
        std::memory_order_acq_rel, std::memory_order_relaxed)) { }
}
extern "C" int hdv_provider_bind_device(void* opaque) {
    if (!is_owner() || !opaque) return 0;
    auto* device = static_cast<IDirect3DDevice9*>(opaque);
    ComPtr<IUnknown> identity;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(identity.GetAddressOf())))) return 0;
    if (identity.Get() != device_identity.Get()) {
        captured_epoch = 0; hdv_invalidate(engine.get()); images.clear(); cache_age = 0;
        device_identity = std::move(identity);
    }
    return 1;
}
extern "C" void hdv_provider_capture(const void* pixels, uint32_t width, uint32_t height, uint32_t pitch, uint32_t format) {
    if (!is_owner()) return;
    captured_epoch = 0;
    const uint64_t epoch = requested_epoch.load(std::memory_order_acquire);
    if (!device_identity || epoch == UINT64_MAX) { hdv_invalidate(engine.get()); return; }
    if (hdv_capture(engine.get(), pixels, width, height, pitch, format) &&
        epoch == requested_epoch.load(std::memory_order_acquire)) captured_epoch = epoch;
}
extern "C" int hdv_provider_draw(void* opaque, int vx, int vy, int vw, int vh, int eligible) {
    if (!is_owner()) return 0;
    try {
        HdvMatch match{};
        if (!opaque || !eligible || vx < 0 || vy < 0 || vw <= 800 || vh <= 600 ||
            vw > 16384 || vh > 16384 || !current_capture() || !hdv_match(engine.get(), &match)) return 0;
        auto* device = static_cast<IDirect3DDevice9*>(opaque);
        ComPtr<IUnknown> identity;
        if (FAILED(device->QueryInterface(IID_PPV_ARGS(identity.GetAddressOf())))) return 0;
        if (identity.Get() != device_identity.Get()) { hdv_provider_bind_device(opaque); return 0; }
        const Image* image = image_for(match);
        if (!image) return 0;
        ComPtr<IDirect3DSurface9> target;
        D3DSURFACE_DESC target_desc{};
        if (FAILED(device->GetRenderTarget(0, target.GetAddressOf())) || FAILED(target->GetDesc(&target_desc)) ||
            static_cast<uint64_t>(vx) + vw > target_desc.Width || static_cast<uint64_t>(vy) + vh > target_desc.Height) return 0;
        ComPtr<IDirect3DTexture9> texture;
        if (FAILED(device->CreateTexture(MasterWidth, MasterHeight, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, texture.GetAddressOf(), nullptr))) return 0;
        D3DLOCKED_RECT locked{};
        if (FAILED(texture->LockRect(0, &locked, nullptr, 0))) return 0;
        if (locked.Pitch < static_cast<int>(MasterWidth * 4)) { texture->UnlockRect(0); return 0; }
        for (uint32_t y = 0; y < MasterHeight; ++y)
            std::memcpy(static_cast<unsigned char*>(locked.pBits) + size_t(y) * locked.Pitch,
                image->bgra.data() + size_t(y) * MasterWidth * 4, MasterWidth * 4);
        if (FAILED(texture->UnlockRect(0))) return 0;
        ComPtr<IDirect3DStateBlock9> state;
        if (FAILED(device->CreateStateBlock(D3DSBT_ALL, state.GetAddressOf())) || FAILED(state->Capture())) return 0;
        const float left = static_cast<float>(vx) + static_cast<float>(match.x) * vw / 800.0f - 0.5f;
        const float top = static_cast<float>(vy) + static_cast<float>(match.y) * vh / 600.0f - 0.5f;
        const float right = left + static_cast<float>(match.width) * vw / 800.0f;
        const float bottom = top + static_cast<float>(match.height) * vh / 600.0f;
        const Vertex vertices[] = {{left, top, 0, 1, 0, 0}, {right, top, 0, 1, 1, 0},
            {left, bottom, 0, 1, 0, 1}, {right, bottom, 0, 1, 1, 1}};
        D3DVIEWPORT9 viewport{0, 0, target_desc.Width, target_desc.Height, 0, 1};
        bool ok = SUCCEEDED(device->SetViewport(&viewport));
        ok = SUCCEEDED(device->SetVertexShader(nullptr)) && ok;
        ok = SUCCEEDED(device->SetPixelShader(nullptr)) && ok;
        ok = SUCCEEDED(device->SetFVF(D3DFVF_XYZRHW | D3DFVF_TEX1)) && ok;
        ok = SUCCEEDED(device->SetTexture(0, texture.Get())) && ok;
        ok = SUCCEEDED(device->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1)) && ok;
        ok = SUCCEEDED(device->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE)) && ok;
        ok = SUCCEEDED(device->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_DISABLE)) && ok;
        ok = SUCCEEDED(device->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE)) && ok;
        for (auto setting : {D3DRS_ZENABLE, D3DRS_ZWRITEENABLE, D3DRS_ALPHABLENDENABLE, D3DRS_ALPHATESTENABLE,
            D3DRS_LIGHTING, D3DRS_FOGENABLE, D3DRS_STENCILENABLE, D3DRS_SCISSORTESTENABLE, D3DRS_SRGBWRITEENABLE})
            ok = SUCCEEDED(device->SetRenderState(setting, FALSE)) && ok;
        ok = SUCCEEDED(device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE)) && ok;
        ok = SUCCEEDED(device->SetRenderState(D3DRS_COLORWRITEENABLE, 15)) && ok;
        ok = SUCCEEDED(device->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_LINEAR)) && ok;
        ok = SUCCEEDED(device->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR)) && ok;
        ok = SUCCEEDED(device->SetSamplerState(0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP)) && ok;
        ok = SUCCEEDED(device->SetSamplerState(0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP)) && ok;
        /* Reset requests can arrive from the window thread. They mutate only
           the atomic epoch; consume cancellation immediately before draw. */
        if (ok) ok = current_capture() && SUCCEEDED(device->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, vertices, sizeof(Vertex)));
        const bool restored = SUCCEEDED(state->Apply());
        return ok && restored ? 1 : 0;
    } catch (...) { return 0; }
}
