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

/* The Effect chain of one element (protocol.h "Effect chain"), QObject child of the element's item.
 *
 * Item tree: the element's layer creates the root (layer.effect = EffectComponents::root, an EffectChainItem with the
 * element's geometry and transform, source = the layer texture); the root finds this object as a child of the layer's
 * source item and asks it to build one stage per link under the root, in application order. A stage is an Item covering
 * the effect area of its link (root = element coordinates); its fx (MultiEffect, or the pass-through ShaderEffect of a
 * SHADER link) samples the input (the layer texture for the first link, the previous stage otherwise) and sits where the
 * input lies. Intermediate stages are hidden and have a layer (texture provider of the next link: a MultiEffect sets
 * that layer's sourceRect to its padded input rect itself, no extra texture), the last stage draws. The effect area
 * grows from link to link by the link's padding (MultiEffect: itemRect incl. auto padding).
 *
 * SHADER stage (SINGLE, TWO_PASS, PYRAMID): the stage covers its input plus the
 * link's padding; the input's texture covers the same area (the stage sets the sourceRect of the input's layer, or of the
 * element's layer source for the first link, to it), so each pass maps it 1:1 over UV 0..1. SINGLE = fx (ShaderItem,
 * chain pass) samples the input; TWO_PASS = fx (hidden, own layer) with passIndex = 0, fx2 samples fx
 * with passIndex = 1, same table otherwise. PYRAMID (doc 21 "Blur-Pyramide"): fx = composite (draws, source = input,
 * glow/shadow = U1 or D1), the passes D1 (prefilter, source = input) .. Dn (pyramid_down) and U(n-1) .. U1 (pyramid_up,
 * base = Dk) are hidden ShaderItems with a layer under the stage, sized p_k / dpr (p_0 = stage x dpr, p_k =
 * ceil(p_(k-1) / 2), n: steps until the smaller side is under 4 px). The step count follows the stage size and dpr only
 * (layoutPyramid on a size / dpr change; passes beyond n stay in the pool, unsampled, until the stage goes); the radius
 * writes the weights only. Values only on the same structure (kind and template): no new item.
 *
 * The links are the desired state; the stages exist while the root does (a recreated root rebuilds them). The mask fields
 * of a link (MultiPart) are kept by Elements (frame items, protocol.h mask rule); a stage binds its mask frame's
 * layer.sourceRect to the stage's effect area. */
class EffectChain final : public QObject
{
    Q_OBJECT
public:
    /* MULTI: the values of one MultiEffect run and its mask (kept by Elements, see link()). */
    struct MultiPart {
        QVariant values[kEffectMultiKeys]; // values as written to the MultiEffect (Qt defaults if unset)
        QQuickItem* mask = nullptr;        // attached mask element's item (Elements, Scene::setEffectChain)
        QQuickItem* maskFrame = nullptr;   // hidden layer item holding mask in the element's parent coordinates
        QObject* maskFrameXf = nullptr;    // Matrix4x4 of the frame's inner item (Elements::updateMaskFrame)
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
