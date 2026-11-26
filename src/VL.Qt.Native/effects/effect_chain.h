/* effects/effect_chain.h - the Effect chain of one element (EffectChain) and the QML components its items are made of
 * (EffectComponents). The chain renders a parsed SET_EFFECT_CHAIN (effects/effect_chain_desc.h) as an item tree in the
 * element's layer.effect; its root is an EffectChainItem (effects/effect_chain_item.h). */
#pragma once

#include "effects/effect_chain_desc.h"
#include "protocol.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QMetaProperty>
#include <QtCore/QObject>
#include <QtCore/QPointer>
#include <QtCore/QRectF>
#include <QtCore/QSize>
#include <QtCore/QStringList>
#include <QtCore/QVariant>
#include <QtCore/QtTypes>
#include <QtQuick/QQuickItem>

#include <array>
#include <functional>
#include <string>
#include <utility>
#include <vector>

class QQmlComponent;
class QQmlEngine;
class QQuickMatrix4x4; // QtQuick/private/qquicktranslate_p.h

Q_DECLARE_LOGGING_CATEGORY(lcEffectChain)

namespace vlqt {

class ShaderItem;

/* Number of MULTI keys (EFFECT_* 833..855); index of a key = prop - VLQT_MULTI_EFFECT_KEY_BLUR_ENABLED. */
constexpr int kEffectMultiKeys = VLQT_MULTI_EFFECT_KEY_MASK_INVERTED - VLQT_MULTI_EFFECT_KEY_BLUR_ENABLED + 1;

/* QML components of the chain items, compiled once per engine (owned by Elements). */
struct EffectComponents {
    explicit EffectComponents(QQmlEngine* e) : engine(e) {}
    ~EffectComponents();
    EffectComponents(const EffectComponents&) = delete;
    EffectComponents& operator=(const EffectComponents&) = delete;

    QQmlEngine* engine;
    QQmlComponent* root = nullptr;  // holder of the layer.effect component (VlqtEffectChain, effects/effect_chain_item.h)
    QObject* rootHolder = nullptr;  // its instance: fx = the inline Component (layer.effect needs a creation context)
    QQmlComponent* multi = nullptr; // stage of a MULTI link: MultiEffect in an Item covering its effect area
    QQmlComponent* pass = nullptr;  // stage of a SHADER link: one or two ShaderItem passes (VlqtShaderPass)
    QMetaProperty multiProps[kEffectMultiKeys]; // MultiEffect property per MULTI key (invalid: PADDING_RECT, MASK_SOURCE)
    bool resolved = false;
    quint64 pyramidDown = 0, pyramidUp = 0; // builtin:pyramid_down / pyramid_up, loaded on first use, shared by all links
};

/* The Effect chain of one element (protocol.h "Effect chain"), QObject child of the element's item. It builds one stage
 * per link under the root of the layer.effect, in application order, and writes the values of the links to the stages.
 * The links are the desired state; the stages exist while the root does. GUI thread. Item tree, shader passes, the blur
 * pyramid and the same-structure rule: docs/design/effects.md, "Item tree of a chain", "Shader link stages" and "Blur
 * pyramid". The mask fields of a link (MultiPart) are kept by Elements. */
class EffectChain final : public QObject
{
    Q_OBJECT
public:
    /* MULTI: the values of one MultiEffect run and its mask (kept by Elements, see link()). */
    struct MultiPart {
        QVariant values[kEffectMultiKeys]; // values as written to the MultiEffect (Qt defaults if unset)
        QQuickItem* mask = nullptr;        // attached mask element's item (Elements, Scene::setEffectChain)
        QQuickItem* maskFrame = nullptr;   // hidden layer item holding mask in the element's parent coordinates
        QQuickMatrix4x4* maskFrameXf = nullptr; // Matrix4x4 of the frame's inner item (Elements::updateMaskFrame)
    };

    /* SHADER (every template): shaders, uniform table and the link's own problems. */
    struct ShaderPart {
        quint16 templ = 0;                   // VLQT_EFFECT_TEMPLATE_*
        quint64 shader = 0;                  // fragment shader resource id (0 = pass-through; PYRAMID: prefilter)
        quint64 shader2 = 0;                 // PYRAMID: composite
        float strength = 0.f;                // PYRAMID: radius in DIP
        std::vector<EffectUniform> uniforms; // table without the reserved and broken entries
        QStringList problems;                // content problems of the link itself (reserved names, refs, ...)
        QString shaderError;                 // setShader refused the id
        std::array<float, 4> elementRect{};  // elementRect last written to the passes (SINGLE / TWO_PASS), valid if ...
        bool elementRectKnown = false;       // ... set: false after the table was cleared and on a fresh stage
    };

    /* SHADER PYRAMID: the passes under the stage and the layout they were made for. */
    struct PyramidPasses {
        std::vector<ShaderItem*> down; // D1 .. (pool, children of stage), down[0] = prefilter
        std::vector<ShaderItem*> up;   // U1 .. (pool)
        int levels = 0;                // n in use (0 = not laid out)
        QSize px;                      // p_0 of the layout
        qreal dpr = 0;                 // dpr of the layout
    };

    /* One link of the chain: what every kind has, and one part per kind (the parts of other kinds stay empty). */
    struct Link {
        quint16 kind = 0;           // VLQT_EFFECT_CHAIN_LINK_*
        QRectF padding;             // MULTI PADDING_RECT / SHADER padding: x left, y top, w right, h bottom (DIP)
        QPointer<QQuickItem> stage; // item of the link under the root (null while there is no root)
        QQuickItem* fx = nullptr;   // MultiEffect / ShaderItem (SHADER pass 1) inside stage
        QQuickItem* fx2 = nullptr;  // SHADER: pass 2 (drawn for TWO_PASS)
        MultiPart multi;            // kind MULTI
        ShaderPart shader;          // kind SHADER
        PyramidPasses pyramid;      // kind SHADER, template PYRAMID
    };

    /* Mask state a link gave up (Elements retires the frame). */
    struct DroppedMask {
        QQuickItem* mask = nullptr;
        QQuickItem* frame = nullptr;
    };

    EffectChain(EffectComponents& components, QQuickItem* element);

    /* Takes the links of desc (masks: mask item per link, nullptr = none). Same structure (link count and kinds) with a
     * root: the stages stay, only changed values are written (no shader switch, no new item, no allocation, no
     * repaint when nothing changed). Otherwise the stages are rebuilt; the old ones are hidden and go to retired (deleted
     * by the caller after the next frame). A mask that stays attached keeps its frame (moved to its new link); the
     * others go to dropped. false + err: a stage could not be created (QML error). */
    bool apply(const EffectChainDesc& desc, const std::vector<QQuickItem*>& masks, std::vector<QPointer<QQuickItem>>& retired,
        std::vector<DroppedMask>& dropped, std::string& err);
    /* Writes link(i).multi.maskFrame to its stage (nullptr = no mask). */
    void syncMaskFrame(size_t i);
    /* layer.effect component (registers the root type once). nullptr + err on a QML error. */
    QQmlComponent* rootComponent(std::string& err);
    QQuickItem* root() const { return m_root; }
    /* true if a SHADER link samples item as a texture uniform (REF; the Scene parked it, protocol.h "Effect chain"). */
    bool samples(const QQuickItem* item) const;
    /* Content problems of the links (EVENT_ERROR text: one line each, "link N: ..."), empty = none. */
    const QString& error() const { return m_error; }

    /* The links in application order (the desired state); Elements keeps the mask fields of MultiPart through link(). */
    size_t linkCount() const { return m_links.size(); }
    Link& link(size_t i) { return m_links[i]; }
    const Link& link(size_t i) const { return m_links[i]; }
    /* Called after the stages were (re)built, with the first stage (Group frame: frameLeft/frameTop). */
    void setBuiltHandler(std::function<void(QQuickItem* first)> handler) { m_built = std::move(handler); }

private:
    friend class EffectChainItem;
    void attach(QQuickItem* root);
    bool buildStages(std::string& err);
    /* Writes the values of l to its stage that differ from old (nullptr: from a fresh stage, i.e. Qt's defaults).
     * true if anything was written. */
    bool writeLink(Link& l, const Link* old);
    bool writeShaderLink(Link& l, const Link* old);
    bool writePyramidLink(Link& l, const Link* old);
    /* Step count, pass sizes and wiring from the stage size and dpr (no-op if both are unchanged). */
    void layoutPyramid(Link& l);
    bool writePyramidWeights(Link& l);
    bool writeElementRect(Link& l); // elementRect auto uniform of SINGLE / TWO_PASS passes (stage geometry), on change only
    /* update() on every ShaderItem pass of the links from `first` on: a pass whose own values stayed the same still has to
     * draw again when a layer it samples changed. */
    void updatePasses(size_t first);
    void updateError();

    EffectComponents& m_components;
    QPointer<QQuickItem> m_root;
    QString m_error;
    std::vector<Link> m_links;
    std::vector<Link> m_next; // apply scratch: the parsed links, then the previous ones; the capacity of the tables survives
    std::function<void(QQuickItem* first)> m_built;
};

} // namespace vlqt
