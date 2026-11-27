// Focus release of windows and offscreen scenes: a press outside the focused element releases the focus, Esc in a
// TextField or SpinBox element too. Qt GUI thread.
#pragma once

#include <QtCore/QObject>

class QQuickItem;
class QQuickWindow;

namespace vlqt {

// Qt Quick never takes the focus away by itself, so an input element kept it until another one was clicked.
// The focused element is the user QML element around the focus item (one owner as a whole), else its Control.
//   Esc        in a TextField or SpinBox element: discardEdit (elements/element_types.h) discards the running edit and
//              the focus is released, the key is consumed. Every other element, user QML included, receives Esc.
//   Press      (mouse, touch, tablet) outside the focused element releases the focus *before* the press is delivered:
//              a running edit is committed like on any focus loss, the press itself is delivered unchanged (a Click
//              handler still clicks, another input takes the focus on its press as before). Nothing is consumed and
//              no grab is taken, so the input handlers see the same event sequence as without the filter.
//   Enter      is not touched: the control commits and keeps the focus.
// Deciding before delivery (hit test against the focused element) instead of after it needs no second pass and no
// queued check; a passive TapHandler on the root would only see presses that reach the root and not those taken by an
// element's handler, and it would add a handler to every pointer grab.
class FocusRelease : public QObject
{
public:
    FocusRelease(QQuickWindow* window);
protected:
    bool eventFilter(QObject* o, QEvent* e) override;
private:
    QQuickItem* focusOwner() const;
    void release();
    QQuickWindow* m_window;
};

} // namespace vlqt
