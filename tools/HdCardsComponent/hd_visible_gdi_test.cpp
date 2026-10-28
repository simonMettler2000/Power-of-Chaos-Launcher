/* Own arithmetic patterns and owned DIB/memory DCs only. No windows or providers. */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "hd_visible_gdi.h"
#include "hd_visible.h"
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <thread>
#include <vector>
namespace {
int checks=0, failures=0;
void check(bool value,const char* name){++checks;if(!value){++failures;std::printf("FAIL %s\n",name);}}
struct Dib {
    HDC dc=nullptr; HBITMAP bitmap=nullptr; HGDIOBJ prior=nullptr; unsigned* pixels=nullptr;
    int w,h;
    Dib(int width,int height):w(width),h(height) {
        BITMAPINFO info{}; info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth=w; info.bmiHeader.biHeight=-h;
        info.bmiHeader.biPlanes=1; info.bmiHeader.biBitCount=32;
        dc=CreateCompatibleDC(nullptr);
        bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,reinterpret_cast<void**>(&pixels),nullptr,0);
        prior=SelectObject(dc,bitmap); clear();
    }
    void clear(){GdiFlush();std::fill(pixels,pixels+size_t(w)*h,0x00192735);}
    unsigned at(int x,int y){GdiFlush();return pixels[size_t(y)*w+x]&0xffffff;}
    ~Dib(){SelectObject(dc,prior);DeleteObject(bitmap);DeleteDC(dc);}
};
std::wstring dir;
std::wstring path(const wchar_t* name){return dir+L"\\"+name;}
std::vector<unsigned char> ref(int key=1){
    auto name=path((L"ref"+std::to_wstring(key)+L".bin").c_str());
    std::ifstream file(name,std::ios::binary); return {std::istreambuf_iterator<char>(file),{}};
}
std::vector<unsigned char> input(int key,int x,int y,int pitch=1600){
    std::vector<unsigned char> result(size_t(pitch)*600,0x22); auto r=ref(key);
    for(int row=0;row<290;++row)std::memcpy(result.data()+size_t(y+row)*pitch+x*2,r.data()+row*400,400);
    return result;
}
int draw(HdvGdi* p,Dib& d,const std::vector<unsigned char>& f,int x=0,int y=0,int w=1600,int h=1200,int pitch=1600){
    return hdvg_present(p,d.dc,x,y,w,h,f.data(),800,600,pitch,HDV_RGB565,1);
}
bool configure(HdvGdi* p,const wchar_t* file){return hdvg_configure(p,path(file).c_str())!=0;}
unsigned expected_master(int x,int y){return ((x/2+y/2)&1)?0x00e03020:0x0010d0f0;}
bool equal_rgb(Dib& a,Dib& b){
    GdiFlush();if(a.w!=b.w||a.h!=b.h)return false;
    for(size_t i=0;i<size_t(a.w)*a.h;++i)if((a.pixels[i]&0xffffff)!=(b.pixels[i]&0xffffff))return false;
    return true;
}
void expected_frame(Dib& d,const std::vector<unsigned char>& source,int vx,int vy,int vw,int vh,bool strokes=false,int mode=HALFTONE){
    /* Unhooked GDI draws only independently synthesized inputs. This expected
       canvas is completed before calling the product, never from its pixels. */
    struct Base {BITMAPINFOHEADER h;DWORD masks[3];} base{};
    base.h.biSize=sizeof(BITMAPINFOHEADER);base.h.biWidth=800;base.h.biHeight=-600;
    base.h.biPlanes=1;base.h.biBitCount=16;base.h.biCompression=BI_BITFIELDS;
    base.masks[0]=0xf800;base.masks[1]=0x07e0;base.masks[2]=0x001f;
    const int saved=SaveDC(d.dc);check(saved!=0,"independent expected save");
    check(IntersectClipRect(d.dc,vx,vy,vx+vw,vy+vh)!=ERROR,"independent expected viewport");
    check(StretchDIBits(d.dc,vx,vy,vw,vh,0,0,800,600,source.data(),reinterpret_cast<BITMAPINFO*>(&base),DIB_RGB_COLORS,SRCCOPY)>0,"independent expected base");
    const int left=vx+2*vw/800,top=vy+74*vh/600,right=vx+202*vw/800,bottom=vy+364*vh/600;
    std::vector<unsigned> art(800*1160);
    for(int y=0;y<1160;++y)for(int x=0;x<800;++x){
        const bool ink=x%47<3||(y%41<3&&x%47<29)||x%47==y%41;
        art[size_t(y)*800+x]=strokes?(ink?0:0xffffff):expected_master(x,y);
    }
    check(SetStretchBltMode(d.dc,mode)!=0&&SetBrushOrgEx(d.dc,0,0,nullptr),"independent expected filter/brush");
    check(IntersectClipRect(d.dc,left,top,right,bottom)!=ERROR,"independent expected ROI");
    BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=800;
    info.bmiHeader.biHeight=-1160;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
    check(StretchDIBits(d.dc,left,top,right-left,bottom-top,0,0,800,1160,art.data(),&info,DIB_RGB_COLORS,SRCCOPY)>0,"independent expected master");
    check(RestoreDC(d.dc,saved)!=0,"independent expected restore");GdiFlush();
}
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;dir=argv[1];
    HdvGdi* p=hdvg_create();check(p!=nullptr,"create"); Dib d(1640,1240);
    auto f=input(1,2,74); const auto pristine=f;
    check(configure(p,L"joey.tsv"),"configure Joey");
    SetStretchBltMode(d.dc,HALFTONE);SetBrushOrgEx(d.dc,3,7,nullptr);
    const COLORREF color=SetTextColor(d.dc,RGB(31,91,171));(void)color;
    HRGN clip=CreateRectRgn(1,2,1638,1238);SelectClipRgn(d.dc,clip);
    Dib expected(1640,1240);SetStretchBltMode(expected.dc,HALFTONE);SetBrushOrgEx(expected.dc,3,7,nullptr);
    SelectClipRgn(expected.dc,clip);expected_frame(expected,f,0,0,1600,1200);
    check(draw(p,d,f)==2,"real HD composited");
    check(GetStretchBltMode(d.dc)==HALFTONE,"stretch mode restored");
    POINT org{};GetBrushOrgEx(d.dc,&org);check(org.x==3&&org.y==7,"brush origin restored");
    check(GetTextColor(d.dc)==RGB(31,91,171),"text color restored");
    HRGN after=CreateRectRgn(0,0,0,0);check(GetClipRgn(d.dc,after)==1&&EqualRgn(clip,after),"clip restored");
    check(f==pristine,"source remains unchanged");
    /* At 2x base scaling the HD card spans 400x580. Its master retains two
       different physical pixels inside a single 2x original source pixel. */
    check(equal_rgb(d,expected),"smooth checker every RGB pixel equals independently drawn expectation");
    check(d.at(4,148)!=d.at(5,148),"new sub-original horizontal detail");
    check(d.at(4,148)!=d.at(4,149),"new sub-original vertical detail");
    check(d.at(1605,100)==0x192735,"outside viewport untouched");
    DeleteObject(after);SelectClipRgn(d.dc,nullptr);DeleteObject(clip);
    Dib base(1640,1240);auto unmatched=f;unmatched[(74*800+2)*2]^=1;
    check(draw(p,base,unmatched)==1,"single-byte wrong reference base fallback");
    GdiFlush();auto baseline=std::vector<unsigned>(base.pixels,base.pixels+size_t(base.w)*base.h);
    d.clear();SetStretchBltMode(d.dc,COLORONCOLOR);draw(p,d,f);
    check(d.at(900,900)==base.at(900,900),"outside ROI equals original base");
    GdiFlush();bool unchanged=true;
    for(int yy=0;yy<d.h;++yy)for(int xx=0;xx<d.w;++xx)
        if(!(xx>=4&&xx<404&&yy>=148&&yy<728) &&
            (d.pixels[size_t(yy)*d.w+xx]&0xffffff)!=(baseline[size_t(yy)*d.w+xx]&0xffffff))unchanged=false;
    check(unchanged,"every pixel outside approved physical ROI matches original base");
    f=pristine; f[((74+289)*800+2+199)*2+1]^=2;
    check(draw(p,d,f)==1,"last reference byte occlusion fails closed");
    f=pristine;for(size_t k=0;k<f.size();++k)f[k]=static_cast<unsigned char>(f[k]/2);
    check(draw(p,d,f)==1,"faded frame fails closed");
    check(configure(p,L"ambiguous.tsv"),"duplicate reference configuration valid");
    check(draw(p,d,pristine)==1,"duplicate exact reference aggregate ambiguity");
    check(configure(p,L"yugi.tsv"),"Yugi configure");
    auto yugi=input(1,5,72);check(draw(p,d,yugi)==2,"Yugi factual duel ROI");
    yugi=input(1,11,88);check(draw(p,d,yugi)==2,"Yugi factual cardlist ROI");
    yugi=input(1,7,89);check(draw(p,d,yugi)==2,"Yugi factual deck ROI");
    check(configure(p,L"kaiba.tsv"),"Kaiba configure");
    auto kaiba=input(1,5,72);check(draw(p,d,kaiba)==2,"Kaiba factual duel ROI");
    kaiba=input(1,16,86);check(draw(p,d,kaiba)==2,"Kaiba factual cardlist ROI");
    kaiba=input(1,7,89);check(draw(p,d,kaiba)==2,"Kaiba factual deck ROI");
    check(configure(p,L"zero.tsv"),"zero reference configure");
    std::vector<unsigned char> zero(800*600*2,0);check(draw(p,d,zero)==1,"multiple placement ambiguity");
    check(configure(p,L"joey.tsv"),"Joey reconfigure");
    auto joey=input(1,6,87);check(draw(p,d,joey)==2,"Joey factual cardlist ROI");
    joey=input(1,7,89);check(draw(p,d,joey)==2,"Joey factual deck ROI");
    auto padded=input(1,2,74,1612);check(draw(p,d,padded,0,0,1600,1200,1612)==2,"padded RGB565 pitch");
    /* Alias source with destination: base draw overwrites source. Overlay and
       base must still be derived from the copied frame. */
    d.clear();std::memcpy(d.pixels,pristine.data(),pristine.size());
    check(hdvg_present(p,d.dc,0,0,1600,1200,d.pixels,800,600,1600,HDV_RGB565,1)==2,"owned snapshot source aliases destination");
    check(d.at(5,148)==expected.at(5,148),"alias retains independently expected smooth master detail");
    /* Rounded odd 4:3 viewport and partial pre-existing clipping. */
    d.clear();HRGN small=CreateRectRgn(12,160,410,700);SelectClipRgn(d.dc,small);
    check(draw(p,d,pristine,9,11,1601,1201)==2,"odd viewport clipped draw");
    check(d.at(11,200)==0x192735&&d.at(411,200)==0x192735,"pre-existing clip honored");
    check(d.at(13,159)==0x192735,"clip top honored");
    after=CreateRectRgn(0,0,0,0);check(GetClipRgn(d.dc,after)==1&&EqualRgn(small,after),"odd clip restored");
    DeleteObject(after);SelectClipRgn(d.dc,nullptr);DeleteObject(small);
    /* High-contrast thin strokes at 4:1 and fractional reduction. The full RGB
       expectation is HALFTONE, and a separate nearest canvas must differ. */
    check(configure(p,L"smooth.tsv"),"smooth textlike fixture configure");
    for(int vw:{800,1280,1601}){
        const int vh=vw==800?600:(vw==1280?960:1201);
        d.clear();expected.clear();SelectClipRgn(expected.dc,nullptr);
        SetStretchBltMode(d.dc,COLORONCOLOR);SetStretchBltMode(expected.dc,COLORONCOLOR);
        expected_frame(expected,pristine,9,11,vw,vh,true);
        Dib nearest(1640,1240);SetStretchBltMode(nearest.dc,COLORONCOLOR);
        expected_frame(nearest,pristine,9,11,vw,vh,true,COLORONCOLOR);
        check(draw(p,d,pristine,9,11,vw,vh)==2,"smooth thin-stroke preview composited");
        check(equal_rgb(d,expected),"thin-stroke every RGB pixel equals independent smooth expectation");
        check(!equal_rgb(d,nearest),"smooth quality is observably different from nearest deletion");
        size_t mixed=0;GdiFlush();
        const int left=9+2*vw/800,top=11+74*vh/600,right=9+202*vw/800,bottom=11+364*vh/600;
        for(int yy=top;yy<bottom;++yy)for(int xx=left;xx<right;++xx){const unsigned c=d.pixels[size_t(yy)*d.w+xx]&0xffffff;if(c!=0&&c!=0xffffff)++mixed;}
        check(mixed>1000,"smooth filter retains partial coverage of thin textlike strokes");
        POINT brush{};check(GetBrushOrgEx(d.dc,&brush)&&brush.x==3&&brush.y==7,"smooth preview restores brush origin");
        check(GetStretchBltMode(d.dc)==COLORONCOLOR,"smooth preview restores caller nearest mode");
    }
    check(configure(p,L"joey.tsv"),"normal fixture restored after quality checks");
    /* No retained DC identity: a second independent owned target is safe. */
    check(draw(p,base,pristine)==2,"new destination HDC");
    d.clear();check(hdvg_present(p,d.dc,0,0,1600,1200,pristine.data(),800,600,1600,HDV_RGB565,0)==0,"ineligible fallback");
    check(d.at(10,200)==0x192735,"ineligible paints nothing");
    for(int pitch:{0,1599,65537})check(hdvg_present(p,d.dc,0,0,1600,1200,pristine.data(),800,600,pitch,HDV_RGB565,1)==0,"unsupported pitch");
    check(hdvg_present(p,d.dc,0,0,1600,1200,pristine.data(),800,600,1600,HDV_RGB555,1)==0,"RGB555 rejected");
    check(draw(p,d,pristine,0,0,1600,1000)==0,"non-4:3 rejected");
    SetMapMode(d.dc,MM_ANISOTROPIC);check(draw(p,d,pristine)==0,"mapping transform rejected");SetMapMode(d.dc,MM_TEXT);
    SetViewportOrgEx(d.dc,1,0,nullptr);check(draw(p,d,pristine)==0,"DC origin rejected");SetViewportOrgEx(d.dc,0,0,nullptr);
    SetGraphicsMode(d.dc,GM_ADVANCED);XFORM x{1,0,0,1,1,0};SetWorldTransform(d.dc,&x);
    check(draw(p,d,pristine)==0,"world transform rejected");ModifyWorldTransform(d.dc,nullptr,MWT_IDENTITY);SetGraphicsMode(d.dc,GM_COMPATIBLE);
    int foreign=9;std::thread t([&]{foreign=draw(p,d,pristine);});t.join();check(foreign==0,"foreign render thread rejected");
    for(const wchar_t* name:{L"empty.tsv",L"badheader.tsv",L"format555.tsv",L"duplicatekey.tsv",L"relative.tsv",L"missingref.tsv",L"shortref.tsv",L"trailing.tsv",L"nul.tsv",L"zero-key.tsv",L"over1024.tsv",L"device.tsv",L"ads.tsv",L"dotdot.tsv",L"badutf8.tsv"}){
        check(!configure(p,name),"invalid config rejected");
        d.clear();check(draw(p,d,pristine)==0,"failed config disables previous");check(d.at(10,200)==0x192735,"invalid config no stale paint");
        check(configure(p,L"joey.tsv"),"recover from invalid config");
    }
    check(!configure(p,L"missingpng.tsv"),"missing master rejected at configuration");check(draw(p,d,pristine)==0,"missing master config disables");
    for(const wchar_t* name:{L"corrupt.tsv",L"rgba.tsv",L"smallpng.tsv",L"badzlib.tsv",L"badadler.tsv",L"truncated.tsv",L"baddeflate.tsv",L"trailingidat.tsv",L"trailingempty.tsv",L"zipbomb.tsv"}){
        check(configure(p,name),"master problem config admission");check(draw(p,d,pristine)==1,"master problem original base fallback");
    }
    check(configure(p,L"storedempty.tsv"),"stored empty final block configure");
    check(draw(p,d,pristine)==2,"valid stored stream including empty final block accepted");
    /* Four-master LRU: after five unique matches, deleted master 1 must not
       survive eviction. Cached master 5 remains available until reconfigure. */
    check(configure(p,L"five.tsv"),"five reference configuration");
    for(int key=1;key<=5;++key){auto a=input(key,2,74);check(draw(p,d,a)==2,"bounded cache fill");}
    check(DeleteFileW(path(L"master1.png").c_str())!=0,"delete evicted owned fixture");
    check(draw(p,d,pristine)==1,"evicted master is re-read and fails closed");
    check(DeleteFileW(path(L"master5.png").c_str())!=0,"delete cached owned fixture");
    auto fifth=input(5,2,74);check(draw(p,d,fifth)==2,"cached decoded master remains bounded resident");
    check(!configure(p,L"five.tsv"),"reconfiguration rejects removed master and clears cache");check(draw(p,d,fifth)==0,"deleted cached master cannot survive reconfigure");
    hdvg_shutdown(p);d.clear();check(draw(p,d,pristine)==0,"shutdown disables");check(d.at(10,200)==0x192735,"shutdown no stale draw");
    check(hdvg_configure(p,nullptr)==1,"explicit disable");
    hdvg_destroy(p);std::printf("%d checks, %d failures\n",checks,failures);return failures?1:0;
}
