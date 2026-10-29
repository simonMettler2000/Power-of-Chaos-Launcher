#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include "hd_visible.h"
#include "hd_visible_gdi.h"
#include "hd_visible_png.h"
#include <algorithm>
#include <cstring>
#include <memory>
#include <sstream>
#include <string>
#include <vector>
namespace {
struct EngineDeleter { void operator()(HdvEngine* value) const { hdv_destroy(value); } };
struct Image { uint32_t key=0; uint64_t age=0; std::vector<unsigned char> bgra; };
constexpr uint32_t MasterWidth=800, MasterHeight=1160;
std::wstring from_utf8(const std::string& input) {
    if (input.empty() || input.size() > 32767 || input.find('\0') != std::string::npos) return {};
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring result(static_cast<size_t>(count), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), result.data(), count) != count) return {};
    return result;
}
bool absolute_local(const std::wstring& path) {
    /* Only drive-rooted local paths: refuse device, network and relative paths. */
    if (path.size() < 3 || path.size() > 32000 || path[1] != L':' ||
        (path[2] != L'\\' && path[2] != L'/') ||
        !((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z'))) return false;
    if (path.find(L':', 2) != std::wstring::npos) return false;
    size_t start = 3;
    while (start < path.size()) {
        const size_t end = path.find_first_of(L"\\/", start);
        const auto part = path.substr(start, end == std::wstring::npos ? path.size() - start : end - start);
        if (part.empty() || part.size() > 255 || part == L"." || part == L".." || part.back() == L'.' || part.back() == L' ') return false;
        for (wchar_t value : part) if (value < 32 || value == L'"' || value == L'<' || value == L'>' || value == L'|' || value == L'*' || value == L'?') return false;
        auto name = part.substr(0, part.find(L'.'));
        while (!name.empty() && name.back() == L' ') name.pop_back();
        for (auto& value : name) if (value >= L'a' && value <= L'z') value = static_cast<wchar_t>(value - L'a' + L'A');
        if (name == L"CON" || name == L"PRN" || name == L"AUX" || name == L"NUL" || name == L"CONIN$" || name == L"CONOUT$" ||
            (name.size() == 4 && (name.substr(0, 3) == L"COM" || name.substr(0, 3) == L"LPT") &&
                ((name[3] >= L'1' && name[3] <= L'9') || name[3] == L'\u00b9' || name[3] == L'\u00b2' || name[3] == L'\u00b3'))) return false;
        if (end == std::wstring::npos) break;
        start = end + 1;
        if (start == path.size()) return false;
    }
    /* Reject all linked ancestors before any content open; caller-supplied
       local packs must consist of concrete files in concrete directories. */
    for (size_t i = 3; i <= path.size(); ++i) {
        if (i != path.size() && path[i] != L'\\' && path[i] != L'/') continue;
        const DWORD attrs = GetFileAttributesW(path.substr(0, i).c_str());
        if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
    }
    return true;
}
bool bounded_file(const std::wstring& path, size_t max_bytes, std::vector<unsigned char>& output) {
    if (!absolute_local(path)) return false;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    BY_HANDLE_FILE_INFORMATION information{};
    const bool valid = GetFileType(file) == FILE_TYPE_DISK && GetFileInformationByHandle(file, &information) &&
        !(information.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) &&
        information.nFileSizeHigh == 0 && information.nFileSizeLow > 0 && information.nFileSizeLow <= max_bytes;
    bool success = false;
    if (valid) {
        try {
            output.resize(information.nFileSizeLow);
            DWORD read = 0;
            success = ReadFile(file, output.data(), information.nFileSizeLow, &read, nullptr) && read == information.nFileSizeLow;
        } catch (...) { CloseHandle(file); throw; }
    }
    CloseHandle(file); return success;
}
bool parse_uint(const std::string& text, uint32_t& value) {
    if (text.empty() || text.size() > 10) return false;
    uint64_t number = 0;
    for (char c : text) { if (c < '0' || c > '9') return false; number = number * 10 + static_cast<unsigned>(c - '0'); }
    if (!number || number > UINT32_MAX) return false;
    value = static_cast<uint32_t>(number); return true;
}
bool bank_sha256(const std::vector<unsigned char>& bytes,const std::string& expected) {
    if(expected.size()!=64)return false;
    for(char c:expected)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))return false;
    struct Hash {
        BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;std::vector<unsigned char> object;
        ~Hash(){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
    } h;
    if(BCryptOpenAlgorithmProvider(&h.algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return false;
    DWORD length=0,received=0;
    if(BCryptGetProperty(h.algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&length),sizeof(length),&received,0)<0 ||
        received!=sizeof(length)||!length||length>1024*1024)return false;
    h.object.resize(length);unsigned char digest[32]{};
    if(BCryptCreateHash(h.algorithm,&h.hash,h.object.data(),length,nullptr,0,0)<0 ||
        BCryptHashData(h.hash,const_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(bytes.size()),0)<0 ||
        BCryptFinishHash(h.hash,digest,sizeof(digest),0)<0)return false;
    /* Destroy while its owned provider buffer still exists. */
    if(BCryptDestroyHash(h.hash)<0)return false;h.hash=nullptr;
    const char* hex="0123456789abcdef";
    for(size_t i=0;i<32;++i)if(expected[i*2]!=hex[digest[i]>>4]||expected[i*2+1]!=hex[digest[i]&15])return false;
    return true;
}
struct CursorCanvas {
    HDC dc=nullptr;HBITMAP bitmap=nullptr;HGDIOBJ prior=nullptr;unsigned char* pixels=nullptr;int w=0,h=0;
    bool create(int width,int height) {
        if(width<=0||height<=0||uint64_t(width)*height>UINT64_C(4)*1024*1024)return false;
        w=width;h=height;dc=CreateCompatibleDC(nullptr);if(!dc)return false;
        BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=w;
        info.bmiHeader.biHeight=-h;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
        bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,reinterpret_cast<void**>(&pixels),nullptr,0);if(!bitmap||!pixels)return false;
        prior=SelectObject(dc,bitmap);return prior&&prior!=HGDI_ERROR;
    }
    ~CursorCanvas(){
        if(dc)GdiFlush();
        if(dc&&prior&&prior!=HGDI_ERROR)SelectObject(dc,prior);
        const bool deleted=!bitmap||DeleteObject(bitmap)!=0;
        if(dc)DeleteDC(dc);
        /* A failed selection restoration may leave the bitmap selected. The
           DC owns that selection only until DeleteDC; retry after releasing it. */
        if(!deleted)DeleteObject(bitmap);
    }
    bool compose_plain(const Image& master,int width,int height) {
        if(!create(width,height)||!SetStretchBltMode(dc,HALFTONE)||!SetBrushOrgEx(dc,0,0,nullptr))return false;
        BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=800;
        info.bmiHeader.biHeight=-1160;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
        const int drawn=StretchDIBits(dc,0,0,w,h,0,0,800,1160,master.bgra.data(),&info,DIB_RGB_COLORS,SRCCOPY);
        return drawn!=0&&drawn!=GDI_ERROR&&GdiFlush()!=0;
    }
    bool compose(const Image& master,const HdvCursor& cursor,int vx,int vy,int vw,int vh,int left,int top,int right,int bottom,int base_mode,const std::vector<uint32_t>& coordinates) {
        if(base_mode<BLACKONWHITE||base_mode>COLORONCOLOR||!create(right-left,bottom-top)||
            !SetStretchBltMode(dc,HALFTONE)||!SetBrushOrgEx(dc,0,0,nullptr))return false;
        BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=800;
        info.bmiHeader.biHeight=-1160;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
        const int drawn=StretchDIBits(dc,0,0,w,h,0,0,800,1160,master.bgra.data(),&info,DIB_RGB_COLORS,SRCCOPY);
        if(!drawn||drawn==GDI_ERROR||!GdiFlush())return false;
        /* Ask GDI to sample an owned coordinate field using the COMPLETE
           viewport geometry and inherited base mode. Fractional device phase
           is not inferred from ROI-local floor arithmetic. This never reads a
           game/window DC: both source field and sampled DIB are owned here. */
        CursorCanvas phase;if(!phase.create(w,h)||!SetStretchBltMode(phase.dc,base_mode))return false;
        if(coordinates.size()!=size_t(800)*600)return false;
        BITMAPINFO mapping{};mapping.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);mapping.bmiHeader.biWidth=800;
        mapping.bmiHeader.biHeight=-600;mapping.bmiHeader.biPlanes=1;mapping.bmiHeader.biBitCount=32;
        const int mapped=StretchDIBits(phase.dc,vx-left,vy-top,vw,vh,0,0,800,600,coordinates.data(),&mapping,DIB_RGB_COLORS,SRCCOPY);
        if(!mapped||mapped==GDI_ERROR||!GdiFlush())return false;
        const auto* sprite=static_cast<const unsigned char*>(cursor.bgra);
        for(int y=0;y<h;++y)for(int x=0;x<w;++x) {
            uint32_t code=0;std::memcpy(&code,phase.pixels+(size_t(y)*w+x)*4,4);code&=0xffffff;
            if(!code||code>800*600)return false;--code;
            const int nx=static_cast<int>(code%800),ny=static_cast<int>(code/800);
            if(nx<cursor.x||ny<cursor.y||nx>=cursor.x+64||ny>=cursor.y+64)continue;
            const size_t si=(size_t(ny-cursor.y)*64+nx-cursor.x)*4;const unsigned q=sprite[si+3]>>4;
            auto* d=pixels+(size_t(y)*w+x)*4;
            for(size_t c=0;c<3;++c){const unsigned shift=c==1?2:3;const unsigned narrow=sprite[si+c]>>shift;
                /* Exhaustively verified against owned GDI conversion of all
                   65536 native565 words: opaque foreground keeps base colors. */
                const unsigned source=(narrow<<shift)|(narrow>>(8-2*shift));
                d[c]=static_cast<unsigned char>(source*q/15+unsigned(d[c])*(15-q)/15);}
            d[3]=255;
        }
        return true;
    }
    bool compose_flip(const Image& master,const HdvFlipPlan& plan,const HdvCursor* cursor,
        const HdvFrame& frame,int vx,int vy,int vw,int vh,int left,int top,int right,int bottom,
        int base_mode,const std::vector<uint32_t>& coordinates) {
        const int fl=vx+static_cast<int>(int64_t(plan.face_x)*vw/800);
        const int fr=vx+static_cast<int>(int64_t(plan.face_x+plan.face_width)*vw/800);
        const int ft=vy+static_cast<int>(int64_t(89)*vh/600),fb=vy+static_cast<int>(int64_t(379)*vh/600);
        const uint64_t area=uint64_t(right-left)*(bottom-top),art_area=uint64_t(fr-fl)*(fb-ft);
        if(base_mode<1||base_mode>3||fr<=fl||fb<=ft||area*2+art_area>UINT64_C(4)*1024*1024||
            coordinates.size()!=size_t(800)*600||!plan.background565||!create(right-left,bottom-top))return false;
        CursorCanvas art,phase;
        if(!art.create(fr-fl,fb-ft)||!phase.create(w,h)||!SetStretchBltMode(art.dc,HALFTONE)||
            !SetBrushOrgEx(art.dc,0,0,nullptr)||!SetStretchBltMode(phase.dc,base_mode))return false;
        BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=800;
        info.bmiHeader.biHeight=-1160;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
        const int drawn=StretchDIBits(art.dc,0,0,art.w,art.h,0,0,800,1160,master.bgra.data(),&info,DIB_RGB_COLORS,SRCCOPY);
        if(!drawn||drawn==GDI_ERROR)return false;
        info.bmiHeader.biHeight=-600;
        const int mapped=StretchDIBits(phase.dc,vx-left,vy-top,vw,vh,0,0,800,600,coordinates.data(),&info,DIB_RGB_COLORS,SRCCOPY);
        if(!mapped||mapped==GDI_ERROR||!GdiFlush())return false;
        const auto* background=static_cast<const unsigned char*>(plan.background565);
        const auto* native=static_cast<const unsigned char*>(frame.pixels);
        const auto expand=[](uint16_t word,unsigned c) {
            const unsigned shift=c==0?0:c==1?5:11,mask=c==1?63:31;
            const unsigned value=(word>>shift)&mask;
            return c==1?(value<<2)|(value>>4):(value<<3)|(value>>2);
        };
        for(int y=0;y<h;++y)for(int x=0;x<w;++x) {
            uint32_t code=0;std::memcpy(&code,phase.pixels+(size_t(y)*w+x)*4,4);code&=0xffffff;
            if(!code||code>800*600)return false;--code;
            const int nx=static_cast<int>(code%800),ny=static_cast<int>(code/800);
            const bool inside=nx>=7&&nx<207&&ny>=89&&ny<379;
            const bool face=x+left>=fl&&x+left<fr&&y+top>=ft&&y+top<fb;
            /* A q15 output owns only its opaque footprint. At fractional edges
               mapping outside the admitted aperture, retain the SAME native
               pixel, including any already-composed cursor; never blend twice. */
            const bool retain_native=!inside||(plan.q==15&&!face);
            uint16_t base=0;
            if(retain_native)std::memcpy(&base,native+(size_t(ny)*800+nx)*2,2);
            else std::memcpy(&base,background+(size_t(ny-89)*200+nx-7)*2,2);
            auto* output=pixels+(size_t(y)*w+x)*4;
            for(unsigned c=0;c<3;++c)output[c]=static_cast<unsigned char>(expand(base,c));
            output[3]=255;
            if(retain_native)continue;
            if(face) {
                const auto* source=art.pixels+(size_t(y+top-ft)*art.w+x+left-fl)*4;
                const bool equal=source[0]==output[0]&&source[1]==output[1]&&source[2]==output[2];
                if(!equal)for(unsigned c=0;c<3;++c)
                    output[c]=static_cast<unsigned char>(unsigned(source[c])*plan.q/15+unsigned(output[c])*(15-plan.q)/15);
            }
            if(cursor&&nx>=cursor->x&&ny>=cursor->y&&nx<cursor->x+64&&ny<cursor->y+64) {
                const auto* sprite=static_cast<const unsigned char*>(cursor->bgra)+(size_t(ny-cursor->y)*64+nx-cursor->x)*4;
                const unsigned q=sprite[3]>>4;
                for(unsigned c=0;c<3;++c) {
                    const unsigned shift=c==1?2:3,value=sprite[c]>>shift;
                    const unsigned foreground=(value<<shift)|(value>>(8-2*shift));
                    output[c]=static_cast<unsigned char>(foreground*q/15+unsigned(output[c])*(15-q)/15);
                }
            }
        }
        return true;
    }
};
bool physical_dc(HDC dc) {
    const DWORD kind=GetObjectType(dc); POINT a{},b{};
    if ((kind!=OBJ_DC && kind!=OBJ_MEMDC) || GetMapMode(dc)!=MM_TEXT || GetLayout(dc)!=0 ||
        !GetWindowOrgEx(dc,&a) || !GetViewportOrgEx(dc,&b) || a.x || a.y || b.x || b.y) return false;
    if (GetGraphicsMode(dc)==GM_ADVANCED) {
        XFORM x{};
        if (!GetWorldTransform(dc,&x) || x.eM11!=1 || x.eM12!=0 || x.eM21!=0 || x.eM22!=1 || x.eDx!=0 || x.eDy!=0) return false;
    }
    return true;
}
struct SavedDC {
    HDC dc; int saved;
    explicit SavedDC(HDC value):dc(value),saved(SaveDC(value)){}
    bool restore() {
        if(!saved) return true;
        if(!RestoreDC(dc,saved)) return false;
        saved=0; return true;
    }
    ~SavedDC(){ restore(); }
};
struct Bitmap565 { BITMAPINFOHEADER header; DWORD masks[3]; };
int paint_owned_base(HDC dc,const HdvFrame& frame,int vx,int vy,int vw,int vh,bool scaled_only=false) {
    /* Drawing a complete destination rectangle does not mutate DC attributes.
       Use the caller clip as-is, including when a prior SaveDC failed. */
    RECT clip{};if(GetClipBox(dc,&clip)==NULLREGION)return 3;
    Bitmap565 base{};base.header.biSize=sizeof(BITMAPINFOHEADER);base.header.biWidth=800;base.header.biHeight=-600;
    base.header.biPlanes=1;base.header.biBitCount=16;base.header.biCompression=BI_BITFIELDS;
    base.masks[0]=0xf800;base.masks[1]=0x07e0;base.masks[2]=0x001f;
    const int painted=StretchDIBits(dc,vx,vy,vw,vh,0,0,800,600,frame.pixels,reinterpret_cast<const BITMAPINFO*>(&base),DIB_RGB_COLORS,SRCCOPY);
    if(painted!=0&&painted!=GDI_ERROR)return 1;
    /* After a possibly partial visible operation, native0,0 cannot repair a
       larger/offset viewport. Only a complete scaled repaint is a repair. */
    if(scaled_only)return 0;
    /* The historical prepaint failed-stretch destination remains exactly0,0. */
    const int native=SetDIBitsToDevice(dc,0,0,800,600,0,0,0,600,frame.pixels,reinterpret_cast<const BITMAPINFO*>(&base),DIB_RGB_COLORS);
    return native!=0&&native!=GDI_ERROR?1:0;
}
int restore_owned_base(SavedDC& save,HDC dc,const HdvFrame& frame,int vx,int vy,int vw,int vh,bool visible_paint) {
    /* A restoration failure is never an HD success. One retry handles a
       transient API failure; unrecoverable state returns the legacy0 route. */
    if(!save.restore()&&!save.restore())return 0;
    return paint_owned_base(dc,frame,vx,vy,vw,vh,visible_paint);
}

}
struct HdvGdi {
    DWORD owner=GetCurrentThreadId();
    std::unique_ptr<HdvEngine,EngineDeleter> engine;
    std::vector<Image> images;
    std::vector<uint32_t> coordinate_field;
    uint64_t cache_age=0;
    bool is_owner()const{return owner==GetCurrentThreadId();}
    void clear() {
        engine.reset(); images.clear(); coordinate_field.clear(); cache_age=0;
    }
bool read_config(const wchar_t* path) {
    if (!path) return false;
    std::vector<unsigned char> data;
    if (!bounded_file(path, 1024 * 1024, data)) return false;
    std::string text(data.begin(), data.end());
    if (text.find('\0') != std::string::npos) return false;
    std::istringstream lines(text); std::string line;
    auto next = [&]() { if (!std::getline(lines, line)) return false; if (!line.empty() && line.back() == '\r') line.pop_back(); return true; };
    if(!next())return false;
    const bool flip_config=line=="POC_HD_VISIBLE_V3";
    const bool cursor_config=line=="POC_HD_VISIBLE_V2"||flip_config;
    if((line!="POC_HD_VISIBLE_V1"&&!cursor_config)||!next())return false;
    auto split = line.find('\t'); uint32_t game = 0, format = 0;
    if (split == std::string::npos || !parse_uint(line.substr(0, split), game) || !parse_uint(line.substr(split + 1), format)) return false;
    if (format != HDV_RGB565) return false;
    std::unique_ptr<HdvEngine, EngineDeleter> candidate(hdv_create(game, format));
    if (!candidate) return false;
    if(cursor_config) {
        if(!next())return false;std::vector<std::string> fields;std::istringstream row(line);std::string field;
        while(std::getline(row,field,'\t'))fields.push_back(field);
        if(fields.size()!=5||fields[0]!="CURSORS_BGRA64_V1"||line.back()=='\t')return false;
        for(uint32_t state=1;state<=2;++state) {
            std::vector<unsigned char> bank;const auto file=from_utf8(fields[state*2-1]);
            if(!bounded_file(file,16384,bank)||bank.size()!=16384||!bank_sha256(bank,fields[state*2])||
                !hdv_add_cursor(candidate.get(),state,bank.data(),bank.size()))return false;
        }
    }
    if(flip_config) {
        if(!next())return false;std::vector<std::string> fields;std::istringstream row(line);std::string field;
        while(std::getline(row,field,'\t'))fields.push_back(field);
        if(fields.size()!=5||fields[0]!="FLIP_BG565_V1"||line.back()=='\t')return false;
        for(size_t state=1;state<=2;++state) {
            std::vector<unsigned char> bank;const auto file=from_utf8(fields[state*2-1]);
            if(!bounded_file(file,116000,bank)||bank.size()!=116000||!bank_sha256(bank,fields[state*2])||
                !hdv_add_flip_background(candidate.get(),bank.data(),bank.size()))return false;
        }
        if(!next()||line!="FLIP_POSES_V1\t196,9,15\t184,15,14\t166,24,13\t141,37,12\t111,52,10\t76,69,7\t39,88,5")return false;
        const HdvFlipPose poses[]={{196,9,15},{184,15,14},{166,24,13},{141,37,12},{111,52,10},{76,69,7},{39,88,5}};
        if(!hdv_enable_flip(candidate.get(),poses,7))return false;
    }
    size_t count = 0;
    while (next()) {
        if (line.empty()) return false;
        const auto first = line.find('\t'), second = first == std::string::npos ? first : line.find('\t', first + 1);
        if (first == std::string::npos || second == std::string::npos || line.find('\t', second + 1) != std::string::npos) return false;
        uint32_t key = 0;
        const auto reference = from_utf8(line.substr(first + 1, second - first - 1));
        const auto master = from_utf8(line.substr(second + 1));
        std::vector<unsigned char> baseline;
        if (++count > 1024 || !parse_uint(line.substr(0, first), key) || !absolute_local(master) ||
            !bounded_file(reference, 200 * 290 * 2, baseline) || baseline.size() != 200 * 290 * 2 ||
            !hdv_add(candidate.get(), key, baseline.data(), baseline.size(), master.c_str())) return false;
    }
    if (!hdv_seal(candidate.get())) return false;
    /* Constant authored coordinate field is prepared once, never per frame.
       Allocation failure here leaves the previously cleared context disabled. */
    std::vector<uint32_t> field;
    if(cursor_config){field.resize(size_t(800)*600);for(size_t i=0;i<field.size();++i)field[i]=static_cast<uint32_t>(i+1);}
    coordinate_field=std::move(field);engine = std::move(candidate); return true;
}
const Image* image_for(const HdvMatch& match) {
    if (cache_age == UINT64_MAX) { images.clear(); cache_age = 0; }
    for (auto& entry : images) if (entry.key == match.key) { entry.age = ++cache_age; return &entry; }
    /* One bounded read and the shared strict decoder produce owned pixels.
       The candidate image is never inserted into the cache before success. */
    std::vector<unsigned char> png;
    if (!bounded_file(match.master_path, 16 * 1024 * 1024, png)) return nullptr;
    Image image; image.key = match.key; image.age = ++cache_age;
    image.bgra.resize(size_t(MasterWidth) * MasterHeight * 4);
    if (!hdv_decode_master_bgra(png.data(),png.size(),image.bgra.data(),image.bgra.size())) return nullptr;
    if (images.size() == 4) images.erase(std::min_element(images.begin(), images.end(), [](const Image& a, const Image& b) { return a.age < b.age; }));
    images.push_back(std::move(image)); return &images.back();
}
};
extern "C" HdvGdi* hdvg_create(void) { try { return new HdvGdi; } catch (...) {return nullptr;} }
extern "C" void hdvg_shutdown(HdvGdi* p) { if(p && p->is_owner()) p->clear(); }
extern "C" void hdvg_destroy(HdvGdi* p) { if(p && p->is_owner()) {p->clear();delete p;} }
extern "C" int hdvg_configure(HdvGdi* p,const wchar_t* config) {
    if(!p || !p->is_owner()) return 0;
    p->clear(); if(!config) return 1;
    try {return p->read_config(config)?1:0;} catch(...) {p->clear();return 0;}
}
extern "C" int hdvg_present(HdvGdi* p,void* destination,int vx,int vy,int vw,int vh,
    const void* source,uint32_t width,uint32_t height,uint32_t pitch,uint32_t format,int eligible) {
    if(!p || !p->is_owner())return 0;
    hdv_invalidate(p->engine.get());HDC dc=static_cast<HDC>(destination);
    const int64_t aspect=int64_t(vw)*3-int64_t(vh)*4;
    if(!p->engine||!eligible||!dc||!source||width!=800||height!=600||format!=HDV_RGB565||pitch<1600||pitch>65536||
        vw<=0||vh<=0||vw>16384||vh>16384||vx<0||vy<0||vx>32767-vw||vy>32767-vh||aspect < -4||aspect > 4||!physical_dc(dc))return 0;
    HdvFrame frame{};bool visible_paint=false;
    try {
        if(!hdv_capture(p->engine.get(),source,width,height,pitch,format)||!hdv_snapshot(p->engine.get(),&frame))return 0;
        HdvMatch match{};HdvFlipPlan flip{};bool flip_match=false;const Image* master=nullptr;
        if(hdv_match(p->engine.get(),&match)&&match.generation==frame.generation) {
            try{master=p->image_for(match);}catch(...){master=nullptr;}
        } else if(hdv_flip(p->engine.get(),&flip)&&flip.generation==frame.generation) {
            flip_match=true;HdvMatch lookup{flip.key,7,89,200,290,flip.generation,flip.master_path};
            try{master=p->image_for(lookup);}catch(...){master=nullptr;}
        }
        if(!master)return paint_owned_base(dc,frame,vx,vy,vw,vh);
        HdvCursor cursor{};const bool has_cursor=hdv_cursor(p->engine.get(),&cursor)!=0;
        if((has_cursor&&cursor.generation!=frame.generation)||((flip_match||has_cursor)&&(vw<800||vh<600)))
            return paint_owned_base(dc,frame,vx,vy,vw,vh);
        const uint32_t lx=flip_match?(flip.q==15?flip.face_x:7):match.x,ly=flip_match?89:match.y;
        const uint32_t lw=flip_match?(flip.q==15?flip.face_width:200):match.width,lh=flip_match?290:match.height;
        const int left=vx+static_cast<int>(int64_t(lx)*vw/800),top=vy+static_cast<int>(int64_t(ly)*vh/600);
        const int right=vx+static_cast<int>(int64_t(lx+lw)*vw/800),bottom=vy+static_cast<int>(int64_t(ly+lh)*vh/600);
        if(right<=left||bottom<=top)return paint_owned_base(dc,frame,vx,vy,vw,vh);
        const int base_mode=GetStretchBltMode(dc);CursorCanvas canvas;bool prepared=false;
        /* Complete all resource/decode/sampling/blend work before ANY visible
           paint. The source remains this exact generation's owned snapshot. */
        if(flip_match)prepared=canvas.compose_flip(*master,flip,has_cursor?&cursor:nullptr,frame,vx,vy,vw,vh,left,top,right,bottom,base_mode,p->coordinate_field);
        else if(has_cursor)prepared=canvas.compose(*master,cursor,vx,vy,vw,vh,left,top,right,bottom,base_mode,p->coordinate_field);
        else prepared=canvas.compose_plain(*master,right-left,bottom-top);
        if(!prepared||GetStretchBltMode(dc)!=base_mode)return paint_owned_base(dc,frame,vx,vy,vw,vh);
        SavedDC save(dc);if(!save.saved)return paint_owned_base(dc,frame,vx,vy,vw,vh);
        RECT clip{};const int clip_kind=GetClipBox(dc,&clip);
        if(clip_kind==ERROR)return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        if(clip_kind==NULLREGION)return save.restore()?3:restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        if(IntersectClipRect(dc,vx,vy,vx+vw,vy+vh)==ERROR)return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        RECT roi{left,top,right,bottom};
        if(!RectVisible(dc,&roi))return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        bool base_ok=false,clip_restored=false;
        {
            SavedDC outside(dc);
            if(outside.saved) {
                const int kind=ExcludeClipRect(dc,left,top,right,bottom);
                /* An ROI-only caller clip legitimately leaves no outside
                   pixels. Do not turn that into the native-size fallback. */
                if(kind==NULLREGION)base_ok=true;
                else if(kind!=ERROR) {
                    Bitmap565 base{};base.header.biSize=sizeof(BITMAPINFOHEADER);base.header.biWidth=800;base.header.biHeight=-600;
                    base.header.biPlanes=1;base.header.biBitCount=16;base.header.biCompression=BI_BITFIELDS;
                    base.masks[0]=0xf800;base.masks[1]=0x07e0;base.masks[2]=0x001f;
                    /* SAME real-window route, full geometry, inherited mode
                       and global phase; only the qualified footprint is cut. */
                    visible_paint=true;
                    const int drawn=StretchDIBits(dc,vx,vy,vw,vh,0,0,800,600,frame.pixels,reinterpret_cast<const BITMAPINFO*>(&base),DIB_RGB_COLORS,SRCCOPY);
                    base_ok=drawn!=0&&drawn!=GDI_ERROR;
                }
                clip_restored=outside.restore();
            }
        }
        if(!base_ok||!clip_restored)return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        const int roi_clip=IntersectClipRect(dc,left,top,right,bottom);
        if(roi_clip==ERROR)return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        if(roi_clip==NULLREGION)return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        if(!SetStretchBltMode(dc,COLORONCOLOR))return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=canvas.w;
        info.bmiHeader.biHeight=-canvas.h;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;
        visible_paint=true;
        const int overlay=StretchDIBits(dc,left,top,right-left,bottom-top,0,0,canvas.w,canvas.h,canvas.pixels,&info,DIB_RGB_COLORS,SRCCOPY);
        if(overlay==0||overlay==GDI_ERROR)return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        if(!save.restore())return restore_owned_base(save,dc,frame,vx,vy,vw,vh,visible_paint);
        return 2;
    }catch(...) {
        hdv_invalidate(p->engine.get());
        /* Unwinding may have failed to restore a mutated caller clip/state.
           Do not paint or claim repair when that state is no longer proven. */
        return 0;
    }
}
