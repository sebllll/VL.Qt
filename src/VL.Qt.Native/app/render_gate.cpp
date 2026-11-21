#include "app/render_gate.h"
#include "diagnostics/perf.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QEvent>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
// Dirty item list of a window (change count): private API, check on Qt updates.
#include <QtQuick/private/qquickitem_p.h>
#include <QtQuick/private/qquickwindow_p.h>
// Touch moves held back until the next render (vlqt_app_deliver_input): private API, check on Qt updates.
#include <QtQuick/private/qquickdeliveryagent_p_p.h>

#include <algorithm>
#include <utility>

namespace vlqt {

UpdateGate::UpdateGate(QQuickWindow* window) : QObject(window), m_window(window)
{
    s_gates.push_back(this);
    // Every render inside the pump counts, also one started by an Expose (basic loop renders it directly).
    connect(window, &QQuickWindow::beforeSynchronizing, this, [this] {
        m_everRendered = true;
        if (s_inPump && !m_blocked)
            m_rendered = true;
        const bool dirty = sceneDirty(); // always: keeps the content children up to date
        if (std::exchange(m_changed, false) || dirty)
            ++m_changeCount;
    }, Qt::DirectConnection);
}

UpdateGate::~UpdateGate()
{
    s_gates.erase(std::remove(s_gates.begin(), s_gates.end(), this), s_gates.end());
}

void UpdateGate::beginPump(bool all, UpdateGate* const* active, int count)
{
    s_inPump = true;
    for (UpdateGate* g : s_gates)
        g->m_blocked = !all;
    for (int i = 0; i < count; i++)
        active[i]->m_blocked = false;
    for (UpdateGate* g : s_gates)
        if (!g->m_blocked)
            g->m_rendered = false;
}

void UpdateGate::endPump()
{
    for (UpdateGate* g : s_gates) {
        if (!g->m_blocked && g->m_pending && !g->m_rendered) {
            QEvent request(QEvent::UpdateRequest);
            QCoreApplication::sendEvent(g->m_window, &request);
        }
        g->m_blocked = false;
    }
    s_inPump = false;
}

void UpdateGate::deliverDelayedTouch()
{
    for (size_t i = 0; i < s_gates.size(); i++) {
        QQuickDeliveryAgentPrivate* agent = QQuickWindowPrivate::get(s_gates[i]->m_window)->deliveryAgentPrivate();
        if (agent && agent->delayedTouch)
            agent->deliverDelayedTouchEvent();
    }
}

bool UpdateGate::eventFilter(QObject* o, QEvent* e)
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

// The window's dirty items (Qt syncs them right after beforeSynchronizing) hold a change other than the footprint
// of a snapshot (grabs must not count, or an open tooltip would grab a static scene forever): grabToImage
// and a snapshot's frame source ref the item when they start and deref it when they are done (EffectReference on
// the item, ChildrenStackingChanged on its parent), and the frame source comes and goes under the content item.
// A real change in the same frame as such a ref (a reorder of the grabbed item's siblings) is missed until the
// next one. Every item update() (elements, reconciler, video, images), polish result, resize or hover style is a
// dirty item.
bool UpdateGate::sceneDirty()
{
    using D = QQuickItemPrivate;
    constexpr quint32 grabRef = D::EffectReference | D::ChildrenStackingChanged;
    constexpr quint32 children = D::ChildrenChanged | grabRef;
    QQuickItem* content = m_window->contentItem();
    bool changed = false, contentDirty = false;
    for (QQuickItem* i = QQuickWindowPrivate::get(m_window)->dirtyItemList; i; i = D::get(i)->nextDirtyItem) {
        const quint32 bits = D::get(i)->dirtyAttributes;
        if (i == content) {
            contentDirty = true;
            changed |= (bits & ~children) != 0; // e.g. Size on resize
        } else if (!changed && !isGrabSource(i) && !inPerfOverlay(i) && (bits & ~D::EffectReference)
                   && ((bits & ~grabRef) || !hasRefChild(i))) {
            changed = true;
        }
    }
    // Children of the content item: a frame source is no change, any other child added or removed is.
    const bool contentChanged = contentDirty && contentChildrenChanged();
    return contentChanged || changed;
}

// The hidden ShaderEffectSource of a snapshot (snapshot_resource.cpp frameTarget): the only one directly under the
// content item (Group frame proxies of effects live under the scene root).
bool UpdateGate::isGrabSource(QQuickItem* i) const
{
    return i->parentItem() == m_window->contentItem() && !i->isVisible() && i->inherits("QQuickShaderEffectSource");
}

// The F5 overlay (diagnostics/perf_monitor.cpp, objectName perfOverlayName under the content item) is no scene change: its graphs
// repaint every frame, and the tooltip's snapshot rate is measured with the overlay open.
bool UpdateGate::inPerfOverlay(QQuickItem* i) const
{
    QQuickItem* content = m_window->contentItem();
    while (i->parentItem() && i->parentItem() != content)
        i = i->parentItem();
    return isPerfOverlay(i);
}

bool UpdateGate::isPerfOverlay(QQuickItem* i)
{
    return i->objectName() == QLatin1String(vlqt::perfOverlayName);
}

bool UpdateGate::hasRefChild(QQuickItem* parent)
{
    for (QQuickItem* c : std::as_const(QQuickItemPrivate::get(parent)->childItems))
        if (QQuickItemPrivate::get(c)->dirtyAttributes & QQuickItemPrivate::EffectReference)
            return true;
    return false;
}

bool UpdateGate::contentChildrenChanged()
{
    s_children.clear();
    for (QQuickItem* c : std::as_const(QQuickItemPrivate::get(m_window->contentItem())->childItems))
        if (!isGrabSource(c) && !isPerfOverlay(c))
            s_children.push_back(c);
    if (s_children == m_contentChildren)
        return false;
    m_contentChildren.swap(s_children);
    return true;
}

} // namespace vlqt
