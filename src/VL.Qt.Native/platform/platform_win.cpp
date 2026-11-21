#include "platform/platform.h"
#include "app/log.h"

#include <QtCore/QAbstractNativeEventFilter>
#include <QtCore/QByteArray>
#include <QtGui/QGuiApplication>
#include <QtGui/QWindow>

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

void installDialogCodeFilter(QGuiApplication& app)
{
    static DialogCodeFilter dialogCodeFilter;
    app.installNativeEventFilter(&dialogCodeFilter);
}

} // namespace vlqt::platform

// At process exit the CLR is already gone when Qt's static destructors log.
// vlqt.dll detaches before Qt6Core (it depends on it), so cut the managed callback here.
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_DETACH)
        vlqt::log::detach();
    return TRUE;
}
