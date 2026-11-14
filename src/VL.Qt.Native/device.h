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
 * Linux and every non-D3D11 backend: no-op, each window keeps its own device. A shared VkDevice docks here later.
 * VLQT_SHARED_DEVICE=0 (set before process start) also keeps Qt's own device per window (diagnosis, A/B timing).
 */
#pragma once

class QQuickWindow;
class QRhi;

namespace vlqt::device {

// Gives the window the shared device; call before the window is shown. No-op without a shared device.
void attach(QQuickWindow* window);

// Upload QRhi on the shared device, nullptr if there is none (Linux, creation failed, VLQT_SHARED_DEVICE=0). Creates
// the device if no window did yet (resources before the first window, #360). Main thread.
QRhi* uploadRhi();

// Native device of a QRhi (ID3D11Device* for D3D11), nullptr for other backends.
void* nativeDevice(QRhi* rhi);

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

} // namespace vlqt::device
