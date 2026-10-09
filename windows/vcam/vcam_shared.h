// Shared-memory contract between DSLR Webcam Studio (writer) and the virtual camera media
// source running inside Windows' Camera Frame Server (reader). The C# side mirrors these
// values in src/VirtualCamera.cs; keep them in sync.
#pragma once
#include <stdint.h>

// Created by the media source (Frame Server has the privilege to create Global\ objects);
// the app opens it for writing while a camera consumer is active.
#define DWS_SECTION_NAME L"Global\\DSLRWebcamStudio_VirtualCamera_v1"
#define DWS_SECTION_NAME_LOCAL L"Local\\DSLRWebcamStudio_VirtualCamera_v1"
#define DWS_MAGIC 0x4D414344u /* "DCAM" */
#define DWS_VERSION 1u
#define DWS_MAX_W 1920
#define DWS_MAX_H 1080
#define DWS_HEADER_SIZE 64
#define DWS_SECTION_SIZE (DWS_HEADER_SIZE + DWS_MAX_W * DWS_MAX_H * 4)

// Pixels follow the header: BGRA (32-bit), top-down, stride = width * 4.
// Writes use a sequence lock: seq is odd while a frame is being written.
typedef struct {
    uint32_t magic;
    uint32_t version;
    volatile int32_t seq;
    uint32_t width;
    uint32_t height;
    uint32_t fps;
    int64_t written_at_ms;   // GetTickCount64() when the last frame finished
    uint32_t reserved[8];
} DwsSharedHeader;

// Friendly name and source id the camera is registered with.
#define DWS_CAMERA_NAME L"DSLR Webcam Studio"
#define DWS_CLSID_STRING L"{8B5C6FE4-7003-435D-B12F-2E76435ADEBA}"
