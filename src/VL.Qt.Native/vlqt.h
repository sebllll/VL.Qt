/* QtNative (VL.Qt native layer) – pure C ABI, cdecl, UTF-8 strings. Return 0 = ok unless noted. */
#pragma once

#ifdef _WIN32
#  ifdef VLQT_BUILD
#    define VLQT_API __declspec(dllexport)
#  else
#    define VLQT_API __declspec(dllimport)
#  endif
#elif defined(VLQT_BUILD)
#  define VLQT_API __attribute__((visibility("default"))) /* built with -fvisibility=hidden */
#else
#  define VLQT_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* level: 0 Debug, 1 Info, 2 Warning, 3 Critical, 4 Fatal */
typedef void (*vlqt_log_fn)(int level, const char* category, const char* message);

/* Averages/maxima over the last ~1 s of pumps, all times in ms. */
typedef struct vlqt_perf {
    double fps;            /* from the average interval between swaps */
    double frame_ms, frame_ms_max;
    double pump_ms, pump_ms_max;   /* processEvents incl. rendering, on the vvvv thread */
    double sync_ms, render_ms, present_ms;
    double gpu_ms, gpu_ms_max;     /* 0 = GPU timestamps unavailable */
    double swaps_per_pump;
    int samples;
} vlqt_perf;

/* One pump (vlqt_app_process_events) of a window, times in ms (#243). */
typedef struct vlqt_perf_sample {
    float frame;    /* interval between the last two frame swaps (0 = none yet) */
    float pump;     /* whole vlqt_app_process_events */
    float sync, render, present; /* frame phases, summed over the swaps of this pump */
    float gpu;      /* QRhi timestamps of the last completed frame, 0 = unavailable */
    float events;   /* pump - sync - render - present: posted events, input, polish, animations (>= 0) */
    int swaps;      /* frames swapped in this pump */
} vlqt_perf_sample;

/* Percentiles over the ring (last up to 240 pumps; frame only over pumps with a swap whose previous pump also had one,
 * i.e. intervals between consecutive frames), nearest rank, and counters.
 * Computed at query time only. Counters: since window creation and over the last 10 s (1 s buckets).
 *   over_budget   pump > budget_ms
 *   long_frames   frame interval > 1.5 x budget_ms and the previous pump also had a frame (a refresh was skipped =
 *                 missed frame; the interval after idle pumps of on-demand rendering does not count)
 *   idle          swaps == 0 (no frame in the pump, mostly because nothing changed)
 *   multi_swap    swaps > 1 */
typedef struct vlqt_perf_stats {
    double budget_ms;   /* 1000 / refresh rate of the window's screen, fallback 60 Hz */
    double frame_p50, frame_p95, frame_p99, frame_max;
    double pump_p50, pump_p95, pump_p99, pump_max;
    int samples;        /* pumps in the ring */
    int frames;         /* of those with a consecutive frame interval */
    long long pumps, over_budget, long_frames, idle, multi_swap;
    long long recent_pumps, recent_over_budget, recent_long_frames, recent_idle, recent_multi_swap;
} vlqt_perf_stats;

/* Details of the last vlqt_window_apply of a window (#92/#275), see vlqt_window_get_apply_error. */
typedef struct vlqt_apply_error {
    int rc;              /* return code of the last apply, 0 = it succeeded (then all other fields are 0/empty) */
    int command;         /* index of the bad command in the buffer */
    unsigned int id;     /* object id the bad command addresses, 0 = none (CLEAR_COLOR, RESET, truncated header, root) */
    unsigned short op;   /* VLQT_OP_* of the bad command, 0 if its header was truncated */
    unsigned short prop; /* VLQT_PROP_* of a bad SET, else 0 */
    const char* reason;  /* UTF-8 reason without the "apply: command #n ..." prefix; valid until the next apply of the window */
} vlqt_apply_error;

/* What the host (VL.Qt, .NET) measured around one pump of a window (#245), times in ms. Reported after the pump with
 * vlqt_window_report_host_frame; "since the previous host pump" = one vvvv frame. */
typedef struct vlqt_host_frame {
    float apply;       /* vlqt_window_apply of this window in this pump (incl. resync) */
    float reconcile;   /* reconcile + command writing in the node updates since the previous host pump */
    float host;        /* the whole host pump (all windows: apply, advance, processEvents, poll) */
    float interval;    /* since the previous host pump of the same vvvv app (#314; 0 = first) */
    float gc_pause;    /* .NET GC pause since the previous host pump */
    int ops;           /* commands applied to this window in this pump */
    int gc0, gc1, gc2; /* .NET collections per generation since the previous host pump */
    long long alloc_bytes; /* .NET bytes allocated (process) since the previous host pump */
    /* #260: the vvvv side of the frame before this pump (0 = unknown). interval ~ previous host + wait + vvvv. */
    float vvvv;        /* vvvv frame start (IFrameClock ticks; fallback the clock's update time) -> start of this pump */
    float wait;        /* end of the previous host pump -> vvvv frame start (frame timer / vsync / other apps) */
} vlqt_host_frame;

/* Host frames of the last ~1 s of pumps (the vlqt_perf range); only pumps with a report count. Rates per second over
 * the summed intervals. */
typedef struct vlqt_host_stats {
    double apply_ms, apply_ms_max;
    double reconcile_ms, reconcile_ms_max;
    double host_ms, host_ms_max;
    double ops, ops_max;               /* per pump */
    double gc0_per_s, gc1_per_s, gc2_per_s;
    double gc_pause_ms_per_s;
    double alloc_bytes, alloc_bytes_max; /* per host frame */
    int samples;
    double vvvv_ms, vvvv_ms_max;       /* #260: vlqt_host_frame.vvvv, average/max */
    double wait_ms, wait_ms_max;       /* #260: vlqt_host_frame.wait, average/max */
} vlqt_host_stats;

/* One resource of vlqt_res_list (#359): introspection for the F5 overlay (images package 07). */
typedef struct vlqt_res_info {
    unsigned long long id;  /* index | generation << 32 */
    int kind;               /* VLQT_RES_KIND_* */
    int state;              /* VLQT_RES_STATE_* (Released ones are gone from the list) */
    long long bytes;        /* uploaded bytes */
    int host_refs;          /* 1 until vlqt_res_release, then 0 */
    int element_refs;       /* references of elements (images package 04) */
} vlqt_res_info;

/* Pixels of vlqt_res_set_pixels (#360). */
typedef struct vlqt_pixel_desc {
    int width, height;      /* > 0 */
    int row_bytes;          /* bytes per row incl. padding, <= 0 = width * bytes per pixel */
    int format;             /* VL PixelFormat number, table in protocol.h */
    int premultiplied;      /* != 0: color already multiplied with alpha */
    int flags;              /* vlqt_res_set_pixels: VLQT_IMG_* (#546); 0 when QtNative fills the desc */
} vlqt_pixel_desc;

VLQT_API int         vlqt_app_init(const char* nativeDir, vlqt_log_fn log); /* idempotent */
VLQT_API void        vlqt_app_process_events(void);
/* #314: like vlqt_app_process_events, but only the given windows (those of one vvvv app) may render in this pump.
 * Update requests of other windows stay pending until their own app's pump. windows NULL = all windows. */
VLQT_API void        vlqt_app_process_events_for(void* const* windows, int count);
VLQT_API int         vlqt_driver_advance(double seconds);
VLQT_API const char* vlqt_last_error(void);

/* Creates the window (hidden; vlqt_window_show shows it). NULL = error. Geometry in Qt logical pixels (DIP), the
 * convention of the Renderer's Bounds pin and of EV_WINDOW_GEOMETRY (round trip stable):
 *   x, y           outer top-left incl. frame (QWindow::setFramePosition, set before show). x or y NaN/inf =
 *                  centered on the primary screen's working area. A window rect on no screen is centered as well.
 *   width, height  client size (QWindow::resize). < 1 = 600x400.
 *   swapInterval   0 = Present never waits, 1 = VSync. Fixed at creation.
 * Wayland: clients cannot place their windows, the position (and centering) is ignored there, only the size applies. */
VLQT_API void* vlqt_window_create_at(float x, float y, int width, int height, const char* title, int swapInterval);
/* Short form: vlqt_window_create_at(NaN, NaN, width, height, title, 0) = centered, no VSync. */
VLQT_API void* vlqt_window_create(int width, int height, const char* title);
/* Pipeline cache (#488): call before the first window. Sets load + save file of the QQuickGraphicsConfiguration of
 * every window created afterwards (driver compiled pipelines survive restarts). utf8Path NULL or empty = off (the
 * default). A cache of another GPU/driver is dropped by Qt; a file that cannot be written only logs a warning.
 * 0 / -7. */
VLQT_API int   vlqt_set_pipeline_cache(const char* utf8Path);
VLQT_API void  vlqt_window_destroy(void* w);
VLQT_API void  vlqt_window_show(void* w);
VLQT_API void  vlqt_window_hide(void* w);
VLQT_API int   vlqt_window_take_close_request(void* w); /* 1 = X was clicked since last call (close is swallowed) */
VLQT_API int   vlqt_window_is_visible(void* w); /* 1 = visible, 0 = hidden */
/* Runtime changes of the create parameters (Renderer [Qt] Title/Size pins). 0 = ok, -1 null window. Size in Qt logical pixels (DIP). */
VLQT_API int   vlqt_window_set_title(void* w, const char* title);
VLQT_API int   vlqt_window_resize(void* w, int width, int height);
/* Window icon (#517): QWindow::setIcon. utf8Path = .ico/.png/.svg (absolute path), NULL or empty = embedded default
 * icon (also applied by vlqt_window_create_at before show). Live, any time. 0 = ok, -1 null window, -7 file not
 * loadable (the default icon is shown instead; vlqt_last_error has the reason). */
VLQT_API int   vlqt_window_set_icon(void* w, const char* utf8Path);
/* Window modes (vlqt_window_mode, protocol.h), live, without re-creating the window; before or after show.
 * Only the bits in mask change (to their value in value), unknown bits are ignored. 0 = ok, -1 null window.
 *   BORDERLESS  Qt::FramelessWindowHint; the client area stays where it is (#233).
 *   FULLSCREEN  fullscreen on the screen with the largest share of the window; off restores the geometry from
 *               before (#234). BORDERLESS changes while fullscreen take effect on leaving fullscreen.
 *               F11 / Alt+Enter in the window toggle this bit (like VL.Skia). No effect on eglfs (always fullscreen).
 * EV_WINDOW_GEOMETRY reports the resulting state (VLQT_WINDOW_FULLSCREEN). vlqt_window_get_mode = requested modes. */
VLQT_API int          vlqt_window_set_mode(void* w, unsigned int mask, unsigned int value);
VLQT_API unsigned int vlqt_window_get_mode(void* w);
VLQT_API int   vlqt_window_grab(void* w, const char* pngPath); /* test */

VLQT_API int   vlqt_window_get_perf(void* w, vlqt_perf* out);
/* Last completed pump of the window. 0 = ok, -1 null window/out, -2 no pump yet. */
VLQT_API int   vlqt_window_get_perf_sample(void* w, vlqt_perf_sample* out);
/* Percentiles, counters, budget (see vlqt_perf_stats). 0 = ok, -1 null window/out. */
VLQT_API int   vlqt_window_get_perf_stats(void* w, vlqt_perf_stats* out);
/* Test: the vlqt_perf_stats computation on n given samples with timestamps t_ms (ascending, ms; "now" = t_ms[n-1])
 * and a fixed budget. 0 = ok, -1 null/negative argument. */
VLQT_API int   vlqt_perf_eval(const vlqt_perf_sample* samples, const double* t_ms, int n, double budget_ms, vlqt_perf_stats* out);
/* #245: host values of the last completed pump of the window (call after vlqt_app_process_events).
 * 0 = ok, -1 null window/in, -2 no pump yet. */
VLQT_API int   vlqt_window_report_host_frame(void* w, const vlqt_host_frame* in);
/* Last reported host frame. 0 = ok, -1 null window/out, -2 none (the last pump has no report). */
VLQT_API int   vlqt_window_get_host_frame(void* w, vlqt_host_frame* out);
/* Averages/maxima/rates over the last ~1 s (see vlqt_host_stats). 0 = ok, -1 null window/out. */
VLQT_API int   vlqt_window_get_host_stats(void* w, vlqt_host_stats* out);
/* Test: the vlqt_host_stats computation on n given host frames. 0 = ok, -1 null/negative argument. */
VLQT_API int   vlqt_perf_host_eval(const vlqt_host_frame* frames, int n, vlqt_host_stats* out);
/* bit0 = perfmeter panel (F2), bit1 = stats (F5); the keys toggle the same flags */
VLQT_API int   vlqt_window_set_overlay(void* w, int flags);
VLQT_API int   vlqt_window_get_overlay(void* w);
/* Debug overlay for all elements of the window (Renderer [Qt] Debug, #279); per element: VLQT_PROP_DEBUG.
 * on != 0 = on. Frame cyan, origin magenta, anchor yellow, baseline green, ... (legend: debug.h). 0 = ok, -1 null window. */
VLQT_API int   vlqt_window_set_debug(void* w, int on);
VLQT_API int   vlqt_window_get_debug(void* w); /* 1 = on */

/* Command buffer (protocol.h). Returns 0 = ok; on error <0 and vlqt_last_error() names command index +
 * reason; processing stops at the bad command, earlier commands stay applied.
 * -1 null window, -2 bad length/alignment, -3 unknown op, -4 unknown id, -5 unknown type,
 * -6 unknown property, -7 bad value (type mismatch / rejected), -8 invalid (duplicate id, cycle, root) */
VLQT_API int   vlqt_window_apply(void* w, const unsigned char* buf, int len);
/* Structured form of the last apply error of the window (the object id lets the host show it at the element).
 * 0 = ok, -1 null window/out. */
VLQT_API int   vlqt_window_get_apply_error(void* w, vlqt_apply_error* out);
/* Writes queued events into out, returns bytes written (0 = none); if cap is too small returns -required
 * (always <= -8, events stay queued). -1 = null window. Call once per frame after vlqt_app_process_events:
 * the poll first queues EV_MEASURED / EV_SCENE_BOUNDS of bound items whose value changed and EV_WINDOW_GEOMETRY of
 * the window after a move/resize/state change (events.h). */
VLQT_API int   vlqt_window_poll(void* w, unsigned char* out, int cap);

/* Resources (#359, resources.h, protocol.h vlqt_res_*): window independent, ids u64 = index | generation << 32.
 * Main thread (the thread of vlqt_app_process_events) only. Return codes: 0 = ok, -1 internal error (log),
 * -7 bad argument / not allowed in this state, -9 unknown, stale or already released id (warning in the log). */
/* New resource of kind (VLQT_RES_KIND_*) in state Pending, host reference 1. */
VLQT_API int vlqt_res_create(int kind, unsigned long long* outId);
/* Drops the host reference; freed at the end of a pump once no element holds it either (a running decode is
 * cancelled or finishes first). No status record for the id after this call. */
VLQT_API int vlqt_res_release(unsigned long long id);
/* Upload bytes per pump (default 8 MB), > 0. Uploads run in vlqt_app_process_events before the windows render. */
VLQT_API int vlqt_res_set_budget(int bytes);
/* Status queue (protocol.h, resources): writes all queued records into out, returns the bytes written (0 = none);
 * if cap is too small returns -required (records stay queued). Call once per frame after vlqt_app_process_events. */
VLQT_API int vlqt_res_poll(unsigned char* out, int cap);
/* Blocks until the resource leaves Decoding/Uploading or timeoutMs passed: a queued decode runs on the calling thread,
 * completely (the timeout applies to waiting for a worker), one in a worker is awaited, then the upload runs at once (outside the budget). Returns the state then
 * (VLQT_RES_STATE_*; Ready/Failed, or the current state on timeout or when it was never started) or -9. */
VLQT_API int vlqt_res_wait(unsigned long long id, int timeoutMs);
/* Live resources (also released ones until the pump frees them): writes min(count, cap) into out, returns count. */
VLQT_API int vlqt_res_list(vlqt_res_info* out, int cap);
/* Cancels and joins the workers and frees every resource (leak warning with the count of still referenced ones).
 * Call before Qt is torn down / at process end; the next start creates the workers again. */
VLQT_API void vlqt_res_shutdown(void);
/* Image resources (#360, image_resource.h): start or replace the content of a VLQT_RES_KIND_IMAGE resource. Decode
 * and conversion run in a worker, the upload in the pump within the budget (vlqt_res_wait: at once). New content on
 * a Ready id keeps the old texture visible until the new one is complete; while a decode/upload runs only the newest
 * content is kept. 0 / -7 (wrong kind, bad argument) / -9. A missing or broken file / an unknown format ends in
 * Failed with RES_EV_ERROR. */
/* File (UTF-8 path; PNG, JPG, ... via QImageReader, EXIF rotation applied). flags: VLQT_IMG_*. */
VLQT_API int vlqt_res_load_image(unsigned long long id, const char* utf8Path, unsigned flags);
/* Pixels of C#: copied synchronously (data may be reused after the call), len = bytes readable at data. */
VLQT_API int vlqt_res_set_pixels(unsigned long long id, const vlqt_pixel_desc* desc, const void* data, int len);
/* Shader resources (#437, shader_resource.h, protocol.h "ShaderEffect"): start or replace the content of a
 * VLQT_RES_KIND_SHADER resource. utf8Path: a .qsb file or "builtin:<name>" (shaders/<name>.frag|.vert.qsb next to
 * QtNative); stage: VLQT_SHADER_STAGE_*. The file is read and checked in a worker. New content on a Ready id keeps
 * the old shader until the new one is Ready. 0 / -7 (wrong kind, empty path, bad stage) / -9. Missing file, not a
 * .qsb, wrong stage, no variant for the backend: Failed with RES_EV_ERROR. */
VLQT_API int vlqt_res_load_shader(unsigned long long id, const char* utf8Path, int stage);
/* Shader from source (#483, protocol.h "ShaderEffect"): utf8Source = full Qt GLSL (#version 440, NUL-terminated,
 * copied at the call), baked by QShaderBaker in a worker for the backend of the process, cached per hash of the source.
 * Same resource rules as vlqt_res_load_shader. 0 / -7 (wrong kind, empty source, bad stage) / -9. Bake errors
 * (compiler message with line numbers) and failed checks: Failed with RES_EV_ERROR. */
VLQT_API int vlqt_res_load_shader_source(unsigned long long id, const char* utf8Source, int stage);
/* Shader reflection (#488, shader node factory): bakes utf8Source (full Qt GLSL, NUL-terminated) synchronously to
 * SPIR-V 100 only (under the same mutex as the runtime bakes, never in parallel) and writes the result as UTF-8 text
 * to buf. Returns the required length in bytes without terminator; if it is > bufLen, call again with a larger buffer
 * (buf is then untouched or partially written, NUL-terminated only if there is room). -7: empty source, stage not
 * VLQT_SHADER_STAGE_*, bufLen < 0. No QGuiApplication needed, any thread.
 * Text: one entry per line, '\n'-separated, fields separated by one space:
 *   line 1: "ok" or "error"
 *   error:  the error lines as in a RES_EV_ERROR of a bake ("<stage> code, line <n>: <text>")
 *   ok:     "uniform <name> <type> <arrayLen>"  in block order (offset), including qt_Matrix / qt_Opacity;
 *                                              arrayLen 0 = no array
 *           "sampler <name> <binding>"         in binding order
 *   <type>: GLSL name: float vec2 vec3 vec4 int ivec2 ivec3 ivec4 uint uvec2 uvec3 uvec4 bool mat2 mat3 mat4
 *           sampler2D, anything else "other". */
VLQT_API int vlqt_shader_reflect(const char* utf8Source, int stage, char* buf, int bufLen);
/* TEST ONLY: content of a Ready image as RGBA8 premultiplied (texture readback). Returns the bytes written, -required
 * if cap is too small (width/height set), -9 if not Ready, -1 readback failed. */
VLQT_API int vlqt_test_res_read_pixels(unsigned long long id, unsigned char* out, int cap, int* width, int* height);
/* Snapshot resources (#363, protocol.h): main thread. Starts grabbing element elementId of window w (scale > 0:
 * pixel size = item size in DIP * scale, ignoring the screen's device pixel ratio) into a VLQT_RES_KIND_SNAPSHOT
 * resource. Asynchronous: the result arrives with RES_EV_INFO / STATE Ready in a later pump (at least one render of
 * the window), errors as Failed with RES_EV_ERROR. A snapshot while one is in flight replaces it (only the newest
 * result is kept); vlqt_res_release during a grab drops the result. A snapshot on a Failed id starts over (Failed ->
 * Decoding -> INFO + Ready | Failed), on a Ready id it stays Ready until the new pixels replace the old ones (INFO).
 * A grab never stays in Decoding: element/window destroyed, window no longer exposed or no result within 10 s ->
 * Failed. vlqt_res_wait does not block on a snapshot (returns the current state). 0 / -7 (wrong kind, w NULL,
 * scale <= 0) / -9; element errors (unknown, not in the window, no size) are Failed + RES_EV_ERROR, rc 0.
 * Qt Quick software backend (QT_QUICK_BACKEND=software, e.g. Linux offscreen): Qt renders the grab into an opaque
 * pixmap, so the pixels have alpha 255 and transparent areas are black (premultiplied colors stay right). */
VLQT_API int vlqt_res_snapshot(unsigned long long id, void* w, unsigned elementId, float scale);
/* Pixels of the last finished snapshot (Ready, also while a replacing one is in flight): fills out (width, height,
 * row_bytes = width * 4, format = 9 B8G8R8A8, premultiplied = 1), copies rows top-down into dst, returns the bytes
 * written; -required if cap is too small (out filled, nothing copied), -7 wrong kind or no snapshot yet, -9. */
VLQT_API int vlqt_res_read_pixels(unsigned long long id, void* dst, int cap, vlqt_pixel_desc* out);
/* Tooltip preview of an image resource (#546), only on a Ready resource: the worker made it when the content was
 * loaded with VLQT_IMG_PREVIEW (load_image flags / set_pixels desc.flags), longest edge <= 256 px, never upscaled.
 * Fills desc (width, height, row_bytes = width * 4, format = 9 B8G8R8A8, premultiplied = 1), copies the rows top-down
 * into dst and returns the bytes written; -required if cap is too small (desc filled, nothing copied), -7 wrong kind
 * or no preview (no flag, not Ready yet), -9. Main thread. */
VLQT_API int vlqt_res_read_preview(unsigned long long id, unsigned char* dst, int cap, vlqt_pixel_desc* desc);
/* TEST ONLY: writes a test image (vertical stripes red, green, blue, white) as PNG/JPG by extension. 0 / -1 / -7. */
VLQT_API int vlqt_test_write_image(const char* utf8Path, int width, int height, int quality);
/* TEST ONLY: starts a Pending VLQT_RES_KIND_TEST resource: decode sleeps decodeMs in a worker (cancellable), upload
 * uses uploadBytes of budget in stripes. flags bit0 = decode fails, bit1 = upload fails. 0 / -7 / -9. */
VLQT_API int vlqt_test_res_start(unsigned long long id, int decodeMs, long long uploadBytes, int flags);
/* TEST ONLY: the state machine's transition (VLQT_RES_STATE_*), 0 = done, -7 = rejected (logged), -9. */
VLQT_API int vlqt_test_res_transition(unsigned long long id, int from, int to);
/* TEST ONLY: element reference +1 (delta 1) or -1 (delta -1), like an element of package 04. 0 / -7 / -9. */
VLQT_API int vlqt_test_res_element_ref(unsigned long long id, int delta);

/* TEST ONLY (SmokeTest, plan 06c): synthetic mouse input, not for production use.
 * Sends a QMouseEvent (VLQT_MOUSE_MOVE with the buttons held, PRESS/RELEASE = left button, *_RIGHT = right button) or a QEvent::Leave
 * (VLQT_MOUSE_LEAVE, x/y ignored) to the window via QCoreApplication::sendEvent; x/y in window logical pixels.
 * Delivered synchronously, no processEvents needed. 0 = ok, -1 null window, -2 unknown type.
 * With env VLQT_TEST_SYNTHETIC_INPUT=1 (read at vlqt_window_create) windows ignore real (OS) pointer input. */
VLQT_API int   vlqt_test_send_mouse(void* w, int type, float x, float y);
/* TEST ONLY (#319): synthetic touch input, not for production use. type = vlqt_touch (protocol.h) for touch point id,
 * x/y in window logical pixels. QtNative keeps the active points per window and sends the complete QTouchEvent
 * (TouchBegin/Update/End, every active point with its state, others stationary) from one test touchscreen
 * (QPointingDevice, registered once) via QCoreApplication::sendEvent: synchronous, not spontaneous (passes
 * VLQT_TEST_SYNTHETIC_INPUT). Several ids = multitouch. 0 = ok, -1 null window, -2 unknown type, PRESS of an active id
 * or MOVE/RELEASE of an inactive one. */
VLQT_API int   vlqt_test_send_touch(void* w, int type, int id, float x, float y);
/* TEST ONLY (#320): synthetic wheel input, not for production use. Sends a QWheelEvent at x/y (window logical pixels)
 * with angleDelta (dx, dy) in eighths of a degree (120 = one notch) via QCoreApplication::sendEvent (synchronous, not
 * spontaneous: passes VLQT_TEST_SYNTHETIC_INPUT). 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_send_wheel(void* w, float x, float y, int dx, int dy);
/* TEST ONLY: number of pointer handlers (QQuickPointerHandler children) of item id (input handlers, #319);
 * -1 null window, -4 unknown id. */
VLQT_API int   vlqt_test_handler_count(void* w, unsigned int id);
/* TEST ONLY (#244): busy-waits ms inside one phase of the window's next rendered frame (0 events, 1 sync, 2 render,
 * 3 present) to check that the F5 timeline attributes a spike. 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_perf_spike(void* w, int phase, float ms);
/* TEST ONLY (#246, #260): plain UTF-8 text of an F5 page (0 Summary, 1 Timeline, 2 Scene, 3 System, 4 Resources; others empty), built on demand, overlay state
 * unchanged. Returns the byte length without NUL; -(length + 1) if cap is too small; -1 null window. */
VLQT_API int   vlqt_test_perf_text(void* w, int page, char* out, int cap);
/* TEST ONLY (#358): QRhi of the window and its native device (ID3D11Device* on D3D11, else null). Null window = the
 * upload QRhi on the shared device. 0 = ok, -2 no QRhi (scenegraph not initialized yet, or no shared device). */
VLQT_API int   vlqt_test_device_info(void* w, void** device, void** rhi);
/* TEST ONLY (#367): makes the shared graphics device lost, mode = vlqt_device_lost (protocol.h). Returns at once; the
 * QtNative notices the loss in the next pump and rebuilds device, windows and resources (device.h). 0 = ok, -2 not
 * possible (no shared device, or REMOVE without VLQT_TEST_D3D11ON12=1), -7 unknown mode. */
VLQT_API int   vlqt_test_device_lost(int mode);
/* TEST ONLY (#244): sends a key press (Qt::Key, Qt::KeyboardModifiers) to the window, e.g. F5 page cycling.
 * 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_send_key(void* w, int key, int modifiers);
/* TEST ONLY (#210): key press + release with text (UTF-8, may be null/empty) to the window's focus item, e.g. typing
 * digits into a SpinBox (key Qt::Key_1, text "1") or Enter (Qt::Key_Return, "\r"). 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_send_key_text(void* w, int key, int modifiers, const char* text);

/* Tooltip previews (#543, package 01 #544): main thread. */
/* Number of frames in which the scene of window w changed since it was created (item/reconciler/video update(),
 * polish, resize): not every pump, and not the render a grab (vlqt_res_snapshot / _fit) triggers by itself, so a
 * static scene keeps the count while snapshots run. Compare two values for "changed since". >= 0, -9 null window. */
VLQT_API long long vlqt_window_change_count(void* w);
/* Like vlqt_res_snapshot, but the scale is chosen so that the longest edge of the image is <= maxEdgePx and the scale
 * is <= 1 (pixels per DIP, never above the DIP size; the screen's device pixel ratio is ignored): a 1000 x 500 DIP
 * element at 256 gives 256 x 128, a 100 x 50 one gives 100 x 50. Same resource rules and errors as vlqt_res_snapshot.
 * 0 / -7 (wrong kind, w NULL, maxEdgePx <= 0) / -9. */
VLQT_API int   vlqt_res_snapshot_fit(unsigned long long id, void* w, unsigned elementId, int maxEdgePx);

#ifdef __cplusplus
}
#endif
