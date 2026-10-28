#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <fstream>
#include <string>
#include <thread>
#include <vector>
#include <atomic>
#include "hd_visible_png.cpp"
bool refused_malloc(size_t n){try{decoder::lodepng_malloc(n);}catch(const std::bad_alloc&){return true;}return false;}
bool refused_realloc(void* p,size_t n){try{decoder::lodepng_realloc(p,n);}catch(const std::bad_alloc&){return true;}return false;}
int checks=0,failures=0;
void check(bool v,const char* name){++checks;if(!v){++failures;std::printf("FAIL %s\n",name);}}
int wmain(int argc,wchar_t** argv){
 if(argc!=2)return 2;
 std::ifstream manifest(std::wstring(argv[1])+L"\\manifest.tsv");
 std::string name;int expected=0;std::vector<unsigned char> good;
 while(manifest>>name>>expected){
  std::ifstream f(std::wstring(argv[1])+L"\\"+std::wstring(name.begin(),name.end()),std::ios::binary);
  std::vector<unsigned char> b{std::istreambuf_iterator<char>(f),{}};
  const size_t page=4096,readable=(b.size()+page-1)/page*page;
  auto* address=static_cast<unsigned char*>(VirtualAlloc(nullptr,readable+page,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
  if(!address)return 3;
  DWORD previous=0;VirtualProtect(address+readable,page,PAGE_NOACCESS,&previous);
  auto* input=address+readable-b.size();std::memcpy(input,b.data(),b.size());
  const bool result=hdv_validate_master_png(input,b.size());
  check(result==(expected!=0),name.c_str());
  check(decoder::live_bytes==0&&decoder::live_blocks==0&&!decoder::allocations&&!decoder::transaction_active,"decoder allocations recovered");
  VirtualFree(address,0,MEM_RELEASE);
  if(name=="filter0.png")good=b;
 }
 check(!good.empty(),"valid fixture loaded");
 check(!hdv_validate_master_png(nullptr,45),"null input rejected");
 check(!hdv_validate_master_png(good.data(),16u*1024*1024+1),"overlong input rejected before access");
 void* a=decoder::lodepng_malloc(20u*1024*1024);
 check(a!=nullptr&&decoder::live_bytes==20u*1024*1024,"account allocated bytes");
 check(refused_malloc(13u*1024*1024),"combined allocation exceeds budget");
 check(refused_realloc(a,33u*1024*1024),"realloc exceeds budget");
 check(decoder::live_bytes==20u*1024*1024,"failed realloc preserves accounting");
 a=decoder::lodepng_realloc(a,decoder::AllocationBudget);
 check(a!=nullptr&&decoder::live_bytes==decoder::AllocationBudget,"realloc exact budget");
 check(refused_malloc(1),"exact budget refuses next byte");
 decoder::lodepng_free(a);
 check(decoder::live_bytes==0,"free restores allocation budget");
 check(hdv_validate_master_png(good.data(),good.size()),"recovered after budget exhaustion");
 std::atomic<int> thread_failures{0};std::vector<std::thread> threads;
 for(int i=0;i<4;++i)threads.emplace_back([&]{for(int j=0;j<8;++j)if(!hdv_validate_master_png(good.data(),good.size())||decoder::live_bytes)++thread_failures;});
 for(auto& t:threads)t.join();check(thread_failures==0,"parallel independent TLS decoder allocators");
 std::printf("%d producer PNG regression checks, %d failures\n",checks,failures);
 return failures?1:0;
}
