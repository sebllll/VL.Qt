/* device.h - one graphics device per process (#358, images package 01).
 *
 * Windows/D3D11: QtNative creates one ID3D11Device + immediate context (lazily, at the first window) on the adapter
 * Qt's D3D11 backend would pick, and hands it to every QQuickWindow with QQuickGraphicsDevice::fromDeviceAndContext
 * before its first expose. Qt still creates one QRhi per window, all on this device, so a texture uploaded once can
 * be drawn in every window. Next to them an upload QRhi on the same device (EnableTimestamps) for the image packages.
 *
 * Teardown order (shared-device spike, docs/decisions/device-sharing.md): texture wrappers -> windows -> upload QRhi -> device. Device and upload
 * QRhi are never released at exit: like g_app they live until process end, so nothing is destroyed out of order.
 * Only rebuild replaces them: the upload QRhi is deleted at once, the old device is released once no window QRhi
 * uses it any more (checkLost, after Qt dropped the window QRhis).
 *
 * Device lost (#367): checkLost notices it (GetDeviceRemovedReason) and calls
 * rebuild: listeners free their objects of the old device (lost), a new device and upload QRhi are created, every
 * QQuickWindow that was on the old device gets the new one (setGraphicsDevice) and an update; Qt then notices the
 * loss itself at that window's next Present, drops the window's QRhi and scenegraph ("Graphics device lost") and
 * creates a new QRhi on the imported new device. Same QQuickWindow, same HWND, items keep their state. Listeners
 * then re-upload (restored). Test trigger: testLost (vlqt_test_device_lost).
 *
 * Adapter (#116, QtTexture): the first device goes on the adapter Qt's D3D11 backend would pick. moveTo rebuilds it
 * on another adapter (that of Stride's device) the same way, but only while no on-screen window renders on it
 * (canMove): Qt replaces a window's QRhi only after it noticed a loss, so a window on a healthy device would stay on
 * the old one. The adapter of a move is kept for later rebuilds.
 *
 * Threads (#116 phase 5): the device is free-threaded; its immediate context is multithread-protected and shared with the
 * render threads of offscreen scenes, so stateful sequences on it take a ContextLock. Everything else here: main thread.
 *
 * Linux and every non-D3D11 backend: no-op, each window keeps its own device. A shared VkDevice docks here later.
 * VLQT_SHARED_DEVICE=0 (set before process start) also keeps Qt's own device per window (diagnosis, A/B timing).
 */
#pragma once

class QQuickWindow;
class QRhi;

namespace vlqt::device {

// Gives the window the shared device; call before the window is shown. No-op without a shared device.
void attach(QQuickWindow* window);

// Holds the shared device's immediate context (ContextLock) from beforeRendering to afterFrameEnd of every frame of
// this on-screen window, so the render threads of offscreen scenes (#116 phase 5) cannot change the context state while
// QRhi D3D11 submits the frame (in endFrame). The vsync wait in beginFrame stays outside. Main thread, basic loop; not
// for offscreen scenes (their render thread locks itself).
void lockFrames(QQuickWindow* window);
// Ends every window frame lock still held. A frame normally ends it at afterFrameEnd; QQuickWindow::grabWindow renders
// without that signal. Called after the pump renders, before rebuild's listeners and before a blocking render-thread
// job (render_thread.h): the main thread must not hold the lock there (lock order, ContextLock). Main thread.
void releaseFrameLocks();

// Upload QRhi on the shared device, nullptr if there is none (Linux, creation failed, VLQT_SHARED_DEVICE=0). Creates
// the device if no window did yet (resources before the first window, #360). Main thread.
QRhi* uploadRhi();

// Native device of a QRhi (ID3D11Device* for D3D11), nullptr for other backends.
void* nativeDevice(QRhi* rhi);

// MSAA sample count for a render target (#118): the highest count not above requested (< 1 = 1) that QRhi lists
// (supportedSampleCounts; D3D11 hard-codes 1/2/4/8 there) and, on D3D11, that the device has quality levels for in
// dxgiFormat and in D24_UNORM_S8_UINT (the depth-stencil Qt adds). rhi: the QRhi that will render (a window's, once it
// has one); null = the upload QRhi; without any QRhi (Linux before the first expose) the list 1/2/4/8. Qt's own clamp
// only logs a warning, so callers clamp here and report the result. Main thread.
int clampSamples(int requested, int dxgiFormat, QRhi* rhi);

// Twice per pump (start and end): releases retired devices, notices a removed shared device (warning with
// GetDeviceRemovedReason) and rebuilds. Cheap, allocates nothing while the device is fine.
void checkLost();

// Changes with every new shared device (starts at 1 with the first one, 0 = none yet). Content made on another
// generation must not be wrapped in a window's QRhi (resources, QtImageItem). Main thread.
unsigned generation();

// Rebuild after a loss (see above). Main thread: inside the pump (checkLost) or from moveTo.
void rebuild();

// Called by rebuild on the main thread, in registration order. lost: the old device is gone, free every object of it
// (textures, staging, QRhi resources), keep the sources. restored: new device and upload QRhi exist, upload again.
struct Listener
{
    void (*lost)() = nullptr;
    void (*restored)() = nullptr;
};
void addListener(Listener listener);

// LUID of the adapter of a D3D11 device (LowPart | HighPart << 32); 0 for null, another API or on failure.
unsigned long long adapterLuidOf(void* d3d11Device);
// LUID of the shared device's adapter, 0 if there is none. Main thread.
unsigned long long adapterLuid();

// True while the shared device exists and no on-screen QQuickWindow has a QRhi on it (windows not yet exposed and
// offscreen scenes, QQuickRenderControl, do not count). Main thread.
bool canMove();
// Rebuilds the shared device on the adapter with this LUID (as rebuild: listeners lost/restored). Main thread,
// only while canMove(). Returns true if the shared device now sits on that adapter; false = unchanged (cannot move,
// unknown adapter) or no shared device afterwards (creation failed, see the log).
bool moveTo(unsigned long long luid);

// vlqt_test_device_lost backend: mode = vlqt_device_lost (protocol.h). 0 / -2 / -7.
int testLost(int mode);

// Holds the immediate context of a D3D11 device for a stateful sequence of calls (ID3D11Multithread::Enter/Leave,
// recursive per thread). The render threads of offscreen scenes (#116, render_thread.h) share the shared device's
// immediate context with the main thread. The shared device's context is multithread-protected from its creation on
// (every rebuild too), so single calls need no lock; a sequence whose context state must not interleave with another
// thread's does (a QRhi frame, Wait + beginFrame, endFrame + Signal + Flush, Map + Unmap). No-op for a null context and
// on other platforms. Main-thread holders: window frames (lockFrames), upload QRhi frames and staging copies
// (image_resource.cpp). Lock order: never wait for another thread while holding it, above all never for a blocking
// render-thread job (RenderThread::call, drain): that job may take the lock too and both threads would wait forever.
class ContextLock
{
public:
    // The shared device's immediate context; no-op without one. Not while the main thread rebuilds the device.
    ContextLock();
    // context: an ID3D11DeviceContext*, or null.
    explicit ContextLock(void* d3d11Context);
    ~ContextLock();
    ContextLock(const ContextLock&) = delete;
    ContextLock& operator=(const ContextLock&) = delete;

private:
    void* m_multithread = nullptr; // ID3D11Multithread*, entered
};

} // namespace vlqt::device
