// PTP / Canon EOS protocol constants and pure parsing helpers (no I/O).
#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

enum {
    PTP_OC_GetDeviceInfo = 0x1001,
    PTP_OC_OpenSession = 0x1002,
    PTP_OC_CloseSession = 0x1003,
    PTP_OC_EOS_SetDevicePropValueEx = 0x9110,
    PTP_OC_EOS_SetRemoteMode = 0x9114,
    PTP_OC_EOS_SetEventMode = 0x9115,
    PTP_OC_EOS_GetEvent = 0x9116,
    PTP_OC_EOS_GetViewFinderData = 0x9153,
};

enum {
    PTP_RC_OK = 0x2001,
    PTP_RC_DeviceBusy = 0x2019,
    PTP_RC_SessionAlreadyOpen = 0x201E,
    PTP_RC_CANON_NotReady = 0xA102,
};

enum {
    DPC_EOS_Aperture = 0xD101,
    DPC_EOS_ShutterSpeed = 0xD102,
    DPC_EOS_ISOSpeed = 0xD103,
    DPC_EOS_AutoExposureMode = 0xD105,
    DPC_EOS_EVFOutputDevice = 0xD1B0,
    DPC_EOS_EVFMode = 0xD1B3,
};

enum { EVF_OUTPUT_OFF = 0, EVF_OUTPUT_TFT = 1, EVF_OUTPUT_PC = 2 };

typedef struct {
    uint16_t code;
    uint32_t params[5];
    int nparams;
} ptp_resp;

typedef struct {
    uint32_t code[256];
    uint32_t value[256];
    int count;
} ptp_props;

typedef struct {
    char manufacturer[64], model[64], version[32];
    uint16_t ops[512];
    int nops;
} ptp_devinfo;

const char *ptp_rc_name(uint16_t rc, char tmp[16]);

// Live-view data is a series of [u32 length][u32 type][payload] blocks; type 1 is the JPEG.
// Falls back to scanning for JPEG SOI/EOI markers. Returns false if no JPEG was found.
bool ptp_extract_jpeg(const uint8_t *d, size_t n, size_t *off, size_t *len);

// Canon event data: [u32 size][u32 type][payload] records; 0xC189 = property changed [u32 prop][u32 value].
void ptp_parse_events(const uint8_t *d, size_t n, ptp_props *props);
void ptp_props_set(ptp_props *p, uint32_t code, uint32_t value);
bool ptp_props_get(const ptp_props *p, uint32_t code, uint32_t *value);

bool ptp_parse_devinfo(const uint8_t *d, size_t n, ptp_devinfo *out);
bool ptp_devinfo_has_op(const ptp_devinfo *di, uint16_t op);

// Human-readable names for Canon exposure codes (returns a static string or writes hex into tmp).
const char *canon_iso_name(uint32_t v, char tmp[16]);
const char *canon_shutter_name(uint32_t v, char tmp[16]);
const char *canon_aperture_name(uint32_t v, char tmp[16]);
const char *canon_mode_name(uint32_t v, char tmp[16]);
bool canon_iso_settable(uint32_t mode);
bool canon_shutter_settable(uint32_t mode);

typedef struct { uint32_t code; const char *name; } canon_choice;
extern const canon_choice CANON_ISO_CHOICES[];
extern const int CANON_ISO_CHOICES_N;
extern const canon_choice CANON_SHUTTER_CHOICES[];
extern const int CANON_SHUTTER_CHOICES_N;

static inline uint16_t rd16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static inline uint32_t rd32(const uint8_t *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static inline void wr16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static inline void wr32(uint8_t *p, uint32_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24); }
