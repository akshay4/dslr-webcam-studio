// PTP over USB (still-image class, bulk endpoints) with libusb, plus the Canon EOS
// live-view sequence: SetRemoteMode -> SetEventMode -> EVF output = PC -> GetViewFinderData.
#include "camera.h"

#include <libusb.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define CANON_VID 0x04A9
#define USB_CLASS_STILL_IMAGE 6
#define TIMEOUT_MS 5000

enum { PTP_TYPE_COMMAND = 1, PTP_TYPE_DATA = 2, PTP_TYPE_RESPONSE = 3 };

struct camera {
    libusb_context *ctx;
    libusb_device_handle *h;
    int iface;
    uint8_t ep_in, ep_out;
    int max_packet_out, max_packet_in;
    uint32_t tid;
    bool session_open, live_view_on;
    ptp_devinfo devinfo;
    ptp_props props;
    double last_event_poll;
    uint8_t *rx;          // receive buffer
    size_t rx_cap;
};

static double now_s(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

static int bulk_write(camera *c, const uint8_t *buf, size_t len)
{
    int sent = 0;
    int r = libusb_bulk_transfer(c->h, c->ep_out, (uint8_t *)buf, (int)len, &sent, TIMEOUT_MS);
    if (r == LIBUSB_ERROR_PIPE) { libusb_clear_halt(c->h, c->ep_out); return r; }
    if (r == 0 && (size_t)sent != len) return LIBUSB_ERROR_IO;
    if (r == 0 && len % c->max_packet_out == 0) libusb_bulk_transfer(c->h, c->ep_out, (uint8_t *)buf, 0, &sent, TIMEOUT_MS); // ZLP
    return r;
}

static int bulk_read(camera *c, uint8_t *buf, size_t cap, int *got)
{
    int r = libusb_bulk_transfer(c->h, c->ep_in, buf, (int)cap, got, TIMEOUT_MS);
    if (r == LIBUSB_ERROR_PIPE) libusb_clear_halt(c->h, c->ep_in);
    return r;
}

static bool ensure_rx(camera *c, size_t need)
{
    if (c->rx_cap >= need) return true;
    size_t cap = c->rx_cap ? c->rx_cap : 1 << 20;
    while (cap < need) cap *= 2;
    uint8_t *p = realloc(c->rx, cap);
    if (!p) return false;
    c->rx = p;
    c->rx_cap = cap;
    return true;
}

static void parse_response(const uint8_t *b, size_t n, ptp_resp *resp)
{
    memset(resp, 0, sizeof *resp);
    resp->code = rd16(b + 6);
    uint32_t len = rd32(b);
    if (len > n) len = (uint32_t)n;
    resp->nparams = len > 12 ? (int)((len - 12) / 4) : 0;
    if (resp->nparams > 5) resp->nparams = 5;
    for (int i = 0; i < resp->nparams; i++) resp->params[i] = rd32(b + 12 + 4 * i);
}

// One PTP transaction. out/outlen: data phase to send (or NULL). in/inlen: receives a malloc'd
// copy of the data phase (or NULL to discard). Returns 0 on success (check resp->code), <0 on USB error.
static int transaction(camera *c, uint16_t code, const uint32_t *params, int np,
                       const uint8_t *out, size_t outlen, uint8_t **in, size_t *inlen, ptp_resp *resp)
{
    if (in) { *in = NULL; *inlen = 0; }
    uint32_t tid = code == PTP_OC_OpenSession ? 0 : c->tid++;

    uint8_t cmd[32];
    uint32_t clen = 12 + 4 * (uint32_t)np;
    wr32(cmd, clen); wr16(cmd + 4, PTP_TYPE_COMMAND); wr16(cmd + 6, code); wr32(cmd + 8, tid);
    for (int i = 0; i < np; i++) wr32(cmd + 12 + 4 * i, params[i]);
    int r = bulk_write(c, cmd, clen);
    if (r) return r;

    if (out) {
        uint8_t *d = malloc(12 + outlen);
        if (!d) return LIBUSB_ERROR_NO_MEM;
        wr32(d, 12 + (uint32_t)outlen); wr16(d + 4, PTP_TYPE_DATA); wr16(d + 6, code); wr32(d + 8, tid);
        memcpy(d + 12, out, outlen);
        r = bulk_write(c, d, 12 + outlen);
        free(d);
        if (r) return r;
    }

    // Read containers until the response arrives; a data container may come first.
    for (;;) {
        if (!ensure_rx(c, 1 << 20)) return LIBUSB_ERROR_NO_MEM;
        int got = 0;
        r = bulk_read(c, c->rx, c->rx_cap, &got);
        if (r) return r;
        if (got == 0) continue; // zero-length packet after a data phase
        if (got < 12) return LIBUSB_ERROR_IO;
        uint32_t len = rd32(c->rx);
        uint16_t type = rd16(c->rx + 4);
        if (type == PTP_TYPE_RESPONSE) {
            parse_response(c->rx, (size_t)got, resp);
            return 0;
        }
        if (type != PTP_TYPE_DATA) return LIBUSB_ERROR_IO;

        size_t total = len, have = (size_t)got;
        if (!ensure_rx(c, total + 512)) return LIBUSB_ERROR_NO_MEM;
        while (have < total) {
            size_t want = c->rx_cap - have;
            want -= want % (size_t)c->max_packet_in; // keep reads packet-aligned
            r = bulk_read(c, c->rx + have, want, &got);
            if (r) return r;
            if (got == 0) break;
            have += (size_t)got;
        }
        if (in && total > 12) {
            *inlen = total - 12;
            *in = malloc(*inlen);
            if (!*in) return LIBUSB_ERROR_NO_MEM;
            memcpy(*in, c->rx + 12, *inlen);
        }
    }
}

static uint16_t simple(camera *c, uint16_t code, uint32_t p1, int np)
{
    ptp_resp resp;
    uint32_t params[1] = {p1};
    return transaction(c, code, params, np, NULL, 0, NULL, NULL, &resp) == 0 ? resp.code : 0;
}

static uint16_t set_prop(camera *c, uint32_t prop, uint32_t value)
{
    uint8_t d[12];
    wr32(d, 12); wr32(d + 4, prop); wr32(d + 8, value);
    ptp_resp resp;
    return transaction(c, PTP_OC_EOS_SetDevicePropValueEx, NULL, 0, d, sizeof d, NULL, NULL, &resp) == 0 ? resp.code : 0;
}

// Canon queues property/state events after remote mode; reading them keeps the camera responsive.
static void drain_events(camera *c)
{
    uint8_t *data;
    size_t len;
    ptp_resp resp;
    if (transaction(c, PTP_OC_EOS_GetEvent, NULL, 0, NULL, 0, &data, &len, &resp) == 0 && resp.code == PTP_RC_OK)
        ptp_parse_events(data, len, &c->props);
    free(data);
    c->last_event_poll = now_s();
}

static bool find_endpoints(libusb_device *dev, int *iface, uint8_t *in, uint8_t *out, int *mpin, int *mpout)
{
    struct libusb_config_descriptor *cfg;
    if (libusb_get_active_config_descriptor(dev, &cfg) != 0) return false;
    bool found = false;
    for (int i = 0; i < cfg->bNumInterfaces && !found; i++) {
        const struct libusb_interface_descriptor *alt = &cfg->interface[i].altsetting[0];
        if (alt->bInterfaceClass != USB_CLASS_STILL_IMAGE) continue;
        *in = *out = 0;
        for (int e = 0; e < alt->bNumEndpoints; e++) {
            const struct libusb_endpoint_descriptor *ep = &alt->endpoint[e];
            if ((ep->bmAttributes & 3) != LIBUSB_TRANSFER_TYPE_BULK) continue;
            if (ep->bEndpointAddress & LIBUSB_ENDPOINT_IN) { *in = ep->bEndpointAddress; *mpin = ep->wMaxPacketSize; }
            else { *out = ep->bEndpointAddress; *mpout = ep->wMaxPacketSize; }
        }
        if (*in && *out) { *iface = alt->bInterfaceNumber; found = true; }
    }
    libusb_free_config_descriptor(cfg);
    return found;
}

camera *camera_open(char *err, size_t errlen)
{
    camera *c = calloc(1, sizeof *c);
    if (!c) { snprintf(err, errlen, "Out of memory."); return NULL; }
    if (libusb_init(&c->ctx) != 0) { snprintf(err, errlen, "Could not initialise libusb."); free(c); return NULL; }

    libusb_device **list;
    ssize_t n = libusb_get_device_list(c->ctx, &list);
    libusb_device *dev = NULL;
    for (ssize_t i = 0; i < n && !dev; i++) {
        struct libusb_device_descriptor dd;
        if (libusb_get_device_descriptor(list[i], &dd) == 0 && dd.idVendor == CANON_VID &&
            find_endpoints(list[i], &c->iface, &c->ep_in, &c->ep_out, &c->max_packet_in, &c->max_packet_out))
            dev = libusb_ref_device(list[i]);
    }
    if (n >= 0) libusb_free_device_list(list, 1);
    if (!dev) {
        snprintf(err, errlen, "No Canon camera found. Connect your Canon EOS camera over USB and switch it on.");
        camera_close(c);
        return NULL;
    }

    int r = libusb_open(dev, &c->h);
    libusb_unref_device(dev);
    if (r == LIBUSB_ERROR_ACCESS) {
        snprintf(err, errlen, "No permission to use the camera. Install the udev rule: "
                              "sudo cp 60-dslr-webcam-studio.rules /etc/udev/rules.d/ && sudo udevadm control --reload, then replug the camera.");
        camera_close(c);
        return NULL;
    }
    if (r) { snprintf(err, errlen, "Could not open the camera: %s", libusb_strerror(r)); camera_close(c); return NULL; }
    libusb_set_auto_detach_kernel_driver(c->h, 1);
    r = libusb_claim_interface(c->h, c->iface);
    if (r) {
        snprintf(err, errlen, r == LIBUSB_ERROR_BUSY
                 ? "Another program is using the camera (often the desktop's photo importer, gvfs-gphoto2). "
                   "Unmount the camera in your file manager or run: gio mount -s gphoto2"
                 : "Could not claim the camera: %s", libusb_strerror(r));
        libusb_close(c->h);
        c->h = NULL;
        camera_close(c);
        return NULL;
    }
    c->tid = 1;

    char tmp[16];
    uint16_t rc = simple(c, PTP_OC_OpenSession, 1, 1);
    if (rc == PTP_RC_SessionAlreadyOpen) { simple(c, PTP_OC_CloseSession, 0, 0); rc = simple(c, PTP_OC_OpenSession, 1, 1); }
    if (rc != PTP_RC_OK) { snprintf(err, errlen, "OpenSession failed: %s", ptp_rc_name(rc, tmp)); camera_close(c); return NULL; }
    c->session_open = true;

    uint8_t *di;
    size_t dilen;
    ptp_resp resp;
    if (transaction(c, PTP_OC_GetDeviceInfo, NULL, 0, NULL, 0, &di, &dilen, &resp) == 0 && resp.code == PTP_RC_OK)
        ptp_parse_devinfo(di, dilen, &c->devinfo);
    free(di);
    if (c->devinfo.nops > 0 && !ptp_devinfo_has_op(&c->devinfo, PTP_OC_EOS_GetViewFinderData)) {
        snprintf(err, errlen, "%s does not support live view over USB (no GetViewFinderData).", c->devinfo.model);
        camera_close(c);
        return NULL;
    }

    if ((rc = simple(c, PTP_OC_EOS_SetRemoteMode, 1, 1)) != PTP_RC_OK ||
        (rc = simple(c, PTP_OC_EOS_SetEventMode, 1, 1)) != PTP_RC_OK) {
        snprintf(err, errlen, "Camera refused remote mode: %s", ptp_rc_name(rc, tmp));
        camera_close(c);
        return NULL;
    }
    drain_events(c);
    set_prop(c, DPC_EOS_EVFMode, 1); // read-only on some bodies; ignore the result
    if ((rc = set_prop(c, DPC_EOS_EVFOutputDevice, EVF_OUTPUT_PC)) != PTP_RC_OK) {
        snprintf(err, errlen, "Could not start live view: %s. Enable Live View in the camera menu and set the mode dial to P/Tv/Av/M or movie.",
                 ptp_rc_name(rc, tmp));
        camera_close(c);
        return NULL;
    }
    c->live_view_on = true;
    drain_events(c);
    return c;
}

int camera_read_jpeg(camera *c, uint8_t **jpeg, size_t *len, char *err, size_t errlen)
{
    *jpeg = NULL;
    *len = 0;
    if (now_s() - c->last_event_poll > 0.5) drain_events(c);
    uint8_t *data;
    size_t n;
    ptp_resp resp;
    uint32_t p = 0x00100000;
    int r = transaction(c, PTP_OC_EOS_GetViewFinderData, &p, 1, NULL, 0, &data, &n, &resp);
    if (r) { snprintf(err, errlen, "USB error: %s", libusb_strerror(r)); return -1; }
    if (resp.code == PTP_RC_CANON_NotReady || resp.code == PTP_RC_DeviceBusy) { free(data); return 0; }
    if (resp.code != PTP_RC_OK) {
        char tmp[16];
        snprintf(err, errlen, "GetViewFinderData failed: %s", ptp_rc_name(resp.code, tmp));
        free(data);
        return -1;
    }
    size_t off, l;
    if (!ptp_extract_jpeg(data, n, &off, &l)) { free(data); return 0; }
    *jpeg = malloc(l);
    if (*jpeg) { memcpy(*jpeg, data + off, l); *len = l; }
    free(data);
    return *jpeg ? 1 : -1;
}

uint16_t camera_set_property(camera *c, uint32_t prop, uint32_t value)
{
    uint16_t rc = set_prop(c, prop, value);
    drain_events(c);
    return rc;
}

bool camera_prop(camera *c, uint32_t prop, uint32_t *value) { return ptp_props_get(&c->props, prop, value); }
const char *camera_model(camera *c) { return c->devinfo.model[0] ? c->devinfo.model : "Canon camera"; }
const ptp_devinfo *camera_devinfo(camera *c) { return &c->devinfo; }

void camera_close(camera *c)
{
    if (!c) return;
    if (c->h && c->session_open) {
        if (c->live_view_on) set_prop(c, DPC_EOS_EVFOutputDevice, EVF_OUTPUT_OFF);
        simple(c, PTP_OC_EOS_SetEventMode, 0, 1);
        simple(c, PTP_OC_EOS_SetRemoteMode, 0, 1);
        simple(c, PTP_OC_CloseSession, 0, 0);
    }
    if (c->h) {
        libusb_release_interface(c->h, c->iface);
        libusb_close(c->h);
    }
    if (c->ctx) libusb_exit(c->ctx);
    free(c->rx);
    free(c);
}
