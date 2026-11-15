#pragma once

// Clear off (#117): the feedback layer keeps the previous frame of a scene (window or offscreen scene) and draws the
// new frame over it. Same code for both targets: the previous frame is part of the scene graph, so neither the swap
// chain (flip model discards the back buffer) nor the render target of a QtTexture has to keep its contents.
//
//   contentItem
//   ├── source   QQuickShaderEffectSource of the root: recursive, live, hideSource (the root is drawn only into it)
//   ├── root     the user scene
//   │   ├── view   draws source's previous texture, z below every element
//   │   └── elements ...
//   └── debug / perf overlays, snapshot frame sources (outside the root: never accumulated)
//
// Per frame the source renders the root (view = previous frame, then the elements over it) into its other texture,
// and the window draws the source over the clear colour. The texture starts transparent, so the first frame after
// switching off (and after a resize, which recreates the texture) starts from the clear colour; premultiplied "over"
// makes "transparent texture over the clear colour" equal to "clear colour in the texture".

#include <QtCore/QPointer>
#include <QtQuick/QQuickItem>

class QQuickShaderEffectSource;
class QQuickWindow;

namespace vlqt {

class FeedbackLayer
{
public:
    FeedbackLayer(QQuickWindow* window, QQuickItem* root);
    ~FeedbackLayer();
    FeedbackLayer(const FeedbackLayer&) = delete;
    FeedbackLayer& operator=(const FeedbackLayer&) = delete;

private:
    QPointer<QQuickItem> m_source; // QQuickShaderEffectSource, child of the content item
    QPointer<QQuickItem> m_view; // FeedbackView, child of the root
};

} // namespace vlqt
