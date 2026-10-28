#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
HDC WINAPI cursor_fault_dc(HDC);
HBITMAP WINAPI cursor_fault_dib(HDC,const BITMAPINFO*,UINT,void**,HANDLE,DWORD);
HGDIOBJ WINAPI cursor_fault_select(HDC,HGDIOBJ);
BOOL WINAPI cursor_fault_flush(void);
int WINAPI cursor_fault_stretch(HDC,int,int,int,int,int,int,int,int,const void*,const BITMAPINFO*,UINT,DWORD);
#define CreateCompatibleDC cursor_fault_dc
#define CreateDIBSection cursor_fault_dib
#define SelectObject cursor_fault_select
#define GdiFlush cursor_fault_flush
#define StretchDIBits cursor_fault_stretch
