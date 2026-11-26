/* qt_image_item.h - the Image element: draws an image resource (image_resource.h).
 *
 * PROP_IMAGE sets the resource id (0 = no image). The item takes an element reference (res::acquire) for the id it
 * shows and for the id it waits for, and drops it on a change or in the destructor; until the new resource is Ready the
 * old image stays. Textures come from resources/image_texture_cache.h, one per window, QRhi and content.
 * Threads: the item lives on the main thread; updatePaintNode and invalidateSceneGraph run on the window's render thread
 * (the main thread waits in sync). A texture belongs to the thread that made it: the item lets it go in
 * updatePaintNode and invalidateSceneGraph, from the main thread through render_thread::retire, always before the
 * window's QRhi. Device lost: the old image is not drawn until its replacement is Ready.
 * Handles, wrappers and the draw rule: docs/design/resources.md, "Images".
 *
 * Fill modes (vlqt_fill_mode: Stretch, Fit, Crop, Tile; centered like QML Image); Tile makes one image node per tile, at
 * most kMaxTiles. smooth (QQuickItem) picks linear or nearest filtering, linear also uses the mipmaps of a resource
 * loaded with VLQT_IMAGE_LOAD_MIPMAPS. Implicit size = image size.
 */
#pragma once

#include "protocol.h"

#include <QtQuick/QQuickItem>

#include <cstdint>
#include <memory>
#include <string>

class QQuickWindow;

namespace vlqt {

namespace res::image {
class ImageContent;
}
struct ImageTexture;

class QtImageItem final : public QQuickItem
{
    Q_OBJECT

public:
    explicit QtImageItem(QQuickItem* parent = nullptr);
    ~QtImageItem() override;

    void setFillMode(vlqt_fill_mode mode);

    // PROP_IMAGE: false + err for an unknown or released id (the current image stays).
    bool setResource(uint64_t id, std::string& err);

    // Once per pump after res::beginFrame: only the items whose resource got new content (res::changedThisFrame)
    // take it and draw it in this pump.
    static void afterResourceFrame();

public slots:
    // Called by Qt Quick on window teardown (cleanupNodesOnShutdown) while the QRhi still exists.
    void invalidateSceneGraph();

protected:
    QSGNode* updatePaintNode(QSGNode* old, UpdatePaintNodeData*) override;
    void releaseResources() override;
    void geometryChange(const QRectF& newGeometry, const QRectF& oldGeometry) override;

private:
    // Takes the Ready content of m_wantId if it differs from the shown one.
    void advance();

    uint64_t m_wantId = 0;  // last PROP_IMAGE (element reference held)
    uint64_t m_shownId = 0; // id of m_content (element reference held; == m_wantId once that is Ready)
    std::shared_ptr<res::image::ImageContent> m_content;
    std::shared_ptr<ImageTexture> m_texture; // texture of m_content in window()
    vlqt_fill_mode m_fillMode = VLQT_FILL_MODE_STRETCH;
};

} // namespace vlqt
