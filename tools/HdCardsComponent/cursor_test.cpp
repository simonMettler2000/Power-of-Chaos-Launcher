/* Procedural cursor recognition and complete memory-DC RGB oracles only. */
#define wmain original_gdi_suite
#include "hd_visible_gdi_test.cpp"
#undef wmain
#include <array>
namespace {
int fault=0,canvas_calls=0,flush_calls=0;
std::array<unsigned,65536> gdi_color{};
void derive_color_oracle() {
    Dib d(256,256);std::vector<uint16_t> words(65536);for(unsigned i=0;i<65536;++i)words[i]=static_cast<uint16_t>(i);
    struct Native {BITMAPINFOHEADER h;DWORD masks[3];} n{};n.h.biSize=sizeof(BITMAPINFOHEADER);n.h.biWidth=256;n.h.biHeight=-256;n.h.biPlanes=1;n.h.biBitCount=16;n.h.biCompression=BI_BITFIELDS;n.masks[0]=0xf800;n.masks[1]=0x7e0;n.masks[2]=31;
    check(::StretchDIBits(d.dc,0,0,256,256,0,0,256,256,words.data(),reinterpret_cast<BITMAPINFO*>(&n),DIB_RGB_COLORS,SRCCOPY)!=0,"all65536 owned GDI color conversion");GdiFlush();size_t wrong=0;
    for(unsigned i=0;i<65536;++i){gdi_color[i]=d.pixels[i]&0xffffff;const unsigned r=i>>11,g=(i>>5)&63,b=i&31;
        const unsigned expected=(((r<<3)|(r>>2))<<16)|(((g<<2)|(g>>4))<<8)|((b<<3)|(b>>2));wrong+=gdi_color[i]!=expected;}
    check(wrong==0,"bit replication verified against EVERY565 word");std::printf("native GDI expansion words=65536 wrong=%zu\n",wrong);
}
std::vector<unsigned char> sprite(unsigned state) {
    std::vector<unsigned char> p(16384);
    for(unsigned y=8;y<43;++y)for(unsigned x=8;x<32;++x) {
        const unsigned q=y==8&&x<18?15:(x+y)%16;const size_t i=(y*64+x)*4;
        p[i]=static_cast<unsigned char>((x*17+state*11)%256);p[i+1]=static_cast<unsigned char>((y*13+state*23)%256);
        p[i+2]=static_cast<unsigned char>((x*7+y*3+state*31)%256);p[i+3]=static_cast<unsigned char>(q*17);
    }return p;
}
/* Independent oracle computes each channel using division/remainder, then
   adds complete packed contributions. It never reads recognizer output. */
uint16_t contribution(uint16_t color,unsigned weight) {
    unsigned out=0;for(unsigned c=0;c<3;++c){const unsigned shifts[3]={0,5,11},masks[3]={31,63,31};
        const unsigned product=((color>>shifts[c])&masks[c])*weight;
        out|=((product-product%15)/15)<<shifts[c];}return static_cast<uint16_t>(out);
}
void stamp(std::vector<unsigned char>& f,const std::vector<unsigned char>& p,int ox,int oy) {
    for(int y=0;y<64;++y)for(int x=0;x<64;++x) {
        const size_t i=(size_t(y)*64+x)*4;const unsigned q=p[i+3]/16;uint16_t d=0;
        const size_t at=(size_t(oy+y)*800+ox+x)*2;std::memcpy(&d,f.data()+at,2);
        const uint16_t color=static_cast<uint16_t>(((p[i+2]/8)<<11)|((p[i+1]/4)<<5)|(p[i]/8));
        const uint16_t v=static_cast<uint16_t>(contribution(color,q)+contribution(d,15-q));std::memcpy(f.data()+at,&v,2);
    }
}
HdvEngine* engine(unsigned game,bool duplicate=false) {
    auto* e=hdv_create(game,HDV_RGB565);const auto r=ref();check(hdv_add(e,1,r.data(),r.size(),L"C:\\authored-master.png")!=0,"add independent reference");
    if(duplicate)check(hdv_add(e,2,r.data(),r.size(),L"C:\\other.png")!=0,"duplicate visible identity admitted then ambiguous");
    for(unsigned state=1;state<=2;++state){const auto p=sprite(state);check(hdv_add_cursor(e,state,p.data(),p.size())!=0,"install owned procedural sprite");}
    check(hdv_seal(e)!=0,"complete bank seal");return e;
}
bool capture(HdvEngine* e,const std::vector<unsigned char>& f,HdvMatch& m){check(hdv_capture(e,f.data(),800,600,1600,HDV_RGB565)!=0,"same owned capture");return hdv_match(e,&m)!=0;}
void base(Dib& d,const std::vector<unsigned char>& f,int vx,int vy,int vw,int vh) {
    struct Base {BITMAPINFOHEADER h;DWORD masks[3];} b{};b.h.biSize=sizeof(BITMAPINFOHEADER);b.h.biWidth=800;b.h.biHeight=-600;
    b.h.biPlanes=1;b.h.biBitCount=16;b.h.biCompression=BI_BITFIELDS;b.masks[0]=0xf800;b.masks[1]=0x7e0;b.masks[2]=31;
    const int saved=SaveDC(d.dc);IntersectClipRect(d.dc,vx,vy,vx+vw,vy+vh);
    check(::StretchDIBits(d.dc,vx,vy,vw,vh,0,0,800,600,f.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY)!=0,"oracle base");RestoreDC(d.dc,saved);
}
void oracle(Dib& d,const std::vector<unsigned char>& f,const std::vector<unsigned char>& p,int ox,int oy,int rx,int ry,int vx,int vy,int vw,int vh) {
    base(d,f,vx,vy,vw,vh);const int l=vx+rx*vw/800,t=vy+ry*vh/600,r=vx+(rx+200)*vw/800,b=vy+(ry+290)*vh/600;
    std::vector<unsigned> art(800*1160);for(int y=0;y<1160;++y)for(int x=0;x<800;++x)art[size_t(y)*800+x]=expected_master(x,y);
    const int saved=SaveDC(d.dc);IntersectClipRect(d.dc,l,t,r,b);SetStretchBltMode(d.dc,HALFTONE);SetBrushOrgEx(d.dc,0,0,nullptr);
    BITMAPINFO bi{};bi.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);bi.bmiHeader.biWidth=800;bi.bmiHeader.biHeight=-1160;bi.bmiHeader.biPlanes=1;bi.bmiHeader.biBitCount=32;
    check(::StretchDIBits(d.dc,l,t,r-l,b-t,0,0,800,1160,art.data(),&bi,DIB_RGB_COLORS,SRCCOPY)!=0,"oracle HALFTONE master");RestoreDC(d.dc,saved);GdiFlush();
    /* Independent full-canvas GDI coordinate oracle: one image encodes X,
       another Y, rendered at original global viewport origin, rather than the
       product's packed index field in an ROI-local DIB. */
    Dib mapx(d.w,d.h),mapy(d.w,d.h);std::vector<unsigned> xs(800*600),ys(800*600);
    for(unsigned yy=0;yy<600;++yy)for(unsigned xx=0;xx<800;++xx){xs[yy*800+xx]=xx;ys[yy*800+xx]=yy;}
    BITMAPINFO mb{};mb.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);mb.bmiHeader.biWidth=800;mb.bmiHeader.biHeight=-600;mb.bmiHeader.biPlanes=1;mb.bmiHeader.biBitCount=32;
    check(::StretchDIBits(mapx.dc,vx,vy,vw,vh,0,0,800,600,xs.data(),&mb,DIB_RGB_COLORS,SRCCOPY)!=0,"global GDI X phase");
    check(::StretchDIBits(mapy.dc,vx,vy,vw,vh,0,0,800,600,ys.data(),&mb,DIB_RGB_COLORS,SRCCOPY)!=0,"global GDI Y phase");GdiFlush();
    for(int y=t;y<b;++y)for(int x=l;x<r;++x) {
        if(x<0||y<0||x>=d.w||y>=d.h||!PtVisible(d.dc,x,y))continue;const size_t at=size_t(y)*d.w+x;
        const int sx=static_cast<int>(mapx.pixels[at]&0xffffff)-ox,sy=static_cast<int>(mapy.pixels[at]&0xffffff)-oy;
        if(sx<0||sx>=64||sy<0||sy>=64)continue;const size_t si=(size_t(sy)*64+sx)*4;const unsigned q=p[si+3]/16;
        auto& v=d.pixels[at];unsigned result=0;
        const unsigned native=((unsigned(p[si+2])/8)<<11)|((unsigned(p[si+1])/4)<<5)|(unsigned(p[si])/8);
        for(unsigned c=0;c<3;++c){const unsigned foreground=(gdi_color[native]>>(c*8))&255;
            const unsigned a=foreground*q,bg=((v>>(c*8))&255)*(15-q);result|=((a-a%15)/15+(bg-bg%15)/15)<<(c*8);}v=result;
    }
}
void opaque_continuity(Dib& actual,const std::vector<unsigned char>& f,const std::vector<unsigned char>& p,int ox,int oy,int rx,int ry,int vx,int vy,int vw,int vh) {
    Dib native(actual.w,actual.h),mask(actual.w,actual.h);base(native,f,vx,vy,vw,vh);
    std::vector<unsigned> flags(800*600);
    for(int y=0;y<64;++y)for(int x=0;x<64;++x)if(p[(size_t(y)*64+x)*4+3]/16==15)flags[size_t(oy+y)*800+ox+x]=0xffffff;
    BITMAPINFO bi{};bi.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);bi.bmiHeader.biWidth=800;bi.bmiHeader.biHeight=-600;bi.bmiHeader.biPlanes=1;bi.bmiHeader.biBitCount=32;
    check(::StretchDIBits(mask.dc,vx,vy,vw,vh,0,0,800,600,flags.data(),&bi,DIB_RGB_COLORS,SRCCOPY)!=0,"owned opaque support projection");GdiFlush();bool same=true;size_t count=0;
    const int l=vx+rx*vw/800,t=vy+ry*vh/600,r=vx+(rx+200)*vw/800,b=vy+(ry+290)*vh/600;
    for(int y=t;y<b;++y)for(int x=l;x<r;++x) {const size_t i=size_t(y)*actual.w+x;if((mask.pixels[i]&0xffffff)==0xffffff){++count;same=same&&((actual.pixels[i]&0xffffff)==(native.pixels[i]&0xffffff));}}
    check(count>0&&same,"opaque foreground at preview crossings equals original GDI base color");
}
}
HDC WINAPI cursor_fault_dc(HDC dc){return fault==1?nullptr: ::CreateCompatibleDC(dc);}
HBITMAP WINAPI cursor_fault_dib(HDC dc,const BITMAPINFO* bi,UINT use,void** bits,HANDLE section,DWORD off){return fault==2?nullptr: ::CreateDIBSection(dc,bi,use,bits,section,off);}
HGDIOBJ WINAPI cursor_fault_select(HDC dc,HGDIOBJ obj){return fault==3?HGDI_ERROR: ::SelectObject(dc,obj);}
BOOL WINAPI cursor_fault_flush(){++flush_calls;if(fault==4&&flush_calls==1)return FALSE;return ::GdiFlush();}
int WINAPI cursor_fault_stretch(HDC dc,int x,int y,int w,int h,int sx,int sy,int sw,int sh,const void* bits,const BITMAPINFO* bi,UINT use,DWORD rop){
    if(bi->bmiHeader.biBitCount==32){++canvas_calls;if((fault==5&&canvas_calls==3)||(fault==6&&canvas_calls==1)){SetPixelV(dc,x+1,y+1,RGB(255,0,255));return 0;}}
    return ::StretchDIBits(dc,x,y,w,h,sx,sy,sw,sh,bits,bi,use,rop);
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=2)return 2;dir=argv[1];checks=0;failures=0;derive_color_oracle();
    const std::array<std::array<unsigned,3>,9> regions={{{1,5,72},{1,11,88},{1,7,89},{2,5,72},{2,16,86},{2,7,89},{3,2,74},{3,6,87},{3,7,89}}};
    for(const auto& region:regions)for(unsigned state=1;state<=2;++state)for(int placement=0;placement<4;++placement) {
        const auto p=sprite(state);const int rx=static_cast<int>(region[1]),ry=static_cast<int>(region[2]),ox=placement==1?rx+180:rx+71,oy=placement==2?ry+270:placement==3?ry-20:ry+89;
        auto f=input(1,rx,ry);stamp(f,p,ox,oy);const auto pristine=f;auto* e=engine(region[0]);HdvMatch match{};HdvCursor cursor{};
        check(capture(e,f,match)&&match.key==1&&match.x==region[1]&&match.y==region[2],"all nine placements exact card+cursor");
        check(hdv_cursor(e,&cursor)&&cursor.state==state&&cursor.x==ox&&cursor.y==oy&&cursor.generation==match.generation,"explicit same-generation foreground plan");
        for(unsigned bit=0;bit<16;++bit){auto bad=f;bad[(size_t(ry+1)*800+rx+1)*2+bit/8]^=static_cast<unsigned char>(1u<<(bit%8));check(!capture(e,bad,match),"every unexplained source-word bit rejects");}
        check(!hdv_cursor(e,&cursor),"rejected frame clears stale cursor");hdv_invalidate(e);check(!hdv_match(e,&match),"invalidate clears match");hdv_destroy(e);
        HdvGdi* g=hdvg_create();const wchar_t* cfg=region[0]==1?L"yugi-cursor.tsv":region[0]==2?L"kaiba-cursor.tsv":L"joey-cursor.tsv";
        check(configure(g,cfg),"V2 game configuration");
        for(const auto& size:std::array<std::array<int,2>,3>{{{800,600},{1280,960},{1067,800}}}) {
            Dib actual(1320,1000),expected(1320,1000);oracle(expected,f,p,ox,oy,rx,ry,7,9,size[0],size[1]);fault=0;canvas_calls=flush_calls=0;
            check(draw(g,actual,f,7,9,size[0],size[1])==2,"cursor HD foreground painted");check(equal_rgb(actual,expected),"EVERY RGB pixel equals independent GDI color/phase oracle");
            opaque_continuity(actual,f,p,ox,oy,rx,ry,7,9,size[0],size[1]);check(f==pristine,"original source unchanged");
        }
        hdvg_destroy(g);
    }
    /* Direct unhooked GDI phase proof: packed565 base versus an independently
       expanded32-bit source at identical GLOBAL viewport coordinates. */
    for(int mode=1;mode<=3;++mode)for(int vw:{800,1067,1280}) {
        const int vh=vw==1067?800:vw*3/4;Dib a(1320,1000),b(1320,1000);
        SetStretchBltMode(a.dc,mode);SetStretchBltMode(b.dc,mode);
        std::vector<unsigned char> packed(800*600*2);std::vector<unsigned> expanded(800*600);
        for(size_t i=0;i<expanded.size();++i){const uint16_t v=static_cast<uint16_t>((i*127+i/800*997)&65535);std::memcpy(packed.data()+i*2,&v,2);
            expanded[i]=((unsigned(v>>11)<<3)<<16)|((unsigned((v>>5)&63)<<2)<<8)|(unsigned(v&31)<<3);}
        base(a,packed,7,9,vw,vh);BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=800;info.bmiHeader.biHeight=-600;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
        check(::StretchDIBits(b.dc,7,9,vw,vh,0,0,800,600,expanded.data(),&info,DIB_RGB_COLORS,SRCCOPY)!=0,"independent32bit phase API");
        GdiFlush();size_t rgb_differences=0,packed_differences=0;unsigned first_a=0,first_b=0;
        const auto repack=[](unsigned color){return (((color>>19)&31)<<11)|(((color>>10)&63)<<5)|((color>>3)&31);};
        for(size_t i=0;i<size_t(a.w)*a.h;++i) {
            if((a.pixels[i]&0xffffff)!=(b.pixels[i]&0xffffff)){if(!rgb_differences){first_a=a.pixels[i]&0xffffff;first_b=b.pixels[i]&0xffffff;}++rgb_differences;}
            if(repack(a.pixels[i])!=repack(b.pixels[i]))++packed_differences;
        }
        /* GDI expands565 output channels with additional low color bits; the
           independently expanded32bit fixture deliberately used left shifts.
           Repacking BOTH complete images compares every native source-word
           selection without falsely equating these RGB expansion policies.
           Foreground final RGB is still checked separately at EVERY pixel. */
        std::printf("phase mode=%d viewport=%d rgb_difference=%zu packed_difference=%zu first=%06x/%06x\n",mode,vw,rgb_differences,packed_differences,first_a,first_b);
        check(packed_differences==0,"565 vs32bit every native-word phase selection");
    }
    auto f=input(1,2,74);auto p=sprite(1);stamp(f,p,73,163);HdvMatch match{};HdvCursor cursor{};
    auto* duplicate=engine(3,true);check(!capture(duplicate,f,match),"duplicate complete explanations reject");hdv_destroy(duplicate);
    auto unknown=f;unknown[(size_t(170)*800+80)*2]^=1;auto* e=engine(3);check(!capture(e,unknown,match),"unknown cursor/occlusion pixel rejects");
    auto many=f;for(int i=0;i<17;++i)stamp(many,p,250+(i%7)*70,(i/7)*100);check(!capture(e,many,match),"candidate cap overflow falls back");hdv_destroy(e);
    auto* g=hdvg_create();check(configure(g,L"joey-cursor.tsv"),"fault config");Dib actual(1320,1000),expected(1320,1000);
    for(int mode=1;mode<=3;++mode) {
        actual.clear();expected.clear();SetStretchBltMode(actual.dc,mode);SetStretchBltMode(expected.dc,mode);
        HRGN clip=CreateRectRgn(50,180,270,680);SelectClipRgn(actual.dc,clip);SelectClipRgn(expected.dc,clip);
        oracle(expected,f,p,73,163,2,74,7,9,1067,800);fault=0;canvas_calls=flush_calls=0;
        check(draw(g,actual,f,7,9,1067,800)==2,"global mode with caller clip");check(equal_rgb(actual,expected),"caller clip every RGB pixel and outside unchanged");
        SelectClipRgn(actual.dc,nullptr);SelectClipRgn(expected.dc,nullptr);DeleteObject(clip);
    }
    for(int mode=1;mode<=3;++mode)for(int vw:{640,400}) {
        actual.clear();expected.clear();SetStretchBltMode(actual.dc,mode);SetStretchBltMode(expected.dc,mode);
        base(expected,f,7,9,vw,vw*3/4);fault=0;canvas_calls=flush_calls=0;
        check(draw(g,actual,f,7,9,vw,vw*3/4)==1,"cursor minification explicit base-only fallback");
        check(equal_rgb(actual,expected),"minification fallback EVERY RGB pixel equals same owned base");
        check(canvas_calls==0,"unsupported minification issues no HD or coordinate draw");
    }
    for(int mode=1;mode<=6;++mode){actual.clear();expected.clear();base(expected,f,0,0,1280,960);fault=mode;canvas_calls=flush_calls=0;
        check(draw(g,actual,f,0,0,1280,960)==1,"canvas resource/partial failure restores same base");fault=0;check(equal_rgb(actual,expected),"failure every pixel exact base");}
    check(!configure(g,L"bad-cursor-hash.tsv"),"bad bank SHA rejected");check(draw(g,actual,f)==0,"bad reconfigure clears old engine");
    check(!configure(g,L"missing-cursor.tsv"),"missing bank rejected");check(configure(g,L"joey-cursor.tsv"),"valid bank reconfigure recovers");
    int other=1;std::thread wrong([&]{other=draw(g,actual,f);});wrong.join();check(other==0,"wrong-thread cursor renderer refused");
    hdvg_destroy(g);std::printf("cursor checks=%d failures=%d\n",checks,failures);return failures?1:0;
}
