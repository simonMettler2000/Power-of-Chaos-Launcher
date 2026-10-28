/* Arithmetic fixtures only. Tests the production decoder and allocation
   transaction, plus a memory-only WIC comparison/old-path benchmark. */
#define wmain allocator_legacy_main
#include "allocator_test.cpp"
#undef wmain
#include <wincodec.h>
#include <wrl/client.h>
#include <chrono>
using Microsoft::WRL::ComPtr;
constexpr size_t OutputBytes=size_t(800)*1160*4;
bool sentinel(const std::vector<unsigned char>& v){for(auto x:v)if(x!=0xa5)return false;return true;}
bool pixels(const unsigned char* p){
    for(size_t y=0;y<1160;++y)for(size_t x=0;x<800;++x){size_t at=(y*800+x)*4;
        if(p[at]!=(x*13+y*17)%256 || p[at+1]!=(x*11+y*5)%256 || p[at+2]!=(x*7+y*3)%256 || p[at+3]!=255)return false;
    }return true;
}
bool old_path(std::vector<unsigned char>& png,std::vector<unsigned char>& out){
    if(!hdv_validate_master_png(png.data(),png.size()))return false;
    ComPtr<IWICImagingFactory> factory;ComPtr<IWICStream> stream;ComPtr<IWICBitmapDecoder> d;
    ComPtr<IWICBitmapFrameDecode> f;ComPtr<IWICFormatConverter> c;
    return SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(factory.GetAddressOf()))) &&
        SUCCEEDED(factory->CreateStream(stream.GetAddressOf())) && SUCCEEDED(stream->InitializeFromMemory(png.data(),static_cast<DWORD>(png.size()))) &&
        SUCCEEDED(factory->CreateDecoderFromStream(stream.Get(),nullptr,WICDecodeMetadataCacheOnLoad,d.GetAddressOf())) &&
        SUCCEEDED(d->GetFrame(0,f.GetAddressOf())) && SUCCEEDED(factory->CreateFormatConverter(c.GetAddressOf())) &&
        SUCCEEDED(c->Initialize(f.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom)) &&
        SUCCEEDED(c->CopyPixels(nullptr,3200,static_cast<UINT>(out.size()),out.data()));
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;std::wstring directory=argv[1];checks=0;failures=0;
    const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);if(FAILED(com))return 3;
    std::vector<unsigned char> good;
    for(const wchar_t* name:{L"filter0",L"filter1",L"filter2",L"filter3",L"filter4",L"mixed",L"adam7"}){
        std::ifstream file(directory+L"\\"+name+L".png",std::ios::binary);std::vector<unsigned char> png{std::istreambuf_iterator<char>(file),{}};
        if(png.empty()){CoUninitialize();return 4;}const auto original=png;
        std::vector<unsigned char> out(OutputBytes+32,0xa5),wic(OutputBytes);
        reset(0);check(hdv_decode_master_bgra(png.data(),png.size(),out.data()+16,OutputBytes),"strict BGRA decode");
        const size_t allocations=allocation_index;check(empty(),"decode leaves exact clean allocation state");
        check(pixels(out.data()+16),"all928000 BGRA pixels equal arithmetic RGB oracle");
        check(old_path(png,wic)&&std::memcmp(wic.data(),out.data()+16,OutputBytes)==0,"all BGRA pixels equal old validated WIC path");
        check(png==original,"input bytes unchanged");
        check(sentinel(std::vector<unsigned char>(out.begin(),out.begin()+16))&&sentinel(std::vector<unsigned char>(out.end()-16,out.end())),"output boundary guards intact");
        for(size_t index=1;index<=allocations;++index){
            std::fill(out.begin(),out.end(),0xa5);reset(index);
            check(!hdv_decode_master_bgra(png.data(),png.size(),out.data()+16,OutputBytes),"every decoder allocation failure rejects");
            check(injected==1&&empty()&&sentinel(out),"failed decode publishes no pixel and releases every block");
            reset(0);check(hdv_decode_master_bgra(png.data(),png.size(),out.data()+16,OutputBytes)&&pixels(out.data()+16)&&empty(),"same-thread full-pixel recovery");
        }
        reset(0);std::fill(out.begin(),out.end(),0xa5);
        check(!hdv_decode_master_bgra(png.data(),png.size(),nullptr,OutputBytes),"null output rejected");
        check(!hdv_decode_master_bgra(png.data(),png.size(),out.data()+16,OutputBytes-1),"short output rejected");
        check(!hdv_decode_master_bgra(png.data(),png.size(),out.data()+16,OutputBytes+1),"oversized output rejected");
        check(!hdv_decode_master_bgra(png.data(),png.size(),png.data(),OutputBytes)&&png==original,"overlapping output rejected without input mutation");
        check(!hdv_decode_master_bgra(nullptr,png.size(),out.data()+16,OutputBytes)&&sentinel(out),"null input leaves output unchanged");
        png.back()^=1;check(!hdv_decode_master_bgra(png.data(),png.size(),out.data()+16,OutputBytes)&&sentinel(out)&&empty(),"corrupt PNG leaves output unchanged");
        png=original;
        decoder::transaction_active=true;check(!hdv_decode_master_bgra(png.data(),png.size(),out.data()+16,OutputBytes)&&decoder::transaction_active&&sentinel(out),"busy decoder rejected without mutation");decoder::transaction_active=false;
        if(std::wcscmp(name,L"mixed")==0)good=png;
    }
    /* Both paths perform a cold decode every iteration, with no image cache.
       Includes strict extent validation in both; compares equivalent pixels. */
    std::vector<unsigned char> out(OutputBytes);double old_ms=0,new_ms=0;
    for(int i=0;i<8;++i){
        auto start=std::chrono::steady_clock::now();check(old_path(good,out),"benchmark old path valid");
        old_ms+=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count();
        start=std::chrono::steady_clock::now();check(hdv_decode_master_bgra(good.data(),good.size(),out.data(),out.size()),"benchmark new path valid");
        new_ms+=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count();
    }
    check(empty(),"final allocation state clean");CoUninitialize();
    std::printf("8 cold loads: old strict+WIC %.3fms; strict BGRA %.3fms (fixture only, not game benchmark)\n",old_ms,new_ms);
    std::printf("%zu BGRA checks, %zu failures\n",checks,failures);return failures?1:0;
}
