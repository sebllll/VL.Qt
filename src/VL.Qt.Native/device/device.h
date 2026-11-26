/* device.h - the one graphics device of the process: on Windows a D3D11 device and its immediate context that every
 * QQuickWindow, image upload and offscreen scene shares. Threads: main, except where a function says otherwise (the device
 * is free-threaded; its context is shared with render threads, see context_lock.h). Owner: QtNative creates it lazily and
 * never releases it at exit; only rebuild replaces it. Linux and other backends: no-op. Design: docs/design/gpu-device.md.
 */
#pragma once

class QQuickWindow;
class QRhi;

namespace vlqt::device {

// Gives the window the shared device (QQuickGraphicsDevice::fromDeviceAndContext); call before the window is shown, so Qt
// creates the window's QRhi on it and a texture uploaded once can be drawn in every window. Creates the device at the
// first window, on the adapter Qt's D3D11 backend would pick. No-op without a shared device (Linux, creation failed,
// VLQT_SHARED_DEVICE=0 set before process start: every window keeps Qt's own device, for diagnosis and A/B timing).
void attach(QQuickWindow* window);

// Returns true if attach or the last rebuild gave the window the shared device, also before the window has a QRhi.
// Main thread, or a render thread while the main thread waits for it.
bool isOnSharedDevice(const QQuickWindow* window);
// Records whether the window is on the shared device (attach, rebuild); forgotten when the window is destroyed.
// Main thread.
void markOnSharedDevice(QQuickWindow* window, bool on);

// The shared device's immediate context (ID3D11DeviceContext*), nullptr if there is none (Linux, creation failed,
// VLQT_SHARED_DEVICE=0) or while rebuild replaces it. Not owned: a caller that keeps it across a pump asks again.
// Main thread, or a render thread while the main thread waits for it (ContextLock, context_lock.h).
void* sharedContext();

// Upload QRhi on the shared device (EnableTimestamps), for the image uploads; nullptr if there is none (Linux, creation
// failed, VLQT_SHARED_DEVICE=0). Creates the device if no window did yet (resources before the first window). Main
// thread.
QRhi* uploadRhi();

// Native device of a QRhi (ID3D11Device* for D3D11), nullptr for other backends.
void* nativeDevice(QRhi* rhi);

// MSAA sample count for a render target: the highest count not above requested (< 1 = 1) that QRhi lists
// (supportedSampleCounts; D3D11 hard-codes 1/2/4/8 there) and, on D3D11, that the device has quality levels for in
// dxgiFormat and in D24_UNORM_S8_UINT (the depth-stencil Qt adds). rhi: the QRhi that will render (a window's, once it
// has one); null = the upload QRhi; without any QRhi (Linux before the first expose) the list 1/2/4/8. Qt's own clamp
// only logs a warning, so callers clamp here and report the result. Main thread.
int clampSamples(int requested, int dxgiFormat, QRhi* rhi);
// Format of a window's swap chain for clampSamples: DXGI_FORMAT_R8G8B8A8_UNORM (Qt's SDR swap chain on D3D11), 0 on
// other platforms.
int windowSwapChainFormat();

// Twice per pump (start and end): releases retired devices, notices a removed shared device (GetDeviceRemovedReason, with
// a warning) and rebuilds; without a shared device after a failed creation, retries it at most once per second and on
// success hands over as rebuild does. Meanwhile windows use devices of their own (kept until their next loss), images are
// not uploaded (CPU copies) and offscreen scenes report VLQT_ERR_DEVICE_LOST; the log says so once. Cheap, allocates
// nothing while the device is fine.
void checkLost();

// Changes with every new shared device (starts at 1 with the first one, 0 = none yet). Content made on another
// generation must not be wrapped in a window's QRhi (resources, QtImageItem). Main thread.
unsigned generation();

// Rebuild after a loss. Listeners free their objects of the old device (lost), the upload QRhi is deleted, a new device
// and upload QRhi are created, every QQuickWindow that was on the old device gets the new one (setGraphicsDevice) and an
// update; Qt then notices the loss itself at that window's next Present, drops the window's QRhi and scene graph and
// creates a new QRhi on the imported device (same window, items keep their state). Listeners then upload again
// (restored). The old device is released once no window QRhi uses it any more (checkLost). Teardown order: texture
// wrappers, windows, upload QRhi, device. Test trigger: vlqt_test_device_lost (vlqt_test.h, test/test_device.h). Main
// thread: inside the pump (checkLost) or from moveTo.
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
// offscreen scenes, QQuickRenderControl, do not count): Qt replaces a window's QRhi only after it noticed a loss, so a
// window on a healthy device would stay on the old one. Main thread.
bool canMove();
// Rebuilds the shared device on the adapter with this LUID (as rebuild: listeners lost/restored), for a scene that binds
// a texture of another adapter (Stride's); the adapter is kept for later rebuilds. Main thread, only while canMove().
// Returns true if the shared device now sits on that adapter; false = unchanged (cannot move, unknown adapter) or no
// shared device afterwards (creation failed, see the log).
bool moveTo(unsigned long long luid);

// test hook (vlqt_test_device_lost, test/test_probes.cpp): the ID3D12Device under the shared device when it was created
// on D3D11On12 (VLQT_TEST_D3D11ON12=1, test/test_device.h), else null. Main thread.
void* testDevice12();

} // namespace vlqt::device
