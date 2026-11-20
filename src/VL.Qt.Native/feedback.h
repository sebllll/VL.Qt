#pragma once

// Clear off: the feedback layer keeps the previous frame of a scene (window or offscreen scene) and draws the
// new frame over it. Same code for both targets: the previous frame is part of the scene graph, so neither the swap
// chain (flip model discards the back buffer) nor the render target of a QtTexture has to keep its contents.
//
//   contentItem
//   ├── source   QQuickShaderEffectSource of the root: recursive, live, hideSource (the root is drawn only into it)
//   ├── root     the user scene
//   │   ├── view   draws source's previous texture and the fade fill over it, z below every element
//   │   └── elements ...
//   └── debug / perf overlays, snapshot frame sources (outside the root: never accumulated)
//
// Per frame the source renders the root (view = previous frame, then the elements over it) into its other texture,
// and the window draws the source over the clear colour. The texture starts transparent, so the first frame after
// switching off (and after a resize, which recreates the texture) starts from the clear colour; premultiplied "over"
// makes "transparent texture over the clear colour" equal to "clear colour in the texture".
//
// Fade: with 0 < alpha of the clear colour, the view draws a full-size fill with the clear colour over the
// previous frame (premultiplied "over": out = C·a + prev·(1 − a)), so trails fade toward the clear colour; alpha 0
// draws no fill (pure accumulation). The window then clears to the clear colour made opaque (Scene::applyClearColor):
// the texture's alpha only grows toward 1 under the fill, and "texture over opaque C" keeps every pixel's colour
// exactly where the texture is not opaque yet (first frames: the background is C, not C·a or C twice), whatever Qt
// does with the alpha of a frame clear.
//
// Attachment: the vlqt::Scene owns the layer and holds the Clear state; the layer's items belong to one QQuickWindow
// (source under its content item, view under Scene::root, size from the window's signals, textures in its QRhi). It
// never moves to another window: Scene::releaseFeedback / restoreFeedback rebuild it from the state when the window
// is replaced.

#include <QtCore/QPointer>
#include <QtGui/QColor>
#include <QtQuick/QQuickItem>

class QQuickShaderEffectSource;
class QQuickWindow;

namespace vlqt {

class FeedbackLayer
{
public:
    /* Builds the layer for window (source under its content item) and root, both sized to the root (Scene sizes the
     * root to the window). The layer texture inherits the sample count of the window's render target (Qt's default,
     * samples 0): edges as with Clear on; Qt resolves a multisampled layer into its sampled texture every frame. */
    FeedbackLayer(QQuickWindow* window, QQuickItem* root);
    ~FeedbackLayer();
    FeedbackLayer(const FeedbackLayer&) = delete;
    FeedbackLayer& operator=(const FeedbackLayer&) = delete;

    /* Colour of the fade fill (straight alpha, as the clear colour); alpha 0 draws no fill. Takes effect at the next
     * frame, the layer stays. */
    void setFill(const QColor& color);

private:
    QPointer<QQuickItem> m_source; // QQuickShaderEffectSource, child of the content item
    QPointer<QQuickItem> m_view; // FeedbackView, child of the root
};

} // namespace vlqt
