// End-to-end check of the installed virtual camera: registers "DSLR Webcam Studio" with Windows
// (like the app does) and feeds a moving test pattern for N seconds, so another program (ffmpeg,
// OBS, the Camera app) can open the camera and see it.
// Usage: test_vcam_live.exe <installed dll path> <seconds>
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <stdio.h>
#include <stdlib.h>

#include "vcam_shared.h"

int main(int argc, char **argv) {
    const char *dll = argc > 1 ? argv[1] : "C:\\ProgramData\\DSLR Webcam Studio\\DSLRWebcamStudioVCam.dll";
    int seconds = argc > 2 ? atoi(argv[2]) : 20;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HMODULE m = LoadLibraryA(dll);
    if (!m) { printf("load failed %lu\n", GetLastError()); return 1; }
    auto create = (HRESULT(WINAPI *)(int, IUnknown **))GetProcAddress(m, "DwsVCamCreate");
    auto remove = (HRESULT(WINAPI *)(IUnknown *))GetProcAddress(m, "DwsVCamRemove");
    IUnknown *cam = nullptr;
    HRESULT hr = create(0, &cam);
    printf("DwsVCamCreate: 0x%08lX\n", (unsigned long)hr);
    if (FAILED(hr)) return 2;
    fflush(stdout);

    HANDLE sec = nullptr;
    BYTE *view = nullptr;
    DWORD end = GetTickCount() + seconds * 1000, nextOpen = 0;
    int frames = 0;
    bool reported = false;
    const UINT32 W = 1280, H = 720;
    while (GetTickCount() < end) {
        if (!view && GetTickCount() >= nextOpen) {
            nextOpen = GetTickCount() + 500;
            sec = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, DWS_SECTION_NAME);
            if (sec) view = (BYTE *)MapViewOfFile(sec, FILE_MAP_ALL_ACCESS, 0, 0, DWS_SECTION_SIZE);
            if (view && !reported) { printf("consumer connected: shared section opened\n"); fflush(stdout); reported = true; }
        }
        if (view) {
            auto *hd = (DwsSharedHeader *)view;
            UINT32 *px = (UINT32 *)(view + DWS_HEADER_SIZE);
            InterlockedIncrement((volatile LONG *)&hd->seq);
            hd->magic = DWS_MAGIC; hd->version = DWS_VERSION; hd->width = W; hd->height = H; hd->fps = 30;
            UINT32 bx = (frames * 8) % W;
            static const UINT32 bars[7] = {0xFFC0C0C0, 0xFFC0C000, 0xFF00C0C0, 0xFF00C000, 0xFFC000C0, 0xFFC00000, 0xFF0000C0};
            for (UINT32 y = 0; y < H; y++)
                for (UINT32 x = 0; x < W; x++)
                    px[y * W + x] = (x >= bx && x < bx + 24) ? 0xFFFFFFFF : bars[x * 7 / W];
            hd->written_at_ms = (int64_t)GetTickCount64();
            InterlockedIncrement((volatile LONG *)&hd->seq);
            frames++;
        }
        Sleep(33);
    }
    printf("frames written: %d\n", frames);
    remove(cam);
    cam->Release();
    return 0;
}
