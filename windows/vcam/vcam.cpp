// DSLR Webcam Studio virtual camera for Windows 11.
//
// A Media Foundation media source that Windows' Camera Frame Server loads when any app
// (OBS, Streamlabs, Zoom, Teams, browsers, the Camera app) opens the "DSLR Webcam Studio"
// camera. Frames come from the desktop app through a shared-memory section (vcam_shared.h).
// When the app isn't sending, the camera shows a dark placeholder frame.
//
// The same DLL also exports helpers the app uses to register the camera with
// MFCreateVirtualCamera (mfsensorgroup.dll).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cguid.h>
#include <initguid.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <sddl.h>

#include "vcam_shared.h"

// ---- declarations missing from MinGW headers (values verified against Windows' FrameServer.dll) ----

DEFINE_GUID(CLSID_DwsVirtualCamera, 0x8b5c6fe4, 0x7003, 0x435d, 0xb1, 0x2f, 0x2e, 0x76, 0x43, 0x5a, 0xde, 0xba);
DEFINE_GUID(DWS_MF_DEVICESTREAM_STREAM_CATEGORY, 0x2939e7b8, 0xa62e, 0x4579, 0xb6, 0x74, 0xd4, 0x07, 0x3d, 0xfa, 0xbb, 0xba);
DEFINE_GUID(DWS_MF_DEVICESTREAM_STREAM_ID, 0x11bd5120, 0xd124, 0x446b, 0x88, 0xe6, 0x17, 0x06, 0x02, 0x57, 0xff, 0xf9);
DEFINE_GUID(DWS_MF_DEVICESTREAM_FRAMESERVER_SHARED, 0x1cb378e9, 0xb279, 0x41d4, 0xaf, 0x97, 0x34, 0xa2, 0x43, 0xe6, 0x83, 0x20);
DEFINE_GUID(DWS_MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, 0x17145fd1, 0x1b2b, 0x423c, 0x80, 0x01, 0x2b, 0x68, 0x33, 0xed, 0x35, 0x88);
DEFINE_GUID(DWS_PINNAME_VIDEO_CAPTURE, 0xfb6c4281, 0x0353, 0x11d1, 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba);
DEFINE_GUID(IID_DwsMFMediaStream2, 0xc5bc37d6, 0x75c7, 0x46a1, 0xa1, 0x32, 0x81, 0xb5, 0xf7, 0x23, 0xc2, 0x0f);
DEFINE_GUID(IID_DwsKsControl, 0x28f54685, 0x06fd, 0x11d2, 0xb2, 0x7a, 0x00, 0xa0, 0xc9, 0x22, 0x31, 0x96);
static const UINT32 MFFrameSourceTypes_Color_ = 0x0001;

struct IDwsMFMediaStream2 : public IMFMediaStream {
    virtual HRESULT STDMETHODCALLTYPE SetStreamState(MF_STREAM_STATE value) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetStreamState(MF_STREAM_STATE *value) = 0;
};

// Frame Server queries IKsControl on sources and streams; this source has no KS properties.
struct IDwsKsControl : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE KsProperty(void *prop, ULONG propLen, void *data, ULONG dataLen, ULONG *ret) = 0;
    virtual HRESULT STDMETHODCALLTYPE KsMethod(void *method, ULONG methodLen, void *data, ULONG dataLen, ULONG *ret) = 0;
    virtual HRESULT STDMETHODCALLTYPE KsEvent(void *ev, ULONG evLen, void *data, ULONG dataLen, ULONG *ret) = 0;
};

// IMFVirtualCamera (mfvirtualcamera.h); only the methods up to Remove are called.
struct IDwsMFVirtualCamera : public IMFAttributes {
    virtual HRESULT STDMETHODCALLTYPE AddDeviceSourceInfo(LPCWSTR deviceSourceInfo) = 0;
    virtual HRESULT STDMETHODCALLTYPE AddProperty(const void *key, DWORD type, const BYTE *data, ULONG cb) = 0;
    virtual HRESULT STDMETHODCALLTYPE AddRegistryEntry(LPCWSTR name, LPCWSTR subkey, DWORD type, const BYTE *data, ULONG cb) = 0;
    virtual HRESULT STDMETHODCALLTYPE Start(IMFAsyncCallback *callback) = 0;
    virtual HRESULT STDMETHODCALLTYPE Stop() = 0;
    virtual HRESULT STDMETHODCALLTYPE Remove() = 0;
};

typedef HRESULT(WINAPI *PFN_MFCreateVirtualCamera)(int type, int lifetime, int access, LPCWSTR friendlyName,
                                                    LPCWSTR sourceId, const GUID *categories, ULONG categoryCount,
                                                    IDwsMFVirtualCamera **camera);

static HMODULE g_module;
static volatile LONG g_objects;

// No C++ runtime: allocation goes straight to the process heap (keeps the DLL small and its exports clean).
struct NoThrow {};
static const NoThrow nothrow_ = {};
void *operator new(size_t n, const NoThrow &) noexcept { return HeapAlloc(GetProcessHeap(), 0, n); }
void *operator new[](size_t n, const NoThrow &) noexcept { return HeapAlloc(GetProcessHeap(), 0, n); }
void *operator new(size_t, void *p) noexcept { return p; }
void operator delete(void *p) noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }
void operator delete(void *p, size_t) noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }
void operator delete[](void *p) noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }
void operator delete[](void *p, size_t) noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }

template <class T> static void SafeRelease(T *&p) { if (p) { p->Release(); p = nullptr; } }

#define KS_NOT_FOUND HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND)

// ---- shared frame reader ---------------------------------------------------------------------

class FrameReader {
public:
    FrameReader() { InitializeCriticalSection(&cs_); }
    ~FrameReader() {
        if (view_) UnmapViewOfFile(view_);
        if (section_) CloseHandle(section_);
        delete[] tmp_;
        DeleteCriticalSection(&cs_);
    }

    // Creates the Global\ section the app writes into (Frame Server may create global objects).
    void Open() {
        if (section_) return;
        SECURITY_ATTRIBUTES sa = {sizeof sa, nullptr, FALSE};
        // Everyone and logged-on users may read/write; low-integrity label so any app can write.
        ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;WD)(A;;GA;;;AU)S:(ML;;NW;;;LW)", SDDL_REVISION_1,
                                                             &sa.lpSecurityDescriptor, nullptr);
        section_ = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, DWS_SECTION_SIZE, DWS_SECTION_NAME);
        if (sa.lpSecurityDescriptor) LocalFree(sa.lpSecurityDescriptor);
        if (!section_) section_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, DWS_SECTION_NAME);
        // Outside Frame Server (tests, in-process use) Global creation needs a privilege; use this session instead.
        if (!section_) section_ = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, DWS_SECTION_SIZE, DWS_SECTION_NAME_LOCAL);
        if (section_) view_ = (BYTE *)MapViewOfFile(section_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, DWS_SECTION_SIZE);
    }

    // Copies the newest app frame into dst (BGRA, w x h), scaling if sizes differ.
    // Returns false (and draws the placeholder) when the app isn't sending.
    bool Read(UINT32 *dst, UINT32 w, UINT32 h) {
        EnterCriticalSection(&cs_);
        bool ok = ReadLocked(dst, w, h);
        LeaveCriticalSection(&cs_);
        if (!ok) Placeholder(dst, w, h);
        return ok;
    }

private:
    bool ReadLocked(UINT32 *dst, UINT32 w, UINT32 h) {
        Open();
        if (!view_) return false;
        auto *hd = (volatile DwsSharedHeader *)view_;
        if (hd->magic != DWS_MAGIC || hd->version != DWS_VERSION) return false;
        if (GetTickCount64() - (ULONGLONG)hd->written_at_ms > 2000) return false; // app stopped sending
        for (int attempt = 0; attempt < 4; attempt++) {
            LONG s1 = hd->seq;
            MemoryBarrier();
            if (s1 & 1) { Sleep(1); continue; }
            UINT32 sw = hd->width, sh = hd->height;
            if (sw == 0 || sh == 0 || sw > DWS_MAX_W || sh > DWS_MAX_H) return false;
            const UINT32 *src = (const UINT32 *)(view_ + DWS_HEADER_SIZE);
            if (sw == w && sh == h) {
                memcpy(dst, src, (size_t)w * h * 4);
            } else {
                // Snapshot first so scaling reads a consistent frame, then scale (bilinear).
                size_t n = (size_t)sw * sh;
                if (tmpCap_ < n) { delete[] tmp_; tmp_ = new (nothrow_) UINT32[n]; tmpCap_ = tmp_ ? n : 0; }
                if (!tmp_) return false;
                memcpy(tmp_, src, n * 4);
                Scale(tmp_, sw, sh, dst, w, h);
            }
            MemoryBarrier();
            if (hd->seq == s1) return true;
        }
        return true; // a torn frame is better than a frozen one
    }

    static void Scale(const UINT32 *s, UINT32 sw, UINT32 sh, UINT32 *d, UINT32 dw, UINT32 dh) {
        for (UINT32 y = 0; y < dh; y++) {
            double fy = (y + 0.5) * sh / dh - 0.5;
            if (fy < 0) fy = 0;
            UINT32 y0 = (UINT32)fy, y1 = y0 + 1 < sh ? y0 + 1 : sh - 1;
            UINT32 wy = (UINT32)((fy - y0) * 256);
            for (UINT32 x = 0; x < dw; x++) {
                double fx = (x + 0.5) * sw / dw - 0.5;
                if (fx < 0) fx = 0;
                UINT32 x0 = (UINT32)fx, x1 = x0 + 1 < sw ? x0 + 1 : sw - 1;
                UINT32 wx = (UINT32)((fx - x0) * 256);
                UINT32 a = s[y0 * sw + x0], b = s[y0 * sw + x1], c = s[y1 * sw + x0], e = s[y1 * sw + x1], out = 0xFF000000;
                for (int sh8 = 0; sh8 <= 16; sh8 += 8) {
                    UINT32 top = ((a >> sh8 & 255) * (256 - wx) + (b >> sh8 & 255) * wx) >> 8;
                    UINT32 bot = ((c >> sh8 & 255) * (256 - wx) + (e >> sh8 & 255) * wx) >> 8;
                    out |= ((top * (256 - wy) + bot * wy) >> 8) << sh8;
                }
                d[y * dw + x] = out;
            }
        }
    }

    // Dark frame with a small camera outline, shown until the app sends video.
    static void Placeholder(UINT32 *d, UINT32 w, UINT32 h) {
        for (size_t i = 0, n = (size_t)w * h; i < n; i++) d[i] = 0xFF1E1F24;
        UINT32 cw = w / 8, ch = cw * 2 / 3, x0 = (w - cw) / 2, y0 = (h - ch) / 2, t = w / 320 + 1;
        for (UINT32 y = y0; y < y0 + ch; y++)
            for (UINT32 x = x0; x < x0 + cw; x++) {
                bool edge = x < x0 + t || x >= x0 + cw - t || y < y0 + t || y >= y0 + ch - t;
                long dx = (long)x - (long)(x0 + cw / 2), dy = (long)y - (long)(y0 + ch / 2), r = (long)ch / 4;
                bool lens = dx * dx + dy * dy <= r * r && dx * dx + dy * dy >= (r - (long)t) * (r - (long)t);
                if (edge || lens) d[y * w + x] = 0xFF5A5F6E;
            }
    }

    CRITICAL_SECTION cs_;
    HANDLE section_ = nullptr;
    BYTE *view_ = nullptr;
    UINT32 *tmp_ = nullptr;
    size_t tmpCap_ = 0;
};

// ---- preferred format (written by the app to ProgramData) -----------------------------------

struct Format { UINT32 w, h, fps; };

static Format PreferredFormat() {
    wchar_t path[MAX_PATH];
    Format f = {1280, 720, 30};
    if (!ExpandEnvironmentStringsW(L"%ProgramData%\\DSLR Webcam Studio\\vcam.ini", path, MAX_PATH)) return f;
    UINT w = GetPrivateProfileIntW(L"VirtualCamera", L"Width", 1280, path);
    UINT h = GetPrivateProfileIntW(L"VirtualCamera", L"Height", 720, path);
    UINT fps = GetPrivateProfileIntW(L"VirtualCamera", L"Fps", 30, path);
    if ((w == 640 && h == 360) || (w == 1280 && h == 720) || (w == 1920 && h == 1080)) { f.w = w; f.h = h; }
    if (fps >= 5 && fps <= 60) f.fps = fps;
    return f;
}

static HRESULT CreateVideoType(const GUID &subtype, UINT32 w, UINT32 h, UINT32 fps, IMFMediaType **out) {
    IMFMediaType *t = nullptr;
    HRESULT hr = MFCreateMediaType(&t);
    if (FAILED(hr)) return hr;
    t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    t->SetGUID(MF_MT_SUBTYPE, subtype);
    MFSetAttributeSize(t, MF_MT_FRAME_SIZE, w, h);
    MFSetAttributeRatio(t, MF_MT_FRAME_RATE, fps, 1);
    MFSetAttributeRatio(t, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    bool nv12 = subtype == MFVideoFormat_NV12;
    t->SetUINT32(MF_MT_DEFAULT_STRIDE, nv12 ? w : w * 4); // positive stride: top-down
    t->SetUINT32(MF_MT_SAMPLE_SIZE, nv12 ? w * h * 3 / 2 : w * h * 4);
    *out = t;
    return S_OK;
}

// ---- media stream ----------------------------------------------------------------------------

class Source;

class Stream final : public IDwsMFMediaStream2, public IDwsKsControl {
public:
    Stream(IMFMediaSource *source, IMFStreamDescriptor *sd) : source_(source), sd_(sd) {
        InterlockedIncrement(&g_objects);
        InitializeCriticalSection(&cs_);
        source_->AddRef();
        sd_->AddRef();
    }

    HRESULT Init() {
        HRESULT hr = MFCreateEventQueue(&queue_);
        if (SUCCEEDED(hr)) hr = MFCreateAttributes(&attrs_, 4);
        if (SUCCEEDED(hr)) {
            attrs_->SetGUID(DWS_MF_DEVICESTREAM_STREAM_CATEGORY, DWS_PINNAME_VIDEO_CAPTURE);
            attrs_->SetUINT32(DWS_MF_DEVICESTREAM_STREAM_ID, 0);
            attrs_->SetUINT32(DWS_MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
            attrs_->SetUINT32(DWS_MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color_);
        }
        return hr;
    }

    IMFAttributes *Attributes() { return attrs_; }

    HRESULT Start() {
        EnterCriticalSection(&cs_);
        HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : S_OK;
        if (SUCCEEDED(hr)) {
            state_ = MF_STREAM_STATE_RUNNING;
            nextDue_ = 0;
            hr = queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
        }
        LeaveCriticalSection(&cs_);
        return hr;
    }

    HRESULT Stop() {
        EnterCriticalSection(&cs_);
        HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : S_OK;
        if (SUCCEEDED(hr)) {
            state_ = MF_STREAM_STATE_STOPPED;
            hr = queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
        }
        LeaveCriticalSection(&cs_);
        return hr;
    }

    bool IsRunning() { return state_ == MF_STREAM_STATE_RUNNING; }

    void Shutdown() {
        EnterCriticalSection(&cs_);
        if (!shutdown_) {
            shutdown_ = true;
            if (queue_) queue_->Shutdown();
            SafeRelease(queue_);
            SafeRelease(attrs_);
            SafeRelease(sd_);
            SafeRelease(source_);
            delete[] bgra_;
            bgra_ = nullptr;
        }
        LeaveCriticalSection(&cs_);
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void **ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaStream || riid == IID_DwsMFMediaStream2)
            *ppv = static_cast<IDwsMFMediaStream2 *>(this);
        else if (riid == IID_DwsKsControl)
            *ppv = static_cast<IDwsKsControl *>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&ref_); }
    STDMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&ref_);
        if (r == 0) delete this;
        return r;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent **ev) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->GetEvent(flags, ev);
        q->Release();
        return hr;
    }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback *cb, IUnknown *state) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->BeginGetEvent(cb, state);
        q->Release();
        return hr;
    }
    STDMETHODIMP EndGetEvent(IMFAsyncResult *res, IMFMediaEvent **ev) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->EndGetEvent(res, ev);
        q->Release();
        return hr;
    }
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID ext, HRESULT status, const PROPVARIANT *value) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->QueueEventParamVar(met, ext, status, value);
        q->Release();
        return hr;
    }

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource **out) override {
        if (!out) return E_POINTER;
        EnterCriticalSection(&cs_);
        HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : S_OK;
        if (SUCCEEDED(hr)) { *out = source_; source_->AddRef(); }
        LeaveCriticalSection(&cs_);
        return hr;
    }
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor **out) override {
        if (!out) return E_POINTER;
        EnterCriticalSection(&cs_);
        HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : S_OK;
        if (SUCCEEDED(hr)) { *out = sd_; sd_->AddRef(); }
        LeaveCriticalSection(&cs_);
        return hr;
    }
    STDMETHODIMP RequestSample(IUnknown *token) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE value) override {
        if (value == MF_STREAM_STATE_RUNNING) return Start();
        if (value == MF_STREAM_STATE_STOPPED) return Stop();
        state_ = value;
        return S_OK;
    }
    STDMETHODIMP GetStreamState(MF_STREAM_STATE *value) override {
        if (!value) return E_POINTER;
        *value = state_;
        return S_OK;
    }

    // IKsControl
    STDMETHODIMP KsProperty(void *, ULONG, void *, ULONG, ULONG *) override { return KS_NOT_FOUND; }
    STDMETHODIMP KsMethod(void *, ULONG, void *, ULONG, ULONG *) override { return KS_NOT_FOUND; }
    STDMETHODIMP KsEvent(void *, ULONG, void *, ULONG, ULONG *) override { return KS_NOT_FOUND; }

private:
    ~Stream() {
        Shutdown();
        DeleteCriticalSection(&cs_);
        InterlockedDecrement(&g_objects);
    }

    IMFMediaEventQueue *LockedQueue() {
        EnterCriticalSection(&cs_);
        IMFMediaEventQueue *q = queue_;
        if (q) q->AddRef();
        LeaveCriticalSection(&cs_);
        return q;
    }

    static void ToNV12(const UINT32 *s, BYTE *d, UINT32 w, UINT32 h) {
        BYTE *uv = d + (size_t)w * h;
        for (UINT32 y = 0; y < h; y++)
            for (UINT32 x = 0; x < w; x++) {
                UINT32 p = s[y * w + x];
                int r = p >> 16 & 255, g = p >> 8 & 255, b = p & 255;
                d[y * w + x] = (BYTE)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16); // BT.601 limited range
            }
        for (UINT32 y = 0; y < h; y += 2)
            for (UINT32 x = 0; x < w; x += 2) {
                int r = 0, g = 0, b = 0;
                for (UINT32 k = 0; k < 4; k++) {
                    UINT32 p = s[(y + (k >> 1)) * w + x + (k & 1)];
                    r += p >> 16 & 255; g += p >> 8 & 255; b += p & 255;
                }
                r >>= 2; g >>= 2; b >>= 2;
                BYTE *o = uv + (size_t)(y / 2) * w + x;
                o[0] = (BYTE)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                o[1] = (BYTE)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
    }

    LONG ref_ = 1;
    CRITICAL_SECTION cs_;
    IMFMediaSource *source_;
    IMFStreamDescriptor *sd_;
    IMFMediaEventQueue *queue_ = nullptr;
    IMFAttributes *attrs_ = nullptr;
    MF_STREAM_STATE state_ = MF_STREAM_STATE_STOPPED;
    bool shutdown_ = false;
    LONGLONG nextDue_ = 0;
    UINT32 *bgra_ = nullptr;
    size_t bgraCap_ = 0;
    FrameReader reader_;
};

STDMETHODIMP Stream::RequestSample(IUnknown *token) {
    EnterCriticalSection(&cs_);
    HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : state_ != MF_STREAM_STATE_RUNNING ? MF_E_INVALIDREQUEST : S_OK;
    IMFMediaType *type = nullptr;
    IMFMediaTypeHandler *handler = nullptr;
    GUID subtype = GUID_NULL;
    UINT32 w = 0, h = 0, num = 30, den = 1;
    if (SUCCEEDED(hr)) hr = sd_->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->GetCurrentMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->GetGUID(MF_MT_SUBTYPE, &subtype);
    if (SUCCEEDED(hr)) hr = MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &w, &h);
    if (SUCCEEDED(hr) && FAILED(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &num, &den))) { num = 30; den = 1; }
    SafeRelease(type);
    SafeRelease(handler);
    if (FAILED(hr)) { LeaveCriticalSection(&cs_); return hr; }

    // Pace samples to the negotiated frame rate.
    LONGLONG interval = 10000000LL * (den ? den : 1) / (num ? num : 30);
    LONGLONG now = MFGetSystemTime();
    if (nextDue_ == 0 || nextDue_ < now - interval) nextDue_ = now;
    LONGLONG wait = nextDue_ - now;
    nextDue_ += interval;
    LeaveCriticalSection(&cs_);
    if (wait > 0) Sleep((DWORD)(wait / 10000));

    EnterCriticalSection(&cs_);
    if (shutdown_) { LeaveCriticalSection(&cs_); return MF_E_SHUTDOWN; }
    size_t px = (size_t)w * h;
    if (bgraCap_ < px) {
        delete[] bgra_;
        bgra_ = new (nothrow_) UINT32[px];
        bgraCap_ = bgra_ ? px : 0;
    }
    if (!bgra_) { LeaveCriticalSection(&cs_); return E_OUTOFMEMORY; }
    reader_.Read(bgra_, w, h);

    bool nv12 = subtype == MFVideoFormat_NV12;
    DWORD size = nv12 ? (DWORD)(px * 3 / 2) : (DWORD)(px * 4);
    IMFMediaBuffer *buf = nullptr;
    IMFSample *sample = nullptr;
    BYTE *data = nullptr;
    hr = MFCreateMemoryBuffer(size, &buf);
    if (SUCCEEDED(hr)) hr = buf->Lock(&data, nullptr, nullptr);
    if (SUCCEEDED(hr)) {
        if (nv12) ToNV12(bgra_, data, w, h); else memcpy(data, bgra_, size);
        buf->Unlock();
        hr = buf->SetCurrentLength(size);
    }
    if (SUCCEEDED(hr)) hr = MFCreateSample(&sample);
    if (SUCCEEDED(hr)) hr = sample->AddBuffer(buf);
    if (SUCCEEDED(hr)) hr = sample->SetSampleTime(MFGetSystemTime());
    if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(interval);
    if (SUCCEEDED(hr) && token) hr = sample->SetUnknown(MFSampleExtension_Token, token);
    if (SUCCEEDED(hr)) hr = queue_->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample);
    SafeRelease(sample);
    SafeRelease(buf);
    LeaveCriticalSection(&cs_);
    return hr;
}

// ---- media source ----------------------------------------------------------------------------

class Source final : public IMFMediaSourceEx, public IMFGetService, public IDwsKsControl {
public:
    Source() { InterlockedIncrement(&g_objects); InitializeCriticalSection(&cs_); }

    HRESULT Init() {
        HRESULT hr = MFCreateEventQueue(&queue_);
        if (SUCCEEDED(hr)) hr = MFCreateAttributes(&attrs_, 2);

        // Preferred format first (what the app outputs), then the other sizes; NV12 and RGB32 each.
        Format pref = PreferredFormat();
        Format sizes[3] = {{1920, 1080, 0}, {1280, 720, 0}, {640, 360, 0}};
        IMFMediaType *types[16] = {};
        DWORD n = 0;
        const GUID *subs[2] = {&MFVideoFormat_NV12, &MFVideoFormat_RGB32};
        for (int s = 0; s < 2 && SUCCEEDED(hr); s++) hr = CreateVideoType(*subs[s], pref.w, pref.h, pref.fps, &types[n++]);
        for (int i = 0; i < 3 && SUCCEEDED(hr); i++) {
            if (sizes[i].w == pref.w) continue;
            for (int s = 0; s < 2 && SUCCEEDED(hr); s++) hr = CreateVideoType(*subs[s], sizes[i].w, sizes[i].h, pref.fps, &types[n++]);
        }

        IMFStreamDescriptor *sd = nullptr;
        IMFMediaTypeHandler *handler = nullptr;
        if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(0, n, types, &sd);
        if (SUCCEEDED(hr)) hr = sd->GetMediaTypeHandler(&handler);
        if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(types[0]);
        if (SUCCEEDED(hr)) {
            sd->SetGUID(DWS_MF_DEVICESTREAM_STREAM_CATEGORY, DWS_PINNAME_VIDEO_CAPTURE);
            sd->SetUINT32(DWS_MF_DEVICESTREAM_STREAM_ID, 0);
            sd->SetUINT32(DWS_MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
            sd->SetUINT32(DWS_MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color_);
            stream_ = new (nothrow_) Stream(static_cast<IMFMediaSourceEx *>(this), sd);
            hr = stream_ ? stream_->Init() : E_OUTOFMEMORY;
        }
        if (SUCCEEDED(hr)) hr = MFCreatePresentationDescriptor(1, &sd, &pd_);
        if (SUCCEEDED(hr)) hr = pd_->SelectStream(0);
        SafeRelease(handler);
        SafeRelease(sd);
        for (DWORD i = 0; i < n; i++) SafeRelease(types[i]);
        return hr;
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void **ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaSource || riid == IID_IMFMediaSourceEx)
            *ppv = static_cast<IMFMediaSourceEx *>(this);
        else if (riid == IID_IMFGetService)
            *ppv = static_cast<IMFGetService *>(this);
        else if (riid == IID_DwsKsControl)
            *ppv = static_cast<IDwsKsControl *>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&ref_); }
    STDMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&ref_);
        if (r == 0) delete this;
        return r;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent **ev) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->GetEvent(flags, ev);
        q->Release();
        return hr;
    }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback *cb, IUnknown *state) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->BeginGetEvent(cb, state);
        q->Release();
        return hr;
    }
    STDMETHODIMP EndGetEvent(IMFAsyncResult *res, IMFMediaEvent **ev) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->EndGetEvent(res, ev);
        q->Release();
        return hr;
    }
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID ext, HRESULT status, const PROPVARIANT *value) override {
        IMFMediaEventQueue *q = LockedQueue();
        if (!q) return MF_E_SHUTDOWN;
        HRESULT hr = q->QueueEventParamVar(met, ext, status, value);
        q->Release();
        return hr;
    }

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD *c) override {
        if (!c) return E_POINTER;
        *c = MFMEDIASOURCE_IS_LIVE;
        return shutdown_ ? MF_E_SHUTDOWN : S_OK;
    }
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor **out) override {
        if (!out) return E_POINTER;
        EnterCriticalSection(&cs_);
        HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : pd_->Clone(out);
        LeaveCriticalSection(&cs_);
        return hr;
    }
    STDMETHODIMP Start(IMFPresentationDescriptor *pd, const GUID *timeFormat, const PROPVARIANT *) override {
        if (!pd) return E_POINTER;
        if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
        EnterCriticalSection(&cs_);
        HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : S_OK;
        BOOL selected = FALSE;
        IMFStreamDescriptor *sd = nullptr;
        if (SUCCEEDED(hr)) hr = pd->GetStreamDescriptorByIndex(0, &selected, &sd);
        if (SUCCEEDED(hr)) {
            // Apply the consumer's chosen media type to our descriptor.
            IMFMediaTypeHandler *in = nullptr, *ours = nullptr;
            IMFMediaType *mt = nullptr;
            IMFStreamDescriptor *own = nullptr;
            DWORD idx = 0;
            BOOL sel = FALSE;
            if (SUCCEEDED(sd->GetMediaTypeHandler(&in)) && SUCCEEDED(in->GetCurrentMediaType(&mt)) &&
                SUCCEEDED(pd_->GetStreamDescriptorByIndex(idx, &sel, &own)) && SUCCEEDED(own->GetMediaTypeHandler(&ours)))
                ours->SetCurrentMediaType(mt);
            SafeRelease(ours); SafeRelease(own); SafeRelease(mt); SafeRelease(in);
        }
        if (SUCCEEDED(hr) && selected) {
            bool wasRunning = stream_->IsRunning();
            hr = queue_->QueueEventParamUnk(wasRunning ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
                                            static_cast<IDwsMFMediaStream2 *>(stream_));
            if (SUCCEEDED(hr)) hr = stream_->Start();
        }
        if (SUCCEEDED(hr)) {
            PROPVARIANT t;
            PropVariantInit(&t);
            t.vt = VT_I8;
            t.hVal.QuadPart = MFGetSystemTime();
            hr = queue_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &t);
            PropVariantClear(&t);
        }
        SafeRelease(sd);
        LeaveCriticalSection(&cs_);
        return hr;
    }
    STDMETHODIMP Stop() override {
        EnterCriticalSection(&cs_);
        HRESULT hr = shutdown_ ? MF_E_SHUTDOWN : S_OK;
        if (SUCCEEDED(hr) && stream_->IsRunning()) hr = stream_->Stop();
        if (SUCCEEDED(hr)) hr = queue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
        LeaveCriticalSection(&cs_);
        return hr;
    }
    STDMETHODIMP Pause() override { return MF_E_INVALID_STATE_TRANSITION; }
    STDMETHODIMP Shutdown() override {
        EnterCriticalSection(&cs_);
        if (!shutdown_) {
            shutdown_ = true;
            if (stream_) { stream_->Shutdown(); stream_->Release(); stream_ = nullptr; }
            if (queue_) queue_->Shutdown();
            SafeRelease(queue_);
            SafeRelease(pd_);
            SafeRelease(attrs_);
        }
        LeaveCriticalSection(&cs_);
        return S_OK;
    }

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes **out) override {
        if (!out) return E_POINTER;
        if (shutdown_) return MF_E_SHUTDOWN;
        *out = attrs_;
        attrs_->AddRef();
        return S_OK;
    }
    STDMETHODIMP GetStreamAttributes(DWORD id, IMFAttributes **out) override {
        if (!out) return E_POINTER;
        if (shutdown_) return MF_E_SHUTDOWN;
        if (id != 0) return MF_E_INVALIDSTREAMNUMBER;
        *out = stream_->Attributes();
        (*out)->AddRef();
        return S_OK;
    }
    STDMETHODIMP SetD3DManager(IUnknown *) override { return S_OK; }

    // IMFGetService
    STDMETHODIMP GetService(REFGUID, REFIID, LPVOID *ppv) override {
        if (ppv) *ppv = nullptr;
        return MF_E_UNSUPPORTED_SERVICE;
    }

    // IKsControl
    STDMETHODIMP KsProperty(void *, ULONG, void *, ULONG, ULONG *) override { return KS_NOT_FOUND; }
    STDMETHODIMP KsMethod(void *, ULONG, void *, ULONG, ULONG *) override { return KS_NOT_FOUND; }
    STDMETHODIMP KsEvent(void *, ULONG, void *, ULONG, ULONG *) override { return KS_NOT_FOUND; }

private:
    ~Source() {
        Shutdown();
        DeleteCriticalSection(&cs_);
        InterlockedDecrement(&g_objects);
    }

    IMFMediaEventQueue *LockedQueue() {
        EnterCriticalSection(&cs_);
        IMFMediaEventQueue *q = queue_;
        if (q) q->AddRef();
        LeaveCriticalSection(&cs_);
        return q;
    }

    LONG ref_ = 1;
    CRITICAL_SECTION cs_;
    IMFMediaEventQueue *queue_ = nullptr;
    IMFPresentationDescriptor *pd_ = nullptr;
    IMFAttributes *attrs_ = nullptr;
    Stream *stream_ = nullptr;
    bool shutdown_ = false;
};

// ---- activator: what Frame Server creates from our CLSID ---------------------------------------

#define FWD(call) return attrs_ ? attrs_->call : E_UNEXPECTED

class Activator final : public IMFActivate {
public:
    Activator() { InterlockedIncrement(&g_objects); }
    HRESULT Init() { return MFCreateAttributes(&attrs_, 1); }

    STDMETHODIMP QueryInterface(REFIID riid, void **ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFAttributes || riid == IID_IMFActivate) {
            *ppv = static_cast<IMFActivate *>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&ref_); }
    STDMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&ref_);
        if (r == 0) delete this;
        return r;
    }

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void **ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (!source_) {
            Source *s = new (nothrow_) Source();
            if (!s) return E_OUTOFMEMORY;
            HRESULT hr = s->Init();
            if (FAILED(hr)) { s->Shutdown(); s->Release(); return hr; }
            source_ = s;
        }
        return source_->QueryInterface(riid, ppv);
    }
    STDMETHODIMP ShutdownObject() override {
        if (source_) { source_->Shutdown(); SafeRelease(source_); }
        return S_OK;
    }
    STDMETHODIMP DetachObject() override { SafeRelease(source_); return S_OK; }

    // IMFAttributes (delegated)
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT *v) override { FWD(GetItem(k, v)); }
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE *t) override { FWD(GetItemType(k, t)); }
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL *r) override { FWD(CompareItem(k, v, r)); }
    STDMETHODIMP Compare(IMFAttributes *a, MF_ATTRIBUTES_MATCH_TYPE m, BOOL *r) override { FWD(Compare(a, m, r)); }
    STDMETHODIMP GetUINT32(REFGUID k, UINT32 *v) override { FWD(GetUINT32(k, v)); }
    STDMETHODIMP GetUINT64(REFGUID k, UINT64 *v) override { FWD(GetUINT64(k, v)); }
    STDMETHODIMP GetDouble(REFGUID k, double *v) override { FWD(GetDouble(k, v)); }
    STDMETHODIMP GetGUID(REFGUID k, GUID *v) override { FWD(GetGUID(k, v)); }
    STDMETHODIMP GetStringLength(REFGUID k, UINT32 *l) override { FWD(GetStringLength(k, l)); }
    STDMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 c, UINT32 *l) override { FWD(GetString(k, v, c, l)); }
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR *v, UINT32 *l) override { FWD(GetAllocatedString(k, v, l)); }
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32 *s) override { FWD(GetBlobSize(k, s)); }
    STDMETHODIMP GetBlob(REFGUID k, UINT8 *b, UINT32 c, UINT32 *s) override { FWD(GetBlob(k, b, c, s)); }
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8 **b, UINT32 *s) override { FWD(GetAllocatedBlob(k, b, s)); }
    STDMETHODIMP GetUnknown(REFGUID k, REFIID r, LPVOID *p) override { FWD(GetUnknown(k, r, p)); }
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { FWD(SetItem(k, v)); }
    STDMETHODIMP DeleteItem(REFGUID k) override { FWD(DeleteItem(k)); }
    STDMETHODIMP DeleteAllItems() override { FWD(DeleteAllItems()); }
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { FWD(SetUINT32(k, v)); }
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { FWD(SetUINT64(k, v)); }
    STDMETHODIMP SetDouble(REFGUID k, double v) override { FWD(SetDouble(k, v)); }
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { FWD(SetGUID(k, v)); }
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { FWD(SetString(k, v)); }
    STDMETHODIMP SetBlob(REFGUID k, const UINT8 *b, UINT32 s) override { FWD(SetBlob(k, b, s)); }
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown *u) override { FWD(SetUnknown(k, u)); }
    STDMETHODIMP LockStore() override { FWD(LockStore()); }
    STDMETHODIMP UnlockStore() override { FWD(UnlockStore()); }
    STDMETHODIMP GetCount(UINT32 *c) override { FWD(GetCount(c)); }
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID *k, PROPVARIANT *v) override { FWD(GetItemByIndex(i, k, v)); }
    STDMETHODIMP CopyAllItems(IMFAttributes *d) override { FWD(CopyAllItems(d)); }

private:
    ~Activator() {
        SafeRelease(source_);
        SafeRelease(attrs_);
        InterlockedDecrement(&g_objects);
    }
    LONG ref_ = 1;
    IMFAttributes *attrs_ = nullptr;
    Source *source_ = nullptr;
};

// ---- COM plumbing -------------------------------------------------------------------------------

class ClassFactory final : public IClassFactory {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void **ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = this; return S_OK; }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown *outer, REFIID riid, void **ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        Activator *a = new (nothrow_) Activator();
        if (!a) return E_OUTOFMEMORY;
        HRESULT hr = a->Init();
        if (SUCCEEDED(hr)) hr = a->QueryInterface(riid, ppv);
        a->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override {
        if (lock) InterlockedIncrement(&g_objects); else InterlockedDecrement(&g_objects);
        return S_OK;
    }
};

static ClassFactory g_factory;

extern "C" BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = inst;
        DisableThreadLibraryCalls(inst);
    }
    return TRUE;
}

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void **ppv) {
    if (clsid != CLSID_DwsVirtualCamera) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

extern "C" HRESULT WINAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

static const wchar_t *kClsidKey = L"Software\\Classes\\CLSID\\" DWS_CLSID_STRING;

// Registers the media source for all users (HKLM); needs an elevated caller.
extern "C" HRESULT WINAPI DllRegisterServer() {
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    HKEY key;
    wchar_t sub[256];
    wsprintfW(sub, L"%s\\InprocServer32", kClsidKey);
    LSTATUS st = RegCreateKeyExW(HKEY_LOCAL_MACHINE, sub, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (st != ERROR_SUCCESS) return HRESULT_FROM_WIN32(st);
    RegSetValueExW(key, nullptr, 0, REG_SZ, (const BYTE *)path, (DWORD)((lstrlenW(path) + 1) * sizeof(wchar_t)));
    const wchar_t *model = L"Both";
    RegSetValueExW(key, L"ThreadingModel", 0, REG_SZ, (const BYTE *)model, (DWORD)((lstrlenW(model) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, KEY_WRITE, &key) == ERROR_SUCCESS) {
        RegSetValueExW(key, nullptr, 0, REG_SZ, (const BYTE *)DWS_CAMERA_NAME, sizeof(DWS_CAMERA_NAME));
        RegCloseKey(key);
    }
    return S_OK;
}

extern "C" HRESULT WINAPI DllUnregisterServer() {
    LSTATUS st = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
    return st == ERROR_SUCCESS || st == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(st);
}

// ---- helpers called by the app -------------------------------------------------------------------

// Registers "DSLR Webcam Studio" with Windows' virtual camera API and starts it.
// lifetime: 0 = while the app runs (session), 1 = until removed (system). Returns the camera object.
extern "C" HRESULT WINAPI DwsVCamCreate(int lifetime, IUnknown **camera) {
    if (!camera) return E_POINTER;
    *camera = nullptr;
    MFStartup(MF_VERSION, MFSTARTUP_LITE);
    HMODULE lib = LoadLibraryW(L"mfsensorgroup.dll");
    if (!lib) return HRESULT_FROM_WIN32(GetLastError());
    auto create = (PFN_MFCreateVirtualCamera)GetProcAddress(lib, "MFCreateVirtualCamera");
    if (!create) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND); // Windows 10 or older
    IDwsMFVirtualCamera *vc = nullptr;
    HRESULT hr = create(0 /* software camera source */, lifetime, 0 /* current user */, DWS_CAMERA_NAME,
                        DWS_CLSID_STRING, nullptr, 0, &vc);
    if (SUCCEEDED(hr)) hr = vc->Start(nullptr);
    if (SUCCEEDED(hr)) *camera = vc; else SafeRelease(vc);
    return hr;
}

extern "C" HRESULT WINAPI DwsVCamRemove(IUnknown *camera) {
    if (!camera) return E_POINTER;
    return static_cast<IDwsMFVirtualCamera *>(camera)->Remove();
}
