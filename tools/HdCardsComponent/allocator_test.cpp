#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdlib>
#include <cstdio>
#include <fstream>
#include <string>
#include <vector>
#include <cstdint>
#include <cstring>
#include <unordered_map>
#include <thread>
#include <atomic>
thread_local size_t allocation_index=0,fail_index=0,injected=0,raw_errors=0;
thread_local std::unordered_map<uintptr_t,size_t> raw_blocks;
void* probe_malloc(size_t n) {
    if(++allocation_index==fail_index){++injected;return nullptr;}
    void* p=std::malloc(n);if(p)raw_blocks.emplace(reinterpret_cast<uintptr_t>(p),n);return p;
}
void* probe_realloc(void* p,size_t n) {
    if(++allocation_index==fail_index){++injected;return nullptr;}
    const auto key=reinterpret_cast<uintptr_t>(p);
    if(p && raw_blocks.find(key)==raw_blocks.end()){++raw_errors;return nullptr;}
    void* result=std::realloc(p,n);
    if(result){raw_blocks.erase(key);raw_blocks.emplace(reinterpret_cast<uintptr_t>(result),n);}
    return result;
}
void probe_free(void* p) {
    if(!p)return;
    if(raw_blocks.erase(reinterpret_cast<uintptr_t>(p))!=1){++raw_errors;return;}
    std::free(p);
}
#define malloc probe_malloc
#define realloc probe_realloc
#define free probe_free
#include "hd_visible_png.cpp"
#undef malloc
#undef realloc
#undef free
size_t checks=0,failures=0;
void check(bool value,const char* message) {++checks;if(!value){++failures;std::printf("FAIL %s\n",message);}}
bool empty() {return !decoder::live_bytes&&!decoder::live_blocks&&!decoder::allocations&&!decoder::transaction_active&&raw_blocks.empty()&&!raw_errors;}
void reset(size_t index) {allocation_index=0;fail_index=index;injected=0;}
bool invoke(unsigned mode,const std::vector<unsigned char>& png,const std::vector<unsigned char>& z,size_t expected) {
    if(!mode)return hdv_validate_master_png(png.data(),png.size());
    try {
        decoder::AllocationTransaction transaction;
        if(mode==1)return strict_stream(z.data(),z.size(),expected);
        unsigned char* pixels=nullptr;unsigned w=0,h=0;
        unsigned error=decoder::lodepng_decode24(&pixels,&w,&h,png.data(),png.size());
        decoder::lodepng_free(pixels);
        return !error&&w==800&&h==1160&&!decoder::live_bytes;
    } catch(const std::bad_alloc&) {return false;}
}
void exercise(const char* name,const std::vector<unsigned char>& png,size_t expected) {
    std::vector<unsigned char> z;
    for(size_t at=8;at+12<=png.size();) {
        size_t n=read32(png.data()+at);
        if(!std::memcmp(png.data()+at+4,"IDAT",4))z.insert(z.end(),png.data()+at+8,png.data()+at+8+n);
        at+=n+12;
    }
    for(unsigned mode=0;mode<3;++mode) {
        reset(0);check(invoke(mode,png,z,expected),"baseline valid decode");
        const size_t count=allocation_index;check(count>0,"baseline performed allocations");check(empty(),"baseline exact cleanup");
        for(size_t i=1;i<=count;++i) {
            reset(i);check(!invoke(mode,png,z,expected),"every allocation failure rejects");
            check(injected==1&&allocation_index==i,"exact indexed failure reached once");
            check(empty(),"failure reclaims every raw block and TLS state");
            reset(0);check(invoke(mode,png,z,expected),"same-thread recovery after failure");
            check(allocation_index==count&&empty(),"recovery deterministic allocation count and cleanup");
        }
        reset(count+1);check(invoke(mode,png,z,expected)&&!injected&&empty(),"one-past failure index succeeds");
        std::printf("%s mode %u: %zu allocation indices checked\n",name,mode,count);
    }
    reset(0);
    void* prior=decoder::lodepng_malloc(128);std::memset(prior,0x65,128);
    const size_t before=allocation_index;
    check(!hdv_validate_master_png(png.data(),png.size()),"busy prior TLS rejected");
    check(allocation_index==before&&decoder::live_bytes==128&&decoder::live_blocks==1&&raw_blocks.size()==1,"prior allocation/accounting preserved");
    check(static_cast<unsigned char*>(prior)[0]==0x65&&static_cast<unsigned char*>(prior)[127]==0x65,"prior contents preserved");
    decoder::lodepng_free(prior);check(empty(),"prior free exact cleanup");
    decoder::transaction_active=true;
    check(!hdv_validate_master_png(png.data(),png.size())&&decoder::transaction_active,"nested transaction rejected and active state preserved");
    decoder::transaction_active=false;check(empty(),"nested rejection restores caller state");
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=2)return 2;
    const std::wstring dir=argv[1];std::vector<unsigned char> good;
    for(const char* name:{"fixed","dynamic","stored","adam7"}) {
        std::ifstream f(dir+L"\\"+std::wstring(name,name+std::strlen(name))+L".png",std::ios::binary);
        std::vector<unsigned char> png{std::istreambuf_iterator<char>(f),{}};
        if(png.empty())return 3;
        size_t expected=size_t(2401)*1160;
        if(png[28]){unsigned w[7]{},h[7]{};size_t filters[8]{},padded[8]{},passes[8]{};decoder::Adam7_getpassvalues(w,h,filters,padded,passes,800,1160,24);expected=filters[7];}
        exercise(name,png,expected);if(std::strcmp(name,"fixed")==0)good=png;
    }
    reset(0);
    void* a=decoder::lodepng_malloc(20u*1024*1024);void* b=decoder::lodepng_malloc(11u*1024*1024);
    check(decoder::live_bytes==31u*1024*1024&&decoder::live_blocks==2,"combined live allocation accounting");
    bool threw=false;try{decoder::lodepng_malloc(2u*1024*1024);}catch(const std::bad_alloc&){threw=true;}
    check(threw&&decoder::live_bytes==31u*1024*1024&&decoder::live_blocks==2,"budget failure throws and preserves blocks");
    std::memset(a,0x7b,128);const size_t old_attempts=allocation_index;fail_index=old_attempts+1;threw=false;
    try{decoder::lodepng_realloc(a,21u*1024*1024);}catch(const std::bad_alloc&){threw=true;}
    check(threw&&injected==1&&decoder::live_bytes==31u*1024*1024&&decoder::live_blocks==2&&raw_blocks.size()==2,"underlying realloc failure preserves pointer/accounting");
    check(static_cast<unsigned char*>(a)[0]==0x7b&&static_cast<unsigned char*>(a)[127]==0x7b,"failed realloc preserves bytes");
    reset(0);threw=false;try{decoder::lodepng_realloc(a,33u*1024*1024);}catch(const std::bad_alloc&){threw=true;}
    check(threw&&allocation_index==0&&decoder::live_bytes==31u*1024*1024,"budget realloc refused before raw allocator");
    decoder::lodepng_free(b);a=decoder::lodepng_realloc(a,decoder::AllocationBudget);
    check(a&&decoder::live_bytes==decoder::AllocationBudget&&decoder::live_blocks==1&&raw_blocks.size()==1,"exact budget realloc updates links");
    threw=false;try{decoder::lodepng_malloc(1);}catch(const std::bad_alloc&){threw=true;}
    check(threw&&decoder::live_bytes==decoder::AllocationBudget,"next byte over budget rejected");
    decoder::lodepng_free(a);check(empty(),"budget recovery free exact cleanup");
    check(hdv_validate_master_png(good.data(),good.size())&&empty(),"same-thread decode after budget exhaustion");
    {decoder::AllocationTransaction transaction;decoder::lodepng_malloc(97);decoder::lodepng_malloc(103);}
    check(empty(),"transaction destructor reclaims unreturned blocks");
    std::atomic<size_t> parallel_failures{0};std::vector<std::thread> threads;
    for(unsigned t=0;t<4;++t)threads.emplace_back([&]{for(size_t i=1;i<=32;++i){reset(i);if(hdv_validate_master_png(good.data(),good.size())||!empty())++parallel_failures;reset(0);if(!hdv_validate_master_png(good.data(),good.size())||!empty())++parallel_failures;}});
    for(auto& thread:threads)thread.join();check(!parallel_failures,"concurrent failure and recovery uses independent TLS");
    check(empty(),"main TLS remains clean after concurrent workers");
    std::printf("%zu allocator checks, %zu failures\n",checks,failures);return failures?1:0;
}
