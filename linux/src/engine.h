// Settings, pacing, frame sources and the fixed-rate output engine.
#pragma once
#include <glib.h>

#include "image.h"
#include "ptp.h"

// ---- settings (same keys as the Windows/macOS apps: [Global] in config.ini) ----

extern const int RESOLUTIONS[3];   // 360, 720, 1080
extern const int FPS_CHOICES[4];   // 15, 24, 25, 30
extern const char *LEVEL_NAMES[4]; // Off, Low, Medium, High

typedef struct {
    int width, height, fps;
    bool fill;               // false = fit (bars), true = crop to fill
    bool fliph, flipv;
    int noise, sharp;        // 0..3
} settings_t;

void settings_default(settings_t *s);
void resolution_to_size(int res, int *w, int *h);
int size_to_resolution(int w, int h);
char *settings_path(void); // g_free the result
void settings_load(settings_t *s, const char *path);
bool settings_save(const settings_t *s, const char *path);
bool settings_same_format(const settings_t *a, const settings_t *b);

// ---- pacing -----------------------------------------------------------------

typedef struct {
    double interval, next;
    bool started;
    int late;
} pacer_t;

void pacer_init(pacer_t *p, int fps);
// Returns how long to sleep before the next tick (seconds); skips missed slots instead of bursting.
double pacer_step(pacer_t *p, double now);

typedef struct {
    double t[512];
    int head, count;
    double last;
} ratemeter_t;

void rate_tick(ratemeter_t *r, double now);
double rate_current(ratemeter_t *r, double now);
double mono_now(void);

// ---- sources ------------------------------------------------------------------

typedef enum { SOURCE_CAMERA, SOURCE_TEST } source_kind;

typedef struct {
    bool valid;
    bool has_mode, has_iso, has_shutter, has_av;
    uint32_t mode, iso, shutter, av;
} exposure_t;

typedef struct source source_t;
source_t *source_new(source_kind kind);
void source_start(source_t *s);
void source_stop(source_t *s);
void source_free(source_t *s);
int source_seq(source_t *s);
// Calls fn on the newest frame under the source lock; returns its sequence number (0 = none yet).
int source_with_latest(source_t *s, void (*fn)(const frame_t *, void *), void *ud);
void source_status(source_t *s, char *model, size_t mlen, char *state, size_t slen);
double source_fps(source_t *s);
exposure_t source_exposure(source_t *s);
void source_request_property(source_t *s, uint32_t prop, uint32_t value);

// ---- output engine -------------------------------------------------------------

typedef struct engine engine_t;
engine_t *engine_new(source_t *src, const settings_t *s, void (*on_frame)(void *), void *ud);
void engine_start(engine_t *e);
void engine_stop(engine_t *e);  // also frees
void engine_set_live(engine_t *e, const settings_t *s); // flip / cleanup levels, applied in place
// Calls fn on the frame most recently sent (under lock). Returns false if none yet.
bool engine_with_frame(engine_t *e, void (*fn)(const frame_t *, void *), void *ud);
void engine_stats(engine_t *e, double *out_fps, int *repeated, int *dropped, int *late, double *compose_ms);
