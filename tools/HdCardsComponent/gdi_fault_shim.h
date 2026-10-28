#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
int WINAPI hdvg_fault_stretch(HDC,int,int,int,int,int,int,int,int,const void*,const BITMAPINFO*,UINT,DWORD);
int WINAPI hdvg_fault_native(HDC,int,int,DWORD,DWORD,int,int,UINT,UINT,const void*,const BITMAPINFO*,UINT);
#define StretchDIBits hdvg_fault_stretch
#define SetDIBitsToDevice hdvg_fault_native
int WINAPI hdvg_fault_save(HDC);
BOOL WINAPI hdvg_fault_restore(HDC,int);
int WINAPI hdvg_fault_clip(HDC,int,int,int,int);
int WINAPI hdvg_fault_clipbox(HDC,LPRECT);
int WINAPI hdvg_fault_mode(HDC,int);
BOOL WINAPI hdvg_fault_brush(HDC,int,int,LPPOINT);
#define SaveDC hdvg_fault_save
#define RestoreDC hdvg_fault_restore
#define IntersectClipRect hdvg_fault_clip
#define GetClipBox hdvg_fault_clipbox
#define SetStretchBltMode hdvg_fault_mode
#define SetBrushOrgEx hdvg_fault_brush
