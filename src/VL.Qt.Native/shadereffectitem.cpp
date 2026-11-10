#include "shadereffectitem.h"

#include "elements.h"
#include "image_resource.h"
#include "protocol.h"
#include "qtimageitem.h"
#include "resources.h"
#include "shader_resource.h"

#include <QtCore/QDirIterator>
#include <QtCore/QFile>
#include <QtCore/QLoggingCategory>
#include <QtGui/QImage>
#include <QtQml/QQmlProperty>
#include <QtQuick/QQuickWindow>
#include <private/qquickshadereffectsource_p.h>
#include <QtQuick/QSGGeometryNode>
#include <QtQuick/QSGMaterial>
#include <QtQuick/QSGMaterialShader>
#include <QtQuick/QSGTexture>
#include <QtQuick/QSGTextureProvider>
#include <rhi/qshaderdescription.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <unordered_map>
#include <unordered_set>

Q_LOGGING_CATEGORY(lcShaderItem, "VL.Qt.ShaderEffect")

namespace vlqt {

namespace {

constexpr int kMaxBindings = 16;   // sampler bindings 1..15 (0 = uniform block)
constexpr int kMaxMesh = 1024;     // MESH_RESOLUTION per axis (vertex count stays below ~1M)

std::unordered_set<ShaderEffectItem*> s_items; // main thread
uint64_t s_contentEpoch = 0, s_failureEpoch = 0;

// Layer requests of the samplers (#492): a mip filter other than None needs the layer of the sampled element rendered with
// mipmaps. Several ShaderEffectItems can sample one element, so the layer has mipmaps while any of them asks.
struct LayerUser
{
    const ShaderEffectItem* owner;
    bool mip;
};
struct LayerEntry
{
    std::vector<LayerUser> users;
    bool mip = false;
};
std::unordered_map<QQuickItem*, LayerEntry> s_layers; // main thread

void writeLayer(QQuickItem* item, bool mip)
{
    // A group frame proxy is the ShaderEffectSource itself, every other element has it as its layer.
    const QString prefix = qobject_cast<QQuickShaderEffectSource*>(item) ? QString() : QStringLiteral("layer.");
    QQmlProperty(item, prefix + QStringLiteral("mipmap")).write(mip);
}

// Applies the union of the requests of one element, drops the entry when nobody asks.
void settleLayer(QQuickItem* item)
{
    const auto it = s_layers.find(item);
    if (it == s_layers.end())
        return;
    LayerEntry& e = it->second;
    bool mip = false;
    for (const LayerUser& u : e.users)
        mip = mip || u.mip;
    if (e.users.empty()) {
        writeLayer(item, false);
        s_layers.erase(it);
    } else if (mip != e.mip) {
        e.mip = mip;
        writeLayer(item, mip);
    }
}

void requestLayer(QQuickItem* item, const ShaderEffectItem* owner, bool mip)
{
    auto [it, added] = s_layers.try_emplace(item);
    if (added)
        QObject::connect(item, &QObject::destroyed, [item] { s_layers.erase(item); });
    auto& users = it->second.users;
    const auto u = std::find_if(users.begin(), users.end(), [&](const LayerUser& x) { return x.owner == owner; });
    if (u == users.end())
        users.push_back({owner, mip});
    else
        u->mip = mip;
    settleLayer(item);
}

void releaseLayer(QQuickItem* item, const ShaderEffectItem* owner)
{
    const auto it = s_layers.find(item);
    if (it == s_layers.end())
        return;
    auto& users = it->second.users;
    users.erase(std::remove_if(users.begin(), users.end(), [&](const LayerUser& x) { return x.owner == owner; }), users.end());
    settleLayer(item);
}

std::string idText(uint64_t id)
{
    return std::to_string(uint32_t(id)) + "/" + std::to_string(uint32_t(id >> 32));
}

// --- Material -------------------------------------------------------------------------------------------------------

// One type per shader pair: the renderer caches one QSGMaterialShader per type (and render mode).
struct TypeEntry
{
    QShader vs, fs;
    std::unique_ptr<QSGMaterialType> type;
};
std::vector<TypeEntry> s_types; // sync (GUI blocked) only; grows with distinct shader pairs, never shrinks

QSGMaterialType* typeFor(const QShader& vs, const QShader& fs)
{
    for (const TypeEntry& e : s_types)
        if (e.fs == fs && e.vs == vs)
            return e.type.get();
    s_types.push_back({vs, fs, std::make_unique<QSGMaterialType>()});
    return s_types.back().type.get();
}

// A texture seen through a sampler state (#492): the sampler Qt creates for a draw comes from the filtering and wrap
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
    int texelOffset = -1, dprOffset = -1, sourceBinding = -1; // chain pass auto uniforms (-1 = none)

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
        // without it rhiTexture() stays null and Qt binds a transparent dummy (#77). No-op for layers and wrappers.
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

// Layers (QSGDynamicTexture) render their source in preprocess, like Qt's ShaderEffect node.
class EffectNode final : public QSGGeometryNode
{
public:
    EffectNode()
    {
        setFlag(UsePreprocess, true);
        setMaterial(new EffectMaterial);
        setFlag(OwnsMaterial, true);
    }
    EffectMaterial& mat() { return *static_cast<EffectMaterial*>(material()); }
    void preprocess() override
    {
        for (QSGTexture* t : mat().tex)
            if (auto* dt = qobject_cast<QSGDynamicTexture*>(t))
                dt->updateTexture();
        writeTexelSize(mat()); // a layer resized in updateTexture: the size of this frame, not the one of the sync
    }
    int meshX = 0, meshY = 0;
    float w = -1, h = -1;
};

int valueBytes(quint16 vt)
{
    switch (vt) {
    case VLQT_VT_F32: case VLQT_VT_I32: case VLQT_VT_BOOL: return 4;
    case VLQT_VT_VEC2: case VLQT_VT_IVEC2: return 8;
    case VLQT_VT_VEC3: return 12;
    case VLQT_VT_VEC4: case VLQT_VT_COLOR: return 16;
    case VLQT_VT_MAT4: return 64;
    default: return 0;
    }
}

const char* valueTypeName(quint16 vt)
{
    switch (vt) {
    case VLQT_VT_F32: return "Float";
    case VLQT_VT_I32: return "Int";
    case VLQT_VT_BOOL: return "Bool";
    case VLQT_VT_COLOR: return "Color";
    case VLQT_VT_MAT4: return "Matrix";
    case VLQT_VT_REF: return "Texture";
    case VLQT_VT_U64: return "Image";
    case VLQT_VT_VEC2: return "Vector2";
    case VLQT_VT_VEC3: return "Vector3";
    case VLQT_VT_VEC4: return "Vector4";
    case VLQT_VT_IVEC2: return "Int2";
    default: return "?";
    }
}

// GLSL type of a block member -> accepted value types (exact, protocol.h); nullptr = not settable.
const char* glslName(QShaderDescription::VariableType t)
{
    switch (t) {
    case QShaderDescription::Float: return "float";
    case QShaderDescription::Int: return "int";
    case QShaderDescription::Bool: return "bool";
    case QShaderDescription::Uint: return "uint"; // a GLSL bool in a uniform block reflects as uint (SPIR-V)
    case QShaderDescription::Vec2: return "vec2";
    case QShaderDescription::Vec3: return "vec3";
    case QShaderDescription::Vec4: return "vec4";
    case QShaderDescription::Int2: return "ivec2";
    case QShaderDescription::Mat4: return "mat4";
    default: return nullptr;
    }
}

bool accepts(QShaderDescription::VariableType t, quint16 vt)
{
    switch (t) {
    case QShaderDescription::Float: return vt == VLQT_VT_F32;
    case QShaderDescription::Int: return vt == VLQT_VT_I32;
    case QShaderDescription::Bool: return vt == VLQT_VT_BOOL;
    case QShaderDescription::Uint: return vt == VLQT_VT_BOOL; // bool (0/1) into the uint a GLSL bool reflects as
    case QShaderDescription::Vec2: return vt == VLQT_VT_VEC2;
    case QShaderDescription::Vec3: return vt == VLQT_VT_VEC3;
    case QShaderDescription::Vec4: return vt == VLQT_VT_VEC4 || vt == VLQT_VT_COLOR;
    case QShaderDescription::Int2: return vt == VLQT_VT_IVEC2;
    case QShaderDescription::Mat4: return vt == VLQT_VT_MAT4;
    default: return false;
    }
}

// Qt Quick's own ShaderEffect default fragment shader (source * qt_Opacity): pass-through of a chain pass while its
// shader is not Ready. Same lookup as res::shader::defaultVertexShader.
const QShader& defaultFragmentShader()
{
    static const QShader s = [] {
        const QString known = QStringLiteral(":/qt-project.org/scenegraph/shaders_ng/shadereffect.frag.qsb");
        QString path = QFile::exists(known) ? known : QString();
        if (path.isEmpty()) {
            QDirIterator it(QStringLiteral(":/qt-project.org"), QDirIterator::Subdirectories);
            while (it.hasNext()) {
                const QString p = it.next();
                if (p.endsWith(QStringLiteral("/shadereffect.frag.qsb"))) {
                    path = p;
                    break;
                }
            }
        }
        QFile f(path);
        QShader r;
        if (!path.isEmpty() && f.open(QIODevice::ReadOnly))
            r = QShader::fromSerialized(f.readAll());
        if (!r.isValid())
            qCWarning(lcShaderItem, "Qt's default ShaderEffect fragment shader not found: effect chain links without a Ready shader draw nothing");
        return r;
    }();
    return s;
}

bool reserved(const QByteArray& name)
{
    return name == "qt_Matrix" || name == "qt_Opacity" || name == "source";
}

} // namespace

// --- Item -----------------------------------------------------------------------------------------------------------

ShaderEffectItem::ShaderEffectItem(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
    s_items.insert(this);
}

ShaderEffectItem::~ShaderEffectItem()
{
    s_items.erase(this);
    for (ShaderSlot* s : {&m_frag, &m_vert}) {
        if (s->want && s->want != s->shown)
            res::releaseElement(s->want);
        if (s->shown)
            res::releaseElement(s->shown);
    }
    // Gives back the layer requests. The element may be in its own destruction (the Scene deletes in any order): the layer
    // goes back to its defaults later, on the event loop of the element, which drops the call if the element is gone.
    for (const auto& held : m_layerItems) {
        if (!held)
            continue;
        QQuickItem* item = held.data();
        const auto it = s_layers.find(item);
        if (it == s_layers.end())
            continue;
        auto& users = it->second.users;
        users.erase(std::remove_if(users.begin(), users.end(), [&](const LayerUser& x) { return x.owner == this; }), users.end());
        QMetaObject::invokeMethod(item, [item] { settleLayer(item); }, Qt::QueuedConnection);
    }
    for (Uniform& u : m_uniforms)
        releaseUniform(u);
    delete m_transparent;
}

void ShaderEffectItem::setSource(QQuickItem* source)
{
    if (source == m_source)
        return;
    disconnect(m_sourceW);
    disconnect(m_sourceH);
    m_source = source;
    if (source) {
        m_sourceW = connect(source, &QQuickItem::widthChanged, this, &ShaderEffectItem::updateImplicitSize);
        m_sourceH = connect(source, &QQuickItem::heightChanged, this, &ShaderEffectItem::updateImplicitSize);
    }
    updateImplicitSize();
    m_layersDirty = true;
    refreshLayerRequests();
    update();
    emit sourceChanged();
}

void ShaderEffectItem::updateImplicitSize()
{
    if (m_source)
        setImplicitSize(m_source->width(), m_source->height());
    else
        setImplicitSize(0, 0);
}

bool ShaderEffectItem::setShader(int stage, uint64_t id, std::string& err)
{
    ShaderSlot& s = stage == VLQT_SHADER_STAGE_VERTEX ? m_vert : m_frag;
    if (id == s.want)
        return true;
    if (id && id != s.shown) {
        if (res::kindOfId(id, nullptr) != VLQT_RES_KIND_SHADER) {
            err = "unknown, released or non shader resource " + idText(id);
            return false;
        }
        if (res::acquire(id) != res::okRc) {
            err = "unknown or released shader resource " + idText(id);
            return false;
        }
    }
    if (s.want && s.want != s.shown)
        res::releaseElement(s.want); // never shown: the one before stays
    s.want = id;
    s.failed = false;
    if (!id) {
        if (s.shown)
            res::releaseElement(s.shown);
        s.shown = 0;
        s.content.reset();
    } else {
        advance(s);
    }
    rebuild();
    update();
    return true;
}

void ShaderEffectItem::setChainPass(bool on)
{
    if (on == m_chain)
        return;
    m_chain = on;
    rebuild();
    update();
}

void ShaderEffectItem::setTexelSamplers(std::vector<QByteArray> names)
{
    m_texelSamplers = std::move(names);
    rebuild();
    update();
}

void ShaderEffectItem::setOptionalUniforms(std::vector<QByteArray> names)
{
    if (names == m_optional)
        return;
    m_optional = std::move(names);
    rebuild();
}

bool ShaderEffectItem::declares(const QByteArray& name) const
{
    return std::find(m_declared.begin(), m_declared.end(), name) != m_declared.end();
}

void ShaderEffectItem::setBlending(bool on)
{
    if (on == m_blending)
        return;
    m_blending = on;
    update();
}

void ShaderEffectItem::setMeshResolution(int x, int y)
{
    x = std::clamp(x, 1, kMaxMesh);
    y = std::clamp(y, 1, kMaxMesh);
    if (x == m_meshX && y == m_meshY)
        return;
    m_meshX = x;
    m_meshY = y;
    m_geometryChanged = true;
    update();
}

bool ShaderEffectItem::advance(ShaderSlot& s)
{
    if (!s.want)
        return false;
    auto r = res::shader::ready(s.want);
    if (r) {
        if (r == s.content && !s.failed && s.shown == s.want)
            return false;
        if (s.shown && s.shown != s.want)
            res::releaseElement(s.shown);
        s.shown = s.want;
        s.content = std::move(r);
        s.failed = false;
        return true;
    }
    const bool failed = res::isFailed(s.want);
    if (failed == s.failed)
        return false;
    s.failed = failed;
    return false; // the shown content (if any) keeps drawing, nothing to rebuild
}

void ShaderEffectItem::advanceAll()
{
    const bool frag = advance(m_frag);
    const bool vert = advance(m_vert);
    bool images = false;
    for (Uniform& u : m_uniforms) {
        if (!u.image)
            continue;
        auto r = res::image::ready(u.image);
        if (r && r != u.imageContent) {
            u.imageContent = std::move(r);
            images = true;
        }
    }
    if (frag || vert)
        rebuild();
    if (frag || vert || images)
        update();
}

void ShaderEffectItem::afterResourceFrame()
{
    const uint64_t content = res::contentEpoch(), failure = res::failureEpoch();
    if (content == s_contentEpoch && failure == s_failureEpoch)
        return;
    s_contentEpoch = content;
    s_failureEpoch = failure;
    for (ShaderEffectItem* item : s_items)
        item->advanceAll();
}

void ShaderEffectItem::releaseUniform(Uniform& u)
{
    if (u.image)
        res::releaseElement(u.image);
    u.image = 0;
    u.imageContent.reset();
    u.imageTexture.reset();
    u.ref = nullptr;
}

void ShaderEffectItem::beginUniforms(bool clear)
{
    if (!clear)
        return;
    for (Uniform& u : m_uniforms)
        releaseUniform(u);
    m_uniforms.clear();
    m_samplerStates.clear();
    m_layersDirty = true;
    m_structureChanged = true;
}

void ShaderEffectItem::setUniform(const char* name, int nameLen, quint16 vt, const unsigned char* value, QQuickItem* ref)
{
    const QByteArrayView key(name, nameLen);
    if (vt == VLQT_VT_SAMPLER) { // own table: the texture of the same name keeps its entry
        quint64 st;
        std::memcpy(&st, value, 8);
        st |= quint64(1) << 63;
        auto sit = std::find_if(m_samplerStates.begin(), m_samplerStates.end(), [&](const auto& e) { return e.first == key; });
        if (sit == m_samplerStates.end()) {
            m_samplerStates.emplace_back(key.toByteArray(), st);
            m_structureChanged = true; // rebuild checks that the shader has this sampler
        } else if (sit->second != st) {
            sit->second = st;
        }
        m_layersDirty = true;
        return;
    }
    auto it = std::find_if(m_uniforms.begin(), m_uniforms.end(), [&](const Uniform& u) { return u.name == key; });
    if (it == m_uniforms.end()) {
        m_uniforms.emplace_back();
        it = std::prev(m_uniforms.end());
        it->name = key.toByteArray();
        m_structureChanged = true;
    }
    Uniform& u = *it;
    if (u.type != vt) {
        releaseUniform(u);
        u.type = vt;
        m_structureChanged = true;
    }
    switch (vt) {
    case VLQT_VT_F32: case VLQT_VT_VEC2: case VLQT_VT_VEC3: case VLQT_VT_VEC4: case VLQT_VT_MAT4:
        std::memcpy(u.f, value, size_t(valueBytes(vt)));
        break;
    case VLQT_VT_COLOR: // straight on the wire, premultiplied like QML's ShaderEffect
        std::memcpy(u.f, value, 16);
        u.f[0] *= u.f[3];
        u.f[1] *= u.f[3];
        u.f[2] *= u.f[3];
        break;
    case VLQT_VT_I32:
        std::memcpy(u.i, value, 4);
        break;
    case VLQT_VT_IVEC2:
        std::memcpy(u.i, value, 8);
        break;
    case VLQT_VT_BOOL:
        u.i[0] = value[0] != 0 ? 1 : 0;
        break;
    case VLQT_VT_REF:
        if (u.ref != ref) {
            u.ref = ref;
            // A texture child is drawn through its layer (hidden on REPARENT); the layer stays while it lives. A Group
            // goes through its frame proxy instead (#486, Elements::syncTextureGroups after endUniforms).
            if (ref && !isGroup(ref))
                QQmlProperty(ref, QStringLiteral("layer.enabled")).write(true);
            m_structureChanged = true;
        }
        break;
    case VLQT_VT_U64: {
        uint64_t id;
        std::memcpy(&id, value, 8);
        if (id != u.image) {
            if (u.image)
                res::releaseElement(u.image);
            u.image = 0;
            u.imageContent.reset();
            u.imageTexture.reset();
            if (id && res::kindOfId(id, nullptr) == VLQT_RES_KIND_IMAGE && res::acquire(id) == res::okRc) {
                u.image = id;
                u.imageContent = res::image::ready(id);
            }
            u.badImage = id && !u.image;
            m_structureChanged = true;
        }
        break;
    }
    default:
        break;
    }
}

void ShaderEffectItem::endUniforms()
{
    if (m_structureChanged) {
        m_structureChanged = false;
        m_refsChanged = true; // rare: the Scene re-collects the Groups among the REF uniforms
        m_layersDirty = true;
        rebuild();
    }
    refreshLayerRequests();
    update();
}

void ShaderEffectItem::rebuild()
{
    m_writes.clear();
    m_samplers.clear();
    m_ubufSize = 0;
    m_matrixOffset = m_opacityOffset = -1;
    m_texelOffset = m_dprOffset = m_texelBinding = -1;
    m_declared.clear();
    m_shaderChanged = true;
    QStringList errors;

    // last good version (#483): a Failed want keeps the Ready content of the slot (held via shown); none -> nothing
    const auto* frag = m_frag.content.get();
    const auto* vert = m_vert.content.get();
    if (frag && frag->stage() != VLQT_SHADER_STAGE_FRAGMENT) {
        errors << QStringLiteral("Fragment Shader: the resource holds a vertex shader");
        frag = nullptr;
    }
    if (vert && vert->stage() != VLQT_SHADER_STAGE_VERTEX) {
        errors << QStringLiteral("Vertex Shader: the resource holds a fragment shader");
        vert = nullptr;
    }
    const bool passThrough = m_chain && !frag; // chain pass without a Ready shader: the input unchanged
    m_fs = frag ? frag->shader() : (passThrough ? defaultFragmentShader() : QShader());
    m_vs = vert ? vert->shader() : (m_vert.want ? QShader() : res::shader::defaultVertexShader());
    m_drawable = m_fs.isValid() && m_vs.isValid();

    if (m_drawable) {
        struct Member
        {
            QByteArray name;
            int offset, size;
            QShaderDescription::VariableType type;
            bool array;
        };
        std::vector<Member> members;
        struct SamplerDesc
        {
            QByteArray name;
            int binding;
            bool array;
        };
        std::vector<SamplerDesc> samplers;
        for (const QShader* s : {&m_vs, &m_fs}) {
            const QShaderDescription d = s->description();
            for (const auto& b : d.uniformBlocks()) {
                m_ubufSize = std::max(m_ubufSize, b.size);
                for (const auto& m : b.members) {
                    auto it = std::find_if(members.begin(), members.end(), [&](const Member& x) { return x.name == m.name; });
                    if (it == members.end())
                        members.push_back({m.name, m.offset, m.size, m.type, !m.arrayDims.isEmpty()});
                    else if (it->offset != m.offset || it->type != m.type)
                        errors << QStringLiteral("uniform '%1' differs between vertex and fragment shader (the blocks must be identical)")
                                      .arg(QString::fromUtf8(m.name));
                }
            }
            for (const auto& smp : d.combinedImageSamplers()) {
                auto it = std::find_if(samplers.begin(), samplers.end(), [&](const SamplerDesc& x) { return x.binding == smp.binding; });
                if (it == samplers.end())
                    samplers.push_back({smp.name, smp.binding, !smp.arrayDims.isEmpty()});
                else if (it->name != smp.name)
                    errors << QStringLiteral("binding %1 is '%2' in one shader and '%3' in the other")
                                  .arg(smp.binding).arg(QString::fromUtf8(it->name), QString::fromUtf8(smp.name));
            }
        }
        for (const Member& m : members) {
            qCDebug(lcShaderItem, "%p: uniform %s offset %d size %d", static_cast<void*>(this), m.name.constData(),
                m.offset, m.size);
            if (m.name == "qt_Matrix")
                m_matrixOffset = m.offset;
            else if (m.name == "qt_Opacity")
                m_opacityOffset = m.offset;
            else if (m_chain && m.name == "texelSize") {
                if (m.type == QShaderDescription::Vec2 && !m.array && m.offset + 8 <= m_ubufSize)
                    m_texelOffset = m.offset;
                else
                    errors << QStringLiteral("uniform 'texelSize' must be a vec2 (set by VL.Qt)");
            } else if (m_chain && m.name == "dpr") {
                if (m.type == QShaderDescription::Float && !m.array && m.offset + 4 <= m_ubufSize)
                    m_dprOffset = m.offset;
                else
                    errors << QStringLiteral("uniform 'dpr' must be a float (set by VL.Qt)");
            }
        }
        for (const SamplerDesc& s : samplers) {
            if (s.binding < 1 || s.binding >= kMaxBindings)
                continue; // rejected by the resource check already
            if (s.array)
                errors << QStringLiteral("sampler '%1': sampler arrays are not supported").arg(QString::fromUtf8(s.name));
            m_samplers.push_back({s.binding, -1, s.name == "source", nullptr, s.name});
            if (s.name == "source" && m_texelBinding < 0)
                m_texelBinding = s.binding;
        }
        // texelSize of a chain pass: the first declared of m_texelSamplers, else source.
        for (const QByteArray& n : m_texelSamplers) {
            const auto it = std::find_if(samplers.begin(), samplers.end(), [&](const SamplerDesc& s) { return s.name == n; });
            if (it != samplers.end()) {
                m_texelBinding = it->binding;
                break;
            }
        }
        if (!passThrough) {
            for (const Member& m : members)
                m_declared.push_back(m.name);
            for (const SamplerDesc& s : samplers)
                m_declared.push_back(s.name);
        }

        for (int k = 0; !passThrough && k < int(m_uniforms.size()); ++k) {
            const Uniform& u = m_uniforms[size_t(k)];
            const QString name = QString::fromUtf8(u.name);
            if (reserved(u.name)) {
                errors << QStringLiteral("uniform '%1' is reserved (%2)")
                              .arg(name, u.name == "source" ? QStringLiteral("use the Input pin")
                                                            : QStringLiteral("set by VL.Qt"));
                continue;
            }
            const auto sit = std::find_if(samplers.begin(), samplers.end(), [&](const SamplerDesc& s) { return s.name == u.name; });
            if (sit != samplers.end()) {
                if (u.type != VLQT_VT_REF && u.type != VLQT_VT_U64) {
                    errors << QStringLiteral("uniform '%1' is a sampler2D, got %2 (use Texture or Image)")
                                  .arg(name, QLatin1StringView(valueTypeName(u.type)));
                    continue;
                }
                if (u.type == VLQT_VT_U64 && u.badImage)
                    errors << QStringLiteral("uniform '%1': unknown or released image resource").arg(name);
                if (u.type == VLQT_VT_REF && u.ref) {
                    bool cyclic = false;
                    for (QQuickItem* a = this; a; a = a->parentItem())
                        cyclic = cyclic || a == u.ref;
                    if (cyclic) {
                        errors << QStringLiteral("uniform '%1': the texture element must not be the effect or one of its ancestors").arg(name);
                        continue;
                    }
                }
                for (Sampler& s : m_samplers)
                    if (s.binding == sit->binding)
                        s.uniform = k;
                continue;
            }
            const auto mit = std::find_if(members.begin(), members.end(), [&](const Member& m) { return m.name == u.name; });
            if (mit == members.end()) {
                // A new shader still baking: the old one is drawn meanwhile, the uniform may be in the new one; rebuild runs again when it is Ready.
                const bool pending = (m_frag.want && m_frag.want != m_frag.shown && !m_frag.failed)
                    || (m_vert.want && m_vert.want != m_vert.shown && !m_vert.failed);
                if (!pending && std::find(m_optional.begin(), m_optional.end(), u.name) == m_optional.end())
                    errors << QStringLiteral("unknown uniform '%1' (not in the shaders)").arg(name);
                continue;
            }
            if (mit->array) {
                errors << QStringLiteral("uniform '%1': arrays are not supported").arg(name);
                continue;
            }
            const char* glsl = glslName(mit->type);
            if (!glsl) {
                errors << QStringLiteral("uniform '%1': this GLSL type is not supported").arg(name);
                continue;
            }
            if (!accepts(mit->type, u.type)) {
                errors << QStringLiteral("uniform '%1' is a %2, got %3")
                              .arg(name, QLatin1StringView(glsl), QLatin1StringView(valueTypeName(u.type)));
                continue;
            }
            const int size = valueBytes(u.type);
            if (mit->offset + size <= m_ubufSize)
                m_writes.push_back({mit->offset, size, k});
        }
        // Sampler states (#492) of names the shader does not have: same leniency as for uniforms while a shader still bakes.
        for (const auto& st : m_samplerStates) {
            if (passThrough || std::any_of(samplers.begin(), samplers.end(), [&](const SamplerDesc& s) { return s.name == st.first; }))
                continue;
            const bool pending = (m_frag.want && m_frag.want != m_frag.shown && !m_frag.failed)
                || (m_vert.want && m_vert.want != m_vert.shown && !m_vert.failed);
            if (!pending && std::find(m_optional.begin(), m_optional.end(), st.first) == m_optional.end())
                errors << QStringLiteral("sampler state '%1': the shaders have no sampler2D of that name").arg(QString::fromUtf8(st.first));
        }
    }

    m_layersDirty = true;
    refreshLayerRequests();

    const QString error = errors.join(u'\n');
    if (error != m_error) {
        m_error = error;
        if (!error.isEmpty())
            qCDebug(lcShaderItem, "%p: %s", static_cast<void*>(this), qPrintable(error));
        emit errorChanged();
    }
    emit rebuilt();
}

void ShaderEffectItem::watchProvider(QSGTextureProvider* p)
{
    for (const auto& w : m_watched)
        if (w == p)
            return;
    m_watched.erase(std::remove_if(m_watched.begin(), m_watched.end(), [](const auto& w) { return w.isNull(); }),
        m_watched.end());
    m_watched.emplace_back(p);
    connect(p, &QSGTextureProvider::textureChanged, this, &QQuickItem::update, Qt::QueuedConnection);
    // A provider that goes (layer off, element deleted) must not stay in the material: refresh at the next sync.
    connect(p, &QObject::destroyed, this, &QQuickItem::update);
}

quint64 ShaderEffectItem::samplerState(const Sampler& s) const
{
    for (const auto& e : m_samplerStates)
        if (e.first == s.name)
            return e.second;
    return 0;
}

QQuickItem* ShaderEffectItem::samplerElement(const Sampler& s) const
{
    QQuickItem* item = nullptr;
    if (s.source)
        item = m_source;
    else if (s.uniform >= 0 && m_uniforms[size_t(s.uniform)].type == VLQT_VT_REF)
        item = m_uniforms[size_t(s.uniform)].ref;
    if (item) {
        if (QQuickItem* proxy = groupTextureSource(item))
            item = proxy;
    }
    return item;
}

void ShaderEffectItem::refreshLayerRequests(bool force)
{
    if (!m_layersDirty && !force)
        return;
    m_layersDirty = false;
    // Items this item asks something from now (an image has no layer: its mipmaps come from its load).
    std::vector<QQuickItem*> now;
    for (const Sampler& s : m_samplers) {
        const quint64 st = samplerState(s);
        QQuickItem* item = st ? samplerElement(s) : nullptr;
        if (!item)
            continue;
        const bool mip = ((st >> 8) & 0xff) != 0;
        if (!mip && std::find_if(m_layerItems.begin(), m_layerItems.end(), [&](const auto& p) { return p == item; }) == m_layerItems.end())
            continue; // defaults need no request (and no write to the layer)
        requestLayer(item, this, mip);
        now.push_back(item);
    }
    for (const auto& held : m_layerItems)
        if (held && std::find(now.begin(), now.end(), held.data()) == now.end())
            releaseLayer(held, this);
    m_layerItems.assign(now.begin(), now.end());
}

QSGTexture* ShaderEffectItem::samplerTexture(Sampler& s)
{
    QQuickItem* item = nullptr;
    const Uniform* u = s.uniform >= 0 ? &m_uniforms[size_t(s.uniform)] : nullptr;
    if (s.source)
        item = m_source;
    else if (u && u->type == VLQT_VT_REF)
        item = u->ref;
    if (item) {
        if (QQuickItem* proxy = groupTextureSource(item)) // a Group texture is its whole frame (#486)
            item = proxy;
        QSGTextureProvider* p = item->isTextureProvider() ? item->textureProvider() : nullptr;
        s.provider = p;
        if (!p)
            return nullptr;
        watchProvider(p);
        return p->texture();
    }
    if (u && u->type == VLQT_VT_U64 && u->imageContent) {
        auto& self = m_uniforms[size_t(s.uniform)];
        self.imageTexture = imageTextureFor(window(), self.imageContent);
        return self.imageTexture ? textureOf(*self.imageTexture) : nullptr;
    }
    return nullptr;
}

void ShaderEffectItem::invalidateSceneGraph()
{
    for (Uniform& u : m_uniforms)
        u.imageTexture.reset();
    delete m_transparent;
    m_transparent = nullptr;
}

void ShaderEffectItem::releaseResources()
{
    invalidateSceneGraph();
}

void ShaderEffectItem::geometryChange(const QRectF& newGeometry, const QRectF& oldGeometry)
{
    QQuickItem::geometryChange(newGeometry, oldGeometry);
    if (newGeometry.size() != oldGeometry.size())
        update();
}

void ShaderEffectItem::itemChange(ItemChange change, const ItemChangeData& data)
{
    QQuickItem::itemChange(change, data);
    if (change == ItemDevicePixelRatioHasChanged || change == ItemSceneChange) {
        update(); // dpr of a chain pass
        emit devicePixelRatioChanged();
    }
}

QSGNode* ShaderEffectItem::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    const float w = float(width()), h = float(height());
    if (!m_drawable || w <= 0 || h <= 0 || !window()) {
        delete old;
        return nullptr;
    }
    if (!m_transparent) {
        QImage img(1, 1, QImage::Format_RGBA8888_Premultiplied);
        img.fill(Qt::transparent);
        m_transparent = window()->createTextureFromImage(img, QQuickWindow::TextureHasAlphaChannel);
    }

    auto* node = static_cast<EffectNode*>(old);
    QSGNode::DirtyState dirty = QSGNode::DirtyMaterial;
    if (!node) {
        node = new EffectNode;
        m_shaderChanged = true;
    }
    EffectMaterial& mat = node->mat();

    if (m_shaderChanged) {
        m_shaderChanged = false;
        mat.vs = m_vs;
        mat.fs = m_fs;
        mat.materialType = typeFor(m_vs, m_fs);
        mat.matrixOffset = m_matrixOffset;
        mat.opacityOffset = m_opacityOffset;
        mat.ubuf.fill(0, m_ubufSize); // removed uniforms go back to 0; sized here only (no allocation per frame)
    }
    char* ub = mat.ubuf.data();
    for (const Write& wr : m_writes) {
        const Uniform& u = m_uniforms[size_t(wr.uniform)];
        const bool ints = u.type == VLQT_VT_I32 || u.type == VLQT_VT_IVEC2 || u.type == VLQT_VT_BOOL;
        std::memcpy(ub + wr.offset, ints ? static_cast<const void*>(u.i) : static_cast<const void*>(u.f), size_t(wr.size));
    }
    mat.blending = m_blending;
    mat.setFlag(QSGMaterial::Blending, m_blending);
    mat.setFlag(QSGMaterial::RequiresFullMatrix, true); // custom vertex shaders see item coordinates
    mat.tex.fill(nullptr);
    mat.state.fill(0);
    mat.sourceBinding = -1;
    for (Sampler& s : m_samplers) {
        QSGTexture* t = samplerTexture(s);
        mat.tex[size_t(s.binding)] = t ? t : m_transparent;
        mat.state[size_t(s.binding)] = samplerState(s);
        if (s.binding == m_texelBinding)
            mat.sourceBinding = s.binding;
    }
    mat.texelOffset = m_texelOffset;
    mat.dprOffset = m_dprOffset;
    if (m_dprOffset >= 0) {
        const float dpr = float(window()->effectiveDevicePixelRatio());
        std::memcpy(ub + m_dprOffset, &dpr, 4);
    }
    writeTexelSize(mat); // again in preprocess, after a layer updated its texture

    if (m_geometryChanged || node->meshX != m_meshX || node->meshY != m_meshY || node->w != w || node->h != h) {
        m_geometryChanged = false;
        const int nx = m_meshX, ny = m_meshY;
        const int vcount = (nx + 1) * (ny + 1), icount = nx * ny * 6;
        QSGGeometry* g = node->geometry();
        if (!g) {
            g = new QSGGeometry(QSGGeometry::defaultAttributes_TexturedPoint2D(), vcount, icount,
                QSGGeometry::UnsignedIntType);
            g->setDrawingMode(QSGGeometry::DrawTriangles);
            node->setGeometry(g);
            node->setFlag(QSGNode::OwnsGeometry, true);
        } else if (node->meshX != nx || node->meshY != ny) {
            g->allocate(vcount, icount);
        }
        auto* v = g->vertexDataAsTexturedPoint2D();
        for (int y = 0; y <= ny; ++y) {
            for (int x = 0; x <= nx; ++x) {
                const float tx = float(x) / nx, ty = float(y) / ny;
                v++->set(tx * w, ty * h, tx, ty);
            }
        }
        if (node->meshX != nx || node->meshY != ny) {
            quint32* idx = g->indexDataAsUInt();
            for (int y = 0; y < ny; ++y) {
                for (int x = 0; x < nx; ++x) {
                    const quint32 a = quint32(y * (nx + 1) + x), b = a + 1, c = a + quint32(nx + 1), d = c + 1;
                    *idx++ = a; *idx++ = c; *idx++ = b;
                    *idx++ = b; *idx++ = c; *idx++ = d;
                }
            }
        }
        node->meshX = nx;
        node->meshY = ny;
        node->w = w;
        node->h = h;
        dirty |= QSGNode::DirtyGeometry;
    }
    node->markDirty(dirty);
    return node;
}

} // namespace vlqt
