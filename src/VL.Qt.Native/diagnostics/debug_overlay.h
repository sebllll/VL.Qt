/* debug_overlay.h - debug overlay of a window.
 *
 * One item per window, child of contentItem next to the user root (like the perf overlay), so it takes no part in
 * layout, childrenRect, implicit/Measured Size, REPARENT indices or effect layers. enabled=false, no mouse buttons,
 * contains() false: hit-testing is unchanged. Drawn in updatePaintNode (sync, after polish: positions of Row/Column
 * are current) from the items of the scene, mapped per point with mapToScene (rotated/scaled items stay exact).
 * Always on top of the scene, not clipped by any parent; rendered wherever the scene is (window, grab, texture).
 *
 * Shown for every element with VLQT_PROPERTY_DEBUG and for all elements while vlqt_scene_set_debug is on. Without
 * either the overlay is hidden and costs nothing per frame (no update, no geometry); it is created on first use.
 * While shown it repaints after an apply and whenever an item of the scene waits for its sync at afterAnimating (after
 * polish), so it follows a Row that re-lays out or a Text that changes its size without a command. In a window that
 * costs one more render after each frame that changed the scene (the repaint is requested after Qt's polish). Its
 * objectName is overlayObjectName (app/scene_change.h): its repaints are no scene change.
 *
 * Legend (no labels; one color per meaning, the line style carries the mode):
 *   cyan frame      = actual size (itemBounds: SIZE, or the Auto Size result); solid = fixed SIZE, dashed = Auto Size on
 *   orange frame    = implicit size, i.e. what Auto Size gives (Group: extent of children + origin, may start negative);
 *                     only where it differs from the actual size (Auto Size on: never), types with AUTO_SIZE and Text
 *   blue dotted     = children extent (Group/Row/Column: visible, non-empty children incl. TRANSFORM), only where it
 *                     differs from the frame and the implicit size
 *   translucent red = overflow: the part of the children extent outside the frame
 *   translucent green = padding (Row/Column/Text), hatched violet = spacing gaps (Row/Column); both inside the frame
 *   magenta cross   = origin (item (0, 0)), yellow diamond = anchor point
 *   Text: baseline green, ascent green dashed, descent red dashed.
 * The frame is the reference: padding and spacing are drawn against it, so a content larger than SIZE (Auto Size off)
 * shows as red overflow instead of padding around the content. Only triangles; the software backend rasterizes the
 * same list (paintSoftware), so both paths show the identical overlay. */
#pragma once

#include "diagnostics/overlay_geometry.h"

#include <QtCore/QtTypes>
#include <QtGui/QImage>
#include <QtQuick/QQuickItem>

#include <vector>

namespace vlqt {

class Scene;
struct ElementEntry;

class DebugOverlay : public QQuickItem
{
public:
    DebugOverlay(QQuickItem* contentItem, const Scene& scene);

    /* Shows/hides the overlay; while shown, refresh() requests a repaint. */
    void setActive(bool on);
    bool active() const { return m_active; }
    /* Repaint with the current scene (after commands were applied). No-op while inactive. */
    void refresh();
    /* Draw every element (window-wide switch) instead of only those with VLQT_PROPERTY_DEBUG. */
    void setAll(bool on) { m_all = on; }

    bool contains(const QPointF&) const override { return false; }

protected:
    QSGNode* updatePaintNode(QSGNode* old, UpdatePaintNodeData*) override;
    void itemChange(ItemChange change, const ItemChangeData& data) override;

private:
    void follow(QQuickWindow* window);
    void build();
    void drawNode(const ElementEntry& node);
    QSGNode* paintSoftware(QSGNode* old);

    const Scene& m_scene;
    bool m_active = false;
    bool m_all = false;
    QMetaObject::Connection m_follow; // afterAnimating of the window while active
    std::vector<OverlayVertex> m_verts; // triangles, premultiplied colors; rebuilt per paint (render thread, GUI blocked)
    std::vector<OverlayVertex> m_lines; // line layer while building; appended after the fills (areas of all nodes) in m_verts
    qreal m_dpr = 1;             // device pixel ratio of the current build (line snapping)
    QImage m_image;              // software backend
};

} // namespace vlqt
