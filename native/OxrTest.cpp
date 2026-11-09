// OxrTest.exe: tries OthVrNative.dll without the game. Makes its own Direct3D 11 device and two test
// pictures (left eye: red with a white top, right eye: blue with a white top), runs the same calls the mod
// makes for N seconds (default 15), and prints the helper's log. Double-clicked (no arguments) it waits
// for Enter at the end so the window stays open.
// Headset on: you should see red in the left eye and blue in the right, white at the top of both.
// Then the flat-screen mode for the last third: a green screen with a white top floating ahead.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <cstdio>
#include <cstdlib>
#include <vector>

extern "C" {
__declspec(dllimport) int OxrInit(void* tex, float scale, char* info, int infoSize);
__declspec(dllimport) void OxrEyeSize(int* w, int* h);
__declspec(dllimport) void OxrShutdown();
__declspec(dllimport) int OxrPoll();
__declspec(dllimport) int OxrWaitFrame(int slot, int mode, const float* quad, int flipEyes, int flipScreen, float* out);
__declspec(dllimport) int OxrSetEyeTextures(void* l, void* r);
__declspec(dllimport) int OxrSetScreenTexture(void* t, int w, int h);
__declspec(dllimport) void* OxrRenderEventFunc();
__declspec(dllimport) int OxrFramesSubmitted();
__declspec(dllimport) int OxrTakeLog(char* buf, int size);
}

static void Flush()
{
    char buf[1024];
    while (OxrTakeLog(buf, sizeof buf) > 0) printf("  helper: %s\n", buf);
}

static ID3D11Texture2D* Picture(ID3D11Device* dev, int w, int h, unsigned color)
{
    std::vector<unsigned> px((size_t)w * h);
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            px[(size_t)y * w + x] = y < h / 4 ? 0xffffffffu : color;   // row 0 = top in Direct3D
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;   // like Unity's sRGB render textures
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    D3D11_SUBRESOURCE_DATA sd = {px.data(), (UINT)(w * 4), 0};
    ID3D11Texture2D* t = nullptr;
    dev->CreateTexture2D(&td, &sd, &t);
    return t;
}

int main(int argc, char** argv)
{
    int seconds = argc > 1 ? atoi(argv[1]) : 15;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_1;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, &dev, nullptr, &ctx)))
    { printf("no Direct3D 11 device\n"); return 1; }

    auto* any = Picture(dev, 16, 16, 0xff000000);
    char info[1024];
    int r = OxrInit(any, 1.0f, info, sizeof info);
    Flush();
    if (r != 0)
    {
        printf("OxrInit failed: %d\n", r);
        if (argc == 1) { printf("Press Enter to close.\n"); getchar(); }
        return 2;
    }
    printf("OK: %s\n", info);

    int w, h;
    OxrEyeSize(&w, &h);
    auto* left = Picture(dev, w, h, 0xff2020e0);    // ABGR in memory: red
    auto* right = Picture(dev, w, h, 0xffe02020);   // blue
    auto* screen = Picture(dev, 1280, 720, 0xff20c020);   // green
    printf("eye textures: %d\n", OxrSetEyeTextures(left, right));
    printf("screen texture: %d\n", OxrSetScreenTexture(screen, 1280, 720));
    Flush();
    auto render = (void(__stdcall*)(int))OxrRenderEventFunc();

    float views[22];
    // the flat screen 2 m ahead, 2.4 m wide, at head height (LOCAL space: origin = head at start)
    float quad[9] = {0, 0, -2, 0, 0, 0, 1, 2.4f, 1.35f};
    DWORD end = GetTickCount() + seconds * 1000, screenFrom = GetTickCount() + seconds * 2000 / 3;
    int slot = 0, waited = 0, shown = 0;
    while (GetTickCount() < end)
    {
        int flags = OxrPoll();
        Flush();
        if (flags & 4) { printf("runtime asked us to quit\n"); break; }
        if (!(flags & 1)) { Sleep(10); continue; }
        int mode = GetTickCount() >= screenFrom ? 1 : 0;
        int fr = OxrWaitFrame(slot, mode, quad, 0, 0, views);
        if (fr < 0) { Flush(); Sleep(10); continue; }
        waited++;
        if (fr == 1) shown++;
        render(slot);
        slot = (slot + 1) & 3;
        if (waited % 90 == 1)
            printf("frame %d: flags %d, left eye at (%.3f %.3f %.3f), fov %.0f..%.0f deg, submitted %d\n", waited, flags,
                   views[0], views[1], views[2], views[7] * 57.3f, views[8] * 57.3f, OxrFramesSubmitted());
    }
    printf("waited %d frames, %d shown, %d submitted\n", waited, shown, OxrFramesSubmitted());
    OxrSetEyeTextures(nullptr, nullptr);
    OxrSetScreenTexture(nullptr, 0, 0);
    OxrShutdown();
    Flush();
    if (argc == 1) { printf("Press Enter to close.\n"); getchar(); }
    return 0;
}
