#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdlib>
#include <cstdio>
#include <fstream>
#include <string>
#include <vector>
#include <new>
thread_local bool track_arrays=false,fail_array=false;
thread_local size_t array_calls=0,array_requested=0,array_live=0;
thread_local void* array_pointer=nullptr;
void* operator new[](size_t n) {
    if(track_arrays){++array_calls;array_requested=n;if(fail_array){fail_array=false;throw std::bad_alloc();}}
    void* p=std::malloc(n);if(!p)throw std::bad_alloc();
    if(track_arrays){array_pointer=p;++array_live;}return p;
}
void operator delete[](void* p) noexcept {
    if(p&&p==array_pointer){array_pointer=nullptr;--array_live;}std::free(p);
}
void operator delete[](void* p,size_t) noexcept {operator delete[](p);}
#include "hd_visible_png.cpp"
size_t checks=0,failures=0;
void check(bool b,const char* name){++checks;if(!b){++failures;std::printf("FAIL %s\n",name);}}
std::wstring wide(const std::string& text){
    int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0);
    std::wstring result(n,L'\0');if(n)MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),&result[0],n);return result;
}
bool clean(){return !decoder::live_bytes&&!decoder::live_blocks&&!decoder::allocations&&!decoder::transaction_active&&!array_live&&!array_pointer;}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;
    std::ifstream manifest(argv[1]);std::string row;size_t masters=0;std::vector<unsigned char> good;
    while(std::getline(manifest,row)){
        const size_t tab=row.rfind('\t');if(tab==std::string::npos)return 3;
        const bool capacity_case=row.substr(tab+1)=="2";const bool expected=row.substr(tab+1)=="1";std::ifstream f(wide(row.substr(0,tab)),std::ios::binary);
        std::vector<unsigned char> bytes{std::istreambuf_iterator<char>(f),{}};if(bytes.empty())return 4;
        size_t idat=0;bool framed=valid_png(bytes.data(),bytes.size(),idat);
        array_calls=0;array_requested=0;track_arrays=true;
        const bool result=hdv_validate_master_png(bytes.data(),bytes.size());track_arrays=false;
        check(result==expected,"master/profile admission result");check(clean(),"master/profile cleanup");
        check((expected||capacity_case)?(framed&&array_calls==1&&array_requested==idat&&idat<=16u*1024*1024):(!framed&&array_calls==0),"exact bounded IDAT allocation or preallocation profile rejection");
        if(expected){++masters;if(good.empty())good=bytes;}
    }
    check(!good.empty(),"at least one valid master loaded");
    array_calls=0;array_requested=0;fail_array=true;track_arrays=true;
    check(!hdv_validate_master_png(good.data(),good.size()),"exact IDAT new-array failure returns false");track_arrays=false;
    check(array_calls==1&&!fail_array&&clean(),"new-array failure cleanup and indexed injection");
    check(hdv_validate_master_png(good.data(),good.size())&&clean(),"same-thread recovery after IDAT allocation failure");
    std::printf("%zu master/profile checks, %zu valid masters, %zu failures\n",checks,masters,failures);return failures?1:0;
}
