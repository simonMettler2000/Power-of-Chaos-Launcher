/* Independent unhooked expected canvas. Never loads a provider or reads an
   observed frame. Input is only authored source pixels/master and geometry. */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <fstream>
#include <vector>
#include <string>
#include "gdi_window_oracle.h"
using Microsoft::WRL::ComPtr;
namespace {
bool read_master(const wchar_t* path,std::vector<unsigned char>& pixels) {
    ComPtr<IWICImagingFactory> f;ComPtr<IWICBitmapDecoder> d;ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICFormatConverter> c;UINT w=0,h=0;
    if(FAILED(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(f.GetAddressOf())))||FAILED(f->CreateDecoderFromFilename(path,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,d.GetAddressOf()))||FAILED(d->GetFrame(0,frame.GetAddressOf()))||FAILED(frame->GetSize(&w,&h))||w!=800||h!=1160||FAILED(f->CreateFormatConverter(c.GetAddressOf()))||FAILED(c->Initialize(frame.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom)))return false;
    pixels.resize(800*1160*4);return SUCCEEDED(c->CopyPixels(nullptr,3200,static_cast<UINT>(pixels.size()),pixels.data()));
}
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=5)return 2;
    HdvGdiOracleRequest r{};std::ifstream request(argv[1],std::ios::binary|std::ios::ate);
    if(!request||request.tellg()!=sizeof(r))return 3;request.seekg(0);if(!request.read(reinterpret_cast<char*>(&r),sizeof(r)))return 3;
    if(r.magic!=0x4f564448||r.version!=1||!r.width||!r.height||r.width>4096||r.height>4096||uint64_t(r.width)*r.height*4>16*1024*1024||r.format<1||r.format>2||r.vx<0||r.vy<0||r.vw<=0||r.vh<=0||uint64_t(r.vx)+r.vw>r.width||uint64_t(r.vy)+r.vh>r.height||r.rx<0||r.ry<0||r.rx>600||r.ry>310)return 3;
    std::vector<unsigned char> input(960000),art;std::ifstream source(argv[2],std::ios::binary|std::ios::ate);
    if(!source||source.tellg()!=960000)return 4;source.seekg(0);if(!source.read(reinterpret_cast<char*>(input.data()),960000))return 4;
    if(FAILED(CoInitializeEx(nullptr,COINIT_MULTITHREADED)))return 5;
    if(r.overlay&&!read_master(argv[3],art))return 5;
    SetProcessDPIAware();POINT origin{};HWND owner=reinterpret_cast<HWND>(static_cast<uintptr_t>(r.owner_window));
    if(!IsWindow(owner)||!ClientToScreen(owner,&origin))return 6;
    if(origin.x<0||origin.y<0||origin.x+int(r.width)>GetSystemMetrics(SM_CXSCREEN)||origin.y+int(r.height)>GetSystemMetrics(SM_CYSCREEN))return 6;
    if(GetModuleHandleW(L"ddraw.dll"))return 6;
    WNDCLASSW wc{};wc.style=CS_OWNDC;wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"AuthoredUnhookedExpectedGdiCanvas";
    if(!RegisterClassW(&wc))return 7;
    HWND window=CreateWindowW(wc.lpszClassName,L"Independent expected GDI canvas",WS_POPUP,origin.x,origin.y,int(r.width),int(r.height),nullptr,nullptr,wc.hInstance,nullptr);if(!window)return 7;
    ShowWindow(window,SW_SHOWNOACTIVATE);SetWindowPos(window,HWND_TOPMOST,origin.x,origin.y,int(r.width),int(r.height),SWP_NOACTIVATE);UpdateWindow(window);
    MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}Sleep(30);
    HDC dc=GetDC(window);if(!dc)return 8;const int saved=SaveDC(dc);
    if(!saved||!PatBlt(dc,0,0,int(r.width),int(r.height),BLACKNESS)||!SetStretchBltMode(dc,BLACKONWHITE))return 8;
    if(!r.black_only){
        if(r.backend_base&&IntersectClipRect(dc,r.vx,r.vy,r.vx+r.vw,r.vy+r.vh)==ERROR)return 8;
        struct Base{BITMAPINFOHEADER h;DWORD masks[3];} b{};b.h.biSize=sizeof(BITMAPINFOHEADER);b.h.biWidth=800;b.h.biHeight=-600;b.h.biPlanes=1;b.h.biBitCount=16;b.h.biCompression=BI_BITFIELDS;
        b.masks[0]=r.format==1?0xf800:0x7c00;b.masks[1]=r.format==1?0x07e0:0x03e0;b.masks[2]=0x001f;
        const int base=r.native_base?int(SetDIBitsToDevice(dc,0,0,800,600,0,0,0,600,input.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS)):StretchDIBits(dc,r.vx,r.vy,r.vw,r.vh,0,0,800,600,input.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY);
        if(base==0||base==GDI_ERROR)return 9;
        if(r.overlay){
            const int left=r.vx+r.rx*r.vw/800,top=r.vy+r.ry*r.vh/600,right=r.vx+(r.rx+200)*r.vw/800,bottom=r.vy+(r.ry+290)*r.vh/600;
            /* Declared smooth artwork policy, established before the provider
               is observed; this process never reads its output. */
            if(!SetStretchBltMode(dc,HALFTONE)||!SetBrushOrgEx(dc,0,0,nullptr)||IntersectClipRect(dc,left,top,right,bottom)==ERROR)return 9;
            BITMAPINFO m{};m.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);m.bmiHeader.biWidth=800;m.bmiHeader.biHeight=-1160;m.bmiHeader.biPlanes=1;m.bmiHeader.biBitCount=32;
            const int n=StretchDIBits(dc,left,top,right-left,bottom-top,0,0,800,1160,art.data(),&m,DIB_RGB_COLORS,SRCCOPY);if(n==0||n==GDI_ERROR)return 9;
        }
    }
    if(!RestoreDC(dc,saved)||!GdiFlush())return 10;
    HDC memory=CreateCompatibleDC(dc);BITMAPINFO m{};m.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);m.bmiHeader.biWidth=LONG(r.width);m.bmiHeader.biHeight=-LONG(r.height);m.bmiHeader.biPlanes=1;m.bmiHeader.biBitCount=32;void* bits=nullptr;
    HBITMAP bitmap=CreateDIBSection(dc,&m,DIB_RGB_COLORS,&bits,nullptr,0);if(!memory||!bitmap||!bits)return 10;HGDIOBJ prior=SelectObject(memory,bitmap);
    if(!prior||prior==HGDI_ERROR||!BitBlt(memory,0,0,int(r.width),int(r.height),dc,0,0,SRCCOPY)||!GdiFlush())return 10;
    std::ofstream out(argv[4],std::ios::binary);out.write(reinterpret_cast<const char*>(bits),std::streamsize(r.width)*r.height*4);if(!out)return 11;
    std::printf("Independent real-window oracle providerLoaded=0 screen=%ld,%ld size=%ux%u viewport=%d,%d,%d,%d mode=%d\n",origin.x,origin.y,r.width,r.height,r.vx,r.vy,r.vw,r.vh,GetStretchBltMode(dc));
    SelectObject(memory,prior);DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(window,dc);DestroyWindow(window);CoUninitialize();return 0;
}
