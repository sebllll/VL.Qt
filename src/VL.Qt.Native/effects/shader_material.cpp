#include "effects/shader_material.h"

#include <QtGui/QMatrix4x4>
#include <QtQuick/QSGMaterialShader>

#include <algorithm>
#include <cstring>
#include <vector>

namespace vlqt {

namespace {

// One type per shader pair: the renderer caches one QSGMaterialShader per type (and render mode).
struct TypeEntry
{
    QShader vs, fs;
    std::unique_ptr<QSGMaterialType> type;
};
// Never freed: Qt's renderer caches the material shader per type address; a freed and reused address would draw with
// a stale shader. Grows by one per distinct shader pair (one per saved edit). Sync (GUI blocked) only.
std::vector<TypeEntry> s_types;

class EffectShader final : public QSGMaterialShader
{
public:
    EffectShader(const QShader& vs, const QShader& fs)
    {
        setShader(VertexStage, vs);
        setShader(FragmentStage, fs);
        setFlag(UpdatesGraphicsPipelineState);
    }

    bool updateUniformData(RenderState& state, QSGMaterial* newMaterial, QSGMaterial*) override
    {
        auto* m = static_cast<EffectMaterial*>(newMaterial);
        QByteArray* buf = state.uniformData();
        const int size = int(buf->size());
        char* dst = buf->data();
        std::memcpy(dst, m->ubuf.constData(), size_t(std::min(size, int(m->ubuf.size()))));
        if (m->matrixOffset >= 0 && m->matrixOffset + 64 <= size) {
            const QMatrix4x4 mat = state.combinedMatrix();
            std::memcpy(dst + m->matrixOffset, mat.constData(), 64);
        }
        if (m->opacityOffset >= 0 && m->opacityOffset + 4 <= size) {
            const float op = state.opacity();
            std::memcpy(dst + m->opacityOffset, &op, 4);
        }
        return true;
    }

    void updateSampledImage(RenderState& state, int binding, QSGTexture** texture, QSGMaterial* newMaterial,
        QSGMaterial*) override
    {
        auto* m = static_cast<EffectMaterial*>(newMaterial);
        if (binding < 0 || binding >= kMaxBindings || !m->tex[binding])
            return;
        QSGTexture* t = m->tex[binding];
        if (const quint64 st = m->state[binding]) {
            auto& v = m->view[size_t(binding)];
            if (!v)
                v = std::make_unique<SamplerView>();
            v->configure(t, st);
            t = v.get();
        }
        // Uploads a texture made from a QImage (the CPU copy of an image resource without a shared device, Linux):
        // without it rhiTexture() stays null and Qt binds a transparent dummy. No-op for layers and wrappers.
        t->commitTextureOperations(state.rhi(), state.resourceUpdateBatch());
        *texture = t;
    }

    bool updateGraphicsPipelineState(RenderState&, GraphicsPipelineState* ps, QSGMaterial* newMaterial,
        QSGMaterial*) override
    {
        auto* m = static_cast<EffectMaterial*>(newMaterial);
        ps->blendEnable = m->blending;
        ps->srcColor = GraphicsPipelineState::One; // premultiplied
        ps->dstColor = GraphicsPipelineState::OneMinusSrcAlpha;
        ps->cullMode = GraphicsPipelineState::CullNone;
        return true;
    }
};

} // namespace

QSGMaterialType* materialTypeFor(const QShader& vs, const QShader& fs)
{
    for (const TypeEntry& e : s_types)
        if (e.fs == fs && e.vs == vs)
            return e.type.get();
    s_types.push_back({vs, fs, std::make_unique<QSGMaterialType>()});
    return s_types.back().type.get();
}

QSGMaterialShader* EffectMaterial::createShader(QSGRendererInterface::RenderMode) const
{
    return new EffectShader(vs, fs);
}

// Chain pass: texelSize = 1 / pixel size of the sampled source texture (no-op without the uniform or a texture).
void writeTexelSize(EffectMaterial& m)
{
    if (m.texelOffset < 0 || m.texelOffset + 8 > m.ubuf.size() || m.sourceBinding < 0 || !m.tex[size_t(m.sourceBinding)])
        return;
    const QSize s = m.tex[size_t(m.sourceBinding)]->textureSize();
    if (s.width() <= 0 || s.height() <= 0)
        return;
    const float v[2] = {1.f / float(s.width()), 1.f / float(s.height())};
    std::memcpy(m.ubuf.data() + m.texelOffset, v, 8);
}

EffectNode::EffectNode()
{
    setFlag(UsePreprocess, true);
    setMaterial(new EffectMaterial);
    setFlag(OwnsMaterial, true);
}

void EffectNode::preprocess()
{
    for (QSGTexture* t : mat().tex)
        if (auto* dt = qobject_cast<QSGDynamicTexture*>(t))
            dt->updateTexture();
    writeTexelSize(mat()); // a layer resized in updateTexture: the size of this frame, not the one of the sync
}

} // namespace vlqt
