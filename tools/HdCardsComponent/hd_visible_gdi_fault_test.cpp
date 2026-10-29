/* Own memory-DC fault injection; no provider, game, GUI or display driver. */
#define wmain normal_synthetic_main
#include "hd_visible_gdi_test.cpp"
#undef wmain
#include <new>
namespace {
int mode=0,stretches=0,natives=0,saves=0,restores=0,clips=0;bool native_geometry=true;bool partial=false;int poison_x=0,poison_y=0;
HDC target_dc=nullptr;bool exception_injected=false;int draws_after_exception=0;
const void* first_owned=nullptr;bool same_owned=true;
void reset(int value){mode=value;stretches=0;natives=0;saves=0;restores=0;clips=0;native_geometry=true;partial=false;exception_injected=false;draws_after_exception=0;first_owned=nullptr;same_owned=true;}
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
    if(dc!=target_dc)return ::StretchDIBits(dc,x,y,w,h,sx,sy,sw,sh,bits,bmi,use,rop);
    ++stretches;
    if(exception_injected)++draws_after_exception;
    if(!exception_injected&&((bmi->bmiHeader.biBitCount==16&&(mode==24||mode==27))||
        (bmi->bmiHeader.biBitCount==32&&(mode==25||mode==28)))){
        poison_x=x+w/2;
        poison_y=bmi->bmiHeader.biBitCount==16?y+h/2:y+h-2;partial=::SetPixelV(dc,poison_x,poison_y,RGB(255,0,255))!=FALSE;
        exception_injected=true;throw std::bad_alloc();
    }
    if(bmi->bmiHeader.biBitCount==16){
        if(!first_owned)first_owned=bits;else same_owned=same_owned&&first_owned==bits;
        if(partial&&(mode==18||mode==19||mode==21))return mode==19?GDI_ERROR:0;
        if((mode>=1&&mode<=4)||(stretches==1&&mode==15))return (mode==3||mode==4)?GDI_ERROR:0;
        if(stretches>=3&&(mode==7||mode==8)){SetPixelV(dc,40,200,RGB(255,0,255));return mode==8?GDI_ERROR:0;}
    }
    if(bmi->bmiHeader.biBitCount==32&&mode>=18&&mode<=22){poison_x=x+w/2;poison_y=y+h-2;partial=SetPixelV(dc,poison_x,poison_y,RGB(255,0,255))!=FALSE;return 0;}
    if(bmi->bmiHeader.biBitCount==32&&((mode>=5&&mode<=8)||mode==16)){SetPixelV(dc,x+1,y+1,RGB(255,0,255));return mode==6?GDI_ERROR:0;}
    return ::StretchDIBits(dc,x,y,w,h,sx,sy,sw,sh,bits,bmi,use,rop);
}
int WINAPI hdvg_fault_native(HDC dc,int x,int y,DWORD w,DWORD h,int sx,int sy,UINT start,UINT lines,const void* bits,const BITMAPINFO* bmi,UINT use){
    if(exception_injected)++draws_after_exception;
    ++natives;native_geometry=native_geometry&&x==0&&y==0&&w==800&&h==600&&sx==0&&sy==0&&start==0&&lines==600;
    same_owned=same_owned&&bits==first_owned;
    if(mode==2||mode==4||mode==7||mode==8)return 0;
    return ::SetDIBitsToDevice(dc,x,y,w,h,sx,sy,start,lines,bits,bmi,use);
}
int WINAPI hdvg_fault_save(HDC dc){++saves;if(mode==9||(mode==16&&saves==2))return 0;return ::SaveDC(dc);}
BOOL WINAPI hdvg_fault_restore(HDC dc,int saved){++restores;if(exception_injected&&mode>=23&&mode<=25)return FALSE;if(mode==22&&partial)return FALSE;if((mode==14||mode==15)&&restores==1)return FALSE;return ::RestoreDC(dc,saved);}
int WINAPI hdvg_fault_clip(HDC dc,int x,int y,int r,int b){++clips;
    if(dc==target_dc&&!exception_injected&&(mode==23||mode==26)){
        ::IntersectClipRect(dc,x,y,r,b);exception_injected=true;throw std::bad_alloc();
    }if((mode==11&&clips==1)||(mode==13&&clips==2))return ERROR;return ::IntersectClipRect(dc,x,y,r,b);}
int WINAPI hdvg_fault_clipbox(HDC dc,LPRECT rc){return mode==10?ERROR: ::GetClipBox(dc,rc);}
int WINAPI hdvg_fault_mode(HDC dc,int value){return mode==12?0: ::SetStretchBltMode(dc,value);}
BOOL WINAPI hdvg_fault_brush(HDC dc,int x,int y,LPPOINT old){return mode==17?FALSE: ::SetBrushOrgEx(dc,x,y,old);}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;dir=argv[1];checks=0;failures=0;
    auto f=input(1,2,74);HdvGdi* p=hdvg_create();check(configure(p,L"joey.tsv"),"fault context configure");
    Dib d(1640,1240),oracle(1640,1240);
    SetStretchBltMode(d.dc,HALFTONE);SetStretchBltMode(oracle.dc,HALFTONE);
    SetBrushOrgEx(d.dc,3,7,nullptr);SetBrushOrgEx(oracle.dc,3,7,nullptr);
    target_dc=d.dc;
    auto present=[&]{return draw(p,d,f,9,11,1601,1201);};
    for(const wchar_t* bad:{L"badzlib.tsv",L"badadler.tsv",L"truncated.tsv",L"baddeflate.tsv",L"trailingidat.tsv",L"trailingempty.tsv",L"zipbomb.tsv"}){
        reset(0);d.clear();oracle.clear();base_oracle(oracle,f);
        check(configure(p,bad),"malformed compressed fixture config admitted");
        check(present()==1,"strict malformed compressed stream never overlays");
        check(equal(d,oracle),"malformed PNG every pixel equals independent original base");
        check(stretches==1,"malformed PNG does not issue overlay draw");
    }
    check(configure(p,L"joey.tsv"),"valid master recovers after malformed streams");
    /* Legacy native fallback is a prepaint route only. Remove one reference
       pixel so no master/footprint can be qualified, then force scaled failure. */
    auto unmatched=f;unmatched[(size_t(74)*800+2)*2]^=1;
    for(int value:{1,3}){
        reset(value);d.clear();oracle.clear();base_oracle(oracle,unmatched,true);
        check(draw(p,d,unmatched,9,11,1601,1201)==1,"prepaint failed stretch retains successful historical native fallback");
        check(stretches==1&&natives==1&&native_geometry,"prepaint native fallback exact0,0 geometry");
        check(same_owned&&first_owned!=unmatched.data(),"prepaint fallback uses same owned snapshot");
        check(equal(d,oracle),"prepaint native success EVERY RGB independently checked");
    }
    for(int value:{1,2,3,4}){
        reset(value);d.clear();oracle.clear();check(present()==0,"failed potentially partial visible base and scaled repair => caller fallback");
        check(stretches==2&&natives==0,"post-visible scaled repair never attempts native0,0");
        check(equal(d,oracle),"both injected failures no false paint claim");
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
        check(stretches==3&&natives==0,"failed scaled repair never claims native0,0 repairs full viewport");
        oracle.clear();base_oracle(oracle,f);base_oracle(d,f);check(equal(d,oracle),"caller original fallback repairs reported failure");
    }
    /* Real drawing before an injected final-upload failure leaves poison below
       native600. A successful native0,0 draw would leave it untouched. Test
       offset scaled geometry and simple/complex caller clips, every RGB pixel. */
    for(int clip_kind=0;clip_kind<=2;++clip_kind)for(int value:{18,19,20,21,22}){
        reset(value);d.clear();oracle.clear();const auto repair_frame=input(1,7,89);const auto pristine=repair_frame;
        const int vx=11,vy=17,vw=1280,vh=960;
        HRGN clip=nullptr;
        if(clip_kind==1)clip=CreateRectRgn(19,29,1200,940);
        if(clip_kind==2){clip=CreateRectRgn(19,29,1200,940);HRGN hole=CreateRectRgn(800,100,900,300);CombineRgn(clip,clip,hole,RGN_DIFF);DeleteObject(hole);}
        if(clip){SelectClipRgn(d.dc,clip);SelectClipRgn(oracle.dc,clip);}
        SetStretchBltMode(d.dc,COLORONCOLOR);SetStretchBltMode(oracle.dc,COLORONCOLOR);
        SetBrushOrgEx(d.dc,3,7,nullptr);SetBrushOrgEx(oracle.dc,3,7,nullptr);
        /* This suite's recognized Joey Deck card is at7,89; its upload extends past
           physicaly600 at this geometry. The caller owns fallback after0. */
        const int caller_save=SaveDC(d.dc);const int result=draw(p,d,repair_frame,vx,vy,vw,vh);const bool poisoned=partial;
        const int expected_result=value==20?1:0;
        check(poisoned&&poison_y>=600,"partial final upload actually poisoned beyond native600");
        check(result==expected_result,"partial upload plus failed scaled repair or state restore returns0");
        check(natives==0,"no native fallback after partial final upload even when native API would succeed");
        if(value!=22){
            POINT point{};check(GetStretchBltMode(d.dc)==COLORONCOLOR&&GetBrushOrgEx(d.dc,&point)&&point.x==3&&point.y==7,"partial upload restores caller mode and brush");
            if(clip){HRGN after=CreateRectRgn(0,0,0,0);check(GetClipRgn(d.dc,after)==1&&EqualRgn(after,clip),"partial upload restores exact complex/simple clip");DeleteObject(after);}
        }
        mode=0;RestoreDC(d.dc,caller_save);
        /* A0 result explicitly transfers repaint responsibility to caller.
           Compare every pixel only after that documented fallback. */
        if(result==0){struct B{BITMAPINFOHEADER h;DWORD masks[3];} b{};b.h.biSize=sizeof(BITMAPINFOHEADER);b.h.biWidth=800;b.h.biHeight=-600;b.h.biPlanes=1;b.h.biBitCount=16;b.h.biCompression=BI_BITFIELDS;b.masks[0]=0xf800;b.masks[1]=0x7e0;b.masks[2]=31;
            ::StretchDIBits(d.dc,vx,vy,vw,vh,0,0,800,600,repair_frame.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY);}
        {struct B{BITMAPINFOHEADER h;DWORD masks[3];} b{};b.h.biSize=sizeof(BITMAPINFOHEADER);b.h.biWidth=800;b.h.biHeight=-600;b.h.biPlanes=1;b.h.biBitCount=16;b.h.biCompression=BI_BITFIELDS;b.masks[0]=0xf800;b.masks[1]=0x7e0;b.masks[2]=31;
            ::StretchDIBits(oracle.dc,vx,vy,vw,vh,0,0,800,600,repair_frame.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY);}
        check(equal(d,oracle),"partial upload EVERY RGB equals full scaled SAME current base after own or caller repair");
        check(repair_frame==pristine,"partial upload original complete source unchanged");
        if(clip){SelectClipRgn(d.dc,nullptr);SelectClipRgn(oracle.dc,nullptr);DeleteObject(clip);}
    }
    SetStretchBltMode(d.dc,HALFTONE);SetStretchBltMode(oracle.dc,HALFTONE);
    for(int value:{9,10,11}){
        reset(value);d.clear();oracle.clear();base_oracle(oracle,f);check(present()==1,"state setup failure paints full SAME owned base");
        check(equal(d,oracle),"failed state setup EVERY RGB pixel is current base");
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
        reset(value);d.clear();oracle.clear();base_oracle(oracle,f);check(present()==1,"clip/state failure repairs owned base and never claims HD");
        check(equal(d,oracle),"state failure full owned-base repair EVERY RGB pixel");
        check(GetStretchBltMode(d.dc)==HALFTONE,"state failure restoration retry");
        POINT brush{};check(GetBrushOrgEx(d.dc,&brush)&&brush.x==3&&brush.y==7,"state failure restores caller brush origin");
        oracle.clear();base_oracle(oracle,f);base_oracle(d,f);check(equal(d,oracle),"reported state failure caller fallback restores every pixel");
    }
    /* C++ exceptions are an adversarial wrapper boundary, not an ordinary
       Win32 error return or a claimed game cause. Once unwinding begins, the
       catch cannot prove DC restoration and must report0 without more paint. */
    for(int clip_kind=0;clip_kind<=2;++clip_kind)for(int value=23;value<=28;++value){
        reset(value);d.clear();oracle.clear();const auto frame=input(1,7,89),pristine=frame;
        const int vx=11,vy=17,vw=1280,vh=960;HRGN clip=nullptr;
        if(clip_kind==1)clip=CreateRectRgn(19,29,1200,940);
        if(clip_kind==2){clip=CreateRectRgn(19,29,1200,940);HRGN hole=CreateRectRgn(800,100,900,300);CombineRgn(clip,clip,hole,RGN_DIFF);DeleteObject(hole);}
        if(clip){SelectClipRgn(d.dc,clip);SelectClipRgn(oracle.dc,clip);}
        SetStretchBltMode(d.dc,WHITEONBLACK);SetStretchBltMode(oracle.dc,WHITEONBLACK);
        SetBrushOrgEx(d.dc,3,7,nullptr);SetBrushOrgEx(oracle.dc,3,7,nullptr);
        const int caller_save=SaveDC(d.dc);const int result=draw(p,d,frame,vx,vy,vw,vh);
        check(exception_injected,"owned wrapper exception actually reached selected before/base/upload seam");
        check(result==0,"outer exception is always0 without an unproven repair claim");
        check(draws_after_exception==0&&natives==0,"outer catch performs NO paint under uncertain unwind state");
        if(value==23||value==26)check(equal(d,oracle),"before-visible exception leaves EVERY destination RGB untouched");
        else check(partial,"after-visible exception really made a partial destination write");
        if(value>=26){
            POINT brush{};check(GetStretchBltMode(d.dc)==WHITEONBLACK&&GetBrushOrgEx(d.dc,&brush)&&brush.x==3&&brush.y==7,"successful unwind restores caller mode/brush");
            HRGN after=CreateRectRgn(0,0,0,0);const int has=GetClipRgn(d.dc,after);
            check(clip?(has==1&&EqualRgn(after,clip)):(has==0),"successful unwind restores exact none/simple/complex caller clip");DeleteObject(after);
        }else check(restores>0,"persistent restoration failure injected; no impossible restoration promise");
        check(frame==pristine,"exception path original complete source unchanged");
        /* The caller can restore its own earlier save after fault injection
           stops; only then is its full scaled repaint/state comparison valid. */
        mode=0;check(RestoreDC(d.dc,caller_save)!=FALSE,"caller recovers its own earlier DC save");
        struct B{BITMAPINFOHEADER h;DWORD masks[3];} b{};b.h.biSize=sizeof(BITMAPINFOHEADER);b.h.biWidth=800;b.h.biHeight=-600;b.h.biPlanes=1;b.h.biBitCount=16;b.h.biCompression=BI_BITFIELDS;b.masks[0]=0xf800;b.masks[1]=0x7e0;b.masks[2]=31;
        ::StretchDIBits(d.dc,vx,vy,vw,vh,0,0,800,600,frame.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY);
        ::StretchDIBits(oracle.dc,vx,vy,vw,vh,0,0,800,600,frame.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY);
        check(equal(d,oracle),"after caller recovery EVERY RGB equals independent full same-frame scaled base");
        if(clip){SelectClipRgn(d.dc,nullptr);SelectClipRgn(oracle.dc,nullptr);DeleteObject(clip);}
    }
    HRGN empty=CreateRectRgn(0,0,0,0);SelectClipRgn(d.dc,empty);reset(2);
    check(present()==3,"known empty caller clip explicit no-op result");
    check(stretches==0&&natives==0,"known empty clip invokes no visible failing driver calls");
    SelectClipRgn(d.dc,nullptr);DeleteObject(empty);
    hdvg_shutdown(p);check(present()==0,"shutdown after injected failures");hdvg_destroy(p);
    std::printf("%d fault checks, %d failures\n",checks,failures);return failures?1:0;
}
