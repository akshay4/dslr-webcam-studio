// DSLR Webcam Studio for Linux: GTK 3 window with live preview and output controls.
//   dslr-webcam-studio              -> app
//   dslr-webcam-studio --selftest   -> run built-in checks (no camera, no display needed)
//   dslr-webcam-studio --diag       -> print a camera diagnostics report
#include <gtk/gtk.h>
#include <stdio.h>
#include <string.h>

#include "camera.h"
#include "engine.h"

#ifndef APP_VERSION
#define APP_VERSION "dev"
#endif
#ifndef DONATE_URL
#define DONATE_URL "https://paypal.me/YOUR_PAYPAL_NAME"
#endif
#define REPO_URL "https://github.com/akshay4/dslr-webcam-studio"

int run_selftest(void);
char *run_diagnostics(void);

typedef struct {
    GtkWidget *window, *preview, *start_btn, *diag_btn;
    GtkWidget *source_box, *res_box, *fps_box, *fit_box, *noise_box, *sharp_box, *iso_box, *shutter_box;
    GtkWidget *mirror_chk, *flip_chk;
    GtkWidget *state_lbl, *exposure_lbl, *stats_lbl;
    settings_t s;
    char *config;
    source_t *src;
    engine_t *eng;
    gint redraw_pending;
    bool syncing;
} app_t;

static bool donate_configured(void)
{
    return strstr(DONATE_URL, "YOUR_PAYPAL_NAME") == NULL && g_str_has_prefix(DONATE_URL, "https://");
}

// ---- streaming -------------------------------------------------------------------

static gboolean redraw_idle(gpointer p)
{
    app_t *a = p;
    g_atomic_int_set(&a->redraw_pending, 0);
    gtk_widget_queue_draw(a->preview);
    return G_SOURCE_REMOVE;
}

static void on_frame(void *p) // output thread
{
    app_t *a = p;
    if (g_atomic_int_compare_and_exchange(&a->redraw_pending, 0, 1)) g_idle_add(redraw_idle, a);
}

static void start_engine(app_t *a)
{
    a->eng = engine_new(a->src, &a->s, on_frame, a);
    engine_start(a->eng);
}

static void start_streaming(app_t *a)
{
    bool test = gtk_combo_box_get_active(GTK_COMBO_BOX(a->source_box)) == 1;
    a->src = source_new(test ? SOURCE_TEST : SOURCE_CAMERA);
    source_start(a->src);
    start_engine(a);
    gtk_button_set_label(GTK_BUTTON(a->start_btn), "Stop");
    gtk_widget_set_sensitive(a->diag_btn, FALSE);
}

static void stop_streaming(app_t *a)
{
    if (a->eng) { engine_stop(a->eng); a->eng = NULL; }
    if (a->src) { source_free(a->src); a->src = NULL; }
    gtk_button_set_label(GTK_BUTTON(a->start_btn), "Start");
    gtk_widget_set_sensitive(a->diag_btn, TRUE);
    gtk_widget_queue_draw(a->preview);
}

static void apply_settings(app_t *a, const settings_t *next)
{
    settings_t prev = a->s;
    a->s = *next;
    settings_save(&a->s, a->config);
    if (!a->eng) return;
    if (settings_same_format(&prev, next)) {
        engine_set_live(a->eng, next); // flip and cleanup change in place
    } else {
        engine_stop(a->eng);
        start_engine(a);
    }
}

// ---- preview -----------------------------------------------------------------------

typedef struct { cairo_t *cr; int w, h; } paint_ctx;

static void paint_frame(const frame_t *f, void *ud)
{
    paint_ctx *p = ud;
    rect_t r = fit_rect(f->w, f->h, p->w, p->h);
    cairo_surface_t *surf = cairo_image_surface_create_for_data((unsigned char *)f->px, CAIRO_FORMAT_RGB24,
                                                                f->w, f->h, f->w * 4);
    cairo_save(p->cr);
    cairo_translate(p->cr, r.x, r.y);
    cairo_scale(p->cr, (double)r.w / f->w, (double)r.h / f->h);
    cairo_set_source_surface(p->cr, surf, 0, 0);
    cairo_pattern_set_filter(cairo_get_source(p->cr), CAIRO_FILTER_GOOD);
    cairo_paint(p->cr);
    cairo_restore(p->cr);
    cairo_surface_destroy(surf);
}

static gboolean on_draw(GtkWidget *w, cairo_t *cr, gpointer p)
{
    app_t *a = p;
    int W = gtk_widget_get_allocated_width(w), H = gtk_widget_get_allocated_height(w);
    cairo_set_source_rgb(cr, 0, 0, 0);
    cairo_paint(cr);
    paint_ctx pc = {cr, W, H};
    if (a->eng && engine_with_frame(a->eng, paint_frame, &pc)) return TRUE;

    char model[96], state[320];
    if (a->src) source_status(a->src, model, sizeof model, state, sizeof state);
    else g_strlcpy(state, "Stopped. Press Start.", sizeof state);
    PangoLayout *layout = gtk_widget_create_pango_layout(w, state);
    pango_layout_set_width(layout, (W - 40) * PANGO_SCALE);
    pango_layout_set_alignment(layout, PANGO_ALIGN_CENTER);
    int tw, th;
    pango_layout_get_pixel_size(layout, &tw, &th);
    cairo_set_source_rgb(cr, 0.6, 0.6, 0.6);
    cairo_move_to(cr, 20, (H - th) / 2.0);
    pango_cairo_show_layout(cr, layout);
    g_object_unref(layout);
    return TRUE;
}

// ---- status & camera controls -----------------------------------------------------

static int combo_index_of(const canon_choice *c, int n, uint32_t code)
{
    for (int i = 0; i < n; i++)
        if (c[i].code == code) return i;
    return -1;
}

static gboolean on_tick(gpointer p)
{
    app_t *a = p;
    char buf[512];
    if (!a->src || !a->eng) {
        gtk_label_set_text(GTK_LABEL(a->state_lbl), "Stopped");
        gtk_label_set_text(GTK_LABEL(a->exposure_lbl), "");
        snprintf(buf, sizeof buf, "Output %dx%d @ %d fps", a->s.width, a->s.height, a->s.fps);
        gtk_label_set_text(GTK_LABEL(a->stats_lbl), buf);
        gtk_widget_set_sensitive(a->iso_box, FALSE);
        gtk_widget_set_sensitive(a->shutter_box, FALSE);
        return G_SOURCE_CONTINUE;
    }
    char model[96], state[320];
    source_status(a->src, model, sizeof model, state, sizeof state);
    exposure_t ex = source_exposure(a->src);
    bool iso_ok = ex.valid && ex.has_mode && canon_iso_settable(ex.mode);
    bool sh_ok = ex.valid && ex.has_mode && canon_shutter_settable(ex.mode);
    snprintf(buf, sizeof buf, "%s%s%s%s", model, model[0] ? ": " : "", state,
             ex.valid && !iso_ok ? "  (turn the mode dial to M to set ISO/shutter)" : "");
    gtk_label_set_text(GTK_LABEL(a->state_lbl), buf);

    if (ex.valid) {
        char t1[16], t2[16], t3[16], t4[16];
        snprintf(buf, sizeof buf, "%s  |  ISO %s  |  %s  |  %s",
                 ex.has_mode ? canon_mode_name(ex.mode, t1) : "?", ex.has_iso ? canon_iso_name(ex.iso, t2) : "?",
                 ex.has_shutter ? canon_shutter_name(ex.shutter, t3) : "?", ex.has_av ? canon_aperture_name(ex.av, t4) : "?");
        gtk_label_set_text(GTK_LABEL(a->exposure_lbl), buf);
    } else {
        gtk_label_set_text(GTK_LABEL(a->exposure_lbl), "");
    }

    a->syncing = true;
    gtk_widget_set_sensitive(a->iso_box, iso_ok);
    gtk_widget_set_sensitive(a->shutter_box, sh_ok);
    if (ex.valid && ex.has_iso) gtk_combo_box_set_active(GTK_COMBO_BOX(a->iso_box), combo_index_of(CANON_ISO_CHOICES, CANON_ISO_CHOICES_N, ex.iso));
    if (ex.valid && ex.has_shutter) gtk_combo_box_set_active(GTK_COMBO_BOX(a->shutter_box), combo_index_of(CANON_SHUTTER_CHOICES, CANON_SHUTTER_CHOICES_N, ex.shutter));
    a->syncing = false;

    double fps, cms;
    int rep, drop, late;
    engine_stats(a->eng, &fps, &rep, &drop, &late, &cms);
    snprintf(buf, sizeof buf, "Output %dx%d @ %d fps target, %.1f actual  |  camera %.1f fps  |  repeated %d  dropped %d",
             a->s.width, a->s.height, a->s.fps, fps, source_fps(a->src), rep, drop);
    gtk_label_set_text(GTK_LABEL(a->stats_lbl), buf);
    return G_SOURCE_CONTINUE;
}

// ---- control callbacks ------------------------------------------------------------

static void on_start(GtkButton *b, gpointer p)
{
    (void)b;
    app_t *a = p;
    if (a->eng) stop_streaming(a); else start_streaming(a);
}

static void on_source(GtkComboBox *c, gpointer p)
{
    (void)c;
    app_t *a = p;
    if (a->eng) { stop_streaming(a); start_streaming(a); }
}

static void on_option(GtkWidget *w, gpointer p)
{
    (void)w;
    app_t *a = p;
    settings_t n = a->s;
    resolution_to_size(RESOLUTIONS[gtk_combo_box_get_active(GTK_COMBO_BOX(a->res_box))], &n.width, &n.height);
    n.fps = FPS_CHOICES[gtk_combo_box_get_active(GTK_COMBO_BOX(a->fps_box))];
    n.fill = gtk_combo_box_get_active(GTK_COMBO_BOX(a->fit_box)) == 1;
    n.fliph = gtk_toggle_button_get_active(GTK_TOGGLE_BUTTON(a->mirror_chk));
    n.flipv = gtk_toggle_button_get_active(GTK_TOGGLE_BUTTON(a->flip_chk));
    n.noise = gtk_combo_box_get_active(GTK_COMBO_BOX(a->noise_box));
    n.sharp = gtk_combo_box_get_active(GTK_COMBO_BOX(a->sharp_box));
    apply_settings(a, &n);
}

static void on_camera_setting(GtkComboBox *c, gpointer p)
{
    app_t *a = p;
    int i = gtk_combo_box_get_active(c);
    if (a->syncing || !a->src || i < 0) return;
    if (GTK_WIDGET(c) == a->iso_box) source_request_property(a->src, DPC_EOS_ISOSpeed, CANON_ISO_CHOICES[i].code);
    else source_request_property(a->src, DPC_EOS_ShutterSpeed, CANON_SHUTTER_CHOICES[i].code);
}

typedef struct { const char *path; bool ok; } snap_ctx;

static void save_frame(const frame_t *f, void *ud)
{
    snap_ctx *s = ud;
    GdkPixbuf *pb = gdk_pixbuf_new(GDK_COLORSPACE_RGB, FALSE, 8, f->w, f->h);
    guchar *px = gdk_pixbuf_get_pixels(pb);
    int stride = gdk_pixbuf_get_rowstride(pb);
    for (int y = 0; y < f->h; y++)
        for (int x = 0; x < f->w; x++) {
            uint32_t v = f->px[y * f->w + x];
            guchar *d = px + y * stride + x * 3;
            d[0] = (v >> 16) & 255; d[1] = (v >> 8) & 255; d[2] = v & 255;
        }
    s->ok = gdk_pixbuf_save(pb, s->path, "png", NULL, NULL);
    g_object_unref(pb);
}

static void on_snapshot(GtkButton *b, gpointer p)
{
    (void)b;
    app_t *a = p;
    if (!a->eng) return;
    const char *dir = g_get_user_special_dir(G_USER_DIRECTORY_PICTURES);
    GDateTime *now = g_date_time_new_now_local();
    char *name = g_date_time_format(now, "DSLRWebcam_%Y%m%d_%H%M%S.png");
    char *path = g_build_filename(dir ? dir : g_get_home_dir(), name, NULL);
    snap_ctx sc = {path, false};
    if (engine_with_frame(a->eng, save_frame, &sc) && sc.ok) {
        char *msg = g_strdup_printf("Saved %s", path);
        gtk_label_set_text(GTK_LABEL(a->state_lbl), msg);
        g_free(msg);
    }
    g_free(path);
    g_free(name);
    g_date_time_unref(now);
}

typedef struct { app_t *a; char *report; } diag_result;

static gboolean diag_done(gpointer p)
{
    diag_result *r = p;
    app_t *a = r->a;
    gtk_widget_set_sensitive(a->diag_btn, TRUE);
    gtk_widget_set_sensitive(a->start_btn, TRUE);

    char *dir = g_build_filename(g_get_user_config_dir(), "dslr-webcam-studio", NULL);
    g_mkdir_with_parents(dir, 0700);
    GDateTime *now = g_date_time_new_now_local();
    char *name = g_date_time_format(now, "diagnostics_%Y%m%d_%H%M%S.txt");
    char *path = g_build_filename(dir, name, NULL);
    g_file_set_contents(path, r->report, -1, NULL);

    GtkWidget *dlg = gtk_dialog_new_with_buttons("Diagnostics", GTK_WINDOW(a->window), GTK_DIALOG_MODAL,
                                                 "_Close", GTK_RESPONSE_CLOSE, NULL);
    gtk_window_set_default_size(GTK_WINDOW(dlg), 760, 520);
    GtkWidget *scroll = gtk_scrolled_window_new(NULL, NULL);
    GtkWidget *view = gtk_text_view_new();
    gtk_text_view_set_monospace(GTK_TEXT_VIEW(view), TRUE);
    gtk_text_view_set_editable(GTK_TEXT_VIEW(view), FALSE);
    char *text = g_strdup_printf("Saved to %s\n\n%s", path, r->report);
    gtk_text_buffer_set_text(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), text, -1);
    gtk_container_add(GTK_CONTAINER(scroll), view);
    gtk_box_pack_start(GTK_BOX(gtk_dialog_get_content_area(GTK_DIALOG(dlg))), scroll, TRUE, TRUE, 0);
    gtk_widget_show_all(dlg);
    gtk_dialog_run(GTK_DIALOG(dlg));
    gtk_widget_destroy(dlg);

    g_free(text); g_free(path); g_free(name); g_free(dir);
    g_date_time_unref(now);
    g_free(r->report);
    g_free(r);
    return G_SOURCE_REMOVE;
}

static gpointer diag_thread(gpointer p)
{
    diag_result *r = p;
    r->report = run_diagnostics();
    g_idle_add(diag_done, r);
    return NULL;
}

static void on_diag(GtkButton *b, gpointer p)
{
    (void)b;
    app_t *a = p;
    gtk_widget_set_sensitive(a->diag_btn, FALSE);
    gtk_widget_set_sensitive(a->start_btn, FALSE);
    gtk_label_set_text(GTK_LABEL(a->state_lbl), "Running diagnostics (about 10 seconds)...");
    diag_result *r = g_new0(diag_result, 1);
    r->a = a;
    g_thread_unref(g_thread_new("diag", diag_thread, r));
}

static void on_coffee(GtkButton *b, gpointer p)
{
    (void)b;
    app_t *a = p;
    gtk_show_uri_on_window(GTK_WINDOW(a->window), DONATE_URL, GDK_CURRENT_TIME, NULL);
}

static gboolean on_delete(GtkWidget *w, GdkEvent *ev, gpointer p)
{
    (void)w; (void)ev;
    stop_streaming(p); // returns the camera's live view to normal while widgets still exist
    return FALSE;
}

// ---- layout -------------------------------------------------------------------------

static GtkWidget *combo(const char **items, int n, int active)
{
    GtkWidget *c = gtk_combo_box_text_new();
    for (int i = 0; i < n; i++) gtk_combo_box_text_append_text(GTK_COMBO_BOX_TEXT(c), items[i]);
    gtk_combo_box_set_active(GTK_COMBO_BOX(c), active);
    return c;
}

static void add_labeled(GtkWidget *flow, const char *label, GtkWidget *w)
{
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
    if (label) gtk_box_pack_start(GTK_BOX(box), gtk_label_new(label), FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(box), w, FALSE, FALSE, 0);
    gtk_container_add(GTK_CONTAINER(flow), box);
}

static int index_of(const int *arr, int n, int v)
{
    for (int i = 0; i < n; i++)
        if (arr[i] == v) return i;
    return 0;
}

static void build_ui(app_t *a)
{
    a->window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    char *title = g_strdup_printf("DSLR Webcam Studio %s", APP_VERSION);
    gtk_window_set_title(GTK_WINDOW(a->window), title);
    g_free(title);
    gtk_window_set_icon_name(GTK_WINDOW(a->window), "dslr-webcam-studio");
    gtk_window_set_default_size(GTK_WINDOW(a->window), 1060, 700);

    GtkWidget *vbox = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    GtkWidget *flow = gtk_flow_box_new();
    gtk_flow_box_set_selection_mode(GTK_FLOW_BOX(flow), GTK_SELECTION_NONE);
    gtk_flow_box_set_max_children_per_line(GTK_FLOW_BOX(flow), 20);
    gtk_flow_box_set_homogeneous(GTK_FLOW_BOX(flow), FALSE);
    gtk_container_set_border_width(GTK_CONTAINER(flow), 6);

    a->start_btn = gtk_button_new_with_label("Start");
    GtkWidget *snap = gtk_button_new_with_label("Snapshot");
    a->diag_btn = gtk_button_new_with_label("Diagnostics");
    gtk_container_add(GTK_CONTAINER(flow), a->start_btn);
    gtk_container_add(GTK_CONTAINER(flow), snap);
    gtk_container_add(GTK_CONTAINER(flow), a->diag_btn);
    if (donate_configured()) {
        GtkWidget *coffee = gtk_button_new_with_label("☕ Buy me a coffee");
        g_signal_connect(coffee, "clicked", G_CALLBACK(on_coffee), a);
        gtk_container_add(GTK_CONTAINER(flow), coffee);
    }

    const char *sources[] = {"Canon EOS (USB)", "Test pattern"};
    const char *res[] = {"640 x 360  (360p)", "1280 x 720  (720p)", "1920 x 1080  (1080p)"};
    const char *fps[] = {"15 fps", "24 fps", "25 fps", "30 fps"};
    const char *fit[] = {"Fit (black side bars)", "Fill (crop to 16:9)"};
    const char *iso[16], *shutter[16];
    for (int i = 0; i < CANON_ISO_CHOICES_N; i++) iso[i] = CANON_ISO_CHOICES[i].name;
    for (int i = 0; i < CANON_SHUTTER_CHOICES_N; i++) shutter[i] = CANON_SHUTTER_CHOICES[i].name;

    a->source_box = combo(sources, 2, 0);
    a->res_box = combo(res, 3, index_of(RESOLUTIONS, 3, size_to_resolution(a->s.width, a->s.height)));
    a->fps_box = combo(fps, 4, index_of(FPS_CHOICES, 4, a->s.fps));
    a->fit_box = combo(fit, 2, a->s.fill ? 1 : 0);
    a->noise_box = combo(LEVEL_NAMES, 4, a->s.noise);
    a->sharp_box = combo(LEVEL_NAMES, 4, a->s.sharp);
    a->iso_box = combo(iso, CANON_ISO_CHOICES_N, -1);
    a->shutter_box = combo(shutter, CANON_SHUTTER_CHOICES_N, -1);
    a->mirror_chk = gtk_check_button_new_with_label("Mirror left/right");
    a->flip_chk = gtk_check_button_new_with_label("Flip upside-down");
    gtk_toggle_button_set_active(GTK_TOGGLE_BUTTON(a->mirror_chk), a->s.fliph);
    gtk_toggle_button_set_active(GTK_TOGGLE_BUTTON(a->flip_chk), a->s.flipv);

    add_labeled(flow, "Source", a->source_box);
    add_labeled(flow, "Streaming Video Output Resolution", a->res_box);
    add_labeled(flow, "Target Streaming Framerate", a->fps_box);
    add_labeled(flow, "Aspect", a->fit_box);
    add_labeled(flow, NULL, a->mirror_chk);
    add_labeled(flow, NULL, a->flip_chk);
    add_labeled(flow, "Noise reduction", a->noise_box);
    add_labeled(flow, "Sharpness", a->sharp_box);
    add_labeled(flow, "Camera ISO", a->iso_box);
    add_labeled(flow, "Shutter", a->shutter_box);

    a->preview = gtk_drawing_area_new();
    gtk_widget_set_hexpand(a->preview, TRUE);
    gtk_widget_set_vexpand(a->preview, TRUE);

    GtkWidget *status = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 16);
    gtk_container_set_border_width(GTK_CONTAINER(status), 4);
    a->state_lbl = gtk_label_new("Stopped");
    a->exposure_lbl = gtk_label_new("");
    a->stats_lbl = gtk_label_new("");
    gtk_label_set_ellipsize(GTK_LABEL(a->state_lbl), PANGO_ELLIPSIZE_END);
    gtk_box_pack_start(GTK_BOX(status), a->state_lbl, TRUE, TRUE, 0);
    gtk_widget_set_halign(a->state_lbl, GTK_ALIGN_START);
    gtk_box_pack_start(GTK_BOX(status), a->exposure_lbl, FALSE, FALSE, 0);
    gtk_box_pack_end(GTK_BOX(status), a->stats_lbl, FALSE, FALSE, 0);

    gtk_box_pack_start(GTK_BOX(vbox), flow, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(vbox), a->preview, TRUE, TRUE, 0);
    gtk_box_pack_start(GTK_BOX(vbox), status, FALSE, FALSE, 0);
    gtk_container_add(GTK_CONTAINER(a->window), vbox);

    g_signal_connect(a->start_btn, "clicked", G_CALLBACK(on_start), a);
    g_signal_connect(snap, "clicked", G_CALLBACK(on_snapshot), a);
    g_signal_connect(a->diag_btn, "clicked", G_CALLBACK(on_diag), a);
    g_signal_connect(a->source_box, "changed", G_CALLBACK(on_source), a);
    GtkWidget *opts[] = {a->res_box, a->fps_box, a->fit_box, a->noise_box, a->sharp_box};
    for (int i = 0; i < 5; i++) g_signal_connect(opts[i], "changed", G_CALLBACK(on_option), a);
    g_signal_connect(a->mirror_chk, "toggled", G_CALLBACK(on_option), a);
    g_signal_connect(a->flip_chk, "toggled", G_CALLBACK(on_option), a);
    g_signal_connect(a->iso_box, "changed", G_CALLBACK(on_camera_setting), a);
    g_signal_connect(a->shutter_box, "changed", G_CALLBACK(on_camera_setting), a);
    g_signal_connect(a->preview, "draw", G_CALLBACK(on_draw), a);
    g_signal_connect(a->window, "delete-event", G_CALLBACK(on_delete), a);
    g_signal_connect(a->window, "destroy", G_CALLBACK(gtk_main_quit), NULL);
    g_timeout_add(250, on_tick, a);
}

int main(int argc, char **argv)
{
    if (argc > 1 && strcmp(argv[1], "--selftest") == 0) return run_selftest();
    if (argc > 1 && strcmp(argv[1], "--diag") == 0) {
        char *r = run_diagnostics();
        fputs(r, stdout);
        g_free(r);
        return 0;
    }
    if (argc > 1 && strcmp(argv[1], "--version") == 0) { printf("DSLR Webcam Studio %s\n", APP_VERSION); return 0; }

    gtk_init(&argc, &argv);
    app_t a = {0};
    a.config = settings_path();
    settings_load(&a.s, a.config);
    build_ui(&a);
    gtk_widget_show_all(a.window);
    start_streaming(&a);
    gtk_main();
    g_free(a.config);
    return 0;
}
