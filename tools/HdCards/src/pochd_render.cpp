/* HD card artwork: Direct3D 11 presenter.

   Draws the game's 800x600 picture into the provider's viewport and, on top of it, the HD picture of
   every card layer - only on the pixels the tracker assigned to that layer. Direct3D and the shader
   compiler are loaded at run time; when either is missing the provider's plain GDI presenter is used. */
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "pochd.h"

extern "C" {
HWND pochd_host_window(void);
BOOL pochd_host_running(void);
HANDLE pochd_host_semaphore(void);
void pochd_host_viewport(int* values);          /* client w, h, viewport x, y, w, h */
DWORD pochd_host_fallback(void);
BOOL pochd_host_primary(WORD* pixels);
DWORD pochd_render_run(void);
unsigned lodepng_decode32(unsigned char** out, unsigned* w, unsigned* h, const unsigned char* in, size_t insize);
}

namespace {

template <class T> void release(T*& pointer) { if (pointer) { pointer->Release(); pointer = NULL; } }

struct Vertex { float x, y, u, v, fu, fv, id, recolour, gain, mixed, left, top, right, bottom; };
/* the whole picture, one quad a layer, and one quad for every piece of a card drawn in pieces */
enum { MAX_QUADS = POCHD_MAX_LAYERS + 1 + POCHD_MAX_PIECES };

const char kShader[] =
    "struct VI { float2 p : POSITION; float2 uv : TEXCOORD0; float2 fuv : TEXCOORD1; float2 id : TEXCOORD2; float2 mix : TEXCOORD3;\n"
    "  float4 box : TEXCOORD4; };\n"
    "struct VO { float4 p : SV_Position; float2 uv : TEXCOORD0; float2 fuv : TEXCOORD1; float2 id : TEXCOORD2; float2 mix : TEXCOORD3;\n"
    "  float4 box : TEXCOORD4; };\n"
    "cbuffer C : register(b0) { float4 k; };\n"   /* x: filter, y: tint, zw: output pixels per frame pixel */
    "Texture2D t0 : register(t0); Texture2D t1 : register(t1); Texture2D t2 : register(t2); Texture2D t3 : register(t3);\n"
    "SamplerState smooth : register(s0); SamplerState exact : register(s1); SamplerState detail : register(s2);\n"
    "VO vs(VI i) { VO o; o.p = float4(i.p, 0, 1); o.uv = i.uv; o.fuv = i.fuv; o.id = i.id; o.mix = i.mix; o.box = i.box; return o; }\n"
    /* One 800x600 plane enlarged with the chosen filter: used for the game's picture and for the mix plane. */
    "float4 picture(Texture2D t, float2 uv) {\n"
    "  if (k.x < 0.5) return t.Sample(exact, uv);\n"
    "  if (k.x < 1.5) return t.Sample(smooth, uv);\n"
    "  float2 size = float2(800, 600); float2 texel = uv * size; float2 scale = max(floor(k.zw), 1.0);\n"
    "  float2 part = frac(texel) - 0.5; float2 region = 0.5 - 0.5 / scale;\n"
    "  float2 f = (part - clamp(part, -region, region)) * scale + 0.5;\n"
    "  return t.Sample(smooth, (floor(texel) + f) / size); }\n"
    "float4 base(VO i) : SV_Target { return float4(picture(t0, i.uv).rgb, 1); }\n"
    "float4 card(VO i) : SV_Target {\n"
    "  float owner = t1.Sample(exact, i.fuv).r * 255.0;\n"
    "  if (abs(owner - i.id.x) > 0.5) discard;\n"
    "  float3 c = t0.Sample(detail, i.uv).rgb * i.mix.x;\n"            /* the game's own mix: card * gain ... */
    "  if (i.mix.y > 0.5) {\n"                                          /* ... * part shown + extra, per pixel; */
    "    float4 m = picture(t3, clamp(i.fuv, i.box.xy, i.box.zw));\n"   /* never read past the card's own area */
    "    c = c * m.a + m.rgb; }\n"
    "  c = saturate(c);\n"
    "  if (i.id.y > 0.5) {\n"   /* the game recoloured this card afterwards: one table row per layer */
    "    float row = (i.id.x + 0.5) / 256.0; float3 at = (c * 255.0 + 0.5) / 256.0;\n"
    "    c = float3(t2.Sample(smooth, float2(at.r, row)).r, t2.Sample(smooth, float2(at.g, row)).g,\n"
    "               t2.Sample(smooth, float2(at.b, row)).b); }\n"
    "  if (k.y > 0.5) c = lerp(c, float3(0.1, 1.0, 0.2), 0.4);\n"
    "  return float4(c, 1); }\n";

/* ---- device objects (kept for the life of the process; the presenter thread restarts on resize) ---- */
ID3D11Device* g_device;
ID3D11DeviceContext* g_context;
IDXGISwapChain* g_swap;
ID3D11RenderTargetView* g_target;
ID3D11VertexShader* g_vs;
ID3D11PixelShader* g_ps_base;
ID3D11PixelShader* g_ps_card;
ID3D11InputLayout* g_layout;
ID3D11Buffer* g_vertices;
ID3D11Buffer* g_constants;
ID3D11SamplerState* g_samplers[3];
ID3D11RasterizerState* g_raster;
ID3D11Texture2D* g_base_texture;
ID3D11ShaderResourceView* g_base_view;
ID3D11Texture2D* g_owner_texture;
ID3D11ShaderResourceView* g_owner_view;
ID3D11Texture2D* g_recolour_texture;
ID3D11ShaderResourceView* g_recolour_view;
ID3D11Texture2D* g_mix_texture;
ID3D11ShaderResourceView* g_mix_view;
int g_swap_w, g_swap_h;
bool g_device_failed;
bool g_device_lost;
HWND g_swap_window;

POCHD_FRAME g_frame;
unsigned g_serial;
DWORD g_palette[65536];
DWORD g_converted[POCHD_FRAME_W * POCHD_FRAME_H];

/* ---- HD pictures ---- */
enum { kNone, kQueued, kLoading, kDecoded, kReady, kFailed };
struct Picture
{
    volatile LONG state;
    ID3D11ShaderResourceView* view;
    unsigned char* pixels;
    unsigned width, height;
    unsigned last_used;
    size_t bytes;
};
Picture* g_pictures;
int g_picture_count;
CRITICAL_SECTION g_queue_lock;
HANDLE g_queue_event;
int g_queue[512];
int g_queue_count;
unsigned g_tick;
size_t g_picture_bytes;
volatile LONG g_loaders_started;
wchar_t g_capture_path[MAX_PATH];
volatile LONG g_capture_pending;
wchar_t g_burst_prefix[MAX_PATH];
volatile LONG g_burst_left;
int g_burst_index;

DWORD WINAPI loader_thread(LPVOID)
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    for (;;)
    {
        int card = -1;
        WaitForSingleObject(g_queue_event, INFINITE);
        EnterCriticalSection(&g_queue_lock);
        if (g_queue_count > 0) card = g_queue[--g_queue_count];   /* newest request first: it is on screen now */
        if (g_queue_count > 0) SetEvent(g_queue_event);
        LeaveCriticalSection(&g_queue_lock);
        if (card < 0) continue;
        Picture& picture = g_pictures[card];
        InterlockedExchange(&picture.state, kLoading);
        const wchar_t* path = pochd_card_image(card);
        unsigned char* decoded = NULL;
        unsigned width = 0, height = 0;
        HANDLE file = path ? CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL)
            : INVALID_HANDLE_VALUE;
        if (file != INVALID_HANDLE_VALUE)
        {
            DWORD size = GetFileSize(file, NULL), done = 0;
            if (size > 64 && size < 64u * 1024 * 1024)
            {
                unsigned char* data = (unsigned char*)malloc(size);
                if (data && ReadFile(file, data, size, &done, NULL) && done == size
                    && lodepng_decode32(&decoded, &width, &height, data, size) != 0)
                {
                    free(decoded);
                    decoded = NULL;
                }
                free(data);
            }
            CloseHandle(file);
        }
        if (decoded && width >= 16 && height >= 16 && width <= 4096 && height <= 4096)
        {
            picture.pixels = decoded;
            picture.width = width;
            picture.height = height;
            InterlockedExchange(&picture.state, kDecoded);
        }
        else
        {
            free(decoded);
            pochd_log("could not read HD picture for %s", pochd_card_name(card));
            InterlockedExchange(&picture.state, kFailed);
        }
    }
}

void start_loaders()
{
    if (InterlockedExchange(&g_loaders_started, 1)) return;
    InitializeCriticalSection(&g_queue_lock);
    g_queue_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    for (int i = 0; i < 2; i++) CloseHandle(CreateThread(NULL, 0, loader_thread, NULL, 0, NULL));
}

void evict_pictures()
{
    size_t budget = (size_t)g_pochd.texture_megabytes * 1024 * 1024;
    while (g_picture_bytes > budget)
    {
        int oldest = -1;
        for (int i = 0; i < g_picture_count; i++)
            if (g_pictures[i].state == kReady && g_pictures[i].last_used + 30 < g_tick
                && (oldest < 0 || g_pictures[i].last_used < g_pictures[oldest].last_used)) oldest = i;
        if (oldest < 0) return;
        release(g_pictures[oldest].view);
        g_picture_bytes -= g_pictures[oldest].bytes;
        InterlockedExchange(&g_pictures[oldest].state, kNone);
    }
}

/* Returns the HD picture when it is ready; otherwise makes sure it is being loaded. */
ID3D11ShaderResourceView* picture_view(int card, int& uploads_left)
{
    if (!g_pictures)
    {
        int count = pochd_cards_count();
        if (count <= 0) return NULL;
        g_pictures = (Picture*)calloc(count, sizeof(Picture));
        if (!g_pictures) return NULL;
        g_picture_count = count;
        start_loaders();
    }
    if (card < 0 || card >= g_picture_count) return NULL;
    Picture& picture = g_pictures[card];
    picture.last_used = g_tick;
    LONG state = picture.state;
    if (state == kReady) return picture.view;
    if (state == kNone)
    {
        EnterCriticalSection(&g_queue_lock);
        if (g_queue_count < (int)(sizeof(g_queue) / sizeof(g_queue[0])))
        {
            g_queue[g_queue_count++] = card;
            InterlockedExchange(&picture.state, kQueued);
            SetEvent(g_queue_event);
        }
        LeaveCriticalSection(&g_queue_lock);
    }
    else if (state == kDecoded && uploads_left > 0)
    {
        uploads_left--;
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = picture.width; desc.Height = picture.height; desc.MipLevels = 0; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        desc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
        ID3D11Texture2D* texture = NULL;
        if (SUCCEEDED(g_device->CreateTexture2D(&desc, NULL, &texture)))
        {
            g_context->UpdateSubresource(texture, 0, NULL, picture.pixels, picture.width * 4, 0);
            if (SUCCEEDED(g_device->CreateShaderResourceView(texture, NULL, &picture.view)))
            {
                g_context->GenerateMips(picture.view);
                picture.bytes = (size_t)picture.width * picture.height * 4 * 4 / 3;
                g_picture_bytes += picture.bytes;
            }
            texture->Release();
        }
        free(picture.pixels);
        picture.pixels = NULL;
        InterlockedExchange(&picture.state, picture.view ? kReady : kFailed);
        evict_pictures();
        return picture.view;
    }
    return NULL;
}

/* ---- device ---- */
bool compile(HMODULE compiler, const char* entry, const char* profile, ID3DBlob** blob)
{
    typedef HRESULT (WINAPI* COMPILE)(LPCVOID, SIZE_T, LPCSTR, const D3D_SHADER_MACRO*, ID3DInclude*, LPCSTR, LPCSTR, UINT, UINT,
        ID3DBlob**, ID3DBlob**);
    COMPILE function = (COMPILE)GetProcAddress(compiler, "D3DCompile");
    ID3DBlob* errors = NULL;
    if (!function) return false;
    HRESULT result = function(kShader, sizeof(kShader) - 1, "pochd", NULL, NULL, entry, profile, 0, 0, blob, &errors);
    if (FAILED(result) && errors) pochd_log("shader %s: %s", entry, (const char*)errors->GetBufferPointer());
    release(errors);
    return SUCCEEDED(result);
}

bool create_device()
{
    if (g_device) return true;
    if (g_device_failed) return false;
    g_device_failed = true;
    HMODULE d3d11 = LoadLibraryW(L"d3d11.dll");
    HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
    if (!d3d11 || !compiler) { pochd_log("Direct3D 11 or its shader compiler is not available"); return false; }
    typedef HRESULT (WINAPI* CREATE)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT,
        ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
    CREATE create = (CREATE)GetProcAddress(d3d11, "D3D11CreateDevice");
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
    D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_10_0;
    if (!create) return false;
    HRESULT result = create(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, 0, levels, 3, D3D11_SDK_VERSION, &g_device, &level, &g_context);
    if (FAILED(result))
        result = create(NULL, D3D_DRIVER_TYPE_WARP, NULL, 0, levels, 3, D3D11_SDK_VERSION, &g_device, &level, &g_context);
    if (FAILED(result)) { pochd_log("Direct3D 11 device creation failed (%08lX)", result); return false; }

    ID3DBlob* vs = NULL; ID3DBlob* base = NULL; ID3DBlob* card = NULL;
    bool ok = compile(compiler, "vs", "vs_4_0", &vs) && compile(compiler, "base", "ps_4_0", &base)
        && compile(compiler, "card", "ps_4_0", &card);
    const D3D11_INPUT_ELEMENT_DESC elements[] = {
        { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 1, DXGI_FORMAT_R32G32_FLOAT, 0, 16, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 2, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 3, DXGI_FORMAT_R32G32_FLOAT, 0, 32, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 4, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 40, D3D11_INPUT_PER_VERTEX_DATA, 0 } };
    ok = ok && SUCCEEDED(g_device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), NULL, &g_vs))
        && SUCCEEDED(g_device->CreatePixelShader(base->GetBufferPointer(), base->GetBufferSize(), NULL, &g_ps_base))
        && SUCCEEDED(g_device->CreatePixelShader(card->GetBufferPointer(), card->GetBufferSize(), NULL, &g_ps_card))
        && SUCCEEDED(g_device->CreateInputLayout(elements, 6, vs->GetBufferPointer(), vs->GetBufferSize(), &g_layout));
    release(vs); release(base); release(card);

    D3D11_BUFFER_DESC buffer = {};
    buffer.ByteWidth = sizeof(Vertex) * 6 * MAX_QUADS;
    buffer.Usage = D3D11_USAGE_DYNAMIC; buffer.BindFlags = D3D11_BIND_VERTEX_BUFFER; buffer.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    ok = ok && SUCCEEDED(g_device->CreateBuffer(&buffer, NULL, &g_vertices));
    buffer.ByteWidth = 16; buffer.Usage = D3D11_USAGE_DEFAULT; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER; buffer.CPUAccessFlags = 0;
    ok = ok && SUCCEEDED(g_device->CreateBuffer(&buffer, NULL, &g_constants));

    D3D11_SAMPLER_DESC sampler = {};
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX; sampler.MaxAnisotropy = 1; sampler.ComparisonFunc = D3D11_COMPARISON_NEVER;
    sampler.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
    ok = ok && SUCCEEDED(g_device->CreateSamplerState(&sampler, &g_samplers[0]));
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    ok = ok && SUCCEEDED(g_device->CreateSamplerState(&sampler, &g_samplers[1]));
    sampler.Filter = D3D11_FILTER_ANISOTROPIC; sampler.MaxAnisotropy = 4;
    ok = ok && SUCCEEDED(g_device->CreateSamplerState(&sampler, &g_samplers[2]));

    D3D11_RASTERIZER_DESC raster = {};
    raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
    ok = ok && SUCCEEDED(g_device->CreateRasterizerState(&raster, &g_raster));

    D3D11_TEXTURE2D_DESC texture = {};
    texture.Width = POCHD_FRAME_W; texture.Height = POCHD_FRAME_H; texture.MipLevels = 1; texture.ArraySize = 1;
    texture.Format = DXGI_FORMAT_B8G8R8A8_UNORM; texture.SampleDesc.Count = 1; texture.Usage = D3D11_USAGE_DYNAMIC;
    texture.BindFlags = D3D11_BIND_SHADER_RESOURCE; texture.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    ok = ok && SUCCEEDED(g_device->CreateTexture2D(&texture, NULL, &g_base_texture))
        && SUCCEEDED(g_device->CreateShaderResourceView(g_base_texture, NULL, &g_base_view));
    ok = ok && SUCCEEDED(g_device->CreateTexture2D(&texture, NULL, &g_mix_texture))
        && SUCCEEDED(g_device->CreateShaderResourceView(g_mix_texture, NULL, &g_mix_view));
    texture.Format = DXGI_FORMAT_R8_UNORM;
    ok = ok && SUCCEEDED(g_device->CreateTexture2D(&texture, NULL, &g_owner_texture))
        && SUCCEEDED(g_device->CreateShaderResourceView(g_owner_texture, NULL, &g_owner_view));
    texture.Width = 256; texture.Height = 256; texture.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    ok = ok && SUCCEEDED(g_device->CreateTexture2D(&texture, NULL, &g_recolour_texture))
        && SUCCEEDED(g_device->CreateShaderResourceView(g_recolour_texture, NULL, &g_recolour_view));
    if (!ok) { pochd_log("Direct3D 11 resources could not be created"); return false; }

    for (unsigned v = 0; v < 65536; v++)
    {
        unsigned r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        g_palette[v] = 0xFF000000u | (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2));
    }
    g_device_failed = false;
    pochd_log("Direct3D 11 presenter ready (feature level %X)", level);
    return true;
}

bool create_swap_chain(HWND window, int width, int height)
{
    if (g_target && g_swap_window == window && g_swap_w == width && g_swap_h == height) return true;
    // ResizeBuffers requires releasing the context's reference to the old back buffer as well.
    if (g_target) g_context->OMSetRenderTargets(0, NULL, NULL);
    release(g_target);
    if (g_swap && g_swap_window == window)
    {
        if (FAILED(g_swap->ResizeBuffers(1, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, 0))) release(g_swap);
    }
    else release(g_swap);
    if (!g_swap)
    {
        IDXGIDevice* device = NULL; IDXGIAdapter* adapter = NULL; IDXGIFactory* factory = NULL;
        DXGI_SWAP_CHAIN_DESC desc = {};
        desc.BufferCount = 1; desc.BufferDesc.Width = width; desc.BufferDesc.Height = height;
        desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.OutputWindow = window; desc.SampleDesc.Count = 1; desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        if (SUCCEEDED(g_device->QueryInterface(__uuidof(IDXGIDevice), (void**)&device)) && SUCCEEDED(device->GetAdapter(&adapter))
            && SUCCEEDED(adapter->GetParent(__uuidof(IDXGIFactory), (void**)&factory)))
        {
            if (SUCCEEDED(factory->CreateSwapChain(g_device, &desc, &g_swap)))
                factory->MakeWindowAssociation(window, DXGI_MWA_NO_WINDOW_CHANGES | DXGI_MWA_NO_ALT_ENTER);
        }
        release(factory); release(adapter); release(device);
        if (!g_swap) { pochd_log("swap chain creation failed"); return false; }
    }
    ID3D11Texture2D* back = NULL;
    if (FAILED(g_swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back))) return false;
    HRESULT result = g_device->CreateRenderTargetView(back, NULL, &g_target);
    back->Release();
    g_swap_window = window; g_swap_w = width; g_swap_h = height;
    return SUCCEEDED(result);
}

void upload_frame()
{
    D3D11_MAPPED_SUBRESOURCE mapped;
    for (int i = 0; i < POCHD_FRAME_W * POCHD_FRAME_H; i++) g_converted[i] = g_palette[g_frame.pixels[i]];
    if (SUCCEEDED(g_context->Map(g_base_texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
    {
        for (int y = 0; y < POCHD_FRAME_H; y++)
            memcpy((BYTE*)mapped.pData + y * mapped.RowPitch, g_converted + y * POCHD_FRAME_W, POCHD_FRAME_W * 4);
        g_context->Unmap(g_base_texture, 0);
    }
    if (SUCCEEDED(g_context->Map(g_owner_texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
    {
        for (int y = 0; y < POCHD_FRAME_H; y++)
            memcpy((BYTE*)mapped.pData + y * mapped.RowPitch, g_frame.owner + y * POCHD_FRAME_W, POCHD_FRAME_W);
        g_context->Unmap(g_owner_texture, 0);
    }
    /* The mix plane is only needed while a card is fading, dimmed, glowing or has an effect over it. */
    if (g_frame.has_mix && SUCCEEDED(g_context->Map(g_mix_texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
    {
        for (int y = 0; y < POCHD_FRAME_H; y++)
            memcpy((BYTE*)mapped.pData + y * mapped.RowPitch, g_frame.mix + y * POCHD_FRAME_W, POCHD_FRAME_W * 4);
        g_context->Unmap(g_mix_texture, 0);
    }
}

/* Expands a 5- or 6-bit channel table to the 0..255 input range the HD picture uses. */
void expand_table(const BYTE* table, int size, int shift, BYTE* out)
{
    for (int v = 0; v < 256; v++)
    {
        float position = v * (size - 1) / 255.0f;
        int low = (int)position, high = low + 1 < size ? low + 1 : low;
        float a = (float)((table[low] << shift) | (table[low] >> (8 - 2 * shift)));
        float b = (float)((table[high] << shift) | (table[high] >> (8 - 2 * shift)));
        out[v * 4] = (BYTE)(a + (b - a) * (position - low) + 0.5f);
    }
}

void upload_recolour()
{
    static DWORD rows[256 * 256];
    D3D11_MAPPED_SUBRESOURCE mapped;
    bool any = false;
    for (int i = 0; i < g_frame.layer_count; i++)
    {
        const POCHD_LAYER& layer = g_frame.layers[i];
        if (!layer.transfer) continue;
        BYTE* row = (BYTE*)(rows + (i + 1) * 256);
        expand_table(layer.blue, 32, 3, row + 0);
        expand_table(layer.green, 64, 2, row + 1);
        expand_table(layer.red, 32, 3, row + 2);
        any = true;
    }
    if (any && SUCCEEDED(g_context->Map(g_recolour_texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
    {
        for (int y = 0; y < 256; y++) memcpy((BYTE*)mapped.pData + y * mapped.RowPitch, rows + y * 256, 256 * 4);
        g_context->Unmap(g_recolour_texture, 0);
    }
}

void quad(Vertex* out, const float* dst, const float at[4][2], const float uv[4][2], float id, float recolour, float gain,
    float mixed)
{
    /* corners: 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right */
    Vertex corner[4];
    for (int c = 0; c < 4; c++)
    {
        float x = at[c][0], y = at[c][1];
        corner[c].x = x / POCHD_FRAME_W * 2.0f - 1.0f;
        corner[c].y = 1.0f - y / POCHD_FRAME_H * 2.0f;
        corner[c].u = uv[c][0]; corner[c].v = uv[c][1];
        corner[c].fu = x / POCHD_FRAME_W; corner[c].fv = y / POCHD_FRAME_H;
        corner[c].id = id;
        corner[c].recolour = recolour;
        corner[c].gain = gain;
        corner[c].mixed = mixed;
        /* centres of the outermost frame pixels inside the quad */
        corner[c].left = (dst[0] + 0.5f) / POCHD_FRAME_W; corner[c].top = (dst[1] + 0.5f) / POCHD_FRAME_H;
        corner[c].right = (dst[2] - 0.5f) / POCHD_FRAME_W; corner[c].bottom = (dst[3] - 0.5f) / POCHD_FRAME_H;
    }
    out[0] = corner[0]; out[1] = corner[1]; out[2] = corner[2];
    out[3] = corner[2]; out[4] = corner[1]; out[5] = corner[3];
}

void capture()
{
    ID3D11Texture2D* back = NULL; ID3D11Texture2D* staging = NULL;
    D3D11_TEXTURE2D_DESC desc;
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (FAILED(g_swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back))) return;
    back->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
    if (SUCCEEDED(g_device->CreateTexture2D(&desc, NULL, &staging)))
    {
        g_context->CopyResource(staging, back);
        if (SUCCEEDED(g_context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped)))
        {
            int row = (desc.Width * 3 + 3) & ~3;
            BITMAPFILEHEADER header = { 0x4D42, (DWORD)(54 + row * desc.Height), 0, 0, 54 };
            BITMAPINFOHEADER info = { 40, (LONG)desc.Width, (LONG)desc.Height, 1, 24, 0, (DWORD)(row * desc.Height), 0, 0, 0, 0 };
            BYTE* line = (BYTE*)calloc(1, row);
            HANDLE file = CreateFileW(g_capture_path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            DWORD written;
            if (file != INVALID_HANDLE_VALUE && line)
            {
                WriteFile(file, &header, sizeof(header), &written, NULL);
                WriteFile(file, &info, sizeof(info), &written, NULL);
                for (int y = (int)desc.Height - 1; y >= 0; y--)
                {
                    const BYTE* source = (const BYTE*)mapped.pData + y * mapped.RowPitch;
                    for (unsigned x = 0; x < desc.Width; x++) memcpy(line + x * 3, source + x * 4, 3);
                    WriteFile(file, line, row, &written, NULL);
                }
            }
            if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
            free(line);
            g_context->Unmap(staging, 0);
        }
        staging->Release();
    }
    back->Release();
}

void draw(const int* host, bool fresh)
{
    static Vertex vertices[6 * MAX_QUADS];
    static ID3D11ShaderResourceView* views[POCHD_MAX_LAYERS];
    static int first_quad[POCHD_MAX_LAYERS], quad_count[POCHD_MAX_LAYERS];
    int batches = 0;
    const float black[4] = { 0, 0, 0, 1 };
    const float full[4] = { 0, 0, POCHD_FRAME_W, POCHD_FRAME_H };
    const float full_uv[4][2] = { { 0, 0 }, { 1, 0 }, { 0, 1 }, { 1, 1 } };
    int uploads = 3, quads = 1;
    g_tick++;
    const float full_at[4][2] = { { 0, 0 }, { POCHD_FRAME_W, 0 }, { 0, POCHD_FRAME_H }, { POCHD_FRAME_W, POCHD_FRAME_H } };
    quad(vertices, full, full_at, full_uv, 0, 0, 1, 0);
    for (int i = 0; i < g_frame.layer_count; i++)
    {
        const POCHD_LAYER& layer = g_frame.layers[i];
        ID3D11ShaderResourceView* view = picture_view(layer.card, uploads);
        if (!view) continue;
        /* A whole card is one quad. A card drawn in pieces is one quad a piece, in the order the game drew
           them, all under the layer's number: where pieces overlap, the later one is on top as in the game. */
        int pieces = layer.piece_count > 0 ? layer.piece_count : 1;
        if (layer.piece_count > 0 && (layer.piece_first < 0 || layer.piece_first + layer.piece_count > g_frame.piece_count)) continue;
        if (quads + pieces > MAX_QUADS) continue;
        views[batches] = view;
        first_quad[batches] = quads;
        quad_count[batches] = pieces;
        batches++;
        for (int p = 0; p < pieces; p++)
        {
            float part[4], area[4], at[4][2], uv[4][2];
            if (layer.piece_count > 0)
            {
                const POCHD_PIECE& piece = g_frame.pieces[layer.piece_first + p];
                part[0] = piece.sx * layer.texel[0]; part[1] = piece.sy * layer.texel[1];
                part[2] = (piece.sx + piece.w) * layer.texel[0]; part[3] = (piece.sy + piece.h) * layer.texel[1];
                area[0] = (float)piece.x; area[1] = (float)piece.y;
                area[2] = (float)(piece.x + piece.w); area[3] = (float)(piece.y + piece.h);
                for (int c = 0; c < 4; c++)
                {
                    at[c][0] = (c & 1) ? area[2] : area[0];
                    at[c][1] = (c & 2) ? area[3] : area[1];
                }
            }
            else
            {
                memcpy(part, layer.uv, sizeof(part));
                memcpy(area, layer.dst, sizeof(area));
                memcpy(at, layer.corner, sizeof(at));
            }
            for (int c = 0; c < 4; c++)
            {
                float s = (c & 1) ? part[2] : part[0], t = (c & 2) ? part[3] : part[1];
                switch (layer.orientation)
                {
                case POCHD_ORIENT_0: uv[c][0] = s; uv[c][1] = t; break;
                case POCHD_ORIENT_180: uv[c][0] = 1 - s; uv[c][1] = 1 - t; break;
                case POCHD_ORIENT_90: uv[c][0] = t; uv[c][1] = 1 - s; break;
                default: uv[c][0] = 1 - t; uv[c][1] = s; break;
                }
            }
            quad(vertices + quads * 6, area, at, uv, (float)(i + 1), layer.transfer ? 1.0f : 0.0f, layer.gain,
                g_frame.has_mix && layer.mixed ? 1.0f : 0.0f);
            quads++;
        }
    }
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (FAILED(g_context->Map(g_vertices, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return;
    memcpy(mapped.pData, vertices, sizeof(Vertex) * 6 * quads);
    g_context->Unmap(g_vertices, 0);

    D3D11_VIEWPORT viewport = { (float)host[2], (float)host[3], (float)host[4], (float)host[5], 0, 1 };
    float constants[4] = { (float)g_pochd.filter, (float)g_pochd.tint, host[4] / (float)POCHD_FRAME_W, host[5] / (float)POCHD_FRAME_H };
    UINT stride = sizeof(Vertex), offset = 0;
    g_context->UpdateSubresource(g_constants, 0, NULL, constants, 0, 0);
    g_context->OMSetRenderTargets(1, &g_target, NULL);
    g_context->ClearRenderTargetView(g_target, black);
    g_context->RSSetViewports(1, &viewport);
    g_context->RSSetState(g_raster);
    g_context->IASetInputLayout(g_layout);
    g_context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    g_context->IASetVertexBuffers(0, 1, &g_vertices, &stride, &offset);
    g_context->VSSetShader(g_vs, NULL, 0);
    g_context->PSSetConstantBuffers(0, 1, &g_constants);
    g_context->PSSetSamplers(0, 3, g_samplers);
    g_context->PSSetShader(g_ps_base, NULL, 0);
    g_context->PSSetShaderResources(0, 1, &g_base_view);
    g_context->Draw(6, 0);
    if (batches > 0)
    {
        g_context->PSSetShader(g_ps_card, NULL, 0);
        g_context->PSSetShaderResources(1, 1, &g_owner_view);
        g_context->PSSetShaderResources(2, 1, &g_recolour_view);
        g_context->PSSetShaderResources(3, 1, &g_mix_view);
        for (int b = 0; b < batches; b++)
        {
            g_context->PSSetShaderResources(0, 1, &views[b]);
            g_context->Draw(6 * quad_count[b], first_quad[b] * 6);
        }
    }
    if (InterlockedExchange(&g_capture_pending, 0)) capture();
    if (g_burst_left > 0 && fresh)
    {
        _snwprintf(g_capture_path, MAX_PATH - 1, L"%s%02d.bmp", g_burst_prefix, g_burst_index++);
        capture();
        InterlockedDecrement(&g_burst_left);
    }
    HRESULT presented = g_swap->Present(1, 0);
    if (presented == DXGI_ERROR_DEVICE_REMOVED || presented == DXGI_ERROR_DEVICE_RESET) g_device_lost = true;
}

void timed_draw(const int* host, bool fresh)
{
    static LONGLONG spent;
    static int drawn;
    LARGE_INTEGER started, finished, frequency;
    QueryPerformanceCounter(&started);
    draw(host, fresh);
    QueryPerformanceCounter(&finished);
    spent += finished.QuadPart - started.QuadPart;
    if (++drawn == 600)
    {
        QueryPerformanceFrequency(&frequency);
        POCHD_DEBUG("presenter: %.3f ms per frame including the wait for the display, %u MB of HD pictures",
            spent * 1000.0 / frequency.QuadPart / drawn, (unsigned)(g_picture_bytes >> 20));
        spent = 0;
        drawn = 0;
    }
}

} /* namespace */

/* After a driver restart or adapter change every Direct3D object is gone; start again from nothing. */
static void destroy_device()
{
    for (int i = 0; i < g_picture_count; i++)
    {
        Picture& picture = g_pictures[i];
        if (picture.state == kReady)
        {
            release(picture.view);
            InterlockedExchange(&picture.state, kNone);
        }
    }
    g_picture_bytes = 0;
    release(g_target); release(g_swap); release(g_base_view); release(g_base_texture); release(g_owner_view);
    release(g_owner_texture); release(g_recolour_view); release(g_recolour_texture); release(g_mix_view);
    release(g_mix_texture); release(g_raster);
    for (int i = 0; i < 3; i++) release(g_samplers[i]);
    release(g_constants); release(g_vertices); release(g_layout); release(g_ps_card); release(g_ps_base); release(g_vs);
    release(g_context); release(g_device);
    g_swap_window = NULL;
    g_device_lost = false;
    pochd_log("the graphics device was lost; starting the presenter again");
}

void pochd_render_request_burst(const wchar_t* prefix, int count)
{
    wcsncpy(g_burst_prefix, prefix, MAX_PATH - 8);
    g_burst_index = 0;
    InterlockedExchange(&g_burst_left, count > 99 ? 99 : count);
}

void pochd_render_request_capture(const wchar_t* path)
{
    wcsncpy(g_capture_path, path, MAX_PATH - 1);
    InterlockedExchange(&g_capture_pending, 1);
}

DWORD pochd_render_run(void)
{
    HANDLE semaphore = pochd_host_semaphore();
    DWORD last_sealed = GetTickCount();
    int host[6];
    Sleep(100);
    if (!create_device()) return pochd_host_fallback();
    while (pochd_host_running())
    {
        DWORD wait = WaitForSingleObject(semaphore, 100);
        if (wait == WAIT_FAILED || !pochd_host_running()) break;
        pochd_host_viewport(host);
        if (host[0] <= 0 || host[1] <= 0 || host[4] <= 0 || host[5] <= 0) continue;
        if (!create_swap_chain(pochd_host_window(), host[0], host[1])) return pochd_host_fallback();
        bool fresh = false;
        if (pochd_track_acquire(&g_frame, &g_serial))
        {
            fresh = true;
            last_sealed = GetTickCount();
            upload_frame();
            upload_recolour();
        }
        else if (GetTickCount() - last_sealed > 300 && pochd_host_primary(g_frame.pixels))
        {
            /* The game is showing a picture this code did not see being composed: show it as it is. */
            g_frame.layer_count = 0;
            memset(g_frame.owner, 0, sizeof(g_frame.owner));
            upload_frame();
        }
        else if (wait == WAIT_TIMEOUT && g_serial == 0) continue;
        timed_draw(host, fresh);
        if (g_device_lost)
        {
            destroy_device();
            if (!create_device()) return pochd_host_fallback();
        }
    }
    return TRUE;
}
