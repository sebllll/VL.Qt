// What differs between Windows and Linux at the start of Qt, in windows and in the process: one interface, one
// implementation per platform, platform_win.cpp (Windows) and platform_linux.cpp (Linux), selected in CMakeLists.txt.
// The unload hook (DllMain, ELF destructor) lives in the same two files. Qt GUI thread.
#pragma once

class QFileInfo;
class QGuiApplication;
class QQuickWindow;
class QString;

namespace vlqt::platform {

// Before the QGuiApplication (vlqt_app_init). Windows: Qt Quick on Direct3D 11. Linux: Qt's default backend (OpenGL,
// QSG_RHI_BACKEND overrides); Mesa's D3D12 driver as the default under WSLg (checked by the helper vlqt-glprobe, with
// fallbacks), and a clear message when there is no display server.
void prepareBeforeApp();

// After the QGuiApplication. Windows: lets Enter, Tab, Esc and the arrow keys reach the Qt windows past vvvv's dialog
// navigation (WM_GETDLGCODE). Linux: registers the bundled DejaVu Sans as the app font when the system has no font.
void initAfterApp(QGuiApplication& app);

// True if VLQT_TEST_SYNTHETIC_INPUT=1 in the process environment (Windows: also when the .NET host set it at run time).
bool testSyntheticInputOnly();

// Turns the window frame on or off (Qt::FramelessWindowHint) without re-creating the window. keepClientRect: a shown
// window keeps its client rect, the frame grows or disappears around it; otherwise only the flag changes.
void setFrameless(QQuickWindow& window, bool on, bool keepClientRect);

// Ends every frame of an on-screen window like a present would, at creation and for the window swap. Linux: on Qt's
// offscreen platform with OpenGL a glFlush after each frame, since that platform's swapBuffers does nothing (see
// platform_linux.cpp). Windows: nothing, the swap chain presents.
void endFramesLikePresent(QQuickWindow& window);

// Identity of a file for a registry: the canonical path, case-folded where the file system ignores case (Windows).
QString pathKey(const QFileInfo& file);

} // namespace vlqt::platform
