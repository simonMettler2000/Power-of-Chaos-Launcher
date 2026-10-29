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
struct FlipBackground { std::vector<unsigned char> bytes; };
struct ProbePosting { uint16_t word, reference; };
struct FlipProbe { uint32_t offset; std::vector<ProbePosting> postings; };
struct FlipProfile {
    size_t background;
    HdvFlipPose pose;
    std::array<uint32_t,200> samples{};
    std::array<FlipProbe,32> probes;
};
constexpr HdvFlipPose FlipPoses[] = {
    {196,9,15},{184,15,14},{166,24,13},{141,37,12},
    {111,52,10},{76,69,7},{39,88,5}
};
uint16_t read_word(const unsigned char* bytes) {
    uint16_t word=0; std::memcpy(&word,bytes,2); return word;
}
uint16_t flip_blend(uint16_t source,uint16_t background,unsigned q) {
    return source==background?source:composite(source,background,q);
}
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
    bool flip_enabled=false;
    std::vector<FlipBackground> backgrounds;
    std::vector<FlipProfile> profiles;
    const Reference* flip_reference=nullptr;
    const FlipProfile* flip_profile=nullptr;
};
namespace {
bool touched(const CursorPlacement& cursor) {
    for(uint32_t y=0;y<64;++y)for(uint32_t x=0;x<64;++x)
        if(cursor.sprite->alpha[y*64+x] && cursor.x+x>=7 && cursor.x+x<207 &&
            cursor.y+y>=89 && cursor.y+y<379)return true;
    return false;
}
unsigned cursor_alpha(const CursorPlacement* cursor,uint32_t x,uint32_t y) {
    if(!cursor || x<cursor->x || y<cursor->y || x>=cursor->x+64 || y>=cursor->y+64)return 0;
    return cursor->sprite->alpha[(y-cursor->y)*64+x-cursor->x];
}
uint16_t flip_pixel(const HdvEngine& engine,const FlipProfile& profile,const Reference& reference,uint32_t x,uint32_t y) {
    const auto& bg=engine.backgrounds[profile.background].bytes;
    const uint16_t background=read_word(bg.data()+(size_t(y)*200+x)*2);
    const uint32_t left=profile.pose.x-7;
    if(x<left || x>=left+profile.pose.width)return background;
    const uint16_t source=read_word(reference.bytes.data()+(size_t(y)*200+profile.samples[x-left])*2);
    return flip_blend(source,background,profile.pose.q);
}
bool equivalent_background(const HdvEngine& engine,const FlipProfile& a,const FlipProfile& b) {
    const auto& first=engine.backgrounds[a.background].bytes;
    const auto& second=engine.backgrounds[b.background].bytes;
    if(first==second)return true;
    if(a.pose.q!=15)return false;
    const size_t left=(a.pose.x-7)*2,right=left+a.pose.width*2;
    for(size_t y=0;y<290;++y)
        if(std::memcmp(first.data()+y*400,second.data()+y*400,left)!=0 ||
            std::memcmp(first.data()+y*400+right,second.data()+y*400+right,400-right)!=0)return false;
    return true;
}
struct FlipCandidate { size_t profile,reference;const CursorPlacement* cursor; };
bool match_flips(HdvEngine& engine,const CursorPlacement* placements,size_t placement_count,size_t& matches,size_t& proof_candidates,size_t& work) {
    if(!engine.flip_enabled)return true;
    constexpr size_t MaximumWork=4*1024*1024,MaximumCandidates=8;
    size_t candidate_count=0;
    std::array<FlipCandidate,MaximumCandidates> candidates{};
    for(size_t pi=0;pi<engine.profiles.size();++pi) {
        const auto& profile=engine.profiles[pi];
        for(size_t ci=0;ci<=placement_count;++ci) {
            const CursorPlacement* cursor=ci?&placements[ci-1]:nullptr;
            if(cursor && !touched(*cursor))continue;
            std::array<uint64_t,16> surviving{};
            for(size_t ri=0;ri<engine.references.size();++ri)surviving[ri/64]|=UINT64_C(1)<<(ri%64);
            for(const auto& probe:profile.probes) {
                const uint32_t x=probe.offset%200,y=probe.offset/200;
                if(cursor_alpha(cursor,x+7,y+89))continue;
                if(++work>MaximumWork)return false;
                const auto word=read_word(engine.snapshot.data()+(size_t(y+89)*800+x+7)*2);
                auto first=std::lower_bound(probe.postings.begin(),probe.postings.end(),word,
                    [](const ProbePosting& a,uint16_t b){return a.word<b;});
                std::array<uint64_t,16> found{};
                for(auto it=first;it!=probe.postings.end()&&it->word==word;++it) {
                    if(++work>MaximumWork)return false;
                    found[it->reference/64]|=UINT64_C(1)<<(it->reference%64);
                }
                for(size_t i=0;i<16;++i)surviving[i]&=found[i];
            }
            for(size_t ri=0;ri<engine.references.size();++ri)if(surviving[ri/64]&(UINT64_C(1)<<(ri%64))) {
                if(proof_candidates==MaximumCandidates)return false;
                ++proof_candidates;
                candidates[candidate_count++]={pi,ri,cursor};
            }
        }
    }
    const Reference* accepted=nullptr;const FlipProfile* accepted_profile=nullptr;
    const CursorPlacement* accepted_cursor=nullptr;
    for(size_t i=0;i<candidate_count;++i) {
        if(200*290>MaximumWork-work)return false;
        work+=200*290;
        const auto& candidate=candidates[i];const auto& profile=engine.profiles[candidate.profile];
        const auto& reference=engine.references[candidate.reference];bool exact=true;
        for(uint32_t y=0;y<290&&exact;++y)for(uint32_t x=0;x<200;++x) {
            uint16_t expected=flip_pixel(engine,profile,reference,x,y);
            const auto* cursor=candidate.cursor;
            if(cursor && x+7>=cursor->x && y+89>=cursor->y && x+7<cursor->x+64 && y+89<cursor->y+64) {
                const size_t at=size_t(y+89-cursor->y)*64+x+7-cursor->x;
                expected=composite(cursor->sprite->color[at],expected,cursor->sprite->alpha[at]);
            }
            if(expected!=read_word(engine.snapshot.data()+(size_t(y+89)*800+x+7)*2)){exact=false;break;}
        }
        if(!exact)continue;
        const bool same_pose=accepted_profile && accepted_profile->pose.width==profile.pose.width &&
            accepted_profile->pose.x==profile.pose.x && accepted_profile->pose.q==profile.pose.q;
        const bool same_cursor=(!accepted_cursor&&!candidate.cursor) || (accepted_cursor&&candidate.cursor&&
            accepted_cursor->sprite==candidate.cursor->sprite&&accepted_cursor->x==candidate.cursor->x&&accepted_cursor->y==candidate.cursor->y);
        const bool alias=accepted && accepted->key==reference.key && same_pose&&same_cursor&&
            equivalent_background(engine,*accepted_profile,profile);
        if(!alias)++matches;
        accepted=&reference;accepted_profile=&profile;accepted_cursor=candidate.cursor;
    }
    if(accepted) {
        engine.flip_reference=accepted;engine.flip_profile=accepted_profile;
        engine.foreground=accepted_cursor?accepted_cursor->sprite:nullptr;
        if(accepted_cursor){engine.cursor_x=accepted_cursor->x;engine.cursor_y=accepted_cursor->y;}
    }
    return true;
}
}
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
/* A named helper also keeps the deterministic provider layout independent of
   MSVC's source-path-derived local lambda COMDAT names. */
static int fail_seal(HdvEngine* engine) {
    engine->failed=true;hdv_invalidate(engine);engine->profiles.clear();return 0;
}
extern "C" int hdv_seal(HdvEngine* engine) {
    if (!engine || engine->failed) return 0;
    /* A successful seal is immutable. In particular, never rebuild a V3 index
       after a capture has exposed pointers into its retained profiles. */
    if (engine->sealed) return 1;
    if(engine->references.empty() || (!engine->cursors.empty() && engine->cursors.size()!=2))return fail_seal(engine);
    try {
        if(engine->flip_enabled) {
            if(engine->backgrounds.empty())return fail_seal(engine);
            constexpr uint32_t rows[]={0,41,82,123,164,205,246,289};
            size_t bytes=0;
            for(size_t bi=0;bi<engine->backgrounds.size();++bi)for(const auto& pose:FlipPoses) {
                FlipProfile profile{};profile.background=bi;profile.pose=pose;
                const uint32_t quotient=200/pose.width,remainder=200%pose.width;
                int residual=-static_cast<int>(pose.width)-1;uint32_t source=0;
                for(uint32_t j=0;j<pose.width;++j) {
                    residual+=static_cast<int>(remainder);
                    if(residual>=0){++source;residual-=static_cast<int>(pose.width);}
                    if(source>=200)return fail_seal(engine);profile.samples[j]=source;source+=quotient;
                }
                size_t index=0;
                for(uint32_t y:rows)for(uint32_t k=0;k<4;++k) {
                    auto& probe=profile.probes[index++];const uint32_t x=pose.x-7+std::min(pose.width-1,pose.width*k/4);
                    probe.offset=y*200+x;
                    for(size_t ri=0;ri<engine->references.size();++ri)
                        probe.postings.push_back({flip_pixel(*engine,profile,engine->references[ri],x,y),static_cast<uint16_t>(ri)});
                    bytes+=probe.postings.size()*sizeof(ProbePosting);
                    if(bytes>16*1024*1024)return fail_seal(engine);
                    std::sort(probe.postings.begin(),probe.postings.end(),[](const ProbePosting& a,const ProbePosting& b){return a.word<b.word;});
                }
                engine->profiles.push_back(std::move(profile));
            }
        }
        engine->sealed = true; return 1;
    }catch(...){return fail_seal(engine);}
}
extern "C" int hdv_add_flip_background(HdvEngine* engine,const void* bytes,size_t count) {
    if(!engine||engine->sealed||engine->failed)return 0;
    if(!bytes||count!=ReferenceBytes||engine->format!=HDV_RGB565){engine->failed=true;return 0;}
    const auto* first=static_cast<const unsigned char*>(bytes);
    for(const auto& bg:engine->backgrounds)if(std::memcmp(bg.bytes.data(),first,count)==0)return 1;
    if(engine->backgrounds.size()==2){engine->failed=true;return 0;}
    try{engine->backgrounds.push_back({{first,first+count}});return 1;}catch(...){engine->failed=true;return 0;}
}
extern "C" int hdv_enable_flip(HdvEngine* engine,const HdvFlipPose* poses,size_t count) {
    if(!engine||engine->sealed||engine->failed)return 0;
    if(!poses||count!=7||engine->format!=HDV_RGB565){engine->failed=true;return 0;}
    for(size_t i=0;i<7;++i)if(poses[i].width!=FlipPoses[i].width||poses[i].x!=FlipPoses[i].x||poses[i].q!=FlipPoses[i].q){engine->failed=true;return 0;}
    engine->flip_enabled=true;return 1;
}
extern "C" void hdv_invalidate(HdvEngine* engine) {
    if (engine) { engine->valid = false; engine->match = nullptr; engine->foreground=nullptr; engine->flip_reference=nullptr;engine->flip_profile=nullptr;++engine->generation; }
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
    size_t proof_candidates=0,proof_work=0;
    const auto consume=[&](size_t amount) {
        if(!engine->flip_enabled)return true;
        if(amount>4*1024*1024-proof_work)return false;
        proof_work+=amount;return true;
    };
    const auto reserve=[&]() {
        if(!engine->flip_enabled)return true;
        if(proof_candidates==8)return false;
        ++proof_candidates;return true;
    };
    const auto refuse=[&]() {
        engine->match=nullptr;engine->foreground=nullptr;engine->flip_reference=nullptr;engine->flip_profile=nullptr;
        engine->valid=true;return 1;
    };
    std::array<CursorPlacement,16> placements{};size_t placement_count=0;
    /* One finite scan per owned sprite. A colorful opaque anchor only locates
       candidates; every opaque pixel and then every ROI byte still verifies. */
    for(const auto& c:engine->cursors)for(uint32_t y=0;y<Height;++y)for(uint32_t x=0;x+6<=Width;++x) {
        if(x<c.ax || y<c.ay || x-c.ax>Width-64 || y-c.ay>Height-64)continue;
        if(!consume(1))return refuse();
        const auto* at=engine->snapshot.data()+(size_t(y)*Width+x)*2;
        if(std::memcmp(at,c.anchor.data(),12)!=0)continue;
        const uint32_t ox=x-c.ax,oy=y-c.ay;bool exact=true;
        for(uint32_t sy=0;sy<64&&exact;++sy)for(uint32_t sx=0;sx<64;++sx)if(c.alpha[sy*64+sx]==15) {
            if(!consume(1))return refuse();
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
            /* In V3 a raw static reference is an independent full-raster
               explanation even when located cursor pixels touch this region.
               It must compete with flip+cursor, not be vetoed by that locator.
               Keep the established V1/V2 intersection behavior unchanged. */
            if((engine->flip_enabled || !intersects) && entry.hash==hash) {
                if(!reserve()||!consume(ReferenceBytes/2))return refuse();
                if(std::memcmp(entry.bytes.data(),engine->region.data(),ReferenceBytes)==0) {
                    engine->match=&entry;engine->foreground=nullptr;engine->x=region.x;engine->y=region.y;++matches;
                }
            }
            for(size_t i=0;i<placement_count;++i) {
                const auto& p=placements[i];bool touched=false,exact=true;
                if(engine->flip_enabled) {
                    /* Necessary static-cursor probes share the V3 global work
                       budget. They only reject; all surviving bytes still verify. */
                    constexpr uint32_t rows[]={0,41,82,123,164,205,246,289};
                    constexpr uint32_t columns[]={24,74,124,174};
                    for(uint32_t y:rows)for(uint32_t x:columns) {
                        if(cursor_alpha(&p,region.x+x,region.y+y))continue;
                        if(!consume(1))return refuse();
                        const size_t at=(size_t(y)*200+x)*2;
                        if(read_word(entry.bytes.data()+at)!=read_word(engine->region.data()+at)){exact=false;break;}
                    }
                    if(!exact)continue;
                }
                /* Reject complete unoccluded row spans with bounded library
                   comparisons before per-pixel composition. They cannot be
                   changed by this candidate; surviving cases still compare
                   the entire explained ROI below, with no acceptance mask. */
                for(uint32_t ry=0;ry<CardHeight&&exact;++ry) {
                    const uint32_t ny=region.y+ry;const size_t row=size_t(ry)*CardWidth*2;
                    if(ny<p.y||ny>=p.y+64||p.x>=region.x+CardWidth||p.x+64<=region.x) {
                        if(!consume(CardWidth))return refuse();
                        exact=std::memcmp(entry.bytes.data()+row,engine->region.data()+row,CardWidth*2)==0;
                    } else {
                        const uint32_t left=p.x>region.x?p.x-region.x:0;
                        const uint32_t right=std::min(CardWidth,p.x+64-region.x);
                        if(!consume(left+CardWidth-right))return refuse();
                        exact=std::memcmp(entry.bytes.data()+row,engine->region.data()+row,left*2)==0 &&
                            std::memcmp(entry.bytes.data()+row+right*2,engine->region.data()+row+right*2,(CardWidth-right)*2)==0;
                    }
                }
                if(!exact)continue;
                bool possible=false;
                for(uint32_t sy=0;sy<64&&!possible;++sy)for(uint32_t sx=0;sx<64;++sx)
                    if(p.sprite->alpha[sy*64+sx]&&p.x+sx>=region.x&&p.x+sx<region.x+CardWidth&&
                        p.y+sy>=region.y&&p.y+sy<region.y+CardHeight){possible=true;break;}
                if(!possible)continue;
                if(!reserve()||!consume(ReferenceBytes/2))return refuse();
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
    if(!match_flips(*engine,placements.data(),placement_count,matches,proof_candidates,proof_work))matches=0;
    if (matches != 1) {engine->match = nullptr;engine->foreground=nullptr;engine->flip_reference=nullptr;engine->flip_profile=nullptr;}
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
    if(!engine||!cursor||!engine->valid||(!engine->match&&!engine->flip_reference)||!engine->foreground)return 0;
    *cursor={engine->foreground->bgra.data(),static_cast<int32_t>(engine->cursor_x),static_cast<int32_t>(engine->cursor_y),engine->foreground->state,engine->generation};return 1;
}
extern "C" int hdv_flip(const HdvEngine* engine,HdvFlipPlan* plan) {
    if(!engine||!plan||!engine->valid||!engine->flip_reference||!engine->flip_profile)return 0;
    const auto& p=*engine->flip_profile;
    *plan={engine->flip_reference->key,7,89,200,290,p.pose.x,p.pose.width,p.pose.q,engine->generation,
        engine->flip_reference->master.c_str(),engine->backgrounds[p.background].bytes.data()};return 1;
}
