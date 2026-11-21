#include "window/window_filters.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QEvent>
#include <QtGui/QKeyEvent>

namespace vlqt {

bool CloseFilter::eventFilter(QObject* o, QEvent* e)
{
    if (e->type() == QEvent::Close) {
        e->ignore();
        requested = true;
        return true;
    }
    return QObject::eventFilter(o, e);
}

bool FullscreenKeys::eventFilter(QObject* o, QEvent* e)
{
    if (e->type() == QEvent::KeyPress) {
        auto* ke = static_cast<QKeyEvent*>(e);
        const auto mods = ke->modifiers() & ~Qt::KeypadModifier;
        const bool enter = ke->key() == Qt::Key_Return || ke->key() == Qt::Key_Enter;
        if (!ke->isAutoRepeat() && ((ke->key() == Qt::Key_F11 && mods == Qt::NoModifier)
                                    || (enter && mods == Qt::AltModifier))) {
            const unsigned fs = VLQT_WINDOW_MODE_FULLSCREEN;
            vlqt_window_set_mode(m_window, fs, (vlqt_window_get_mode(m_window) & fs) ^ fs);
            return true;
        }
    }
    return QObject::eventFilter(o, e);
}

} // namespace vlqt
