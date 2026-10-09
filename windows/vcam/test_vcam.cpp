// Exercises the virtual camera media source in-process through Windows' own IMFSourceReader:
// activation, media types, start, paced samples, frame contents (RGB32 + NV12) and placeholder.
// Build: zig cc -target x86_64-windows-gnu -x c++ test_vcam.cpp -lmfplat -lmfreadwrite -lmfuuid -lole32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cguid.h>
#include <initguid.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <stdio.h>
#include <string.h>

#include "vcam_shared.h"

DEFINE_GUID(CLSID_Dws, 0x8b5c6fe4, 0x7003, 0x435d, 0xb1, 0x2f, 0x2e, 0x76, 0x43, 0x5a, 0xde, 0xba);

static int failures;
static void check(bool ok, const char *what) { printf("%s %s\n", ok ? "PASS" : "FAIL", what); if (!ok) failures++; }

static DwsSharedHeader *g_hdr;
static UINT32 *g_px;

static void write_frame(UINT32 w, UINT32 h, UINT32 color) {
    InterlockedIncrement((volatile LONG *)&g_hdr->seq);
    g_hdr->magic = DWS_MAGIC; g_hdr->version = DWS_VERSION; g_hdr->width = w; g_hdr->height = h; g_hdr->fps = 30;
    for (UINT32 i = 0; i < w * h; i++) g_px[i] = color;
    g_hdr->written_at_ms = (int64_t)GetTickCount64();
    InterlockedIncrement((volatile LONG *)&g_hdr->seq);
}

static bool read_one(IMFSourceReader *r, BYTE *out, DWORD cap, DWORD *len, LONGLONG *ts) {
    DWORD flags = 0;
    IMFSample *s = nullptr;
    HRESULT hr = r->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, nullptr, &flags, ts, &s);
    if (FAILED(hr) || !s) return false;
    IMFMediaBuffer *b = nullptr;
    s->ConvertToContiguousBuffer(&b);
    BYTE *p; DWORD n;
    b->Lock(&p, nullptr, &n);
    memcpy(out, p, n < cap ? n : cap);
    *len = n;
    b->Unlock();
    b->Release();
    s->Release();
    return true;
}

int main(int argc, char **argv) {
    const char *dll = argc > 1 ? argv[1] : "DSLRWebcamStudioVCam.dll";
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION, MFSTARTUP_FULL);

    HANDLE sec = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, DWS_SECTION_SIZE, DWS_SECTION_NAME_LOCAL);
    BYTE *view = (BYTE *)MapViewOfFile(sec, FILE_MAP_ALL_ACCESS, 0, 0, DWS_SECTION_SIZE);
    g_hdr = (DwsSharedHeader *)view;
    g_px = (UINT32 *)(view + DWS_HEADER_SIZE);

    HMODULE m = LoadLibraryA(dll);
    check(m != nullptr, "load DLL");
    if (!m) return 1;
    auto getCO = (HRESULT(WINAPI *)(REFCLSID, REFIID, void **))GetProcAddress(m, "DllGetClassObject");
    IClassFactory *cf = nullptr;
    check(getCO && SUCCEEDED(getCO(CLSID_Dws, IID_IClassFactory, (void **)&cf)), "DllGetClassObject");
    IMFActivate *act = nullptr;
    check(SUCCEEDED(cf->CreateInstance(nullptr, IID_IMFActivate, (void **)&act)), "create IMFActivate");
    IMFMediaSource *src = nullptr;
    check(SUCCEEDED(act->ActivateObject(IID_IMFMediaSource, (void **)&src)), "activate IMFMediaSource");

    IMFMediaSourceEx *ex = nullptr;
    IMFAttributes *sa = nullptr;
    UINT32 shared = 0;
    check(SUCCEEDED(src->QueryInterface(IID_IMFMediaSourceEx, (void **)&ex)) && SUCCEEDED(ex->GetStreamAttributes(0, &sa)) &&
          SUCCEEDED(sa->GetUINT32(GUID{0x1cb378e9, 0xb279, 0x41d4, {0xaf, 0x97, 0x34, 0xa2, 0x43, 0xe6, 0x83, 0x20}}, &shared)) && shared == 1,
          "stream attributes: frame-server shared");

    IMFSourceReader *reader = nullptr;
    check(SUCCEEDED(MFCreateSourceReaderFromMediaSource(src, nullptr, &reader)), "source reader");
    int ntypes = 0;
    char desc[512] = "";
    for (DWORD i = 0;; i++) {
        IMFMediaType *t = nullptr;
        if (FAILED(reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, i, &t))) break;
        GUID sub; UINT32 w, h, n, d;
        t->GetGUID(MF_MT_SUBTYPE, &sub);
        MFGetAttributeSize(t, MF_MT_FRAME_SIZE, &w, &h);
        MFGetAttributeRatio(t, MF_MT_FRAME_RATE, &n, &d);
        char one[64];
        snprintf(one, sizeof one, "%s %ux%u@%u ", sub == MFVideoFormat_NV12 ? "NV12" : "RGB32", w, h, n / (d ? d : 1));
        strncat(desc, one, sizeof desc - strlen(desc) - 1);
        t->Release();
        ntypes++;
    }
    printf("     media types: %s\n", desc);
    check(ntypes == 6, "6 media types (3 sizes x NV12/RGB32)");

    static BYTE buf[1920 * 1080 * 4];
    DWORD len;
    LONGLONG ts;

    // RGB32 1280x720: frames from the app come through unchanged.
    IMFMediaType *rgb = nullptr;
    reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1, &rgb); // preferred size, RGB32
    check(SUCCEEDED(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, rgb)), "select RGB32 1280x720");
    rgb->Release();
    write_frame(1280, 720, 0xFF336699);
    check(read_one(reader, buf, sizeof buf, &len, &ts) && len == 1280 * 720 * 4 && ((UINT32 *)buf)[640 * 360] == 0xFF336699,
          "RGB32 sample carries the app frame");
    LONGLONG t0 = 0, t1 = 0;
    DWORD start = GetTickCount();
    int got = 0;
    for (int i = 0; i < 30; i++) { write_frame(1280, 720, 0xFF336699); if (read_one(reader, buf, sizeof buf, &len, i == 0 ? &t0 : &t1)) got++; }
    double secs = (GetTickCount() - start) / 1000.0;
    char msg[160];
    snprintf(msg, sizeof msg, "paced: 30 samples in %.2f s (target 1.00 s at 30 fps)", secs);
    check(got == 30 && secs > 0.85 && secs < 1.3, msg);

    // App frame of a different size is scaled to the negotiated size.
    write_frame(640, 360, 0xFF00FF00);
    check(read_one(reader, buf, sizeof buf, &len, &ts) && ((UINT32 *)buf)[1280 * 360 + 640] == 0xFF00FF00, "smaller app frame scaled up");

    // NV12 at 640x360: white -> Y=235, U=V=128 (BT.601 limited range).
    IMFMediaType *nv = nullptr;
    reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 4, &nv);
    UINT32 w = 0, h = 0;
    MFGetAttributeSize(nv, MF_MT_FRAME_SIZE, &w, &h);
    check(SUCCEEDED(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, nv)), "select NV12");
    nv->Release();
    write_frame(1280, 720, 0xFFFFFFFF);
    bool ok = read_one(reader, buf, sizeof buf, &len, &ts);
    snprintf(msg, sizeof msg, "NV12 %ux%u white: Y=%u U=%u V=%u", w, h, buf[0], buf[w * h], buf[w * h + 1]);
    check(ok && len == w * h * 3 / 2 && buf[0] == 235 && buf[w * h] == 128 && buf[w * h + 1] == 128, msg);

    // App stops sending: placeholder after 2 s.
    g_hdr->written_at_ms -= 5000;
    ok = read_one(reader, buf, sizeof buf, &len, &ts);
    snprintf(msg, sizeof msg, "placeholder when app is not sending (Y=%u)", buf[0]);
    check(ok && buf[0] < 60, msg);

    reader->Release();
    src->Shutdown();
    src->Release();
    act->ShutdownObject();
    act->Release();
    printf(failures ? "%d FAILED\n" : "ALL PASSED\n", failures);
    return failures ? 1 : 0;
}
