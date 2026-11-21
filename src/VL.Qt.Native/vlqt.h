/* QtNative (VL.Qt native layer) – pure C ABI, cdecl, UTF-8 strings.
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
 * only from vlqt_window_apply (and vlqt_apply_error.rc); -20 ... -25 only from the offscreen exports. */
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
    VLQT_ERR_NO_DATA = -10,           /* nothing to read yet: no pump, no host frame, no snapshot, no preview, no QRhi */
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
    double vsync_ms;               /* QRhi::beginFrame, see vlqt_perf_sample.vsync (appended) */
} vlqt_perf;

/* One pump (vlqt_app_process_events) of a window, times in ms. */
typedef struct vlqt_perf_sample {
    float frame;    /* interval between the last two frame swaps (0 = none yet) */
    float pump;     /* whole vlqt_app_process_events */
    float sync, render, present; /* frame phases, summed over the swaps of this pump */
    float gpu;      /* QRhi timestamps of the last completed frame, 0 = unavailable */
    float events;   /* pump - vsync - sync - render - present: posted events, input, polish, animations (>= 0) */
    int swaps;      /* frames swapped in this pump */
    /* Appended: beforeFrameBegin -> beforeSynchronizing, summed over the swaps, i.e. QRhi::beginFrame. With VSync
     * D3D11 waits there for the refresh (frame latency waitable object), not in Present; without VSync it waits there
     * when the GPU is behind. F5 segment "vsync wait". 0 on the software backend. */
    float vsync;
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

/* Details of the last vlqt_window_apply of a window, see vlqt_window_get_apply_error. */
typedef struct vlqt_apply_error {
    int rc;              /* return code of the last apply, 0 = it succeeded (then all other fields are 0/empty) */
    int command;         /* index of the bad command in the buffer */
    unsigned int id;     /* object id the bad command addresses, 0 = none (CLEAR_COLOR, RESET, truncated header, root) */
    unsigned short op;   /* VLQT_OP_* of the bad command, 0 if its header was truncated */
    unsigned short prop; /* VLQT_PROP_* of a bad SET, else 0 */
    const char* reason;  /* UTF-8 reason without the "apply: command #n ..." prefix; valid until the next apply of the window */
} vlqt_apply_error;

/* What the host (VL.Qt, .NET) measured around one pump of a window, times in ms. Reported after the pump with
 * vlqt_window_report_host_frame; "since the previous host pump" = one vvvv frame. */
typedef struct vlqt_host_frame {
    float apply;       /* vlqt_window_apply of this window in this pump (incl. resync) */
    float reconcile;   /* reconcile + command writing in the node updates since the previous host pump */
    float host;        /* the whole host pump (all windows: apply, advance, processEvents, poll) */
    float interval;    /* since the previous host pump of the same vvvv app (0 = first) */
    float gc_pause;    /* .NET GC pause since the previous host pump */
    int ops;           /* commands applied to this window in this pump */
    int gc0, gc1, gc2; /* .NET collections per generation since the previous host pump */
    long long alloc_bytes; /* .NET bytes allocated (process) since the previous host pump */
    /* the vvvv side of the frame before this pump (0 = unknown). interval ~ previous host + wait + vvvv. */
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
    double vvvv_ms, vvvv_ms_max;       /* vlqt_host_frame.vvvv, average/max */
    double wait_ms, wait_ms_max;       /* vlqt_host_frame.wait, average/max */
} vlqt_host_stats;

/* One resource of vlqt_res_list: introspection for the F5 overlay (images package 07). */
typedef struct vlqt_res_info {
    unsigned long long id;  /* index | generation << 32 */
    int kind;               /* VLQT_RES_KIND_* */
    int state;              /* VLQT_RES_STATE_* (Released ones are gone from the list) */
    long long bytes;        /* uploaded bytes */
    int host_refs;          /* 1 until vlqt_res_release, then 0 */
    int element_refs;       /* references of elements (images package 04) */
} vlqt_res_info;

/* Pixels of vlqt_res_set_pixels. */
typedef struct vlqt_pixel_desc {
    int width, height;      /* > 0 */
    int row_bytes;          /* bytes per row incl. padding, <= 0 = width * bytes per pixel */
    int format;             /* VL PixelFormat number, table in protocol.h */
    int premultiplied;      /* != 0: color already multiplied with alpha */
    int flags;              /* vlqt_res_set_pixels: VLQT_IMG_*; 0 when QtNative fills the desc */
} vlqt_pixel_desc;

/* State of an audio tap, filled by vlqt_res_read_audio (protocol.h "Audio tap"). 96 bytes, mirrored in Native.cs.
 * Times in ns of vlqt_now_ns, media times in us on Qt's audio startTime axis (keeps counting across loop wraps).
 * "First frame" = the first frame this call delivered or dropped, or the next one in the ring when it moved none. */
typedef struct vlqt_audio_tap_state {
    long long now_ns;                    /* vlqt_now_ns at the call */
    long long due_ns;                    /* when the first frame is due: emitTime + k * 1e9 / (sample_rate * rate); 0 = no frame */
    long long media_us;                  /* media time of the first frame: startTime + k * 1e6 / sample_rate; 0 = no frame */
    long long video_media_us;            /* diagnostics: startTime of the last video frame at the sink (Qt's video axis, restarts at a loop wrap) */
    long long video_ns;                  /* diagnostics: vlqt_now_ns when that frame reached the sink; 0 = none yet */
    unsigned long long overrun_frames;   /* frames the producer dropped because the ring was full (since init) */
    unsigned long long underrun_frames;  /* frames a copying read (dst != NULL) asked for but did not get (since init) */
    double rate;                         /* playback rate of the first frame's segment (1 = normal); 0 = no frame */
    unsigned discontinuity;              /* segment counter of the first frame (see protocol.h); a change = flush and re-lock */
    int available;                       /* frames of the current segment left in the ring after the call */
    int capacity;                        /* ring size in frames */
    int channels;                        /* format of the tap (vlqt_res_audio_tap_init) */
    int sample_rate;
    int flags;                           /* VLQT_AUDIO_TAP_* (protocol.h) */
    int reserved[2];                     /* 0 */
} vlqt_audio_tap_state;

/* ABI version of this library (VLQT_ABI_VERSION, protocol.h): protocol constants, struct layouts and signatures of this
 * header. Needs no vlqt_app_init; the host reads it first and refuses a library with another number. */
VLQT_API int         vlqt_app_get_abi_version(void);
VLQT_API int         vlqt_app_init(const char* nativeDir, vlqt_log_fn log); /* idempotent */
/* One pump: resources, posted and window events, rendering. 0, VLQT_ERR_NOT_INITIALIZED. */
VLQT_API int         vlqt_app_process_events(void);
/* like vlqt_app_process_events, but only the given windows (those of one vvvv app) may render in this pump.
 * Update requests of other windows stay pending until their own app's pump. windows NULL = all windows. */
VLQT_API int         vlqt_app_process_events_for(void* const* windows, int count);
/* delivers the window system events Qt has queued (input of all windows, also geometry and close) without
 * rendering: update requests stay pending and an Expose is deferred, both render in the next pump. Call at the start
 * of the host frame, then vlqt_window_poll, so input that arrived between two frames reaches the host in this frame.
 * Main thread only, outside vlqt_app_process_events (a call from inside the pump does nothing). Returns the number of
 * queued events it delivered (0 = none), VLQT_ERR_NOT_INITIALIZED. */
VLQT_API int         vlqt_app_deliver_input(void);
/* Animation time from the host: 0, VLQT_ERR_NOT_INITIALIZED. */
VLQT_API int         vlqt_driver_advance(double seconds);
/* Text of the last failing call on the calling thread (top of this file). */
VLQT_API const char* vlqt_last_error(void);

/* Creates the window (hidden; vlqt_window_show shows it). NULL = error (vlqt_last_error). Geometry in Qt logical pixels (DIP), the
 * convention of the Renderer's Bounds pin and of EV_WINDOW_GEOMETRY (round trip stable):
 *   x, y           outer top-left incl. frame (QWindow::setFramePosition, set before show). x or y NaN/inf =
 *                  centered on the primary screen's working area. A window rect on no screen is centered as well.
 *   width, height  client size (QWindow::resize). < 1 = 600x400.
 *   swapInterval   0 = Present never waits, 1 = VSync. Fixed at creation. Several VSync windows do not halve the frame rate
 *                  on Windows (D3D11): Qt runs one VBlank thread per monitor (qdxgivsyncservice) that posts the update
 *                  requests, and each window waits in its own QRhi::beginFrame. Condition: the host dispatches the Win32
 *                  messages of its thread between the pumps (vvvv does); otherwise a request renders one pump later.
 *                  Linux not measured yet.
 * Wayland: clients cannot place their windows, the position (and centering) is ignored there, only the size applies. */
VLQT_API void* vlqt_window_create_at(float x, float y, int width, int height, const char* title, int swapInterval);
/* Short form: vlqt_window_create_at(NaN, NaN, width, height, title, 0) = centered, no VSync. */
VLQT_API void* vlqt_window_create(int width, int height, const char* title);
/* Pipeline cache: call before the first window. Sets load + save file of the QQuickGraphicsConfiguration of
 * every window created afterwards (driver compiled pipelines survive restarts). utf8Path NULL or empty = off (the
 * default). A cache of another GPU/driver is dropped by Qt; a file that cannot be written only logs a warning. 0. */
VLQT_API int   vlqt_set_pipeline_cache(const char* utf8Path);
/* destroy, show, hide: a null handle does nothing; an exception sets vlqt_last_error. */
VLQT_API void  vlqt_window_destroy(void* w);
VLQT_API void  vlqt_window_show(void* w);
VLQT_API void  vlqt_window_hide(void* w);
/* 1 = X was clicked since last call (close is swallowed), 0 = not (always on a scene), VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_window_take_close_request(void* w);
/* 1 = visible, 0 = hidden (always on a scene), VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_window_is_visible(void* w);
/* Runtime changes of the create parameters (Renderer [Qt] Title/Size pins). 0, VLQT_ERR_NULL_HANDLE; resize
 * VLQT_ERR_UNSUPPORTED on an offscreen scene. Size in Qt logical pixels (DIP). */
VLQT_API int   vlqt_window_set_title(void* w, const char* title);
VLQT_API int   vlqt_window_resize(void* w, int width, int height);
/* Window icon: QWindow::setIcon. utf8Path = .ico/.png/.svg (absolute path), NULL or empty = embedded default
 * icon (also applied by vlqt_window_create_at before show). Live, any time. 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_IO file
 * not loadable (the default icon is shown instead; vlqt_last_error has the reason). */
VLQT_API int   vlqt_window_set_icon(void* w, const char* utf8Path);
/* Window modes (vlqt_window_mode, protocol.h), live, without re-creating the window; before or after show.
 * Only the bits in mask change (to their value in value), unknown bits are ignored. 0, VLQT_ERR_NULL_HANDLE.
 *   BORDERLESS  Qt::FramelessWindowHint; the client area stays where it is.
 *   FULLSCREEN  fullscreen on the screen with the largest share of the window; off restores the geometry from
 *               before. BORDERLESS changes while fullscreen take effect on leaving fullscreen.
 *               F11 / Alt+Enter in the window toggle this bit (like VL.Skia). No effect on eglfs (always fullscreen).
 * EV_WINDOW_GEOMETRY reports the resulting state (VLQT_WINDOW_FULLSCREEN). vlqt_window_get_mode = requested modes
 * (>= 0), VLQT_ERR_NULL_HANDLE. */
VLQT_API int          vlqt_window_set_mode(void* w, unsigned int mask, unsigned int value);
VLQT_API int          vlqt_window_get_mode(void* w);
/* Size and device pixel ratio of the scene (Space): what C# needs for the scale of Normalized (height) and
 * PixelTopLeft (dpr). width/height = size of the scene root in DIP (window: client size; offscreen scene: its bound
 * size), dpr = QWindow::effectiveDevicePixelRatio of a window, 1 for an offscreen scene (1 DIP = 1 texture pixel).
 * Live values, cheap enough for every frame. Main thread. 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_BAD_ARGUMENT null out. */
typedef struct vlqt_scene_metrics {
    float width, height, dpr;
} vlqt_scene_metrics;
VLQT_API int          vlqt_window_get_scene_metrics(void* w, vlqt_scene_metrics* out);
/* MSAA sample count of the window (docs/decisions/render-texture.md, "MSAA"). A window is created with 4 (the
 * look of every window before #118, clamped like here). requested < 1 counts as 1 (none); QtNative clamps it to the
 * highest count not above it that QRhi and the device support for the swap chain (D3D11: CheckMultisampleQualityLevels
 * for R8G8B8A8_UNORM and D24_UNORM_S8_UINT; OpenGL: QRhi::supportedSampleCounts) and returns the count used (>= 1), so
 * the caller can warn when it is lower. Equal to the current count: nothing happens. A window not shown yet only
 * changes its format. A shown window cannot change the count of its swap chain (Qt fixes it per QQuickWindow), so
 * QtNative replaces the QQuickWindow inside the same handle: a new window with the new count, same screen, geometry,
 * window states, mode, title, icon, colour and overlay; the scene (items, their state, the QML engine) moves to it,
 * then the old one is deleted. Costs one new QRhi (tens of ms) and a new native window (HWND), which becomes active.
 * Lost on a change: a snapshot (vlqt_res_snapshot) still in flight fails ("the window was destroyed during the grab",
 * the next request works), pointer grabs and hover of the old window end, the perf history (F5) restarts, and with
 * Clear off the kept frame: the feedback layer is rebuilt for the new window and starts from the clear colour.
 * Effect layers and snapshots in the window follow its count. VLQT_ERR_NULL_HANDLE, VLQT_ERR_UNSUPPORTED on an
 * offscreen scene (its count comes from vlqt_offscreen_bind). Main thread. */
VLQT_API int vlqt_window_set_samples(void* w, int requested);
/* Sample count of the window: of its swap chain once it has one, else the count it will be created with. 0 on an
 * offscreen scene, VLQT_ERR_NULL_HANDLE. */
VLQT_API int vlqt_window_samples(void* w);

/* The perf getters: 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_BAD_ARGUMENT null out/in. */
VLQT_API int   vlqt_window_get_perf(void* w, vlqt_perf* out);
/* Last completed pump of the window. VLQT_ERR_NO_DATA: no pump yet. */
VLQT_API int   vlqt_window_get_perf_sample(void* w, vlqt_perf_sample* out);
/* Percentiles, counters, budget (see vlqt_perf_stats). */
VLQT_API int   vlqt_window_get_perf_stats(void* w, vlqt_perf_stats* out);
/* host values of the last completed pump of the window (call after vlqt_app_process_events).
 * VLQT_ERR_NO_DATA: no pump yet. */
VLQT_API int   vlqt_window_report_host_frame(void* w, const vlqt_host_frame* in);
/* Last reported host frame. VLQT_ERR_NO_DATA: none (the last pump has no report). */
VLQT_API int   vlqt_window_get_host_frame(void* w, vlqt_host_frame* out);
/* Averages/maxima/rates over the last ~1 s (see vlqt_host_stats). */
VLQT_API int   vlqt_window_get_host_stats(void* w, vlqt_host_stats* out);
/* bit0 = perfmeter panel (F2), bit1 = stats (F5); the keys toggle the same flags. set: 0, get: the flags (>= 0);
 * both VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_window_set_overlay(void* w, int flags);
VLQT_API int   vlqt_window_get_overlay(void* w);
/* Debug overlay for all elements of the window (Renderer [Qt] Debug); per element: VLQT_PROP_DEBUG.
 * on != 0 = on. Frame cyan, origin magenta, anchor yellow, baseline green, ... (legend: debug.h). set: 0, get: 1 = on,
 * 0 = off; both VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_window_set_debug(void* w, int on);
VLQT_API int   vlqt_window_get_debug(void* w);

/* Command buffer (protocol.h). Returns 0 = ok; on error <0 and vlqt_last_error() names command index +
 * reason; processing stops at the bad command, earlier commands stay applied. VLQT_ERR_NULL_HANDLE, and the apply
 * range of vlqt_result: VLQT_ERR_BAD_LENGTH ... VLQT_ERR_INVALID (VLQT_ERR_BAD_ARGUMENT = bad value). */
VLQT_API int   vlqt_window_apply(void* w, const unsigned char* buf, int len);
/* Structured form of the last apply error of the window (the object id lets the host show it at the element).
 * 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_BAD_ARGUMENT null out. */
VLQT_API int   vlqt_window_get_apply_error(void* w, vlqt_apply_error* out);
/* Copies the queued events into out (a buffer export, top of this file; 0 bytes = none) and clears the queue; on
 * VLQT_ERR_BUFFER_TOO_SMALL they stay queued. VLQT_ERR_NULL_HANDLE. Call once per frame after vlqt_app_process_events:
 * the poll first queues EV_MEASURED / EV_SCENE_BOUNDS of bound items whose value changed and EV_WINDOW_GEOMETRY of
 * the window after a move/resize/state change (events.h). */
VLQT_API int   vlqt_window_poll(void* w, unsigned char* out, int cap, int* written, int* required);

/* Resources (resources.h, protocol.h vlqt_res_*): window independent, ids u64 = index | generation << 32.
 * Main thread (the thread of vlqt_app_process_events) only. Return codes: 0 = ok, VLQT_ERR_INTERNAL (log),
 * VLQT_ERR_BAD_ARGUMENT bad argument / not allowed in this state, VLQT_ERR_UNKNOWN_ID unknown, stale or already
 * released id (warning in the log). */
/* New resource of kind (VLQT_RES_KIND_*) in state Pending, host reference 1. */
VLQT_API int vlqt_res_create(int kind, unsigned long long* outId);
/* Drops the host reference; freed at the end of a pump once no element holds it either (a running decode is
 * cancelled or finishes first). No status record for the id after this call. */
VLQT_API int vlqt_res_release(unsigned long long id);
/* Upload bytes per pump (default 8 MB), > 0. Uploads run in vlqt_app_process_events before the windows render. */
VLQT_API int vlqt_res_set_budget(int bytes);
/* Status queue (protocol.h, resources): copies all queued records into out (a buffer export, top of this file; 0 bytes
 * = none) and clears the queue; on VLQT_ERR_BUFFER_TOO_SMALL they stay queued. Call once per frame after
 * vlqt_app_process_events. */
VLQT_API int vlqt_res_poll(unsigned char* out, int cap, int* written, int* required);
/* Blocks until the resource leaves Decoding/Uploading or timeoutMs passed: a queued decode runs on the calling thread,
 * completely (the timeout applies to waiting for a worker), one in a worker is awaited, then the upload runs at once (outside the budget). Returns the state then
 * (VLQT_RES_STATE_*; Ready/Failed, or the current state on timeout or when it was never started) or VLQT_ERR_UNKNOWN_ID. */
VLQT_API int vlqt_res_wait(unsigned long long id, int timeoutMs);
/* Live resources (also released ones until the pump frees them): writes min(count, cap) into out, returns count. */
VLQT_API int vlqt_res_list(vlqt_res_info* out, int cap);
/* Cancels and joins the workers and frees every resource (leak warning with the count of still referenced ones).
 * Call before Qt is torn down / at process end; the next start creates the workers again. An exception sets
 * vlqt_last_error. */
VLQT_API void vlqt_res_shutdown(void);
/* Image resources (image_resource.h): start or replace the content of a VLQT_RES_KIND_IMAGE resource. Decode
 * and conversion run in a worker, the upload in the pump within the budget (vlqt_res_wait: at once). New content on
 * a Ready id keeps the old texture visible until the new one is complete; while a decode/upload runs only the newest
 * content is kept. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, bad argument), VLQT_ERR_UNKNOWN_ID. A missing or broken file
 * / an unknown format ends in Failed with RES_EV_ERROR. */
/* File (UTF-8 path; PNG, JPG, ... via QImageReader, EXIF rotation applied). flags: VLQT_IMG_*. */
VLQT_API int vlqt_res_load_image(unsigned long long id, const char* utf8Path, unsigned flags);
/* Pixels of C#: copied synchronously (data may be reused after the call), len = bytes readable at data. */
VLQT_API int vlqt_res_set_pixels(unsigned long long id, const vlqt_pixel_desc* desc, const void* data, int len);
/* Shader resources (shader_resource.h, protocol.h "ShaderEffect"): start or replace the content of a
 * VLQT_RES_KIND_SHADER resource. utf8Path: a .qsb file or "builtin:<name>" (shaders/<name>.frag|.vert.qsb next to
 * QtNative); stage: VLQT_SHADER_STAGE_*. The file is read and checked in a worker. New content on a Ready id keeps
 * the old shader until the new one is Ready. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, empty path, bad stage),
 * VLQT_ERR_UNKNOWN_ID. Missing file, not a .qsb, wrong stage, no variant for the backend: Failed with RES_EV_ERROR. */
VLQT_API int vlqt_res_load_shader(unsigned long long id, const char* utf8Path, int stage);
/* Shader from source (protocol.h "ShaderEffect"): utf8Source = full Qt GLSL (#version 440, NUL-terminated,
 * copied at the call), baked by QShaderBaker in a worker for the backend of the process, cached per hash of the source.
 * Same resource rules as vlqt_res_load_shader. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, empty source, bad stage),
 * VLQT_ERR_UNKNOWN_ID. Bake errors
 * (compiler message with line numbers) and failed checks: Failed with RES_EV_ERROR. */
VLQT_API int vlqt_res_load_shader_source(unsigned long long id, const char* utf8Source, int stage);
/* Shader reflection (shader node factory): bakes utf8Source (full Qt GLSL, NUL-terminated) synchronously to
 * SPIR-V 100 only (under the same mutex as the runtime bakes, never in parallel) and writes the result as UTF-8 text
 * to buf (a buffer export, top of this file; a bake error is VLQT_OK with the text "error ..."). VLQT_ERR_BAD_ARGUMENT:
 * empty source, stage not VLQT_SHADER_STAGE_*. No QGuiApplication needed, any thread.
 * Text: one entry per line, '\n'-separated, fields separated by one space:
 *   line 1: "ok" or "error"
 *   error:  the error lines as in a RES_EV_ERROR of a bake ("<stage> code, line <n>: <text>")
 *   ok:     "uniform <name> <type> <arrayLen>"  in block order (offset), including qt_Matrix / qt_Opacity;
 *                                              arrayLen 0 = no array
 *           "sampler <name> <binding>"         in binding order
 *   <type>: GLSL name: float vec2 vec3 vec4 int ivec2 ivec3 ivec4 uint uvec2 uvec3 uvec4 bool mat2 mat3 mat4
 *           sampler2D, anything else "other". */
VLQT_API int vlqt_shader_reflect(const char* utf8Source, int stage, char* buf, int bufLen, int* written, int* required);
/* Snapshot resources (protocol.h): main thread. Starts grabbing element elementId of window w (scale > 0:
 * pixel size = item size in DIP * scale, ignoring the screen's device pixel ratio) into a VLQT_RES_KIND_SNAPSHOT
 * resource. Asynchronous: the result arrives with RES_EV_INFO / STATE Ready in a later pump (at least one render of
 * the window), errors as Failed with RES_EV_ERROR. A snapshot while one is in flight replaces it (only the newest
 * result is kept); vlqt_res_release during a grab drops the result. A snapshot on a Failed id starts over (Failed ->
 * Decoding -> INFO + Ready | Failed), on a Ready id it stays Ready until the new pixels replace the old ones (INFO).
 * A grab never stays in Decoding: element/window destroyed, window no longer exposed (offscreen scene, #128: unbound)
 * or no result within 10 s -> Failed. An offscreen scene must be bound: the grab marks it dirty, so the next
 * vlqt_offscreen_render_batch that contains it renders it also when nothing changed. vlqt_res_wait does not block on a
 * snapshot (returns the current state). 0, VLQT_ERR_NULL_HANDLE (w NULL), VLQT_ERR_BAD_ARGUMENT (wrong kind,
 * scale <= 0), VLQT_ERR_UNKNOWN_ID; element errors (unknown, not in the window, no size) are Failed + RES_EV_ERROR, rc 0.
 * Qt Quick software backend (QT_QUICK_BACKEND=software, e.g. Linux offscreen): Qt renders the grab into an opaque
 * pixmap, so the pixels have alpha 255 and transparent areas are black (premultiplied colors stay right). */
VLQT_API int vlqt_res_snapshot(unsigned long long id, void* w, unsigned elementId, float scale);
/* Pixels of the last finished snapshot (Ready, also while a replacing one is in flight): fills out (width, height,
 * row_bytes = width * 4, format = 9 B8G8R8A8, premultiplied = 1; also on VLQT_ERR_BUFFER_TOO_SMALL) and copies the
 * rows top-down into dst (a buffer export, top of this file). VLQT_ERR_BAD_ARGUMENT wrong kind, VLQT_ERR_NO_DATA no
 * snapshot yet, VLQT_ERR_UNKNOWN_ID. */
VLQT_API int vlqt_res_read_pixels(unsigned long long id, void* dst, int cap, vlqt_pixel_desc* out, int* written, int* required);
/* Tooltip preview of an image resource, only on a Ready resource: the worker made it when the content was
 * loaded with VLQT_IMG_PREVIEW (load_image flags / set_pixels desc.flags), longest edge <= 256 px, never upscaled.
 * Fills desc (width, height, row_bytes = width * 4, format = 9 B8G8R8A8, premultiplied = 1; also on
 * VLQT_ERR_BUFFER_TOO_SMALL) and copies the rows top-down into dst (a buffer export, top of this file).
 * VLQT_ERR_BAD_ARGUMENT wrong kind, VLQT_ERR_NO_DATA no preview (no flag, not Ready yet), VLQT_ERR_UNKNOWN_ID. Main
 * thread. */
VLQT_API int vlqt_res_read_preview(unsigned long long id, unsigned char* dst, int cap, vlqt_pixel_desc* desc, int* written,
                                   int* required);
/* Audio taps (audio_tap.h, protocol.h "Audio tap"). Main thread: fixes the format of a Pending
 * VLQT_RES_KIND_AUDIO_TAP resource (interleaved float, channels 1..VLQT_AUDIO_TAP_MAX_CHANNELS, sampleRate
 * VLQT_AUDIO_TAP_MIN_SAMPLE_RATE..MAX) and makes it Ready at once (RES_EV_INFO w = channels, h = sampleRate, bytes =
 * ring). Once per tap: a new sample rate needs a new tap. 0, VLQT_ERR_BAD_ARGUMENT (wrong kind, bad format, already
 * initialised), VLQT_ERR_UNKNOWN_ID. */
VLQT_API int vlqt_res_audio_tap_init(unsigned long long id, int channels, int sampleRate);
/* Reads the tap: any thread (VL.Audio's audio thread), no allocation, never waits for the main thread; one reader per
 * tap. dst != NULL: copies up to maxFrames frames (maxFrames * channels floats, interleaved) of one segment into dst
 * and consumes them; dst == NULL: drops up to maxFrames frames of one segment (catching up); maxFrames == 0: state
 * only. channels must equal the tap's (guards dst). state (may be NULL) is filled on every return >= 0 and on
 * VLQT_ERR_BAD_ARGUMENT for a tap not yet initialised. Returns the frames copied/dropped (0 = none available),
 * VLQT_ERR_BAD_ARGUMENT (maxFrames < 0, channels mismatch, not initialised) or VLQT_ERR_UNKNOWN_ID (unknown, released
 * or freed id; silent: the reader polls after a release). Allocates nothing, also not for vlqt_last_error. */
VLQT_API int vlqt_res_read_audio(unsigned long long id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state);
/* The clock of the audio tap times: std::chrono::steady_clock in ns (Windows: QPC = Stopwatch.GetTimestamp,
 * Linux: CLOCK_MONOTONIC). Any thread, no Qt needed. */
VLQT_API long long vlqt_now_ns(void);
/* Image file writer (FileWriter nodes): any thread, synchronous (encodes and writes before it returns), no
 * window needed. Writes the pixels of desc/data (rows top-down, desc.row_bytes per row, len >= height * row_bytes;
 * desc.format a VL PixelFormat number as in vlqt_res_set_pixels: 1, 2, 4-10 and 12, e.g. 9 B8G8R8A8, 7 R8G8B8A8; not
 * 3 R32F and 11 R32G32F; premultiplied 0/1, flags ignored) to utf8Path. The file format comes from the extension (png, jpg/jpeg, bmp, ... what
 * QImageWriter can write); premultiplied pixels are un-premultiplied for formats with alpha, formats without alpha
 * get the pixels over black. quality 0..100 applies to lossy formats (jpg, webp) only, -1 = Qt's default. Missing
 * folders are created, an existing file is overwritten. Returns 0; VLQT_ERR_BAD_ARGUMENT (null, size <= 0, row_bytes
 * or len too small, unsupported pixel format), VLQT_ERR_UNSUPPORTED unknown or unsupported file format (extension),
 * VLQT_ERR_IO folder could not be created or the write failed; vlqt_last_error says why. */
VLQT_API int vlqt_image_write(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality);
/* Tooltip previews (package 01): main thread. */
/* Number of frames in which the scene of window w changed since it was created (item/reconciler/video update(),
 * polish, resize): not every pump, and not the render a grab (vlqt_res_snapshot / _fit) triggers by itself, so a
 * static scene keeps the count while snapshots run. Compare two values for "changed since". >= 0, VLQT_ERR_NULL_HANDLE. */
VLQT_API long long vlqt_window_change_count(void* w);
/* Like vlqt_res_snapshot, but the scale is chosen so that the longest edge of the image is <= maxEdgePx and the scale
 * is <= 1 (pixels per DIP, never above the DIP size; the screen's device pixel ratio is ignored): a 1000 x 500 DIP
 * element at 256 gives 256 x 128, a 100 x 50 one gives 100 x 50. Same resource rules and errors as vlqt_res_snapshot.
 * 0, VLQT_ERR_NULL_HANDLE (w NULL), VLQT_ERR_BAD_ARGUMENT (wrong kind, maxEdgePx <= 0), VLQT_ERR_UNKNOWN_ID. */
VLQT_API int   vlqt_res_snapshot_fit(unsigned long long id, void* w, unsigned elementId, int maxEdgePx);

/* App fonts (LoadFont node): font files for Text/Style FontFamily without installing them. Qt GUI thread only (the
 * vvvv main thread, after vlqt_app_init); other threads get VLQT_ERR_WRONG_THREAD. One QFontDatabase registration per file (canonical path
 * + size + last-modified), shared and reference counted: every successful add takes one reference, release drops it.
 * An entry without references is removed at the end of the pump (vlqt_app_process_events) unless it was added again in
 * between; texts naming a removed family fall back to a substitute at their next layout.
 * add: 0 = registered or shared, *outHandle != 0, families = '\n'-separated family names of the file (a .ttc can hold
 * several) as text of a buffer export (top of this file). Every failure leaves *outHandle 0 and takes no reference,
 * also VLQT_ERR_BUFFER_TOO_SMALL (call again with a larger buffer); VLQT_ERR_NOT_INITIALIZED, VLQT_ERR_WRONG_THREAD,
 * VLQT_ERR_BAD_ARGUMENT (null path or outHandle), VLQT_ERR_IO file not found, VLQT_ERR_UNSUPPORTED not a font Qt can
 * read. release: 0, VLQT_ERR_WRONG_THREAD, VLQT_ERR_UNKNOWN_ID unknown handle or no reference left. */
VLQT_API int   vlqt_font_add(const char* utf8Path, unsigned long long* outHandle, char* families, int familiesCap,
                             int* written, int* required);
VLQT_API int   vlqt_font_release(unsigned long long handle);

/* Offscreen scenes (QtTexture; design: docs/decisions/render-texture.md). A Qt scene without a native window
 * (QQuickRenderControl) on QtNative's shared D3D11 device that renders into a texture of a foreign D3D11 device
 * (Stride's), shared by NT handle. Two shared ID3D11Fences per binding order Qt's write and the foreign device's read
 * on the GPU; no CPU wait, the foreign context gets only Signal/Wait calls. Main thread (the thread of
 * vlqt_app_process_events) only. Return codes: vlqt_result, VLQT_ERR_NULL_HANDLE for a null handle or a window.
 *
 * The scene handle is a window handle: these vlqt_window_* functions take it unchanged and work as for a window:
 * _destroy (unbinds first), _apply (clear colour via VLQT_OP_CLEAR_COLOR; alpha 0 = transparent texture),
 * _get_apply_error, _poll, _set_debug/_get_debug, _set_overlay/_get_overlay, _get_perf/_sample/_stats,
 * _report_host_frame/_get_host_frame/_get_host_stats, _change_count, vlqt_res_snapshot/_fit. The window-only ones
 * are no-ops on a scene: _show, _hide, _set_title, _set_icon, _set_mode return 0 or do nothing, _take_close_request and
 * _is_visible return 0, _resize and _set_samples return VLQT_ERR_UNSUPPORTED (size and samples come from
 * vlqt_offscreen_bind); input
 * goes through vlqt_offscreen_send_*. An offscreen scene never renders in
 * vlqt_app_process_events(_for): only vlqt_offscreen_render_batch renders it. 1 DIP = 1 texture pixel. */

/* Creates an unbound offscreen scene of width x height DIP (< 1 = 1). NULL = error (vlqt_last_error). Free it with
 * vlqt_window_destroy. */
VLQT_API void* vlqt_offscreen_create(int width, int height);
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
VLQT_API int   vlqt_offscreen_bind(void* scene, void* device, void* ntHandle, int width, int height, int format, int samples,
                                  int* usedSamples);
/* Frees the binding in the order render target -> opened texture -> fences; the scene and its elements stay. VLQT_OK
 * also when not bound; VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_offscreen_unbind(void* scene);
/* 1 = bound, 0 = unbound (never bound, unbound, failed bind, or unbound by QtNative after a device loss of its own
 * device: the caller binds again), VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_offscreen_is_bound(void* scene);
/* Renders the scheduled scenes of one host frame; call once per frame per foreign device (Stride: VLGame.DrawStarted,
 * after all updates, before the first draw). context: the immediate ID3D11DeviceContext* of the device the scenes
 * are bound to. Advances the animation driver to driverSeconds once (as vlqt_driver_advance, idempotent per value),
 * then per bound scene, in this order:
 *   Signal(readFence, n) on context (the commands of the previous frame that read the texture were submitted before);
 *   Wait(readFence, n) on QtNative's context;
 *   if the scene changed (dirty) or was just bound: polish, sync, render into the texture; Signal(writeFence, n) and
 *   Flush on QtNative's context (without the flush the foreign GPU waits forever); Wait(writeFence, n) on context.
 * An unchanged scene costs no render and no fence signal. NULL entries and unbound scenes are skipped. Returns the
 * number of scenes rendered (>= 0), or VLQT_ERR_BAD_ARGUMENT (null context, null scenes with count > 0, count < 0),
 * VLQT_ERR_DEVICE_LOST, VLQT_ERR_UNSUPPORTED.
 * On a render thread (vlqt_offscreen_set_threaded) the fence order is the same, the work splits: the read signals and
 * polish run here, then one Flush of context; beginFrame and sync run on the render thread while this call waits;
 * the Wait(writeFence, n) on context is queued before the render thread signals n; render, endFrame, Signal and Flush
 * run on the render thread after this call returned. context's GPU waits for Qt's frame, its CPU never does; this
 * call blocks only while a render thread is still busy with the previous batch. */
VLQT_API int   vlqt_offscreen_render_batch(void* context, void* const* scenes, int count, double driverSeconds);
/* Where offscreen scenes render (phase 5; docs/decisions/render-texture.md, "Qt render thread"). threadCount
 * 0 = on the main thread, inside vlqt_offscreen_render_batch; n = 1 ... 16 = on a pool of n Qt render threads shared by
 * every scene of the process, a scene assigned round robin at vlqt_offscreen_create. Without this call the environment
 * variable VLQT_OFFSCREEN_THREAD decides (0 = main thread, n = n threads), unset = 1 render thread. Effective only
 * before the first vlqt_offscreen_create; afterwards only the count in use returns VLQT_OK. VLQT_OK,
 * VLQT_ERR_BAD_ARGUMENT (threadCount < 0 or > 16), VLQT_ERR_UNSUPPORTED (too late; vlqt_last_error). */
VLQT_API int   vlqt_offscreen_set_threaded(int threadCount);

/* Input into an offscreen scene (design: docs/decisions/render-texture.md, "Input"). The host maps its input to
 * texture pixels (top-left origin, 1 DIP = 1 pixel; positions outside 0..size are valid: hover ends, a drag goes on) and
 * calls these between two vlqt_offscreen_render_batch calls, before the batch that should show the result. QtNative
 * sends the events with QCoreApplication::sendEvent to the scene's QQuickWindow (synchronous, not marked spontaneous), so the event pipeline of windows (handlers, vlqt_window_poll, channels) is unchanged. Each
 * scene has its own mouse, touchscreen and pen QPointingDevice (point ids and grabs live in the device, so a press sent
 * to two scenes does not cancel the other's grab). QtNative synthesizes MouseButtonDblClick from two presses as
 * QGuiApplication does; Qt shortcuts (QShortcut) do not see the keys. A scene that changes
 * renders in the next batch. Bound or not does not matter. Main thread. Return codes: VLQT_OK, VLQT_ERR_NULL_HANDLE,
 * VLQT_ERR_BAD_ARGUMENT (unknown type or device, a touch id in the wrong state, null text with length > 0); a key or
 * text event while the scene has no focus is dropped and returns VLQT_OK.
 *
 * Focus: a scene window never becomes QGuiApplication's focus window. QtNative gives it focus with QFocusEvent
 * FocusIn when a PRESS lands inside 0..size and takes it with FocusOut when a PRESS lands outside or the host calls
 * vlqt_offscreen_set_focus(scene, 0) (e.g. its window lost the focus). The FocusRelease filter of windows (Esc,
 * press outside the focused element) runs on scenes too. */

/* Pointer event (vlqt_pointer) of device (vlqt_pointer_device) at x/y in texture pixels.
 *   mouse  id ignored; buttons = Qt::MouseButtons held after the event (PRESS/RELEASE: the changed buttons are the
 *          difference to the previous call, one event each; none changed = VLQT_ERR_BAD_ARGUMENT), modifiers = Qt::KeyboardModifiers. QMouseEvent; LEAVE sends QEvent::Leave.
 *   touch  id = the point's id (any int, unique while pressed); buttons ignored. QtNative keeps the active points per
 *          scene and sends one QTouchEvent per call with all of them (others stationary).
 *          Several ids = multitouch. CANCEL cancels every active point (TouchCancel), id ignored; LEAVE is VLQT_ERR_BAD_ARGUMENT.
 *   pen    as a window on Windows gets one (WM_POINTER): a QTabletEvent from a stylus (pointer type Pen), and if nobody
 *          accepts it the same as a mouse event synthesized from the stylus, as QGuiApplication does. id and buttons
 *          ignored (one point; MOVE without PRESS hovers); CANCEL drops the pen's grabs without a release; LEAVE as mouse. */
VLQT_API int   vlqt_offscreen_send_pointer(void* scene, int device, int type, int id, float x, float y, int buttons,
                                           int modifiers);
/* Wheel at x/y (texture pixels): angleDelta dx, dy in eighths of a degree (120 = one notch), modifiers =
 * Qt::KeyboardModifiers. QWheelEvent from the mouse. */
VLQT_API int   vlqt_offscreen_send_wheel(void* scene, float x, float y, int dx, int dy, int modifiers);
/* Key press or release (vlqt_key_event) of key (Qt::Key) with modifiers (Qt::KeyboardModifiers), without text, to the
 * focus item; autoRepeat 1 for repeated presses. Text comes separately (vlqt_offscreen_send_text). */
VLQT_API int   vlqt_offscreen_send_key(void* scene, int type, int key, int modifiers, int autoRepeat);
/* Typed text (UTF-8, length bytes, no NUL needed) to the focus item, e.g. one character of a KeyPress. */
VLQT_API int   vlqt_offscreen_send_text(void* scene, const char* utf8, int length);
/* focused 1: FocusIn, 0: FocusOut (a running edit commits as on any focus loss). No-op if unchanged. */
VLQT_API int   vlqt_offscreen_set_focus(void* scene, int focused);

#ifdef __cplusplus
}
#endif