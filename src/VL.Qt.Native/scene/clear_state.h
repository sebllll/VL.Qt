/* scene/clear_state.h - the Clear pin and the clear colour of a scene, and the previous-frame layer built from them. */
#pragma once

#include <QtGui/QColor>

#include <memory>

class QQuickItem;
class QQuickWindow;

namespace vlqt {

class PreviousFrameLayer;

/* Clear (scene/previous_frame_layer.h): false keeps the previous frame through the previous-frame layer, which exists only
 * while clear is false; the first frame after switching off starts from the clear colour. Set by VLQT_OP_SET_CLEAR, kept
 * by RESET. The layer is derived state: clearFrames is the state, the layer is built from it for the current window.
 * Owned by the Scene, which passes its current window and root. */
class ClearState
{
public:
    explicit ClearState(const QColor& color);
    ~ClearState();
    ClearState(const ClearState&) = delete;
    ClearState& operator=(const ClearState&) = delete;

    void setClear(bool clear, QQuickWindow* window, QQuickItem* root);
    bool clear() const { return clearFrames; }
    /* Clear colour (VLQT_OP_SET_CLEAR_COLOR, straight alpha; kept by RESET and across a window swap). With Clear on, or
     * Clear off and alpha 0 or 1, the window clears to it. With Clear off its alpha is the fade strength: the
     * layer draws it over the previous frame (alpha > 0), and for 0 < alpha < 1 the window clears to it opaque
     * (previous_frame_layer.h "Fade"). Runtime changes keep the layer. */
    void setColor(const QColor& color, QQuickWindow* window);
    /* Before the QQuickWindow is replaced (MSAA change): drops the layer (its source is a child of the old
     * content item, its textures belong to the old window's QRhi), keeps the state. After the root moved to the new
     * window: rebuilds the layer from the state; the first frame starts from the clear colour, as after a resize. */
    void release(QQuickWindow* window);
    void restore(QQuickWindow* window, QQuickItem* root);
    /* Deletes the layer without touching the window (~Scene: the layer's view is a child of the root). */
    void destroyLayer();

private:
    void applyColor(QQuickWindow* window); // window clear and layer fill from clearColor and the layer
    std::unique_ptr<PreviousFrameLayer> layer; // Clear off only
    bool clearFrames = true; // Clear pin state
    QColor clearColor; // VLQT_OP_SET_CLEAR_COLOR; initially the colour the window was created with
    bool softwareWarned = false; // Clear off ignored on the software backend, logged once per scene
};

} // namespace vlqt
