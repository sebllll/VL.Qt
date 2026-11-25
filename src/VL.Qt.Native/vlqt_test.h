/* QtNative test exports – for the SmokeTest and the unit tests only, not for production use.
 *
 * Contract: these functions are exported by every build of vlqt.dll / libvlqt.so (the CI tests the binary that
 * ships), but they are not part of the API of vlqt.h. They may change or go in any version; the only promise is the
 * one of VLQT_ABI_VERSION (protocol.h): the number changes whenever a signature, constant or struct here changes.
 * Conventions as in vlqt.h (top comment): C ABI, cdecl, UTF-8 strings, return codes of vlqt_result, vlqt_last_error,
 * buffer exports, threads (main thread, except vlqt_test_perf_eval, vlqt_test_perf_host_eval and vlqt_test_write_image:
 * any thread). Definitions: test/test_*.cpp.
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
 *         Affects every application on that GPU, manual checks only.
 * REMOVE_FAIL_ONCE: REMOVE, and the next creation of the shared device (the rebuild's) fails once; QtNative retries it
 *         about a second later (device.h). */
enum vlqt_device_lost {
    VLQT_DEVICE_LOST_REMOVE = 1,
    VLQT_DEVICE_LOST_HANG = 2,
    VLQT_DEVICE_LOST_REMOVE_FAIL_ONCE = 3
};

/* Resource kind of vlqt_res_create (int) for vlqt_test_res_start; the product kinds are vlqt_resource_kind (protocol.h). */
enum vlqt_test_resource_kind {
    VLQT_RESOURCE_KIND_TEST = 1 /* decode sleeps n ms in a worker, upload uses n bytes of budget (vlqt_test_res_start) */
};

/* --- Synthetic input (test/test_input.cpp) ---
 * With env VLQT_TEST_SYNTHETIC_INPUT=1 (read at window creation) windows ignore real (OS) pointer input; the events
 * below pass. On an offscreen scene every input function returns VLQT_ERR_UNSUPPORTED: a scene takes input through
 * vlqt_offscreen_send_*. Null window: VLQT_ERR_NULL_HANDLE. */

/* Sends a QMouseEvent (VLQT_MOUSE_MOVE with the buttons held, PRESS/RELEASE = left button, *_RIGHT = right button) or a
 * QEvent::Leave (VLQT_MOUSE_LEAVE, x/y ignored) to the window via QCoreApplication::sendEvent; x/y in window logical
 * pixels. Delivered synchronously, no processEvents needed. 0, VLQT_ERR_BAD_ARGUMENT unknown type. */
VLQT_API int   vlqt_test_send_mouse(vlqt_scene* window, int type, float x, float y);
/* Synthetic touch input. type = vlqt_touch for touch point id, x/y in window logical pixels. QtNative keeps the active
 * points per window and sends the complete QTouchEvent (TouchBegin/Update/End, every active point with its state,
 * others stationary) from one test touchscreen (QPointingDevice, registered once) via QCoreApplication::sendEvent:
 * synchronous, not spontaneous (passes VLQT_TEST_SYNTHETIC_INPUT). Several ids = multitouch. 0, VLQT_ERR_BAD_ARGUMENT
 * unknown type, PRESS of an active id or MOVE/RELEASE of an inactive one. */
VLQT_API int   vlqt_test_send_touch(vlqt_scene* window, int type, int id, float x, float y);
/* Like vlqt_test_send_mouse / vlqt_test_send_touch (PRESS, MOVE, RELEASE), but queued like platform input
 * (QWindowSystemInterface, asynchronous): delivered by vlqt_app_deliver_input or the next pump. The events come from
 * test devices and pass VLQT_TEST_SYNTHETIC_INPUT. 0, VLQT_ERR_BAD_ARGUMENT unknown type or touch id state. */
VLQT_API int   vlqt_test_queue_mouse(vlqt_scene* window, int type, float x, float y);
VLQT_API int   vlqt_test_queue_touch(vlqt_scene* window, int type, int id, float x, float y);
/* Synthetic wheel input: a QWheelEvent at x/y (window logical pixels) with angleDelta (dx, dy) in eighths of a degree
 * (120 = one notch) via QCoreApplication::sendEvent (synchronous, not spontaneous: passes VLQT_TEST_SYNTHETIC_INPUT).
 * 0. */
VLQT_API int   vlqt_test_send_wheel(vlqt_scene* window, float x, float y, int dx, int dy);
/* Sends a key press (Qt::Key, Qt::KeyboardModifiers) to the window, e.g. F5 page cycling. 0. */
VLQT_API int   vlqt_test_send_key(vlqt_scene* window, int key, int modifiers);
/* Key press + release with text (UTF-8, may be null/empty) to the window's focus item, e.g. typing digits into a
 * SpinBox (key Qt::Key_1, text "1") or Enter (Qt::Key_Return, "\r"). 0. */
VLQT_API int   vlqt_test_send_key_text(vlqt_scene* window, int key, int modifiers, const char* text);

/* --- Probes (test/test_probes.cpp) --- */

/* Grabs the window's frame (QQuickWindow::grabWindow) into a PNG file. 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_NO_DATA no
 * image, VLQT_ERR_IO not saved, VLQT_ERR_UNSUPPORTED on an offscreen scene. */
VLQT_API int   vlqt_test_window_grab(vlqt_scene* window, const char* pngPath);
/* The vlqt_perf_stats computation on n given samples with timestamps t_ms (ascending, ms; "now" = t_ms[n-1]) and a
 * fixed budget. 0, VLQT_ERR_BAD_ARGUMENT null/negative argument. */
VLQT_API int   vlqt_test_perf_eval(const vlqt_perf_sample* samples, const double* t_ms, int n, double budget_ms, vlqt_perf_stats* out);
/* The vlqt_perf computation (vlqt_scene_get_perf) on n given samples: the last 60 of them. 0, VLQT_ERR_BAD_ARGUMENT
 * null/negative argument. */
VLQT_API int   vlqt_test_perf_summary_eval(const vlqt_perf_sample* samples, int n, vlqt_perf* out);
/* The vlqt_host_pump_stats computation on n given host pumps. 0, VLQT_ERR_BAD_ARGUMENT null/negative argument. */
VLQT_API int   vlqt_test_perf_host_eval(const vlqt_host_pump* pumps, int n, vlqt_host_pump_stats* out);
/* Number of pointer handlers (QQuickPointerHandler children) of item id (input handlers); VLQT_ERR_NULL_HANDLE,
 * VLQT_ERR_UNKNOWN_OBJECT. */
VLQT_API int   vlqt_test_handler_count(vlqt_scene* window, unsigned int id);
/* Sets the perf overlay of the window like the keys do: flags as vlqt_scene_get_overlay returns them (bit0 meter, bit1
 * stats, bit3 freeze with the stats, bits 4..6 = page + 1, 0 = the remembered page). 0, VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_test_set_overlay(vlqt_scene* window, int flags);
/* Busy-waits ms inside one phase of the window's next rendered frame (0 events, 1 sync, 2 render, 3 present, 4 vsync
 * wait) to check that the F5 timeline attributes a spike. 0, VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_test_perf_spike(vlqt_scene* window, int phase, float ms);
/* Plain UTF-8 text of an F5 page (0 Summary, 1 Timeline, 2 Scene, 3 System, 4 Resources; others empty), built on
 * demand, overlay state unchanged: a buffer export (vlqt.h). VLQT_ERR_NULL_HANDLE. */
VLQT_API int   vlqt_test_perf_text(vlqt_scene* window, int page, char* out, int cap, int* written, int* required);
/* QRhi of the window and its native device (ID3D11Device* on D3D11, else null). Null window = the upload QRhi on the
 * shared device (creates the device if no creation was attempted yet; a failed one only the pump retries, device.h).
 * 0, VLQT_ERR_NO_DATA no QRhi (scenegraph not initialized yet, or no shared device). */
VLQT_API int   vlqt_test_device_info(vlqt_scene* window, void** device, void** rhi);
/* LUID of the adapter of the shared device (LowPart | HighPart << 32), 0 if there is none yet (never creates it), on
 * Linux and for another graphics API. For vlqt_test_d3d11_create_device of the testkit (testkit/testkit.h). */
VLQT_API unsigned long long vlqt_test_device_luid(void);
/* Makes the shared graphics device lost, mode = vlqt_device_lost. Returns at once; QtNative notices the loss in the
 * next pump and rebuilds device, windows and resources (device.h). 0, VLQT_ERR_UNSUPPORTED not possible (no shared
 * device, REMOVE or REMOVE_FAIL_ONCE without VLQT_TEST_D3D11ON12=1, HANG, not D3D11), VLQT_ERR_BAD_ARGUMENT unknown mode. */
VLQT_API int   vlqt_test_device_lost(int mode);
/* Content of a Ready image as RGBA8 premultiplied (texture readback): a buffer export (vlqt.h); width/height set
 * also on VLQT_ERR_BUFFER_TOO_SMALL. VLQT_ERR_UNKNOWN_ID, VLQT_ERR_NO_DATA not Ready, VLQT_ERR_INTERNAL readback
 * failed. */
VLQT_API int   vlqt_test_res_read_pixels(unsigned long long id, unsigned char* out, int cap, int* width, int* height,
                                         int* written, int* required);
/* Writes a test image (vertical stripes red, green, blue, white) as PNG/JPG by extension. 0, VLQT_ERR_BAD_ARGUMENT,
 * VLQT_ERR_IO. */
VLQT_API int   vlqt_test_write_image(const char* utf8Path, int width, int height, int quality);
/* References of a font handle of vlqt_font_add, 0 = removal pending (end of pump), VLQT_ERR_UNKNOWN_ID = removed or
 * unknown. */
VLQT_API int   vlqt_test_font_refs(unsigned long long handle);
/* Last values signalled on the two fences of a bound offscreen scene (see vlqt_offscreen_render_batch) and the number
 * of renders into its texture since bind: an unchanged scene leaves all three unchanged. Out pointers may be NULL.
 * 0, VLQT_ERR_NULL_HANDLE, VLQT_ERR_UNBOUND. */
VLQT_API int   vlqt_test_offscreen_state(vlqt_scene* scene, unsigned long long* readFence, unsigned long long* writeFence,
                                         long long* renders);

/* --- Test resource kind (test/test_resource_kind.cpp) --- */

/* Starts a Pending VLQT_RESOURCE_KIND_TEST resource: decode sleeps decodeMs in a worker (cancellable), upload uses
 * uploadBytes of budget in stripes. flags bit0 = decode fails, bit1 = upload fails. 0, VLQT_ERR_BAD_ARGUMENT,
 * VLQT_ERR_UNKNOWN_ID. */
VLQT_API int   vlqt_test_res_start(unsigned long long id, int decodeMs, long long uploadBytes, int flags);
/* The state machine's transition (VLQT_RESOURCE_STATE_*), 0 = done, VLQT_ERR_BAD_ARGUMENT = rejected (logged),
 * VLQT_ERR_UNKNOWN_ID. */
VLQT_API int   vlqt_test_res_transition(unsigned long long id, int from, int to);
/* Element reference +1 (delta 1) or -1 (delta -1), like an element of package 04. 0, VLQT_ERR_BAD_ARGUMENT,
 * VLQT_ERR_UNKNOWN_ID. */
VLQT_API int   vlqt_test_res_element_ref(unsigned long long id, int delta);

#ifdef __cplusplus
}
#endif
