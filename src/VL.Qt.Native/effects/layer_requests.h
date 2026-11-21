/* layer_requests.h - the mipmap requests of the samplers of ShaderItems on the layers of elements.
 *
 * A sampler with a mip filter other than None needs the layer of the sampled element rendered with mipmaps. Several
 * ShaderItems can sample one element, so the layer has mipmaps while any of them asks. Process-wide registry, GUI
 * thread only.
 */
#pragma once

#include <QtQuick/QQuickItem>

namespace vlqt {

class ShaderItem;

class LayerRequests
{
public:
    // Sets the request of owner on the layer of item and applies the union of all requests of that element.
    static void request(QQuickItem* item, const ShaderItem* owner, bool mip);
    // Withdraws the request of owner and applies what is left (the layer goes back to its defaults for the last one).
    static void release(QQuickItem* item, const ShaderItem* owner);
    // Like release for the destruction of owner: the element may be in its own destruction (the Scene deletes in any
    // order), so the layer goes back to its defaults later, on the event loop of the element, which drops the call if
    // the element is gone.
    static void releaseQueued(QQuickItem* item, const ShaderItem* owner);
};

} // namespace vlqt
