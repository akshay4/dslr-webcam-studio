// Canon EOS live view over USB (libusb + PTP). Use one camera from a single thread.
#pragma once
#include "ptp.h"

typedef struct camera camera;

// Finds the first Canon camera, opens a PTP session and routes live view to the PC.
// On failure returns NULL and writes a user-facing message into err.
camera *camera_open(char *err, size_t errlen);

// Downloads one live-view JPEG into a malloc'd buffer.
// Returns 1 with a frame, 0 if the camera has no new frame yet, -1 on error.
int camera_read_jpeg(camera *c, uint8_t **jpeg, size_t *len, char *err, size_t errlen);

// Turns live view back on after the camera dropped it. Returns 0 on success.
int camera_restart_live_view(camera *c);

// The camera's live view output device as last reported (or as we last set it), -1 if unknown.
int64_t camera_evf_output(camera *c);

// Sets an exposure property (ISO, shutter, ...); returns the PTP response code (0 on transport error).
uint16_t camera_set_property(camera *c, uint32_t prop, uint32_t value);

bool camera_prop(camera *c, uint32_t prop, uint32_t *value);
const char *camera_model(camera *c);
const ptp_devinfo *camera_devinfo(camera *c);

// Hands live view back to the camera and releases the USB device.
void camera_close(camera *c);
