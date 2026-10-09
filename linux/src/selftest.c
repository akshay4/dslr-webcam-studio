// Built-in checks for the platform-independent logic (run in CI: --selftest).
// Mirrors the Windows app's self-test so the ports stay in step.
#include <glib.h>
#include <glib/gstdio.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "camera.h"
#include "engine.h"

static int failures;

static void check(bool ok, const char *what)
{
    printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) failures++;
}

static bool rect_eq(rect_t r, int x, int y, int w, int h) { return r.x == x && r.y == y && r.w == w && r.h == h; }

static double stddev_green(const frame_t *f)
{
    double s = 0, s2 = 0;
    int n = f->w * f->h;
    for (int i = 0; i < n; i++) { int v = (f->px[i] >> 8) & 255; s += v; s2 += (double)v * v; }
    double m = s / n;
    return sqrt(fmax(0, s2 / n - m * m));
}

static void append32(GByteArray *b, uint32_t v)
{
    uint8_t d[4];
    wr32(d, v);
    g_byte_array_append(b, d, 4);
}

int run_selftest(void)
{
    failures = 0;
    int w, h;
    resolution_to_size(360, &w, &h); check(w == 640 && h == 360, "360 -> 640x360");
    resolution_to_size(720, &w, &h); check(w == 1280 && h == 720, "720 -> 1280x720");
    resolution_to_size(999, &w, &h); check(w == 1920 && h == 1080, "unknown -> 1080p (Canon default)");

    // Settings round trip and fallbacks.
    char *dir = g_dir_make_tmp("dws-XXXXXX", NULL);
    char *ini = g_build_filename(dir, "config.ini", NULL);
    g_file_set_contents(ini, "[Other]\nx=1\n[Global]\nLogLevel=3\nStreamFps=59\n", -1, NULL);
    settings_t s, d;
    settings_default(&d);
    settings_load(&s, ini);
    check(memcmp(&s, &d, sizeof s) == 0, "invalid ini values fall back to defaults");
    s.width = 1920; s.height = 1080; s.fps = 25; s.fill = true; s.fliph = true; s.noise = 3; s.sharp = 0;
    settings_save(&s, ini);
    settings_t back;
    settings_load(&back, ini);
    check(memcmp(&s, &back, sizeof s) == 0, "ini round trip");
    char *text = NULL;
    g_file_get_contents(ini, &text, NULL, NULL);
    check(text && strstr(text, "StreamWidth=1920") && strstr(text, "StreamFps=25") && strstr(text, "FitMode=fill"), "Canon-style keys written");
    check(text && strstr(text, "LogLevel=3") && strstr(text, "[Other]"), "other ini keys preserved");
    g_free(text);
    g_remove(ini);
    g_rmdir(dir);
    g_free(ini);
    g_free(dir);

    // Geometry (Canon live view is 960x640 on the 700D).
    check(rect_eq(fit_rect(960, 640, 1280, 720), 100, 0, 1080, 720), "fit 960x640 into 720p");
    check(rect_eq(fit_rect(960, 640, 1920, 1080), 150, 0, 1620, 1080), "fit into 1080p");
    check(rect_eq(fit_rect(960, 640, 640, 360), 50, 0, 540, 360), "fit into 360p");
    check(rect_eq(crop_rect(960, 640, 1280, 720), 0, 50, 960, 540), "fill crop 960x640 to 16:9");
    check(rect_eq(crop_rect(2000, 720, 1280, 720), 360, 0, 1280, 720), "fill crop wide source");

    // Pacer: no drift, no bursts after a stall.
    pacer_t p;
    pacer_init(&p, 30);
    double t = 0;
    t += pacer_step(&p, t);
    double t0 = t;
    for (int i = 0; i < 300; i++) { t += (i % 2 == 0) ? 0.013 : 0.001; t += pacer_step(&p, t); }
    check(fabs(t - t0 - 10.0) < 1e-6 && p.late == 0, "pacer: 300 ticks at 30 fps = 10 s, no drift");
    t += 0.5;
    t += pacer_step(&p, t);
    double before = t;
    t += pacer_step(&p, t);
    check(p.late == 1 && fabs(t - before - 1 / 30.0) < 1e-9, "pacer: resync after stall without burst");

    ratemeter_t rm = {0};
    double rt = 0;
    for (int i = 0; i < 75; i++) { rate_tick(&rm, rt); rt += 1 / 25.0; }
    rt -= 1 / 25.0;
    check(fabs(rate_current(&rm, rt) - 25) < 0.01, "rate meter: 25 fps");
    check(rate_current(&rm, rt + 2) == 0, "rate meter: 0 after ticks stop");

    // Scaler: color kept, bars black, mirror/flip correct.
    frame_t src, dst;
    frame_alloc(&src, 4, 4);
    frame_alloc(&dst, 16, 9);
    frame_fill(&src, 0xFFC86432);
    scaler *sc = scaler_new();
    rect_t r = fit_rect(4, 4, 16, 9);
    scaler_draw(sc, &src, (rect_t){0, 0, 4, 4}, &dst, r, false, false);
    check(dst.px[(r.y + r.h / 2) * 16 + r.x + r.w / 2] == 0xFFC86432, "scaler keeps color");
    check(dst.px[4 * 16] == 0xFF000000, "scaler paints side bars black");
    frame_free(&src);
    frame_free(&dst);

    frame_alloc(&src, 8, 8);
    frame_alloc(&dst, 8, 8);
    for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            src.px[y * 8 + x] = 0xFF000000u | (x < 4 ? 0xFF0000u : 0x0000FFu) | (y < 4 ? 0x00FF00u : 0);
    scaler_draw(sc, &src, (rect_t){0, 0, 8, 8}, &dst, (rect_t){0, 0, 8, 8}, true, false);
    check((dst.px[0] & 0xFF) == 0xFF && ((dst.px[7] >> 16) & 0xFF) == 0xFF, "mirror swaps left and right");
    scaler_draw(sc, &src, (rect_t){0, 0, 8, 8}, &dst, (rect_t){0, 0, 8, 8}, false, true);
    check(((dst.px[0] >> 8) & 0xFF) == 0 && ((dst.px[7 * 8] >> 8) & 0xFF) == 0xFF, "flip swaps top and bottom");
    scaler_draw(sc, &src, (rect_t){0, 0, 8, 8}, &dst, (rect_t){0, 0, 8, 8}, false, false);
    check(dst.px[0] == 0xFFFFFF00 && (dst.px[63] & 0xFF) == 0xFF, "no flip keeps orientation");
    frame_free(&src);
    frame_free(&dst);
    scaler_free(sc);

    // Noise reduction: static noise shrinks; a big change passes straight through.
    enhancer *en = enhancer_new();
    frame_alloc(&src, 64, 64);
    GRand *rnd = g_rand_new_with_seed(1);
    const frame_t *out = NULL;
    for (int f = 0; f < 20; f++) {
        for (int i = 0; i < 64 * 64; i++) {
            int v = 128 + (int)(g_rand_double(rnd) * 30 - 15);
            src.px[i] = 0xFF000000u | (uint32_t)v << 16 | (uint32_t)v << 8 | (uint32_t)v;
        }
        out = enhancer_process(en, &src, 3, 0);
    }
    double in_std = 30 / sqrt(12), out_std = stddev_green(out);
    char msg[128];
    snprintf(msg, sizeof msg, "noise reduction: static noise std %.1f -> %.1f", in_std, out_std);
    check(out_std < in_std * 0.6, msg);
    frame_fill(&src, 0xFFFFFFFF);
    out = enhancer_process(en, &src, 3, 0);
    check(((out->px[32 * 64 + 32] >> 8) & 255) > 245, "noise reduction: motion passes through without smearing");
    frame_free(&src);
    g_rand_free(rnd);
    enhancer_free(en);

    // Protocol parsing.
    GByteArray *ev = g_byte_array_new();
    append32(ev, 16); append32(ev, 0xC189); append32(ev, DPC_EOS_ISOSpeed); append32(ev, 0x60);
    append32(ev, 12); append32(ev, 0xC18A); append32(ev, DPC_EOS_ISOSpeed);
    append32(ev, 8); append32(ev, 0);
    ptp_props props = {0};
    ptp_parse_events(ev->data, ev->len, &props);
    uint32_t iso;
    char tmp[16];
    check(props.count == 1 && ptp_props_get(&props, DPC_EOS_ISOSpeed, &iso) && iso == 0x60 &&
          strcmp(canon_iso_name(iso, tmp), "800") == 0, "event parse: ISO 800");
    g_byte_array_free(ev, TRUE);

    const uint8_t jpeg[] = {0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9};
    GByteArray *vf = g_byte_array_new();
    append32(vf, 12); append32(vf, 5); append32(vf, 0);
    append32(vf, 8 + sizeof jpeg); append32(vf, 1);
    g_byte_array_append(vf, jpeg, sizeof jpeg);
    size_t off, len;
    check(ptp_extract_jpeg(vf->data, vf->len, &off, &len) && len == sizeof jpeg && memcmp(vf->data + off, jpeg, len) == 0,
          "viewfinder block parse (type 1 = JPEG)");
    check(ptp_extract_jpeg(jpeg, sizeof jpeg, &off, &len) && off == 0 && len == sizeof jpeg, "viewfinder bare JPEG fallback");
    g_byte_array_free(vf, TRUE);

    printf(failures ? "%d FAILED\n" : "ALL PASSED\n", failures);
    return failures ? 1 : 0;
}

// Camera diagnostics report: what libusb sees and a short live-view test.
char *run_diagnostics(void)
{
    GString *s = g_string_new(NULL);
    GDateTime *now = g_date_time_new_now_local();
    char *ts = g_date_time_format(now, "%Y-%m-%d %H:%M:%S");
    g_string_append_printf(s, "DSLR Webcam Studio diagnostics  %s\n", ts);
    g_free(ts);
    g_date_time_unref(now);
#ifdef APP_VERSION
    g_string_append_printf(s, "App %s (Linux)\n\nLive view test:\n", APP_VERSION);
#endif
    char err[320];
    camera *c = camera_open(err, sizeof err);
    if (!c) {
        g_string_append_printf(s, "  result: FAILED - %s\n", err);
        return g_string_free(s, FALSE);
    }
    const ptp_devinfo *di = camera_devinfo(c);
    g_string_append_printf(s, "  Model: %s  firmware %s  (%s)\n  Canon ops:", di->model, di->version, di->manufacturer);
    for (int i = 0; i < di->nops; i++)
        if (di->ops[i] >= 0x9100) g_string_append_printf(s, " %04X", di->ops[i]);
    g_string_append(s, "\n");
    double start = mono_now(), first = -1;
    int frames = 0, not_ready = 0;
    size_t bytes = 0;
    while (mono_now() - start < 6) {
        uint8_t *j;
        size_t n;
        int r = camera_read_jpeg(c, &j, &n, err, sizeof err);
        if (r < 0) { g_string_append_printf(s, "  error: %s\n", err); break; }
        if (r == 0) { not_ready++; g_usleep(5000); continue; }
        if (first < 0) first = mono_now() - start;
        frames++;
        bytes += n;
        free(j);
    }
    double active = mono_now() - start - (first > 0 ? first : 0);
    g_string_append_printf(s, "  first frame after %.0f ms\n  %d frames, %.1f fps, %d not-ready polls, avg JPEG %zu KB\n",
                           first * 1000, frames, frames / (active > 0 ? active : 1), not_ready, frames ? bytes / frames / 1024 : 0);
    uint32_t v;
    char t1[16];
    if (camera_prop(c, DPC_EOS_AutoExposureMode, &v)) g_string_append_printf(s, "  mode %s", canon_mode_name(v, t1));
    if (camera_prop(c, DPC_EOS_ISOSpeed, &v)) g_string_append_printf(s, "  ISO %s", canon_iso_name(v, t1));
    if (camera_prop(c, DPC_EOS_ShutterSpeed, &v)) g_string_append_printf(s, "  %s", canon_shutter_name(v, t1));
    if (camera_prop(c, DPC_EOS_Aperture, &v)) g_string_append_printf(s, "  %s", canon_aperture_name(v, t1));
    g_string_append(s, frames ? "\n  result: OK\n" : "\n  result: FAILED - no frames\n");
    camera_close(c);
    return g_string_free(s, FALSE);
}
