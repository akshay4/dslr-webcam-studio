// Linux virtual camera: writes output frames to a v4l2loopback device so OBS, browsers,
// Zoom etc. can use them as a webcam. Setup (once):
//   sudo apt install v4l2loopback-dkms
//   sudo modprobe v4l2loopback exclusive_caps=1 card_label="DSLR Webcam Studio"
#pragma once
#include <stdbool.h>
#include <stddef.h>

#include "image.h"

typedef struct vcam vcam;

// Finds the v4l2loopback device (preferring one labelled "DSLR Webcam Studio") and sets its format.
// Returns NULL with a user-facing message in err when none is available.
vcam *vcam_open(int width, int height, char *err, size_t errlen);
bool vcam_write(vcam *v, const frame_t *f);  // converts BGRA to YUYV
const char *vcam_device(vcam *v);
void vcam_close(vcam *v);
