#include "scene/clear_state.h"
#include "scene/previous_frame_layer.h"

#include <QtCore/QLoggingCategory>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>

Q_LOGGING_CATEGORY(lcPreviousFrame, "vlqt.previousframe")

namespace vlqt {

ClearState::ClearState(const QColor& color) : clearColor(color) {}

ClearState::~ClearState() = default;

void ClearState::setClear(bool clear, QQuickWindow* window, QQuickItem* root)
{
    if (clear == clearFrames)
        return;
    clearFrames = clear;
    release(window);
    restore(window, root);
}

void ClearState::release(QQuickWindow* window)
{
    layer.reset();
    applyColor(window);
    window->update();
}

void ClearState::restore(QQuickWindow* window, QQuickItem* root)
{
    if (!clearFrames && !layer) {
        // The software backend draws no ShaderEffectSource (the layer would show a black frame): Clear off is ignored
        // there and the scene renders as with Clear on.
        QSGRendererInterface* ri = window->rendererInterface();
        if (ri && ri->graphicsApi() == QSGRendererInterface::Software) {
            if (!softwareWarned) {
                softwareWarned = true;
                qCWarning(lcPreviousFrame, "Clear off needs a QRhi scenegraph; ignored on the Qt Quick software backend");
            }
        } else {
            layer = std::make_unique<PreviousFrameLayer>(window, root);
        }
    }
    // Also for Clear on: after a window swap the new window takes the clear colour from here.
    applyColor(window);
    window->update();
}

void ClearState::setColor(const QColor& color, QQuickWindow* window)
{
    clearColor = color;
    applyColor(window);
}

void ClearState::applyColor(QQuickWindow* window)
{
    const float alpha = clearColor.alphaF();
    if (layer)
        layer->setFill(alpha > 0 ? clearColor : QColor(Qt::transparent));
    // Fade (previous_frame_layer.h): under the layer the window clears to the colour made opaque.
    QColor frame = clearColor;
    if (layer && alpha > 0 && alpha < 1)
        frame.setAlphaF(1);
    window->setColor(frame); // no-op when unchanged
}

void ClearState::destroyLayer() { layer.reset(); }

} // namespace vlqt
