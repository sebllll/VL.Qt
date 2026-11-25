/* QtNative (VL.Qt native layer) – pure C ABI, cdecl, UTF-8 strings.
 *
 * Names: vlqt_<group>_<verb>, one section per group in this order: app (process, main thread), scene (a window or an
 * offscreen scene), window (window only), offscreen (offscreen scene only), res (lifecycle of any resource), image,
 * shader, snapshot, audio tap, font.
 *
 * Handles. vlqt_scene* is a window (vlqt_window_create) or an offscreen scene (vlqt_offscreen_create); the vlqt_scene_*
 * functions take both, the vlqt_window_* functions are no-ops or VLQT_ERR_UNSUPPORTED on an offscreen scene (see each
 * function), the vlqt_offscreen_* functions return VLQT_ERR_NULL_HANDLE for a window. A handle is valid until
 * vlqt_scene_destroy. Resources and fonts have u64 ids, not handles.
 *
 * Return convention. An export that can fail returns an int: >= 0 = success (0, or the count or state its comment
 * names), < 0 = a code of vlqt_result. An export that returns a pointer returns NULL on failure; the few with another
 * return type say what they return on failure. Every failing call sets the text of vlqt_last_error.
 *
 * vlqt_last_error returns the UTF-8 text of the last failing call on the calling thread (empty = none yet). It is valid
 * until the next failing call on the same thread; a successful call does not clear it. Read it right after the
 * failing call, on the same thread.
 *
 * Buffers. An export that fills a caller buffer (buf, cap) has two out counts, int* written and int* required (each
 * may be NULL). It sets both on every return and copies all or nothing:
 *   VLQT_OK                    the content is in buf; *written = *required = its size in bytes.
 *   VLQT_ERR_BUFFER_TOO_SMALL  nothing copied; *written = 0, *required = the size buf needs. Call again with a larger
 *                              buffer (the content may have grown in between).
 *   any other code             *written = *required = 0.
 * buf may be NULL when cap is 0 (asks for the size); cap < 0 is VLQT_ERR_BAD_ARGUMENT. Text is UTF-8 with a terminating
 * NUL, which both counts include.
 *
 * Exceptions never cross the ABI: an exception inside QtNative returns VLQT_ERR_INTERNAL (NULL for a pointer) with its
 * text in vlqt_last_error.
 *
 * Threads. Every export runs on the Qt GUI thread, the thread that called vlqt_app_init ("main thread" in this file; the
 * vvvv main thread), except the six marked "any thread": vlqt_app_get_abi_version, vlqt_last_error,
 * vlqt_image_write_file, vlqt_shader_reflect, vlqt_audio_tap_read and vlqt_audio_tap_now_ns. On another thread a
 * main-thread export does nothing and returns VLQT_ERR_WRONG_THREAD (NULL for a pointer; a void export only sets
 * vlqt_last_error). A null handle is VLQT_ERR_NULL_HANDLE on every thread. Before vlqt_app_init the main thread is not
 * known yet and every thread passes the check.
 *
 * Startup: Qt may abort the process at vlqt_app_init when no platform plugin can be loaded (qFatal); the log then has
 * the reason. */
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

/* Return codes of every export, of vlqt_test.h and of the testkit (see the top of this file). -2 ... -8 except -7 come
 * only from vlqt_scene_apply (and vlqt_apply_error.rc); -20 ... -25 only from the offscreen exports. */
typedef enum vlqt_result {
    VLQT_OK = 0,
    VLQT_ERR_NULL_HANDLE = -1,        /* null handle, or one of the wrong kind (a window where a scene is needed) */
    VLQT_ERR_BAD_LENGTH = -2,         /* apply: truncated buffer, wrong payload length for the op or value type, bad alignment */
    VLQT_ERR_UNKNOWN_OP = -3,         /* apply: unknown VLQT_OP_* */
    VLQT_ERR_UNKNOWN_OBJECT = -4,     /* apply: unknown object id (also vlqt_test_handler_count) */
    VLQT_ERR_UNKNOWN_TYPE = -5,       /* apply: unknown element type */
    VLQT_ERR_UNKNOWN_PROPERTY = -6,   /* apply: property, field or state the element type does not have */
    VLQT_ERR_BAD_ARGUMENT = -7,       /* argument out of range or not allowed in this state; apply: value of the wrong
                                         type or rejected by the setter */
    VLQT_ERR_INVALID = -8,            /* apply: id 0 or already used, cycle, root */
    VLQT_ERR_UNKNOWN_ID = -9,         /* resource id or font handle unknown, stale or released */
    VLQT_ERR_NO_DATA = -10,           /* nothing to read yet: no pump, no host pump, no snapshot, no preview, no QRhi */
    VLQT_ERR_WRONG_THREAD = -11,      /* called on another thread than the one the function needs */
    VLQT_ERR_BUFFER_TOO_SMALL = -12,  /* *required holds the size (see "Buffers" above) */
    VLQT_ERR_UNSUPPORTED = -13,       /* file format, platform or graphics API, or not available on an offscreen scene */
    VLQT_ERR_IO = -14,                /* a file could not be read or written */
    VLQT_ERR_NOT_INITIALIZED = -15,   /* vlqt_app_init has not run */
    VLQT_ERR_UNBOUND = -20,           /* offscreen: the scene has no texture (vlqt_test_offscreen_state) */
    VLQT_ERR_ADAPTER_MISMATCH = -21,  /* offscreen: foreign device on another adapter than QtNative's device (warning once) */
    VLQT_ERR_OPEN_FAILED = -22,       /* offscreen: OpenSharedResource1 failed or the handle is no usable Texture2D */
    VLQT_ERR_FORMAT_UNSUPPORTED = -23,/* offscreen: format not allowed or not compatible with the texture's format */
    VLQT_ERR_FENCE_FAILED = -24,      /* offscreen: no ID3D11Device5/ID3D11DeviceContext4, or fence create/share/open failed */
    VLQT_ERR_DEVICE_LOST = -25,       /* offscreen: QtNative's device is lost or being rebuilt: try again next frame */
    VLQT_ERR_INTERNAL = -100          /* exception or Qt/driver failure inside QtNative */
} vlqt_result;

/* A window or an offscreen scene (see "Handles" at the top). Opaque. */
typedef struct vlqt_scene vlqt_scene;

/* ===== app: main thread, except vlqt_app_get_abi_version and vlqt_last_error ===== */

/* Log callback of vlqt_app_init: a plain cdecl function the host keeps alive for the life of the process (from .NET a
 * static [UnmanagedCallersOnly] method, no delegate). Called on any thread (Qt messages of render threads and workers
 * too), also during vlqt_app_init. level: 0 Debug, 1 Info, 2 Warning, 3 Critical, 4 Fatal. category and message are
 * UTF-8, valid only during the call. It must not unwind (throw) into QtNative. */
typedef void (*vlqt_log_fn)(int level, const char* category, const char* message);

/* ABI version of this library (VLQT_ABI_VERSION, protocol.h): protocol constants, struct layouts and signatures of this
 * header. Needs no vlqt_app_init; the host reads it first and refuses a library with another number. Any thread. */
VLQT_API int         vlqt_app_get_abi_version(void);
/* Starts Qt on the calling thread, which is the Qt GUI thread ("main thread") from then on. log may be NULL; it is set
 * once (idempotent: a second call changes nothing; from another thread it is VLQT_ERR_WRONG_THREAD). */
VLQT_API int         vlqt_app_init(const char* nativeDir, vlqt_log_fn log);
/* Pipeline cache: sets load + save file of the QQuickGraphicsConfiguration of every window created afterwards
 * (driver compiled pipelines survive restarts). utf8Path NULL or empty = off (the default). A call after the first
 * window has no effect on the windows that exist. A cache of another GPU/driver is dropped by Qt; a file that cannot
 * be written only logs a warning. Always 0. */
VLQT_API int         vlqt_app_set_pipeline_cache(const char* utf8Path);
/* One pump: resources, posted and window events, rendering. Only the given windows (those of one vvvv app) may render
 * in this pump; update requests of other windows stay pending until their own app's pump. windows NULL = all windows
 * (count ignored). 0, VLQT_ERR_NOT_INITIALIZED, VLQT_ERR_UNSUPPORTED when called inside the pump (from a host callback
 * that processEvents dispatched): the nested call does nothing, the outer pump goes on (a warning is logged once). */
VLQT_API int         vlqt_app_process_events_for(vlqt_scene* const* windows, int count);
/* The frame budget in ms: vvvv's frame interval, which QtNative renders in step with. Each window of a pump takes it at
 * the end of the pump and judges that pump's Qt work against it (vlqt_perf_stats: budget_ms, over_budget, long_frames;
 * the colours and the red highlight of the F2/F5 overlay). VL.Qt calls it before each vlqt_app_process_events_for with
 * the interval of its frame clock; 1000 / 60 until the first call. Process-wide, needs no vlqt_app_init. Qt GUI thread.
 * 0, VLQT_ERR_BAD_ARGUMENT (ms not in (0, 1000), NaN), VLQT_ERR_WRONG_THREAD. */
VLQT_API int         vlqt_app_set_frame_budget(double ms);
/* delivers the window system events Qt has queued (input of all windows, also geometry and close) without
 * rendering: update requests stay pending and an Expose is deferred, both render in the next pump. Call at the start
 * of the host frame, then vlqt_scene_poll, so input that arrived between two frames reaches the host in this frame.
 * Main thread only, outside vlqt_app_process_events_for (a call from inside the pump does nothing). Returns the number
 * of queued events it delivered (0 = none), VLQT_ERR_NOT_INITIALIZED. */
VLQT_API int         vlqt_app_deliver_input(void);
/* Animation clock: the time QML animations run on, from the host. Animations follow it forward and hold with it; a jump
 * back does not rewind them (they go on from their value). 0, VLQT_ERR_NOT_INITIALIZED, VLQT_ERR_BAD_ARGUMENT (seconds
 * is NaN, infinite or out of range). */
VLQT_API int         vlqt_app_advance_animations(double seconds);
/* Text of the last failing call on the calling thread (top of this file). Any thread. */
VLQT_API const char* vlqt_last_error(void);

/* ===== scene: a window or an offscreen scene; main thread ===== */

/* Averages/maxima over the last ~1 s of pumps (the last 60), all times in ms. Frame statistics (fps, frame_ms,
 * frame_ms_max) come only from consecutive frames, like the percentiles of vlqt_perf_stats: pumps with a swap whose
 * previous pump also had one. After idle pumps the interval spans the idle time and is not a frame time. Without a
 * consecutive frame in the range (an idle or on-demand scene) all three are 0. The phases (sync_ms, render_ms,
 * present_ms, vsync_ms) are averages per pump that rendered (swaps > 0), summed over its swaps; pump_ms, work_ms and
 * swaps_per_pump are per pump of the range, gpu_ms per pump with a GPU timestamp. The F2 meter and the F5 Summary
 * page use the same computation. */
typedef struct vlqt_perf {
    double fps;            /* 1000 / frame_ms, 0 = no consecutive frame */
    double frame_ms, frame_ms_max; /* average/max frame interval (vlqt_perf_sample.frame_interval) */
    double pump_ms, pump_ms_max;   /* processEvents incl. rendering, vsync wait and present, on the vvvv thread */
    double sync_ms, render_ms, present_ms;
    double gpu_ms, gpu_ms_max;     /* 0 = GPU timestamps unavailable */
    double swaps_per_pump;
    int samples;
    double vsync_ms;               /* QRhi::beginFrame, see vlqt_perf_sample.vsync (appended) */
    double work_ms;                /* Qt work per pump, see vlqt_perf_sample.work (appended) */
} vlqt_perf;

/* One pump (vlqt_app_process_events_for) of a scene, times in ms. */
typedef struct vlqt_perf_sample {
    float frame_interval; /* interval between the last two frame swaps (0 = none yet) */
    float pump;     /* whole vlqt_app_process_events_for */
    float sync, render, present; /* frame phases, summed over the swaps of this pump */
    float gpu;      /* QRhi timestamps of the last completed frame, 0 = unavailable */
    float events;   /* pump - vsync - sync - render - present: posted events, input, polish, animations (>= 0) */
    int swaps;      /* frames swapped in this pump */
    /* Appended: beforeFrameBegin -> beforeSynchronizing, summed over the swaps, i.e. QRhi::beginFrame. With VSync
     * D3D11 waits there for the refresh (frame latency waitable object), not in Present; without VSync it waits there
     * when the GPU is behind. F5 segment "vsync wait". 0 on the software backend. */
    float vsync;
    /* Appended: Qt work = events + sync + render, i.e. pump - vsync - present (>= 0): the pump without the waits for the
     * display refresh or the GPU. The one definition of Qt work: over_budget, the F2 series "work" and the slow-frame
     * warning of VL.Qt judge it. Filled by QtNative; vlqt_test_perf_eval computes it from pump, vsync and present. */
    float work;
} vlqt_perf_sample;

/* Percentiles over the ring (last up to 240 pumps; frame only over pumps with a swap whose previous pump also had one,
 * i.e. intervals between consecutive frames), nearest rank, and counters.
 * Computed at query time only. Counters: since scene creation and over the last 10 s (1 s buckets).
 *   over_budget   Qt work > budget_ms (vlqt_perf_sample.work: the pump without the vsync wait and the present)
 *   long_frames   frame interval > 1.5 x budget_ms and the previous pump also had a frame (a refresh was skipped =
 *                 missed frame; the interval after idle pumps of on-demand rendering does not count)
 *   idle          swaps == 0 (no frame in the pump, mostly because nothing changed)
 *   multi_swap    swaps > 1 */
typedef struct vlqt_perf_stats {
    double budget_ms;   /* vvvv's frame interval (vlqt_app_set_frame_budget) at the window's last pump, 1000 / 60 until set */
    double frame_p50, frame_p95, frame_p99, frame_max;
    double pump_p50, pump_p95, pump_p99, pump_max;
    int samples;        /* pumps in the ring */
    int frames;         /* of those with a consecutive frame interval */
    long long pumps, over_budget, long_frames, idle, multi_swap;
    long long recent_pumps, recent_over_budget, recent_long_frames, recent_idle, recent_multi_swap;
} vlqt_perf_stats;

/* What the host (VL.Qt, .NET) measured around one pump of a scene, times in ms. Reported after the pump with
 * vlqt_scene_report_host_pump; "since the previous host pump" = one vvvv frame. */
typedef struct vlqt_host_pump {
    float apply;       /* vlqt_scene_apply of this scene in this pump (incl. resync) */
    float reconcile;   /* reconcile + command writing in the node updates since the previous host pump */
    float interval;    /* since the previous host pump of the same vvvv app (0 = first) */
    float gc_pause;    /* .NET GC pause since the previous host pump */
    int ops;           /* commands applied to this scene in this pump */
    int gc0, gc1, gc2; /* .NET collections per generation since the previous host pump */
    long long alloc_bytes; /* .NET bytes allocated (process) since the previous host pump */
    /* the vvvv side of the frame before this pump (0 = unknown). interval ~ previous host + wait + vvvv. */
    float vvvv;        /* vvvv frame start (IFrameClock ticks; fallback the clock's update time) -> start of this pump */
    float wait;        /* end of the previous host pump -> vvvv frame start (frame timer / vsync / other apps) */
} vlqt_host_pump;

/* Host pumps of the last ~1 s of pumps (the vlqt_perf range); only pumps with a report count. Rates per second over
 * the summed intervals. */
typedef struct vlqt_host_pump_stats {
    double apply_ms, apply_ms_max;
    double reconcile_ms, reconcile_ms_max;
    double ops, ops_max;               /* per pump */
    double gc0_per_s, gc1_per_s, gc2_per_s;
    double gc_pause_ms_per_s;
    double alloc_bytes;                /* per host pump */
    int samples;
    double vvvv_ms, vvvv_ms_max;       /* vlqt_host_pump.vvvv, average/max */
    double wait_ms, wait_ms_max;       /* vlqt_host_pump.wait, average/max */
} vlqt_host_pump_stats;

/* Details of the last vlqt_scene_apply of a scene, see vlqt_scene_get_apply_error. */
typedef struct vlqt_apply_error {
    int rc;              /* return code of the last apply, 0 = it succeeded (then all other fields are 0/empty) */
    int command;         /* index of the bad command in the buffer */
    unsigned int id;     /* object id the bad command addresses, 0 = none (SET_CLEAR_COLOR, RESET, truncated header, root) */
    unsigned short op;   /* VLQT_OP_* of the bad command, 0 if its header was truncated */
    unsigned short prop; /* VLQT_PROPERTY_* of a bad SET, else 0 */
    const char* reason;  /* UTF-8 reason without the "apply: command #n ..." prefix; valid until the next apply of the scene */
} vlqt_apply_error;

/* Size and device pixel ratio of the scene (Space): what C# needs for the scale of Normalized (height) and
 * PixelTopLeft (dpr). width/height = size of the scene root in DIP (window: client size; offscreen scene: its bound
 * size), dpr = QWindow::effectiveDevicePixelRatio of a window, 1 for an offscreen scene (1 DIP = 1 texture pixel). */
typedef struct vlqt_scene_metrics {
    float width, height, dpr;
} vlqt_scene_metrics;

/* Destroys a window or an offscreen scene (a scene unbinds first). A null handle does nothing; an exception sets
 * vlqt_last_error. */
VLQT_API void      vlqt_scene_destroy(vlqt_scene* scene);
/* Command buffer (protocol.h). Returns 0 = ok; on error <0 and vlqt_last_error() names command index +
 * reason; processing stops at the bad command, earlier commands stay applied. VLQT_ERR_NULL_HANDLE, and the apply
 * range of vlqt_result: VLQT_ERR_BAD_LENGTH ... VLQT_ERR_INVALID (VLQT_ERR_BAD_ARGUMENT = bad value). On an offscreen
 * scene the clear colour comes from VLQT_OP_SET_CLEAR_COLOR too (alpha 0 = transparent texture). */
VLQT_API int       vlqt_scene_apply(vlqt_scene* scene, const unsigned char* buf, int len);
/* Structured form of the last apply error of the scene (the object id lets the host show it at the element).
 * 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_BAD_ARGUMENT null out. */
VLQT_API int       vlqt_scene_get_apply_error(vlqt_scene* scene, vlqt_apply_error* out);
/* Copies the queued events into out (a buffer export, top of this file; 0 bytes = none) and clears the queue; on
 * VLQT_ERR_BUFFER_TOO_SMALL they stay queued and *required is exact: the call that follows only copies (it collects
 * nothing new, new changes come with the poll after it). VLQT_ERR_NULL_HANDLE. Call once per frame after
 * vlqt_app_process_events_for: the poll first queues EVENT_MEASURED / EVENT_SCENE_BOUNDS of bound items whose value
 * changed and EVENT_WINDOW_GEOMETRY of a window after a move/resize/state change (events.h). */
VLQT_API int       vlqt_scene_poll(vlqt_scene* scene, unsigned char* out, int cap, int* written, int* required);
/* Size and device pixel ratio (vlqt_scene_metrics). Live values, cheap enough for every frame. Main thread. 0,
 * VLQT_ERR_NULL_HANDLE, VLQT_ERR_BAD_ARGUMENT null out. */
VLQT_API int       vlqt_scene_get_metrics(vlqt_scene* scene, vlqt_scene_metrics* out);
/* Number of frames in which the scene changed since it was created (item/reconciler/video update(), polish, resize):
 * not every pump, and not the render a grab (vlqt_snapshot_start / _fit) triggers by itself, so a static scene keeps
 * the count while snapshots run. Compare two values for "changed since". >= 0, VLQT_ERR_NULL_HANDLE. Main thread. */
VLQT_API long long vlqt_scene_get_change_count(vlqt_scene* scene);
/* Debug overlay for all elements of the scene (Renderer [Qt] Debug); per element: VLQT_PROPERTY_DEBUG.
 * on != 0 = on. Frame cyan, origin magenta, anchor yellow, baseline green, ... (legend: debug.h). set: 0, get: 1 = on,
 * 0 = off; both VLQT_ERR_NULL_HANDLE. */
VLQT_API int       vlqt_scene_set_debug(vlqt_scene* scene, int on);
VLQT_API int       vlqt_scene_get_debug(vlqt_scene* scene);
/* The perf overlay flags: bit0 = perfmeter panel (F2), bit1 = stats (F5), bit3 = freeze (F6), bits 4..6 = stats page + 1;
 * the keys of a window change them (vlqt_window_set_keys). >= 0, VLQT_ERR_NULL_HANDLE. */
VLQT_API int       vlqt_scene_get_overlay(vlqt_scene* scene);
/* The perf getters: 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_BAD_ARGUMENT null out/in. */
VLQT_API int       vlqt_scene_get_perf(vlqt_scene* scene, vlqt_perf* out);
/* Last completed pump of the scene. VLQT_ERR_NO_DATA: no pump yet. */
VLQT_API int       vlqt_scene_get_perf_sample(vlqt_scene* scene, vlqt_perf_sample* out);
/* Percentiles, counters, budget (see vlqt_perf_stats). */
VLQT_API int       vlqt_scene_get_perf_stats(vlqt_scene* scene, vlqt_perf_stats* out);
/* host values of the last completed pump of the scene (call after vlqt_app_process_events_for).
 * VLQT_ERR_NO_DATA: no pump yet. */
VLQT_API int       vlqt_scene_report_host_pump(vlqt_scene* scene, const vlqt_host_pump* in);
/* Last reported host pump. VLQT_ERR_NO_DATA: none (the last pump has no report). */
VLQT_API int       vlqt_scene_get_host_pump(vlqt_scene* scene, vlqt_host_pump* out);
/* Averages/maxima/rates over the last ~1 s (see vlqt_host_pump_stats). */
VLQT_API int       vlqt_scene_get_host_pump_stats(vlqt_scene* scene, vlqt_host_pump_stats* out);

/* ===== window: only windows; on an offscreen scene a no-op or VLQT_ERR_UNSUPPORTED as each function says; main
 * thread ===== */

/* Creates the window (hidden; vlqt_window_show shows it). NULL = error (vlqt_last_error). Geometry in Qt logical pixels
 * (DIP), the convention of the Renderer's Bounds pin and of EVENT_WINDOW_GEOMETRY (round trip stable):
 *   x, y           outer top-left incl. frame (QWindow::setFramePosition, set before show). x or y NaN/inf =
 *                  centered on the primary screen's working area. A window rect on no screen is centered as well.
 *   width, height  client size (QWindow::resize). < 1 = 600x400.
 *   swapInterval   0 = Present never waits, 1 = VSync. Fixed at creation. Several VSync windows do not halve the frame rate
 *                  on Windows (D3D11): Qt runs one VBlank thread per monitor (qdxgivsyncservice) that posts the update
 *                  requests, and each window waits in its own QRhi::beginFrame. Condition: the host dispatches the Win32
 *                  messages of its thread between the pumps (vvvv does); otherwise a request renders one pump later.
 *                  Linux not measured yet.
 * Wayland: clients cannot place their windows, the position (and centering) is ignored there, only the size applies.
 * Free it with vlqt_scene_destroy. */
VLQT_API vlqt_scene* vlqt_window_create(float x, float y, int width, int height, const char* title, int swapInterval);
/* show, hide: a null handle or an offscreen scene does nothing; an exception sets vlqt_last_error. */
VLQT_API void  vlqt_window_show(vlqt_scene* window);
VLQT_API void  vlqt_window_hide(vlqt_scene* window);
/* 1 = X was clicked since last call (close is swallowed), 0 = not (always on a scene), VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_window_take_close_request(vlqt_scene* window);
/* 1 = visible, 0 = hidden (always on a scene), VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_window_is_visible(vlqt_scene* window);
/* Runtime changes of the create parameters (Renderer [Qt] Title/Size pins). 0, VLQT_ERR_NULL_HANDLE; set_title does
 * nothing on an offscreen scene, resize returns VLQT_ERR_UNSUPPORTED there. Size in Qt logical pixels (DIP). */
VLQT_API int   vlqt_window_set_title(vlqt_scene* window, const char* title);
VLQT_API int   vlqt_window_resize(vlqt_scene* window, int width, int height);
/* Window icon: QWindow::setIcon. utf8Path = .ico/.png/.svg (absolute path), NULL or empty = embedded default
 * icon (also applied by vlqt_window_create before show). Live, any time. 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_IO file
 * not loadable (the default icon is shown instead; vlqt_last_error has the reason). 0 on an offscreen scene. */
VLQT_API int   vlqt_window_set_icon(vlqt_scene* window, const char* utf8Path);
/* Window modes (vlqt_window_mode, protocol.h), live, without re-creating the window; before or after show.
 * Only the bits in mask change (to their value in value), unknown bits are ignored. 0, VLQT_ERR_NULL_HANDLE.
 *   BORDERLESS  Qt::FramelessWindowHint; the client area stays where it is.
 *   FULLSCREEN  fullscreen on the screen with the largest share of the window; off restores the geometry from
 *               before. BORDERLESS changes while fullscreen take effect on leaving fullscreen.
 *               F11 / Alt+Enter in the window toggle this bit (like VL.Skia). No effect on eglfs (always fullscreen).
 * EVENT_WINDOW_GEOMETRY reports the resulting state (VLQT_WINDOW_STATE_FULLSCREEN). vlqt_window_get_mode = requested
 * modes (>= 0), VLQT_ERR_NULL_HANDLE. Both do nothing on an offscreen scene (get: 0). */
VLQT_API int   vlqt_window_set_mode(vlqt_scene* window, unsigned int mask, unsigned int value);
VLQT_API int   vlqt_window_get_mode(vlqt_scene* window);
/* MSAA sample count of the window (docs/decisions/render-texture.md, "MSAA"). A window is created with 4 (the
 * look of every window before #118, clamped like here). requested < 1 counts as 1 (none); QtNative clamps it to the
 * highest count not above it that QRhi and the device support for the swap chain (D3D11: CheckMultisampleQualityLevels
 * for R8G8B8A8_UNORM and D24_UNORM_S8_UINT; OpenGL: QRhi::supportedSampleCounts) and returns the count used (>= 1), so
 * the caller can warn when it is lower. Equal to the current count: nothing happens. A window not shown yet only
 * changes its format. A shown window cannot change the count of its swap chain (Qt fixes it per QQuickWindow), so
 * QtNative replaces the QQuickWindow inside the same handle: a new window with the new count, same screen, geometry,
 * window states, mode, title, icon, colour and overlay; the scene (items, their state, the QML engine) moves to it,
 * then the old one is deleted. Costs one new QRhi (tens of ms) and a new native window (HWND), which becomes active.
 * Lost on a change: a snapshot (vlqt_snapshot_start) still in flight fails ("the window was destroyed during the
 * grab", the next request works), pointer grabs and hover of the old window end, the perf history (F5) restarts, and
 * with Clear off the kept frame: the previous-frame layer is rebuilt for the new window and starts from the clear
 * colour. Effect layers and snapshots in the window follow its count. VLQT_ERR_NULL_HANDLE, VLQT_ERR_UNSUPPORTED on an
 * offscreen scene (its count comes from vlqt_offscreen_bind). Main thread. */
VLQT_API int   vlqt_window_set_samples(vlqt_scene* window, int requested);
/* Sample count of the window: of its swap chain once it has one, else the count it will be created with. 0 on an
 * offscreen scene, VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_window_get_samples(vlqt_scene* window);
/* The diagnostic keys of the window (Renderer [Qt] Enable Keyboard Shortcuts): on != 0 (default) = F2 (perfmeter), F5 and
 * Shift+F5 (stats), F6 (freeze), F7 (host segments) are taken by QtNative and never reach the scene; 0 = none of them is
 * taken, the keys reach the scene, and an open overlay stays as it is. Kept when the window is replaced by
 * vlqt_window_set_samples. 0, VLQT_ERR_NULL_HANDLE; a no-op on an offscreen scene (it gets no keyboard focus). Main
 * thread. */
VLQT_API int   vlqt_window_set_keys(vlqt_scene* window, int on);

/* ===== offscreen: only offscreen scenes (VLQT_ERR_NULL_HANDLE for a window) =====
 * QtTexture; design: docs/decisions/render-texture.md. A Qt scene without a native window (QQuickRenderControl) on
 * QtNative's shared D3D11 device that renders into a texture of a foreign D3D11 device (Stride's), shared by NT handle.
 * Two shared ID3D11Fences per binding order Qt's write and the foreign device's read on the GPU; no CPU wait, the
 * foreign context gets only Signal/Wait calls. Main thread (the thread of vlqt_app_process_events_for) only. Input
 * goes through vlqt_offscreen_send_*. An offscreen scene never renders in vlqt_app_process_events_for: only
 * vlqt_offscreen_render_batch renders it. 1 DIP = 1 texture pixel. */

/* Creates an unbound offscreen scene of width x height DIP (< 1 = 1). NULL = error (vlqt_last_error). Free it with
 * vlqt_scene_destroy. */
VLQT_API vlqt_scene* vlqt_offscreen_create(int width, int height);
/* Binds scene to a texture of a foreign device, replacing an earlier binding (as vlqt_offscreen_unbind first).
 *   device     ID3D11Device* of the foreign device (Stride's). Must be on the adapter of QtNative's shared device
 *              (LUIDs compared). If it is not and nothing renders on QtNative's device yet (no on-screen window has
 *              rendered, no other scene is bound), QtNative rebuilds its device on device's adapter, as after a device
 *              loss (images upload again, every scene is unbound); otherwise ADAPTER_MISMATCH. QtNative keeps no
 *              reference to device beyond the fences it opens on it: before the owner releases device, the caller
 *              unbinds (or destroys) every scene bound to it and stops calling vlqt_offscreen_render_batch with its
 *              context. A scene still bound keeps device alive through the fences until unbind or destroy.
 *   ntHandle   NT handle of an ID3D11Texture2D of device created with SHARED | SHARED_NTHANDLE and RENDER_TARGET;
 *              opened with ID3D11Device1::OpenSharedResource1. QtNative does not close it: the caller may close it
 *              right after the call.
 *   width, height  texture size in pixels (= the scene size from now on); must match the texture.
 *   format     DXGI_FORMAT the caller samples the texture with: DXGI_FORMAT_R8G8B8A8_UNORM (28), _R8G8B8A8_UNORM_SRGB
 *              (29), _B8G8R8A8_UNORM (87), _B8G8R8A8_UNORM_SRGB (91) or _R16G16B16A16_FLOAT (10). An 8-bit texture may
 *              be typeless (R8G8B8A8_TYPELESS 27 / B8G8R8A8_TYPELESS 90) or of the same format. The texture always
 *              holds premultiplied alpha. UNORM: Qt renders into the texture, gamma-space values as in a Qt window.
 *              SRGB and float: the caller reads linear values; Qt renders into a float texture of QtNative and after
 *              each frame a pass writes a * linear(c) into every pixel (unpremultiply, sRGB decode, premultiply).
 *   samples    MSAA sample count, < 2 = none. Qt renders into a multisample colour and depth-stencil texture of
 *              its own and resolves into the texture (or into the float texture of the linear pass) at the end of each
 *              frame; the texture itself stays single-sample. Clamped to the highest count not above it that QRhi and
 *              QtNative's device support for the render target format (D3D11 CheckMultisampleQualityLevels). Layers in
 *              the scene (effects, snapshots) stay single-sample. A change of the count is a new bind.
 *   usedSamples  out, may be NULL: the count used after the clamp (>= 1 on success, 0 on an error).
 * Creates the write and read fences (ID3D11Device5::CreateFence, SHARED) on QtNative's device and opens them on
 * device. Errors (VLQT_ERR_*): NULL_HANDLE, BAD_ARGUMENT (null device/handle, size < 1 or above the device limit, size
 * not the texture's, device without a DXGI adapter), FORMAT_UNSUPPORTED, ADAPTER_MISMATCH (a warning is logged once),
 * OPEN_FAILED, FENCE_FAILED (no ID3D11Device5 / ID3D11DeviceContext4 on one side), DEVICE_LOST (QtNative's device is
 * lost or being rebuilt, or the move failed: try again next frame), UNSUPPORTED (not D3D11), INTERNAL. On an error the
 * scene is unbound. */
VLQT_API int   vlqt_offscreen_bind(vlqt_scene* scene, void* device, void* ntHandle, int width, int height, int format,
                                  int samples, int* usedSamples);
/* Frees the binding in the order render target -> opened texture -> fences; the scene and its elements stay. VLQT_OK
 * also when not bound; VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_offscreen_unbind(vlqt_scene* scene);
/* 1 = bound, 0 = unbound (never bound, unbound, failed bind, or unbound by QtNative after a device loss of its own
 * device: the caller binds again), VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_offscreen_is_bound(vlqt_scene* scene);
/* Renders the scheduled scenes of one host frame; call once per frame per foreign device (Stride: VLGame.DrawStarted,
 * after all updates, before the first draw). context: the immediate ID3D11DeviceContext* of the device the scenes
 * are bound to. Advances the animation clock to driverSeconds once (as vlqt_app_advance_animations, idempotent per
 * value), then per bound scene, in this order:
 *   Signal(readFence, n) on context (the commands of the previous frame that read the texture were submitted before);
 *   Wait(readFence, n) on QtNative's context;
 *   if the scene changed (dirty) or was just bound: polish, sync, render into the texture; Signal(writeFence, n) and
 *   Flush on QtNative's context (without the flush the foreign GPU waits forever); Wait(writeFence, n) on context.
 * An unchanged scene costs no render and no fence signal. NULL entries and unbound scenes are skipped. Returns the
 * number of scenes rendered (>= 0), or VLQT_ERR_BAD_ARGUMENT (null context, null scenes with count > 0, count < 0),
 * VLQT_ERR_DEVICE_LOST, VLQT_ERR_UNSUPPORTED.
 * On a render thread (vlqt_offscreen_set_render_threads) the fence order is the same, the work splits: the read
 * signals and polish run here, then one Flush of context; beginFrame and sync run on the render thread while this call
 * waits; the Wait(writeFence, n) on context is queued before the render thread signals n; render, endFrame, Signal and
 * Flush run on the render thread after this call returned. context's GPU waits for Qt's frame, its CPU never does; this
 * call blocks only while a render thread is still busy with the previous batch. */
VLQT_API int   vlqt_offscreen_render_batch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds);
/* Size of the render-thread pool (pin Render Threads; docs/decisions/render-texture.md, "Qt render thread"). count
 * 0 = offscreen scenes render on the main thread, inside vlqt_offscreen_render_batch; n = 1 ... 16 = on a pool of Qt
 * render threads shared by every scene of the process, a scene assigned round robin at vlqt_offscreen_create. May be
 * called at any time; it applies to scenes created afterwards, a scene keeps the thread (or the main thread) it got.
 * The pool only grows: a larger count adds threads when a scene needs them, a smaller one (or 0) leaves the existing
 * threads to the scenes that have them. Without a call the environment variable VLQT_OFFSCREEN_THREAD decides (0 =
 * main thread, n = n threads), unset = 1 render thread. VLQT_OK, VLQT_ERR_BAD_ARGUMENT (count < 0 or > 16). */
VLQT_API int   vlqt_offscreen_set_render_threads(int count);

/* Input into an offscreen scene (design: docs/decisions/render-texture.md, "Input"). The host maps its input to
 * texture pixels (top-left origin, 1 DIP = 1 pixel; positions outside 0..size are valid: hover ends, a drag goes on) and
 * calls these between two vlqt_offscreen_render_batch calls, before the batch that should show the result. QtNative
 * sends the events with QCoreApplication::sendEvent to the scene's QQuickWindow (synchronous, not marked spontaneous),
 * so the event pipeline of windows (handlers, vlqt_scene_poll, channels) is unchanged. Each scene has its own mouse,
 * touchscreen and pen QPointingDevice (point ids and grabs live in the device, so a press sent to two scenes does not
 * cancel the other's grab). QtNative synthesizes MouseButtonDblClick from two presses as QGuiApplication does; Qt
 * shortcuts (QShortcut) do not see the keys. A scene that changes renders in the next batch. Bound or not does not
 * matter. Main thread. Return codes: VLQT_OK, VLQT_ERR_NULL_HANDLE, VLQT_ERR_BAD_ARGUMENT (unknown type or device, a
 * touch id in the wrong state, null text with length > 0); a key or text event while the scene has no focus is dropped
 * and returns VLQT_OK.
 *
 * Focus: a scene window never becomes QGuiApplication's focus window. QtNative gives it focus with QFocusEvent
 * FocusIn when a PRESS lands inside 0..size and takes it with FocusOut when a PRESS lands outside or the host calls
 * vlqt_offscreen_set_focus(scene, 0) (e.g. its window lost the focus). The FocusRelease filter of windows (Esc,
 * press outside the focused element) runs on scenes too. */

/* Pointer event (vlqt_pointer) of device (vlqt_pointer_device) at x/y in texture pixels.
 *   mouse  id ignored; buttons = Qt::MouseButtons held after the event (PRESS/RELEASE: the changed buttons are the
 *          difference to the previous call, one event each; none changed = VLQT_ERR_BAD_ARGUMENT), modifiers =
 *          Qt::KeyboardModifiers. QMouseEvent; LEAVE sends QEvent::Leave.
 *   touch  id = the point's id (any int, unique while pressed); buttons ignored. QtNative keeps the active points per
 *          scene and sends one QTouchEvent per call with all of them (others stationary).
 *          Several ids = multitouch. CANCEL cancels every active point (TouchCancel), id ignored; LEAVE is
 *          VLQT_ERR_BAD_ARGUMENT.
 *   pen    as a window on Windows gets one (WM_POINTER): a QTabletEvent from a stylus (pointer type Pen), and if nobody
 *          accepts it the same as a mouse event synthesized from the stylus, as QGuiApplication does. id and buttons
 *          ignored (one point; MOVE without PRESS hovers); CANCEL drops the pen's grabs without a release; LEAVE as mouse. */
VLQT_API int   vlqt_offscreen_send_pointer(vlqt_scene* scene, int device, int type, int id, float x, float y, int buttons,
                                           int modifiers);
/* Wheel at x/y (texture pixels): angleDelta dx, dy in eighths of a degree (120 = one notch), modifiers =
 * Qt::KeyboardModifiers. QWheelEvent from the mouse. */
VLQT_API int   vlqt_offscreen_send_wheel(vlqt_scene* scene, float x, float y, int dx, int dy, int modifiers);
/* Key press or release (vlqt_key_event) of key (Qt::Key) with modifiers (Qt::KeyboardModifiers), without text, to the
 * focus item; autoRepeat 1 for repeated presses. Text comes separately (vlqt_offscreen_send_text). */
VLQT_API int   vlqt_offscreen_send_key(vlqt_scene* scene, int type, int key, int modifiers, int autoRepeat);
/* Typed text (UTF-8, length bytes, no NUL needed) to the focus item, e.g. one character of a KeyPress. */
VLQT_API int   vlqt_offscreen_send_text(vlqt_scene* scene, const char* utf8, int length);
/* focused 1: FocusIn, 0: FocusOut (a running edit commits as on any focus loss). No-op if unchanged. */
VLQT_API int   vlqt_offscreen_set_focus(vlqt_scene* scene, int focused);

/* ===== res: lifecycle of any resource =====
 * resources.h, protocol.h vlqt_resource_*: independent of scenes, ids u64 = index | generation << 32.
 * Main thread (the thread of vlqt_app_process_events_for) only. Return codes: 0 = ok, VLQT_ERR_INTERNAL (log),
 * VLQT_ERR_BAD_ARGUMENT bad argument / not allowed in this state, VLQT_ERR_UNKNOWN_ID unknown, stale or already
 * released id (warning in the log), VLQT_ERR_WRONG_THREAD called on another thread (logged once; vlqt_res_shutdown
 * then does nothing). */

/* One resource of vlqt_res_list: introspection for the F5 overlay (images package 07). */
typedef struct vlqt_res_info {
    unsigned long long id;  /* index | generation << 32 */
    int kind;               /* VLQT_RESOURCE_KIND_* */
    int state;              /* VLQT_RESOURCE_STATE_* (Released ones are gone from the list) */
    long long bytes;        /* uploaded bytes */
    int host_refs;          /* 1 until vlqt_res_release, then 0 */
    int element_refs;       /* references of elements (images package 04) */
} vlqt_res_info;

/* New resource of kind (VLQT_RESOURCE_KIND_*) in state Pending, host reference 1. */
VLQT_API int vlqt_res_create(int kind, unsigned long long* outId);
/* Drops the host reference; freed at the end of a pump once no element holds it either (a running decode is
 * cancelled or finishes first). No status record for the id after this call. */
VLQT_API int vlqt_res_release(unsigned long long id);
/* Upload bytes per pump (default 8 MB), > 0. Uploads run in vlqt_app_process_events_for before the windows render. */
VLQT_API int vlqt_res_set_budget(int bytes);
/* Status queue (protocol.h, resources): copies all queued records into out (a buffer export, top of this file; 0 bytes
 * = none) and clears the queue; on VLQT_ERR_BUFFER_TOO_SMALL they stay queued. Call once per frame after
 * vlqt_app_process_events_for. */
VLQT_API int vlqt_res_poll(unsigned char* out, int cap, int* written, int* required);
/* Blocks until the resource leaves Decoding/Uploading or timeoutMs passed: a queued decode runs on the calling thread,
 * completely (the timeout applies to waiting for a worker), one in a worker is awaited, then the upload runs at once
 * (outside the budget). Returns the state then (VLQT_RESOURCE_STATE_*; Ready/Failed, or the current state on timeout
 * or when it was never started) or VLQT_ERR_UNKNOWN_ID. Does not block on a snapshot (returns the current state). */
VLQT_API int vlqt_res_wait(unsigned long long id, int timeoutMs);
/* Live resources (also released ones until the pump frees them): writes min(count, cap) into out, returns count. */
VLQT_API int vlqt_res_list(vlqt_res_info* out, int cap);
/* Cancels and joins the workers and frees every resource (leak warning with the count of still referenced ones).
 * Call before Qt is torn down / at process end; the next start creates the workers again. An exception sets
 * vlqt_last_error. */
VLQT_API void vlqt_res_shutdown(void);

/* ===== image: main thread, except vlqt_image_write_file ===== */

/* Pixels of vlqt_image_set_pixels and vlqt_image_write_file. */
typedef struct vlqt_pixel_desc {
    int width, height;      /* > 0 */
    int row_bytes;          /* bytes per row incl. padding, <= 0 = width * bytes per pixel */
    int format;             /* VL PixelFormat number, table in protocol.h */
    int premultiplied;      /* != 0: color already multiplied with alpha */
    int flags;              /* vlqt_image_set_pixels: VLQT_IMAGE_LOAD_*; 0 when QtNative fills the desc */
} vlqt_pixel_desc;

/* Image resources (image_resource.h): start or replace the content of a VLQT_RESOURCE_KIND_IMAGE resource. Decode
 * and conversion run in a worker, the upload in the pump within the budget (vlqt_res_wait: at once). New content on
 * a Ready id keeps the old texture visible until the new one is complete; while a decode/upload runs only the newest
 * content is kept. upload: vlqt_image_upload (protocol.h), the path the texture takes; another value is
 * VLQT_ERR_BAD_ARGUMENT. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, bad argument), VLQT_ERR_UNKNOWN_ID. A missing or broken
 * file / an unknown format ends in Failed with RESOURCE_EVENT_ERROR. Main thread, else VLQT_ERR_WRONG_THREAD. */
/* File (UTF-8 path; PNG, JPG, ... via QImageReader, EXIF rotation applied). flags: VLQT_IMAGE_LOAD_*. */
VLQT_API int vlqt_image_load(unsigned long long id, const char* utf8Path, unsigned flags, int upload);
/* Pixels of C#: copied synchronously (data may be reused after the call), len = bytes readable at data; flags in
 * desc->flags. */
VLQT_API int vlqt_image_set_pixels(unsigned long long id, const vlqt_pixel_desc* desc, const void* data, int len,
                                   int upload);
/* Tooltip preview of an image resource, only on a Ready resource: the worker made it when the content was
 * loaded with VLQT_IMAGE_LOAD_PREVIEW (load flags / set_pixels desc.flags), longest edge <= 256 px, never upscaled.
 * Fills desc (width, height, row_bytes = width * 4, format = 9 B8G8R8A8, premultiplied = 1; also on
 * VLQT_ERR_BUFFER_TOO_SMALL) and copies the rows top-down into dst (a buffer export, top of this file).
 * VLQT_ERR_BAD_ARGUMENT wrong kind, VLQT_ERR_NO_DATA no preview (no flag, not Ready yet), VLQT_ERR_UNKNOWN_ID. Main
 * thread, else VLQT_ERR_WRONG_THREAD. */
VLQT_API int vlqt_image_read_preview(unsigned long long id, unsigned char* dst, int cap, vlqt_pixel_desc* desc,
                                     int* written, int* required);
/* Image file writer (FileWriter nodes): any thread, synchronous (encodes and writes before it returns), no
 * scene needed. Writes the pixels of desc/data (rows top-down, desc.row_bytes per row, len >= height * row_bytes;
 * desc.format a VL PixelFormat number as in vlqt_image_set_pixels: 1, 2, 4-10 and 12, e.g. 9 B8G8R8A8, 7 R8G8B8A8; not
 * 3 R32F and 11 R32G32F; premultiplied 0/1, flags ignored) to utf8Path. The file format comes from the extension (png,
 * jpg/jpeg, bmp, ... what QImageWriter can write); premultiplied pixels are un-premultiplied for formats with alpha,
 * formats without alpha get the pixels over black. More than 8 bits per channel (2 R16, 10 and 12 float RGBA) are
 * written as a 16-bit PNG (float values clamped to 0..1); every other file format gets 8 bit and a warning in the log.
 * quality 0..100 applies to lossy formats (jpg, webp) only, -1 = Qt's default. Missing folders are created, an
 * existing file is overwritten. len needs only the pixels of the last row, as in vlqt_image_set_pixels. Returns 0;
 * VLQT_ERR_BAD_ARGUMENT (null, size <= 0, row_bytes or len too small, unsupported pixel format), VLQT_ERR_UNSUPPORTED
 * unknown or unsupported file format (extension), VLQT_ERR_IO folder could not be created or the write failed;
 * vlqt_last_error says why. */
VLQT_API int vlqt_image_write_file(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path,
                                   int quality);

/* ===== shader: main thread, except vlqt_shader_reflect ===== */

/* Shader resources (shader_resource.h, protocol.h "ShaderEffect"): start or replace the content of a
 * VLQT_RESOURCE_KIND_SHADER resource. utf8Path: a .qsb file or "builtin:<name>" (shaders/<name>.frag|.vert.qsb next to
 * QtNative); stage: VLQT_SHADER_STAGE_*. The file is read and checked in a worker. New content on a Ready id keeps
 * the old shader until the new one is Ready. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, empty path, bad stage),
 * VLQT_ERR_UNKNOWN_ID. Missing file, not a .qsb, wrong stage, no variant for the backend: Failed with
 * RESOURCE_EVENT_ERROR. Main thread, else VLQT_ERR_WRONG_THREAD. */
VLQT_API int vlqt_shader_load(unsigned long long id, const char* utf8Path, int stage);
/* Shader from source (protocol.h "ShaderEffect"): utf8Source = full Qt GLSL (#version 440, NUL-terminated,
 * copied at the call), baked by QShaderBaker in a worker for the backend of the process, cached per hash of the source.
 * Same resource rules as vlqt_shader_load. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, empty source, bad stage),
 * VLQT_ERR_UNKNOWN_ID. Bake errors (compiler message with line numbers) and failed checks: Failed with
 * RESOURCE_EVENT_ERROR. Main thread, else VLQT_ERR_WRONG_THREAD. */
VLQT_API int vlqt_shader_load_source(unsigned long long id, const char* utf8Source, int stage);
/* Shader reflection (shader node factory): bakes utf8Source (full Qt GLSL, NUL-terminated) to SPIR-V 100 only (under
 * the same mutex as the runtime bakes, never in parallel) and writes the result as UTF-8 text to buf (a buffer export,
 * top of this file; a bake error is VLQT_OK with the text "error ..."). VLQT_ERR_BAD_ARGUMENT: empty source, stage not
 * VLQT_SHADER_STAGE_*. No QGuiApplication needed.
 * Thread: any, and it is synchronous on the calling thread (there is no async variant). The node factory calls it
 * from the description of a shader node (ShaderNodeLayout.Load, a thread of vvvv's choosing): a source not seen
 * before blocks the caller for one
 * bake (and for a runtime bake that holds the mutex), the same source again is answered from the bake cache of
 * QtNative (64 entries, a bake error too) without a bake.
 * Text: one entry per line, '\n'-separated, fields separated by one space:
 *   line 1: "ok" or "error"
 *   error:  the error lines as in a RESOURCE_EVENT_ERROR of a bake ("<stage> code, line <n>: <text>")
 *   ok:     "uniform <name> <type> <arrayLen>"  in block order (offset), including qt_Matrix / qt_Opacity;
 *                                              arrayLen 0 = no array
 *           "sampler <name> <binding>"         in binding order
 *   <type>: GLSL name: float vec2 vec3 vec4 int ivec2 ivec3 ivec4 uint uvec2 uvec3 uvec4 bool mat2 mat3 mat4
 *           sampler2D, anything else "other". */
VLQT_API int vlqt_shader_reflect(const char* utf8Source, int stage, char* buf, int bufLen, int* written, int* required);

/* ===== snapshot: main thread ===== */

/* Snapshot resources (protocol.h): main thread, else VLQT_ERR_WRONG_THREAD. Starts grabbing element elementId of scene (scale > 0:
 * pixel size = item size in DIP * scale, ignoring the screen's device pixel ratio) into a VLQT_RESOURCE_KIND_SNAPSHOT
 * resource. Asynchronous: the result arrives with RESOURCE_EVENT_INFO / STATE Ready in a later pump (at least one
 * render of the scene), errors as Failed with RESOURCE_EVENT_ERROR. A snapshot while one is in flight replaces it (only
 * the newest result is kept); vlqt_res_release during a grab drops the result. A snapshot on a Failed id starts over
 * (Failed -> Decoding -> INFO + Ready | Failed), on a Ready id it stays Ready until the new pixels replace the old ones
 * (INFO). A grab never stays in Decoding: element/scene destroyed, window no longer exposed (offscreen scene, #128:
 * unbound) or no result within 10 s -> Failed. An offscreen scene must be bound: the grab marks it dirty, so the next
 * vlqt_offscreen_render_batch that contains it renders it also when nothing changed. 0, VLQT_ERR_NULL_HANDLE (scene
 * NULL), VLQT_ERR_BAD_ARGUMENT (wrong kind, scale <= 0), VLQT_ERR_UNKNOWN_ID; element errors (unknown, not in the
 * scene, no size) are Failed + RESOURCE_EVENT_ERROR, rc 0.
 * Qt Quick software backend (QT_QUICK_BACKEND=software, e.g. Linux offscreen): Qt renders the grab into an opaque
 * pixmap, so the pixels have alpha 255 and transparent areas are black (premultiplied colors stay right). */
VLQT_API int vlqt_snapshot_start(unsigned long long id, vlqt_scene* scene, unsigned elementId, float scale);
/* Like vlqt_snapshot_start, but the scale is chosen so that the longest edge of the image is <= maxEdgePx and the
 * scale is <= 1 (pixels per DIP, never above the DIP size; the screen's device pixel ratio is ignored): a 1000 x 500
 * DIP element at 256 gives 256 x 128, a 100 x 50 one gives 100 x 50 (tooltip previews). Same resource rules and errors
 * as vlqt_snapshot_start. 0, VLQT_ERR_NULL_HANDLE (scene NULL), VLQT_ERR_BAD_ARGUMENT (wrong kind, maxEdgePx <= 0),
 * VLQT_ERR_UNKNOWN_ID. */
VLQT_API int vlqt_snapshot_start_fit(unsigned long long id, vlqt_scene* scene, unsigned elementId, int maxEdgePx);
/* Pixels of the last finished snapshot (Ready, also while a replacing one is in flight): fills out (width, height,
 * row_bytes = width * 4, format = 9 B8G8R8A8, premultiplied = 1; also on VLQT_ERR_BUFFER_TOO_SMALL) and copies the
 * rows top-down into dst (a buffer export, top of this file). VLQT_ERR_BAD_ARGUMENT wrong kind, VLQT_ERR_NO_DATA no
 * snapshot yet, VLQT_ERR_UNKNOWN_ID. Main thread, else VLQT_ERR_WRONG_THREAD. */
VLQT_API int vlqt_snapshot_read(unsigned long long id, void* dst, int cap, vlqt_pixel_desc* out, int* written,
                                int* required);

/* ===== audio tap: main thread, except vlqt_audio_tap_read and vlqt_audio_tap_now_ns ===== */

/* State of an audio tap, filled by vlqt_audio_tap_read (protocol.h "Audio tap"). 96 bytes, mirrored in Native.cs.
 * Times in ns of vlqt_audio_tap_now_ns, media times in us on Qt's audio startTime axis (keeps counting across loop
 * wraps). "First frame" = the first frame this call delivered or dropped, or the next one in the ring when it moved
 * none. */
typedef struct vlqt_audio_tap_state {
    long long now_ns;                    /* vlqt_audio_tap_now_ns at the call */
    long long due_ns;                    /* when the first frame is due: emitTime + k * 1e9 / (sample_rate * rate); 0 = no frame */
    long long media_us;                  /* media time of the first frame: startTime + k * 1e6 / sample_rate; 0 = no frame */
    long long video_media_us;            /* diagnostics: startTime of the last video frame at the sink (Qt's video axis, restarts at a loop wrap) */
    long long video_ns;                  /* diagnostics: vlqt_audio_tap_now_ns when that frame reached the sink; 0 = none yet */
    unsigned long long overrun_frames;   /* frames the producer dropped because the ring was full (since init) */
    unsigned long long underrun_frames;  /* frames a copying read (dst != NULL) asked for but did not get (since init) */
    double rate;                         /* playback rate of the first frame's segment (1 = normal); 0 = no frame */
    unsigned discontinuity;              /* segment counter of the first frame (see protocol.h); a change = flush and re-lock */
    int available;                       /* frames of the current segment left in the ring after the call */
    int capacity;                        /* ring size in frames */
    int channels;                        /* format of the tap (vlqt_audio_tap_init) */
    int sample_rate;
    int flags;                           /* VLQT_AUDIO_TAP_* (protocol.h) */
    int reserved[2];                     /* 0 */
} vlqt_audio_tap_state;

/* Audio taps (resources/audio_tap.h, protocol.h "Audio tap"). Main thread (else VLQT_ERR_WRONG_THREAD): fixes the format of a Pending
 * VLQT_RESOURCE_KIND_AUDIO_TAP resource (interleaved float, channels 1..VLQT_AUDIO_TAP_MAX_CHANNELS, sampleRate
 * VLQT_AUDIO_TAP_MIN_SAMPLE_RATE..MAX) and makes it Ready at once (RESOURCE_EVENT_INFO w = channels, h = sampleRate,
 * bytes = ring). Once per tap: a new sample rate needs a new tap. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, bad format,
 * already initialised, 64 taps already initialised and not freed), VLQT_ERR_UNKNOWN_ID. */
VLQT_API int vlqt_audio_tap_init(unsigned long long id, int channels, int sampleRate);
/* Reads the tap: any thread (VL.Audio's audio thread), one reader per tap at a time; no lock, no allocation, never
 * waits for the main thread. dst != NULL: copies up to maxFrames frames (maxFrames * channels floats, interleaved) of one segment into dst
 * and consumes them; dst == NULL: drops up to maxFrames frames of one segment (catching up); maxFrames == 0: state
 * only. channels must equal the tap's (guards dst). state (may be NULL) is filled on every return >= 0 and on
 * VLQT_ERR_BAD_ARGUMENT for a tap not yet initialised. Returns the frames copied/dropped (0 = none available),
 * VLQT_ERR_BAD_ARGUMENT (maxFrames < 0, channels mismatch, not initialised) or VLQT_ERR_UNKNOWN_ID (unknown, released
 * or freed id; silent: the reader polls after a release). Allocates nothing, also not for vlqt_last_error. */
VLQT_API int vlqt_audio_tap_read(unsigned long long id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state);
/* The clock of the audio tap times: std::chrono::steady_clock in ns (Windows: QPC = Stopwatch.GetTimestamp,
 * Linux: CLOCK_MONOTONIC). Any thread, no Qt needed. */
VLQT_API long long vlqt_audio_tap_now_ns(void);

/* ===== font: main thread ===== */

/* App fonts (LoadFont node): font files for Text/Style FontFamily without installing them. Qt GUI thread only (the
 * vvvv main thread, after vlqt_app_init); other threads get VLQT_ERR_WRONG_THREAD. One QFontDatabase registration per
 * file (canonical path + size + last-modified), shared and reference counted: every successful add takes one
 * reference, release drops it. An entry without references is removed at the end of the pump
 * (vlqt_app_process_events_for) unless it was added again in between; texts naming a removed family fall back to a
 * substitute at their next layout.
 * add: 0 = registered or shared, *outHandle != 0, families = '\n'-separated family names of the file (a .ttc can hold
 * several) as text of a buffer export (top of this file). Every failure leaves *outHandle 0 and takes no reference,
 * also VLQT_ERR_BUFFER_TOO_SMALL (call again with a larger buffer); VLQT_ERR_NOT_INITIALIZED, VLQT_ERR_WRONG_THREAD,
 * VLQT_ERR_BAD_ARGUMENT (null path or outHandle), VLQT_ERR_IO file not found, VLQT_ERR_UNSUPPORTED not a font Qt can
 * read. release: 0, VLQT_ERR_WRONG_THREAD, VLQT_ERR_UNKNOWN_ID unknown handle or no reference left. */
VLQT_API int   vlqt_font_add(const char* utf8Path, unsigned long long* outHandle, char* families, int familiesCap,
                             int* written, int* required);
VLQT_API int   vlqt_font_release(unsigned long long handle);

#ifdef __cplusplus
}
#endif
