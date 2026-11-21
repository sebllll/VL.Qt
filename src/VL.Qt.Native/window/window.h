// Windows and the parts of a scene handle that windows and offscreen scenes share: creation, placement, window modes
// (borderless, fullscreen) and the window swap for a new MSAA count. Qt GUI thread. The exports are in window_exports.cpp.
#pragma once

class QIcon;
class QQmlEngine;
class QQuickWindow;
class QSurfaceFormat;

namespace vlqt {

struct SceneHandle;

// Embedded default window icon (CMakeLists.txt, :/vlqt/icon).
const QIcon& defaultIcon();
// Initial geometry of a window before its first show (DIP; the rules are at the definition).
void placeWindow(QQuickWindow* win, float x, float y, int width, int height);
// QML engine with QtNative's imports.
QQmlEngine* newEngine();
// Event filters and frame lock of an on-screen window.
void installWindowFilters(SceneHandle* w, QQuickWindow* win);
// MSAA count of a window's swap chain for requested, clamped by the device (device::clampSamples).
int clampWindowSamples(int requested, QQuickWindow* win);
// Graphics configuration, shared device, scene and perf monitor of a handle whose window, engine and filters exist.
void initShared(SceneHandle* w);
// Window modes (vlqt_window_set_mode): w->mode already holds the new modes.
void applyBorderless(SceneHandle* w, bool on);
void applyFullscreen(SceneHandle* w, bool on);
// Window swap: a new QQuickWindow with format f in the same handle (vlqt_window_set_samples).
void replaceWindow(SceneHandle* w, const QSurfaceFormat& f);

} // namespace vlqt
