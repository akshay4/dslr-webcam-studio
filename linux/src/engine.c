#include "engine.h"

#include <gdk-pixbuf/gdk-pixbuf.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "camera.h"

const int RESOLUTIONS[3] = {360, 720, 1080};
// Canon DSLR live view tops out around 17-30 fps over USB 2.0, so 60 would only repeat frames.
const int FPS_CHOICES[4] = {15, 24, 25, 30};
const char *LEVEL_NAMES[4] = {"Off", "Low", "Medium", "High"};

double mono_now(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

// ---- settings -------------------------------------------------------------------

void settings_default(settings_t *s)
{
    *s = (settings_t){1280, 720, 30, false, false, false, 2, 1};
}

void resolution_to_size(int res, int *w, int *h)
{
    switch (res) {
    case 360: *w = 640; *h = 360; break;
    case 720: *w = 1280; *h = 720; break;
    default: *w = 1920; *h = 1080; break; // Canon's default branch
    }
}

int size_to_resolution(int w, int h)
{
    if (w == 640 && h == 360) return 360;
    if (w == 1280 && h == 720) return 720;
    return 1080;
}

char *settings_path(void)
{
    return g_build_filename(g_get_user_config_dir(), "dslr-webcam-studio", "config.ini", NULL);
}

static int key_int(GKeyFile *kf, const char *key, int def)
{
    char *v = g_key_file_get_value(kf, "Global", key, NULL);
    if (!v) return def;
    char *end;
    long n = strtol(g_strstrip(v), &end, 10);
    int ok = *end == 0 && end != v;
    g_free(v);
    return ok ? (int)n : def;
}

void settings_load(settings_t *s, const char *path)
{
    settings_default(s);
    GKeyFile *kf = g_key_file_new();
    if (g_key_file_load_from_file(kf, path, G_KEY_FILE_KEEP_COMMENTS, NULL)) {
        int w = key_int(kf, "StreamWidth", 0), h = key_int(kf, "StreamHeight", 0);
        for (int i = 0; i < 3; i++) {
            int rw, rh;
            resolution_to_size(RESOLUTIONS[i], &rw, &rh);
            if (rw == w && rh == h) { s->width = w; s->height = h; }
        }
        int fps = key_int(kf, "StreamFps", 30);
        for (int i = 0; i < 4; i++)
            if (FPS_CHOICES[i] == fps) s->fps = fps;
        char *fit = g_key_file_get_value(kf, "Global", "FitMode", NULL);
        s->fill = fit && g_ascii_strcasecmp(g_strstrip(fit), "fill") == 0;
        g_free(fit);
        s->fliph = key_int(kf, "FlipHorizontal", 0) == 1;
        s->flipv = key_int(kf, "FlipVertical", 0) == 1;
        int nr = key_int(kf, "NoiseReduction", 2), sh = key_int(kf, "Sharpness", 1);
        s->noise = nr < 0 ? 0 : nr > 3 ? 3 : nr;
        s->sharp = sh < 0 ? 0 : sh > 3 ? 3 : sh;
    }
    g_key_file_free(kf);
}

bool settings_save(const settings_t *s, const char *path)
{
    GKeyFile *kf = g_key_file_new();
    g_key_file_load_from_file(kf, path, G_KEY_FILE_KEEP_COMMENTS, NULL); // keep unrelated keys
    g_key_file_set_integer(kf, "Global", "StreamWidth", s->width);
    g_key_file_set_integer(kf, "Global", "StreamHeight", s->height);
    g_key_file_set_integer(kf, "Global", "StreamFps", s->fps);
    g_key_file_set_string(kf, "Global", "FitMode", s->fill ? "fill" : "fit");
    g_key_file_set_integer(kf, "Global", "FlipHorizontal", s->fliph);
    g_key_file_set_integer(kf, "Global", "FlipVertical", s->flipv);
    g_key_file_set_integer(kf, "Global", "NoiseReduction", s->noise);
    g_key_file_set_integer(kf, "Global", "Sharpness", s->sharp);
    char *dir = g_path_get_dirname(path);
    g_mkdir_with_parents(dir, 0700);
    g_free(dir);
    bool ok = g_key_file_save_to_file(kf, path, NULL);
    g_key_file_free(kf);
    return ok;
}

bool settings_same_format(const settings_t *a, const settings_t *b)
{
    return a->width == b->width && a->height == b->height && a->fps == b->fps && a->fill == b->fill;
}

// ---- pacing ---------------------------------------------------------------------

void pacer_init(pacer_t *p, int fps)
{
    *p = (pacer_t){1.0 / fps, 0, false, 0};
}

double pacer_step(pacer_t *p, double now)
{
    if (!p->started) {
        p->started = true;
        p->next = now + p->interval;
        return 0;
    }
    if (now >= p->next) {
        // Fell behind: skip the missed slots and wait for the next slot on the same timeline.
        p->late++;
        p->next += (floor((now - p->next) / p->interval) + 1) * p->interval;
    }
    double delay = p->next - now;
    p->next += p->interval;
    return delay;
}

void rate_tick(ratemeter_t *r, double now)
{
    r->t[(r->head + r->count) % 512] = now;
    if (r->count < 512) r->count++;
    else r->head = (r->head + 1) % 512;
    r->last = now;
}

// Events per second over the last second.
double rate_current(ratemeter_t *r, double now)
{
    while (r->count > 0 && now - r->t[r->head] > 1.0) {
        r->head = (r->head + 1) % 512;
        r->count--;
    }
    if (r->count < 2) return 0;
    return (r->count - 1) / (r->last - r->t[r->head]);
}

static void sleep_s(double s)
{
    if (s > 0) g_usleep((gulong)(s * 1e6));
}

// ---- sources --------------------------------------------------------------------

#define MAX_REQ 16

struct source {
    source_kind kind;
    GThread *thread;
    volatile gint stopping;
    GMutex lock;
    frame_t latest;
    int seq;
    char model[96], state[320];
    ratemeter_t meter;
    exposure_t exposure;
    uint32_t req_prop[MAX_REQ], req_val[MAX_REQ];
    int nreq;
};

source_t *source_new(source_kind kind)
{
    source_t *s = calloc(1, sizeof *s);
    s->kind = kind;
    g_mutex_init(&s->lock);
    snprintf(s->state, sizeof s->state, "Stopped");
    return s;
}

static void set_state(source_t *s, const char *model, const char *state)
{
    g_mutex_lock(&s->lock);
    if (model) g_strlcpy(s->model, model, sizeof s->model);
    if (state) g_strlcpy(s->state, state, sizeof s->state);
    g_mutex_unlock(&s->lock);
}

static void publish(source_t *s, frame_t *f)
{
    g_mutex_lock(&s->lock);
    frame_free(&s->latest);
    s->latest = *f;
    s->seq++;
    rate_tick(&s->meter, mono_now());
    g_mutex_unlock(&s->lock);
}

// Decodes a JPEG into a packed 0xFFRRGGBB frame using gdk-pixbuf.
static bool decode_jpeg(const uint8_t *jpeg, size_t len, frame_t *out)
{
    GdkPixbufLoader *ld = gdk_pixbuf_loader_new();
    gboolean wrote = gdk_pixbuf_loader_write(ld, jpeg, len, NULL);
    gboolean closed = gdk_pixbuf_loader_close(ld, NULL); // always close exactly once
    GdkPixbuf *pb = wrote && closed ? gdk_pixbuf_loader_get_pixbuf(ld) : NULL;
    if (!pb) { g_object_unref(ld); return false; }
    int w = gdk_pixbuf_get_width(pb), h = gdk_pixbuf_get_height(pb);
    int n = gdk_pixbuf_get_n_channels(pb), stride = gdk_pixbuf_get_rowstride(pb);
    const guchar *p = gdk_pixbuf_read_pixels(pb);
    if (!frame_alloc(out, w, h)) { g_object_unref(ld); return false; }
    for (int y = 0; y < h; y++) {
        const guchar *r = p + (size_t)y * stride;
        uint32_t *d = out->px + (size_t)y * w;
        for (int x = 0; x < w; x++, r += n) d[x] = 0xFF000000u | ((uint32_t)r[0] << 16) | ((uint32_t)r[1] << 8) | r[2];
    }
    g_object_unref(ld);
    return true;
}

static void update_exposure(source_t *s, camera *c)
{
    exposure_t e = {true, false, false, false, false, 0, 0, 0, 0};
    e.has_mode = camera_prop(c, DPC_EOS_AutoExposureMode, &e.mode);
    e.has_iso = camera_prop(c, DPC_EOS_ISOSpeed, &e.iso);
    e.has_shutter = camera_prop(c, DPC_EOS_ShutterSpeed, &e.shutter);
    e.has_av = camera_prop(c, DPC_EOS_Aperture, &e.av);
    g_mutex_lock(&s->lock);
    s->exposure = e;
    g_mutex_unlock(&s->lock);
}

static void apply_requests(source_t *s, camera *c)
{
    uint32_t props[MAX_REQ], vals[MAX_REQ];
    g_mutex_lock(&s->lock);
    int n = s->nreq;
    memcpy(props, s->req_prop, sizeof props);
    memcpy(vals, s->req_val, sizeof vals);
    s->nreq = 0;
    g_mutex_unlock(&s->lock);
    for (int i = 0; i < n; i++) {
        uint16_t rc = camera_set_property(c, props[i], vals[i]);
        if (rc != PTP_RC_OK) {
            char tmp[16], msg[160];
            snprintf(msg, sizeof msg, "Camera refused the change (%s). Set the mode dial to M.", ptp_rc_name(rc, tmp));
            set_state(s, NULL, msg);
        } else {
            set_state(s, NULL, "Live");
        }
    }
    if (n) update_exposure(s, c);
}

static void camera_loop(source_t *s)
{
    char err[320];
    while (!g_atomic_int_get(&s->stopping)) {
        set_state(s, NULL, "Connecting to camera...");
        camera *c = camera_open(err, sizeof err);
        if (c) {
            set_state(s, camera_model(c), "Live");
            update_exposure(s, c);
            double last_exp = mono_now();
            while (!g_atomic_int_get(&s->stopping)) {
                apply_requests(s, c);
                if (mono_now() - last_exp > 0.5) { update_exposure(s, c); last_exp = mono_now(); }
                uint8_t *jpeg;
                size_t len;
                int r = camera_read_jpeg(c, &jpeg, &len, err, sizeof err);
                if (r < 0) break;
                if (r == 0) { g_usleep(5000); continue; }
                frame_t f;
                if (decode_jpeg(jpeg, len, &f)) publish(s, &f);
                free(jpeg);
            }
            camera_close(c);
            g_mutex_lock(&s->lock);
            s->exposure.valid = false;
            g_mutex_unlock(&s->lock);
        }
        if (g_atomic_int_get(&s->stopping)) break;
        char msg[360];
        snprintf(msg, sizeof msg, "Camera: %s Retrying...", err);
        set_state(s, NULL, msg);
        for (int i = 0; i < 40 && !g_atomic_int_get(&s->stopping); i++) g_usleep(50000); // retry in 2 s
    }
}

// 3:2 moving test pattern at ~29.97 fps, sized like Canon live view (960x640).
static void test_loop(source_t *s)
{
    set_state(s, "Test pattern 960x640", "Live");
    static const uint32_t bars[7] = {0xFFC0C0C0, 0xFFC0C000, 0xFF00C0C0, 0xFF00C000, 0xFFC000C0, 0xFFC00000, 0xFF0000C0};
    double next = mono_now();
    for (int n = 0; !g_atomic_int_get(&s->stopping); n++) {
        frame_t f;
        if (!frame_alloc(&f, 960, 640)) break;
        int bw = 960 / 7, bx = (n * 8) % 960;
        for (int y = 0; y < 640; y++)
            for (int x = 0; x < 960; x++) {
                int b = x / bw > 6 ? 6 : x / bw;
                f.px[y * 960 + x] = (x >= bx && x < bx + 24) ? 0xFFFFFFFF : bars[b];
            }
        publish(s, &f);
        next += 1 / 29.97;
        double wait = next - mono_now();
        if (wait > 0) sleep_s(wait); else next = mono_now();
    }
}

static gpointer source_thread(gpointer p)
{
    source_t *s = p;
    if (s->kind == SOURCE_CAMERA) camera_loop(s); else test_loop(s);
    return NULL;
}

void source_start(source_t *s)
{
    g_atomic_int_set(&s->stopping, 0);
    s->thread = g_thread_new("source", source_thread, s);
}

void source_stop(source_t *s)
{
    g_atomic_int_set(&s->stopping, 1);
    if (s->thread) { g_thread_join(s->thread); s->thread = NULL; }
    g_mutex_lock(&s->lock);
    frame_free(&s->latest);
    g_strlcpy(s->state, "Stopped", sizeof s->state);
    g_mutex_unlock(&s->lock);
}

void source_free(source_t *s)
{
    if (!s) return;
    source_stop(s);
    g_mutex_clear(&s->lock);
    free(s);
}

int source_seq(source_t *s)
{
    g_mutex_lock(&s->lock);
    int v = s->seq;
    g_mutex_unlock(&s->lock);
    return v;
}

int source_with_latest(source_t *s, void (*fn)(const frame_t *, void *), void *ud)
{
    g_mutex_lock(&s->lock);
    int seq = s->latest.px ? s->seq : 0;
    if (seq) fn(&s->latest, ud);
    g_mutex_unlock(&s->lock);
    return seq;
}

void source_status(source_t *s, char *model, size_t mlen, char *state, size_t slen)
{
    g_mutex_lock(&s->lock);
    g_strlcpy(model, s->model, mlen);
    g_strlcpy(state, s->state, slen);
    g_mutex_unlock(&s->lock);
}

double source_fps(source_t *s)
{
    g_mutex_lock(&s->lock);
    double r = rate_current(&s->meter, mono_now());
    g_mutex_unlock(&s->lock);
    return r;
}

exposure_t source_exposure(source_t *s)
{
    g_mutex_lock(&s->lock);
    exposure_t e = s->exposure;
    g_mutex_unlock(&s->lock);
    return e;
}

void source_request_property(source_t *s, uint32_t prop, uint32_t value)
{
    g_mutex_lock(&s->lock);
    if (s->nreq < MAX_REQ) {
        s->req_prop[s->nreq] = prop;
        s->req_val[s->nreq] = value;
        s->nreq++;
    }
    g_mutex_unlock(&s->lock);
}

// ---- output engine ----------------------------------------------------------------
// Each tick first sends the frame composed during the previous slot, then composes the
// newest camera frame for the next one, so send timing doesn't depend on scaling time.

struct engine {
    source_t *src;
    settings_t s;                 // output format (fixed for this engine)
    volatile gint fliph, flipv, noise, sharp;
    GMutex front_lock;
    frame_t front, pending;
    bool pending_ready;
    GThread *thread;
    volatile gint stopping;
    void (*on_frame)(void *);
    void *ud;
    void (*sink)(const frame_t *, void *);
    void *sink_ud;
    scaler *sc;
    enhancer *en;
    GMutex stats_lock;
    ratemeter_t meter;
    int repeated, dropped, frames, late;
    double compose_ms;
};

engine_t *engine_new(source_t *src, const settings_t *s, void (*on_frame)(void *), void *ud)
{
    engine_t *e = calloc(1, sizeof *e);
    e->src = src;
    e->s = *s;
    e->on_frame = on_frame;
    e->ud = ud;
    engine_set_live(e, s);
    g_mutex_init(&e->front_lock);
    g_mutex_init(&e->stats_lock);
    frame_alloc(&e->front, s->width, s->height);
    frame_alloc(&e->pending, s->width, s->height);
    frame_fill(&e->front, 0xFF000000);
    frame_fill(&e->pending, 0xFF000000);
    e->sc = scaler_new();
    e->en = enhancer_new();
    return e;
}

void engine_set_sink(engine_t *e, void (*sink)(const frame_t *, void *), void *ud)
{
    e->sink = sink;
    e->sink_ud = ud;
}

void engine_set_live(engine_t *e, const settings_t *s)
{
    g_atomic_int_set(&e->fliph, s->fliph);
    g_atomic_int_set(&e->flipv, s->flipv);
    g_atomic_int_set(&e->noise, s->noise);
    g_atomic_int_set(&e->sharp, s->sharp);
}

static int live_key(engine_t *e)
{
    return g_atomic_int_get(&e->fliph) | g_atomic_int_get(&e->flipv) << 1 |
           g_atomic_int_get(&e->noise) << 2 | g_atomic_int_get(&e->sharp) << 4;
}

static void compose(const frame_t *src, void *ud)
{
    engine_t *e = ud;
    const frame_t *img = enhancer_process(e->en, src, g_atomic_int_get(&e->noise), g_atomic_int_get(&e->sharp));
    bool fh = g_atomic_int_get(&e->fliph), fv = g_atomic_int_get(&e->flipv);
    int W = e->s.width, H = e->s.height;
    if (e->s.fill)
        scaler_draw(e->sc, img, crop_rect(img->w, img->h, W, H), &e->pending, (rect_t){0, 0, W, H}, fh, fv);
    else
        scaler_draw(e->sc, img, (rect_t){0, 0, img->w, img->h}, &e->pending, fit_rect(img->w, img->h, W, H), fh, fv);
}

static gpointer engine_thread(gpointer p)
{
    engine_t *e = p;
    pacer_t pacer;
    pacer_init(&pacer, e->s.fps);
    int last_seq = 0, last_opts = live_key(e);
    bool started = false;
    while (!g_atomic_int_get(&e->stopping)) {
        sleep_s(pacer_step(&pacer, mono_now()));
        if (g_atomic_int_get(&e->stopping)) break;

        // 1. Send on the slot.
        if (e->pending_ready) {
            g_mutex_lock(&e->front_lock);
            frame_t t = e->front; e->front = e->pending; e->pending = t;
            g_mutex_unlock(&e->front_lock);
            e->pending_ready = false;
            started = true;
        } else if (started) {
            g_mutex_lock(&e->stats_lock); e->repeated++; g_mutex_unlock(&e->stats_lock);
        }
        if (started) {
            if (e->sink) {
                g_mutex_lock(&e->front_lock);
                e->sink(&e->front, e->sink_ud);
                g_mutex_unlock(&e->front_lock);
            }
            g_mutex_lock(&e->stats_lock);
            e->frames++;
            e->late = pacer.late;
            rate_tick(&e->meter, mono_now());
            g_mutex_unlock(&e->stats_lock);
            if (e->on_frame) e->on_frame(e->ud);
        }

        // 2. Prepare the next slot from the newest camera frame (or right away after an option change).
        int opts = live_key(e);
        if (source_seq(e->src) != last_seq || opts != last_opts) {
            last_opts = opts;
            double t0 = mono_now();
            int drawn = source_with_latest(e->src, compose, e);
            double ms = (mono_now() - t0) * 1000;
            if (drawn) {
                g_mutex_lock(&e->stats_lock);
                if (last_seq) e->dropped += drawn - last_seq - 1 > 0 ? drawn - last_seq - 1 : 0;
                e->compose_ms = e->compose_ms == 0 ? ms : 0.9 * e->compose_ms + 0.1 * ms;
                g_mutex_unlock(&e->stats_lock);
                last_seq = drawn;
                e->pending_ready = true;
            }
        }
    }
    return NULL;
}

void engine_start(engine_t *e)
{
    e->thread = g_thread_new("output", engine_thread, e);
}

void engine_stop(engine_t *e)
{
    if (!e) return;
    g_atomic_int_set(&e->stopping, 1);
    if (e->thread) g_thread_join(e->thread);
    frame_free(&e->front);
    frame_free(&e->pending);
    scaler_free(e->sc);
    enhancer_free(e->en);
    g_mutex_clear(&e->front_lock);
    g_mutex_clear(&e->stats_lock);
    free(e);
}

bool engine_with_frame(engine_t *e, void (*fn)(const frame_t *, void *), void *ud)
{
    g_mutex_lock(&e->front_lock);
    g_mutex_lock(&e->stats_lock);
    bool have = e->frames > 0;
    g_mutex_unlock(&e->stats_lock);
    if (have) fn(&e->front, ud);
    g_mutex_unlock(&e->front_lock);
    return have;
}

void engine_stats(engine_t *e, double *out_fps, int *repeated, int *dropped, int *late, double *compose_ms)
{
    g_mutex_lock(&e->stats_lock);
    *out_fps = rate_current(&e->meter, mono_now());
    *repeated = e->repeated;
    *dropped = e->dropped;
    *late = e->late;
    *compose_ms = e->compose_ms;
    g_mutex_unlock(&e->stats_lock);
}
