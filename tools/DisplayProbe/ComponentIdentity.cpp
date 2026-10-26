// DirectDraw object provenance check using Microsoft SDK interface layout.
// No game layout or handwritten COM ABI is used.
#define WIN32_LEAN_AND_MEAN
#define CINTERFACE
#define DIRECTDRAW_VERSION 0x0300
#include <windows.h>
#include <ddraw.h>

namespace {
template<class Function>
bool ownedByModule(Function method, HMODULE expected) {
    HMODULE actual = nullptr;
    const DWORD flags = GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
    return GetModuleHandleExW(flags, reinterpret_cast<LPCWSTR>(method), &actual) != FALSE && actual == expected;
}
}
bool verifiedDrawProvider(IDirectDraw* draw, HMODULE expected) {
    if (!draw || !draw->lpVtbl || !expected) return false;
    return ownedByModule(draw->lpVtbl->SetCooperativeLevel, expected) &&
        ownedByModule(draw->lpVtbl->SetDisplayMode, expected) &&
        ownedByModule(draw->lpVtbl->CreateSurface, expected);
}
