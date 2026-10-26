// Original synthetic DirectDraw test host. Contains no game code or assets.
// The executable is a component probe; successful output is not game compatibility.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define DIRECTDRAW_VERSION 0x0300
#include <windows.h>
#include <windowsx.h>
#include <ddraw.h>
#ifndef ASPECT_PROBE_OBSERVER
#include <mmsystem.h>
#endif
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cwchar>
#include <filesystem>
#include <string>
#include <vector>

// Implemented using the SDK's C interface definitions in a separate translation unit.
bool verifiedDrawProvider(IDirectDraw* draw, HMODULE expected);

namespace {
constexpr int imageWidth = 800;
constexpr int imageHeight = 600;
struct Viewport { int x; int y; int width; int height; };
Viewport fitViewport(int width, int height) {
    if (width <= 0 || height <= 0) return {};
    int fittedWidth = width;
    int fittedHeight = static_cast<int>(static_cast<long long>(width) * imageHeight / imageWidth);
    if (fittedHeight > height) {
        fittedHeight = height;
        fittedWidth = static_cast<int>(static_cast<long long>(height) * imageWidth / imageHeight);
    }
    return {(width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight};
}
bool imagePoint(Viewport viewport, POINT client, POINT& image) {
    if (viewport.width <= 0 || viewport.height <= 0 || client.x < viewport.x || client.y < viewport.y ||
        client.x >= viewport.x + viewport.width || client.y >= viewport.y + viewport.height) return false;
    image.x = static_cast<LONG>(static_cast<long long>(client.x - viewport.x) * imageWidth / viewport.width);
    image.y = static_cast<LONG>(static_cast<long long>(client.y - viewport.y) * imageHeight / viewport.height);
    return true;
}
COLORREF patternColor(int x, int y) {
    // Thick grid and colored quadrants are intentionally generated, original art.
    if (x < 4 || y < 4 || x >= imageWidth - 4 || y >= imageHeight - 4) return RGB(255, 255, 255);
    if (x % 100 < 2 || y % 100 < 2) return RGB(96, 96, 96);
    if (x < imageWidth / 2) return y < imageHeight / 2 ? RGB(224, 32, 32) : RGB(32, 64, 224);
    return y < imageHeight / 2 ? RGB(32, 208, 64) : RGB(224, 208, 32);
}
DWORD channelValue(unsigned value, DWORD mask) {
    if (mask == 0) return 0;
    unsigned shift = 0;
    while ((mask & 1u) == 0) { mask >>= 1; ++shift; }
    return ((value * mask + 127) / 255) << shift;
}
DWORD packedColor(COLORREF color, const DDPIXELFORMAT& format) {
    return channelValue(GetRValue(color), format.dwRBitMask) |
           channelValue(GetGValue(color), format.dwGBitMask) |
           channelValue(GetBValue(color), format.dwBBitMask);
}
std::wstring executableDirectory() {
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) return {};
    path.resize(length);
    return std::filesystem::path(path).parent_path().wstring();
}
#ifdef ASPECT_PROBE_OBSERVER
int watchOwnedProcess(DWORD processId, const std::wstring& expectedExecutable, DWORD milliseconds, FILE* log) {
    if (processId == 0 || std::filesystem::path(expectedExecutable).filename() != L"DisplayProbe.exe") return 2;
    HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (!process) { std::fprintf(log, "watch-refused=open-process error=%lu\n", GetLastError()); return 2; }
    std::wstring actual(32768, L'\0');
    DWORD length = static_cast<DWORD>(actual.size());
    const bool queried = QueryFullProcessImageNameW(process, 0, actual.data(), &length) != FALSE;
    actual.resize(queried ? length : 0);
    const bool owned = queried && CompareStringOrdinal(actual.c_str(), -1, expectedExecutable.c_str(), -1, TRUE) == CSTR_EQUAL;
    if (!owned) { std::fprintf(log, "watch-refused=executable-mismatch\n"); CloseHandle(process); return 2; }
    std::fprintf(log, "watch-owned-pid=%lu deadline-ms=%lu executable=%ls\n", processId, milliseconds, actual.c_str());
    std::fflush(log);
    const DWORD result = WaitForSingleObject(process, milliseconds);
    if (result == WAIT_TIMEOUT) {
        // The launcher supplied this exact authored child PID/path. Never terminate another image.
        const bool terminated = TerminateProcess(process, 124) != FALSE;
        std::fprintf(log, "watch-deadline-expired terminated-owned-child=%d\n", terminated ? 1 : 0);
        CloseHandle(process);
        return 1;
    }
    std::fprintf(log, "watch-owned-child-ended wait=%lu\n", result);
    CloseHandle(process);
    return result == WAIT_OBJECT_0 ? 0 : 2;
}
#endif
struct App {
    HWND window = nullptr;
    HMODULE module = nullptr;
    IDirectDraw* draw = nullptr;
    IDirectDrawSurface* primary = nullptr;
    IDirectDrawSurface* offscreen = nullptr;
    IDirectDrawClipper* clipper = nullptr;
    FILE* log = nullptr;
    std::wstring output;
    std::wstring dll;
    std::wstring observerPath;
    bool wrapperVirtual = false;
    bool comActivation = true;
    bool comInitialized = false;
    bool hostFitControl = false;
    bool running = true;
    bool failed = false;
    bool captureFailed = false;
    bool interactive = false;
    double interactiveLimit = 180.0;
    const char* interactiveEnd = "not-started";
    double stageSeconds = 0.9;
    int stage = -1;
    unsigned long long frames = 0;
    LARGE_INTEGER frequency{};
    LARGE_INTEGER start{};
    LARGE_INTEGER stageStart{};
    DEVMODEW initialMode{};
    ~App() {
        if (offscreen) offscreen->Release();
        if (primary) primary->Release();
        if (clipper) clipper->Release();
        if (draw) draw->Release();
        if (comInitialized) CoUninitialize();
        if (module) FreeLibrary(module);
        if (log) std::fclose(log);
    }
    double elapsed(LARGE_INTEGER since) const {
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        return static_cast<double>(now.QuadPart - since.QuadPart) / static_cast<double>(frequency.QuadPart);
    }
    void note(const char* event, HRESULT result = S_OK) {
        if (log) { std::fprintf(log, "event=%s hr=0x%08lX elapsed=%.6f\n", event, static_cast<unsigned long>(result), elapsed(start)); std::fflush(log); }
    }
    bool check(const char* operation, HRESULT result) {
        note(operation, result);
        if (FAILED(result)) failed = true;
        return SUCCEEDED(result);
    }
    bool fillSurface(IDirectDrawSurface* surface) {
        DDSURFACEDESC desc{};
        desc.dwSize = sizeof(desc);
        if (!check("lock", surface->Lock(nullptr, &desc, DDLOCK_WAIT | DDLOCK_WRITEONLY, nullptr))) return false;
        const DWORD bits = desc.ddpfPixelFormat.dwRGBBitCount;
        const bool supported = (desc.ddpfPixelFormat.dwFlags & DDPF_RGB) != 0 && (bits == 16 || bits == 24 || bits == 32);
        if (supported) {
            for (int y = 0; y < imageHeight; ++y) {
                auto* row = static_cast<unsigned char*>(desc.lpSurface) + y * desc.lPitch;
                for (int x = 0; x < imageWidth; ++x) {
                    const DWORD value = packedColor(patternColor(x, y), desc.ddpfPixelFormat);
                    if (bits == 16) reinterpret_cast<WORD*>(row)[x] = static_cast<WORD>(value);
                    if (bits == 24) { row[x * 3] = static_cast<unsigned char>(value); row[x * 3 + 1] = static_cast<unsigned char>(value >> 8); row[x * 3 + 2] = static_cast<unsigned char>(value >> 16); }
                    if (bits == 32) reinterpret_cast<DWORD*>(row)[x] = value;
                }
            }
        }
        const HRESULT result = surface->Unlock(nullptr);
        if (!supported) { note("unsupported-pixel-format", E_NOTIMPL); failed = true; }
        return check("unlock", result) && supported;
    }
    bool initialize() {
        // There is no automatic fallback to system ddraw. The coordinator supplies an explicit tested DLL.
        module = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!module) { note("load-explicit-ddraw", HRESULT_FROM_WIN32(GetLastError())); return false; }
        if (comActivation) {
            const HRESULT apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            if (!check("CoInitializeEx", apartment)) return false;
            comInitialized = true;
            // Matches the discovered game activation shape. No fallback if COM interception fails.
            if (!check("CoCreateInstance-CLSID_DirectDraw", CoCreateInstance(CLSID_DirectDraw, nullptr, CLSCTX_INPROC_SERVER, IID_IDirectDraw, reinterpret_cast<void**>(&draw)))) return false;
        } else {
            using CreateDraw = HRESULT (WINAPI*)(GUID*, LPDIRECTDRAW*, IUnknown*);
            const auto createDraw = reinterpret_cast<CreateDraw>(GetProcAddress(module, "DirectDrawCreate"));
            if (!createDraw || !check("DirectDrawCreate-control", createDraw(nullptr, &draw, nullptr))) return false;
        }
        if (!verifiedDrawProvider(draw, module)) {
            // In particular, never apply exclusive cooperative flags to accidental system DirectDraw.
            note("REFUSED-object-provider-is-not-explicit-wrapper", E_ACCESSDENIED); return false;
        }
        note("object-provider-verified");
        if (comActivation && !check("Initialize-after-COM", draw->Initialize(nullptr))) return false;
        const DWORD cooperativeFlags = wrapperVirtual ? DDSCL_FULLSCREEN | DDSCL_EXCLUSIVE : DDSCL_NORMAL;
        if (!check("SetCooperativeLevel", draw->SetCooperativeLevel(window, cooperativeFlags))) return false;
        if (wrapperVirtual && !check("SetDisplayMode-virtual-800x600x16", draw->SetDisplayMode(imageWidth, imageHeight, 16))) return false;
        DDSURFACEDESC primaryDesc{};
        primaryDesc.dwSize = sizeof(primaryDesc);
        primaryDesc.dwFlags = DDSD_CAPS;
        primaryDesc.ddsCaps.dwCaps = DDSCAPS_PRIMARYSURFACE;
        if (!check("CreatePrimary", draw->CreateSurface(&primaryDesc, &primary, nullptr))) return false;
        DDSURFACEDESC actualPrimary{};
        actualPrimary.dwSize = sizeof(actualPrimary);
        if (!check("GetPrimaryDesc", primary->GetSurfaceDesc(&actualPrimary))) return false;
        std::fprintf(log, "primary=%lux%lu bits=%lu\n", actualPrimary.dwWidth, actualPrimary.dwHeight, actualPrimary.ddpfPixelFormat.dwRGBBitCount);
        if (wrapperVirtual && (actualPrimary.dwWidth != imageWidth || actualPrimary.dwHeight != imageHeight)) {
            note("virtual-primary-is-not-800x600", E_FAIL); return false;
        }
        DDSURFACEDESC sourceDesc{};
        sourceDesc.dwSize = sizeof(sourceDesc);
        sourceDesc.dwFlags = DDSD_CAPS | DDSD_WIDTH | DDSD_HEIGHT;
        sourceDesc.dwWidth = imageWidth;
        sourceDesc.dwHeight = imageHeight;
        sourceDesc.ddsCaps.dwCaps = DDSCAPS_OFFSCREENPLAIN | DDSCAPS_SYSTEMMEMORY;
        if (!check("CreateOffscreen", draw->CreateSurface(&sourceDesc, &offscreen, nullptr)) || !fillSurface(offscreen)) return false;
        if (!wrapperVirtual) {
            if (!check("CreateClipper", draw->CreateClipper(0, &clipper, nullptr)) ||
                !check("ClipperSetHWnd", clipper->SetHWnd(0, window)) || !check("SetClipper", primary->SetClipper(clipper))) return false;
        }
        return true;
    }
    void render() {
        if (!primary || !offscreen || IsIconic(window)) return;
        RECT client{};
        GetClientRect(window, &client);
        if (client.right <= 0 || client.bottom <= 0) return;
        RECT source{0, 0, imageWidth, imageHeight};
        HRESULT result = S_OK;
        if (wrapperVirtual) {
            // Full 800x600 application frame; the wrapper alone scales it and supplies bars.
            result = primary->BltFast(0, 0, offscreen, &source, DDBLTFAST_WAIT);
        } else {
            POINT origin{0, 0};
            ClientToScreen(window, &origin);
            RECT whole{origin.x, origin.y, origin.x + client.right, origin.y + client.bottom};
            if (hostFitControl) {
                // Control only: this branch cannot establish wrapper aspect correction.
                const Viewport viewport = fitViewport(client.right, client.bottom);
                DDBLTFX fill{};
                fill.dwSize = sizeof(fill);
                result = primary->Blt(&whole, nullptr, nullptr, DDBLT_COLORFILL | DDBLT_WAIT, &fill);
                RECT target{origin.x + viewport.x, origin.y + viewport.y, origin.x + viewport.x + viewport.width, origin.y + viewport.y + viewport.height};
                if (SUCCEEDED(result)) result = primary->Blt(&target, offscreen, &source, DDBLT_WAIT, nullptr);
            } else {
                // Native-windowed game shape: client rect offset to screen, no host aspect correction.
                result = primary->Blt(&whole, offscreen, &source, DDBLT_WAIT, nullptr);
            }
        }
        if (FAILED(result)) { check("present", result); running = false; }
        ++frames;
    }
    bool capture(const wchar_t* name) {
        RECT rect{};
        GetClientRect(window, &rect);
        if (rect.right <= 0 || rect.bottom <= 0) return false;
        HDC source = GetDC(window);
        HDC destination = CreateCompatibleDC(source);
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(info.bmiHeader);
        info.bmiHeader.biWidth = rect.right;
        info.bmiHeader.biHeight = -rect.bottom;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        void* pixels = nullptr;
        HBITMAP bitmap = CreateDIBSection(source, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
        if (!source || !destination || !bitmap) { if (bitmap) DeleteObject(bitmap); if (destination) DeleteDC(destination); if (source) ReleaseDC(window, source); return false; }
        const HGDIOBJ previous = SelectObject(destination, bitmap);
        // Print only this owned synthetic window, including compositor-backed content when supported.
        const bool printed = PrintWindow(window, destination, PW_CLIENTONLY | 0x00000002) != FALSE;
        const bool copied = printed || BitBlt(destination, 0, 0, rect.right, rect.bottom, source, 0, 0, SRCCOPY) != FALSE;
        std::fprintf(log, "capture-method=%s\n", printed ? "PrintWindow-client-renderfullcontent" : "BitBlt-owned-window-DC");
        bool verified = copied;
        const Viewport viewport = fitViewport(rect.right, rect.bottom);
        const auto sample = [&](int x, int y) {
            const DWORD value = static_cast<const DWORD*>(pixels)[static_cast<size_t>(y) * rect.right + x];
            return RGB((value >> 16) & 255, (value >> 8) & 255, value & 255);
        };
        // Compare interior points, away from grid edges, with tolerance for RGB565 conversion.
        for (int quadrant = 0; quadrant < 4; ++quadrant) {
            const int imageX = (quadrant % 2 == 0) ? 151 : 651;
            const int imageY = (quadrant < 2) ? 151 : 451;
            const int x = viewport.x + imageX * viewport.width / imageWidth;
            const int y = viewport.y + imageY * viewport.height / imageHeight;
            const COLORREF actual = sample(x, y);
            const COLORREF expected = patternColor(imageX, imageY);
            const bool matches = std::abs(static_cast<int>(GetRValue(actual)) - GetRValue(expected)) <= 12 &&
                std::abs(static_cast<int>(GetGValue(actual)) - GetGValue(expected)) <= 12 &&
                std::abs(static_cast<int>(GetBValue(actual)) - GetBValue(expected)) <= 12;
            verified = verified && matches;
            std::fprintf(log, "sample=%d x=%d y=%d rgb=%06lX expected=%06lX matches=%d\n", quadrant, x, y, actual, expected, matches ? 1 : 0);
        }
        const auto black = [&](int x, int y) { const COLORREF c = sample(x, y); return GetRValue(c) <= 12 && GetGValue(c) <= 12 && GetBValue(c) <= 12; };
        if (viewport.x > 2) verified = black(viewport.x / 2, rect.bottom / 2) && black(rect.right - 1 - viewport.x / 2, rect.bottom / 2) && verified;
        if (viewport.y > 2) verified = black(rect.right / 2, viewport.y / 2) && black(rect.right / 2, rect.bottom - 1 - viewport.y / 2) && verified;
        int firstX = rect.right;
        int lastX = -1;
        int firstY = rect.bottom;
        int lastY = -1;
        for (int x = 0; x < rect.right; ++x) {
            if (!black(x, rect.bottom / 2)) { firstX = std::min(firstX, x); lastX = x; }
        }
        for (int y = 0; y < rect.bottom; ++y) {
            if (!black(rect.right / 2, y)) { firstY = std::min(firstY, y); lastY = y; }
        }
        const bool extentMatches = std::abs(firstX - viewport.x) <= 2 && std::abs(firstY - viewport.y) <= 2 &&
            std::abs(lastX - (viewport.x + viewport.width - 1)) <= 2 && std::abs(lastY - (viewport.y + viewport.height - 1)) <= 2;
        verified = verified && extentMatches;
        std::fprintf(log, "observed-content-extent=%d,%d..%d,%d expected=%d,%d..%d,%d matches=%d\n", firstX, firstY, lastX, lastY, viewport.x, viewport.y, viewport.x + viewport.width - 1, viewport.y + viewport.height - 1, extentMatches ? 1 : 0);
        BITMAPFILEHEADER header{};
        header.bfType = 0x4D42;
        header.bfOffBits = sizeof(header) + sizeof(info.bmiHeader);
        const DWORD imageBytes = static_cast<DWORD>(rect.right) * static_cast<DWORD>(rect.bottom) * 4;
        header.bfSize = header.bfOffBits + imageBytes;
        const std::filesystem::path path = std::filesystem::path(output) / (std::wstring(name) + L".bmp");
        FILE* file = nullptr;
        if (_wfopen_s(&file, path.c_str(), L"wb") == 0 && file) {
            const bool written = std::fwrite(&header, sizeof(header), 1, file) == 1 && std::fwrite(&info.bmiHeader, sizeof(info.bmiHeader), 1, file) == 1 && std::fwrite(pixels, imageBytes, 1, file) == 1;
            std::fclose(file);
            verified = verified && written;
        } else verified = false;
        std::fprintf(log, "capture=%ls client=%ldx%ld viewport=%d,%d,%d,%d verified=%d elapsed=%.6f frames=%llu\n", name, rect.right, rect.bottom, viewport.x, viewport.y, viewport.width, viewport.height, verified ? 1 : 0, elapsed(start), frames);
        std::fflush(log);
        SelectObject(destination, previous);
        DeleteObject(bitmap);
        DeleteDC(destination);
        ReleaseDC(window, source);
        return verified;
    }
    bool captureWithObserver(const wchar_t* name) {
        // The child never loads ddraw. Measurements cannot be altered by wrapper IAT hooks.
        const auto executable = observerPath.empty() ? std::filesystem::path(executableDirectory()) / L"DisplayObserver.exe" : std::filesystem::path(observerPath);
        const std::wstring command = L"\"" + executable.wstring() + L"\" --output \"" + output + L"\" --observe-client " +
            std::to_wstring(reinterpret_cast<std::uintptr_t>(window)) + L" --expected-pid " + std::to_wstring(GetCurrentProcessId()) + L" --capture-name " + name;
        std::vector<wchar_t> mutableCommand(command.begin(), command.end());
        mutableCommand.push_back(L'\0');
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(executable.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, executable.parent_path().c_str(), &startup, &process)) {
            note("observer-create", HRESULT_FROM_WIN32(GetLastError())); return false;
        }
        DWORD wait = WAIT_TIMEOUT;
        LARGE_INTEGER observerStart{};
        QueryPerformanceCounter(&observerStart);
        while (elapsed(observerStart) < 5.0) {
            wait = WaitForSingleObject(process.hProcess, 0);
            if (wait != WAIT_TIMEOUT) break;
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            render();
            Sleep(1);
        }
        DWORD result = 1;
        if (wait == WAIT_OBJECT_0) GetExitCodeProcess(process.hProcess, &result);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        std::fprintf(log, "observer=%ls wait=%lu exit=%lu\n", name, wait, result);
        return wait == WAIT_OBJECT_0 && result == 0;
    }
    void applyStage(int value) {
        const auto wrapperResize = [&](int width, int height) {
            // These messages are a test seam into this component's own drag/sizing handler.
            // The wrapper intentionally ignores application SetWindowPos and ShowWindow(MAXIMIZE).
            RECT proposed{60, 60, 60 + width, 60 + height};
            AdjustWindowRectEx(&proposed, static_cast<DWORD>(GetWindowLongPtrW(window, GWL_STYLE)), FALSE, static_cast<DWORD>(GetWindowLongPtrW(window, GWL_EXSTYLE)));
            SendMessageW(window, WM_ENTERSIZEMOVE, 0, 0);
            SendMessageW(window, WM_SIZING, WMSZ_BOTTOMRIGHT, reinterpret_cast<LPARAM>(&proposed));
            SendMessageW(window, WM_EXITSIZEMOVE, 0, 0);
            std::fprintf(log, "sizing-request=%dx%d proposed-window=%ld,%ld,%ld,%ld\n", width, height, proposed.left, proposed.top, proposed.right, proposed.bottom);
        };
        // Values below are the pinned wrapper's named window messages, not global input.
        constexpr UINT wrapperToggleFullscreen = WM_APP + 117;
        constexpr WPARAM wrapperSetFullscreen = 1;
        constexpr WPARAM wrapperSetWindowed = 2;
        if (value == 1) wrapperResize(1000, 700);
        if (value == 2) wrapperResize(700, 700);
        if (value == 3) SendMessageW(window, WM_SYSCOMMAND, SC_MAXIMIZE, 0);
        if (value == 4) SendMessageW(window, wrapperToggleFullscreen, wrapperSetFullscreen, 0);
        if (value == 5) { SendMessageW(window, wrapperToggleFullscreen, wrapperSetWindowed, 0); wrapperResize(800, 600); }
        QueryPerformanceCounter(&stageStart);
        note("stage-applied");
    }
    void advance() {
        if (stage < 0) { stage = 0; applyStage(stage); return; }
        if (interactive) {
            if (elapsed(start) >= interactiveLimit) { interactiveEnd = "timeout"; running = false; }
            return;
        }
        if (elapsed(stageStart) < stageSeconds) return;
        const wchar_t* names[] = {L"01-window-800x600", L"02-window-1000x700", L"03-window-700x700", L"04-maximized", L"05-borderless", L"06-restored"};
        if (!captureWithObserver(names[stage])) captureFailed = true;
        ++stage;
        if (stage >= 6) { running = false; return; }
        applyStage(stage);
    }
};
LRESULT CALLBACK windowProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    auto* app = reinterpret_cast<App*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        app = static_cast<App*>(create->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(app));
    }
    if (app && message == WM_CLOSE) { app->interactiveEnd = "window-close"; app->running = false; return 0; }
    if (app && app->interactive && message == WM_KEYDOWN) {
        if (wparam == VK_ESCAPE) { app->interactiveEnd = "escape"; app->running = false; return 0; }
        if (wparam == 'N') {
            app->stage = (app->stage + 1) % 6;
            app->applyStage(app->stage);
            std::fprintf(app->log, "interactive-next-stage=%d elapsed=%.6f\n", app->stage, app->elapsed(app->start));
            std::fflush(app->log);
            return 0;
        }
    }
    if (app && app->log && (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP)) {
        // These imported APIs reproduce the game's input-query pattern, without setting the cursor.
        POINT cursor{};
        POINT origin{};
        const bool valid = GetCursorPos(&cursor) != FALSE && ClientToScreen(window, &origin) != FALSE;
        std::fprintf(app->log, "left-click=%s stage=%d received=%d,%d hooked-cursor=%ld,%ld hooked-origin=%ld,%ld cursor-minus-origin=%ld,%ld valid=%d elapsed=%.6f\n",
            message == WM_LBUTTONDOWN ? "down" : "up", app->stage, GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam),
            cursor.x, cursor.y, origin.x, origin.y, cursor.x - origin.x, cursor.y - origin.y, valid ? 1 : 0, app->elapsed(app->start));
        std::fflush(app->log);
    }
    if (app && message == WM_MOUSEMOVE && app->log) {
        RECT client{};
        GetClientRect(window, &client);
        const POINT received{GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
        POINT mapped{};
        const bool hit = imagePoint(fitViewport(client.right, client.bottom), received, mapped);
        // These are received application coordinates, not an independent physical-input oracle.
        std::fprintf(app->log, "mouse-received=%ld,%ld host-fit-hit=%d host-fit-image=%ld,%ld\n", received.x, received.y, hit ? 1 : 0, mapped.x, mapped.y);
    }
    if (message == WM_ERASEBKGND) return 1;
    return DefWindowProcW(window, message, wparam, lparam);
}
bool geometryChecks(FILE* log) {
    struct Case { int width; int height; Viewport expected; };
    const Case cases[] = {{800, 600, {0, 0, 800, 600}}, {1920, 1080, {240, 0, 1440, 1080}}, {1000, 700, {33, 0, 933, 700}}, {700, 700, {0, 87, 700, 525}}, {0, 600, {0, 0, 0, 0}}};
    bool passed = true;
    for (const auto& test : cases) {
        const Viewport actual = fitViewport(test.width, test.height);
        const bool matches = actual.x == test.expected.x && actual.y == test.expected.y && actual.width == test.expected.width && actual.height == test.expected.height;
        passed = passed && matches;
        std::fprintf(log, "geometry=%dx%d result=%d,%d,%d,%d pass=%d\n", test.width, test.height, actual.x, actual.y, actual.width, actual.height, matches ? 1 : 0);
    }
    const Viewport viewport = fitViewport(1920, 1080);
    POINT image{};
    passed = imagePoint(viewport, {240, 0}, image) && image.x == 0 && image.y == 0 && passed;
    passed = imagePoint(viewport, {960, 540}, image) && image.x == 400 && image.y == 300 && passed;
    passed = imagePoint(viewport, {1679, 1079}, image) && image.x == 799 && image.y == 599 && passed;
    passed = !imagePoint(viewport, {239, 540}, image) && !imagePoint(viewport, {1680, 540}, image) && passed;
    std::fprintf(log, "geometry-and-host-mapping=%s; wrapper-input-unverified\n", passed ? "PASS" : "FAIL");
    return passed;
}
}
int wmain(int argc, wchar_t** argv) {
    App app;
    bool allowVirtual = false;
    bool mathOnly = false;
    HWND observeWindow = nullptr;
    DWORD expectedProcessId = 0;
    std::wstring captureName;
    DWORD watchProcessId = 0;
    DWORD watchMilliseconds = 180000;
    std::wstring expectedExecutable;
    for (int index = 1; index < argc; ++index) {
        if (std::wcscmp(argv[index], L"--dll") == 0 && index + 1 < argc) app.dll = argv[++index];
        else if (std::wcscmp(argv[index], L"--observer-path") == 0 && index + 1 < argc) app.observerPath = argv[++index];
        else if (std::wcscmp(argv[index], L"--output") == 0 && index + 1 < argc) app.output = argv[++index];
        else if (std::wcscmp(argv[index], L"--wrapper-virtual") == 0) app.wrapperVirtual = true;
        else if (std::wcscmp(argv[index], L"--direct-create-control") == 0) app.comActivation = false;
        else if (std::wcscmp(argv[index], L"--host-fit-control") == 0) app.hostFitControl = true;
        else if (std::wcscmp(argv[index], L"--verified-windowed-profile") == 0) allowVirtual = true;
        else if (std::wcscmp(argv[index], L"--math-only") == 0) mathOnly = true;
        else if (std::wcscmp(argv[index], L"--interactive") == 0) app.interactive = true;
        else if (std::wcscmp(argv[index], L"--watch-owned-process") == 0 && index + 1 < argc) watchProcessId = std::wcstoul(argv[++index], nullptr, 10);
        else if (std::wcscmp(argv[index], L"--expected-executable") == 0 && index + 1 < argc) expectedExecutable = argv[++index];
        else if (std::wcscmp(argv[index], L"--watch-seconds") == 0 && index + 1 < argc) {
            const unsigned long seconds = std::wcstoul(argv[++index], nullptr, 10);
            if (seconds == 0 || seconds > 180) return 2;
            watchMilliseconds = seconds * 1000;
        }
        else if (std::wcscmp(argv[index], L"--interactive-seconds") == 0 && index + 1 < argc) {
            app.interactiveLimit = std::wcstod(argv[++index], nullptr);
            if (!std::isfinite(app.interactiveLimit) || app.interactiveLimit < 1.0 || app.interactiveLimit > 180.0) return 2;
        }
        else if (std::wcscmp(argv[index], L"--observe-client") == 0 && index + 1 < argc) observeWindow = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(std::wcstoull(argv[++index], nullptr, 10)));
        else if (std::wcscmp(argv[index], L"--expected-pid") == 0 && index + 1 < argc) expectedProcessId = std::wcstoul(argv[++index], nullptr, 10);
        else if (std::wcscmp(argv[index], L"--capture-name") == 0 && index + 1 < argc) captureName = argv[++index];
        else if (std::wcscmp(argv[index], L"--stage-seconds") == 0 && index + 1 < argc) {
            app.stageSeconds = std::wcstod(argv[++index], nullptr);
            if (!std::isfinite(app.stageSeconds) || app.stageSeconds < 0.1 || app.stageSeconds > 10.0) return 2;
        }
        else { std::fwprintf(stderr, L"Unknown or incomplete argument: %ls\n", argv[index]); return 2; }
    }
    if (app.output.empty() || !std::filesystem::is_directory(app.output)) { std::fwprintf(stderr, L"Supply an existing ignored --output directory.\n"); return 2; }
    if (!captureName.empty() && captureName.find_first_not_of(L"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-") != std::wstring::npos) return 2;
    const auto logPath = std::filesystem::path(app.output) / (watchProcessId ? L"watchdog.log" : observeWindow ? captureName + L"-capture.log" : L"probe.log");
    if (_wfopen_s(&app.log, logPath.c_str(), L"wb") != 0 || !app.log) return 2;
    QueryPerformanceFrequency(&app.frequency);
    QueryPerformanceCounter(&app.start);
    SetProcessDPIAware();
    if (watchProcessId) {
#ifdef ASPECT_PROBE_OBSERVER
        return watchOwnedProcess(watchProcessId, expectedExecutable, watchMilliseconds, app.log);
#else
        std::fprintf(app.log, "REFUSED: watchdog requires the build without a WinMM import.\n");
        return 2;
#endif
    }
    if (observeWindow) {
        DWORD windowProcessId = 0;
        wchar_t className[128]{};
        GetWindowThreadProcessId(observeWindow, &windowProcessId);
        GetClassNameW(observeWindow, className, static_cast<int>(std::size(className)));
        if (expectedProcessId == 0 || windowProcessId != expectedProcessId || std::wcscmp(className, L"OriginalAspectSyntheticProbe") != 0 || captureName.empty()) {
            std::fprintf(app.log, "REFUSED: observer target is not the declared synthetic probe window.\n"); return 2;
        }
        app.window = observeWindow;
        return app.capture(captureName.c_str()) ? 0 : 1;
    }
    const bool geometryPassed = geometryChecks(app.log);
    if (mathOnly) return geometryPassed ? 0 : 1;
#ifdef ASPECT_PROBE_OBSERVER
    std::fprintf(app.log, "REFUSED: observer build cannot load a graphics component.\n");
    return 2;
#else
    // A real, retained static import causes the approved app-local winmm bootstrap to load before main.
    std::fprintf(app.log, "bootstrap-timeGetTime=%lu\n", timeGetTime());
    if (app.dll.empty() || !std::filesystem::path(app.dll).is_absolute()) { std::fprintf(app.log, "REFUSED: explicit absolute --dll required.\n"); return 2; }
    if (app.wrapperVirtual && !allowVirtual) { std::fprintf(app.log, "REFUSED: virtual fullscreen needs externally verified windowed wrapper profile.\n"); return 2; }
    // A virtual profile is safe only after the coordinator verifies wrapper, hash and config.
    // This opt-in is evidence of that external check, not an automatic correctness claim.
    std::fprintf(app.log, "mode=%s activation=%s; game-compatibility=UNTESTED; wrapper-input=UNVERIFIED\n", app.wrapperVirtual ? "wrapper-scaled-virtual" : app.hostFitControl ? "host-scaled-control" : "wrapper-native-windowed", app.comActivation ? "CoCreateInstance" : "DirectDrawCreate-control");
    app.initialMode.dmSize = sizeof(app.initialMode);
    EnumDisplaySettingsW(nullptr, ENUM_CURRENT_SETTINGS, &app.initialMode);
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = windowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"OriginalAspectSyntheticProbe";
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    if (!RegisterClassW(&windowClass)) return 3;
    const std::wstring title = L"Synthetic Display Probe (PID " + std::to_wstring(GetCurrentProcessId()) + L") - N next stage, Esc close";
    app.window = CreateWindowExW(0, windowClass.lpszClassName, title.c_str(), WS_OVERLAPPEDWINDOW, 60, 60, 816, 639, nullptr, nullptr, windowClass.hInstance, &app);
    if (!app.window) return 3;
    ShowWindow(app.window, SW_SHOWNOACTIVATE);
    if (!app.initialize()) { DestroyWindow(app.window); return 4; }
    if (app.interactive) {
        app.interactiveEnd = "running";
        std::fprintf(app.log, "interactive=1 max-seconds=%.3f pid=%lu controls=N-next-stage,Esc-close stage-order=startup,resize1000,minimum,maximize,borderless,restore\n", app.interactiveLimit, GetCurrentProcessId());
        std::fflush(app.log);
    }
    MSG message{};
    while (app.running) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) app.running = false;
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        app.render();
        app.advance();
        // Presentation workload only. No inferred game tick or game FPS setting.
        Sleep(1);
    }
    DEVMODEW finalMode{};
    finalMode.dmSize = sizeof(finalMode);
    const bool modeRead = EnumDisplaySettingsW(nullptr, ENUM_CURRENT_SETTINGS, &finalMode) != FALSE;
    const bool unchanged = modeRead && finalMode.dmPelsWidth == app.initialMode.dmPelsWidth && finalMode.dmPelsHeight == app.initialMode.dmPelsHeight && finalMode.dmBitsPerPel == app.initialMode.dmBitsPerPel && finalMode.dmDisplayFrequency == app.initialMode.dmDisplayFrequency;
    std::fprintf(app.log, "display-mode-unchanged=%d start=%lux%lu/%lu/%lu end=%lux%lu/%lu/%lu\n", unchanged ? 1 : 0, app.initialMode.dmPelsWidth, app.initialMode.dmPelsHeight, app.initialMode.dmBitsPerPel, app.initialMode.dmDisplayFrequency, finalMode.dmPelsWidth, finalMode.dmPelsHeight, finalMode.dmBitsPerPel, finalMode.dmDisplayFrequency);
    if (app.interactive) {
        std::fprintf(app.log, "RESULT interactive-end=%s stage=%d graphics-operations=%d display-mode-unchanged=%d pixel-check=EXTERNAL physical-input-check=EXTERNAL game-speed=UNTESTED\n", app.interactiveEnd, app.stage, app.failed ? 0 : 1, unchanged ? 1 : 0);
        DestroyWindow(app.window);
        return !app.failed && unchanged ? 0 : 1;
    }
    const bool complete = app.stage == 6 && app.frames > 0;
    std::fprintf(app.log, "RESULT complete=%d geometry=%d graphics-operations=%d captures=%d display-mode-unchanged=%d game-speed=UNTESTED input-physical=UNVERIFIED\n", complete ? 1 : 0, geometryPassed ? 1 : 0, app.failed ? 0 : 1, app.captureFailed ? 0 : 1, unchanged ? 1 : 0);
    DestroyWindow(app.window);
    return complete && geometryPassed && !app.failed && !app.captureFailed && unchanged ? 0 : 1;
#endif
}
