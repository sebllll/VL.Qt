#include "effects/effect_chain.h"

#include "effects/effect_chain_item.h"
#include "effects/shader_item.h"
#include "elements/elements.h"
#include "elements/geometry.h"
#include "elements/group_item.h"
#include "shader_resource.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QUrl>
#include <QtGui/QColor>
#include <QtGui/QMatrix4x4>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQml/QQmlListReference>
#include <QtQml/QQmlProperty>
#include <QtQml/qqml.h>
#include <QtQuick/QQuickWindow>

#include <algorithm>
#include <cmath>
#include <cstring>

Q_LOGGING_CATEGORY(lcEffectChain, "vlqt.effectchain")

namespace vlqt {

namespace {

// Stage of a MULTI link: fx (MultiEffect) has the size of the input and lies where the input lies (input = the root
// for the first link, whose layer texture covers the element's (0, 0, w, h); otherwise the previous stage, whose
// x/y/size are its effect area in root coordinates); the stage covers fx.itemRect (input + padding), its area in root
// = element coordinates. frameLeft/frameTop (first link only): how far an auto-sized Group's frame reaches left of /
// above its origin; Qt's layer covers (0, 0, width, height) of the item, so that part goes into the left/top padding
// (added to the automatic and the manual padding): the effect area includes the whole frame, unclipped. Mask: the
// Binding points the frame's layer.sourceRect at the effect area of this link (protocol.h mask rule), maskEnabled
// follows the frame; MultiEffect keeps a raw pointer to maskSource, so the frame is detached (null) before it goes.
// The shader-switching values (blurEnabled, shadowEnabled, blurMax) are written by EffectChain::writeLink on change only.
// fx stays at (0, 0) of an inner Item that is moved instead: MultiEffect reports itemRect again on every geometry change
// of its own, so a position bound to it would be a binding loop.
constexpr const char* kMultiStageQml =
    "import QtQuick\n"
    "import QtQuick.Effects\n"
    "Item {\n"
    "  id: s\n"
    "  property Item input: null\n"
    "  property bool first: true\n"
    "  property Item maskFrame: null\n"
    "  property real frameLeft: 0\n"
    "  property real frameTop: 0\n"
    "  property rect pad: Qt.rect(0, 0, 0, 0)\n"
    "  readonly property Item fx: e\n"
    "  readonly property real inX: first || !input ? 0 : input.x\n"
    "  readonly property real inY: first || !input ? 0 : input.y\n"
    "  readonly property rect area: Qt.rect(inX + e.itemRect.x, inY + e.itemRect.y, e.itemRect.width, e.itemRect.height)\n"
    "  x: area.x; y: area.y; width: area.width; height: area.height\n"
    "  Item {\n"
    "    x: s.inX - s.area.x; y: s.inY - s.area.y\n"
    "    MultiEffect {\n"
    "      id: e\n"
    "      width: s.input ? s.input.width : 0; height: s.input ? s.input.height : 0\n"
    "      paddingRect: Qt.rect(s.pad.x + s.frameLeft, s.pad.y + s.frameTop, s.pad.width, s.pad.height)\n"
    "      maskEnabled: s.maskFrame !== null; maskSource: s.maskFrame\n"
    "      Binding {\n"
    "        target: s.maskFrame ? s.maskFrame.layer : null; property: \"sourceRect\"; when: s.maskFrame !== null\n"
    "        restoreMode: Binding.RestoreNone\n"
    "        value: s.area\n"
    "      }\n"
    "    }\n"
    "  }\n"
    "}\n";

// Stage of a SHADER link (pass driver, protocol.h "Effect chain"): the stage covers its input plus the link's padding
// (pad: x left, y top, w right, h bottom; first link plus the Group frame, frameLeft/frameTop as for MULTI). The Binding
// points the sourceRect of the input's layer (tex: the element's layer source for the first link, else the previous
// stage) at that area, so the input arrives padded and transparent around it, 1:1 over UV 0..1 of the passes. Always
// through layer.sourceRect, never on the layer's ShaderEffectSource itself: QQuickItemLayer caches the value and skips an
// equal write, and a later MultiEffect on the same layer sets its padded rect there (else it kept our rect, hard edge).
// SINGLE: e1 draws. TWO_PASS: e1 hidden with its own layer (texture provider of e2), e2 draws. PYRAMID: e1 = composite
// draws, the pyramid passes are created by EffectChain::layoutPyramid under the stage, e2 is unused. active false: retired.
constexpr const char* kPassStageQml =
    "import QtQuick\n"
    "import VLQt.EffectChain\n"
    "Item {\n"
    "  id: s\n"
    "  property Item input: null\n"
    "  property Item tex: null\n"
    "  property bool first: true\n"
    "  property bool active: true\n"
    "  property bool twoPass: false\n"
    "  property real frameLeft: 0\n"
    "  property real frameTop: 0\n"
    "  property rect pad: Qt.rect(0, 0, 0, 0)\n"
    "  readonly property Item fx: e1\n"
    "  readonly property Item fx2: e2\n"
    "  readonly property real padL: pad.x + frameLeft\n"
    "  readonly property real padT: pad.y + frameTop\n"
    "  readonly property real inW: input ? input.width : 0\n"
    "  readonly property real inH: input ? input.height : 0\n"
    "  readonly property rect area: Qt.rect((first || !input ? 0 : input.x) - padL, (first || !input ? 0 : input.y) - padT,\n"
    "    inW + padL + pad.width, inH + padT + pad.height)\n"
    "  x: area.x; y: area.y; width: area.width; height: area.height\n"
    "  Binding {\n"
    "    target: !s.tex ? null : s.first ? s.tex.sourceItem.layer : s.tex.layer; property: \"sourceRect\"; when: s.active && s.tex !== null\n"
    "    restoreMode: Binding.RestoreNone\n"
    "    value: Qt.rect(-s.padL, -s.padT, s.area.width, s.area.height)\n"
    "  }\n"
    "  VlqtShaderPass { id: e1; width: s.width; height: s.height; visible: !s.twoPass; layer.enabled: s.twoPass }\n"
    "  VlqtShaderPass { id: e2; width: s.width; height: s.height; visible: s.twoPass }\n"
    "}\n";

// PYRAMID (doc 21): at most this many steps down (n); 16 halvings take any texture below 4 px.
constexpr int kMaxLevels = 16;

// layer.effect needs a component with a creation context: an inline Component of a holder object (one per engine).
constexpr const char* kRootQml =
    "import QtQuick\n"
    "import VLQt.EffectChain\n"
    "QtObject { property Component fx: Component { VlqtEffectChain {} } }\n";

// MultiEffect property per MULTI key (index = prop - 833); nullptr: on the stage (PADDING_RECT pad, MASK_SOURCE maskFrame).
constexpr const char* kMultiNames[kEffectMultiKeys] = {
    "blurEnabled", "blur", "blurMax", "blurMultiplier",
    "shadowEnabled", "shadowColor", "shadowHorizontalOffset", "shadowVerticalOffset", "shadowBlur", "shadowScale",
    "brightness", "contrast", "saturation", "colorization", "colorizationColor",
    "autoPaddingEnabled", nullptr, nullptr,
    "maskThresholdMin", "maskSpreadAtMin", "maskThresholdMax", "maskSpreadAtMax", "maskInverted",
};

constexpr int multiIndex(quint16 prop) { return prop - VLQT_MULTI_EFFECT_KEY_BLUR_ENABLED; }

// Qt's defaults of MultiEffect (protocol.h "Effect chain": a value missing in a send is back at its default).
const QVariant& multiDefault(int i)
{
    static const QVariant defaults[kEffectMultiKeys] = {
        false, 0.0, 32, 0.0,
        false, QColor(Qt::black), 0.0, 0.0, 1.0, 1.0,
        0.0, 0.0, 0.0, 0.0, QColor(Qt::red),
        true, QVariant(), QVariant(),
        0.0, 0.0, 1.0, 0.0, false,
    };
    return defaults[i];
}

QVariant toVariant(const EffectValue& v)
{
    switch (v.type) {
    case VLQT_VALUE_TYPE_BOOL: return v.b;
    case VLQT_VALUE_TYPE_I32: return int(v.i);
    case VLQT_VALUE_TYPE_F32: return double(v.f[0]); // QML real
    case VLQT_VALUE_TYPE_COLOR: return QColor::fromRgbF(v.f[0], v.f[1], v.f[2], v.f[3]);
    case VLQT_VALUE_TYPE_RECTF: return QRectF(v.f[0], v.f[1], v.f[2], v.f[3]);
    default: return {};
    }
}

QQmlComponent* compile(QQmlEngine* engine, QQmlComponent*& slot, const char* qml, const char* url, std::string& err)
{
    if (!slot) {
        slot = new QQmlComponent(engine);
        slot->setData(QByteArray(qml), QUrl(QString::fromLatin1(url)));
    }
    if (slot->isError()) {
        err = std::string(url) + ": " + slot->errorString().toStdString();
        return nullptr;
    }
    return slot;
}

void setLayerEnabled(QQuickItem* item, bool on)
{
    // Once per structure or source change, not per frame: QQmlProperty resolves the grouped "layer.enabled".
    QQmlProperty(item, QStringLiteral("layer.enabled")).write(on);
}

void registerTypes()
{
    static const bool registered = [] {
        qmlRegisterType<EffectChainItem>("VLQt.EffectChain", 1, 0, "VlqtEffectChain");
        qmlRegisterType<ShaderItem>("VLQt.EffectChain", 1, 0, "VlqtShaderPass");
        return true;
    }();
    Q_UNUSED(registered);
}

// Names QtNative sets in a SHADER link (protocol.h "Effect chain"): an entry from C# is an error line.
bool reservedName(const QByteArray& n)
{
    return n == "qt_Matrix" || n == "qt_Opacity" || n == "source" || n == "texelSize" || n == "dpr" || n == "weight" || n == "elementRect"
        || n == "passIndex" || n == "base" || n == "glow" || n == "shadow";
}

// Wire layout of a value (like SET) for ShaderItem::setUniform; out holds at least 64 bytes.
void wireValue(const EffectValue& v, unsigned char* out)
{
    switch (v.type) {
    case VLQT_VALUE_TYPE_BOOL: out[0] = v.b ? 1 : 0; break;
    case VLQT_VALUE_TYPE_I32: std::memcpy(out, &v.i, 4); break;
    case VLQT_VALUE_TYPE_IVEC2: std::memcpy(out, &v.i, 4); std::memcpy(out + 4, &v.i2, 4); break;
    case VLQT_VALUE_TYPE_U64: std::memcpy(out, &v.u, 8); break;
    case VLQT_VALUE_TYPE_SAMPLER: std::memcpy(out, &v.u, 8); break;
    case VLQT_VALUE_TYPE_REF: std::memcpy(out, &v.ref, 4); break;
    default: std::memcpy(out, v.f, sizeof v.f); break; // f32, vec2/3/4, color, mat4
    }
}

// Optional names of a SINGLE / TWO_PASS pass: the shader may leave them out (setOptionalUniforms copies on change only).
const std::vector<QByteArray> kPassOptional = {QByteArrayLiteral("elementRect"), QByteArrayLiteral("passIndex")};

// Same names and types in the same order: a send with values only (no layout rebuild in the passes).
bool sameUniformTable(const std::vector<EffectUniform>& a, const std::vector<EffectUniform>& b)
{
    if (a.size() != b.size())
        return false;
    for (size_t i = 0; i < a.size(); ++i)
        if (a[i].name != b[i].name || a[i].value.type != b[i].value.type)
            return false;
    return true;
}

// Back to a fresh link of a kind, keeping the capacity of its tables (the scratch of EffectChain::apply).
void resetLink(EffectChain::Link& l, quint16 kind)
{
    l.kind = kind;
    l.padding = QRectF();
    l.stage.clear();
    l.fx = l.fx2 = nullptr;
    l.multi.mask = l.multi.maskFrame = nullptr;
    l.multi.maskFrameXf = nullptr;
    l.shader.templ = 0;
    l.shader.shader = l.shader.shader2 = 0;
    l.shader.strength = 0.f;
    l.shader.uniforms.clear();
    l.shader.problems.clear();
    l.shader.shaderError.clear();
    l.shader.elementRect = {};
    l.shader.elementRectKnown = false;
    l.pyramid.down.clear();
    l.pyramid.up.clear();
    l.pyramid.levels = 0;
    l.pyramid.px = QSize();
    l.pyramid.dpr = 0;
}

// Sets one uniform of a pass (between beginUniforms and endUniforms).
bool setF32(ShaderItem* p, const char* name, float v)
{
    unsigned char value[4];
    std::memcpy(value, &v, 4);
    return p->setUniform(name, int(std::strlen(name)), VLQT_VALUE_TYPE_F32, value, nullptr);
}

bool setVec4(ShaderItem* p, const char* name, float x, float y, float w, float h)
{
    const float v[4] = {x, y, w, h};
    unsigned char value[16];
    std::memcpy(value, v, 16);
    return p->setUniform(name, int(std::strlen(name)), VLQT_VALUE_TYPE_VEC4, value, nullptr);
}

bool setRef(ShaderItem* p, const char* name, QQuickItem* item)
{
    const unsigned char value[4] = {};
    return p->setUniform(name, int(std::strlen(name)), VLQT_VALUE_TYPE_REF, value, item);
}

// Shared builtin shader of the pyramid passes (host reference held by EffectComponents).
quint64 builtinShader(quint64& slot, const char* path)
{
    if (slot)
        return slot;
    uint64_t id = 0;
    if (res::create(VLQT_RESOURCE_KIND_SHADER, &id) != res::okRc || res::shader::load(id, path, VLQT_SHADER_STAGE_FRAGMENT) != res::okRc) {
        qCWarning(lcEffectChain, "effect chain: %s could not be loaded", path);
        if (id)
            res::release(id);
        return 0;
    }
    return slot = id;
}

// Pyramid step of a radius (doc 21): sigma = radius x dpr / 2 pixels, L = 1/2 log2(1 + 6/7 sigma^2).
double pyramidLevel(float radius, qreal dpr)
{
    const double s = double(radius) * dpr / 2;
    return 0.5 * std::log2(1 + s * s * 6 / 7);
}

} // namespace

EffectComponents::~EffectComponents()
{
    for (quint64 id : {pyramidDown, pyramidUp})
        if (id)
            res::release(id);
    delete rootHolder;
    delete root;
    delete multi;
    delete pass;
}

EffectChain::EffectChain(EffectComponents& components, QQuickItem* element)
    : QObject(element), m_components(components)
{
}

QQmlComponent* EffectChain::rootComponent(std::string& err)
{
    registerTypes();
    if (!m_components.rootHolder) {
        QQmlComponent* c = compile(m_components.engine, m_components.root, kRootQml, "vlqt_effect_chain.qml", err);
        m_components.rootHolder = c ? c->create() : nullptr;
        if (!m_components.rootHolder) {
            if (err.empty())
                err = "effect chain root could not be created: " + c->errorString().toStdString();
            return nullptr;
        }
        QQmlEngine::setObjectOwnership(m_components.rootHolder, QQmlEngine::CppOwnership);
    }
    return m_components.rootHolder->property("fx").value<QQmlComponent*>();
}

bool EffectChain::apply(const EffectChainDesc& desc, const std::vector<QQuickItem*>& masks,
    std::vector<QPointer<QQuickItem>>& retired, std::vector<DroppedMask>& dropped, std::string& err)
{
    // The same-structure send (an animated pin: every frame) allocates nothing once the scratch has its capacity.
    std::vector<Link>& next = m_next;
    next.resize(desc.links.size());
    for (size_t i = 0; i < next.size(); ++i) {
        const EffectLinkDesc& d = desc.links[i];
        Link& l = next[i];
        resetLink(l, d.kind);
        if (d.kind == VLQT_EFFECT_CHAIN_LINK_MULTI) {
            for (int k = 0; k < kEffectMultiKeys; ++k)
                l.multi.values[k] = multiDefault(k);
            for (const EffectMultiEntry& e : d.multi) {
                if (e.prop == VLQT_MULTI_EFFECT_KEY_PADDING_RECT)
                    l.padding = QRectF(e.value.f[0], e.value.f[1], e.value.f[2], e.value.f[3]);
                else if (e.prop != VLQT_MULTI_EFFECT_KEY_MASK_SOURCE)
                    l.multi.values[multiIndex(e.prop)] = toVariant(e.value);
            }
            l.multi.mask = i < masks.size() ? masks[i] : nullptr;
        } else {
            l.shader.templ = d.templ;
            l.shader.shader = d.shaders.empty() ? 0 : d.shaders[0]; // PYRAMID: prefilter
            if (d.templ == VLQT_EFFECT_TEMPLATE_PYRAMID)
                l.shader.shader2 = d.shaders.size() > 1 ? d.shaders[1] : 0;
            float pad[4];
            bool negative = false;
            for (int k = 0; k < 4; ++k) {
                pad[k] = d.padding[k] >= 0.f ? d.padding[k] : 0.f; // NaN too
                negative = negative || !(d.padding[k] >= 0.f);
            }
            if (negative)
                l.shader.problems << QStringLiteral("padding must not be negative (0 is used)");
            l.padding = QRectF(pad[0], pad[1], pad[2], pad[3]);
            l.shader.strength = d.strength >= 0.f ? d.strength : 0.f;
            if (!(d.strength >= 0.f))
                l.shader.problems << QStringLiteral("strength must not be negative (0 is used)");
            for (const EffectUniform& u : d.uniforms) {
                // The text is built for a problem only.
                if (reservedName(u.name))
                    l.shader.problems << QStringLiteral("uniform '%1' is reserved (set by VL.Qt)").arg(QString::fromUtf8(u.name));
                else if (u.refError)
                    l.shader.problems << QStringLiteral("uniform '%1': %2").arg(QString::fromUtf8(u.name), QString::fromUtf8(u.refError));
                else
                    l.shader.uniforms.push_back(u);
            }
        }
    }
    // A mask that stays attached keeps its frame, also in another link.
    for (const Link& o : m_links) {
        if (!o.multi.mask)
            continue;
        auto it = std::find_if(next.begin(), next.end(), [&](const Link& n) { return n.multi.mask == o.multi.mask; });
        if (it != next.end()) {
            it->multi.maskFrame = o.multi.maskFrame;
            it->multi.maskFrameXf = o.multi.maskFrameXf;
        } else {
            dropped.push_back({o.multi.mask, o.multi.maskFrame});
        }
    }

    bool same = m_root && next.size() == m_links.size();
    for (size_t i = 0; same && i < next.size(); ++i)
        same = next[i].kind == m_links[i].kind && next[i].shader.templ == m_links[i].shader.templ && m_links[i].stage;
    if (same) {
        // Same structure: values only, where they changed (no new item, no shader switch). Swapped first: a PYRAMID
        // stage lays out again on a size change of its own (layoutPyramid finds its link in m_links).
        for (size_t i = 0; i < next.size(); ++i) {
            Link& n = next[i];
            const Link& o = m_links[i];
            n.stage = o.stage;
            n.fx = o.fx;
            n.fx2 = o.fx2;
            n.pyramid = o.pyramid;
            n.shader.elementRect = o.shader.elementRect;
            n.shader.elementRectKnown = o.shader.elementRectKnown;
        }
        m_links.swap(next);
        bool errorsChanged = false;
        size_t firstChanged = m_links.size();
        for (size_t i = 0; i < m_links.size(); ++i) {
            if (writeLink(m_links[i], &next[i]) && firstChanged > i)
                firstChanged = i;
            // The error text depends on these (the pass errors reach updateError by their errorChanged signal).
            const ShaderPart& n = m_links[i].shader;
            const ShaderPart& o = next[i].shader;
            errorsChanged = errorsChanged || n.problems != o.problems || n.shaderError != o.shaderError
                || (n.templ == VLQT_EFFECT_TEMPLATE_PYRAMID && !sameUniformTable(n.uniforms, o.uniforms));
        }
        updatePasses(firstChanged);
        if (errorsChanged)
            updateError();
        return true;
    }
    for (Link& o : m_links) {
        if (!o.stage)
            continue;
        if (o.kind == VLQT_EFFECT_CHAIN_LINK_MULTI)
            o.stage->setProperty("maskFrame", QVariant::fromValue<QQuickItem*>(nullptr)); // before the frame may go
        else
            o.stage->setProperty("active", false); // its Binding leaves the input's sourceRect to the new stages
        o.stage->setVisible(false);
        retired.emplace_back(o.stage);
    }
    m_links.swap(next);
    const bool ok = !m_root || buildStages(err);
    updateError();
    return ok;
}

bool EffectChain::samples(const QQuickItem* item) const
{
    for (const Link& l : m_links)
        for (const EffectUniform& u : l.shader.uniforms)
            if (u.refItem == item)
                return true;
    return false;
}

void EffectChain::syncMaskFrame(size_t i)
{
    Link& l = m_links[i];
    if (l.stage && l.kind == VLQT_EFFECT_CHAIN_LINK_MULTI)
        l.stage->setProperty("maskFrame", QVariant::fromValue<QQuickItem*>(l.multi.maskFrame));
}

void EffectChain::attach(QQuickItem* root)
{
    m_root = root;
    std::string err;
    if (!buildStages(err))
        qCWarning(lcEffectChain, "effect chain: %s", err.c_str());
    updateError();
}

bool EffectChain::buildStages(std::string& err)
{
    registerTypes();
    QQuickItem* root = m_root;
    auto* src = qobject_cast<QQuickItem*>(static_cast<EffectChainItem*>(root)->source());
    QQuickItem* prev = nullptr;
    for (size_t i = 0; i < m_links.size(); ++i) {
        Link& l = m_links[i];
        const bool multi = l.kind == VLQT_EFFECT_CHAIN_LINK_MULTI;
        QQmlComponent* c = multi
            ? compile(m_components.engine, m_components.multi, kMultiStageQml, "vlqt_effect_multi.qml", err)
            : compile(m_components.engine, m_components.pass, kPassStageQml, "vlqt_effect_pass.qml", err);
        QObject* obj = c ? c->create() : nullptr;
        auto* stage = qobject_cast<QQuickItem*>(obj);
        if (!stage) {
            delete obj;
            if (err.empty())
                err = "effect stage could not be created: " + c->errorString().toStdString();
            return false;
        }
        QQmlEngine::setObjectOwnership(stage, QQmlEngine::CppOwnership);
        stage->setParent(root); // dies with the root (the layer deletes it)
        stage->setParentItem(root);
        stage->setProperty("first", i == 0);
        stage->setProperty("input", QVariant::fromValue<QQuickItem*>(prev ? prev : root));
        l.stage = stage;
        l.fx = stage->property("fx").value<QQuickItem*>();
        QQuickItem* input = prev ? prev : src;
        if (multi) {
            l.fx->setProperty("source", QVariant::fromValue<QQuickItem*>(input));
        } else {
            stage->setProperty("tex", QVariant::fromValue<QQuickItem*>(input));
            stage->setProperty("twoPass", l.shader.templ == VLQT_EFFECT_TEMPLATE_TWO_PASS);
            auto* p1 = static_cast<ShaderItem*>(l.fx);
            p1->setChainPass(true);
            p1->setSource(input);
            connect(p1, &ShaderItem::errorChanged, this, &EffectChain::updateError);
            if (l.shader.templ == VLQT_EFFECT_TEMPLATE_PYRAMID) {
                // p1 = composite; its texelSize is the one of the glow / shadow texture. The passes follow the stage.
                p1->setTexelSamplers({QByteArrayLiteral("glow"), QByteArrayLiteral("shadow")});
                connect(p1, &ShaderItem::rebuilt, this, &EffectChain::updateError);
                const QPointer<QQuickItem> st = stage;
                const auto relayout = [this, st] {
                    for (Link& x : m_links)
                        if (x.stage == st) {
                            layoutPyramid(x);
                            return;
                        }
                };
                connect(stage, &QQuickItem::widthChanged, stage, relayout);
                connect(stage, &QQuickItem::heightChanged, stage, relayout);
                connect(p1, &ShaderItem::devicePixelRatioChanged, stage, relayout);
            } else {
                l.fx2 = stage->property("fx2").value<QQuickItem*>();
                auto* p2 = static_cast<ShaderItem*>(l.fx2);
                p2->setChainPass(true);
                p2->setSource(p1);
                // elementRect follows the stage area (grows with the padding of earlier links) and the element's size
                const QPointer<QQuickItem> st = stage;
                const auto place = [this, st] {
                    for (Link& x : m_links)
                        if (x.stage == st) {
                            writeElementRect(x);
                            return;
                        }
                };
                for (auto sig : {&QQuickItem::xChanged, &QQuickItem::yChanged, &QQuickItem::widthChanged, &QQuickItem::heightChanged})
                    connect(stage, sig, stage, place);
                connect(root, &QQuickItem::widthChanged, stage, place);
                connect(root, &QQuickItem::heightChanged, stage, place);
            }
        }
        writeLink(l, nullptr);
        const bool last = i + 1 == m_links.size();
        stage->setVisible(last);
        if (!last)
            setLayerEnabled(stage, true); // texture provider of the next link
        prev = stage;
    }
    if (m_built)
        m_built(m_links.empty() ? nullptr : m_links.front().stage.data());
    return true;
}

bool EffectChain::writeLink(Link& l, const Link* old)
{
    if (!l.stage)
        return false;
    bool changed = false;
    if (l.kind == VLQT_EFFECT_CHAIN_LINK_MULTI) {
        if (!m_components.resolved) {
            const QMetaObject* mo = l.fx->metaObject();
            for (int k = 0; k < kEffectMultiKeys; ++k)
                if (kMultiNames[k])
                    if (const int idx = mo->indexOfProperty(kMultiNames[k]); idx >= 0)
                        m_components.multiProps[k] = mo->property(idx);
            m_components.resolved = true;
        }
        for (int k = 0; k < kEffectMultiKeys; ++k) {
            const QMetaProperty& p = m_components.multiProps[k];
            if (!p.isValid())
                continue;
            // Only on change: blurEnabled/shadowEnabled/blurMax switch the MultiEffect's shaders.
            if (l.multi.values[k] == (old ? old->multi.values[k] : multiDefault(k)))
                continue;
            changed = true;
            if (!p.write(l.fx, l.multi.values[k]))
                qCWarning(lcEffectChain, "writing MultiEffect.%s failed", p.name());
        }
        if (l.multi.maskFrame != (old ? old->multi.maskFrame : nullptr)) {
            changed = true;
            l.stage->setProperty("maskFrame", QVariant::fromValue<QQuickItem*>(l.multi.maskFrame));
        }
    } else {
        changed = writeShaderLink(l, old);
    }
    if (l.padding != (old ? old->padding : QRectF())) {
        changed = true;
        l.stage->setProperty("pad", l.padding);
    }
    return changed;
}

void EffectChain::updatePasses(size_t first)
{
    for (size_t i = first; i < m_links.size(); ++i) {
        const Link& l = m_links[i];
        if (!l.stage || l.kind != VLQT_EFFECT_CHAIN_LINK_SHADER)
            continue;
        if (l.fx)
            l.fx->update();
        if (l.fx2)
            l.fx2->update();
        for (const std::vector<ShaderItem*>* pool : {&l.pyramid.down, &l.pyramid.up})
            for (ShaderItem* p : *pool)
                p->update();
    }
}

bool EffectChain::writeShaderLink(Link& l, const Link* old)
{
    if (l.shader.templ == VLQT_EFFECT_TEMPLATE_PYRAMID)
        return writePyramidLink(l, old);
    auto* p1 = static_cast<ShaderItem*>(l.fx);
    auto* p2 = static_cast<ShaderItem*>(l.fx2);
    const bool two = l.shader.templ == VLQT_EFFECT_TEMPLATE_TWO_PASS;
    bool changed = false;
    if (!old || old->shader.shader != l.shader.shader) {
        changed = true;
        std::string err, ignored;
        l.shader.shaderError.clear();
        if (!p1->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shader.shader, err)) {
            l.shader.shaderError = QString::fromStdString(err);
            p1->setShader(VLQT_SHADER_STAGE_FRAGMENT, 0, ignored); // pass-through
        }
        if (two)
            p2->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shader.shaderError.isEmpty() ? l.shader.shader : 0, ignored);
    } else {
        l.shader.shaderError = old->shader.shaderError;
    }
    // Same names and types as before: values only (no layout rebuild in the passes).
    const bool sameTable = old && sameUniformTable(old->shader.uniforms, l.shader.uniforms);
    if (!sameTable)
        l.shader.elementRectKnown = false; // beginUniforms(true) below drops elementRect with the rest of the table
    unsigned char value[sizeof(EffectValue::f)];
    for (ShaderItem* p : {p1, p2}) {
        if (p == p2 && !two)
            break;
        p->setOptionalUniforms(kPassOptional);
        p->beginUniforms(!sameTable);
        changed = changed || !sameTable;
        for (const EffectUniform& u : l.shader.uniforms) {
            wireValue(u.value, value);
            changed = p->setUniform(u.name.constData(), int(u.name.size()), u.value.type, value, u.refItem) || changed;
        }
        // passIndex (protocol.h): 0 in pass 1 (and SINGLE), 1 in pass 2 of TWO_PASS; written only if the shader declares it.
        const float passIndex = p == p2 ? 1.f : 0.f;
        std::memcpy(value, &passIndex, 4);
        changed = p->setUniform("passIndex", 9, VLQT_VALUE_TYPE_F32, value, nullptr) || changed;
        p->endUniforms();
    }
    return writeElementRect(l) || changed;
}

// elementRect (protocol.h): the element's rectangle (0, 0, root width, root height) in UV of the stage area, which is placed
// in element coordinates (x, y negative where earlier links grew it). A texture that sits on the element maps itself with it.
bool EffectChain::writeElementRect(Link& l)
{
    if (!l.stage || !m_root || l.kind != VLQT_EFFECT_CHAIN_LINK_SHADER || l.shader.templ == VLQT_EFFECT_TEMPLATE_PYRAMID)
        return false;
    const qreal w = l.stage->width(), h = l.stage->height();
    if (w <= 0 || h <= 0)
        return false;
    const std::array<float, 4> rect = {float(-l.stage->x() / w), float(-l.stage->y() / h), float(m_root->width() / w), float(m_root->height() / h)};
    if (l.shader.elementRectKnown && l.shader.elementRect == rect)
        return false; // the passes hold it already: no write, no repaint
    l.shader.elementRect = rect;
    l.shader.elementRectKnown = true;
    for (QQuickItem* item : {l.fx, l.fx2}) {
        if (!item || (item == l.fx2 && l.shader.templ != VLQT_EFFECT_TEMPLATE_TWO_PASS))
            continue;
        auto* p = static_cast<ShaderItem*>(item);
        p->beginUniforms(false);
        setVec4(p, "elementRect", rect[0], rect[1], rect[2], rect[3]);
        p->endUniforms();
    }
    return true;
}

// PYRAMID: prefilter (D1) and composite get the C# table, each takes what it declares; the composite also gets weight
// and its glow / shadow input (U1, or D1 for a single step).
bool EffectChain::writePyramidLink(Link& l, const Link* old)
{
    auto* c = static_cast<ShaderItem*>(l.fx);
    layoutPyramid(l); // the first write creates the passes
    if (l.pyramid.down.empty())
        return false;
    ShaderItem* d1 = l.pyramid.down[0];
    bool changed = false;
    if (!old || old->shader.shader != l.shader.shader || old->shader.shader2 != l.shader.shader2) {
        changed = true;
        std::string err, ignored;
        QStringList errors;
        if (!d1->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shader.shader, err)) {
            errors << QStringLiteral("prefilter: ") + QString::fromStdString(err);
            d1->setShader(VLQT_SHADER_STAGE_FRAGMENT, 0, ignored); // pass-through
        }
        err.clear();
        if (!c->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shader.shader2, err)) {
            errors << QStringLiteral("composite: ") + QString::fromStdString(err);
            c->setShader(VLQT_SHADER_STAGE_FRAGMENT, 0, ignored);
        }
        l.shader.shaderError = errors.join(QStringLiteral("; "));
    } else {
        l.shader.shaderError = old->shader.shaderError;
    }
    const bool sameTable = old && sameUniformTable(old->shader.uniforms, l.shader.uniforms);
    if (!sameTable) {
        changed = true;
        std::vector<QByteArray> names;
        for (const EffectUniform& u : l.shader.uniforms)
            names.push_back(u.name);
        d1->setOptionalUniforms(names);
        names.push_back(QByteArrayLiteral("glow"));
        names.push_back(QByteArrayLiteral("shadow"));
        c->setOptionalUniforms(names);
    }
    unsigned char value[sizeof(EffectValue::f)];
    const double level = pyramidLevel(l.shader.strength, l.pyramid.dpr);
    QQuickItem* top = l.pyramid.levels > 1 ? static_cast<QQuickItem*>(l.pyramid.up[0]) : d1;
    for (ShaderItem* p : {d1, c}) {
        p->beginUniforms(!sameTable);
        for (const EffectUniform& u : l.shader.uniforms) {
            wireValue(u.value, value);
            changed = p->setUniform(u.name.constData(), int(u.name.size()), u.value.type, value, u.refItem) || changed;
        }
        if (p == c) {
            changed = setF32(c, "weight", float(std::clamp(level, 0.0, 1.0))) || changed;
            changed = setRef(c, "glow", top) || changed;
            changed = setRef(c, "shadow", top) || changed;
        }
        p->endUniforms();
    }
    if (!old || old->shader.strength != l.shader.strength)
        changed = writePyramidWeights(l) || changed;
    return changed;
}

void EffectChain::layoutPyramid(Link& l)
{
    QQuickItem* stage = l.stage;
    if (!stage || !l.fx)
        return;
    // Without a window only the first layout (the passes must exist for writePyramidLink); a stage leaving its window
    // (layer off, root going) keeps its passes as they are.
    if (!stage->window() && l.pyramid.levels > 0)
        return;
    auto* c = static_cast<ShaderItem*>(l.fx);
    const qreal dpr = stage->window() ? stage->window()->effectiveDevicePixelRatio() : 1.0;
    const QSize p0(std::max(1, qRound(stage->width() * dpr)), std::max(1, qRound(stage->height() * dpr)));
    if (l.pyramid.levels > 0 && p0 == l.pyramid.px && dpr == l.pyramid.dpr)
        return;
    l.pyramid.px = p0;
    l.pyramid.dpr = dpr;
    // Step sizes in pixels: each the ceiling of half the one above, down to the first under 4 px on the smaller side.
    QSize px[kMaxLevels + 1];
    px[0] = p0;
    int n = 0;
    while (n < kMaxLevels && (n == 0 || std::min(px[n].width(), px[n].height()) >= 4)) {
        px[n + 1] = QSize((px[n].width() + 1) / 2, (px[n].height() + 1) / 2);
        ++n;
    }
    // Passes: hidden, own layer (texture provider of the next), children of the stage (go with it). Kept beyond n.
    std::string ignored;
    const auto make = [&](quint64 shader) {
        auto* p = new ShaderItem(stage);
        p->setVisible(false);
        p->setChainPass(true);
        setLayerEnabled(p, true);
        if (shader)
            p->setShader(VLQT_SHADER_STAGE_FRAGMENT, shader, ignored);
        return p;
    };
    if (l.pyramid.down.empty()) {
        ShaderItem* d1 = make(0); // prefilter: shader and table by writePyramidLink
        connect(d1, &ShaderItem::errorChanged, this, &EffectChain::updateError);
        connect(d1, &ShaderItem::rebuilt, this, &EffectChain::updateError);
        l.pyramid.down.push_back(d1);
    }
    while (int(l.pyramid.down.size()) < n)
        l.pyramid.down.push_back(make(builtinShader(m_components.pyramidDown, "builtin:pyramid_down")));
    while (int(l.pyramid.up.size()) < n - 1)
        l.pyramid.up.push_back(make(builtinShader(m_components.pyramidUp, "builtin:pyramid_up")));

    QQuickItem* input = c->source();
    for (int k = 1; k <= n; ++k) {
        ShaderItem* d = l.pyramid.down[size_t(k - 1)];
        d->setSource(k == 1 ? input : l.pyramid.down[size_t(k - 2)]);
        d->setSize(QSizeF(px[k]) / dpr);
    }
    for (size_t k = size_t(n); k < l.pyramid.down.size(); ++k)
        l.pyramid.down[k]->setSource(nullptr);
    for (int k = 1; k < n; ++k) {
        ShaderItem* u = l.pyramid.up[size_t(k - 1)];
        u->setSource(k == n - 1 ? static_cast<QQuickItem*>(l.pyramid.down[size_t(n - 1)]) : l.pyramid.up[size_t(k)]);
        u->setSize(QSizeF(px[k]) / dpr);
        u->beginUniforms(false);
        setRef(u, "base", l.pyramid.down[size_t(k - 1)]);
        u->endUniforms();
    }
    for (size_t k = size_t(n - 1); k < l.pyramid.up.size(); ++k)
        l.pyramid.up[k]->setSource(nullptr);
    l.pyramid.levels = n;
    QQuickItem* top = n > 1 ? static_cast<QQuickItem*>(l.pyramid.up[0]) : l.pyramid.down[0];
    c->beginUniforms(false);
    setRef(c, "glow", top);
    setRef(c, "shadow", top);
    c->endUniforms();
    qCDebug(lcEffectChain, "pyramid %p: %d x %d px, dpr %.2f, %d steps", static_cast<void*>(stage), p0.width(),
        p0.height(), dpr, n);
    writePyramidWeights(l); // dpr is part of the step of a radius
}

// Weights of the radius (doc 21): weight_k = clamp(L - k, 0, 1), k = 0 the composite.
bool EffectChain::writePyramidWeights(Link& l)
{
    if (l.pyramid.levels <= 0)
        return false;
    bool changed = false;
    const double level = pyramidLevel(l.shader.strength, l.pyramid.dpr);
    for (int k = 1; k < l.pyramid.levels; ++k) {
        ShaderItem* u = l.pyramid.up[size_t(k - 1)];
        u->beginUniforms(false);
        changed = setF32(u, "weight", float(std::clamp(level - k, 0.0, 1.0))) || changed;
        u->endUniforms();
    }
    auto* c = static_cast<ShaderItem*>(l.fx);
    c->beginUniforms(false);
    changed = setF32(c, "weight", float(std::clamp(level, 0.0, 1.0))) || changed;
    c->endUniforms();
    return changed;
}

void EffectChain::updateError()
{
    QStringList lines;
    for (size_t i = 0; i < m_links.size(); ++i) {
        const Link& l = m_links[i];
        if (l.kind != VLQT_EFFECT_CHAIN_LINK_SHADER)
            continue;
        const QString prefix = QStringLiteral("link %1: ").arg(i);
        for (const QString& p : l.shader.problems)
            lines << prefix + p;
        if (!l.shader.shaderError.isEmpty())
            lines << prefix + l.shader.shaderError;
        if (l.stage && l.fx)
            for (const QString& e : static_cast<ShaderItem*>(l.fx)->error().split(u'\n', Qt::SkipEmptyParts))
                lines << prefix + e;
        if (l.stage && l.shader.templ == VLQT_EFFECT_TEMPLATE_PYRAMID && !l.pyramid.down.empty()) {
            const ShaderItem* d1 = l.pyramid.down[0];
            const auto* c = static_cast<const ShaderItem*>(l.fx);
            for (const QString& e : d1->error().split(u'\n', Qt::SkipEmptyParts))
                lines << prefix + QStringLiteral("prefilter: ") + e;
            // The table goes to both: a name is unknown only if neither declares it.
            if (d1->hasShader() && c->hasShader())
                for (const EffectUniform& u : l.shader.uniforms)
                    if (!d1->declares(u.name) && !c->declares(u.name))
                        lines << prefix + QStringLiteral("unknown uniform '%1' (not in the shaders)").arg(QString::fromUtf8(u.name));
        }
    }
    m_error = lines.join(u'\n');
}

// ---------------------------------------------------------------------------------------------
// Effect layer (SET_EFFECT_CHAIN) and the mask frames of its links.

int Elements::setEffectChain(ElementEntry& node, const EffectChainDesc& chain, const std::vector<QQuickItem*>& masks, std::string& err)
{
    if (chain.links.empty()) {
        releaseEffectLayer(node);
        return ApplyOk;
    }
    if (!effectComponents)
        effectComponents = std::make_unique<EffectComponents>(engine);
    EffectChain* ec = node.effectChain;
    if (!ec) {
        ec = node.effectChain = new EffectChain(*effectComponents, node.item); // dies with the item at the latest
        if (auto* group = dynamic_cast<GroupItem*>(node.item))
            ec->setBuiltHandler([group](QQuickItem* first) { // the chain is a child of the group: never outlives it
                group->effectHolder = first;
                group->syncEffectFrame();
            });
    }
    std::vector<QPointer<QQuickItem>> retired;
    std::vector<EffectChain::DroppedMask> dropped;
    const bool ok = ec->apply(chain, masks, retired, dropped, err);
    for (const QPointer<QQuickItem>& stage : retired)
        frameDelete.emplace_back(stage); // may still be sampled this frame
    for (const EffectChain::DroppedMask& d : dropped)
        retireMaskFrame(node, d.mask, d.frame);
    if (!ok)
        return ApplyBadValue;
    for (size_t i = 0; i < ec->linkCount(); ++i) {
        EffectChain::MultiPart& m = ec->link(i).multi;
        if (!m.mask || m.maskFrame)
            continue;
        m.maskFrame = createMaskFrame(node, m.mask, m.maskFrameXf, err);
        if (!m.maskFrame) {
            m.mask = nullptr;
            return ApplyBadValue;
        }
        ec->syncMaskFrame(i);
    }
    if (!effectLayers.count(node.item)) {
        QQmlComponent* root = ec->rootComponent(err);
        // Component first, then enabled: activating the layer creates the root once, the root builds the stages.
        if (!root || !QQmlProperty(node.item, QStringLiteral("layer.effect")).write(QVariant::fromValue(root))) {
            if (err.empty())
                err = "setting layer.effect failed";
            return ApplyBadValue;
        }
        setLayerEnabled(node.item, true);
        effectLayers.insert(node.item);
    }
    updateMaskFrame(node);
    return ApplyOk;
}

void Elements::releaseEffectLayer(ElementEntry& node)
{
    EffectChain* ec = node.effectChain;
    if (!ec)
        return;
    for (QQuickItem* mask : effectMasks(node))
        detachEffectMask(node, mask);
    effectLayers.erase(node.item);
    // A SHADER source or an attached mask keeps its layer (an effect samples it), only the layer effect goes;
    // a Group source is sampled through its frame proxy, not its layer. The root and its stages go with it.
    if ((!isEffectSource(node.item) || isGroup(node.item)) && !isMaskLayer(node.item))
        setLayerEnabled(node.item, false);
    QQmlProperty(node.item, QStringLiteral("layer.effect")).write(QVariant::fromValue<QQmlComponent*>(nullptr));
    if (auto* group = dynamic_cast<GroupItem*>(node.item))
        group->effectHolder = nullptr;
    node.effectChain = nullptr;
    delete ec;
}

void Elements::detachEffectMask(ElementEntry& node, QQuickItem* mask)
{
    if (!node.effectChain || !mask)
        return;
    EffectChain* ec = node.effectChain;
    for (size_t i = 0; i < ec->linkCount(); ++i) {
        EffectChain::MultiPart& m = ec->link(i).multi;
        if (m.mask != mask)
            continue;
        QQuickItem* frame = m.maskFrame;
        m.mask = nullptr;
        m.maskFrame = nullptr;
        m.maskFrameXf = nullptr;
        ec->syncMaskFrame(i); // detach before the frame goes: MultiEffect keeps a raw maskSource pointer
        retireMaskFrame(node, mask, frame);
    }
}

std::vector<QQuickItem*> Elements::effectMasks(const ElementEntry& node) const
{
    std::vector<QQuickItem*> masks;
    if (const EffectChain* ec = node.effectChain)
        for (size_t i = 0; i < ec->linkCount(); ++i)
            if (QQuickItem* mask = ec->link(i).multi.mask)
                masks.push_back(mask);
    return masks;
}

QQuickItem* Elements::createMaskFrame(ElementEntry& node, QQuickItem* mask, QObject*& xfOut, std::string& err)
{
    // Mask rule (protocol.h): the mask lies in the element's parent coordinates like a sibling, with its own SIZE,
    // ANCHOR and TRANSFORM. frame (hidden, layer on, the link's maskSource) > inner (Matrix4x4 = inverse of the
    // element's parent mapping, updateMaskFrame) > mask: frame coordinates are the element's own, so the layer's
    // sourceRect is the link's effect area as it is (stage Binding) and the texture lands exactly where a sibling would be.
    QObject* xf = createTransform(err);
    if (!xf)
        return nullptr;
    auto* frame = new QQuickItem(mask->parentItem()); // next to the parked mask (window root): outside every layout
    frame->setVisible(false);                         // texture source only, the effect draws it as alpha
    frame->setSize(QSizeF(1, 1)); // a 0-sized source item gives no texture, whatever sourceRect says
    auto* inner = new QQuickItem(frame);
    xf->setParent(inner);
    QQmlListReference list(inner, "transform");
    if (!list.isValid() || !list.append(xf)) {
        delete frame;
        err = "appending Matrix4x4 to the mask frame failed";
        return nullptr;
    }
    mask->setParentItem(inner);
    mask->setVisible(true); // inside the hidden frame: rendered into its layer only
    maskLayers.insert(frame);
    // Linear filtering: a rotated/scaled element samples the texture off the pixel grid.
    QQmlProperty(frame, QStringLiteral("layer.smooth")).write(true);
    setLayerEnabled(frame, true);
    // Position (anchor placement or Row/Column layout) and size (anchor) of the element; TRANSFORM/ANCHOR/drag offset call
    // updateMaskFrame themselves. Context = frame: disconnected on detach, gone with it. The Scene detaches the
    // masks before it erases the node, so the pointer never dangles.
    ElementEntry* n = &node;
    auto update = [this, n]() { updateMaskFrame(*n); };
    QObject::connect(node.item, &QQuickItem::xChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::yChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::widthChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::heightChanged, frame, update);
    xfOut = xf;
    return frame;
}

void Elements::retireMaskFrame(ElementEntry& node, QQuickItem* mask, QQuickItem* frame)
{
    if (!frame)
        return;
    maskLayers.erase(frame);
    QObject::disconnect(node.item, nullptr, frame, nullptr);
    if (mask && frame->isAncestorOf(mask)) {
        mask->setParentItem(frame->parentItem()); // parked next to it again, hidden
        mask->setVisible(false);
    }
    // The effect may sample the frame until it polishes: layer off and delete after the next frame.
    layerOff.emplace_back(frame);
    frameDelete.emplace_back(frame);
}

void Elements::updateMaskFrame(ElementEntry& node)
{
    const EffectChain* ec = node.effectChain;
    if (!ec)
        return;
    bool anyFrame = false;
    for (size_t i = 0; i < ec->linkCount() && !anyFrame; ++i)
        anyFrame = ec->link(i).multi.maskFrameXf != nullptr;
    if (!anyFrame)
        return;
    // Element -> parent, as Qt maps it: T(x, y) * item.transform (x/y = -anchor or the layout position; our Matrix4x4:
    // TRANSFORM around the anchor + drag offset). Its inverse brings parent coordinates (the mask's) into the element's (the effect area's).
    QMatrix4x4 m;
    m.translate(float(node.item->x()), float(node.item->y()));
    if (node.transform && node.xf)
        m *= pivoted(*node.xf, node.item);
    const QMatrix4x4 inv = m.inverted();
    for (size_t i = 0; i < ec->linkCount(); ++i)
        if (QObject* xf = ec->link(i).multi.maskFrameXf)
            setMatrixValue(xf, inv);
}

QQuickItem* Elements::effectLayerItem(const ElementEntry& node)
{
    return node.effectChain ? node.effectChain->root() : nullptr;
}

} // namespace vlqt
