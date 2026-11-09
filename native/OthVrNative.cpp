// OthVrNative.dll: the OTH VR mod's OpenXR side.
//
// The C# mod renders both eyes with Unity cameras into render textures; this helper hands them to the
// headset through OpenXR (any runtime: Virtual Desktop, SteamVR, Meta, ...). Threads:
//   main thread   OxrInit / OxrPoll / OxrWaitFrame / OxrSet* / OxrShutdown  (from the mod's Update)
//   render thread OnRenderEvent, queued by GL.IssuePluginEvent after Unity rendered the frame:
//                 xrBeginFrame, copy the textures into the swapchains, xrEndFrame.
// One lock keeps the two apart, except xrWaitFrame, which blocks until the render thread has begun the
// previous frame and therefore must not hold the lock.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_PLATFORM_WIN32
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

#include "blit_vs.h"
#include "blit_ps.h"

#define EXPORT extern "C" __declspec(dllexport)

// ---------------------------------------------------------------- log (read by the mod each frame)

static std::mutex g_logLock;
static std::deque<std::string> g_log;

static void Log(const char* fmt, ...)
{
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_log.size() < 200) g_log.push_back(buf);
}

// Copies the oldest waiting log line into buf. Returns its length, 0 when there is none.
EXPORT int OxrTakeLog(char* buf, int size)
{
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_log.empty() || size <= 0) return 0;
    std::string s = g_log.front();
    g_log.pop_front();
    int n = (int)std::min<size_t>(s.size(), (size_t)size - 1);
    memcpy(buf, s.data(), n);
    buf[n] = 0;
    return n;
}

// ---------------------------------------------------------------- state

struct Swapchain
{
    XrSwapchain handle = XR_NULL_HANDLE;
    int width = 0, height = 0;
    std::vector<ID3D11RenderTargetView*> rtvs;
    ID3D11Texture2D* simTexture = nullptr;   // simulate mode: our own texture instead of the runtime's
};

// What the mod decided for one frame (written by OxrWaitFrame, used by OnRenderEvent).
struct FrameSlot
{
    bool waited = false;
    XrTime displayTime = 0;
    bool shouldRender = false;
    bool located = false;
    int mode = 0;                       // 0 = 3D (both eyes), 1 = flat screen floating in front of you
    XrView views[2] = {{XR_TYPE_VIEW}, {XR_TYPE_VIEW}};
    XrPosef quadPose{};
    float quadWidth = 0, quadHeight = 0;
    bool flipEyes = true, flipScreen = true;
};

static std::mutex g_lock;
static XrInstance g_instance = XR_NULL_HANDLE;
static XrSystemId g_system = XR_NULL_SYSTEM_ID;
static XrSession g_session = XR_NULL_HANDLE;
static XrSpace g_space = XR_NULL_HANDLE;
static XrSessionState g_state = XR_SESSION_STATE_UNKNOWN;
static bool g_running = false;          // between xrBeginSession and xrEndSession
static bool g_exit = false;             // the runtime wants us gone (exiting / loss pending / instance lost)
static XrEnvironmentBlendMode g_blend = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
static int64_t g_format = 0;
static Swapchain g_eye[2], g_quad;
static int g_eyeWidth = 0, g_eyeHeight = 0;
static FrameSlot g_slots[4];
static std::atomic<int> g_framesSubmitted{0};
static std::atomic<int> g_framesWaited{0};

static ID3D11Device* g_device = nullptr;
static ID3D11DeviceContext* g_context = nullptr;
static ID3D11VertexShader* g_vs = nullptr;
static ID3D11PixelShader* g_ps = nullptr;
static ID3D11SamplerState* g_sampler = nullptr;
static ID3D11Buffer* g_cbuffer = nullptr;
static ID3D11RasterizerState* g_raster = nullptr;
static ID3D11BlendState* g_blendState = nullptr;
static ID3D11DepthStencilState* g_depthState = nullptr;

// Unity's textures we read from, with the views made for them.
struct Source
{
    ID3D11Texture2D* tex = nullptr;
    ID3D11ShaderResourceView* srv = nullptr;
};
static Source g_eyeSrc[2], g_screenSrc;

static PFN_xrGetD3D11GraphicsRequirementsKHR g_getReq = nullptr;

// Simulate mode (testing without a headset): no OpenXR at all. A pretend headset at 90 Hz with a fixed
// head; the pictures that would go to the headset are saved as BMP files in g_simDir now and then.
static bool g_sim = false, g_simActive = false;
static std::wstring g_simDir;
static LARGE_INTEGER g_simNext{}, g_simFreq{};
static int g_simHz = 90;   // 0 = as fast as the game can go (for measuring)

static const char* ResultName(XrResult r)
{
    static char buf[XR_MAX_RESULT_STRING_SIZE];
    if (g_instance != XR_NULL_HANDLE && XR_SUCCEEDED(xrResultToString(g_instance, r, buf))) return buf;
    snprintf(buf, sizeof buf, "XrResult %d", (int)r);
    return buf;
}

#define XR_CHECK(call)                                                                  \
    do {                                                                                \
        XrResult r_ = (call);                                                           \
        if (XR_FAILED(r_)) { Log("%s failed: %s", #call, ResultName(r_)); return r_; }   \
    } while (0)

template <class T> static void Release(T*& p)
{
    if (p) { p->Release(); p = nullptr; }
}

// ---------------------------------------------------------------- copy (render thread)

static bool CreateBlitResources()
{
    if (g_vs) return true;
    if (FAILED(g_device->CreateVertexShader(g_blitVS, sizeof g_blitVS, nullptr, &g_vs))) { Log("vertex shader failed"); return false; }
    if (FAILED(g_device->CreatePixelShader(g_blitPS, sizeof g_blitPS, nullptr, &g_ps))) { Log("pixel shader failed"); return false; }
    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    g_device->CreateSamplerState(&sd, &g_sampler);
    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = 16;
    bd.Usage = D3D11_USAGE_DEFAULT;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    g_device->CreateBuffer(&bd, nullptr, &g_cbuffer);
    D3D11_RASTERIZER_DESC rd = {};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    g_device->CreateRasterizerState(&rd, &g_raster);
    D3D11_BLEND_DESC bld = {};
    bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    g_device->CreateBlendState(&bld, &g_blendState);
    D3D11_DEPTH_STENCIL_DESC dd = {};
    dd.DepthEnable = FALSE;
    dd.StencilEnable = FALSE;
    g_device->CreateDepthStencilState(&dd, &g_depthState);
    return g_sampler && g_cbuffer && g_raster && g_blendState && g_depthState;
}

// Unity's own pipeline state, put back after our copy (what we touch, like Dear ImGui's DX11 backend).
struct SavedState
{
    UINT scissorCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE, viewportCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    D3D11_RECT scissors[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
    D3D11_VIEWPORT viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
    ID3D11RasterizerState* rs = nullptr;
    ID3D11BlendState* blend = nullptr;
    FLOAT blendFactor[4];
    UINT sampleMask = 0;
    UINT stencilRef = 0;
    ID3D11DepthStencilState* depth = nullptr;
    ID3D11ShaderResourceView* psSrv = nullptr;
    ID3D11SamplerState* psSampler = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11VertexShader* vs = nullptr;
    ID3D11GeometryShader* gs = nullptr;
    ID3D11HullShader* hs = nullptr;
    ID3D11DomainShader* ds = nullptr;
    ID3D11ClassInstance *psInst[256], *vsInst[256], *gsInst[256];
    UINT psInstCount = 256, vsInstCount = 256, gsInstCount = 256;
    ID3D11Buffer* psCb = nullptr;
    D3D11_PRIMITIVE_TOPOLOGY topology;
    ID3D11InputLayout* layout = nullptr;
    ID3D11RenderTargetView* rtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    ID3D11DepthStencilView* dsv = nullptr;

    void Save(ID3D11DeviceContext* c)
    {
        c->RSGetScissorRects(&scissorCount, scissors);
        c->RSGetViewports(&viewportCount, viewports);
        c->RSGetState(&rs);
        c->OMGetBlendState(&blend, blendFactor, &sampleMask);
        c->OMGetDepthStencilState(&depth, &stencilRef);
        c->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtvs, &dsv);
        c->PSGetShaderResources(0, 1, &psSrv);
        c->PSGetSamplers(0, 1, &psSampler);
        c->PSGetShader(&ps, psInst, &psInstCount);
        c->VSGetShader(&vs, vsInst, &vsInstCount);
        c->GSGetShader(&gs, gsInst, &gsInstCount);
        c->HSGetShader(&hs, nullptr, nullptr);
        c->DSGetShader(&ds, nullptr, nullptr);
        c->PSGetConstantBuffers(0, 1, &psCb);
        c->IAGetPrimitiveTopology(&topology);
        c->IAGetInputLayout(&layout);
    }

    void Restore(ID3D11DeviceContext* c)
    {
        c->RSSetScissorRects(scissorCount, scissors);
        c->RSSetViewports(viewportCount, viewports);
        c->RSSetState(rs);
        c->OMSetBlendState(blend, blendFactor, sampleMask);
        c->OMSetDepthStencilState(depth, stencilRef);
        c->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtvs, dsv);
        c->PSSetShaderResources(0, 1, &psSrv);
        c->PSSetSamplers(0, 1, &psSampler);
        c->PSSetShader(ps, psInst, psInstCount);
        c->VSSetShader(vs, vsInst, vsInstCount);
        c->GSSetShader(gs, gsInst, gsInstCount);
        c->HSSetShader(hs, nullptr, 0);
        c->DSSetShader(ds, nullptr, 0);
        c->PSSetConstantBuffers(0, 1, &psCb);
        c->IASetPrimitiveTopology(topology);
        c->IASetInputLayout(layout);
        Release(rs); Release(blend); Release(depth); Release(dsv);
        for (auto& v : rtvs) Release(v);
        Release(psSrv); Release(psSampler); Release(ps); Release(vs); Release(gs); Release(hs); Release(ds); Release(psCb); Release(layout);
        for (UINT i = 0; i < psInstCount; i++) Release(psInst[i]);
        for (UINT i = 0; i < vsInstCount; i++) Release(vsInst[i]);
        for (UINT i = 0; i < gsInstCount; i++) Release(gsInst[i]);
    }
};

static void Draw(ID3D11ShaderResourceView* srv, ID3D11RenderTargetView* rtv, int w, int h, bool flip)
{
    float p[4] = {1, flip ? -1.f : 1.f, 0, flip ? 1.f : 0.f};
    g_context->UpdateSubresource(g_cbuffer, 0, nullptr, p, 0, 0);
    D3D11_VIEWPORT vp = {0, 0, (float)w, (float)h, 0, 1};
    g_context->RSSetViewports(1, &vp);
    g_context->OMSetRenderTargets(1, &rtv, nullptr);
    g_context->PSSetShaderResources(0, 1, &srv);
    g_context->Draw(3, 0);
    ID3D11ShaderResourceView* none = nullptr;
    g_context->PSSetShaderResources(0, 1, &none);
}

// Acquire an image, copy srv into it, release it. False if the runtime didn't give us an image.
static bool CopyInto(Swapchain& sc, ID3D11ShaderResourceView* srv, bool flip)
{
    if (g_sim)
    {
        if (srv && !sc.rtvs.empty()) Draw(srv, sc.rtvs[0], sc.width, sc.height, flip);
        return srv != nullptr;
    }
    XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    uint32_t index = 0;
    XrResult r = xrAcquireSwapchainImage(sc.handle, &ai, &index);
    if (XR_FAILED(r)) { Log("xrAcquireSwapchainImage: %s", ResultName(r)); return false; }
    XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wi.timeout = 100000000;   // 100 ms
    r = xrWaitSwapchainImage(sc.handle, &wi);
    if (r == XR_SUCCESS && srv && index < sc.rtvs.size()) Draw(srv, sc.rtvs[index], sc.width, sc.height, flip);
    else if (r != XR_SUCCESS) Log("xrWaitSwapchainImage: %s", ResultName(r));
    XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    xrReleaseSwapchainImage(sc.handle, &ri);
    return r == XR_SUCCESS;
}

// Simulate mode: one swapchain picture as a BMP (the bytes are what the headset would show).
static void SaveBmp(Swapchain& sc, const wchar_t* name)
{
    if (!sc.simTexture) return;
    D3D11_TEXTURE2D_DESC td;
    sc.simTexture->GetDesc(&td);
    td.Usage = D3D11_USAGE_STAGING;
    td.BindFlags = 0;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* staging = nullptr;
    if (FAILED(g_device->CreateTexture2D(&td, nullptr, &staging))) return;
    g_context->CopyResource(staging, sc.simTexture);
    D3D11_MAPPED_SUBRESOURCE m;
    if (SUCCEEDED(g_context->Map(staging, 0, D3D11_MAP_READ, 0, &m)))
    {
        std::wstring path = g_simDir + L"\\" + name;
        FILE* f = _wfopen(path.c_str(), L"wb");
        if (f)
        {
            int w = (int)td.Width, h = (int)td.Height, row = (w * 3 + 3) & ~3;
            BITMAPFILEHEADER fh = {0x4D42, (DWORD)(sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER) + row * h), 0, 0,
                                   sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER)};
            BITMAPINFOHEADER ih = {sizeof(BITMAPINFOHEADER), w, -h, 1, 24, BI_RGB};   // -h: first row = top
            fwrite(&fh, sizeof fh, 1, f);
            fwrite(&ih, sizeof ih, 1, f);
            std::vector<unsigned char> line(row, 0);
            for (int y = 0; y < h; y++)
            {
                auto* src = (const unsigned char*)m.pData + (size_t)y * m.RowPitch;
                for (int x = 0; x < w; x++)
                {
                    line[x * 3 + 0] = src[x * 4 + 2];
                    line[x * 3 + 1] = src[x * 4 + 1];
                    line[x * 3 + 2] = src[x * 4 + 0];
                }
                fwrite(line.data(), 1, row, f);
            }
            fclose(f);
            Log("saved %ls (%dx%d)", path.c_str(), w, h);
        }
        g_context->Unmap(staging, 0);
    }
    staging->Release();
}

static void SubmitFrame(FrameSlot& f)
{
    XrResult r = XR_SUCCESS;
    if (!g_sim)
    {
        XrFrameBeginInfo bi{XR_TYPE_FRAME_BEGIN_INFO};
        r = xrBeginFrame(g_session, &bi);
        if (XR_FAILED(r)) { Log("xrBeginFrame: %s", ResultName(r)); return; }
    }

    XrCompositionLayerProjectionView pv[2] = {{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}};
    XrCompositionLayerProjection proj{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    XrCompositionLayerQuad quad{XR_TYPE_COMPOSITION_LAYER_QUAD};
    const XrCompositionLayerBaseHeader* layers[1];
    uint32_t layerCount = 0;

    if (f.shouldRender)
    {
        SavedState saved;
        saved.Save(g_context);
        g_context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        g_context->IASetInputLayout(nullptr);
        g_context->VSSetShader(g_vs, nullptr, 0);
        g_context->PSSetShader(g_ps, nullptr, 0);
        g_context->GSSetShader(nullptr, nullptr, 0);
        g_context->HSSetShader(nullptr, nullptr, 0);
        g_context->DSSetShader(nullptr, nullptr, 0);
        g_context->PSSetSamplers(0, 1, &g_sampler);
        g_context->PSSetConstantBuffers(0, 1, &g_cbuffer);
        g_context->RSSetState(g_raster);
        g_context->OMSetBlendState(g_blendState, nullptr, 0xffffffff);
        g_context->OMSetDepthStencilState(g_depthState, 0);

        if (f.mode == 0 && f.located && g_eyeSrc[0].srv && g_eyeSrc[1].srv)
        {
            bool ok = true;
            for (int e = 0; e < 2; e++)
            {
                ok &= CopyInto(g_eye[e], g_eyeSrc[e].srv, f.flipEyes);
                pv[e].pose = f.views[e].pose;
                pv[e].fov = f.views[e].fov;
                pv[e].subImage.swapchain = g_eye[e].handle;
                pv[e].subImage.imageRect = {{0, 0}, {g_eye[e].width, g_eye[e].height}};
            }
            if (ok)
            {
                proj.space = g_space;
                proj.viewCount = 2;
                proj.views = pv;
                layers[layerCount++] = (XrCompositionLayerBaseHeader*)&proj;
            }
        }
        else if (f.mode == 1 && (g_quad.handle != XR_NULL_HANDLE || g_quad.simTexture) && g_screenSrc.srv)
        {
            if (CopyInto(g_quad, g_screenSrc.srv, f.flipScreen))
            {
                quad.space = g_space;
                quad.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
                quad.subImage.swapchain = g_quad.handle;
                quad.subImage.imageRect = {{0, 0}, {g_quad.width, g_quad.height}};
                quad.pose = f.quadPose;
                quad.size = {f.quadWidth, f.quadHeight};
                layers[layerCount++] = (XrCompositionLayerBaseHeader*)&quad;
            }
        }
        if (g_sim)
        {
            int n = g_framesSubmitted.load();
            if (n == 150 || n == 900 || n == 2700)
            {
                wchar_t name[64];
                if (f.mode == 0)
                {
                    swprintf_s(name, L"sim_%d_left.bmp", n); SaveBmp(g_eye[0], name);
                    swprintf_s(name, L"sim_%d_right.bmp", n); SaveBmp(g_eye[1], name);
                }
                else { swprintf_s(name, L"sim_%d_screen.bmp", n); SaveBmp(g_quad, name); }
            }
        }
        saved.Restore(g_context);
    }

    if (g_sim) { g_framesSubmitted++; return; }
    XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
    ei.displayTime = f.displayTime;
    ei.environmentBlendMode = g_blend;
    ei.layerCount = layerCount;
    ei.layers = layerCount ? layers : nullptr;
    r = xrEndFrame(g_session, &ei);
    if (XR_FAILED(r)) Log("xrEndFrame: %s", ResultName(r));
    else g_framesSubmitted++;
}

static void __stdcall OnRenderEvent(int eventId)
{
    std::lock_guard<std::mutex> l(g_lock);
    FrameSlot& f = g_slots[eventId & 3];
    if (!f.waited) return;
    f.waited = false;
    if (!g_context || (g_sim ? !g_simActive : (g_session == XR_NULL_HANDLE || !g_running))) return;
    SubmitFrame(f);
}

EXPORT void* OxrRenderEventFunc() { return (void*)&OnRenderEvent; }

// ---------------------------------------------------------------- setup (main thread)

static int64_t PickFormat()
{
    uint32_t n = 0;
    xrEnumerateSwapchainFormats(g_session, 0, &n, nullptr);
    std::vector<int64_t> formats(n);
    xrEnumerateSwapchainFormats(g_session, n, &n, formats.data());
    for (int64_t want : {(int64_t)DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, (int64_t)DXGI_FORMAT_B8G8R8A8_UNORM_SRGB})
        for (int64_t f : formats)
            if (f == want) return f;
    return formats.empty() ? 0 : formats[0];
}

static XrResult MakeSimSwapchain(Swapchain& sc, int w, int h)
{
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(g_device->CreateTexture2D(&td, nullptr, &sc.simTexture))) return XR_ERROR_RUNTIME_FAILURE;
    D3D11_RENDER_TARGET_VIEW_DESC rd = {};
    rd.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    ID3D11RenderTargetView* rtv = nullptr;
    if (FAILED(g_device->CreateRenderTargetView(sc.simTexture, &rd, &rtv))) return XR_ERROR_RUNTIME_FAILURE;
    sc.rtvs.push_back(rtv);
    sc.width = w;
    sc.height = h;
    return XR_SUCCESS;
}

static XrResult MakeSwapchain(Swapchain& sc, int w, int h)
{
    if (g_sim) return MakeSimSwapchain(sc, w, h);
    XrSwapchainCreateInfo ci{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    ci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
    ci.format = g_format;
    ci.sampleCount = 1;
    ci.width = w;
    ci.height = h;
    ci.faceCount = 1;
    ci.arraySize = 1;
    ci.mipCount = 1;
    XR_CHECK(xrCreateSwapchain(g_session, &ci, &sc.handle));
    sc.width = w;
    sc.height = h;
    uint32_t n = 0;
    XR_CHECK(xrEnumerateSwapchainImages(sc.handle, 0, &n, nullptr));
    std::vector<XrSwapchainImageD3D11KHR> images(n, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
    XR_CHECK(xrEnumerateSwapchainImages(sc.handle, n, &n, (XrSwapchainImageBaseHeader*)images.data()));
    for (auto& img : images)
    {
        D3D11_RENDER_TARGET_VIEW_DESC rd = {};
        rd.Format = (DXGI_FORMAT)g_format;
        rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        ID3D11RenderTargetView* rtv = nullptr;
        if (FAILED(g_device->CreateRenderTargetView(img.texture, &rd, &rtv))) Log("swapchain view failed (%dx%d)", w, h);
        sc.rtvs.push_back(rtv);
    }
    return XR_SUCCESS;
}

static void DestroySwapchain(Swapchain& sc)
{
    for (auto* v : sc.rtvs) if (v) v->Release();
    sc.rtvs.clear();
    Release(sc.simTexture);
    if (sc.handle != XR_NULL_HANDLE) xrDestroySwapchain(sc.handle);
    sc = Swapchain();
}

static void DropSource(Source& s)
{
    Release(s.srv);
    s.tex = nullptr;
}

static void DestroyAll()
{
    for (auto& s : g_slots) s = FrameSlot();
    DropSource(g_eyeSrc[0]); DropSource(g_eyeSrc[1]); DropSource(g_screenSrc);
    DestroySwapchain(g_eye[0]); DestroySwapchain(g_eye[1]); DestroySwapchain(g_quad);
    if (g_space != XR_NULL_HANDLE) xrDestroySpace(g_space);
    if (g_session != XR_NULL_HANDLE) xrDestroySession(g_session);
    if (g_instance != XR_NULL_HANDLE) xrDestroyInstance(g_instance);
    g_space = XR_NULL_HANDLE; g_session = XR_NULL_HANDLE; g_instance = XR_NULL_HANDLE; g_system = XR_NULL_SYSTEM_ID;
    g_state = XR_SESSION_STATE_UNKNOWN;
    g_running = false;
    g_exit = false;
    g_simActive = false;
    Release(g_vs); Release(g_ps); Release(g_sampler); Release(g_cbuffer); Release(g_raster); Release(g_blendState); Release(g_depthState);
    Release(g_context);
    Release(g_device);
}

static XrResult Init(void* unityTexture, float renderScale, char* info, int infoSize)
{
    if (!unityTexture) { Log("no texture to find Unity's Direct3D 11 device"); return XR_ERROR_VALIDATION_FAILURE; }
    ((ID3D11Resource*)unityTexture)->GetDevice(&g_device);
    if (!g_device) { Log("texture has no Direct3D 11 device (is the game running on DirectX 11?)"); return XR_ERROR_GRAPHICS_DEVICE_INVALID; }
    g_device->GetImmediateContext(&g_context);

    if (g_sim)
    {
        float s = std::clamp(renderScale, 0.3f, 2.0f);
        // like a Quest 3 through Virtual Desktop
        g_eyeWidth = (int)(2688 * s) & ~1;
        g_eyeHeight = (int)(2880 * s) & ~1;
        g_format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        for (int e = 0; e < 2; e++) { XrResult rr = MakeSwapchain(g_eye[e], g_eyeWidth, g_eyeHeight); if (XR_FAILED(rr)) return rr; }
        if (!CreateBlitResources()) return XR_ERROR_RUNTIME_FAILURE;
        QueryPerformanceFrequency(&g_simFreq);
        QueryPerformanceCounter(&g_simNext);
        g_simActive = true;
        snprintf(info, infoSize, "SIMULATED headset (no OpenXR), eye %dx%d, pictures saved to the mod folder", g_eyeWidth, g_eyeHeight);
        return XR_SUCCESS;
    }

    uint32_t extCount = 0;
    XR_CHECK(xrEnumerateInstanceExtensionProperties(nullptr, 0, &extCount, nullptr));
    std::vector<XrExtensionProperties> exts(extCount, {XR_TYPE_EXTENSION_PROPERTIES});
    XR_CHECK(xrEnumerateInstanceExtensionProperties(nullptr, extCount, &extCount, exts.data()));
    bool hasD3D11 = false;
    for (auto& e : exts) if (!strcmp(e.extensionName, XR_KHR_D3D11_ENABLE_EXTENSION_NAME)) hasD3D11 = true;
    if (!hasD3D11) { Log("the active OpenXR runtime has no Direct3D 11 support"); return XR_ERROR_EXTENSION_NOT_PRESENT; }

    const char* enabled[] = {XR_KHR_D3D11_ENABLE_EXTENSION_NAME};
    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ici.applicationInfo.applicationName, "over the hill (OTH VR)");
    ici.applicationInfo.applicationVersion = 1;
    strcpy_s(ici.applicationInfo.engineName, "Unity");
    ici.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ici.enabledExtensionCount = 1;
    ici.enabledExtensionNames = enabled;
    XR_CHECK(xrCreateInstance(&ici, &g_instance));

    XrInstanceProperties ip{XR_TYPE_INSTANCE_PROPERTIES};
    xrGetInstanceProperties(g_instance, &ip);

    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO};
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrResult r = xrGetSystem(g_instance, &sgi, &g_system);
    if (r == XR_ERROR_FORM_FACTOR_UNAVAILABLE) { Log("%s: no headset connected (put it on / connect Virtual Desktop or Link)", ip.runtimeName); return r; }
    XR_CHECK(r);
    XrSystemProperties sp{XR_TYPE_SYSTEM_PROPERTIES};
    xrGetSystemProperties(g_instance, g_system, &sp);

    XR_CHECK(xrGetInstanceProcAddr(g_instance, "xrGetD3D11GraphicsRequirementsKHR", (PFN_xrVoidFunction*)&g_getReq));
    XrGraphicsRequirementsD3D11KHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    XR_CHECK(g_getReq(g_instance, g_system, &req));
    IDXGIDevice* dxgi = nullptr;
    if (SUCCEEDED(g_device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi)))
    {
        IDXGIAdapter* adapter = nullptr;
        DXGI_ADAPTER_DESC ad = {};
        if (SUCCEEDED(dxgi->GetAdapter(&adapter))) { adapter->GetDesc(&ad); adapter->Release(); }
        dxgi->Release();
        if (memcmp(&ad.AdapterLuid, &req.adapterLuid, sizeof(LUID)) != 0)
            Log("warning: the headset wants another graphics card than the game uses");
    }

    XrGraphicsBindingD3D11KHR binding{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    binding.device = g_device;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &binding;
    sci.systemId = g_system;
    XR_CHECK(xrCreateSession(g_instance, &sci, &g_session));

    XrReferenceSpaceCreateInfo rci{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rci.poseInReferenceSpace.orientation.w = 1;
    XR_CHECK(xrCreateReferenceSpace(g_session, &rci, &g_space));

    uint32_t n = 0;
    XR_CHECK(xrEnumerateEnvironmentBlendModes(g_instance, g_system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &n, nullptr));
    std::vector<XrEnvironmentBlendMode> modes(n);
    XR_CHECK(xrEnumerateEnvironmentBlendModes(g_instance, g_system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, n, &n, modes.data()));
    if (!modes.empty()) g_blend = modes[0];

    XrViewConfigurationView vcv[2] = {{XR_TYPE_VIEW_CONFIGURATION_VIEW}, {XR_TYPE_VIEW_CONFIGURATION_VIEW}};
    n = 2;
    XR_CHECK(xrEnumerateViewConfigurationViews(g_instance, g_system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &n, vcv));
    float s = std::clamp(renderScale, 0.3f, 2.0f);
    g_eyeWidth = std::min((int)(vcv[0].recommendedImageRectWidth * s + 0.5f), (int)vcv[0].maxImageRectWidth);
    g_eyeHeight = std::min((int)(vcv[0].recommendedImageRectHeight * s + 0.5f), (int)vcv[0].maxImageRectHeight);
    g_eyeWidth &= ~1; g_eyeHeight &= ~1;

    g_format = PickFormat();
    for (int e = 0; e < 2; e++) { XrResult rr = MakeSwapchain(g_eye[e], g_eyeWidth, g_eyeHeight); if (XR_FAILED(rr)) return rr; }
    if (!CreateBlitResources()) return XR_ERROR_RUNTIME_FAILURE;

    snprintf(info, infoSize, "runtime %s %d.%d.%d, headset '%s', eye %dx%d (recommended %dx%d x %.2f), format %lld, blend %d",
             ip.runtimeName, XR_VERSION_MAJOR(ip.runtimeVersion), XR_VERSION_MINOR(ip.runtimeVersion), XR_VERSION_PATCH(ip.runtimeVersion),
             sp.systemName, g_eyeWidth, g_eyeHeight, vcv[0].recommendedImageRectWidth, vcv[0].recommendedImageRectHeight, s,
             (long long)g_format, (int)g_blend);
    return XR_SUCCESS;
}

// Starts OpenXR on the game's Direct3D 11 device (found through any Unity texture). info gets a line
// for the log. Returns 0 on success, otherwise the OpenXR error code (details in the log).
EXPORT int OxrInit(void* unityTexture, float renderScale, char* info, int infoSize)
{
    std::lock_guard<std::mutex> l(g_lock);
    if (info && infoSize > 0) info[0] = 0;
    if (g_instance != XR_NULL_HANDLE || g_simActive) return 0;
    XrResult r = Init(unityTexture, renderScale, info, infoSize);
    if (XR_FAILED(r)) DestroyAll();
    return (int)r;
}

EXPORT void OxrEyeSize(int* w, int* h) { *w = g_eyeWidth; *h = g_eyeHeight; }

// Before OxrInit: dir = folder for the saved pictures to test without a headset, null = real OpenXR.
EXPORT void OxrSimulateRate(int hz) { g_simHz = hz; }

EXPORT void OxrSimulate(const wchar_t* dir)
{
    std::lock_guard<std::mutex> l(g_lock);
    if (g_instance != XR_NULL_HANDLE || g_simActive) return;
    g_sim = dir != nullptr;
    g_simDir = dir ? dir : L"";
}

EXPORT void OxrShutdown()
{
    std::lock_guard<std::mutex> l(g_lock);
    if (g_session != XR_NULL_HANDLE && g_running) xrEndSession(g_session);
    DestroyAll();
    Log("OpenXR closed");
}

// Handles the runtime's messages. Returns flags: 1 = session running, 2 = headset shows our frames
// (visible or focused), 4 = the runtime wants us to quit (turn VR off), 8 = focused,
// 16 = the player recentered with the headset's own button (since the last call).
EXPORT int OxrPoll()
{
    std::lock_guard<std::mutex> l(g_lock);
    if (g_sim) return g_simActive ? 1 | 2 | 8 : 4;
    if (g_instance == XR_NULL_HANDLE) return 4;
    bool recentered = false;
    XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
    while (xrPollEvent(g_instance, &ev) == XR_SUCCESS)
    {
        switch (ev.type)
        {
        case XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED:
        {
            auto& e = *(XrEventDataSessionStateChanged*)&ev;
            g_state = e.state;
            Log("session state %d", (int)g_state);
            if (g_state == XR_SESSION_STATE_READY)
            {
                XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
                bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                XrResult r = xrBeginSession(g_session, &bi);
                g_running = XR_SUCCEEDED(r);
                if (!g_running) Log("xrBeginSession: %s", ResultName(r));
            }
            else if (g_state == XR_SESSION_STATE_STOPPING)
            {
                xrEndSession(g_session);
                g_running = false;
                for (auto& s : g_slots) s.waited = false;
            }
            else if (g_state == XR_SESSION_STATE_EXITING || g_state == XR_SESSION_STATE_LOSS_PENDING) g_exit = true;
            break;
        }
        case XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING:
            g_exit = true;
            break;
        case XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING:
            Log("reference space changed (recentered by the headset)");
            recentered = true;
            break;
        default:
            break;
        }
        ev = {XR_TYPE_EVENT_DATA_BUFFER};
    }
    int flags = 0;
    if (g_running) flags |= 1;
    if (g_state == XR_SESSION_STATE_VISIBLE || g_state == XR_SESSION_STATE_FOCUSED) flags |= 2;
    if (g_exit) flags |= 4;
    if (g_state == XR_SESSION_STATE_FOCUSED) flags |= 8;
    if (recentered) flags |= 16;
    return flags;
}

// Waits until the headset wants the next frame (this paces the game), then reports where both eyes
// will be when it is shown. out: per eye position xyz, orientation xyzw, fov left right up down
// (radians), OpenXR coordinates of the LOCAL space = 22 floats. mode/quad/flip describe what this
// frame shows. Returns 1 = render this frame, 0 = submit nothing (headset not showing us), < 0 = error.
// The mod must queue OnRenderEvent(slot) for every call that returned >= 0.
static int SimWaitFrame(int slot, int mode, int flipEyes, int flipScreen, float* out)
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (g_simHz > 0)
    {
        LONGLONG period = g_simFreq.QuadPart / g_simHz;
        if (now.QuadPart < g_simNext.QuadPart) Sleep((DWORD)((g_simNext.QuadPart - now.QuadPart) * 1000 / g_simFreq.QuadPart));
        g_simNext.QuadPart = std::max(g_simNext.QuadPart + period, now.QuadPart);
    }
    g_framesWaited++;

    std::lock_guard<std::mutex> l(g_lock);
    FrameSlot& f = g_slots[slot & 3];
    f = FrameSlot();
    f.waited = true;
    f.shouldRender = true;
    f.located = true;
    f.mode = mode;
    f.flipEyes = flipEyes != 0;
    f.flipScreen = flipScreen != 0;
    // eyes 64 mm apart, looking straight ahead, about 95 x 100 degrees
    for (int e = 0; e < 2; e++)
    {
        float* o = out + e * 11;
        o[0] = e == 0 ? -0.032f : 0.032f; o[1] = 0; o[2] = 0;
        o[3] = 0; o[4] = 0; o[5] = 0; o[6] = 1;
        o[7] = -0.83f; o[8] = 0.83f; o[9] = 0.87f; o[10] = -0.87f;
    }
    return 1;
}

EXPORT int OxrWaitFrame(int slot, int mode, const float* quad, int flipEyes, int flipScreen, float* out)
{
    if (g_sim) return g_simActive ? SimWaitFrame(slot, mode, flipEyes, flipScreen, out) : -1;
    if (g_session == XR_NULL_HANDLE || !g_running) return -1;
    XrFrameWaitInfo wi{XR_TYPE_FRAME_WAIT_INFO};
    XrFrameState fs{XR_TYPE_FRAME_STATE};
    XrResult r = xrWaitFrame(g_session, &wi, &fs);
    if (XR_FAILED(r)) { Log("xrWaitFrame: %s", ResultName(r)); return -2; }
    g_framesWaited++;

    std::lock_guard<std::mutex> l(g_lock);
    FrameSlot& f = g_slots[slot & 3];
    f = FrameSlot();
    f.waited = true;
    f.displayTime = fs.predictedDisplayTime;
    f.shouldRender = fs.shouldRender == XR_TRUE;
    f.mode = mode;
    f.flipEyes = flipEyes != 0;
    f.flipScreen = flipScreen != 0;
    if (quad)
    {
        f.quadPose.position = {quad[0], quad[1], quad[2]};
        f.quadPose.orientation = {quad[3], quad[4], quad[5], quad[6]};
        f.quadWidth = quad[7];
        f.quadHeight = quad[8];
    }

    XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
    li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    li.displayTime = fs.predictedDisplayTime;
    li.space = g_space;
    XrViewState vs{XR_TYPE_VIEW_STATE};
    uint32_t n = 0;
    r = xrLocateViews(g_session, &li, &vs, 2, &n, f.views);
    f.located = XR_SUCCEEDED(r) && n == 2 && (vs.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT);
    for (int e = 0; e < 2; e++)
    {
        const XrView& v = f.views[e];
        float* o = out + e * 11;
        o[0] = v.pose.position.x; o[1] = v.pose.position.y; o[2] = v.pose.position.z;
        o[3] = v.pose.orientation.x; o[4] = v.pose.orientation.y; o[5] = v.pose.orientation.z; o[6] = v.pose.orientation.w;
        o[7] = v.fov.angleLeft; o[8] = v.fov.angleRight; o[9] = v.fov.angleUp; o[10] = v.fov.angleDown;
    }
    return f.shouldRender && (mode != 0 || f.located) ? 1 : 0;
}

static XrResult SetSource(Source& s, void* tex)
{
    if (s.tex == tex && s.srv) return XR_SUCCESS;
    DropSource(s);
    if (!tex || !g_device) return XR_SUCCESS;
    auto* t = (ID3D11Texture2D*)tex;
    D3D11_TEXTURE2D_DESC td;
    t->GetDesc(&td);
    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
    // Unity makes sRGB render textures "typeless"; read them as sRGB (the target is sRGB too, so
    // the colours pass through unchanged).
    switch (td.Format)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB; break;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB; break;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: sd.Format = DXGI_FORMAT_R16G16B16A16_FLOAT; break;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: sd.Format = DXGI_FORMAT_R10G10B10A2_UNORM; break;
    default: sd.Format = td.Format; break;
    }
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sd.Texture2D.MipLevels = 1;
    if (FAILED(g_device->CreateShaderResourceView(t, &sd, &s.srv)))
    {
        Log("can't read texture (format %d)", (int)td.Format);
        return XR_ERROR_RUNTIME_FAILURE;
    }
    s.tex = t;
    Log("texture %ux%u format %d -> view %d", td.Width, td.Height, (int)td.Format, (int)sd.Format);
    return XR_SUCCESS;
}

// The eye render textures (GetNativeTexturePtr); null to drop them.
EXPORT int OxrSetEyeTextures(void* left, void* right)
{
    std::lock_guard<std::mutex> l(g_lock);
    XrResult r = SetSource(g_eyeSrc[0], left);
    if (XR_SUCCEEDED(r)) r = SetSource(g_eyeSrc[1], right);
    return (int)r;
}

// The flat game picture for the virtual screen; makes a swapchain of the same size.
EXPORT int OxrSetScreenTexture(void* tex, int w, int h)
{
    std::lock_guard<std::mutex> l(g_lock);
    if (g_session == XR_NULL_HANDLE && !g_simActive) return -1;
    bool have = g_quad.handle != XR_NULL_HANDLE || g_quad.simTexture;
    if (have && (g_quad.width != w || g_quad.height != h)) { DestroySwapchain(g_quad); have = false; }
    if (tex && !have)
    {
        XrResult r = MakeSwapchain(g_quad, w, h);
        if (XR_FAILED(r)) { DestroySwapchain(g_quad); return (int)r; }
    }
    return (int)SetSource(g_screenSrc, tex);
}

EXPORT int OxrFramesSubmitted() { return g_framesSubmitted.load(); }
