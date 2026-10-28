/* Authored full-provider GDI test. The provider observer reads its actual
   persistent window HDC after base and overlay. A separate unhooked process
   independently draws the expected full frame into its own real window on the
   same display device before this host reads observed pixels. */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <ddraw.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <cstdlib>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <cmath>
#include "hd_visible_test_observer.h"
#include "gdi_window_oracle.h"
#include <algorithm>
using Microsoft::WRL::ComPtr;
namespace {
void messages() {
    MSG m{};
    while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
}
std::wstring utf8(const std::string& value) {
    const int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0);
    if(n<=0)return {};
    std::wstring result(size_t(n),L'\0');
    if(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),result.data(),n)!=n)return {};
    return result;
}
bool master(const std::wstring& path,std::vector<unsigned char>& pixels) {
    ComPtr<IWICImagingFactory> factory;ComPtr<IWICBitmapDecoder> decoder;
    ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICFormatConverter> converter;UINT w=0,h=0;
    if(FAILED(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(factory.GetAddressOf()))) ||
       FAILED(factory->CreateDecoderFromFilename(path.c_str(),nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,decoder.GetAddressOf())) ||
       FAILED(decoder->GetFrame(0,frame.GetAddressOf())) || FAILED(frame->GetSize(&w,&h)) || w!=800 || h!=1160 ||
       FAILED(factory->CreateFormatConverter(converter.GetAddressOf())) ||
       FAILED(converter->Initialize(frame.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom)))return false;
    pixels.resize(800*1160*4);
    return SUCCEEDED(converter->CopyPixels(nullptr,3200,static_cast<UINT>(pixels.size()),pixels.data()));
}
struct Oracle {
    std::vector<uint32_t> expected;uint32_t* pixels=nullptr;
    int w=0,h=0,vx=0,vy=0,vw=0,vh=0;unsigned counter=0;
    HWND owner=nullptr;std::wstring root,master_path;
    bool create(int width,int height) {
        w=width;h=height;vw=w;vh=int(std::lround(double(w)*3/4));
        if(double(h)/w<.75){vw=int(std::lround(double(w)/vh*h));vh=h;}
        if(vw>w)vw=w;if(vh>h)vh=h;vx=w/2-vw/2;vy=h/2-vh/2;return true;
    }
    bool draw(const std::vector<unsigned char>& input,uint32_t format,const std::vector<unsigned char>* art,int rx,int ry,bool native_base=false,bool black_only=false,bool clear=false,bool backend_base=true) {
        (void)clear;
        if(!owner||root.empty()||input.size()!=960000)return false;
        const std::wstring stem=root+L"\\oracle-"+std::to_wstring(++counter);
        const std::wstring request_path=stem+L".request",input_path=stem+L".input",output_path=stem+L".expected.bgra";
        HdvGdiOracleRequest request{};request.magic=0x4f564448;request.version=1;request.width=uint32_t(w);request.height=uint32_t(h);request.format=format;request.vx=vx;request.vy=vy;request.vw=vw;request.vh=vh;request.rx=rx;request.ry=ry;request.overlay=art?1u:0u;request.native_base=native_base?1u:0u;request.black_only=black_only?1u:0u;request.backend_base=backend_base?1u:0u;request.owner_window=uint64_t(reinterpret_cast<uintptr_t>(owner));
        {std::ofstream f(request_path,std::ios::binary);f.write(reinterpret_cast<char*>(&request),sizeof(request));if(!f)return false;}
        {std::ofstream f(input_path,std::ios::binary);f.write(reinterpret_cast<const char*>(input.data()),std::streamsize(input.size()));if(!f)return false;}
        wchar_t image[32768]{};const DWORD n=GetModuleFileNameW(nullptr,image,32768);if(!n||n>=32768)return false;
        std::wstring executable(image,n);executable=executable.substr(0,executable.find_last_of(L"\\/"))+L"\\GdiWindowOracle.exe";
        auto quoted=[](const std::wstring& value){return L"\""+value+L"\"";};
        const std::wstring master=art?master_path:L"-";
        const std::wstring* values[]={&executable,&request_path,&input_path,&master,&output_path};
        for(const auto* value:values)if(value->find(L'"')!=std::wstring::npos)return false;
        std::wstring command=quoted(executable)+L" "+quoted(request_path)+L" "+quoted(input_path)+L" "+quoted(master)+L" "+quoted(output_path);
        STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};
        if(!CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&process))return false;
        const DWORD wait=WaitForSingleObject(process.hProcess,60000);DWORD code=1;GetExitCodeProcess(process.hProcess,&code);CloseHandle(process.hThread);CloseHandle(process.hProcess);if(wait!=WAIT_OBJECT_0||code)return false;
        std::ifstream f(output_path,std::ios::binary|std::ios::ate);if(!f||f.tellg()!=std::streamoff(w)*h*4)return false;f.seekg(0);expected.resize(size_t(w)*h);if(!f.read(reinterpret_cast<char*>(expected.data()),std::streamsize(expected.size()*4)))return false;pixels=expected.data();
        SetWindowPos(owner,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW);UpdateWindow(owner);return true;
    }
};

bool upload(IDirectDrawSurface* primary,const std::vector<unsigned char>& pixels) {
    DDSURFACEDESC d{};d.dwSize=sizeof(d);
    if(FAILED(primary->Lock(nullptr,&d,DDLOCK_WAIT|DDLOCK_WRITEONLY,nullptr)))return false;
    // The pinned provider reports RGB565 masks even with rgb555=true; its
    // actual BMI selects 555. That existing reporting behavior stays intact.
    const bool valid=d.dwWidth==800 && d.dwHeight==600 && d.lPitch>=1600 && d.lpSurface && d.ddpfPixelFormat.dwRGBBitCount==16;
    if(valid)for(size_t y=0;y<600;++y)std::memcpy(static_cast<unsigned char*>(d.lpSurface)+y*d.lPitch,pixels.data()+y*1600,1600);
    const HRESULT result=primary->Unlock(nullptr);return valid && SUCCEEDED(result);
}
bool reviewed(uint32_t game,int x,int y) {
    if(game==1)return (x==5 && y==72)||(x==11 && y==88)||(x==7 && y==89);
    if(game==2)return (x==5 && y==72)||(x==16 && y==86)||(x==7 && y==89);
    return game==3 && ((x==2 && y==74)||(x==6 && y==87)||(x==7 && y==89));
}
}
int wmain(int argc,wchar_t** argv) {
    if(argc<5){std::fprintf(stderr,"Usage: SyntheticGdiProviderProbe test-provider.dll config.tsv reference.bin fresh-output [--base-control] [--native] [--letterbox] [--vhack] [--child] [--bnet] [--restart] [--roi=X,Y]\n");return 2;}
    bool base_control=false,native=false,letterbox=false,vhack=false,child_case=false,bnet_case=false,restart=false;int rx=-1,ry=-1;
    for(int i=5;i<argc;++i) {
        if(!std::wcscmp(argv[i],L"--base-control"))base_control=true;
        else if(!std::wcscmp(argv[i],L"--native"))native=true;
        else if(!std::wcscmp(argv[i],L"--letterbox"))letterbox=true;
        else if(!std::wcscmp(argv[i],L"--vhack"))vhack=true;
        else if(!std::wcscmp(argv[i],L"--child"))child_case=true;
        else if(!std::wcscmp(argv[i],L"--bnet"))bnet_case=true;
        else if(!std::wcscmp(argv[i],L"--restart"))restart=true;
        else if(std::wcsncmp(argv[i],L"--roi=",6)==0) {
            wchar_t* end=nullptr;const long x=std::wcstol(argv[i]+6,&end,10);if(!end||*end!=L',')return 2;
            const long y=std::wcstol(end+1,&end,10);if(!end||*end||x<0||x>799||y<0||y>599)return 2;rx=int(x);ry=int(y);
        } else return 2;
    }
    uint32_t game=0,format=0;std::wstring art_path;
    {std::ifstream f(argv[2],std::ios::binary);std::string line;std::getline(f,line);if(line!="POC_HD_VISIBLE_V1")return 2;
     std::getline(f,line);std::istringstream meta(line);meta>>game>>format;std::getline(f,line);if(!line.empty()&&line.back()=='\r')line.pop_back();
     const auto one=line.find('\t'),two=one==std::string::npos?one:line.find('\t',one+1);
     if(two==std::string::npos||game<1||game>3||format<1||format>2)return 2;art_path=utf8(line.substr(two+1));}
    if(rx<0){rx=game==3?2:5;ry=game==3?74:72;}if(!reviewed(game,rx,ry))return 2;
    const bool expect_overlay=!base_control && format==1 && !vhack;
    const uint32_t normal_flags=vhack?HDV_TEST_GDI_VHACK:0;
    const int width=native?(letterbox?1024:800):1280,height=native?600:(letterbox?800:960);
    if(GetSystemMetrics(SM_CXSCREEN)<width||GetSystemMetrics(SM_CYSCREEN)<height){std::fprintf(stderr,"Full real-HDC readback needs the complete synthetic window visible.\n");return 3;}
    SetProcessDPIAware();
    const std::wstring out=argv[4];if(!CreateDirectoryW(out.c_str(),nullptr))return 3;
    if(FAILED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED)))return 4;
    const std::wstring ini=out+L"\\ddraw.ini";
    {std::ofstream f(ini);f<<"[ddraw]\nwidth="<<width<<"\nheight="<<height<<"\nwindowed=true\nmaintas=true\naspect_ratio=4:3\nrenderer=gdi\nmaxfps=30\nminfps=-1\nsinglecpu=false\nlock_surfaces=true\nadjmouse=false\nnoactivateapp=true\nresizable=false\nborder=false\nvsync=false\nvhack="<<(vhack?"true":"false")<<"\nfixchilds="<<(child_case?1:0)<<"\nrgb555="<<(format==2?"true":"false")<<'\n';}
    SetEnvironmentVariableW(L"CNC_DDRAW_CONFIG_FILE",ini.c_str());SetEnvironmentVariableW(L"POC_HD_CONFIG",argv[2]);
    WNDCLASSW c{};c.lpfnWndProc=DefWindowProcW;c.hInstance=GetModuleHandleW(nullptr);c.lpszClassName=L"OwnedGdiFullProviderProbe";
    if(!RegisterClassW(&c))return 5;
    // The pinned provider clears vhack for unknown titles. This one verified
    // title gate uses only owned-primary pixels; our nonzero corner keeps the
    // special low-resolution crop inactive. No original game is involved.
    const wchar_t* title=vhack?L"KKND Xtreme":L"Authored GDI provider test";
    HWND window=CreateWindowW(c.lpszClassName,title,WS_POPUP,0,0,width,height,nullptr,nullptr,c.hInstance,nullptr);if(!window)return 5;
    ShowWindow(window,SW_SHOW);UpdateWindow(window);
    HMODULE module=LoadLibraryExW(argv[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)return 6;
    auto create=reinterpret_cast<HRESULT(WINAPI*)(GUID*,LPDIRECTDRAW*,IUnknown*)>(GetProcAddress(module,"DirectDrawCreate"));
    auto read=reinterpret_cast<int(*)(void*,uint32_t,HdvTestResult*)>(GetProcAddress(module,"hdv_test_read"));
    auto metadata=reinterpret_cast<int(*)(HdvGdiTestState*)>(GetProcAddress(module,"hdv_test_gdi_read_state"));
    auto control=reinterpret_cast<int(*)(uint32_t)>(GetProcAddress(module,"hdv_test_gdi_control"));
    if(!create||!read||!metadata||!control||!control(0))return 7;
    IDirectDraw* draw=nullptr;IDirectDrawSurface* primary=nullptr;
    if(FAILED(create(nullptr,&draw,nullptr))||FAILED(draw->SetCooperativeLevel(window,DDSCL_FULLSCREEN|DDSCL_EXCLUSIVE))||FAILED(draw->SetDisplayMode(800,600,16)))return 8;
    auto create_primary=[&](){DDSURFACEDESC d{};d.dwSize=sizeof(d);d.dwFlags=DDSD_CAPS;d.ddsCaps.dwCaps=DDSCAPS_PRIMARYSURFACE;return SUCCEEDED(draw->CreateSurface(&d,&primary,nullptr));};
    if(!create_primary())return 9;
    std::vector<unsigned char> input(800*600*2),reference(116000),art;
    {std::ifstream f(argv[3],std::ios::binary|std::ios::ate);if(!f||f.tellg()!=116000)return 10;f.seekg(0);if(!f.read(reinterpret_cast<char*>(reference.data()),116000))return 10;}
    for(size_t n=0;n<input.size();n+=2){const uint16_t value=uint16_t(0x1042u+((n/2)%31));std::memcpy(input.data()+n,&value,2);}
    for(size_t y=0;y<290;++y)std::memcpy(input.data()+((size_t(ry)+y)*800+rx)*2,reference.data()+y*400,400);
    if(expect_overlay&&!master(art_path,art))return 10;
    Oracle oracle;if(!oracle.create(width,height))return 10;oracle.owner=window;oracle.root=out;oracle.master_path=art_path;
    std::vector<unsigned char> captured(16*1024*1024);HdvTestResult result{};HdvGdiTestState state{};
    struct Case {std::string name;uint64_t wrong,sequence,instance,bars;uint32_t flags;int backend,vx,vy,vw,vh;};std::vector<Case> cases;
    bool ok=true;uint64_t last_sequence=0;unsigned stage_stamp=0;int stamp_x=0,stamp_y=0;
    auto wait=[&](bool replaced,uint32_t flags,uint64_t minimum_instance){const ULONGLONG end=GetTickCount64()+10000;while(GetTickCount64()<end){messages();if(read(captured.data(),static_cast<uint32_t>(captured.size()),&result)&&metadata(&state)&&state.sequence==result.sequence&&result.sequence>last_sequence&&(result.replaced!=0)==replaced&&state.guard_flags==flags&&state.render_instance>=minimum_instance&&state.backend_result!=3){uint32_t value=0;std::memcpy(&value,captured.data()+(size_t(stamp_y)*width+stamp_x)*4,4);if((value&0xffffff)==(oracle.pixels[size_t(stamp_y)*width+stamp_x]&0xffffff))return true;}Sleep(10);}std::printf("WAIT FAIL seq=%llu instance=%llu flags=%u backend=%d expectedFlags=%u replaced=%u\n",static_cast<unsigned long long>(result.sequence),static_cast<unsigned long long>(state.render_instance),state.guard_flags,state.backend_result,flags,result.replaced);return false;};
    auto stage=[&](const char* name,bool replaced,uint32_t flags,bool native_base=false,bool black=false,uint64_t minimum_instance=1){
        const bool clear=cases.empty()||std::strcmp(name,"mode-restart-recovery")==0;
        const bool backend_base=format==1&&flags==0;
        // A fresh flat source stamp, outside every admitted preview, ties even
        // unchanged fallback status to this upload. It masks no compared pixel.
        const unsigned shade=(++stage_stamp%30)+1;const uint16_t word=uint16_t(format==1?((shade<<11)|(shade<<6)|shade):((shade<<10)|(shade<<5)|shade));
        for(int y=580;y<588;++y)for(int x=780;x<788;++x)std::memcpy(input.data()+(size_t(y)*800+x)*2,&word,2);
        stamp_x=native_base?784:oracle.vx+784*oracle.vw/800;stamp_y=native_base?584:oracle.vy+584*oracle.vh/600;
        if(!oracle.draw(input,format,replaced?&art:nullptr,rx,ry,native_base,black,clear,backend_base))return false;
        // Discard all observations produced while the expected window covered
        // the provider. Only a subsequent real provider frame can be selected.
        if(read(captured.data(),static_cast<uint32_t>(captured.size()),&result))last_sequence=std::max(last_sequence,result.sequence);
        if(!upload(primary,input)||!wait(replaced,flags,minimum_instance))return false;
        uint64_t wrong=0;
        const bool geometry=state.vx==oracle.vx&&state.vy==oracle.vy&&state.vw==oracle.vw&&state.vh==oracle.vh;
        if(result.width!=uint32_t(width)||result.height!=uint32_t(height)||result.byte_count!=uint32_t(width*height*4)||!geometry)wrong=uint64_t(width)*height;
        else for(size_t i=0;i<size_t(width)*height;++i){uint32_t actual=0;std::memcpy(&actual,captured.data()+i*4,4);if((actual&0xffffff)!=(oracle.pixels[i]&0xffffff))++wrong;}
        uint64_t bars=0;
        for(int y=0;y<height;++y)for(int x=0;x<width;++x)
            if(x<oracle.vx||x>=oracle.vx+oracle.vw||y<oracle.vy||y>=oracle.vy+oracle.vh){uint32_t actual=0;std::memcpy(&actual,captured.data()+(size_t(y)*width+x)*4,4);if(actual&0xffffff)++bars;}
        if(!(flags&HDV_TEST_GDI_CHILD)&&bars)wrong+=bars;
        // A guard must reach the actual original base branch, not claim HD.
        if(flags && state.backend_result!=0)++wrong;
        cases.push_back({name,wrong,result.sequence,state.render_instance,bars,state.guard_flags,state.backend_result,state.vx,state.vy,state.vw,state.vh});last_sequence=result.sequence;ok=ok&&wrong==0;
        std::wstring file=out+L"\\"+utf8(name)+L".bgra";std::ofstream f(file,std::ios::binary);f.write(reinterpret_cast<char*>(captured.data()),result.byte_count);
        std::ofstream expected(out+L"\\"+utf8(name)+L"-expected.bgra",std::ios::binary);expected.write(reinterpret_cast<char*>(oracle.pixels),std::streamsize(width)*height*4);
        return true;
    };
    if(!stage("matched",expect_overlay,normal_flags))return 11;
    input[((size_t(ry)+289)*800+rx)*2+399]^=1;
    if(!stage("occluded-fallback",false,normal_flags))return 12;
    input[((size_t(ry)+289)*800+rx)*2+399]^=1;
    if(!stage("unoccluded-recovery",expect_overlay,normal_flags))return 12;
    if(child_case){
        // A real, hidden, non-Edit child HWND is enough for the pinned
        // EnumChildWindows detector; it paints nothing into observed pixels.
        HWND child=CreateWindowW(c.lpszClassName,L"Owned child detection probe",WS_CHILD,700,500,40,40,window,nullptr,c.hInstance,nullptr);
        if(!child||!stage("child-native-fallback",false,normal_flags|HDV_TEST_GDI_CHILD,true))return 14;
        DestroyWindow(child);messages();if(!stage("child-removal-recovery",expect_overlay,normal_flags))return 14;
    }
    if(bnet_case){
        // This explicit synthetic-only control scopes bnet_active to the
        // locked renderer branch; the actual original FillRect branch runs.
        if(!control(HDV_TEST_GDI_FORCE_BNET)||!stage("bnet-black-fallback",false,normal_flags|HDV_TEST_GDI_BNET,false,true))return 15;
        if(!control(0)||!stage("bnet-exit-recovery",expect_overlay,normal_flags))return 15;
    }
    uint64_t restart_before=0,restart_after=0;
    if(restart){
        restart_before=state.render_instance;const uint64_t stopped_before=state.stops;
        primary->Release();primary=nullptr;
        if(FAILED(draw->RestoreDisplayMode())||!metadata(&state)||state.stops<=stopped_before)return 16;
        if(FAILED(draw->SetDisplayMode(800,600,16))||!create_primary()||!stage("mode-restart-recovery",expect_overlay,normal_flags,false,false,restart_before+1))return 16;
        restart_after=state.render_instance;
        input[((size_t(ry)+289)*800+rx)*2+399]^=1;
        if(!stage("mode-restart-occlusion",false,normal_flags,false,false,restart_after))return 16;
    }
    primary->Release();draw->Release();messages();
    if(!metadata(&state)||state.starts!=state.stops)ok=false;
    {std::ofstream f(out+L"\\result.json");f<<"{\"scope\":\"synthetic actual provider window HDC\",\"originalGameExecuted\":false,\"productionBinaryProven\":false,\"width\":"<<width<<",\"height\":"<<height<<",\"game\":"<<game<<",\"format\":"<<format<<",\"x\":"<<rx<<",\"y\":"<<ry<<",\"letterbox\":"<<(letterbox?"true":"false")<<",\"vhackConfigured\":"<<(vhack?"true":"false")<<",\"restartBefore\":"<<restart_before<<",\"restartAfter\":"<<restart_after<<",\"starts\":"<<state.starts<<",\"stops\":"<<state.stops<<",\"cases\":[";
     for(size_t i=0;i<cases.size();++i){const auto& item=cases[i];if(i)f<<',';f<<"{\"name\":\""<<item.name<<"\",\"wrongPixels\":"<<item.wrong<<",\"nonblackBarPixels\":"<<item.bars<<",\"sequence\":"<<item.sequence<<",\"renderInstance\":"<<item.instance<<",\"guardFlags\":"<<item.flags<<",\"backendResult\":"<<item.backend<<",\"viewport\":["<<item.vx<<','<<item.vy<<','<<item.vw<<','<<item.vh<<"]}";}
     f<<"],\"passed\":"<<(ok?"true":"false")<<"}";}
    DestroyWindow(window);messages();FreeLibrary(module);CoUninitialize();
    std::printf("Actual GDI HDC full-frame guard/lifecycle comparisons: %s\n",ok?"PASS":"FAIL");return ok?0:13;
}
