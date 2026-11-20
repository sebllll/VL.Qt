/* QtNative test exports – for the SmokeTest and the unit tests only, not for production use.
 *
 * Contract: these functions are exported by every build of vlqt.dll / libvlqt.so (the CI tests the binary that
 * ships), but they are not part of the API of vlqt.h. They may change or go in any version; the only promise is the
 * one of VLQT_ABI_VERSION (protocol.h): the number changes whenever a signature, constant or struct here changes.
 * Conventions as in vlqt.h: C ABI, cdecl, UTF-8 strings, return 0 = ok unless noted. Definitions: test/test_*.cpp.
 * The stand-in D3D11 device of the QtTexture tests is a library of its own, not part of QtNative: testkit/testkit.h. */
#pragma once

#include "vlqt.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Mouse event types of vlqt_test_send_mouse / vlqt_test_queue_mouse. PRESS/RELEASE = left button, *_RIGHT = right
 * button, *_RIGHT_SYSTEM = right button synthesized by the OS from touch. */
enum vlqt_mouse {
    VLQT_MOUSE_MOVE = 1,
    VLQT_MOUSE_PRESS = 2,
    VLQT_MOUSE_RELEASE = 3,
    VLQT_MOUSE_LEAVE = 4,
    VLQT_MOUSE_PRESS_RIGHT = 5,
    VLQT_MOUSE_RELEASE_RIGHT = 6,
    VLQT_MOUSE_PRESS_RIGHT_SYSTEM = 7,  /* right button, source Qt::MouseEventSynthesizedBySystem (Windows press-and-hold) */
    VLQT_MOUSE_RELEASE_RIGHT_SYSTEM = 8
};

/* Touch point events of vlqt_test_send_touch / vlqt_test_queue_touch. */
enum vlqt_touch {
    VLQT_TOUCH_PRESS = 1,
    VLQT_TOUCH_MOVE = 2,
    VLQT_TOUCH_RELEASE = 3
};

/* Modes of vlqt_test_device_lost.
 * REMOVE: real removal of the shared device (ID3D12Device5::RemoveDevice of the D3D12 device under it). Needs the
 *         shared device created through D3D11On12, i.e. VLQT_TEST_D3D11ON12=1 set before the first window/resource;
 *         deterministic, no admin, works on WARP (CI). Qt then sees the loss itself at the next Present.
 * HANG:   real loss by the driver: an endless shader on the shared device, Windows resets the GPU after ~2 s (TDR).
 *         Affects every application on that GPU, manual checks only. */
enum vlqt_device_lost {
    VLQT_DEVICE_LOST_REMOVE = 1,
    VLQT_DEVICE_LOST_HANG = 2
};

/* Resource kind of vlqt_res_create (int) for vlqt_test_res_start; the product kinds are vlqt_res_kind (protocol.h). */
enum vlqt_test_res_kind {
    VLQT_RES_KIND_TEST = 1 /* decode sleeps n ms in a worker, upload uses n bytes of budget (vlqt_test_res_start) */
};

/* --- Synthetic input (test/test_input.cpp) ---
 * With env VLQT_TEST_SYNTHETIC_INPUT=1 (read at window creation) windows ignore real (OS) pointer input; the events
 * below pass. On an offscreen scene every input function returns VLQT_OFFSCREEN_NOT_SUPPORTED (protocol.h): a scene
 * takes input through vlqt_offscreen_send_*. */

/* Sends a QMouseEvent (VLQT_MOUSE_MOVE with the buttons held, PRESS/RELEASE = left button, *_RIGHT = right button) or a
 * QEvent::Leave (VLQT_MOUSE_LEAVE, x/y ignored) to the window via QCoreApplication::sendEvent; x/y in window logical
 * pixels. Delivered synchronously, no processEvents needed. 0 = ok, -1 null window, -2 unknown type. */
VLQT_API int   vlqt_test_send_mouse(void* w, int type, float x, float y);
/* Synthetic touch input. type = vlqt_touch for touch point id, x/y in window logical pixels. QtNative keeps the active
 * points per window and sends the complete QTouchEvent (TouchBegin/Update/End, every active point with its state,
 * others stationary) from one test touchscreen (QPointingDevice, registered once) via QCoreApplication::sendEvent:
 * synchronous, not spontaneous (passes VLQT_TEST_SYNTHETIC_INPUT). Several ids = multitouch. 0 = ok, -1 null window,
 * -2 unknown type, PRESS of an active id or MOVE/RELEASE of an inactive one. */
VLQT_API int   vlqt_test_send_touch(void* w, int type, int id, float x, float y);
/* Like vlqt_test_send_mouse / vlqt_test_send_touch (PRESS, MOVE, RELEASE), but queued like platform input
 * (QWindowSystemInterface, asynchronous): delivered by vlqt_app_deliver_input or the next pump. The events come from
 * test devices and pass VLQT_TEST_SYNTHETIC_INPUT. 0 = ok, -1 null window, -2 unknown type or touch id state. */
VLQT_API int   vlqt_test_queue_mouse(void* w, int type, float x, float y);
VLQT_API int   vlqt_test_queue_touch(void* w, int type, int id, float x, float y);
/* Synthetic wheel input: a QWheelEvent at x/y (window logical pixels) with angleDelta (dx, dy) in eighths of a degree
 * (120 = one notch) via QCoreApplication::sendEvent (synchronous, not spontaneous: passes VLQT_TEST_SYNTHETIC_INPUT).
 * 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_send_wheel(void* w, float x, float y, int dx, int dy);
/* Sends a key press (Qt::Key, Qt::KeyboardModifiers) to the window, e.g. F5 page cycling. 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_send_key(void* w, int key, int modifiers);
/* Key press + release with text (UTF-8, may be null/empty) to the window's focus item, e.g. typing digits into a
 * SpinBox (key Qt::Key_1, text "1") or Enter (Qt::Key_Return, "\r"). 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_send_key_text(void* w, int key, int modifiers, const char* text);

/* --- Probes (test/test_probes.cpp) --- */

/* Grabs the window's frame (QQuickWindow::grabWindow) into a PNG file. 0 = ok, -1 null window, -2 no image, -3 not
 * saved; VLQT_OFFSCREEN_NOT_SUPPORTED on an offscreen scene. */
VLQT_API int   vlqt_test_window_grab(void* w, const char* pngPath);
/* The vlqt_perf_stats computation on n given samples with timestamps t_ms (ascending, ms; "now" = t_ms[n-1]) and a
 * fixed budget. 0 = ok, -1 null/negative argument. */
VLQT_API int   vlqt_test_perf_eval(const vlqt_perf_sample* samples, const double* t_ms, int n, double budget_ms, vlqt_perf_stats* out);
/* The vlqt_host_stats computation on n given host frames. 0 = ok, -1 null/negative argument. */
VLQT_API int   vlqt_test_perf_host_eval(const vlqt_host_frame* frames, int n, vlqt_host_stats* out);
/* Number of pointer handlers (QQuickPointerHandler children) of item id (input handlers); -1 null window, -4 unknown
 * id. */
VLQT_API int   vlqt_test_handler_count(void* w, unsigned int id);
/* Busy-waits ms inside one phase of the window's next rendered frame (0 events, 1 sync, 2 render, 3 present, 4 vsync
 * wait) to check that the F5 timeline attributes a spike. 0 = ok, -1 null window. */
VLQT_API int   vlqt_test_perf_spike(void* w, int phase, float ms);
/* Plain UTF-8 text of an F5 page (0 Summary, 1 Timeline, 2 Scene, 3 System, 4 Resources; others empty), built on
 * demand, overlay state unchanged. Returns the byte length without NUL; -(length + 1) if cap is too small; -1 null
 * window. */
VLQT_API int   vlqt_test_perf_text(void* w, int page, char* out, int cap);
/* QRhi of the window and its native device (ID3D11Device* on D3D11, else null). Null window = the upload QRhi on the
 * shared device (creates the device if there is none yet). 0 = ok, -2 no QRhi (scenegraph not initialized yet, or no
 * shared device). */
VLQT_API int   vlqt_test_device_info(void* w, void** device, void** rhi);
/* LUID of the adapter of the shared device (LowPart | HighPart << 32), 0 if there is none yet (never creates it), on
 * Linux and for another graphics API. For vlqt_test_d3d11_create_device of the testkit (testkit/testkit.h). */
VLQT_API unsigned long long vlqt_test_device_luid(void);
/* Makes the shared graphics device lost, mode = vlqt_device_lost. Returns at once; QtNative notices the loss in the
 * next pump and rebuilds device, windows and resources (device.h). 0 = ok, -2 not possible (no shared device, or REMOVE
 * without VLQT_TEST_D3D11ON12=1), -7 unknown mode. */
VLQT_API int   vlqt_test_device_lost(int mode);
/* Content of a Ready image as RGBA8 premultiplied (texture readback). Returns the bytes written, -required if cap is
 * too small (width/height set), -9 if not Ready, -1 readback failed. */
VLQT_API int   vlqt_test_res_read_pixels(unsigned long long id, unsigned char* out, int cap, int* width, int* height);
/* Writes a test image (vertical stripes red, green, blue, white) as PNG/JPG by extension. 0 / -1 / -7. */
VLQT_API int   vlqt_test_write_image(const char* utf8Path, int width, int height, int quality);
/* References of a font handle of vlqt_font_add, 0 = removal pending (end of pump), -9 = removed or unknown. */
VLQT_API int   vlqt_test_font_refs(unsigned long long handle);
/* Last values signalled on the two fences of a bound offscreen scene (see vlqt_offscreen_render_batch) and the number
 * of renders into its texture since bind: an unchanged scene leaves all three unchanged. Out pointers may be NULL.
 * 0 = ok, VLQT_OFFSCREEN_NULL_SCENE, VLQT_OFFSCREEN_UNBOUND. */
VLQT_API int   vlqt_test_offscreen_state(void* scene, unsigned long long* readFence, unsigned long long* writeFence,
                                         long long* renders);

/* --- Test resource kind (test/test_resource_kind.cpp) --- */

/* Starts a Pending VLQT_RES_KIND_TEST resource: decode sleeps decodeMs in a worker (cancellable), upload uses
 * uploadBytes of budget in stripes. flags bit0 = decode fails, bit1 = upload fails. 0 / -7 / -9. */
VLQT_API int   vlqt_test_res_start(unsigned long long id, int decodeMs, long long uploadBytes, int flags);
/* The state machine's transition (VLQT_RES_STATE_*), 0 = done, -7 = rejected (logged), -9. */
VLQT_API int   vlqt_test_res_transition(unsigned long long id, int from, int to);
/* Element reference +1 (delta 1) or -1 (delta -1), like an element of package 04. 0 / -7 / -9. */
VLQT_API int   vlqt_test_res_element_ref(unsigned long long id, int delta);

#ifdef __cplusplus
}
#endif
