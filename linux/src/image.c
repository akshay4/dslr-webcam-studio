#include "image.h"

#include <glib.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

bool frame_alloc(frame_t *f, int w, int h)
{
    f->w = w;
    f->h = h;
    f->px = calloc((size_t)w * h, 4);
    return f->px != NULL;
}

void frame_free(frame_t *f)
{
    free(f->px);
    f->px = NULL;
    f->w = f->h = 0;
}

void frame_fill(frame_t *f, uint32_t color)
{
    for (size_t i = 0, n = (size_t)f->w * f->h; i < n; i++) f->px[i] = color;
}

static int iround(double v) { return (int)floor(v + 0.5); }

rect_t fit_rect(int sw, int sh, int dw, int dh)
{
    double scale = fmin((double)dw / sw, (double)dh / sh);
    int w = iround(sw * scale), h = iround(sh * scale);
    if (w > dw) w = dw;
    if (h > dh) h = dh;
    return (rect_t){(dw - w) / 2, (dh - h) / 2, w, h};
}

rect_t crop_rect(int sw, int sh, int dw, int dh)
{
    if ((long long)sw * dh > (long long)dw * sh) {
        int w = iround((double)sh * dw / dh);
        return (rect_t){(sw - w) / 2, 0, w, sh};
    }
    int h = iround((double)sw * dh / dw);
    return (rect_t){0, (sh - h) / 2, sw, h};
}

// ---- parallel helper --------------------------------------------------------

typedef struct {
    void (*fn)(void *, int, int);
    void *ctx;
    int y0, y1;
} job_t;

static gpointer run_job(gpointer p)
{
    job_t *j = p;
    j->fn(j->ctx, j->y0, j->y1);
    return NULL;
}

void parallel_rows(int n, void (*fn)(void *ctx, int y0, int y1), void *ctx)
{
    int threads = (int)g_get_num_processors();
    if (threads > 16) threads = 16;
    if (threads < 2 || n < 64) { fn(ctx, 0, n); return; }
    job_t jobs[16];
    GThread *th[16];
    int per = (n + threads - 1) / threads;
    for (int t = 0; t < threads; t++) {
        jobs[t] = (job_t){fn, ctx, t * per, (t + 1) * per > n ? n : (t + 1) * per};
        th[t] = t == 0 ? NULL : g_thread_new("rows", run_job, &jobs[t]);
    }
    run_job(&jobs[0]);
    for (int t = 1; t < threads; t++) g_thread_join(th[t]);
}

// ---- bicubic scaler ---------------------------------------------------------

typedef struct {
    int taps, len;
    int *idx;
    float *w;
} axis_t;

struct scaler {
    float *tmp;
    size_t tmp_cap;
};

scaler *scaler_new(void) { return calloc(1, sizeof(scaler)); }

void scaler_free(scaler *s)
{
    if (!s) return;
    free(s->tmp);
    free(s);
}

static float catmull_rom(double x)
{
    x = fabs(x);
    if (x < 1) return (float)((1.5 * x - 2.5) * x * x + 1);
    if (x < 2) return (float)(((-0.5 * x + 2.5) * x - 4) * x + 2);
    return 0;
}

// dst position i reads src indices idx[i*taps + t] with weights w[i*taps + t].
static void axis_build(axis_t *a, int src_start, int src_len, int dst_len, bool flip)
{
    double scale = (double)src_len / dst_len;
    double fscale = scale > 1 ? scale : 1;  // widen the kernel when shrinking
    double support = 2 * fscale;
    a->taps = (int)ceil(support * 2) + 1;
    a->len = dst_len;
    a->idx = malloc(sizeof(int) * dst_len * a->taps);
    a->w = malloc(sizeof(float) * dst_len * a->taps);
    for (int i = 0; i < dst_len; i++) {
        int o = (flip ? dst_len - 1 - i : i) * a->taps;
        double center = (i + 0.5) * scale - 0.5;
        int first = (int)floor(center - support) + 1;
        float sum = 0;
        for (int t = 0; t < a->taps; t++) {
            int s = first + t;
            float w = catmull_rom((s - center) / fscale);
            int c = s < 0 ? 0 : s > src_len - 1 ? src_len - 1 : s;
            a->idx[o + t] = src_start + c;
            a->w[o + t] = w;
            sum += w;
        }
        if (sum != 0)
            for (int t = 0; t < a->taps; t++) a->w[o + t] /= sum;
    }
}

static void axis_free(axis_t *a)
{
    free(a->idx);
    free(a->w);
}

typedef struct {
    const frame_t *src;
    rect_t sr, dr;
    frame_t *dst;
    axis_t ax, ay;
    float *tmp;
} scale_job;

static void h_pass(void *p, int y0, int y1)
{
    scale_job *j = p;
    int dw = j->dr.w, taps = j->ax.taps;
    for (int row = y0; row < y1; row++) {
        const uint32_t *s = j->src->px + (size_t)(j->sr.y + row) * j->src->w;
        float *t = j->tmp + (size_t)row * dw * 3;
        for (int x = 0; x < dw; x++) {
            float r = 0, g = 0, b = 0;
            int o = x * taps;
            for (int k = 0; k < taps; k++) {
                uint32_t px = s[j->ax.idx[o + k]];
                float w = j->ax.w[o + k];
                r += ((px >> 16) & 255) * w;
                g += ((px >> 8) & 255) * w;
                b += (px & 255) * w;
            }
            *t++ = r; *t++ = g; *t++ = b;
        }
    }
}

static inline uint32_t clamp8(float v)
{
    int i = (int)(v + 0.5f);
    return (uint32_t)(i < 0 ? 0 : i > 255 ? 255 : i);
}

static void v_pass(void *p, int y0, int y1)
{
    scale_job *j = p;
    int dw = j->dr.w, W = j->dst->w, taps = j->ay.taps;
    for (int y = y0; y < y1; y++) {
        uint32_t *d = j->dst->px + (size_t)y * W;
        if (y < j->dr.y || y >= j->dr.y + j->dr.h) {
            for (int x = 0; x < W; x++) d[x] = 0xFF000000;
            continue;
        }
        for (int x = 0; x < j->dr.x; x++) d[x] = 0xFF000000;
        for (int x = j->dr.x + dw; x < W; x++) d[x] = 0xFF000000;
        int o = (y - j->dr.y) * taps;
        d += j->dr.x;
        for (int x = 0; x < dw; x++) {
            float r = 0, g = 0, b = 0;
            for (int k = 0; k < taps; k++) {
                const float *t = j->tmp + ((size_t)j->ay.idx[o + k] * dw + x) * 3;
                float w = j->ay.w[o + k];
                r += t[0] * w; g += t[1] * w; b += t[2] * w;
            }
            d[x] = 0xFF000000 | (clamp8(r) << 16) | (clamp8(g) << 8) | clamp8(b);
        }
    }
}

void scaler_draw(scaler *s, const frame_t *src, rect_t sr, frame_t *dst, rect_t dr, bool fliph, bool flipv)
{
    scale_job j = {src, sr, dr, dst, {0}, {0}, NULL};
    axis_build(&j.ax, sr.x, sr.w, dr.w, fliph);
    axis_build(&j.ay, 0, sr.h, dr.h, flipv); // rows index into the temp buffer
    size_t need = (size_t)sr.h * dr.w * 3;
    if (s->tmp_cap < need) {
        free(s->tmp);
        s->tmp = malloc(need * sizeof(float));
        s->tmp_cap = s->tmp ? need : 0;
    }
    if (s->tmp) {
        j.tmp = s->tmp;
        parallel_rows(sr.h, h_pass, &j);
        parallel_rows(dst->h, v_pass, &j);
    }
    axis_free(&j.ax);
    axis_free(&j.ay);
}

// ---- enhancer -----------------------------------------------------------------
// 1. Temporal noise reduction: each pixel is blended with its running average. Motion is
//    detected from the brightness change averaged over 3x3; random sensor noise cancels out
//    in that average but real movement does not, so moving areas take the new frame directly.
// 2. Unsharp mask (3x3) with a small dead zone so leftover noise isn't sharpened.

struct enhancer {
    int w, h;
    uint32_t *cur, *acc, *out;
    int *diff;
    bool have_acc;
    frame_t result;
};

//                                Off  Low  Medium High
static const int NR_BASE[] = {256, 96, 64, 40};   // weight (of 256) for the new frame when nothing moved
static const int NR_THRESH[] = {1, 6, 9, 12};     // 3x3-averaged brightness change treated as motion
static const int SHARP_AMOUNT[] = {0, 128, 256, 420};

enhancer *enhancer_new(void) { return calloc(1, sizeof(enhancer)); }

static void enh_release(enhancer *e)
{
    free(e->cur); free(e->acc); free(e->out); free(e->diff);
    e->cur = e->acc = e->out = NULL;
    e->diff = NULL;
}

void enhancer_free(enhancer *e)
{
    if (!e) return;
    enh_release(e);
    free(e);
}

static inline int luma(uint32_t p) { return (int)((2 * ((p >> 16) & 255) + 5 * ((p >> 8) & 255) + (p & 255)) >> 3); }

typedef struct {
    enhancer *e;
    int base, thr, amount;
    const uint32_t *in;
    uint32_t *outp;
} enh_job;

static void diff_rows(void *p, int y0, int y1)
{
    enh_job *j = p;
    enhancer *e = j->e;
    for (int i = y0 * e->w, end = y1 * e->w; i < end; i++) e->diff[i] = luma(e->cur[i]) - luma(e->acc[i]);
}

static void temporal_rows(void *p, int y0, int y1)
{
    enh_job *j = p;
    enhancer *e = j->e;
    int W = e->w, H = e->h;
    for (int y = y0; y < y1; y++) {
        int ym = (y > 0 ? y - 1 : 0) * W, yc = y * W, yp = (y < H - 1 ? y + 1 : H - 1) * W;
        for (int x = 0; x < W; x++) {
            int xm = x > 0 ? x - 1 : 0, xp = x < W - 1 ? x + 1 : W - 1;
            int *df = e->diff;
            int m = abs(df[ym + xm] + df[ym + x] + df[ym + xp] + df[yc + xm] + df[yc + x] + df[yc + xp] +
                        df[yp + xm] + df[yp + x] + df[yp + xp]) / 9;
            int k = m >= j->thr ? 256 : j->base + (256 - j->base) * m / j->thr;
            uint32_t a = e->acc[yc + x], n = e->cur[yc + x];
            int pr = (a >> 16) & 255, pg = (a >> 8) & 255, pb = a & 255;
            pr += ((((int)(n >> 16) & 255) - pr) * k + 128) >> 8;
            pg += ((((int)(n >> 8) & 255) - pg) * k + 128) >> 8;
            pb += ((((int)n & 255) - pb) * k + 128) >> 8;
            e->acc[yc + x] = 0xFF000000u | ((uint32_t)pr << 16) | ((uint32_t)pg << 8) | (uint32_t)pb;
        }
    }
}

static inline int ch(uint32_t p, int sh) { return (int)((p >> sh) & 255); }

static void sharpen_rows(void *p, int y0, int y1)
{
    enh_job *j = p;
    enhancer *e = j->e;
    int W = e->w, H = e->h;
    const uint32_t *s = j->in;
    for (int y = y0; y < y1; y++) {
        int ym = (y > 0 ? y - 1 : 0) * W, yc = y * W, yp = (y < H - 1 ? y + 1 : H - 1) * W;
        for (int x = 0; x < W; x++) {
            int xm = x > 0 ? x - 1 : 0, xp = x < W - 1 ? x + 1 : W - 1;
            uint32_t c = s[yc + x], result = 0xFF000000u;
            for (int sh = 0; sh <= 16; sh += 8) {
                int blur = (ch(s[ym + xm], sh) + 2 * ch(s[ym + x], sh) + ch(s[ym + xp], sh) +
                            2 * ch(s[yc + xm], sh) + 4 * ch(c, sh) + 2 * ch(s[yc + xp], sh) +
                            ch(s[yp + xm], sh) + 2 * ch(s[yp + x], sh) + ch(s[yp + xp], sh) + 8) >> 4;
                int v = ch(c, sh), d = v - blur;
                d = d > 2 ? d - 2 : d < -2 ? d + 2 : 0; // dead zone
                v += (d * j->amount) >> 8;
                result |= (uint32_t)(v < 0 ? 0 : v > 255 ? 255 : v) << sh;
            }
            j->outp[yc + x] = result;
        }
    }
}

const frame_t *enhancer_process(enhancer *e, const frame_t *src, int nr, int sharp)
{
    if (nr <= 0 && sharp <= 0) { e->have_acc = false; return src; }
    if (nr > 3) nr = 3;
    if (sharp > 3) sharp = 3;
    if (e->w != src->w || e->h != src->h || !e->cur) {
        enh_release(e);
        e->w = src->w;
        e->h = src->h;
        size_t n = (size_t)e->w * e->h;
        e->cur = malloc(n * 4); e->acc = malloc(n * 4); e->out = malloc(n * 4); e->diff = malloc(n * sizeof(int));
        e->have_acc = false;
        if (!e->cur || !e->acc || !e->out || !e->diff) { enh_release(e); e->w = e->h = 0; return src; }
    }
    size_t bytes = (size_t)e->w * e->h * 4;
    memcpy(e->cur, src->px, bytes);
    const uint32_t *img = e->cur;
    enh_job j = {e, 0, 0, 0, NULL, NULL};

    if (nr > 0) {
        if (!e->have_acc) {
            memcpy(e->acc, e->cur, bytes);
            e->have_acc = true;
        } else {
            j.base = NR_BASE[nr];
            j.thr = NR_THRESH[nr];
            parallel_rows(e->h, diff_rows, &j);
            parallel_rows(e->h, temporal_rows, &j);
        }
        img = e->acc;
    } else {
        e->have_acc = false;
    }
    if (sharp > 0) {
        j.amount = SHARP_AMOUNT[sharp];
        j.in = img;
        j.outp = e->out;
        parallel_rows(e->h, sharpen_rows, &j);
        img = e->out;
    }
    e->result.w = e->w;
    e->result.h = e->h;
    e->result.px = (uint32_t *)img;
    return &e->result;
}
