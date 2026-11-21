/* shader_item.h - the ShaderEffect element (effects package 03, protocol.h "ShaderEffect").
 *
 * Own QQuickItem with a QSGGeometryNode + QSGMaterial/QSGMaterialShader instead of QML's ShaderEffect: the uniforms
 * of SET_UNIFORMS are mapped by name through the reflection of the shaders (QShaderDescription of the .qsb).
 *
 * Shaders: FRAGMENT_SHADER / VERTEX_SHADER are shader resource ids (shader_resource.h). The item holds an element
 * reference on the shown id and on the one it waits for; the old shader stays until the new one is Ready. If the
 * wanted id (or a replacement on the shown id) fails, the slot keeps drawing its last Ready content (live
 * editing never goes black; the content pointer stays, the reference on shown too); a Failed shader without an
 * earlier Ready one in that slot draws nothing (its error goes by RESOURCE_EVENT_ERROR). Without a vertex shader Qt's default
 * ShaderEffect vertex shader (embedded in Qt Quick) is used. Pipelines: one QSGMaterialType per shader pair (content
 * compared), so the renderer's shader cache never mixes programs.
 *
 * Uniform block: std140, the members of both stages merged by name (same offset and type required, else an error
 * line). qt_Matrix and qt_Opacity are written by the material shader from the render state, everything else from
 * the uniform table (values by name, kept across shader changes; members without a value are 0). Color is
 * premultiplied, a mat4 goes as sent (row-major Stride matrix = transposed Qt matrix in column-major memory).
 *
 * Samplers (combinedImageSamplers of both stages, bindings 1..15): "source" = Input (SOURCE, layer of the item, the
 * texture provider of QQuickItem::layer), others from the table: REF = element (a hidden child; its layer gets
 * enabled), U64 = image resource (shared texture cache of qtimageitem.h). A sampler without texture samples a 1x1
 * transparent texture. Dynamic textures (layers) are updated in the node's preprocess like Qt's ShaderEffect does.
 *
 * Chain pass (setChainPass, effect chain, effectchain.cpp): the item is a pass of a SHADER link. QtNative sets
 * texelSize (vec2, 1 / pixel size of the source texture, read from the texture itself, also after the layer resized it in
 * preprocess) and dpr (float, effectiveDevicePixelRatio) when the fragment shader declares them; while the fragment slot has
 * no Ready content (id 0, loading, Failed without an earlier Ready one) Qt's default ShaderEffect fragment shader passes
 * source through unchanged (no table checks then). Reserved names of the chain are filtered by the chain, not here. A
 * PYRAMID link also uses setTexelSamplers (texelSize of the composite = the glow / shadow texture, not source),
 * setOptionalUniforms (prefilter and composite get the same table, each takes what it declares; the chain reports names
 * neither declares) and devicePixelRatioChanged (the pass sizes follow dpr).
 *
 * Errors: content problems (unknown name, wrong type, arrays, reserved names, blocks of both stages differing) are
 * joined into vlqtError (one line each), which Elements::readError sends as EVENT_ERROR of the element.
 *
 * Threads: everything is collected on the GUI thread; updatePaintNode (GUI blocked) copies it into the material; the
 * material shader reads only the material. Values only: no allocation per frame (buffers are sized on a layout change).
 */
#pragma once

#include "protocol.h"

#include <QtCore/QMetaObject>
#include <QtCore/QPointer>
#include <QtCore/QString>
#include <QtQuick/QQuickItem>
#include <rhi/qshader.h>

#include <cstdint>
#include <memory>
#include <string>
#include <utility>
#include <vector>

class QSGTexture;
class QSGTextureProvider;

namespace vlqt {

namespace res::image {
class ImageResource;
}
namespace res::shader {
class ShaderResource;
}
struct ImageTexture;

class ShaderItem : public QQuickItem // not final: registered as QML type of the effect chain (qmlRegisterType derives)
{
    Q_OBJECT
    Q_PROPERTY(QQuickItem* source READ source WRITE setSource NOTIFY sourceChanged)
    Q_PROPERTY(QString vlqtError READ error NOTIFY errorChanged)

public:
    explicit ShaderItem(QQuickItem* parent = nullptr);
    ~ShaderItem() override;

    QQuickItem* source() const { return m_source; }
    void setSource(QQuickItem* source);
    const QString& error() const { return m_error; }

    // FRAGMENT_SHADER / VERTEX_SHADER (stage VLQT_SHADER_STAGE_*): false + err for an unknown, released or non shader id.
    bool setShader(int stage, uint64_t id, std::string& err);
    void setBlending(bool on);
    void setMeshResolution(int x, int y);
    // Pass of an effect chain link (see the head comment): auto uniforms texelSize/dpr, pass-through while not Ready.
    void setChainPass(bool on);
    // Chain pass: texelSize from the first of these samplers the fragment shader declares (none: source).
    void setTexelSamplers(std::vector<QByteArray> names);
    // Names of the table that are not reported as unknown when the shaders do not declare them.
    void setOptionalUniforms(std::vector<QByteArray> names);
    // The shown shaders declare a uniform or sampler of that name (false while passing through or not drawable).
    bool declares(const QByteArray& name) const;
    // A Ready fragment shader is shown (not the pass-through of a chain pass).
    bool hasShader() const { return m_frag.content != nullptr; }

    // SET_UNIFORMS (validated by the Scene): begin, one call per entry, end.
    void beginUniforms(bool clear);
    // value points at the wire value (layout like SET); ref = resolved element for REF (nullptr = none).
    void setUniform(const char* name, int nameLen, quint16 valueType, const unsigned char* value, QQuickItem* ref);
    void endUniforms();
    // Layer mipmaps the samplers ask for, on the elements they read; also after the Groups got their proxy.
    void refreshLayerRequests(bool force = false);
    // The REF uniforms changed since the last call (structural change of the table, Elements::syncTextureGroups).
    bool takeRefsChanged() { return std::exchange(m_refsChanged, false); }
    template <class F>
    void forEachRef(F&& f) const
    {
        for (const Uniform& u : m_uniforms)
            if (u.type == VLQT_VALUE_TYPE_REF && u.ref)
                f(u.ref.data());
    }

    // Once per pump after res::beginFrame: items take new Ready / Failed shaders and images.
    static void afterResourceFrame();

signals:
    void sourceChanged();
    void errorChanged();
    void rebuilt();                 // layout from new shaders or a new table (declares may differ)
    void devicePixelRatioChanged(); // window or its device pixel ratio changed

public slots:
    void invalidateSceneGraph();

protected:
    QSGNode* updatePaintNode(QSGNode* old, UpdatePaintNodeData*) override;
    void releaseResources() override;
    void geometryChange(const QRectF& newGeometry, const QRectF& oldGeometry) override;
    void itemChange(ItemChange change, const ItemChangeData& data) override;

private:
    struct ShaderSlot
    {
        uint64_t want = 0, shown = 0; // element references held
        std::shared_ptr<res::shader::ShaderResource> content; // last Ready content of shown, drawn even if want failed
        bool failed = false; // want is Failed (bookkeeping only)
    };
    struct Uniform
    {
        QByteArray name;
        quint16 type = 0;               // VLQT_VALUE_TYPE_*
        float f[16] = {};               // f32, vec*, color (premultiplied), mat4
        qint32 i[2] = {};               // i32, ivec2, bool (0/1)
        QPointer<QQuickItem> ref;       // REF
        uint64_t image = 0;             // U64 image resource id (element reference held)
        bool badImage = false;          // U64 id that is no live image resource (error line)
        std::shared_ptr<res::image::ImageResource> imageContent; // Ready content of image
        std::shared_ptr<ImageTexture> imageTexture;              // its texture in window()
    };
    struct Write
    {
        int offset, size, uniform; // bytes into the block, uniform index
    };
    struct Sampler
    {
        int binding;
        int uniform;  // index into m_uniforms, -1 = none
        bool source;  // the Input
        QPointer<QSGTextureProvider> provider; // current provider (updatePaintNode)
        QByteArray name;
    };

    bool advance(ShaderSlot& slot);
    void advanceAll();
    void rebuild();          // layout + errors from shaders and table (GUI thread, on structural changes only)
    void releaseUniform(Uniform& u);
    void updateImplicitSize();
    QSGTexture* samplerTexture(Sampler& s);
    void watchProvider(QSGTextureProvider* p);
    quint64 samplerState(const Sampler& s) const; // VLQT_VALUE_TYPE_SAMPLER entry of the sampler (present bit set), 0 = none
    QQuickItem* samplerElement(const Sampler& s) const; // element whose layer the sampler reads (Group: its proxy), null for images

    ShaderSlot m_frag, m_vert;
    QPointer<QQuickItem> m_source;
    QMetaObject::Connection m_sourceW, m_sourceH;
    bool m_blending = true;
    int m_meshX = 1, m_meshY = 1;

    std::vector<Uniform> m_uniforms;
    bool m_structureChanged = false;
    std::vector<std::pair<QByteArray, quint64>> m_samplerStates; // SAMPLER entries by sampler2D name, bit 63 = present
    std::vector<QPointer<QQuickItem>> m_layerItems; // elements this item holds a layer request on (layer mipmap)
    bool m_layersDirty = false;
    bool m_refsChanged = false;

    // layout (rebuild)
    std::vector<Write> m_writes;
    std::vector<Sampler> m_samplers;
    int m_ubufSize = 0, m_matrixOffset = -1, m_opacityOffset = -1;
    int m_texelOffset = -1, m_dprOffset = -1; // chain pass: auto uniforms (-1 = not declared)
    int m_texelBinding = -1;                  // chain pass: sampler binding texelSize is read from
    std::vector<QByteArray> m_texelSamplers;  // setTexelSamplers
    std::vector<QByteArray> m_optional;       // setOptionalUniforms
    std::vector<QByteArray> m_declared;       // uniform and sampler names of the shown shaders (rebuild)
    bool m_chain = false;
    bool m_drawable = false;      // both shaders there and usable
    bool m_shaderChanged = true;  // material needs the new shader pair
    bool m_geometryChanged = true;
    QString m_error;

    QSGTexture* m_transparent = nullptr; // 1x1 transparent in window()
    std::vector<QPointer<QSGTextureProvider>> m_watched; // providers with a connection to this item
    QShader m_vs, m_fs; // shader pair of the layout (rebuild)
};

} // namespace vlqt
