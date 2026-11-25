// The Qt Quick software backend (QT_QUICK_BACKEND=software, the fallback without a GPU) draws no custom geometry. The
// overlay items (PerfGraph, PerfTimeline, DebugOverlay) therefore paint their lines and triangles into one image,
// shown by one image node. This is everything of that path except the QPainter body of each item.
#pragma once

#include <QtGui/QImage>
#include <QtGui/QPainter>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGImageNode>
#include <QtQuick/QSGRendererInterface>

#include <algorithm>

namespace vlqt {

// true if the window renders with the Qt Quick software backend (no QRhi). sceneGraphBackend() already tells before the
// window has a renderer interface (an item that is set up before the scene graph starts).
inline bool isSoftwareBackend(QQuickWindow* window)
{
    if (QQuickWindow::sceneGraphBackend() == QLatin1String("software"))
        return true;
    const QSGRendererInterface* ri = window ? window->rendererInterface() : nullptr;
    return ri && ri->graphicsApi() == QSGRendererInterface::Software;
}

// Paints the item into `cache` (resized to the item in device pixels, cleared transparent) with paint(QPainter&), which
// draws in item coordinates (the painter is scaled by the device pixel ratio), and returns the image node that shows
// it. `old` is the node of the last call (null at the first). The texture of the node is replaced on every call.
template <class Paint>
QSGNode* paintToImageNode(QQuickItem* item, QSGNode* old, QImage& cache, Paint paint)
{
    QQuickWindow* window = item->window();
    auto* node = static_cast<QSGImageNode*>(old);
    if (!node) {
        node = window->createImageNode();
        node->setOwnsTexture(true);
    }
    const qreal dpr = window->effectiveDevicePixelRatio();
    const QSize px(std::max(1, qRound(item->width() * dpr)), std::max(1, qRound(item->height() * dpr)));
    if (cache.size() != px)
        cache = QImage(px, QImage::Format_ARGB32_Premultiplied);
    cache.fill(Qt::transparent);
    {
        QPainter painter(&cache);
        painter.scale(dpr, dpr);
        paint(painter);
    }
    node->setTexture(window->createTextureFromImage(cache));
    node->setRect(QRectF(0, 0, item->width(), item->height()));
    node->markDirty(QSGNode::DirtyMaterial);
    return node;
}

} // namespace vlqt
