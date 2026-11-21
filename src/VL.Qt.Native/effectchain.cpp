#include "effectchain.h"

#include "shader_resource.h"
#include "shader_item.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QUrl>
#include <QtGui/QColor>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQml/QQmlProperty>
#include <QtQml/qqml.h>
#include <QtQuick/QQuickWindow>
#include <QtQuick/private/qquickshadereffectsource_p.h> // the layer's texture source: its sourceItem is the element

#include <algorithm>
#include <cmath>
#include <cstring>

Q_LOGGING_CATEGORY(lcEffectChain, "vlqt.effectchain")

namespace vlqt {

/* Root of a chain = the item layer.effect creates (registered as VLQt.EffectChain/VlqtEffectChain). The layer sets
 * source (its ShaderEffectSource of the element) before completing it; the root then hands itself to the EffectChain
 * child of that element, which builds the stages under it. Created and deleted by the layer only. */
class EffectChainItem : public QQuickItem // not final: qmlRegisterType derives from it
{
    Q_OBJECT
    Q_PROPERTY(QObject* source READ source WRITE setSource NOTIFY sourceChanged)
public:
    explicit EffectChainItem(QQuickItem* parent = nullptr) : QQuickItem(parent) {}

    QObject* source() const { return m_source; }
    void setSource(QObject* s)
    {
        if (m_source == s)
            return;
        m_source = s;
        emit sourceChanged();
        if (isComponentComplete())
            attach();
    }

signals:
    void sourceChanged();

protected:
    void componentComplete() override
    {
        QQuickItem::componentComplete();
        attach();
    }

private:
    void attach()
    {
        auto* src = qobject_cast<QQuickShaderEffectSource*>(m_source.data());
        QQuickItem* element = src ? src->sourceItem() : nullptr;
        if (auto* chain = element ? element->findChild<EffectChain*>(QString(), Qt::FindDirectChildrenOnly) : nullptr)
            chain->attach(this);
        else
            qCWarning(lcEffectChain, "effect chain root without chain (source %p)", static_cast<void*>(m_source.data()));
    }

    QPointer<QObject> m_source;
};

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

void setLayer(QQuickItem* item, bool on)
{
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

// Sets one uniform of a pass (between beginUniforms and endUniforms).
void setF32(ShaderItem* p, const char* name, float v)
{
    unsigned char value[4];
    std::memcpy(value, &v, 4);
    p->setUniform(name, int(std::strlen(name)), VLQT_VALUE_TYPE_F32, value, nullptr);
}

void setVec4(ShaderItem* p, const char* name, float x, float y, float w, float h)
{
    const float v[4] = {x, y, w, h};
    unsigned char value[16];
    std::memcpy(value, v, 16);
    p->setUniform(name, int(std::strlen(name)), VLQT_VALUE_TYPE_VEC4, value, nullptr);
}

void setRef(ShaderItem* p, const char* name, QQuickItem* item)
{
    const unsigned char value[4] = {};
    p->setUniform(name, int(std::strlen(name)), VLQT_VALUE_TYPE_REF, value, item);
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
    std::vector<Link> next(desc.links.size());
    for (size_t i = 0; i < next.size(); ++i) {
        const EffectLinkDesc& d = desc.links[i];
        Link& l = next[i];
        l.kind = d.kind;
        if (d.kind == VLQT_EFFECT_CHAIN_LINK_MULTI) {
            for (int k = 0; k < kEffectMultiKeys; ++k)
                l.values[k] = multiDefault(k);
            for (const EffectMultiEntry& e : d.multi) {
                if (e.prop == VLQT_MULTI_EFFECT_KEY_PADDING_RECT)
                    l.padding = QRectF(e.value.f[0], e.value.f[1], e.value.f[2], e.value.f[3]);
                else if (e.prop != VLQT_MULTI_EFFECT_KEY_MASK_SOURCE)
                    l.values[multiIndex(e.prop)] = toVariant(e.value);
            }
            l.mask = i < masks.size() ? masks[i] : nullptr;
        } else {
            l.templ = d.templ;
            l.shader = d.shaders.empty() ? 0 : d.shaders[0]; // PYRAMID: prefilter
            if (d.templ == VLQT_EFFECT_TEMPLATE_PYRAMID)
                l.shader2 = d.shaders.size() > 1 ? d.shaders[1] : 0;
            float pad[4];
            bool negative = false;
            for (int k = 0; k < 4; ++k) {
                pad[k] = d.padding[k] >= 0.f ? d.padding[k] : 0.f; // NaN too
                negative = negative || !(d.padding[k] >= 0.f);
            }
            if (negative)
                l.problems << QStringLiteral("padding must not be negative (0 is used)");
            l.padding = QRectF(pad[0], pad[1], pad[2], pad[3]);
            l.strength = d.strength >= 0.f ? d.strength : 0.f;
            if (!(d.strength >= 0.f))
                l.problems << QStringLiteral("strength must not be negative (0 is used)");
            for (const EffectUniform& u : d.uniforms) {
                const QString name = QString::fromUtf8(u.name);
                if (reservedName(u.name))
                    l.problems << QStringLiteral("uniform '%1' is reserved (set by VL.Qt)").arg(name);
                else if (u.refError)
                    l.problems << QStringLiteral("uniform '%1': %2").arg(name, QString::fromUtf8(u.refError));
                else
                    l.uniforms.push_back(u);
            }
        }
    }
    // A mask that stays attached keeps its frame, also in another link.
    for (const Link& o : links) {
        if (!o.mask)
            continue;
        auto it = std::find_if(next.begin(), next.end(), [&](const Link& n) { return n.mask == o.mask; });
        if (it != next.end()) {
            it->maskFrame = o.maskFrame;
            it->maskFrameXf = o.maskFrameXf;
        } else {
            dropped.push_back({o.mask, o.maskFrame});
        }
    }

    bool same = m_root && next.size() == links.size();
    for (size_t i = 0; same && i < next.size(); ++i)
        same = next[i].kind == links[i].kind && next[i].templ == links[i].templ && links[i].stage;
    if (same) {
        // Same structure: values only, where they changed (no new item, no shader switch). Swapped first: a PYRAMID
        // stage lays out again on a size change of its own (layoutPyramid finds its link in links).
        for (size_t i = 0; i < next.size(); ++i) {
            Link& n = next[i];
            const Link& o = links[i];
            n.stage = o.stage;
            n.fx = o.fx;
            n.fx2 = o.fx2;
            n.down = o.down;
            n.up = o.up;
            n.levels = o.levels;
            n.px = o.px;
            n.dpr = o.dpr;
        }
        links.swap(next);
        for (size_t i = 0; i < links.size(); ++i)
            writeLink(links[i], &next[i]);
        updateError();
        return true;
    }
    for (Link& o : links) {
        if (!o.stage)
            continue;
        if (o.kind == VLQT_EFFECT_CHAIN_LINK_MULTI)
            o.stage->setProperty("maskFrame", QVariant::fromValue<QQuickItem*>(nullptr)); // before the frame may go
        else
            o.stage->setProperty("active", false); // its Binding leaves the input's sourceRect to the new stages
        o.stage->setVisible(false);
        retired.emplace_back(o.stage);
    }
    links.swap(next);
    const bool ok = !m_root || buildStages(err);
    updateError();
    return ok;
}

void EffectChain::syncMaskFrame(size_t i)
{
    Link& l = links[i];
    if (l.stage && l.kind == VLQT_EFFECT_CHAIN_LINK_MULTI)
        l.stage->setProperty("maskFrame", QVariant::fromValue<QQuickItem*>(l.maskFrame));
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
    for (size_t i = 0; i < links.size(); ++i) {
        Link& l = links[i];
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
            stage->setProperty("twoPass", l.templ == VLQT_EFFECT_TEMPLATE_TWO_PASS);
            auto* p1 = static_cast<ShaderItem*>(l.fx);
            p1->setChainPass(true);
            p1->setSource(input);
            connect(p1, &ShaderItem::errorChanged, this, &EffectChain::updateError);
            if (l.templ == VLQT_EFFECT_TEMPLATE_PYRAMID) {
                // p1 = composite; its texelSize is the one of the glow / shadow texture. The passes follow the stage.
                p1->setTexelSamplers({QByteArrayLiteral("glow"), QByteArrayLiteral("shadow")});
                connect(p1, &ShaderItem::rebuilt, this, &EffectChain::updateError);
                const QPointer<QQuickItem> st = stage;
                const auto relayout = [this, st] {
                    for (Link& x : links)
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
                    for (Link& x : links)
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
        const bool last = i + 1 == links.size();
        stage->setVisible(last);
        if (!last)
            setLayer(stage, true); // texture provider of the next link
        prev = stage;
    }
    if (built)
        built(links.empty() ? nullptr : links.front().stage.data());
    return true;
}

void EffectChain::writeLink(Link& l, const Link* old)
{
    if (!l.stage)
        return;
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
            if (l.values[k] == (old ? old->values[k] : multiDefault(k)))
                continue;
            if (!p.write(l.fx, l.values[k]))
                qCWarning(lcEffectChain, "writing MultiEffect.%s failed", p.name());
        }
        if (l.maskFrame != (old ? old->maskFrame : nullptr))
            l.stage->setProperty("maskFrame", QVariant::fromValue<QQuickItem*>(l.maskFrame));
    } else {
        writeShaderLink(l, old);
    }
    if (l.padding != (old ? old->padding : QRectF()))
        l.stage->setProperty("pad", l.padding);
}

void EffectChain::writeShaderLink(Link& l, const Link* old)
{
    if (l.templ == VLQT_EFFECT_TEMPLATE_PYRAMID) {
        writePyramidLink(l, old);
        return;
    }
    auto* p1 = static_cast<ShaderItem*>(l.fx);
    auto* p2 = static_cast<ShaderItem*>(l.fx2);
    const bool two = l.templ == VLQT_EFFECT_TEMPLATE_TWO_PASS;
    if (!old || old->shader != l.shader) {
        std::string err, ignored;
        l.shaderError.clear();
        if (!p1->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shader, err)) {
            l.shaderError = QString::fromStdString(err);
            p1->setShader(VLQT_SHADER_STAGE_FRAGMENT, 0, ignored); // pass-through
        }
        if (two)
            p2->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shaderError.isEmpty() ? l.shader : 0, ignored);
    } else {
        l.shaderError = old->shaderError;
    }
    // Same names and types as before: values only (no layout rebuild in the passes).
    bool sameTable = old && old->uniforms.size() == l.uniforms.size();
    for (size_t i = 0; sameTable && i < l.uniforms.size(); ++i)
        sameTable = old->uniforms[i].name == l.uniforms[i].name && old->uniforms[i].value.type == l.uniforms[i].value.type;
    unsigned char value[sizeof(EffectValue::f)];
    for (ShaderItem* p : {p1, p2}) {
        if (p == p2 && !two)
            break;
        p->setOptionalUniforms({QByteArrayLiteral("elementRect"), QByteArrayLiteral("passIndex")});
        p->beginUniforms(!sameTable);
        for (const EffectUniform& u : l.uniforms) {
            wireValue(u.value, value);
            p->setUniform(u.name.constData(), int(u.name.size()), u.value.type, value, u.refItem);
        }
        // passIndex (protocol.h): 0 in pass 1 (and SINGLE), 1 in pass 2 of TWO_PASS; written only if the shader declares it.
        const float passIndex = p == p2 ? 1.f : 0.f;
        std::memcpy(value, &passIndex, 4);
        p->setUniform("passIndex", 9, VLQT_VALUE_TYPE_F32, value, nullptr);
        p->endUniforms();
    }
    writeElementRect(l);
}

// elementRect (protocol.h): the element's rectangle (0, 0, root width, root height) in UV of the stage area, which is placed
// in element coordinates (x, y negative where earlier links grew it). A texture that sits on the element maps itself with it.
void EffectChain::writeElementRect(Link& l)
{
    if (!l.stage || !m_root || l.kind != VLQT_EFFECT_CHAIN_LINK_SHADER || l.templ == VLQT_EFFECT_TEMPLATE_PYRAMID)
        return;
    const qreal w = l.stage->width(), h = l.stage->height();
    if (w <= 0 || h <= 0)
        return;
    const float rect[4] = {float(-l.stage->x() / w), float(-l.stage->y() / h), float(m_root->width() / w), float(m_root->height() / h)};
    for (QQuickItem* item : {l.fx, l.fx2}) {
        if (!item || (item == l.fx2 && l.templ != VLQT_EFFECT_TEMPLATE_TWO_PASS))
            continue;
        auto* p = static_cast<ShaderItem*>(item);
        p->beginUniforms(false);
        setVec4(p, "elementRect", rect[0], rect[1], rect[2], rect[3]);
        p->endUniforms();
    }
}

// PYRAMID: prefilter (D1) and composite get the C# table, each takes what it declares; the composite also gets weight
// and its glow / shadow input (U1, or D1 for a single step).
void EffectChain::writePyramidLink(Link& l, const Link* old)
{
    auto* c = static_cast<ShaderItem*>(l.fx);
    layoutPyramid(l); // the first write creates the passes
    if (l.down.empty())
        return;
    ShaderItem* d1 = l.down[0];
    if (!old || old->shader != l.shader || old->shader2 != l.shader2) {
        std::string err, ignored;
        QStringList errors;
        if (!d1->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shader, err)) {
            errors << QStringLiteral("prefilter: ") + QString::fromStdString(err);
            d1->setShader(VLQT_SHADER_STAGE_FRAGMENT, 0, ignored); // pass-through
        }
        err.clear();
        if (!c->setShader(VLQT_SHADER_STAGE_FRAGMENT, l.shader2, err)) {
            errors << QStringLiteral("composite: ") + QString::fromStdString(err);
            c->setShader(VLQT_SHADER_STAGE_FRAGMENT, 0, ignored);
        }
        l.shaderError = errors.join(QStringLiteral("; "));
    } else {
        l.shaderError = old->shaderError;
    }
    bool sameTable = old && old->uniforms.size() == l.uniforms.size();
    for (size_t i = 0; sameTable && i < l.uniforms.size(); ++i)
        sameTable = old->uniforms[i].name == l.uniforms[i].name && old->uniforms[i].value.type == l.uniforms[i].value.type;
    if (!sameTable) {
        std::vector<QByteArray> names;
        for (const EffectUniform& u : l.uniforms)
            names.push_back(u.name);
        d1->setOptionalUniforms(names);
        names.push_back(QByteArrayLiteral("glow"));
        names.push_back(QByteArrayLiteral("shadow"));
        c->setOptionalUniforms(std::move(names));
    }
    unsigned char value[sizeof(EffectValue::f)];
    const double level = pyramidLevel(l.strength, l.dpr);
    QQuickItem* top = l.levels > 1 ? static_cast<QQuickItem*>(l.up[0]) : d1;
    for (ShaderItem* p : {d1, c}) {
        p->beginUniforms(!sameTable);
        for (const EffectUniform& u : l.uniforms) {
            wireValue(u.value, value);
            p->setUniform(u.name.constData(), int(u.name.size()), u.value.type, value, u.refItem);
        }
        if (p == c) {
            setF32(c, "weight", float(std::clamp(level, 0.0, 1.0)));
            setRef(c, "glow", top);
            setRef(c, "shadow", top);
        }
        p->endUniforms();
    }
    if (!old || old->strength != l.strength)
        writePyramidWeights(l);
}

void EffectChain::layoutPyramid(Link& l)
{
    QQuickItem* stage = l.stage;
    if (!stage || !l.fx)
        return;
    // Without a window only the first layout (the passes must exist for writePyramidLink); a stage leaving its window
    // (layer off, root going) keeps its passes as they are.
    if (!stage->window() && l.levels > 0)
        return;
    auto* c = static_cast<ShaderItem*>(l.fx);
    const qreal dpr = stage->window() ? stage->window()->effectiveDevicePixelRatio() : 1.0;
    const QSize p0(std::max(1, qRound(stage->width() * dpr)), std::max(1, qRound(stage->height() * dpr)));
    if (l.levels > 0 && p0 == l.px && dpr == l.dpr)
        return;
    l.px = p0;
    l.dpr = dpr;
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
        setLayer(p, true);
        if (shader)
            p->setShader(VLQT_SHADER_STAGE_FRAGMENT, shader, ignored);
        return p;
    };
    if (l.down.empty()) {
        ShaderItem* d1 = make(0); // prefilter: shader and table by writePyramidLink
        connect(d1, &ShaderItem::errorChanged, this, &EffectChain::updateError);
        connect(d1, &ShaderItem::rebuilt, this, &EffectChain::updateError);
        l.down.push_back(d1);
    }
    while (int(l.down.size()) < n)
        l.down.push_back(make(builtinShader(m_components.pyramidDown, "builtin:pyramid_down")));
    while (int(l.up.size()) < n - 1)
        l.up.push_back(make(builtinShader(m_components.pyramidUp, "builtin:pyramid_up")));

    QQuickItem* input = c->source();
    for (int k = 1; k <= n; ++k) {
        ShaderItem* d = l.down[size_t(k - 1)];
        d->setSource(k == 1 ? input : l.down[size_t(k - 2)]);
        d->setSize(QSizeF(px[k]) / dpr);
    }
    for (size_t k = size_t(n); k < l.down.size(); ++k)
        l.down[k]->setSource(nullptr);
    for (int k = 1; k < n; ++k) {
        ShaderItem* u = l.up[size_t(k - 1)];
        u->setSource(k == n - 1 ? static_cast<QQuickItem*>(l.down[size_t(n - 1)]) : l.up[size_t(k)]);
        u->setSize(QSizeF(px[k]) / dpr);
        u->beginUniforms(false);
        setRef(u, "base", l.down[size_t(k - 1)]);
        u->endUniforms();
    }
    for (size_t k = size_t(n - 1); k < l.up.size(); ++k)
        l.up[k]->setSource(nullptr);
    l.levels = n;
    QQuickItem* top = n > 1 ? static_cast<QQuickItem*>(l.up[0]) : l.down[0];
    c->beginUniforms(false);
    setRef(c, "glow", top);
    setRef(c, "shadow", top);
    c->endUniforms();
    qCDebug(lcEffectChain, "pyramid %p: %d x %d px, dpr %.2f, %d steps", static_cast<void*>(stage), p0.width(),
        p0.height(), dpr, n);
    writePyramidWeights(l); // dpr is part of the step of a radius
}

// Weights of the radius (doc 21): weight_k = clamp(L - k, 0, 1), k = 0 the composite.
void EffectChain::writePyramidWeights(Link& l)
{
    if (l.levels <= 0)
        return;
    const double level = pyramidLevel(l.strength, l.dpr);
    for (int k = 1; k < l.levels; ++k) {
        ShaderItem* u = l.up[size_t(k - 1)];
        u->beginUniforms(false);
        setF32(u, "weight", float(std::clamp(level - k, 0.0, 1.0)));
        u->endUniforms();
    }
    auto* c = static_cast<ShaderItem*>(l.fx);
    c->beginUniforms(false);
    setF32(c, "weight", float(std::clamp(level, 0.0, 1.0)));
    c->endUniforms();
}

void EffectChain::updateError()
{
    QStringList lines;
    for (size_t i = 0; i < links.size(); ++i) {
        const Link& l = links[i];
        if (l.kind != VLQT_EFFECT_CHAIN_LINK_SHADER)
            continue;
        const QString prefix = QStringLiteral("link %1: ").arg(i);
        for (const QString& p : l.problems)
            lines << prefix + p;
        if (!l.shaderError.isEmpty())
            lines << prefix + l.shaderError;
        if (l.stage && l.fx)
            for (const QString& e : static_cast<ShaderItem*>(l.fx)->error().split(u'\n', Qt::SkipEmptyParts))
                lines << prefix + e;
        if (l.stage && l.templ == VLQT_EFFECT_TEMPLATE_PYRAMID && !l.down.empty()) {
            const ShaderItem* d1 = l.down[0];
            const auto* c = static_cast<const ShaderItem*>(l.fx);
            for (const QString& e : d1->error().split(u'\n', Qt::SkipEmptyParts))
                lines << prefix + QStringLiteral("prefilter: ") + e;
            // The table goes to both: a name is unknown only if neither declares it.
            if (d1->hasShader() && c->hasShader())
                for (const EffectUniform& u : l.uniforms)
                    if (!d1->declares(u.name) && !c->declares(u.name))
                        lines << prefix + QStringLiteral("unknown uniform '%1' (not in the shaders)").arg(QString::fromUtf8(u.name));
        }
    }
    m_error = lines.join(u'\n');
}

} // namespace vlqt

#include "effectchain.moc"