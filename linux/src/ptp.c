#include "ptp.h"

#include <stdio.h>
#include <string.h>

const char *ptp_rc_name(uint16_t rc, char tmp[16])
{
    switch (rc) {
    case 0x2001: return "OK";
    case 0x2002: return "GeneralError";
    case 0x2003: return "SessionNotOpen";
    case 0x2005: return "OperationNotSupported";
    case 0x2019: return "DeviceBusy";
    case 0x201D: return "InvalidParameter";
    case 0x201E: return "SessionAlreadyOpen";
    case 0xA102: return "Canon NotReady";
    default: snprintf(tmp, 16, "0x%04X", rc); return tmp;
    }
}

bool ptp_extract_jpeg(const uint8_t *d, size_t n, size_t *off, size_t *len)
{
    if (!d || n < 4) return false;
    size_t pos = 0;
    while (pos + 8 <= n) {
        uint32_t l = rd32(d + pos), type = rd32(d + pos + 4);
        if (l < 8 || pos + l > n) break;
        if (type == 1 && l > 10 && d[pos + 8] == 0xFF && d[pos + 9] == 0xD8) {
            *off = pos + 8;
            *len = l - 8;
            return true;
        }
        pos += l;
    }
    for (size_t i = 0; i + 1 < n; i++) {
        if (d[i] != 0xFF || d[i + 1] != 0xD8) continue;
        for (size_t e = n - 2; e > i; e--) {
            if (d[e] == 0xFF && d[e + 1] == 0xD9) {
                *off = i;
                *len = e + 2 - i;
                return true;
            }
        }
        break;
    }
    return false;
}

void ptp_props_set(ptp_props *p, uint32_t code, uint32_t value)
{
    for (int i = 0; i < p->count; i++)
        if (p->code[i] == code) { p->value[i] = value; return; }
    if (p->count < 256) {
        p->code[p->count] = code;
        p->value[p->count] = value;
        p->count++;
    }
}

bool ptp_props_get(const ptp_props *p, uint32_t code, uint32_t *value)
{
    for (int i = 0; i < p->count; i++)
        if (p->code[i] == code) { *value = p->value[i]; return true; }
    return false;
}

void ptp_parse_events(const uint8_t *d, size_t n, ptp_props *props)
{
    if (!d) return;
    size_t pos = 0;
    while (pos + 8 <= n) {
        uint32_t size = rd32(d + pos), type = rd32(d + pos + 4);
        if (size < 8 || pos + size > n || type == 0) break;
        if (type == 0xC189 && size >= 16) ptp_props_set(props, rd32(d + pos + 8), rd32(d + pos + 12));
        pos += size;
    }
}

// PTP string: u8 character count (including the terminating NUL), then UTF-16LE.
static bool rd_str(const uint8_t *d, size_t n, size_t *pos, char *out, size_t outlen)
{
    if (*pos >= n) return false;
    size_t chars = d[(*pos)++];
    if (*pos + chars * 2 > n) return false;
    size_t o = 0;
    for (size_t i = 0; i < chars; i++) {
        uint16_t c = rd16(d + *pos + i * 2);
        if (c == 0) break;
        if (o + 1 < outlen) out[o++] = c < 128 ? (char)c : '?';
    }
    if (outlen) out[o] = 0;
    *pos += chars * 2;
    return true;
}

static bool rd_u16_array(const uint8_t *d, size_t n, size_t *pos, uint16_t *out, int max, int *count)
{
    if (*pos + 4 > n) return false;
    uint32_t c = rd32(d + *pos);
    *pos += 4;
    if (*pos + (size_t)c * 2 > n) return false;
    int kept = 0;
    for (uint32_t i = 0; i < c; i++) {
        if (out && kept < max) out[kept++] = rd16(d + *pos);
        *pos += 2;
    }
    if (count) *count = kept;
    return true;
}

bool ptp_parse_devinfo(const uint8_t *d, size_t n, ptp_devinfo *out)
{
    memset(out, 0, sizeof *out);
    size_t pos = 0;
    char skip[128];
    if (n < 8) return false;
    pos += 2 + 4 + 2;                                                     // StandardVersion, VendorExtensionID, Version
    if (!rd_str(d, n, &pos, skip, sizeof skip)) return false;             // VendorExtensionDesc
    pos += 2;                                                             // FunctionalMode
    if (!rd_u16_array(d, n, &pos, out->ops, 512, &out->nops)) return false;
    for (int i = 0; i < 4; i++)                                           // Events, Properties, Capture, Image formats
        if (!rd_u16_array(d, n, &pos, NULL, 0, NULL)) return false;
    return rd_str(d, n, &pos, out->manufacturer, sizeof out->manufacturer)
        && rd_str(d, n, &pos, out->model, sizeof out->model)
        && rd_str(d, n, &pos, out->version, sizeof out->version);
}

bool ptp_devinfo_has_op(const ptp_devinfo *di, uint16_t op)
{
    for (int i = 0; i < di->nops; i++)
        if (di->ops[i] == op) return true;
    return false;
}

// ---- Canon exposure value names -----------------------------------------

typedef struct { uint32_t k; const char *v; } kv;

static const kv ISO[] = {
    {0x00, "Auto"}, {0x48, "100"}, {0x4B, "125"}, {0x4D, "160"}, {0x50, "200"}, {0x53, "250"}, {0x55, "320"},
    {0x58, "400"}, {0x5B, "500"}, {0x5D, "640"}, {0x60, "800"}, {0x63, "1000"}, {0x65, "1250"}, {0x68, "1600"},
    {0x6B, "2000"}, {0x6D, "2500"}, {0x70, "3200"}, {0x73, "4000"}, {0x75, "5000"}, {0x78, "6400"},
    {0x7B, "8000"}, {0x7D, "10000"}, {0x80, "12800"},
};
static const kv SHUTTER[] = {
    {0x0C, "bulb"}, {0x48, "1/4"}, {0x4B, "1/5"}, {0x4D, "1/6"}, {0x50, "1/8"}, {0x53, "1/10"}, {0x55, "1/13"},
    {0x58, "1/15"}, {0x5B, "1/20"}, {0x5D, "1/25"}, {0x60, "1/30"}, {0x63, "1/40"}, {0x65, "1/50"}, {0x68, "1/60"},
    {0x6B, "1/80"}, {0x6D, "1/100"}, {0x70, "1/125"}, {0x73, "1/160"}, {0x75, "1/200"}, {0x78, "1/250"},
    {0x7B, "1/320"}, {0x7D, "1/400"}, {0x80, "1/500"}, {0x83, "1/640"}, {0x85, "1/800"}, {0x88, "1/1000"},
    {0x8B, "1/1250"}, {0x8D, "1/1600"}, {0x90, "1/2000"}, {0x93, "1/2500"}, {0x95, "1/3200"}, {0x98, "1/4000"},
};
static const kv APERTURE[] = {
    {0x18, "f/2.0"}, {0x1B, "f/2.2"}, {0x1C, "f/2.5"}, {0x1D, "f/2.5"}, {0x20, "f/2.8"}, {0x23, "f/3.2"},
    {0x24, "f/3.5"}, {0x25, "f/3.5"}, {0x28, "f/4.0"}, {0x2B, "f/4.5"}, {0x2C, "f/4.5"}, {0x2D, "f/5.0"},
    {0x30, "f/5.6"}, {0x33, "f/6.3"}, {0x34, "f/6.7"}, {0x35, "f/7.1"}, {0x38, "f/8.0"}, {0x3B, "f/9.0"},
    {0x3C, "f/9.5"}, {0x3D, "f/10"}, {0x40, "f/11"}, {0x43, "f/13"}, {0x45, "f/14"}, {0x48, "f/16"},
    {0x4B, "f/18"}, {0x4D, "f/20"}, {0x50, "f/22"},
};
static const kv MODES[] = {
    {0, "P"}, {1, "Tv"}, {2, "Av"}, {3, "M"}, {4, "Bulb"}, {5, "A-DEP"}, {9, "Auto"}, {10, "Night portrait"},
    {11, "Sports"}, {12, "Portrait"}, {13, "Landscape"}, {14, "Close-up"}, {15, "Flash off"},
    {19, "Creative Auto"}, {20, "Movie"}, {22, "A+ (Scene Intelligent Auto)"},
};

static const char *lookup(const kv *t, size_t n, uint32_t v, char tmp[16])
{
    for (size_t i = 0; i < n; i++)
        if (t[i].k == v) return t[i].v;
    snprintf(tmp, 16, "0x%X", v);
    return tmp;
}

#define N(a) (sizeof(a) / sizeof((a)[0]))
const char *canon_iso_name(uint32_t v, char tmp[16]) { return lookup(ISO, N(ISO), v, tmp); }
const char *canon_shutter_name(uint32_t v, char tmp[16]) { return lookup(SHUTTER, N(SHUTTER), v, tmp); }
const char *canon_aperture_name(uint32_t v, char tmp[16]) { return lookup(APERTURE, N(APERTURE), v, tmp); }
const char *canon_mode_name(uint32_t v, char tmp[16]) { return lookup(MODES, N(MODES), v, tmp); }

// ISO can be set in P/Tv/Av/M (and movie mode with manual exposure); shutter only in Tv/M/movie.
bool canon_iso_settable(uint32_t mode) { return mode <= 3 || mode == 20; }
bool canon_shutter_settable(uint32_t mode) { return mode == 1 || mode == 3 || mode == 20; }

const canon_choice CANON_ISO_CHOICES[] = {
    {0x00, "Auto"}, {0x48, "100"}, {0x50, "200"}, {0x58, "400"}, {0x60, "800"}, {0x68, "1600"}, {0x70, "3200"}, {0x78, "6400"},
};
const int CANON_ISO_CHOICES_N = (int)N(CANON_ISO_CHOICES);
const canon_choice CANON_SHUTTER_CHOICES[] = {
    {0x60, "1/30"}, {0x63, "1/40"}, {0x65, "1/50"}, {0x68, "1/60"}, {0x6B, "1/80"}, {0x6D, "1/100"}, {0x70, "1/125"},
};
const int CANON_SHUTTER_CHOICES_N = (int)N(CANON_SHUTTER_CHOICES);
