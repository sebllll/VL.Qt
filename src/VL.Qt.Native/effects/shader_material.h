/* shader_material.h - the render side of the Shader element: material, material shader, node.
 *
 * Only for ShaderItem::updatePaintNode (GUI thread, blocked in the sync), EffectNode::preprocess and the material shader
 * during rendering: the item copies its state into the material and never touches it elsewhere.
 */
#pragma once

#include <QtCore/QByteArray>
#include <QtQuick/QSGGeometryNode>
#include <QtQuick/QSGMaterial>
#include <QtQuick/QSGTexture>
#include <rhi/qshader.h>

#include <array>
#include <memory>

namespace vlqt {

constexpr int kMaxBindings = 16; // sampler bindings 1..15 (0 = uniform block)

// A texture seen through a sampler state: the sampler Qt creates for a draw comes from the filtering and wrap
// modes of the QSGTexture it gets, and the texture of a layer or an image is shared with other items, so its own modes
// are never touched. The view carries the modes and forwards everything else.
class SamplerView final : public QSGTexture
{
public:
    void configure(QSGTexture* inner, quint64 st)
    {
        m_inner = inner;
        const auto b = [&](int i) { return int((st >> (8 * i)) & 0xff); };
        setFiltering(b(0) == 1 ? QSGTexture::Nearest : QSGTexture::Linear);
        setMipmapFiltering(b(1) == 2 ? QSGTexture::Linear : b(1) == 1 ? QSGTexture::Nearest : QSGTexture::None);
        const auto wrap = [](int a) {
            return a == 1 ? QSGTexture::Repeat : a == 2 ? QSGTexture::MirroredRepeat : QSGTexture::ClampToEdge;
        };
        setHorizontalWrapMode(wrap(b(2)));
        setVerticalWrapMode(wrap(b(3)));
    }
    qint64 comparisonKey() const override { return m_inner->comparisonKey(); }
    QRhiTexture* rhiTexture() const override { return m_inner->rhiTexture(); }
    QSize textureSize() const override { return m_inner->textureSize(); }
    bool hasAlphaChannel() const override { return m_inner->hasAlphaChannel(); }
    bool hasMipmaps() const override { return m_inner->hasMipmaps(); }
    QRectF normalizedTextureSubRect() const override { return m_inner->normalizedTextureSubRect(); }
    bool isAtlasTexture() const override { return m_inner->isAtlasTexture(); }
    void commitTextureOperations(QRhi* rhi, QRhiResourceUpdateBatch* batch) override
    {
        m_inner->commitTextureOperations(rhi, batch);
    }

private:
    QSGTexture* m_inner = nullptr;
};

class EffectMaterial final : public QSGMaterial
{
public:
    QShader vs, fs;
    QSGMaterialType* materialType = nullptr;
    QByteArray ubuf;                 // std140 image of the block (qt_Matrix / qt_Opacity filled by the shader)
    int matrixOffset = -1, opacityOffset = -1;
    std::array<QSGTexture*, kMaxBindings> tex{};
    std::array<quint64, kMaxBindings> state{};                       // sampler state per binding (0 = the texture as it is)
    std::array<std::unique_ptr<SamplerView>, kMaxBindings> view{};   // the texture with that state, made at the first draw
    bool blending = true;
    int texelOffset = -1, sourceBinding = -1; // chain pass auto uniforms (-1 = none)

    QSGMaterialType* type() const override { return materialType; }
    QSGMaterialShader* createShader(QSGRendererInterface::RenderMode) const override;
    int compare(const QSGMaterial* other) const override
    {
        const auto* o = static_cast<const EffectMaterial*>(other);
        for (int i = 0; i < kMaxBindings; ++i) {
            if (tex[i] != o->tex[i])
                return tex[i] < o->tex[i] ? -1 : 1;
            if (state[i] != o->state[i])
                return state[i] < o->state[i] ? -1 : 1;
        }
        if (blending != o->blending)
            return blending ? 1 : -1;
        if (ubuf.size() != o->ubuf.size())
            return ubuf.size() < o->ubuf.size() ? -1 : 1;
        return std::memcmp(ubuf.constData(), o->ubuf.constData(), size_t(ubuf.size()));
    }
};

// One QSGMaterialType per shader pair (content compared).
QSGMaterialType* materialTypeFor(const QShader& vs, const QShader& fs);

// Chain pass: texelSize = 1 / pixel size of the sampled source texture (no-op without the uniform or a texture).
void writeTexelSize(EffectMaterial& m);

// Layers (QSGDynamicTexture) render their source in preprocess, like Qt's ShaderEffect node.
class EffectNode final : public QSGGeometryNode
{
public:
    EffectNode();
    EffectMaterial& mat() { return *static_cast<EffectMaterial*>(material()); }
    void preprocess() override;
    int meshX = 0, meshY = 0;
    float w = -1, h = -1;
};

} // namespace vlqt
