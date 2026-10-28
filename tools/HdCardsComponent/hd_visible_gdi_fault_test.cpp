/* Own memory-DC fault injection; no provider, game, GUI or display driver. */
#define wmain normal_synthetic_main
#include "hd_visible_gdi_test.cpp"
#undef wmain
namespace {
int mode=0,stretches=0,natives=0,saves=0,restores=0,clips=0;bool native_geometry=true;
const void* first_owned=nullptr;bool same_owned=true;
void reset(int value){mode=value;stretches=0;natives=0;saves=0;restores=0;clips=0;native_geometry=true;first_owned=nullptr;same_owned=true;}
bool equal(Dib& a,Dib& b){GdiFlush();for(int y=0;y<a.h;++y)for(int x=0;x<a.w;++x)if((a.pixels[size_t(y)*a.w+x]&0xffffff)!=(b.pixels[size_t(y)*b.w+x]&0xffffff))return false;return true;}
void base_oracle(Dib& d,const std::vector<unsigned char>& source,bool native=false){
    struct B {BITMAPINFOHEADER h;DWORD masks[3];} b{};
    b.h.biSize=sizeof(BITMAPINFOHEADER);b.h.biWidth=800;b.h.biHeight=-600;b.h.biPlanes=1;b.h.biBitCount=16;b.h.biCompression=BI_BITFIELDS;
    b.masks[0]=0xf800;b.masks[1]=0x07e0;b.masks[2]=0x001f;
    if(native)::SetDIBitsToDevice(d.dc,0,0,800,600,0,0,0,600,source.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS);
    else ::StretchDIBits(d.dc,9,11,1601,1201,0,0,800,600,source.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY);
}
}
int WINAPI hdvg_fault_stretch(HDC dc,int x,int y,int w,int h,int sx,int sy,int sw,int sh,const void* bits,const BITMAPINFO* bmi,UINT use,DWORD rop){
    ++stretches;
    if(bmi->bmiHeader.biBitCount==16){
        if(!first_owned)first_owned=bits;else same_owned=same_owned&&first_owned==bits;
        if(stretches==1&&(mode==1||mode==2||mode==3||mode==4||mode==15))return (mode==3||mode==4)?GDI_ERROR:0;
        if(stretches>=3&&(mode==7||mode==8)){SetPixelV(dc,40,200,RGB(255,0,255));return mode==8?GDI_ERROR:0;}
    }
    if(bmi->bmiHeader.biBitCount==32&&((mode>=5&&mode<=8)||mode==16)){SetPixelV(dc,x+1,y+1,RGB(255,0,255));return mode==6?GDI_ERROR:0;}
    return ::StretchDIBits(dc,x,y,w,h,sx,sy,sw,sh,bits,bmi,use,rop);
}
int WINAPI hdvg_fault_native(HDC dc,int x,int y,DWORD w,DWORD h,int sx,int sy,UINT start,UINT lines,const void* bits,const BITMAPINFO* bmi,UINT use){
    ++natives;native_geometry=native_geometry&&x==0&&y==0&&w==800&&h==600&&sx==0&&sy==0&&start==0&&lines==600;
    same_owned=same_owned&&bits==first_owned;
    if(mode==2||mode==4)return 0;
    return ::SetDIBitsToDevice(dc,x,y,w,h,sx,sy,start,lines,bits,bmi,use);
}
int WINAPI hdvg_fault_save(HDC dc){++saves;if(mode==9||(mode==16&&saves==2))return 0;return ::SaveDC(dc);}
BOOL WINAPI hdvg_fault_restore(HDC dc,int saved){++restores;if((mode==14||mode==15)&&restores==1)return FALSE;return ::RestoreDC(dc,saved);}
int WINAPI hdvg_fault_clip(HDC dc,int x,int y,int r,int b){++clips;if((mode==11&&clips==1)||(mode==13&&clips==2))return ERROR;return ::IntersectClipRect(dc,x,y,r,b);}
int WINAPI hdvg_fault_clipbox(HDC dc,LPRECT rc){return mode==10?ERROR: ::GetClipBox(dc,rc);}
int WINAPI hdvg_fault_mode(HDC dc,int value){return mode==12?0: ::SetStretchBltMode(dc,value);}
BOOL WINAPI hdvg_fault_brush(HDC dc,int x,int y,LPPOINT old){return mode==17?FALSE: ::SetBrushOrgEx(dc,x,y,old);}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;dir=argv[1];checks=0;failures=0;
    auto f=input(1,2,74);HdvGdi* p=hdvg_create();check(configure(p,L"joey.tsv"),"fault context configure");
    Dib d(1640,1240),oracle(1640,1240);
    SetStretchBltMode(d.dc,HALFTONE);SetStretchBltMode(oracle.dc,HALFTONE);
    SetBrushOrgEx(d.dc,3,7,nullptr);SetBrushOrgEx(oracle.dc,3,7,nullptr);
    auto present=[&]{return draw(p,d,f,9,11,1601,1201);};
    for(const wchar_t* bad:{L"badzlib.tsv",L"badadler.tsv",L"truncated.tsv",L"baddeflate.tsv",L"trailingidat.tsv",L"trailingempty.tsv",L"zipbomb.tsv"}){
        reset(0);d.clear();oracle.clear();base_oracle(oracle,f);
        check(configure(p,bad),"malformed compressed fixture config admitted");
        check(present()==1,"strict malformed compressed stream never overlays");
        check(equal(d,oracle),"malformed PNG every pixel equals independent original base");
        check(stretches==1,"malformed PNG does not issue overlay draw");
    }
    check(configure(p,L"joey.tsv"),"valid master recovers after malformed streams");
    for(int value:{1,3}){
        reset(value);d.clear();oracle.clear();base_oracle(oracle,f,true);
        check(present()==1,"failed base stretch + successful native fallback");
        check(stretches==1&&natives==1&&native_geometry,"exact native fallback geometry, no overlay");
        check(same_owned&&first_owned!=f.data(),"fallback uses same owned snapshot");
        check(equal(d,oracle),"native success all pixels independently checked");
    }
    for(int value:{2,4}){
        reset(value);d.clear();oracle.clear();check(present()==0,"both base calls fail => caller fallback");
        check(stretches==1&&natives==1&&native_geometry,"failed native target preserved");
        check(equal(d,oracle),"both failed no false paint claim");
    }
    for(int value:{5,6}){
        reset(value);d.clear();oracle.clear();base_oracle(oracle,f);
        check(present()==1,"partial overlay failure restored original base");
        check(stretches==3&&natives==0,"base overlay repair, no native fallback");
        check(same_owned,"repair uses same owned frame");
        check(equal(d,oracle),"every pixel equals base after failed overlay");
        check(GetStretchBltMode(d.dc)==HALFTONE,"repair restores base filter/DC mode");
    }
    for(int value:{7,8}){
        reset(value);d.clear();check(present()==0,"partial overlay and failed base repair => caller fallback");
        check(stretches==3&&natives==0,"repair failure no false native geometry substitution");
        oracle.clear();base_oracle(oracle,f);base_oracle(d,f);check(equal(d,oracle),"caller original fallback repairs reported failure");
    }
    for(int value:{9,10,11}){
        reset(value);d.clear();oracle.clear();check(present()==0,"state setup failure => caller fallback");
        check(equal(d,oracle),"failed state setup paints nothing");
        check(GetStretchBltMode(d.dc)==HALFTONE,"failed setup state restored");
    }
    for(int value:{12,17}){
        reset(value);d.clear();oracle.clear();base_oracle(oracle,f);
        check(present()==1&&stretches==1,"overlay filter/brush setup failure keeps base");
        check(equal(d,oracle),"filter/brush failure every original base pixel intact");
        check(GetStretchBltMode(d.dc)==HALFTONE,"filter/brush failure restores caller mode");
        POINT brush{};check(GetBrushOrgEx(d.dc,&brush)&&brush.x==3&&brush.y==7,"filter/brush failure restores caller brush origin");
    }
    for(int value:{13,14,15,16}){
        reset(value);d.clear();check(present()==0,"clip/state/repair setup failure never claims success");
        check(GetStretchBltMode(d.dc)==HALFTONE,"state failure restoration retry");
        POINT brush{};check(GetBrushOrgEx(d.dc,&brush)&&brush.x==3&&brush.y==7,"state failure restores caller brush origin");
        oracle.clear();base_oracle(oracle,f);base_oracle(d,f);check(equal(d,oracle),"reported state failure caller fallback restores every pixel");
    }
    HRGN empty=CreateRectRgn(0,0,0,0);SelectClipRgn(d.dc,empty);reset(2);
    check(present()==3,"known empty caller clip explicit no-op result");
    check(stretches==0&&natives==0,"known empty clip invokes no failing driver calls");
    SelectClipRgn(d.dc,nullptr);DeleteObject(empty);
    hdvg_shutdown(p);check(present()==0,"shutdown after injected failures");hdvg_destroy(p);
    std::printf("%d fault checks, %d failures\n",checks,failures);return failures?1:0;
}
