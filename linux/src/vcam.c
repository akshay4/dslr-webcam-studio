#include "vcam.h"

#include <dirent.h>
#include <fcntl.h>
#include <linux/videodev2.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

struct vcam {
    int fd, w, h;
    char path[64];
    uint8_t *buf;
};

static bool is_loopback(int fd, bool *ours)
{
    struct v4l2_capability cap;
    memset(&cap, 0, sizeof cap);
    if (ioctl(fd, VIDIOC_QUERYCAP, &cap) < 0) return false;
    *ours = strstr((const char *)cap.card, "DSLR Webcam Studio") != NULL;
    return strstr((const char *)cap.driver, "v4l2 loopback") != NULL;
}

vcam *vcam_open(int width, int height, char *err, size_t errlen)
{
    char best[64] = "";
    DIR *d = opendir("/dev");
    if (d) {
        struct dirent *e;
        while ((e = readdir(d))) {
            if (strncmp(e->d_name, "video", 5) != 0) continue;
            char path[64];
            snprintf(path, sizeof path, "/dev/%s", e->d_name);
            int fd = open(path, O_RDWR | O_NONBLOCK);
            if (fd < 0) continue;
            bool ours = false;
            bool loop = is_loopback(fd, &ours);
            close(fd);
            if (loop && (ours || !best[0])) snprintf(best, sizeof best, "%s", path);
            if (loop && ours) break; // a device labelled for us wins
        }
        closedir(d);
    }
    if (!best[0]) {
        snprintf(err, errlen, "not set up (install v4l2loopback, see README)");
        return NULL;
    }

    int fd = open(best, O_RDWR);
    if (fd < 0) { snprintf(err, errlen, "cannot open %s", best); return NULL; }
    struct v4l2_format fmt;
    memset(&fmt, 0, sizeof fmt);
    fmt.type = V4L2_BUF_TYPE_VIDEO_OUTPUT;
    fmt.fmt.pix.width = (unsigned)width;
    fmt.fmt.pix.height = (unsigned)height;
    fmt.fmt.pix.pixelformat = V4L2_PIX_FMT_YUYV;
    fmt.fmt.pix.field = V4L2_FIELD_NONE;
    fmt.fmt.pix.bytesperline = (unsigned)width * 2;
    fmt.fmt.pix.sizeimage = (unsigned)(width * height * 2);
    fmt.fmt.pix.colorspace = V4L2_COLORSPACE_SRGB;
    if (ioctl(fd, VIDIOC_S_FMT, &fmt) < 0) {
        snprintf(err, errlen, "%s is in use by another program", best);
        close(fd);
        return NULL;
    }
    vcam *v = calloc(1, sizeof *v);
    v->fd = fd;
    v->w = width;
    v->h = height;
    snprintf(v->path, sizeof v->path, "%s", best);
    v->buf = malloc((size_t)width * height * 2);
    return v;
}

bool vcam_write(vcam *v, const frame_t *f)
{
    if (!v || f->w != v->w || f->h != v->h) return false;
    uint8_t *o = v->buf;
    for (int y = 0; y < f->h; y++) {
        const uint32_t *p = f->px + (size_t)y * f->w;
        for (int x = 0; x + 1 < f->w; x += 2, o += 4) {
            int r0 = p[x] >> 16 & 255, g0 = p[x] >> 8 & 255, b0 = p[x] & 255;
            int r1 = p[x + 1] >> 16 & 255, g1 = p[x + 1] >> 8 & 255, b1 = p[x + 1] & 255;
            int r = (r0 + r1) >> 1, g = (g0 + g1) >> 1, b = (b0 + b1) >> 1;
            o[0] = (uint8_t)(((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16); // BT.601 limited range
            o[1] = (uint8_t)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
            o[2] = (uint8_t)(((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16);
            o[3] = (uint8_t)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
        }
    }
    size_t n = (size_t)v->w * v->h * 2;
    return write(v->fd, v->buf, n) == (ssize_t)n;
}

const char *vcam_device(vcam *v) { return v ? v->path : ""; }

void vcam_close(vcam *v)
{
    if (!v) return;
    close(v->fd);
    free(v->buf);
    free(v);
}
