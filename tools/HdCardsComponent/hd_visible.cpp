#include "hd_visible.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <cwchar>
#include <new>
#include <memory>
#include <string>
#include <vector>

namespace {
constexpr uint32_t Width = 800, Height = 600, CardWidth = 200, CardHeight = 290;
constexpr size_t ReferenceBytes = size_t(CardWidth) * CardHeight * 2;
constexpr size_t MaximumEntries = 1024;
struct Region { uint32_t game, x, y; };
/* Bounded, explicit layouts. Additional positions require a complete observed
   original-raster match; no sliding search or inferred cross-game positions. */
constexpr Region Regions[] = {
    {HDV_YUGI, 5, 72},
    {HDV_YUGI, 11, 88},
    {HDV_YUGI, 7, 89},
    {HDV_KAIBA, 5, 72},
    {HDV_KAIBA, 16, 86},
    {HDV_KAIBA, 7, 89},
    {HDV_JOEY, 2, 74},
    {HDV_JOEY, 6, 87},
    {HDV_JOEY, 7, 89}
};
constexpr bool valid_regions() {
    for (size_t i = 0; i < sizeof(Regions) / sizeof(Regions[0]); ++i) {
        if (Regions[i].game < HDV_YUGI || Regions[i].game > HDV_JOEY ||
            Regions[i].x > Width - CardWidth || Regions[i].y > Height - CardHeight)
            return false;
        for (size_t j = 0; j < i; ++j)
            if (Regions[i].game == Regions[j].game && Regions[i].x == Regions[j].x &&
                Regions[i].y == Regions[j].y) return false;
    }
    return true;
}
static_assert(valid_regions(), "Preview regions must be distinct and inside the logical frame");
uint64_t hash_bytes(const unsigned char* bytes, size_t count) {
    uint64_t value = UINT64_C(14695981039346656037);
    for (size_t i = 0; i < count; ++i) {
        value ^= bytes[i]; value *= UINT64_C(1099511628211);
    }
    return value;
}
bool absolute_path(const wchar_t* path) {
    if (!path || !path[0] || !path[1] || !path[2]) return false;
    const size_t length = std::wcslen(path);
    return length < 32000 && ((path[1] == L':' && (path[2] == L'\\' || path[2] == L'/')) ||
        (path[0] == L'\\' && path[1] == L'\\'));
}
struct Cursor {
    uint32_t state=0, ax=0, ay=0;
    std::vector<unsigned char> bgra;
    std::array<uint16_t,4096> color{};
    std::array<unsigned char,4096> alpha{};
    std::array<uint16_t,6> anchor{};
};
struct CursorPlacement { const Cursor* sprite; uint32_t x,y; };
uint16_t composite(uint16_t source,uint16_t destination,unsigned q) {
    const unsigned r=((source>>11)*q/15)+((destination>>11)*(15-q)/15);
    const unsigned g=(((source>>5)&63)*q/15)+(((destination>>5)&63)*(15-q)/15);
    const unsigned b=((source&31)*q/15)+((destination&31)*(15-q)/15);
    return static_cast<uint16_t>((r<<11)|(g<<5)|b);
}
struct Reference {
    uint32_t key;
    uint64_t hash;
    std::vector<unsigned char> bytes;
    std::wstring master;
};
}
struct HdvEngine {
    uint32_t game = 0, format = 0, x = 0, y = 0;
    uint64_t generation = 0;
    bool sealed = false, valid = false, failed = false;
    std::vector<Reference> references;
    std::vector<unsigned char> snapshot;
    std::vector<unsigned char> region;
    const Reference* match = nullptr;
    std::vector<Cursor> cursors;
    const Cursor* foreground=nullptr;
    uint32_t cursor_x=0,cursor_y=0;
};
extern "C" HdvEngine* hdv_create(uint32_t game, uint32_t format) {
    if (game < HDV_YUGI || game > HDV_JOEY || format < HDV_RGB565 || format > HDV_RGB555) return nullptr;
    try {
        auto engine = std::make_unique<HdvEngine>();
        engine->game = game; engine->format = format;
        engine->snapshot.resize(size_t(Width) * Height * 2);
        engine->region.resize(ReferenceBytes);
        return engine.release();
    } catch (...) { return nullptr; }
}
extern "C" void hdv_destroy(HdvEngine* engine) { delete engine; }
extern "C" int hdv_add(HdvEngine* engine, uint32_t key, const void* bytes,
    size_t count, const wchar_t* master) {
    if (!engine || engine->sealed || engine->failed) return 0;
    if (!key || !bytes || count != ReferenceBytes || !absolute_path(master) ||
        engine->references.size() >= MaximumEntries ||
        std::any_of(engine->references.begin(), engine->references.end(),
            [key](const Reference& entry) { return entry.key == key; })) {
        engine->failed = true; return 0;
    }
    try {
        const auto* first = static_cast<const unsigned char*>(bytes);
        Reference entry{key, hash_bytes(first, count), {first, first + count}, master};
        engine->references.push_back(std::move(entry)); return 1;
    } catch (...) { engine->failed = true; return 0; }
}
extern "C" int hdv_add_cursor(HdvEngine* engine,uint32_t state,const void* bytes,size_t count) {
    if(!engine || engine->sealed || engine->failed) return 0;
    if(!bytes || count!=16384 || state<1 || state>2 || engine->format!=HDV_RGB565 ||
        std::any_of(engine->cursors.begin(),engine->cursors.end(),[state](const Cursor& c){return c.state==state;})) {
        engine->failed=true;return 0;
    }
    try {
        Cursor c;c.state=state;const auto* p=static_cast<const unsigned char*>(bytes);
        c.bgra.assign(p,p+count);
        for(size_t i=0;i<4096;++i) {
            c.color[i]=static_cast<uint16_t>(((p[i*4+2]>>3)<<11)|((p[i*4+1]>>2)<<5)|(p[i*4]>>3));
            c.alpha[i]=static_cast<unsigned char>(p[i*4+3]>>4);
        }
        bool found=false;
        for(uint32_t y=0;y<64&&!found;++y)for(uint32_t x=0;x<=58&&!found;++x) {
            bool opaque=true,different=false;
            for(size_t i=0;i<6;++i){opaque=opaque&&c.alpha[y*64+x+i]==15;different=different||c.color[y*64+x+i]!=c.color[y*64+x];}
            if(opaque&&different){c.ax=x;c.ay=y;for(size_t i=0;i<6;++i)c.anchor[i]=c.color[y*64+x+i];found=true;}
        }
        if(!found){engine->failed=true;return 0;}
        engine->cursors.push_back(std::move(c));return 1;
    }catch(...){engine->failed=true;return 0;}
}
extern "C" int hdv_seal(HdvEngine* engine) {
    if (!engine || engine->failed || engine->references.empty() || (!engine->cursors.empty() && engine->cursors.size()!=2)) return 0;
    engine->sealed = true; return 1;
}
extern "C" void hdv_invalidate(HdvEngine* engine) {
    if (engine) { engine->valid = false; engine->match = nullptr; engine->foreground=nullptr; ++engine->generation; }
}
extern "C" int hdv_capture(HdvEngine* engine, const void* pixels, uint32_t width,
    uint32_t height, uint32_t pitch, uint32_t format) {
    hdv_invalidate(engine);
    if (!engine || !engine->sealed || engine->failed || !pixels ||
        width != Width || height != Height || pitch < Width * 2 || pitch > 65536 ||
        format != engine->format) return 0;
    const auto* input = static_cast<const unsigned char*>(pixels);
    for (uint32_t y = 0; y < Height; ++y)
        std::memcpy(engine->snapshot.data() + size_t(y) * Width * 2,
            input + size_t(y) * pitch, Width * 2);
    std::array<CursorPlacement,16> placements{};size_t placement_count=0;
    /* One finite scan per owned sprite. A colorful opaque anchor only locates
       candidates; every opaque pixel and then every ROI byte still verifies. */
    for(const auto& c:engine->cursors)for(uint32_t y=0;y<Height;++y)for(uint32_t x=0;x+6<=Width;++x) {
        if(x<c.ax || y<c.ay || x-c.ax>Width-64 || y-c.ay>Height-64)continue;
        const auto* at=engine->snapshot.data()+(size_t(y)*Width+x)*2;
        if(std::memcmp(at,c.anchor.data(),12)!=0)continue;
        const uint32_t ox=x-c.ax,oy=y-c.ay;bool exact=true;
        for(uint32_t sy=0;sy<64&&exact;++sy)for(uint32_t sx=0;sx<64;++sx)if(c.alpha[sy*64+sx]==15) {
            uint16_t value=0;std::memcpy(&value,engine->snapshot.data()+(size_t(oy+sy)*Width+ox+sx)*2,2);
            if(value!=c.color[sy*64+sx]){exact=false;break;}
        }
        if(exact){if(placement_count==placements.size()){engine->valid=true;return 1;}placements[placement_count++]={&c,ox,oy};}
    }
    size_t matches = 0;
    for (const auto& region : Regions) {
        if (region.game != engine->game) continue;
        for (uint32_t y = 0; y < CardHeight; ++y)
            std::memcpy(engine->region.data() + size_t(y) * CardWidth * 2,
                engine->snapshot.data() + (size_t(y + region.y) * Width + region.x) * 2,
                CardWidth * 2);
        const uint64_t hash = hash_bytes(engine->region.data(), ReferenceBytes);
        bool intersects=false;
        for(size_t i=0;i<placement_count;++i) {
            const auto& p=placements[i];
            for(uint32_t sy=0;sy<64&&!intersects;++sy)for(uint32_t sx=0;sx<64;++sx)
                if(p.sprite->alpha[sy*64+sx] && p.x+sx>=region.x && p.x+sx<region.x+CardWidth &&
                    p.y+sy>=region.y && p.y+sy<region.y+CardHeight){intersects=true;break;}
        }
        for (const auto& entry : engine->references) {
            if(!intersects && entry.hash==hash && std::memcmp(entry.bytes.data(),engine->region.data(),ReferenceBytes)==0) {
                engine->match=&entry;engine->foreground=nullptr;engine->x=region.x;engine->y=region.y;++matches;
            }
            for(size_t i=0;i<placement_count;++i) {
                const auto& p=placements[i];bool touched=false,exact=true;
                /* Reject complete unoccluded row spans with bounded library
                   comparisons before per-pixel composition. They cannot be
                   changed by this candidate; surviving cases still compare
                   the entire explained ROI below, with no acceptance mask. */
                for(uint32_t ry=0;ry<CardHeight&&exact;++ry) {
                    const uint32_t ny=region.y+ry;const size_t row=size_t(ry)*CardWidth*2;
                    if(ny<p.y||ny>=p.y+64||p.x>=region.x+CardWidth||p.x+64<=region.x) {
                        exact=std::memcmp(entry.bytes.data()+row,engine->region.data()+row,CardWidth*2)==0;
                    } else {
                        const uint32_t left=p.x>region.x?p.x-region.x:0;
                        const uint32_t right=std::min(CardWidth,p.x+64-region.x);
                        exact=std::memcmp(entry.bytes.data()+row,engine->region.data()+row,left*2)==0 &&
                            std::memcmp(entry.bytes.data()+row+right*2,engine->region.data()+row+right*2,(CardWidth-right)*2)==0;
                    }
                }
                if(!exact)continue;
                for(uint32_t ry=0;ry<CardHeight&&exact;++ry)for(uint32_t rx=0;rx<CardWidth;++rx) {
                    const size_t offset=(size_t(ry)*CardWidth+rx)*2;uint16_t expected=0,actual=0;
                    std::memcpy(&expected,entry.bytes.data()+offset,2);std::memcpy(&actual,engine->region.data()+offset,2);
                    const uint32_t nx=region.x+rx,ny=region.y+ry;
                    if(nx>=p.x&&nx<p.x+64&&ny>=p.y&&ny<p.y+64) {
                        const size_t pos=size_t(ny-p.y)*64+nx-p.x;const unsigned q=p.sprite->alpha[pos];
                        touched=touched||q!=0;expected=composite(p.sprite->color[pos],expected,q);
                    }
                    if(expected!=actual){exact=false;break;}
                }
                if(exact&&touched){engine->match=&entry;engine->foreground=p.sprite;engine->cursor_x=p.x;engine->cursor_y=p.y;engine->x=region.x;engine->y=region.y;++matches;}
            }
        }
    }
    if (matches != 1) {engine->match = nullptr;engine->foreground=nullptr;}
    engine->valid = true; return 1;
}
extern "C" int hdv_snapshot(const HdvEngine* engine, HdvFrame* frame) {
    if (!engine || !frame || !engine->valid) return 0;
    *frame = {engine->snapshot.data(), Width, Height, Width * 2, engine->format, engine->generation}; return 1;
}
extern "C" int hdv_match(const HdvEngine* engine, HdvMatch* match) {
    if (!engine || !match || !engine->valid || !engine->match) return 0;
    *match = {engine->match->key, engine->x, engine->y, CardWidth, CardHeight,
        engine->generation, engine->match->master.c_str()}; return 1;
}

extern "C" int hdv_cursor(const HdvEngine* engine,HdvCursor* cursor) {
    if(!engine||!cursor||!engine->valid||!engine->match||!engine->foreground)return 0;
    *cursor={engine->foreground->bgra.data(),static_cast<int32_t>(engine->cursor_x),static_cast<int32_t>(engine->cursor_y),engine->foreground->state,engine->generation};return 1;
}
