/* effects/effect_chain_desc.h - parsed SET_EFFECT_CHAIN: the links of an element's Effect chain in the order they apply
 * (first = applied first to the element). Built by Scene::setEffectChain (scene/commands.cpp) after the whole command was
 * validated (wire format: protocol.h head comment "Effect chain"), handed to Elements::setEffectChain.
 * The description owns its data (names, values), so it may be kept beyond the apply; refs are element ids, resolved
 * by the Scene (0 = none). EffectChain (effects/effect_chain.h) renders it as an item tree in the element's layer.effect. */
#pragma once

#include "protocol.h"

#include <QtCore/QByteArray>
#include <QtCore/QPointer>
#include <QtCore/QtTypes>
#include <QtQuick/QQuickItem>

#include <vector>

namespace vlqt {

/* One value of a MULTI entry or a uniform: raw value bytes as on the wire (like SET), refs as element ids. */
struct EffectValue {
    quint16 type = 0;     // VLQT_VALUE_TYPE_*
    float f[16] = {};     // f32 (f[0]), color, rectf, vec2/3/4, mat4 (row-major like Stride)
    qint32 i = 0;         // i32, ivec2 x
    qint32 i2 = 0;        // ivec2 y
    bool b = false;       // bool
    quint64 u = 0;        // u64 (image resource id)
    quint32 ref = 0;      // ref: element id (0 = none)
};

/* MULTI entry: an EFFECT_* property (833..855) of the run's MultiEffect, value type already checked. */
struct EffectMultiEntry {
    quint16 prop = 0;
    EffectValue value;
};

/* SHADER uniform entry, like a SET_UNIFORMS entry (content checks such as unknown names come later, by EVENT_ERROR). */
struct EffectUniform {
    QByteArray name; // UTF-8, 1..VLQT_UNIFORM_NAME_MAX bytes
    EffectValue value;
    QPointer<QQuickItem> refItem; // REF: the texture element, resolved and parked by the Scene (null = none or refError)
    const char* refError = nullptr; // REF: why the element cannot be a texture (an EVENT_ERROR line), static text
};

struct EffectLinkDesc {
    quint16 kind = 0; // VLQT_EFFECT_CHAIN_LINK_*

    /* kind MULTI: one MultiEffect run */
    std::vector<EffectMultiEntry> multi;

    /* kind SHADER */
    quint16 templ = 0;              // VLQT_EFFECT_TEMPLATE_*
    std::vector<quint64> shaders;   // shader resource ids by role (SINGLE/TWO_PASS: frag; PYRAMID: prefilter, composite)
    float padding[4] = {};          // DIP: left, top, right, bottom
    float strength = 0.f;           // PYRAMID: radius in DIP; SINGLE / TWO_PASS: unused
    std::vector<EffectUniform> uniforms;
};

struct EffectChainDesc {
    std::vector<EffectLinkDesc> links; // application order; empty = no chain (layer off)
};

} // namespace vlqt
