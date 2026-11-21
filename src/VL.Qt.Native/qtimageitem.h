/* qtimageitem.h - the Image element (images package 04): draws an image resource (image_resource.h).
 *
 * PROP_IMAGE sets the resource id (0 = no image). The item takes an element reference (res::acquire) for the id it
 * shows and for the id it waits for, and drops it on a change or in the destructor. Until the new resource is Ready
 * the old image stays (no empty frame); a replacement of the same id shows up the same way (res::contentEpoch).
 *
 * Textures: one per window and content, shared by every item of that window (cache in qtimageitem.cpp):
 *   - shared device (Windows/D3D11): a QRhiTexture of the window's QRhi created from the resource's native texture
 *     (createFrom, same format and mip flags), no copy. The native texture gets an own reference (AddRef) for as long
 *     as the wrapper lives, so a resource freed or replaced meanwhile never leaves a dangling texture.
 *   - no shared device (Linux, VLQT_SHARED_DEVICE=0) or no QRhi (software backend): createTextureFromImage of the
 *     resource's CPU copy, once per window.
 * Everything runs on the main thread (render loop basic). Textures go in releaseResources/invalidateSceneGraph,
 * before the window's QRhi.
 *
 * Device lost: content of an older device generation (ImageResource::generation) is skipped silently until its
 * replacement is Ready; so is a window that still renders with its old QRhi right after device::rebuild.
 *
 * Fill modes as QML Image (values of Image.fillMode, centered): Stretch, PreserveAspectFit, PreserveAspectCrop and
 * Tile (one image node per tile, at most kMaxTiles). smooth (QQuickItem) picks linear or nearest filtering, linear
 * also uses the mipmaps of a resource loaded with VLQT_IMAGE_LOAD_MIPMAPS. Implicit size = image size.
 */
#pragma once

#include <QtQuick/QQuickItem>

#include <cstdint>
#include <memory>
#include <string>

class QSGTexture;
class QQuickWindow;

namespace vlqt {

namespace res::image {
class ImageResource;
}
struct ImageTexture;

// Texture of an image resource in a window from the shared cache (also used for ShaderEffect samplers);
// nullptr if it cannot be drawn there (device generation, no shared device). Main thread.
std::shared_ptr<ImageTexture> imageTextureFor(QQuickWindow* window, const std::shared_ptr<res::image::ImageResource>& content);
QSGTexture* textureOf(const ImageTexture& t);

class QtImageItem final : public QQuickItem
{
    Q_OBJECT
    Q_PROPERTY(int fillMode READ fillMode WRITE setFillMode NOTIFY fillModeChanged)

public:
    explicit QtImageItem(QQuickItem* parent = nullptr);
    ~QtImageItem() override;

    int fillMode() const { return m_fillMode; }
    void setFillMode(int mode);

    // PROP_IMAGE: false + err for an unknown or released id (the current image stays).
    bool setResource(uint64_t id, std::string& err);

    // Once per pump after res::beginFrame: items whose resource got new content draw it in this pump.
    static void afterResourceFrame();

signals:
    void fillModeChanged();

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
    std::shared_ptr<res::image::ImageResource> m_content;
    std::shared_ptr<ImageTexture> m_texture; // texture of m_content in window()
    int m_fillMode = 0;
};

} // namespace vlqt
