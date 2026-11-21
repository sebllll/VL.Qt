#include "effects/shader_item.h"

#include "effects/layer_requests.h"
#include "effects/shader_material.h"
#include "elements/elements.h"
#include "elements/group_item.h"
#include "image_resource.h"
#include "protocol.h"
#include "qtimageitem.h"
#include "resources/resources.h"
#include "shader_resource.h"

#include <QtCore/QDirIterator>
#include <QtCore/QFile>
#include <QtCore/QLoggingCategory>
#include <QtGui/QImage>
#include <QtQml/QQmlProperty>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGGeometryNode>
#include <QtQuick/QSGMaterial>
#include <QtQuick/QSGTexture>
#include <QtQuick/QSGTextureProvider>
#include <rhi/qshaderdescription.h>

#include <algorithm>
#include <cstring>
#include <unordered_set>

Q_LOGGING_CATEGORY(lcShaderItem, "VL.Qt.ShaderEffect")

namespace vlqt {

namespace {

constexpr int kMaxMesh = 1024;     // MESH_RESOLUTION per axis (vertex count stays below ~1M)

std::unordered_set<ShaderItem*> s_items; // main thread
uint64_t s_contentEpoch = 0, s_failureEpoch = 0;

std::string idText(uint64_t id)
{
    return std::to_string(uint32_t(id)) + "/" + std::to_string(uint32_t(id >> 32));
}

int valueBytes(quint16 vt)
{
    switch (vt) {
    case VLQT_VALUE_TYPE_F32: case VLQT_VALUE_TYPE_I32: case VLQT_VALUE_TYPE_BOOL: return 4;
    case VLQT_VALUE_TYPE_VEC2: case VLQT_VALUE_TYPE_IVEC2: return 8;
    case VLQT_VALUE_TYPE_VEC3: return 12;
    case VLQT_VALUE_TYPE_VEC4: case VLQT_VALUE_TYPE_COLOR: return 16;
    case VLQT_VALUE_TYPE_MAT4: return 64;
    default: return 0;
    }
}

// Copies bytes into a stored value; true if they differ from it.
bool assign(void* dst, const void* src, size_t n)
{
    if (std::memcmp(dst, src, n) == 0)
        return false;
    std::memcpy(dst, src, n);
    return true;
}

const char* valueTypeName(quint16 vt)
{
    switch (vt) {
    case VLQT_VALUE_TYPE_F32: return "Float";
    case VLQT_VALUE_TYPE_I32: return "Int";
    case VLQT_VALUE_TYPE_BOOL: return "Bool";
    case VLQT_VALUE_TYPE_COLOR: return "Color";
    case VLQT_VALUE_TYPE_MAT4: return "Matrix";
    case VLQT_VALUE_TYPE_REF: return "Texture";
    case VLQT_VALUE_TYPE_U64: return "Image";
    case VLQT_VALUE_TYPE_VEC2: return "Vector2";
    case VLQT_VALUE_TYPE_VEC3: return "Vector3";
    case VLQT_VALUE_TYPE_VEC4: return "Vector4";
    case VLQT_VALUE_TYPE_IVEC2: return "Int2";
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
    case QShaderDescription::Float: return vt == VLQT_VALUE_TYPE_F32;
    case QShaderDescription::Int: return vt == VLQT_VALUE_TYPE_I32;
    case QShaderDescription::Bool: return vt == VLQT_VALUE_TYPE_BOOL;
    case QShaderDescription::Uint: return vt == VLQT_VALUE_TYPE_BOOL; // bool (0/1) into the uint a GLSL bool reflects as
    case QShaderDescription::Vec2: return vt == VLQT_VALUE_TYPE_VEC2;
    case QShaderDescription::Vec3: return vt == VLQT_VALUE_TYPE_VEC3;
    case QShaderDescription::Vec4: return vt == VLQT_VALUE_TYPE_VEC4 || vt == VLQT_VALUE_TYPE_COLOR;
    case QShaderDescription::Int2: return vt == VLQT_VALUE_TYPE_IVEC2;
    case QShaderDescription::Mat4: return vt == VLQT_VALUE_TYPE_MAT4;
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

ShaderItem::ShaderItem(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
    s_items.insert(this);
}

ShaderItem::~ShaderItem()
{
    s_items.erase(this);
    for (ShaderSlot* s : {&m_frag, &m_vert}) {
        if (s->want && s->want != s->shown)
            res::releaseElement(s->want);
        if (s->shown)
            res::releaseElement(s->shown);
    }
    // Gives back the layer requests (queued: the element may be in its own destruction).
    for (const auto& held : m_layerItems)
        if (held)
            LayerRequests::releaseQueued(held.data(), this);
    for (Uniform& u : m_uniforms)
        releaseUniform(u);
}

void ShaderItem::setSource(QQuickItem* source)
{
    if (source == m_source)
        return;
    disconnect(m_sourceW);
    disconnect(m_sourceH);
    m_source = source;
    if (source) {
        m_sourceW = connect(source, &QQuickItem::widthChanged, this, &ShaderItem::updateImplicitSize);
        m_sourceH = connect(source, &QQuickItem::heightChanged, this, &ShaderItem::updateImplicitSize);
    }
    updateImplicitSize();
    m_layersDirty = true;
    refreshLayerRequests();
    update();
    emit sourceChanged();
}

void ShaderItem::updateImplicitSize()
{
    if (m_source)
        setImplicitSize(m_source->width(), m_source->height());
    else
        setImplicitSize(0, 0);
}

bool ShaderItem::setShader(int stage, uint64_t id, std::string& err)
{
    ShaderSlot& s = stage == VLQT_SHADER_STAGE_VERTEX ? m_vert : m_frag;
    if (id == s.want)
        return true;
    if (id && id != s.shown) {
        if (res::kindOfId(id, nullptr) != VLQT_RESOURCE_KIND_SHADER) {
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

void ShaderItem::setChainPass(bool on)
{
    if (on == m_chain)
        return;
    m_chain = on;
    rebuild();
    update();
}

void ShaderItem::setTexelSamplers(std::vector<QByteArray> names)
{
    m_texelSamplers = std::move(names);
    rebuild();
    update();
}

void ShaderItem::setOptionalUniforms(const std::vector<QByteArray>& names)
{
    if (names == m_optional)
        return;
    m_optional = names;
    rebuild();
}

bool ShaderItem::declares(const QByteArray& name) const
{
    return std::find(m_declared.begin(), m_declared.end(), name) != m_declared.end();
}

void ShaderItem::setBlending(bool on)
{
    if (on == m_blending)
        return;
    m_blending = on;
    update();
}

void ShaderItem::setMeshResolution(int x, int y)
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

bool ShaderItem::advance(ShaderSlot& s)
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

void ShaderItem::advanceAll()
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

void ShaderItem::afterResourceFrame()
{
    const uint64_t content = res::contentEpoch(), failure = res::failureEpoch();
    if (content == s_contentEpoch && failure == s_failureEpoch)
        return;
    s_contentEpoch = content;
    s_failureEpoch = failure;
    for (ShaderItem* item : s_items)
        item->advanceAll();
}

void ShaderItem::releaseUniform(Uniform& u)
{
    if (u.image)
        res::releaseElement(u.image);
    u.image = 0;
    u.imageContent.reset();
    u.imageTexture.reset();
    u.ref = nullptr;
}

void ShaderItem::beginUniforms(bool clear)
{
    if (!clear)
        return;
    for (Uniform& u : m_uniforms)
        releaseUniform(u);
    m_uniforms.clear();
    m_samplerStates.clear();
    m_layersDirty = true;
    m_structureChanged = true;
    m_uniformsChanged = true;
}

bool ShaderItem::setUniform(const char* name, int nameLen, quint16 vt, const unsigned char* value, QQuickItem* ref)
{
    const QByteArrayView key(name, nameLen);
    if (vt == VLQT_VALUE_TYPE_SAMPLER) { // own table: the texture of the same name keeps its entry
        quint64 st;
        std::memcpy(&st, value, 8);
        st |= quint64(1) << 63;
        auto sit = std::find_if(m_samplerStates.begin(), m_samplerStates.end(), [&](const auto& e) { return e.first == key; });
        if (sit == m_samplerStates.end()) {
            m_samplerStates.emplace_back(key.toByteArray(), st);
            m_structureChanged = true; // rebuild checks that the shader has this sampler
        } else if (sit->second != st) {
            sit->second = st;
        } else {
            return false;
        }
        m_layersDirty = true; // the layer requests follow the sampler states
        m_uniformsChanged = true;
        return true;
    }
    bool changed = false;
    auto it = std::find_if(m_uniforms.begin(), m_uniforms.end(), [&](const Uniform& u) { return u.name == key; });
    if (it == m_uniforms.end()) {
        m_uniforms.emplace_back();
        it = std::prev(m_uniforms.end());
        it->name = key.toByteArray();
        m_structureChanged = changed = true;
    }
    Uniform& u = *it;
    if (u.type != vt) {
        releaseUniform(u);
        u.type = vt;
        m_structureChanged = changed = true;
    }
    switch (vt) {
    case VLQT_VALUE_TYPE_F32: case VLQT_VALUE_TYPE_VEC2: case VLQT_VALUE_TYPE_VEC3: case VLQT_VALUE_TYPE_VEC4: case VLQT_VALUE_TYPE_MAT4:
        changed = assign(u.f, value, size_t(valueBytes(vt))) || changed;
        break;
    case VLQT_VALUE_TYPE_COLOR: { // straight on the wire, premultiplied like QML's ShaderEffect
        float c[4];
        std::memcpy(c, value, 16);
        c[0] *= c[3];
        c[1] *= c[3];
        c[2] *= c[3];
        changed = assign(u.f, c, 16) || changed;
        break;
    }
    case VLQT_VALUE_TYPE_I32:
        changed = assign(u.i, value, 4) || changed;
        break;
    case VLQT_VALUE_TYPE_IVEC2:
        changed = assign(u.i, value, 8) || changed;
        break;
    case VLQT_VALUE_TYPE_BOOL: {
        const qint32 b = value[0] != 0 ? 1 : 0;
        changed = assign(u.i, &b, 4) || changed;
        break;
    }
    case VLQT_VALUE_TYPE_REF:
        if (u.ref != ref) {
            u.ref = ref;
            // A texture child is drawn through its layer (hidden on REPARENT); the layer stays while it lives. A Group
            // goes through its frame proxy instead (Elements::syncTextureGroups after endUniforms).
            if (ref && !isGroup(ref))
                QQmlProperty(ref, QStringLiteral("layer.enabled")).write(true);
            m_structureChanged = changed = true;
        }
        break;
    case VLQT_VALUE_TYPE_U64: {
        uint64_t id;
        std::memcpy(&id, value, 8);
        if (id != u.image) {
            if (u.image)
                res::releaseElement(u.image);
            u.image = 0;
            u.imageContent.reset();
            u.imageTexture.reset();
            if (id && res::kindOfId(id, nullptr) == VLQT_RESOURCE_KIND_IMAGE && res::acquire(id) == res::okRc) {
                u.image = id;
                u.imageContent = res::image::ready(id);
            }
            u.badImage = id && !u.image;
            m_structureChanged = changed = true;
        }
        break;
    }
    default:
        break;
    }
    m_uniformsChanged = m_uniformsChanged || changed;
    return changed;
}

void ShaderItem::endUniforms()
{
    if (m_structureChanged) {
        m_structureChanged = false;
        m_refsChanged = true; // rare: the Scene re-collects the Groups among the REF uniforms
        m_layersDirty = true;
        rebuild();
    }
    refreshLayerRequests();
    // A repeated send of the same values repaints nothing.
    if (std::exchange(m_uniformsChanged, false))
        update();
}

void ShaderItem::rebuild()
{
    m_writes.clear();
    m_samplers.clear();
    m_ubufSize = 0;
    m_matrixOffset = m_opacityOffset = -1;
    m_texelOffset = m_dprOffset = m_texelBinding = -1;
    m_declared.clear();
    m_shaderChanged = true;
    QStringList errors;

    // last good version: a Failed want keeps the Ready content of the slot (held via shown); none -> nothing
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
            m_samplers.push_back({s.binding, -1, s.name == "source", s.name});
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
                if (u.type != VLQT_VALUE_TYPE_REF && u.type != VLQT_VALUE_TYPE_U64) {
                    errors << QStringLiteral("uniform '%1' is a sampler2D, got %2 (use Texture or Image)")
                                  .arg(name, QLatin1StringView(valueTypeName(u.type)));
                    continue;
                }
                if (u.type == VLQT_VALUE_TYPE_U64 && u.badImage)
                    errors << QStringLiteral("uniform '%1': unknown or released image resource").arg(name);
                if (u.type == VLQT_VALUE_TYPE_REF && u.ref) {
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
        // Sampler states of names the shader does not have: same leniency as for uniforms while a shader still bakes.
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

void ShaderItem::watchProvider(QSGTextureProvider* p)
{
    for (const auto& w : m_watched)
        if (w == p)
            return;
    m_watched.erase(std::remove_if(m_watched.begin(), m_watched.end(), [](const auto& w) { return w.isNull(); }),
        m_watched.end());
    m_watched.emplace_back(p);
    connect(p, &QSGTextureProvider::textureChanged, this, &QQuickItem::update, Qt::QueuedConnection);
    // A provider that goes (layer off, element deleted) must not stay in the material: refresh at the next sync. Direct
    // connection, relies on the basic loop (docs/design/render-loop.md, "Places that rely on the basic loop").
    connect(p, &QObject::destroyed, this, &QQuickItem::update);
}

quint64 ShaderItem::samplerState(const Sampler& s) const
{
    for (const auto& e : m_samplerStates)
        if (e.first == s.name)
            return e.second;
    return 0;
}

QQuickItem* ShaderItem::samplerElement(const Sampler& s) const
{
    QQuickItem* item = nullptr;
    if (s.source)
        item = m_source;
    else if (s.uniform >= 0 && m_uniforms[size_t(s.uniform)].type == VLQT_VALUE_TYPE_REF)
        item = m_uniforms[size_t(s.uniform)].ref;
    if (item) {
        if (QQuickItem* proxy = groupTextureSource(item))
            item = proxy;
    }
    return item;
}

void ShaderItem::refreshLayerRequests(bool force)
{
    if (!m_layersDirty && !force)
        return;
    m_layersDirty = false;
    // Elements this item asks something from now, the mip flags of all samplers on one element ORed (an image has no
    // layer: its mipmaps come from its load).
    auto& now = m_layerNow;
    now.clear();
    for (const Sampler& s : m_samplers) {
        const quint64 st = samplerState(s);
        QQuickItem* item = st ? samplerElement(s) : nullptr;
        if (!item)
            continue;
        const bool mip = ((st >> 8) & 0xff) != 0;
        const auto e = std::find_if(now.begin(), now.end(), [&](const auto& x) { return x.first == item; });
        if (e == now.end())
            now.emplace_back(item, mip);
        else
            e->second = e->second || mip;
    }
    const auto held = [&](QQuickItem* item) {
        return std::find_if(m_layerItems.begin(), m_layerItems.end(), [&](const auto& p) { return p == item; }) != m_layerItems.end();
    };
    // Defaults need no request (and no write to the layer), unless the element already holds one.
    now.erase(std::remove_if(now.begin(), now.end(), [&](const auto& x) { return !x.second && !held(x.first); }), now.end());
    for (const auto& [item, mip] : now)
        LayerRequests::request(item, this, mip);
    for (const auto& p : m_layerItems)
        if (p && std::find_if(now.begin(), now.end(), [&](const auto& x) { return x.first == p.data(); }) == now.end())
            LayerRequests::release(p, this);
    m_layerItems.clear();
    for (const auto& x : now)
        m_layerItems.emplace_back(x.first);
}

QSGTexture* ShaderItem::samplerTexture(Sampler& s)
{
    QQuickItem* item = nullptr;
    const Uniform* u = s.uniform >= 0 ? &m_uniforms[size_t(s.uniform)] : nullptr;
    if (s.source)
        item = m_source;
    else if (u && u->type == VLQT_VALUE_TYPE_REF)
        item = u->ref;
    if (item) {
        if (QQuickItem* proxy = groupTextureSource(item)) // a Group texture is its whole frame
            item = proxy;
        QSGTextureProvider* p = item->isTextureProvider() ? item->textureProvider() : nullptr;
        if (!p)
            return nullptr;
        watchProvider(p);
        return p->texture();
    }
    if (u && u->type == VLQT_VALUE_TYPE_U64 && u->imageContent) {
        auto& self = m_uniforms[size_t(s.uniform)];
        self.imageTexture = imageTextureFor(window(), self.imageContent);
        return self.imageTexture ? textureOf(*self.imageTexture) : nullptr;
    }
    return nullptr;
}

void ShaderItem::invalidateSceneGraph()
{
    for (Uniform& u : m_uniforms)
        u.imageTexture.reset();
}

void ShaderItem::releaseResources()
{
    invalidateSceneGraph();
}

void ShaderItem::geometryChange(const QRectF& newGeometry, const QRectF& oldGeometry)
{
    QQuickItem::geometryChange(newGeometry, oldGeometry);
    if (newGeometry.size() != oldGeometry.size())
        update();
}

void ShaderItem::itemChange(ItemChange change, const ItemChangeData& data)
{
    QQuickItem::itemChange(change, data);
    if (change == ItemDevicePixelRatioHasChanged || change == ItemSceneChange) {
        update(); // dpr of a chain pass
        emit devicePixelRatioChanged();
    }
}

QSGNode* ShaderItem::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    const float w = float(width()), h = float(height());
    if (!m_drawable || w <= 0 || h <= 0 || !window()) {
        delete old;
        return nullptr;
    }

    auto* node = static_cast<EffectNode*>(old);
    QSGNode::DirtyState dirty = QSGNode::DirtyMaterial;
    if (!node) {
        node = new EffectNode;
        m_shaderChanged = true;
    }
    if (!node->transparent) {
        QImage img(1, 1, QImage::Format_RGBA8888_Premultiplied);
        img.fill(Qt::transparent);
        node->transparent.reset(window()->createTextureFromImage(img, QQuickWindow::TextureHasAlphaChannel));
    }
    EffectMaterial& mat = node->mat();

    if (m_shaderChanged) {
        m_shaderChanged = false;
        mat.vs = m_vs;
        mat.fs = m_fs;
        mat.materialType = materialTypeFor(m_vs, m_fs);
        mat.matrixOffset = m_matrixOffset;
        mat.opacityOffset = m_opacityOffset;
        mat.ubuf.fill(0, m_ubufSize); // removed uniforms go back to 0; sized here only (no allocation per frame)
    }
    char* ub = mat.ubuf.data();
    for (const Write& wr : m_writes) {
        const Uniform& u = m_uniforms[size_t(wr.uniform)];
        const bool ints = u.type == VLQT_VALUE_TYPE_I32 || u.type == VLQT_VALUE_TYPE_IVEC2 || u.type == VLQT_VALUE_TYPE_BOOL;
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
        mat.tex[size_t(s.binding)] = t ? t : node->transparent.get();
        mat.state[size_t(s.binding)] = samplerState(s);
        if (s.binding == m_texelBinding)
            mat.sourceBinding = s.binding;
    }
    mat.texelOffset = m_texelOffset;
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
