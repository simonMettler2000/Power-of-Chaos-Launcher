/* Independent complete native565 and physicalRGB memory-DC oracles. */
#define wmain inherited_gdi_suite
#include "hd_visible_gdi_test.cpp"
#undef wmain
#include <array>
#include <cwchar>
#include <atomic>
#include <cstdlib>
#include <cstddef>
#include <new>
namespace allocation_probe {
std::atomic<long> budget{-1},live{0};
std::atomic<size_t> live_bytes{0};
struct Allocation { void* pointer;size_t bytes; };
/* Test-owned pointer metadata only; unknown CRT/nothrow allocations are
   explicitly untracked. No assumed prefix is ever read from a pointer. */
Allocation allocations[16384]{};
std::atomic_flag guard=ATOMIC_FLAG_INIT;
void lock(){while(guard.test_and_set(std::memory_order_acquire)){};}
void unlock(){guard.clear(std::memory_order_release);}
void* allocate(size_t bytes) {
    const long remaining=budget.load();
    if(!remaining)throw std::bad_alloc();
    if(remaining>0)--budget;
    void* value=std::malloc(bytes?bytes:1);if(!value)throw std::bad_alloc();
    lock();bool recorded=false;
    for(auto& item:allocations)if(!item.pointer){item={value,bytes};recorded=true;break;}
    if(recorded){++live;live_bytes+=bytes;}unlock();
    if(!recorded){std::free(value);throw std::bad_alloc();}return value;
}
void release(void* value)noexcept {
    if(!value)return;lock();
    for(auto& item:allocations)if(item.pointer==value){--live;live_bytes-=item.bytes;item={nullptr,0};break;}
    unlock();std::free(value);
}
}
void* operator new(size_t bytes){return allocation_probe::allocate(bytes);}
void* operator new[](size_t bytes){return allocation_probe::allocate(bytes);}
void operator delete(void* p)noexcept{allocation_probe::release(p);}
void operator delete[](void* p)noexcept{allocation_probe::release(p);}
void operator delete(void* p,size_t)noexcept{allocation_probe::release(p);}
void operator delete[](void* p,size_t)noexcept{allocation_probe::release(p);}
namespace {
int fault=0,canvas_calls=0,flush_calls=0,select_calls=0;
constexpr HdvFlipPose poses[]={{196,9,15},{184,15,14},{166,24,13},{141,37,12},{111,52,10},{76,69,7},{39,88,5}};
std::vector<unsigned char> file_bytes(const wchar_t* name){std::ifstream f(path(name),std::ios::binary);return {std::istreambuf_iterator<char>(f),{}};}
uint16_t word(const unsigned char* p){uint16_t v=0;std::memcpy(&v,p,2);return v;}
uint16_t independent_blend(uint16_t s,uint16_t d,unsigned q,bool equal_shortcut) {
    if(equal_shortcut&&s==d)return d;
    unsigned result=0;
    for(unsigned c=0;c<3;++c){const unsigned shift=c==0?0:c==1?5:11,mask=c==1?63:31;
        const unsigned a=((s>>shift)&mask)*q,b=((d>>shift)&mask)*(15-q);
        result+=(((a-a%15)/15+(b-b%15)/15)<<shift);}
    return static_cast<uint16_t>(result);
}
std::vector<unsigned char> native_frame(const std::vector<unsigned char>& background,const HdvFlipPose& p,
    const std::vector<unsigned char>* sprite=nullptr,int ox=0,int oy=0) {
    std::vector<unsigned char> result(960000,0x22);const auto reference=ref();
    for(unsigned y=0;y<290;++y)for(unsigned x=0;x<200;++x) {
        uint16_t value=word(background.data()+(size_t(y)*200+x)*2);
        if(x>=p.x-7&&x<p.x-7+p.width) {
            const unsigned j=x-(p.x-7),a=200/p.width,b=200%p.width;
            const int extra=(static_cast<int>((j+1)*b)-1)/static_cast<int>(p.width);
            const unsigned sx=j*a+static_cast<unsigned>(std::max(0,extra));
            value=independent_blend(word(reference.data()+(size_t(y)*200+sx)*2),value,p.q,true);
        }
        std::memcpy(result.data()+(size_t(y+89)*800+x+7)*2,&value,2);
    }
    if(sprite)for(int y=0;y<64;++y)for(int x=0;x<64;++x) {
        const size_t si=(size_t(y)*64+x)*4;const auto* s=sprite->data()+si;
        const uint16_t source=static_cast<uint16_t>(((s[2]/8)<<11)|((s[1]/4)<<5)|(s[0]/8));
        auto* at=result.data()+(size_t(y+oy)*800+x+ox)*2;
        const uint16_t value=independent_blend(source,word(at),s[3]/16,false);std::memcpy(at,&value,2);
    }
    return result;
}
unsigned expand_word(uint16_t v){const unsigned r=v/2048,g=(v/32)%64,b=v%32;return (((r*8+r/4)<<16)|((g*4+g/16)<<8)|(b*8+b/4));}
void base_only(Dib& d,const std::vector<unsigned char>& frame,int vx,int vy,int vw,int vh) {
    struct Base{BITMAPINFOHEADER h;DWORD masks[3];} bi{};
    bi.h.biSize=sizeof(BITMAPINFOHEADER);bi.h.biWidth=800;bi.h.biHeight=-600;bi.h.biPlanes=1;bi.h.biBitCount=16;bi.h.biCompression=BI_BITFIELDS;
    bi.masks[0]=0xf800;bi.masks[1]=0x7e0;bi.masks[2]=31;
    const int saved=SaveDC(d.dc);IntersectClipRect(d.dc,vx,vy,vx+vw,vy+vh);
    check(::StretchDIBits(d.dc,vx,vy,vw,vh,0,0,800,600,frame.data(),reinterpret_cast<BITMAPINFO*>(&bi),DIB_RGB_COLORS,SRCCOPY)!=0,"independent native base");RestoreDC(d.dc,saved);
}
void physical_oracle(Dib& d,const std::vector<unsigned char>& frame,const std::vector<unsigned char>& background,
    const HdvFlipPose& p,const std::vector<unsigned char>* cursor,int ox,int oy,int vx,int vy,int vw,int vh,int mode) {
    base_only(d,frame,vx,vy,vw,vh);
    Dib art(d.w,d.h),mx(d.w,d.h),my(d.w,d.h);SetStretchBltMode(mx.dc,mode);SetStretchBltMode(my.dc,mode);
    std::vector<unsigned> xs(480000),ys(480000),master(928000);
    for(unsigned y=0;y<600;++y)for(unsigned x=0;x<800;++x){xs[y*800+x]=x;ys[y*800+x]=y;}
    for(int y=0;y<1160;++y)for(int x=0;x<800;++x)master[size_t(y)*800+x]=expected_master(x,y);
    BITMAPINFO bi{};bi.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);bi.bmiHeader.biWidth=800;bi.bmiHeader.biHeight=-600;bi.bmiHeader.biPlanes=1;bi.bmiHeader.biBitCount=32;
    ::StretchDIBits(mx.dc,vx,vy,vw,vh,0,0,800,600,xs.data(),&bi,DIB_RGB_COLORS,SRCCOPY);
    ::StretchDIBits(my.dc,vx,vy,vw,vh,0,0,800,600,ys.data(),&bi,DIB_RGB_COLORS,SRCCOPY);
    const int fl=vx+static_cast<int>(p.x)*vw/800,fr=vx+static_cast<int>(p.x+p.width)*vw/800;
    const int top=vy+89*vh/600,bottom=vy+379*vh/600;
    SetStretchBltMode(art.dc,HALFTONE);SetBrushOrgEx(art.dc,0,0,nullptr);bi.bmiHeader.biHeight=-1160;
    ::StretchDIBits(art.dc,fl,top,fr-fl,bottom-top,0,0,800,1160,master.data(),&bi,DIB_RGB_COLORS,SRCCOPY);GdiFlush();
    const int left=p.q==15?fl:vx+7*vw/800,right=p.q==15?fr:vx+207*vw/800;
    for(int y=top;y<bottom;++y)for(int x=left;x<right;++x) {
        if(!PtVisible(d.dc,x,y))continue;
        const size_t at=size_t(y)*d.w+x;const int nx=static_cast<int>(mx.pixels[at]&0xffffff),ny=static_cast<int>(my.pixels[at]&0xffffff);
        if(nx<7||nx>=207||ny<89||ny>=379)continue;
        unsigned color=expand_word(word(background.data()+(size_t(ny-89)*200+nx-7)*2));
        if(x>=fl&&x<fr) {
            const unsigned source=art.pixels[at]&0xffffff;
            if(source!=color){unsigned out=0;for(unsigned c=0;c<3;++c){const unsigned a=((source>>(c*8))&255)*p.q,b=((color>>(c*8))&255)*(15-p.q);out|=((a-a%15)/15+(b-b%15)/15)<<(c*8);}color=out;}
        }
        if(cursor&&nx>=ox&&ny>=oy&&nx<ox+64&&ny<oy+64) {
            const auto* s=cursor->data()+(size_t(ny-oy)*64+nx-ox)*4;
            const unsigned source=expand_word(static_cast<uint16_t>(((s[2]/8)<<11)|((s[1]/4)<<5)|(s[0]/8))),q=s[3]/16;
            unsigned out=0;for(unsigned c=0;c<3;++c){const unsigned a=((source>>(c*8))&255)*q,b=((color>>(c*8))&255)*(15-q);out|=((a-a%15)/15+(b-b%15)/15)<<(c*8);}color=out;
        }
        d.pixels[at]=color;
    }
}
Dib* observed_destination=nullptr;Dib* intermediate_expected=nullptr;RECT observed_roi{};
bool observe_intermediate=false;unsigned intermediate_samples=0,intermediate_bad=0,target_base_calls=0,target_uploads=0,target_saves=0,target_restores=0,target_clips=0;
void sample_intermediate() {
    if(!observe_intermediate||!observed_destination||!intermediate_expected)return;
    ::GdiFlush();++intermediate_samples;bool same=equal_rgb(*observed_destination,*intermediate_expected);
    for(int y=observed_roi.top;y<observed_roi.bottom;++y)for(int x=observed_roi.left;x<observed_roi.right;++x)
        if(observed_destination->pixels[size_t(y)*observed_destination->w+x]!=0xff192735u)same=false;
    if(!same)++intermediate_bad;
    check(same,"INTERMEDIATE every RGB outside exact footprint native; entire footprint remains prior OPAQUE pattern");
}
void reset_target(Dib& target,RECT roi,bool observe,Dib* intermediate=nullptr) {
    observed_destination=&target;observed_roi=roi;observe_intermediate=observe;intermediate_expected=intermediate;
    intermediate_samples=intermediate_bad=target_base_calls=target_uploads=target_saves=target_restores=target_clips=0;
}
void static_oracle(Dib& d,const std::vector<unsigned char>& frame,const std::vector<unsigned char>* cursor,int ox,int oy,int vx,int vy,int vw,int vh,int mode) {
    base_only(d,frame,vx,vy,vw,vh);const int l=vx+7*vw/800,r=vx+207*vw/800,top=vy+89*vh/600,bottom=vy+379*vh/600;
    std::vector<unsigned> master(928000),xs(480000),ys(480000);
    for(int y=0;y<1160;++y)for(int x=0;x<800;++x)master[size_t(y)*800+x]=expected_master(x,y);
    for(unsigned y=0;y<600;++y)for(unsigned x=0;x<800;++x){xs[y*800+x]=x;ys[y*800+x]=y;}
    const int saved=::SaveDC(d.dc);::IntersectClipRect(d.dc,l,top,r,bottom);::SetStretchBltMode(d.dc,HALFTONE);::SetBrushOrgEx(d.dc,0,0,nullptr);
    BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=800;info.bmiHeader.biHeight=-1160;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
    check(::StretchDIBits(d.dc,l,top,r-l,bottom-top,0,0,800,1160,master.data(),&info,DIB_RGB_COLORS,SRCCOPY)!=0,"independent static full master");::RestoreDC(d.dc,saved);
    if(!cursor)return;
    Dib mx(d.w,d.h),my(d.w,d.h);::SetStretchBltMode(mx.dc,mode);::SetStretchBltMode(my.dc,mode);info.bmiHeader.biHeight=-600;
    ::StretchDIBits(mx.dc,vx,vy,vw,vh,0,0,800,600,xs.data(),&info,DIB_RGB_COLORS,SRCCOPY);::StretchDIBits(my.dc,vx,vy,vw,vh,0,0,800,600,ys.data(),&info,DIB_RGB_COLORS,SRCCOPY);::GdiFlush();
    for(int y=top;y<bottom;++y)for(int x=l;x<r;++x)if(::PtVisible(d.dc,x,y)) {
        const size_t at=size_t(y)*d.w+x;const int nx=static_cast<int>(mx.pixels[at]&0xffffff),ny=static_cast<int>(my.pixels[at]&0xffffff);
        if(nx<ox||ny<oy||nx>=ox+64||ny>=oy+64)continue;
        const auto* sprite=cursor->data()+(size_t(ny-oy)*64+nx-ox)*4;const unsigned q=sprite[3]/16;
        const unsigned source=expand_word(static_cast<uint16_t>(((sprite[2]/8)<<11)|((sprite[1]/4)<<5)|(sprite[0]/8)));unsigned color=0;
        for(unsigned c=0;c<3;++c)color|=((((source>>(8*c))&255)*q/15+((d.pixels[at]>>(8*c))&255)*(15-q)/15)<<(8*c));d.pixels[at]=color;
    }
}
void witness_case(HdvGdi* g,const std::vector<unsigned char>& b0,int kind,int clip_kind,int vx,int vy,int vw,int vh,int mode) {
    const bool transformed=kind>=3;const unsigned state=transformed?static_cast<unsigned>((kind-3)%3):static_cast<unsigned>(kind);
    const HdvFlipPose pose=transformed?poses[(kind-3)/3]:HdvFlipPose{200,7,15};
    const auto sprite=file_bytes(state==2?L"cursor2.bgra":L"cursor1.bgra");const auto* cp=state?&sprite:nullptr;
    const int ox=transformed?std::max(0,static_cast<int>(pose.x)-16):180,oy=179;
    const auto frame=native_frame(b0,pose,cp,ox,oy);const auto untouched=frame;
    const int lx=transformed&&pose.q==15?static_cast<int>(pose.x):7,lw=transformed&&pose.q==15?static_cast<int>(pose.width):200;
    RECT roi{vx+lx*vw/800,vy+89*vh/600,vx+(lx+lw)*vw/800,vy+379*vh/600};
    Dib actual(1800,1200),expected(1800,1200),intermediate(1800,1200);
    std::fill(actual.pixels,actual.pixels+size_t(actual.w)*actual.h,0xff192735u);std::fill(expected.pixels,expected.pixels+size_t(expected.w)*expected.h,0xff192735u);std::fill(intermediate.pixels,intermediate.pixels+size_t(intermediate.w)*intermediate.h,0xff192735u);
    ::SetStretchBltMode(actual.dc,mode);::SetStretchBltMode(expected.dc,mode);::SetStretchBltMode(intermediate.dc,mode);
    HRGN clip=nullptr;
    if(clip_kind==1)clip=::CreateRectRgn(roi.left,roi.top,roi.right,roi.bottom);
    if(clip_kind==2){clip=::CreateRectRgn(vx,vy,roi.left+(roi.right-roi.left)/2,vy+vh);HRGN other=::CreateRectRgn(roi.left+10,roi.top+100,vx+vw,roi.top+150);::CombineRgn(clip,clip,other,RGN_OR);::DeleteObject(other);}
    if(clip){::SelectClipRgn(actual.dc,clip);::SelectClipRgn(expected.dc,clip);::SelectClipRgn(intermediate.dc,clip);}
    base_only(intermediate,frame,vx,vy,vw,vh);::GdiFlush();
    for(int y=roi.top;y<roi.bottom;++y)for(int x=roi.left;x<roi.right;++x)intermediate.pixels[size_t(y)*intermediate.w+x]=0xff192735u;
    if(transformed)physical_oracle(expected,frame,b0,pose,cp,ox,oy,vx,vy,vw,vh,mode);else static_oracle(expected,frame,cp,ox,oy,vx,vy,vw,vh,mode);
    fault=canvas_calls=flush_calls=select_calls=0;reset_target(actual,roi,true,&intermediate);
    check(draw(g,actual,frame,vx,vy,vw,vh)==2,"prepared ROI commit succeeds");observe_intermediate=false;
    check(intermediate_samples>=1&&intermediate_bad==0,"intermediate witness observed before final ROI upload");
    check(target_uploads==1,"one final visible HD ROI upload");if(clip_kind==1)check(target_base_calls==0,"ROI-only clip has legitimate empty outside; no native fallback");
    check(equal_rgb(actual,expected),"commit final EVERY RGB pixel equals unchanged independent oracle");check(frame==untouched,"commit original source unchanged");
    check(::GetStretchBltMode(actual.dc)==mode,"commit caller filter restored");
    if(clip){HRGN after=::CreateRectRgn(0,0,0,0);check(::GetClipRgn(actual.dc,after)==1&&::EqualRgn(clip,after),"commit exact complex/ROI caller clip restored");::DeleteObject(after);::SelectClipRgn(actual.dc,nullptr);::SelectClipRgn(expected.dc,nullptr);::SelectClipRgn(intermediate.dc,nullptr);::DeleteObject(clip);}
    observed_destination=nullptr;intermediate_expected=nullptr;
}
void intermediate_suite(const std::vector<unsigned char>& b0) {
    for(unsigned game=1;game<=3;++game) {
        auto* g=hdvg_create();check(configure(g,game==1?L"yugi-flip.tsv":game==2?L"kaiba-flip.tsv":L"joey-flip.tsv"),"witness profile");
        for(int kind=0;kind<24;++kind){witness_case(g,b0,kind,0,7,9,1067,800,kind%3+1);witness_case(g,b0,kind,0,240,0,1440,1080,kind%3+1);}
        for(int clip=1;clip<=2;++clip)for(int kind:{0,1,3,6})witness_case(g,b0,kind,clip,7,9,1067,800,3);
        for(int kind:{0,1,6})witness_case(g,b0,kind,0,0,0,800,600,2);
        hdvg_destroy(g);
    }
    std::printf("intermediate cumulative checks=%d failures=%d\n",checks,failures);
}
HdvEngine* cpu(unsigned game,const std::vector<unsigned char>& b0,const std::vector<unsigned char>& b1,bool duplicate=false) {
    auto* e=hdv_create(game,HDV_RGB565);const auto reference=ref();check(hdv_add(e,1,reference.data(),reference.size(),L"C:\\master.png")!=0,"CPU reference");
    if(duplicate)check(hdv_add(e,2,reference.data(),reference.size(),L"C:\\other.png")!=0,"duplicate master input");
    check(hdv_add_flip_background(e,b0.data(),b0.size())!=0&&hdv_add_flip_background(e,b1.data(),b1.size())!=0,"copied background banks");
    check(hdv_enable_flip(e,poses,7)!=0,"explicit supported poses");
    for(unsigned state=1;state<=2;++state){const auto sprite=file_bytes(state==1?L"cursor1.bgra":L"cursor2.bgra");check(hdv_add_cursor(e,state,sprite.data(),sprite.size())!=0,"copied cursor bank");}
    check(hdv_seal(e)!=0,"bounded probe postings sealed");return e;
}
}
HDC WINAPI cursor_fault_dc(HDC dc){return fault==1?nullptr: ::CreateCompatibleDC(dc);}
HBITMAP WINAPI cursor_fault_dib(HDC dc,const BITMAPINFO* bi,UINT use,void** bits,HANDLE s,DWORD o){return fault==2?nullptr: ::CreateDIBSection(dc,bi,use,bits,s,o);}
HGDIOBJ WINAPI cursor_fault_select(HDC dc,HGDIOBJ o){++select_calls;return fault==3||(fault==7&&select_calls>3)?HGDI_ERROR: ::SelectObject(dc,o);}
BOOL WINAPI cursor_fault_flush(){++flush_calls;return fault==4&&flush_calls==1?FALSE: ::GdiFlush();}
int WINAPI cursor_fault_stretch(HDC dc,int x,int y,int w,int h,int sx,int sy,int sw,int sh,const void* bits,const BITMAPINFO* bi,UINT use,DWORD rop){
    const bool target=observed_destination&&dc==observed_destination->dc;
    if(target&&bi->bmiHeader.biBitCount==16)++target_base_calls;
    if(target&&bi->bmiHeader.biBitCount==32){sample_intermediate();++target_uploads;}
    if(target&&fault==8&&bi->bmiHeader.biBitCount==16&&target_base_calls==1){::SetPixelV(dc,observed_roi.right+8,observed_roi.top+8,RGB(255,0,255));return 0;}
    if(target&&fault==9&&bi->bmiHeader.biBitCount==32){::SetPixelV(dc,x+1,y+1,RGB(255,0,255));return 0;}
    if(bi->bmiHeader.biBitCount==32){++canvas_calls;if((fault==5&&canvas_calls==3)||(fault==6&&canvas_calls==1)){SetPixelV(dc,x+1,y+1,RGB(255,0,255));return 0;}}
    const int result=::StretchDIBits(dc,x,y,w,h,sx,sy,sw,sh,bits,bi,use,rop);
    if(target&&bi->bmiHeader.biBitCount==16)sample_intermediate();return result;
}
int WINAPI cursor_fault_save(HDC dc){if(observed_destination&&dc==observed_destination->dc){++target_saves;if(fault==11&&target_saves==2)return 0;if(fault==16&&target_saves==1)return 0;}return ::SaveDC(dc);}
BOOL WINAPI cursor_fault_restore(HDC dc,int n){if(observed_destination&&dc==observed_destination->dc){++target_restores;if(fault==12&&target_restores==1)return FALSE;}return ::RestoreDC(dc,n);}
int WINAPI cursor_fault_exclude(HDC dc,int l,int top,int r,int b){if(observed_destination&&dc==observed_destination->dc&&fault==10)return ERROR;return ::ExcludeClipRect(dc,l,top,r,b);}
int WINAPI cursor_fault_clip(HDC dc,int l,int top,int r,int b){if(observed_destination&&dc==observed_destination->dc){++target_clips;if(fault==13&&target_clips==2){::IntersectClipRect(dc,l,top,r,b);return ERROR;}}return ::IntersectClipRect(dc,l,top,r,b);}
int WINAPI cursor_fault_mode(HDC dc,int mode){if(observed_destination&&dc==observed_destination->dc&&fault==14){::SetStretchBltMode(dc,mode);return 0;}return ::SetStretchBltMode(dc,mode);}
BOOL WINAPI cursor_fault_visible(HDC dc,const RECT* r){if(observed_destination&&dc==observed_destination->dc&&fault==15)return FALSE;return ::RectVisible(dc,r);}
int wmain(int argc,wchar_t** argv) {
    if(argc!=2&&!(argc==3&&std::wcscmp(argv[2],L"--witness-only")==0))return 2;dir=argv[1];const auto b0=file_bytes(L"background0.rgb565"),b1=file_bytes(L"background1.rgb565");
    intermediate_suite(b0);if(argc==3){std::printf("witness-only checks=%d failures=%d\n",checks,failures);return failures?1:0;}
    for(unsigned game=1;game<=3;++game)for(const auto& pose:poses)for(unsigned state=0;state<=2;++state) {
        const auto sprite=file_bytes(state==2?L"cursor2.bgra":L"cursor1.bgra");const auto* cp=state?&sprite:nullptr;
        const int ox=std::max(0,static_cast<int>(pose.x)-16),oy=179;const auto frame=native_frame(b0,pose,cp,ox,oy);const auto pristine=frame;
        auto* e=cpu(game,b0,b1);check(hdv_capture(e,frame.data(),800,600,1600,HDV_RGB565)!=0,"owned flip capture");
        HdvFlipPlan plan{};HdvMatch flat{};HdvCursor cursor{};
        check(hdv_flip(e,&plan)&&plan.key==1&&plan.face_x==pose.x&&plan.face_width==pose.width&&plan.q==pose.q,"unique complete flip plan");
        check(!hdv_match(e,&flat),"static HdvMatch semantics unchanged");
        check((hdv_cursor(e,&cursor)!=0)==(state!=0),"supported foreground hypothesis");
        for(unsigned bit=0;bit<16;++bit){auto bad=frame;bad[(90*800+10)*2+bit/8]^=static_cast<unsigned char>(1u<<(bit%8));hdv_capture(e,bad.data(),800,600,1600,HDV_RGB565);check(!hdv_flip(e,&plan),"every unexplained native bit rejects");}
        check(!hdv_cursor(e,&cursor),"failed frame clears cursor/plan");hdv_destroy(e);
        auto* g=hdvg_create();check(configure(g,game==1?L"yugi-flip.tsv":game==2?L"kaiba-flip.tsv":L"joey-flip.tsv"),"strict V3 configuration");
        for(int mode=1;mode<=3;++mode)for(int vw:{800,1067,1280}) {
            const int vh=vw==1067?800:vw*3/4;Dib actual(1320,1000),expected(1320,1000);
            SetStretchBltMode(actual.dc,mode);SetStretchBltMode(expected.dc,mode);
            physical_oracle(expected,frame,b0,pose,cp,ox,oy,7,9,vw,vh,mode);fault=canvas_calls=flush_calls=select_calls=0;
            check(draw(g,actual,frame,7,9,vw,vh)==2,"V3 physical flip");check(equal_rgb(actual,expected),"EVERY RGB pixel independent cleanBG/smoothHD/cursor oracle");
            check(frame==pristine,"original full source unchanged");check(GetStretchBltMode(actual.dc)==mode,"caller mode restored");
        }
        hdvg_destroy(g);
    }
    auto frame=native_frame(b0,poses[1]);auto* e=cpu(1,b0,b1,true);hdv_capture(e,frame.data(),800,600,1600,HDV_RGB565);HdvFlipPlan plan{};
    check(!hdv_flip(e,&plan),"same complete raster different masters ambiguous");hdv_destroy(e);
    e=hdv_create(1,HDV_RGB565);const auto reference=ref();
    for(unsigned i=1;i<=9;++i)check(hdv_add(e,i,reference.data(),reference.size(),L"C:\\master.png")!=0,"candidate-cap setup");
    hdv_add_flip_background(e,b0.data(),b0.size());hdv_enable_flip(e,poses,7);check(hdv_seal(e)!=0,"cap profile seal");hdv_capture(e,frame.data(),800,600,1600,HDV_RGB565);
    check(!hdv_flip(e,&plan),"ninth probe survivor aborts entire capture");hdv_destroy(e);
    /* A flat-card explanation and a transformed explanation must not compete
       by priority: both are complete and therefore the frame is ambiguous. */
    e=hdv_create(1,HDV_RGB565);hdv_add(e,1,reference.data(),reference.size(),L"C:\\front.png");
    std::vector<unsigned char> flat(116000);
    for(unsigned y=0;y<290;++y)std::memcpy(flat.data()+size_t(y)*400,frame.data()+(size_t(y+89)*800+7)*2,400);
    hdv_add(e,2,flat.data(),flat.size(),L"C:\\different.png");hdv_add_flip_background(e,b0.data(),b0.size());hdv_enable_flip(e,poses,7);hdv_seal(e);
    hdv_capture(e,frame.data(),800,600,1600,HDV_RGB565);HdvMatch static_plan{};
    check(!hdv_flip(e,&plan)&&!hdv_match(e,&static_plan),"joint complete static/flip ambiguity fails closed");hdv_destroy(e);
    const auto normal=file_bytes(L"cursor1.bgra"),normal2=file_bytes(L"cursor2.bgra");
    /* Raw static B already contains partial-alpha cursor pixels. V3 must test
       B independently, even though the same frame also explains A+flip+cursor.
       These producer-owned cases use another pose/origin and both states. */
    for(unsigned game=1;game<=3;++game)for(unsigned state=1;state<=2;++state) {
        const auto ambiguous=file_bytes(state==1?L"ambiguous-state1-frame.rgb565":L"ambiguous-state2-frame.rgb565");
        const auto raw=file_bytes(state==1?L"ambiguous-state1.bin":L"ambiguous-state2.bin");
        const auto sprite=file_bytes(state==1?L"cursor1.bgra":L"cursor2.bgra");
        check(ambiguous==native_frame(b0,poses[4],&sprite,103,205),"independently generated whole ambiguous frame");
        unsigned changed_partial=0;
        for(unsigned y=0;y<64;++y)for(unsigned x=0;x<64;++x) {
            const auto* s=sprite.data()+(size_t(y)*64+x)*4;const unsigned q=s[3]/16;
            const uint16_t source=static_cast<uint16_t>(((s[2]/8)<<11)|((s[1]/4)<<5)|(s[0]/8));
            const uint16_t dest=word(ambiguous.data()+(size_t(y+205)*800+x+103)*2);
            if(q>0&&q<15&&independent_blend(source,dest,q,false)!=dest)++changed_partial;
        }
        check(changed_partial>0,"raw B plus partial cursor is different from raw B");
        e=hdv_create(game,HDV_RGB565);
        hdv_add(e,1,reference.data(),reference.size(),L"C:\\front.png");hdv_add(e,2,raw.data(),raw.size(),L"C:\\different.png");
        hdv_add_cursor(e,1,normal.data(),normal.size());hdv_add_cursor(e,2,normal2.data(),normal2.size());
        hdv_add_flip_background(e,b0.data(),b0.size());hdv_add_flip_background(e,b1.data(),b1.size());hdv_enable_flip(e,poses,7);hdv_seal(e);
        check(hdv_capture(e,ambiguous.data(),800,600,1600,HDV_RGB565)!=0,"ambiguous owned capture remains valid base");
        HdvCursor foreground{};HdvFrame snapshot{};
        check(!hdv_match(e,&static_plan)&&!hdv_flip(e,&plan)&&!hdv_cursor(e,&foreground),"raw static versus flip-cursor clears ALL plans");
        check(hdv_snapshot(e,&snapshot)&&std::memcmp(snapshot.pixels,ambiguous.data(),ambiguous.size())==0,"ambiguity preserves complete current owned snapshot");hdv_destroy(e);
        auto* ambiguous_gdi=hdvg_create();
        const wchar_t* config=game==1?(state==1?L"ambiguous-yugi-state1.tsv":L"ambiguous-yugi-state2.tsv"):
            game==2?(state==1?L"ambiguous-kaiba-state1.tsv":L"ambiguous-kaiba-state2.tsv"):
                    (state==1?L"ambiguous-joey-state1.tsv":L"ambiguous-joey-state2.tsv");
        check(configure(ambiguous_gdi,config),"ambiguous GDI strict fixture");
        for(int mode=1;mode<=3;++mode)for(int vw:{800,1067,1280}) {
            const int vh=vw==1067?800:vw*3/4;Dib actual(1320,1000),expected(1320,1000);
            SetStretchBltMode(actual.dc,mode);SetStretchBltMode(expected.dc,mode);base_only(expected,ambiguous,7,9,vw,vh);
            fault=canvas_calls=flush_calls=select_calls=0;
            check(draw(ambiguous_gdi,actual,ambiguous,7,9,vw,vh)==1&&canvas_calls==0,"ambiguous presentation uses current base only");
            check(equal_rgb(actual,expected),"ambiguous EVERY RGB pixel equals same owned base");
        }
        hdvg_destroy(ambiguous_gdi);
    }
    /* Sealed profiles are immutable, including pointers exposed by a live
       generation. Even an exhausted allocator cannot affect a repeated seal. */
    for(unsigned game=1;game<=3;++game) {
        e=cpu(game,b0,b1);const auto current=native_frame(b0,poses[4],&normal,103,205);
        hdv_capture(e,current.data(),800,600,1600,HDV_RGB565);
        HdvFlipPlan before{},after{};HdvCursor cb{},ca{};HdvFrame fb{},fa{};
        check(hdv_flip(e,&before)&&hdv_cursor(e,&cb)&&hdv_snapshot(e,&fb),"repeat seal has live generation pointers");
        const long retained=allocation_probe::live.load();const size_t retained_bytes=allocation_probe::live_bytes.load();bool repeated=true;
        allocation_probe::budget=0;for(unsigned n=0;n<16;++n)if(hdv_seal(e)!=1)repeated=false;allocation_probe::budget=-1;
        check(repeated&&allocation_probe::live.load()==retained&&allocation_probe::live_bytes.load()==retained_bytes,"repeat seal has ZERO allocations or retained growth");
        check(hdv_flip(e,&after)&&hdv_cursor(e,&ca)&&hdv_snapshot(e,&fa),"repeat seal preserves every getter");
        check(before.key==after.key&&before.generation==after.generation&&before.master_path==after.master_path&&before.background565==after.background565&&
            before.face_x==after.face_x&&before.face_width==after.face_width&&before.q==after.q,"repeat seal preserves complete flip plan pointers and values");
        check(cb.bgra==ca.bgra&&cb.x==ca.x&&cb.y==ca.y&&cb.state==ca.state&&cb.generation==ca.generation&&
            fb.pixels==fa.pixels&&fb.generation==fa.generation&&std::memcmp(fa.pixels,current.data(),current.size())==0,"repeat seal preserves cursor and owned current frame");hdv_destroy(e);
    }
    e=hdv_create(1,HDV_RGB565);hdv_add(e,1,reference.data(),reference.size(),L"C:\\front.png");hdv_enable_flip(e,poses,7);
    check(!hdv_seal(e),"missing background fails seal");
    check(!hdv_add_flip_background(e,b0.data(),b0.size())&&!hdv_seal(e)&&!hdv_capture(e,frame.data(),800,600,1600,HDV_RGB565),"failed seal remains terminal");hdv_destroy(e);
    /* Many near-equal flat references agree at probes and differ only at the
       final unoccluded word. V3 must abort its GLOBAL work budget even after
       finding the one earlier exact static+cursor explanation. */
    e=hdv_create(1,HDV_RGB565);
    for(unsigned i=0;i<100;++i){auto near_reference=reference;uint16_t v=static_cast<uint16_t>(word(near_reference.data()+115998)+i);std::memcpy(near_reference.data()+115998,&v,2);hdv_add(e,i+1,near_reference.data(),near_reference.size(),L"C:\\master.png");}
    hdv_add_cursor(e,1,normal.data(),normal.size());hdv_add_cursor(e,2,normal2.data(),normal2.size());
    hdv_add_flip_background(e,b0.data(),b0.size());hdv_enable_flip(e,poses,7);hdv_seal(e);
    auto near_frame=input(1,7,89);
    for(int y=0;y<64;++y)for(int x=0;x<64;++x){const auto* s=normal.data()+(size_t(y)*64+x)*4;auto* d=near_frame.data()+(size_t(y+179)*800+x+80)*2;
        const uint16_t color=static_cast<uint16_t>(((s[2]/8)<<11)|((s[1]/4)<<5)|(s[0]/8)),v=independent_blend(color,word(d),s[3]/16,false);std::memcpy(d,&v,2);}
    hdv_capture(e,near_frame.data(),800,600,1600,HDV_RGB565);
    check(!hdv_match(e,&static_plan)&&!hdv_flip(e,&plan),"V3 static-cursor near-reference global work cap");
    hdv_invalidate(e);check(!hdv_flip(e,&plan),"explicit invalidate clears generation plan");hdv_destroy(e);
    auto* g=hdvg_create();check(configure(g,L"yugi-flip.tsv"),"fault V3 config");
    for(int f=1;f<=7;++f){Dib actual(1320,1000),expected(1320,1000);base_only(expected,frame,0,0,1280,960);fault=f;canvas_calls=flush_calls=select_calls=0;
        const int result=draw(g,actual,frame,0,0,1280,960);fault=0;
        check(result==(f==7?2:1),"resource/partial failure or restoration cleanup");if(f!=7)check(equal_rgb(actual,expected),"failure restores EVERY base RGB pixel");}
    for(int injected=8;injected<=16;++injected) {
        Dib actual(1320,1000),expected(1320,1000);SetStretchBltMode(actual.dc,WHITEONBLACK);SetStretchBltMode(expected.dc,WHITEONBLACK);base_only(expected,frame,7,9,1067,800);
        RECT roi{7+7*1067/800,9+89*800/600,7+207*1067/800,9+379*800/600};reset_target(actual,roi,false);
        fault=injected;canvas_calls=flush_calls=select_calls=0;const int result=draw(g,actual,frame,7,9,1067,800);fault=0;
        check(result==1,"commit partial base/overlay/clip/state fault repairs full current base");
        check(equal_rgb(actual,expected),"commit fault EVERY RGB pixel equals full same-owned base");
        check(GetStretchBltMode(actual.dc)==WHITEONBLACK,"commit failure caller filter restored");observed_destination=nullptr;
    }
    for(int mode=1;mode<=3;++mode)for(int vw:{640,400}) {
        Dib actual(1320,1000),expected(1320,1000);SetStretchBltMode(actual.dc,mode);SetStretchBltMode(expected.dc,mode);
        base_only(expected,frame,7,9,vw,vw*3/4);canvas_calls=flush_calls=select_calls=0;
        check(draw(g,actual,frame,7,9,vw,vw*3/4)==1&&canvas_calls==0,"minification explicit no-HD fallback");
        check(equal_rgb(actual,expected),"minification EVERY RGB pixel same owned base");
    }
    for(const auto& pose:poses) {
        const auto alternate=native_frame(b1,pose);Dib actual(1320,1000),expected(1320,1000);
        HRGN clip=CreateRectRgn(30,180,280,700);SelectClipRgn(actual.dc,clip);SelectClipRgn(expected.dc,clip);
        physical_oracle(expected,alternate,b1,pose,nullptr,0,0,7,9,1067,800,1);canvas_calls=flush_calls=select_calls=0;
        check(draw(g,actual,alternate,7,9,1067,800)==2,"alternate background with caller clip");
        check(equal_rgb(actual,expected),"alternate BG/clip EVERY RGB pixel and outside unchanged");
        SelectClipRgn(actual.dc,nullptr);SelectClipRgn(expected.dc,nullptr);DeleteObject(clip);
    }
    Dib owned(1320,1000);int other=1;std::thread wrong([&]{other=draw(g,owned,frame);});wrong.join();check(other==0,"V3 wrong owner refused");
    const auto fault_config=path(L"yugi-flip.tsv");
    bool complete=false;unsigned failed_allocations=0;
    for(long limit=0;limit<1024&&!complete;++limit) {
        const long live=allocation_probe::live.load();const size_t live_bytes=allocation_probe::live_bytes.load();auto* context=hdvg_create();
        allocation_probe::budget=limit;const bool configured=hdvg_configure(context,fault_config.c_str())!=0;allocation_probe::budget=-1;
        if(!configured){++failed_allocations;check(draw(context,owned,frame)==0,"allocation failure publishes no partial profile");}
        else complete=true;
        hdvg_destroy(context);check(allocation_probe::live.load()==live&&allocation_probe::live_bytes.load()==live_bytes,"EVERY failed configure/seal allocation and exact byte total is reclaimed");
    }
    check(complete&&failed_allocations>100,"all V3 profile/posting/coordinate allocations faulted through success");
    for(long limit=0;limit<5;++limit) {
        auto* context=hdvg_create();check(configure(context,L"yugi-flip.tsv"),"present allocation setup");
        Dib actual(1320,1000),expected(1320,1000);base_only(expected,frame,0,0,1280,960);
        allocation_probe::budget=limit;const int result=draw(context,actual,frame,0,0,1280,960);allocation_probe::budget=-1;
        check(result==1||result==2,"present ordinary bad_alloc caught at API boundary");
        if(result==1)check(equal_rgb(actual,expected),"present allocation failure EVERY base RGB pixel retained");
        hdvg_destroy(context);
    }
    std::printf("flip configure allocation fault points=%u\n",failed_allocations);
    Dib cleared(1320,1000);
    check(!configure(g,L"bad-flip-hash.tsv")&&draw(g,cleared,frame)==0,"bad bank clears old engine");
    check(!configure(g,L"bad-flip-pose.tsv"),"unknown pose metadata rejected");hdvg_destroy(g);
    std::printf("flip checks=%d failures=%d\n",checks,failures);return failures?1:0;
}
