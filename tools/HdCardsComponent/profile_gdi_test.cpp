/* Own memory-DC fault injection; no provider, game, GUI or display driver. */
#define wmain normal_synthetic_main
#include "hd_visible_gdi_test.cpp"
#undef wmain
namespace {
bool equal(Dib& a,Dib& b){GdiFlush();for(int y=0;y<a.h;++y)for(int x=0;x<a.w;++x)if((a.pixels[size_t(y)*a.w+x]&0xffffff)!=(b.pixels[size_t(y)*b.w+x]&0xffffff))return false;return true;}
void base_oracle(Dib& d,const std::vector<unsigned char>& source,bool native=false){
    struct B {BITMAPINFOHEADER h;DWORD masks[3];} b{};
    b.h.biSize=sizeof(BITMAPINFOHEADER);b.h.biWidth=800;b.h.biHeight=-600;b.h.biPlanes=1;b.h.biBitCount=16;b.h.biCompression=BI_BITFIELDS;
    b.masks[0]=0xf800;b.masks[1]=0x07e0;b.masks[2]=0x001f;
    if(native)::SetDIBitsToDevice(d.dc,0,0,800,600,0,0,0,600,source.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS);
    else ::StretchDIBits(d.dc,9,11,1601,1201,0,0,800,600,source.data(),reinterpret_cast<BITMAPINFO*>(&b),DIB_RGB_COLORS,SRCCOPY);
}
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;dir=argv[1];checks=0;failures=0;
    auto source=input(1,2,74);HdvGdi* p=hdvg_create();
    Dib d(1640,1240),oracle(1640,1240);
    SetStretchBltMode(d.dc,HALFTONE);SetStretchBltMode(oracle.dc,HALFTONE);
    SetBrushOrgEx(d.dc,3,7,nullptr);SetBrushOrgEx(oracle.dc,3,7,nullptr);
    std::ifstream manifest(path(L"gdi-manifest.txt"));std::string name;size_t rejected=0;
    while(std::getline(manifest,name)){
        std::wstring file(name.begin(),name.end());d.clear();oracle.clear();base_oracle(oracle,source);
        check(configure(p,file.c_str()),"profile fixture configuration");
        check(draw(p,d,source,9,11,1601,1201)==1,"unsupported profile uses original base fallback");
        check(equal(d,oracle),"profile fallback every destination pixel equals original base");++rejected;
    }
    check(configure(p,L"good.tsv"),"valid profile recovers after rejections");
    check(draw(p,d,source,9,11,1601,1201)==2,"valid metadata-free profile overlays");
    hdvg_destroy(p);std::printf("%d profile GDI checks, %zu rejected masters, %d failures\n",checks,rejected,failures);return failures?1:0;
}
