#include "platform/platform.h"
#include "app/log.h"

#include <QtCore/QAbstractNativeEventFilter>
#include <QtCore/QByteArray>
#include <QtCore/QFileInfo>
#include <QtCore/QString>
#include <QtGui/QGuiApplication>
#include <QtGui/QWindow>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>

#include <windows.h>

namespace {

// vvvv's main thread runs a WinForms message loop. Its PreTranslateMessage hands every key message for a
// non-WinForms HWND (our Qt windows) to IsDialogMessage(root), which takes Enter, Tab, Escape and the arrow keys for
// dialog navigation unless the window asks for them via WM_GETDLGCODE. Qt answers 0 (DefWindowProc), so Enter never
// reached a TextField or SpinBox (commit only on focus loss). Qt does its own focus handling: want all keys.
class DialogCodeFilter : public QAbstractNativeEventFilter
{
public:
    bool nativeEventFilter(const QByteArray&, void* message, qintptr* result) override
    {
        const auto* msg = static_cast<const MSG*>(message);
        if (msg->message != WM_GETDLGCODE)
            return false;
        for (QWindow* w : QGuiApplication::topLevelWindows()) {
            if (w->handle() && reinterpret_cast<HWND>(w->winId()) == msg->hwnd) {
                *result = DLGC_WANTALLKEYS | DLGC_WANTARROWS | DLGC_WANTTAB | DLGC_WANTCHARS;
                return true;
            }
        }
        return false;
    }
};

} // namespace

namespace vlqt::platform {

void prepareBeforeApp()
{
    QQuickWindow::setGraphicsApi(QSGRendererInterface::Direct3D11);
}

void initAfterApp(QGuiApplication& app)
{
    // A static: its destructor removes it from the app at DLL detach.
    static DialogCodeFilter dialogCodeFilter;
    app.installNativeEventFilter(&dialogCodeFilter);
}

// The Win32 process environment, not the CRT copy (qgetenv): the .NET host sets the variable at run time.
bool testSyntheticInputOnly()
{
    wchar_t v[4] = {};
    return GetEnvironmentVariableW(L"VLQT_TEST_SYNTHETIC_INPUT", v, 4) == 1 && v[0] == L'1';
}

// The D3D11 swap chain presents every frame.
void endFramesLikePresent(QQuickWindow&)
{
}

// QWindow::setGeometry does not work right after setFlags (Qt still has the old frame margins and a stale cached
// geometry; frame on: the new style only takes effect later, the outer rect stays = old client rect). So the outer
// rect for the unchanged client rect is computed from the new style (AdjustWindowRectExForDpi, includes the invisible
// resize borders) and set with SWP_FRAMECHANGED; Qt follows from WM_NCCALCSIZE/WM_MOVE/WM_SIZE.
void setFrameless(QQuickWindow& window, bool on, bool keepClientRect)
{
    const Qt::WindowFlags flags = on ? (window.flags() | Qt::FramelessWindowHint) : (window.flags() & ~Qt::FramelessWindowHint);
    if (!keepClientRect) {
        window.setFlags(flags);
        return;
    }
    const HWND hwnd = reinterpret_cast<HWND>(window.winId());
    RECT c {};
    GetClientRect(hwnd, &c);
    MapWindowPoints(hwnd, nullptr, reinterpret_cast<POINT*>(&c), 2);
    window.setFlags(flags);
    RECT r = c;
    const DWORD style = DWORD(GetWindowLongPtrW(hwnd, GWL_STYLE)), exStyle = DWORD(GetWindowLongPtrW(hwnd, GWL_EXSTYLE));
    AdjustWindowRectExForDpi(&r, style, FALSE, exStyle, GetDpiForWindow(hwnd));
    SetWindowPos(hwnd, nullptr, r.left, r.top, r.right - r.left, r.bottom - r.top,
        SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
}

// NTFS paths are case-insensitive.
QString pathKey(const QFileInfo& file)
{
    return file.canonicalFilePath().toLower();
}

} // namespace vlqt::platform

// Unload hook. At process exit (reserved != null) the CLR is already gone when Qt's static destructors log, and
// vlqt.dll detaches before Qt6Core (it depends on it): cut the managed callback, the message handler stays (the DLL
// stays mapped). FreeLibrary (reserved == null) also removes the message handler, which would point into unmapped code.
// Unloading QtNative is not supported otherwise (docs/architecture.md, Lifetime).
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_DETACH)
        vlqt::log::detach(reserved == nullptr);
    return TRUE;
}
