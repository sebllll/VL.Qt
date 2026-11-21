#include "app/render_gate.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QEvent>
#include <QtQuick/QQuickWindow>

#include <algorithm>

namespace vlqt {

RenderGate::RenderGate(QQuickWindow* window) : QObject(window), m_window(window)
{
    s_gates.push_back(this);
    // Every render inside the pump counts, also one started by an Expose (basic loop renders it directly).
    connect(window, &QQuickWindow::beforeSynchronizing, this, [this] {
        m_everRendered = true;
        if (s_inPump && !m_blocked)
            m_rendered = true;
    }, Qt::DirectConnection);
}

RenderGate::~RenderGate()
{
    s_gates.erase(std::remove(s_gates.begin(), s_gates.end(), this), s_gates.end());
}

void RenderGate::beginPump(bool all, RenderGate* const* active, int count)
{
    s_inPump = true;
    for (RenderGate* g : s_gates)
        g->m_blocked = !all;
    for (int i = 0; i < count; i++)
        active[i]->m_blocked = false;
    for (RenderGate* g : s_gates)
        if (!g->m_blocked)
            g->m_rendered = false;
}

void RenderGate::endPump()
{
    for (RenderGate* g : s_gates) {
        if (!g->m_blocked && g->m_pending && !g->m_rendered) {
            QEvent request(QEvent::UpdateRequest);
            QCoreApplication::sendEvent(g->m_window, &request);
        }
        g->m_blocked = false;
    }
    s_inPump = false;
}

bool RenderGate::eventFilter(QObject* o, QEvent* e)
{
    if (e->type() == QEvent::UpdateRequest) {
        if (!s_inPump || m_blocked || m_rendered) {
            m_pending = true;
            return true;
        }
        m_pending = false;
    } else if (e->type() == QEvent::Expose && m_window->isExposed()
               && (s_inPump ? m_blocked || m_rendered : s_delivering || !m_everRendered)) {
        // Re-expose after this pump's render (XWayland sends two Expose on show) and the initial Expose outside
        // the pump (Windows: on show) render once in the pump instead. Later Expose outside the pump
        // (live resize in Windows' modal size loop) still renders directly, except during the input delivery of
        // the frame start; un-expose always passes.
        m_pending = true;
        return true;
    }
    return QObject::eventFilter(o, e);
}

} // namespace vlqt
