#include "app/scene_change.h"

#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
// Dirty item list of a window: private API, check on Qt updates.
#include <QtQuick/private/qquickitem_p.h>
#include <QtQuick/private/qquickwindow_p.h>

#include <utility>

namespace vlqt {

SceneChangeCounter::SceneChangeCounter(QQuickWindow* window) : QObject(window), m_window(window)
{
    connect(window, &QQuickWindow::beforeSynchronizing, this, [this] {
        const bool dirty = sceneDirty(); // always: keeps the content children up to date
        if (std::exchange(m_changed, false) || dirty)
            ++m_count;
    }, Qt::DirectConnection);
}

// The window's dirty items (Qt syncs them right after beforeSynchronizing) hold a change other than the footprint
// of a snapshot (grabs must not count, or an open tooltip would grab a static scene forever): grabToImage
// and a snapshot's frame source ref the item when they start and deref it when they are done (EffectReference on
// the item, ChildrenStackingChanged on its parent), and the frame source comes and goes under the content item.
// A real change in the same frame as such a ref (a reorder of the grabbed item's siblings) is missed until the
// next one. Every item update() (elements, reconciler, video, images), polish result, resize or hover style is a
// dirty item.
bool SceneChangeCounter::sceneDirty()
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
        } else if (!changed && !isGrabSource(i) && !inOverlay(i, content) && (bits & ~D::EffectReference)
                   && ((bits & ~grabRef) || !hasRefChild(i))) {
            changed = true;
        }
    }
    // Children of the content item: a frame source or an overlay is no change, any other child added or removed is.
    const bool contentChanged = contentDirty && contentChildrenChanged();
    return contentChanged || changed;
}

bool SceneChangeCounter::itemsDirty(QQuickWindow* window)
{
    const QQuickItem* content = window->contentItem();
    for (QQuickItem* i = QQuickWindowPrivate::get(window)->dirtyItemList; i; i = QQuickItemPrivate::get(i)->nextDirtyItem)
        if (i == content || !inOverlay(i, content))
            return true;
    return false;
}

// The hidden ShaderEffectSource of a snapshot (snapshot_resource.cpp frameTarget): the only one directly under the
// content item (Group frame proxies of effects live under the scene root).
bool SceneChangeCounter::isGrabSource(QQuickItem* i) const
{
    return i->parentItem() == m_window->contentItem() && !i->isVisible() && i->inherits("QQuickShaderEffectSource");
}

// The perf overlay's graphs repaint every frame and the tooltip's snapshot rate is measured with it open; the debug
// overlay repaints whenever the scene does (diagnostics/debug_overlay.cpp).
bool SceneChangeCounter::inOverlay(QQuickItem* i, const QQuickItem* content)
{
    while (i->parentItem() && i->parentItem() != content)
        i = i->parentItem();
    return isOverlay(i);
}

bool SceneChangeCounter::isOverlay(const QQuickItem* i)
{
    return i->objectName() == QLatin1String(overlayObjectName);
}

bool SceneChangeCounter::hasRefChild(QQuickItem* parent)
{
    for (QQuickItem* c : std::as_const(QQuickItemPrivate::get(parent)->childItems))
        if (QQuickItemPrivate::get(c)->dirtyAttributes & QQuickItemPrivate::EffectReference)
            return true;
    return false;
}

bool SceneChangeCounter::contentChildrenChanged()
{
    m_children.clear();
    for (QQuickItem* c : std::as_const(QQuickItemPrivate::get(m_window->contentItem())->childItems))
        if (!isGrabSource(c) && !isOverlay(c))
            m_children.push_back(c);
    if (m_children == m_contentChildren)
        return false;
    m_contentChildren.swap(m_children);
    return true;
}

} // namespace vlqt
