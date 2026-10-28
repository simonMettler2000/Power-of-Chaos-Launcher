/* The decoder implementation below is an external, pinned upstream input.
   This file contains only project-authored admission/extent/allocation glue. */
#include "hd_visible_png.h"
#include <stdlib.h>
#include <stdio.h>
#include <limits.h>
#include <string.h>
#include <memory>
#include <cstdint>
#include <new>
#define LODEPNG_NO_COMPILE_CPP
#define LODEPNG_NO_COMPILE_ENCODER
#define LODEPNG_NO_COMPILE_DISK
#define LODEPNG_NO_COMPILE_ANCILLARY_CHUNKS
#define LODEPNG_NO_COMPILE_ERROR_TEXT
#define LODEPNG_NO_COMPILE_ALLOCATORS
namespace {
namespace decoder {
/* No upstream source is copied into this authored file. Build must supply the
   immutable N/src and N/inc include roots and verify their exact hashes. */
#include <lodepng.c>
/* Upstream is compiled as C++ in this private namespace. Its fixed-tree
   builder discards allocation return codes, so a null-return allocator is
   unsafe. Every allocation failure instead throws ordinary std::bad_alloc
   before upstream can use null. The public C++ boundary catches it after this
   transaction reclaims outstanding blocks; no exception crosses that boundary.
   No upstream function, source text, return-code check or parser is replaced. */
struct Allocation { Allocation* previous; Allocation* next; size_t bytes; };
thread_local Allocation* allocations=nullptr;
thread_local size_t live_bytes=0;
thread_local size_t live_blocks=0;
thread_local bool transaction_active=false;
constexpr size_t AllocationBudget=32u*1024*1024;
void* lodepng_malloc(size_t bytes) {
    if(bytes>AllocationBudget-live_bytes) throw std::bad_alloc();
    auto* value=static_cast<Allocation*>(::malloc(sizeof(Allocation)+bytes));
    if(!value) throw std::bad_alloc();
    value->bytes=bytes;value->previous=nullptr;value->next=allocations;
    if(allocations)allocations->previous=value;
    allocations=value;live_bytes+=bytes;++live_blocks;return value+1;
}
void lodepng_free(void* pointer) {
    if(!pointer)return;
    auto* value=static_cast<Allocation*>(pointer)-1;
    if(value->previous)value->previous->next=value->next;
    else allocations=value->next;
    if(value->next)value->next->previous=value->previous;
    live_bytes-=value->bytes;--live_blocks;::free(value);
}
void* lodepng_realloc(void* pointer,size_t bytes) {
    if(!pointer)return lodepng_malloc(bytes);
    auto* old=static_cast<Allocation*>(pointer)-1;const size_t previous=old->bytes;
    if(bytes>AllocationBudget-(live_bytes-previous))throw std::bad_alloc();
    Allocation* before=old->previous;Allocation* after=old->next;
    auto* value=static_cast<Allocation*>(::realloc(old,sizeof(Allocation)+bytes));
    if(!value)throw std::bad_alloc();
    if(before)before->next=value;else allocations=value;
    if(after)after->previous=value;
    value->previous=before;value->next=after;value->bytes=bytes;
    live_bytes=live_bytes-previous+bytes;return value+1;
}
class AllocationTransaction {
    bool previous_active;
public:
    AllocationTransaction():previous_active(transaction_active) { transaction_active=true; }
    ~AllocationTransaction() {
        while(allocations)lodepng_free(allocations+1);
        transaction_active=previous_active;
    }
    AllocationTransaction(const AllocationTransaction&)=delete;
    AllocationTransaction& operator=(const AllocationTransaction&)=delete;
};
}
uint32_t read32(const unsigned char* p) {
    return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3];
}
bool valid_png(const unsigned char* p,size_t count,size_t& compressed_size) {
    compressed_size=0;
    const unsigned char signature[] = {137,80,78,71,13,10,26,10};
    if (count<45 || ::memcmp(p,signature,8)) return false;
    size_t at=8; bool header=false, data=false;
    while (at+12<=count) {
        const uint32_t n=read32(p+at);
        if (n>count-at-12) return false;
        const auto* type=p+at+4; const auto* bytes=p+at+8;
        for(unsigned k=0;k<4;++k)
            if(!((type[k]>='A'&&type[k]<='Z')||(type[k]>='a'&&type[k]<='z')))return false;
        if(type[2]<'A'||type[2]>'Z')return false;
        uint32_t crc=UINT32_MAX;
        for (size_t k=0;k<size_t(n)+4;++k) {
            crc^=type[k];
            for (unsigned b=0;b<8;++b) crc=(crc>>1)^((crc&1)?0xedb88320u:0u);
        }
        if ((crc^UINT32_MAX)!=read32(bytes+n)) return false;
        if (!::memcmp(type,"IHDR",4)) {
            if (header || at!=8 || n!=13 || read32(bytes)!=800 || read32(bytes+4)!=1160 ||
                bytes[8]!=8 || bytes[9]!=2 || bytes[10]!=0 || bytes[11]!=0 || bytes[12]>1) return false;
            header=true;
        } else if (!::memcmp(type,"IDAT",4)) {
            if (!header || !n) return false;
            data=true;compressed_size+=n;
        } else if (!::memcmp(type,"IEND",4)) return header && data && n==0 && at+12==count;
        else return false; /* Supported masters are deliberately metadata-free:
                              IHDR, consecutive nonempty IDAT, terminal IEND. */
        at+=size_t(n)+12;
    }
    return false;
}

/* Reuse the pinned decoder's real Huffman/stored-block parsers. Their bit
   cursor lets this wrapper reject all complete bytes after final DEFLATE end,
   which the old upstream zlib public API does not itself report. */
bool strict_stream(const unsigned char* z,size_t size,size_t expected) {
    if(size<7 || (unsigned(z[0])*256+z[1])%31 || (z[0]&15)!=8 ||
        (z[0]>>4)>7 || (z[1]&32))return false;
    const auto* compressed=z+2;const size_t compressed_bytes=size-6;
    decoder::ucvector output;decoder::ucvector_init(&output);
    size_t bits=0,position=0;unsigned last=0,error=0;
    while(!last && !error) {
        if(bits+3>compressed_bytes*8){error=1;break;}
        last=decoder::readBitFromStream(&bits,compressed);
        const unsigned kind=decoder::readBitsFromStream(&bits,compressed,2);
        /* Include the already-owned checksum bytes in parser bounds to avoid
           the pinned stored-empty-block lookahead quirk. The final strict
           cursor check below forbids consuming any checksum bits. */
        if(kind==0)error=decoder::inflateNoCompression(&output,compressed,&bits,&position,size-2);
        else if(kind==1 || kind==2)error=decoder::inflateHuffmanBlock(&output,compressed,&bits,&position,size-2,kind);
        else error=1;
        if(bits>compressed_bytes*8 || output.size>expected)error=1;
    }
    const bool valid=!error && last && (bits+7)/8==compressed_bytes && output.size==expected &&
        decoder::adler32(output.data,static_cast<unsigned>(output.size))==read32(z+size-4);
    decoder::ucvector_cleanup(&output);return valid;
}
}
namespace {
bool decode_master(const unsigned char* bytes,size_t size,unsigned char* bgra) {
    if(!bytes || size<45 || size>16u*1024*1024 || decoder::live_bytes ||
       decoder::live_blocks || decoder::allocations || decoder::transaction_active)return false;
    try {
    decoder::AllocationTransaction transaction;
    size_t compressed_size=0;
    if(!valid_png(bytes,size,compressed_size))return false;
    /* One allocation request of exactly the validated logical IDAT size avoids
       vector growth/spare capacity. Both file and this buffer are <=16MiB. */
    std::unique_ptr<unsigned char[]> compressed(new unsigned char[compressed_size]);
    size_t position=0;
    for(size_t at=8;at+12<=size;) {
        const size_t n=read32(bytes+at);if(n>size-at-12)return false;
        if(!::memcmp(bytes+at+4,"IDAT",4)) {
            if(n>compressed_size-position)return false;
            ::memcpy(compressed.get()+position,bytes+at+8,n);position+=n;
        }
        at+=n+12;
    }
    if(position!=compressed_size)return false;
    unsigned widths[7]{},heights[7]{};size_t filters[8]{},padded[8]{},passes[8]{};
    size_t expected=(size_t(800)*3+1)*1160;
    if(bytes[28]==1) {
        decoder::Adam7_getpassvalues(widths,heights,filters,padded,passes,800,1160,24);
        expected=filters[7];
    }
    if(!strict_stream(compressed.get(),compressed_size,expected))return false;
    unsigned char* decoded=nullptr;unsigned width=0,height=0;
    const unsigned error=decoder::lodepng_decode24(&decoded,&width,&height,bytes,size);
    bool valid=!error && width==800 && height==1160;
    if(bgra) {
        /* Publish only after successful full decode and proof that the output
           is the sole remaining decoder allocation. No allocation can fail
           during the exact RGB-to-opaque-BGRA conversion below. */
        valid=valid && decoded && decoder::live_blocks==1 && decoder::allocations &&
            decoder::allocations+1==reinterpret_cast<decoder::Allocation*>(decoded) &&
            !decoder::allocations->previous && !decoder::allocations->next &&
            decoder::allocations->bytes>=size_t(800)*1160*3 &&
            decoder::live_bytes==decoder::allocations->bytes;
        if(valid) for(size_t pixel=0;pixel<size_t(800)*1160;++pixel) {
            bgra[pixel*4]=decoded[pixel*3+2];bgra[pixel*4+1]=decoded[pixel*3+1];
            bgra[pixel*4+2]=decoded[pixel*3];bgra[pixel*4+3]=255;
        }
    }
    decoder::lodepng_free(decoded);
    return valid && decoder::live_bytes==0 && decoder::live_blocks==0;
    } catch(const std::bad_alloc&) {
        return false;
    }
}

}
bool hdv_validate_master_png(const unsigned char* bytes,size_t size) {
    return decode_master(bytes,size,nullptr);
}
bool hdv_decode_master_bgra(const unsigned char* bytes,size_t size,unsigned char* output,size_t output_size) {
    constexpr size_t OutputBytes=size_t(800)*1160*4;
    if(!bytes || !output || output_size!=OutputBytes || size>16u*1024*1024)return false;
    const uintptr_t input_at=reinterpret_cast<uintptr_t>(bytes),output_at=reinterpret_cast<uintptr_t>(output);
    if(input_at>UINTPTR_MAX-size || output_at>UINTPTR_MAX-output_size ||
       (input_at<output_at+output_size && output_at<input_at+size))return false;
    return decode_master(bytes,size,output);
}
