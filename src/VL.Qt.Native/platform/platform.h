// The parts of the start and of the unload that exist on one platform only: platform_win.cpp (Windows) and
// platform_linux.cpp (Linux), selected in CMakeLists.txt. Qt GUI thread, inside vlqt_app_init.
#pragma once

class QGuiApplication;

namespace vlqt::platform {

#ifdef _WIN32
// After the QGuiApplication: lets Enter, Tab, Esc and the arrow keys reach the Qt windows past vvvv's dialog
// navigation (WM_GETDLGCODE).
void installDialogCodeFilter(QGuiApplication& app);
#else
// Before the QGuiApplication: Mesa's D3D12 driver as the default under WSLg (probed, with fallbacks), and a clear
// message when there is no display server.
void applyWslGpuDefault();
// After the QGuiApplication: registers the bundled DejaVu Sans as the app font when the system has no font at all.
void ensureFonts();
#endif

} // namespace vlqt::platform
