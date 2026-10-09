// Frames, geometry, bicubic scaling with mirroring, and live-view cleanup.
// Pixels are packed 32-bit 0xAARRGGBB (cairo CAIRO_FORMAT_RGB24/ARGB32 layout).
#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct {
    int w, h;          // stride is always w
    uint32_t *px;
} frame_t;

typedef struct {
    int x, y, w, h;
} rect_t;

bool frame_alloc(frame_t *f, int w, int h);
void frame_free(frame_t *f);
void frame_fill(frame_t *f, uint32_t color);

// Largest rect with the source aspect that fits inside dst, centered (pillar/letterbox).
rect_t fit_rect(int sw, int sh, int dw, int dh);
// Centered region of the source with the dst aspect (crop to fill).
rect_t crop_rect(int sw, int sh, int dw, int dh);

// Runs fn(ctx, y0, y1) over [0, n) split across CPU cores.
void parallel_rows(int n, void (*fn)(void *ctx, int y0, int y1), void *ctx);

// Catmull-Rom bicubic resize of src region sr into dst region dr, optionally mirrored
// left-right (fliph) and/or upside-down (flipv); dst outside dr is filled black.
typedef struct scaler scaler;
scaler *scaler_new(void);
void scaler_free(scaler *s);
void scaler_draw(scaler *s, const frame_t *src, rect_t sr, frame_t *dst, rect_t dr, bool fliph, bool flipv);

// Motion-adaptive temporal noise reduction + unsharp mask. Levels 0..3 (Off..High).
// Returns src when both are off, otherwise an internal frame valid until the next call.
typedef struct enhancer enhancer;
enhancer *enhancer_new(void);
void enhancer_free(enhancer *e);
const frame_t *enhancer_process(enhancer *e, const frame_t *src, int noise_level, int sharp_level);
