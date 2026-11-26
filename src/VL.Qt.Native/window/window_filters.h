// Event filters of an on-screen window: the close request and the fullscreen keys. Qt GUI thread.
#pragma once

#include <QtCore/QObject>

struct vlqt_scene;

namespace vlqt {

// Swallows the X-button close so the host decides (hide / close document), like VL.Skia's FormClosing.
class CloseFilter : public QObject
{
public:
    using QObject::QObject;
    bool requested = false;
protected:
    bool eventFilter(QObject* o, QEvent* e) override;
};

// F11 and Alt+Enter toggle fullscreen like VL.Skia's renderer: the same path as the Fullscreen pin
// (vlqt_window_set_mode). The pin only acts on change, so a toggle by key stays until the pin changes.
class FullscreenKeys : public QObject
{
public:
    FullscreenKeys(QObject* parent, vlqt_scene* window) : QObject(parent), m_window(window) {}
protected:
    bool eventFilter(QObject* o, QEvent* e) override;
private:
    vlqt_scene* m_window;
};

} // namespace vlqt
