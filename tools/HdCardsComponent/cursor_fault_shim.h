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

int WINAPI cursor_fault_save(HDC);
BOOL WINAPI cursor_fault_restore(HDC,int);
int WINAPI cursor_fault_exclude(HDC,int,int,int,int);
int WINAPI cursor_fault_clip(HDC,int,int,int,int);
int WINAPI cursor_fault_mode(HDC,int);
BOOL WINAPI cursor_fault_visible(HDC,const RECT*);
#define SaveDC cursor_fault_save
#define RestoreDC cursor_fault_restore
#define ExcludeClipRect cursor_fault_exclude
#define IntersectClipRect cursor_fault_clip
#define SetStretchBltMode cursor_fault_mode
#define RectVisible cursor_fault_visible
