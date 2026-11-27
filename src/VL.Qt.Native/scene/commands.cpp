#include "scene/commands.h"
#include "effects/effect_chain_desc.h"
#include "effects/shader_item.h"
#include "elements/group_item.h"
#include "user_qml/qml_item.h"
#include "user_qml/qml_wire.h"
#include "scene/scene.h"
#include "style/vl_style.h"
#include "wire.h"

#include <QtCore/QRectF>
#include <QtGui/QColor>
#include <QtGui/QVector2D>
#include <QtGui/QVector3D>
#include <QtGui/QVector4D>
#include <QtQuick/QQuickItem>

#include <algorithm>
#include <optional>
#include <utility>
#include <vector>

namespace vlqt {
namespace {

/* Value type of a MULTI entry key (EFFECT_* 833..855), 0 = not a key (protocol.h "Effect chain"). */
quint16 effectValueType(quint16 prop)
{
    switch (prop) {
    case VLQT_MULTI_EFFECT_KEY_BLUR_ENABLED:
    case VLQT_MULTI_EFFECT_KEY_SHADOW_ENABLED:
    case VLQT_MULTI_EFFECT_KEY_AUTO_PADDING:
    case VLQT_MULTI_EFFECT_KEY_MASK_INVERTED:
        return VLQT_VALUE_TYPE_BOOL;
    case VLQT_MULTI_EFFECT_KEY_BLUR_MAX: return VLQT_VALUE_TYPE_I32;
    case VLQT_MULTI_EFFECT_KEY_SHADOW_COLOR:
    case VLQT_MULTI_EFFECT_KEY_COLORIZATION_COLOR:
        return VLQT_VALUE_TYPE_COLOR;
    case VLQT_MULTI_EFFECT_KEY_PADDING_RECT: return VLQT_VALUE_TYPE_RECTF;
    case VLQT_MULTI_EFFECT_KEY_MASK_SOURCE: return VLQT_VALUE_TYPE_REF;
    default:
        return prop >= VLQT_MULTI_EFFECT_KEY_BLUR_ENABLED && prop <= VLQT_MULTI_EFFECT_KEY_MASK_INVERTED ? VLQT_VALUE_TYPE_F32 : 0;
    }
}

/* Copies a value (type already checked, size = fixedValueSize >= 0, vp holds it padded) from the wire; refs stay ids. */
EffectValue readEffectValue(quint16 vt, const unsigned char* vp)
{
    EffectValue v;
    v.type = vt;
    const int size = fixedValueSize(vt);
    WireReader r(vp, size);
    switch (vt) {
    case VLQT_VALUE_TYPE_I32: v.i = r.i32(); break;
    case VLQT_VALUE_TYPE_IVEC2:
        v.i = r.i32();
        v.i2 = r.i32();
        break;
    case VLQT_VALUE_TYPE_BOOL: v.b = r.u8() != 0; break;
    case VLQT_VALUE_TYPE_U64:
    case VLQT_VALUE_TYPE_SAMPLER: v.u = r.u64(); break;
    case VLQT_VALUE_TYPE_REF: v.ref = r.u32(); break;
    default:
        // f32, color, rectf, mat4, vec2/3/4; variable-length types never get here (fixedValueSize < 0 is rejected)
        if (size > 0)
            r.copy(v.f, size);
        break;
    }
    return v;
}

} // namespace

// ---------------------------------------------------------------------------------------------
// The op table: one row per opcode of protocol.h. apply checks the payload length of a command against its row, takes
// the id (and property) for the structured error from it and calls its decoder. A new command adds its opcode to
// protocol.h and Protocol.cs, one row here and its decoder (docs/contributing/architecture.md "Adding a command").

struct Scene::Op {
    quint16 op;
    const char* name; // in the error text: "op 4 SET"
    bool hasId; // the payload starts with the u32 id of the object it addresses (ApplyError::id)
    bool hasProp; // the id is followed by the u16 property it sets (ApplyError::prop)
    int payloadLen; // exact payload length, or the minimum when variable is true
    bool variable; // the decoder checks the length beyond payloadLen
    int (Scene::*decode)(WireReader& payload, std::string& err); // gets the payload only, the length checked as above
};

const Scene::Op* Scene::findOp(quint16 op)
{
    static constexpr Op ops[] = {
        // op, name, hasId, hasProp, payloadLen, variable, decode
        {VLQT_OP_CREATE, "CREATE", true, false, VLQT_CREATE_PAYLOAD, false, &Scene::opCreate},
        {VLQT_OP_DESTROY, "DESTROY", true, false, 4, false, &Scene::opDestroy},
        {VLQT_OP_REPARENT, "REPARENT", true, false, VLQT_REPARENT_PAYLOAD, false, &Scene::opReparent},
        {VLQT_OP_SET, "SET", true, true, VLQT_SET_FIELDS_SIZE, true, &Scene::opSet},
        {VLQT_OP_BIND, "BIND", true, false, VLQT_BIND_PAYLOAD, false, &Scene::opBind},
        {VLQT_OP_SET_CLEAR_COLOR, "CLEAR_COLOR", false, false, 16, false, &Scene::opSetClearColor},
        {VLQT_OP_RESET, "RESET", false, false, 0, false, &Scene::opReset},
        {VLQT_OP_SET_STYLE, "SET_STYLE", true, false, VLQT_LIST_FIELDS_SIZE, true, &Scene::opSetStyle},
        {VLQT_OP_SET_CLEAR, "SET_CLEAR", false, false, 4, false, &Scene::opSetClear},
        {VLQT_OP_SET_SPACE, "SET_SPACE", false, false, 4, false, &Scene::opSetSpace},
        {VLQT_OP_SET_UNIFORMS, "SET_UNIFORMS", true, false, VLQT_LIST_FIELDS_SIZE, true, &Scene::opSetUniforms},
        {VLQT_OP_SET_EFFECT_CHAIN, "SET_EFFECT_CHAIN", true, false, VLQT_LIST_FIELDS_SIZE, true, &Scene::opSetEffectChain},
        {VLQT_OP_SET_QML_PROPERTIES, "SET_QML_PROPERTIES", true, false, VLQT_LIST_FIELDS_SIZE, true, &Scene::opSetQmlProperties},
    };
    for (const Op& o : ops)
        if (o.op == op)
            return &o;
    return nullptr;
}

ElementEntry* Scene::findElement(quint32 id, const char* what, int& rc, std::string& err)
{
    auto it = nodes.find(id);
    if (it != nodes.end())
        return &it->second;
    if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
        err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id)) + " has no " + what;
        rc = ApplyUnknownProperty;
    } else {
        err = "unknown id " + std::to_string(id);
        rc = ApplyUnknownId;
    }
    return nullptr;
}

int Scene::apply(const unsigned char* buf, int len, std::string& err)
{
    // Former effect sources destroyed in an earlier apply: the frame in between has polished the effects onto
    // their new sources, so nothing refers to them any more (see destroySubtree).
    deleteRetired();
    appliedSincePoll = true; // also a failed apply may have applied its first commands
    lastApplyError = ApplyError{};
    // QML files are compiled once all commands of the frame have set their properties (also after a failed apply).
    struct BuildQml {
        Elements& elements;
        ~BuildQml() { elements.buildQml(); }
    } buildQml{elements};
    if (len < 0 || (len > 0 && !buf)) {
        err = "invalid buffer (null or negative length)";
        lastApplyError = ApplyError{ApplyBadLength, 0, 0, 0, 0, "invalid buffer (null or negative length)"};
        return ApplyBadLength;
    }
    WireReader stream(buf, len);
    int index = 0;
    while (stream.remaining() > 0) {
        const int off = stream.offset();
        const Op* info = nullptr;
        quint16 op = 0;
        quint32 cmdId = 0;
        quint16 cmdProp = 0;
        int rc = ApplyOk;
        std::string reason;
        if (stream.remaining() < VLQT_HEADER_SIZE) {
            rc = ApplyBadLength;
            reason = "truncated header (" + std::to_string(stream.remaining()) + " bytes left)";
        } else {
            WireReader header = stream;
            op = header.u16();
            const quint16 flags = header.u16();
            const quint32 plen32 = header.u32();
            // Within the cap, so plen and size fit an int (len is an int).
            const bool overCap = plen32 > quint32(VLQT_MAX_COMMAND_PAYLOAD);
            const int plen = overCap ? 0 : int(plen32);
            const int size = align4(VLQT_HEADER_SIZE + plen);
            info = findOp(op);
            if (overCap) {
                rc = ApplyBadLength;
                reason = "payloadLen " + std::to_string(plen32) + " above VLQT_MAX_COMMAND_PAYLOAD ("
                    + std::to_string(VLQT_MAX_COMMAND_PAYLOAD) + ")";
            } else if (size > stream.remaining()) {
                rc = ApplyBadLength;
                reason = "command (" + std::to_string(size) + " bytes incl. padding) exceeds buffer ("
                    + std::to_string(stream.remaining()) + " bytes left)";
            } else if (flags != 0) {
                rc = ApplyBadValue;
                reason = "header flags " + std::to_string(flags) + ", expected 0 (reserved)";
            } else {
                WireReader payload = header.sub(plen); // fits: size checked against remaining() above
                if (info && info->hasId) {
                    WireReader probe = payload; // a payload too short for them leaves the id and the property 0
                    cmdId = probe.u32();
                    if (info->hasProp)
                        cmdProp = probe.u16();
                }
                if (!info) {
                    rc = ApplyUnknownOp;
                    reason = "unknown opcode";
                } else if (!info->variable && plen != info->payloadLen) {
                    rc = ApplyBadLength;
                    reason = "payloadLen " + std::to_string(plen) + ", expected " + std::to_string(info->payloadLen);
                } else if (info->variable && plen < info->payloadLen) {
                    rc = ApplyBadLength;
                    reason = "payloadLen " + std::to_string(plen) + " too short for " + info->name + " (>= "
                        + std::to_string(info->payloadLen) + ")";
                } else {
                    rc = (this->*info->decode)(payload, reason);
                }
            }
            if (rc == ApplyOk)
                stream.bytes(size); // fits: checked against remaining() above
        }
        if (rc != ApplyOk) {
            err = "apply: command #" + std::to_string(index) + " (op " + std::to_string(op) + " " + (info ? info->name : "?")
                + (cmdId ? ", id " + std::to_string(cmdId) : std::string()) + ", offset " + std::to_string(off)
                + "): " + reason;
            lastApplyError = ApplyError{rc, index, cmdId, op, cmdProp, std::move(reason)};
            return rc;
        }
        ++index;
    }
    return ApplyOk;
}

// ---------------------------------------------------------------------------------------------
// Decoders of the op table.

int Scene::opCreate(WireReader& r, std::string& err)
{
    const quint32 id = r.u32();
    const quint16 type = r.u16();
    return create(id, type, err);
}

int Scene::opDestroy(WireReader& r, std::string& err)
{
    return destroy(r.u32(), err);
}

int Scene::opReparent(WireReader& r, std::string& err)
{
    const quint32 id = r.u32();
    const quint32 parent = r.u32();
    const quint32 at = r.u32();
    return reparent(id, parent, at, err);
}

int Scene::opSet(WireReader& r, std::string& err)
{
    const quint32 id = r.u32();
    const quint16 prop = r.u16();
    Value v;
    v.type = r.u16();
    const int vlen = r.remaining();
    const int fixed = valueSize(v.type);
    if (fixed < 0) {
        err = "unknown value type " + std::to_string(v.type);
        return ApplyBadValue;
    }
    if (v.type == VLQT_VALUE_TYPE_LIST || v.type == VLQT_VALUE_TYPE_DATE_TIME || v.type == VLQT_VALUE_TYPE_FONT) {
        err = "value type " + std::to_string(v.type) + " (a QML value) only goes into SET_QML_PROPERTIES";
        return ApplyBadValue;
    }
    // Variable length: the fixed part holds the counts (64 bit, a u32 count must not overflow the comparison). A value
    // shorter than its fixed part reads the counts as 0 and fails the comparison below.
    WireReader counts = r;
    qint64 expected = fixed;
    if (v.type == VLQT_VALUE_TYPE_STRING) {
        expected = 4 + qint64(counts.peek<quint32>());
    } else if (v.type == VLQT_VALUE_TYPE_F32_ARRAY) {
        expected = 4 + 4 * qint64(counts.peek<quint32>());
    } else if (v.type == VLQT_VALUE_TYPE_PATH) {
        const qint64 verbs = counts.u32();
        const qint64 points = counts.u32();
        expected = 8 + align4(verbs) + 8 * points;
    }
    if (vlen != expected) {
        err = "value length " + std::to_string(vlen) + ", expected " + std::to_string(expected)
            + " for value type " + std::to_string(v.type);
        return ApplyBadLength;
    }

    auto it = nodes.find(id);
    auto hit = it == nodes.end() ? handlerNodes.find(id) : handlerNodes.end();
    if (it == nodes.end() && hit == handlerNodes.end()) {
        err = id == VLQT_ROOT_ID ? "the window root has no properties" : "unknown id " + std::to_string(id);
        return id == VLQT_ROOT_ID ? ApplyInvalid : ApplyUnknownId;
    }

    // The length is exact now, so the reads below stay inside the value.
    switch (v.type) {
    case VLQT_VALUE_TYPE_F32:
    case VLQT_VALUE_TYPE_COLOR:
    case VLQT_VALUE_TYPE_RECTF:
    case VLQT_VALUE_TYPE_MAT4:
    case VLQT_VALUE_TYPE_VEC2:
    case VLQT_VALUE_TYPE_VEC3:
    case VLQT_VALUE_TYPE_VEC4:
        r.copy(v.f, fixed);
        break;
    case VLQT_VALUE_TYPE_I32: v.i = r.i32(); break;
    case VLQT_VALUE_TYPE_IVEC2:
        v.i = r.i32();
        v.i2 = r.i32();
        break;
    case VLQT_VALUE_TYPE_BOOL: v.b = r.u8() != 0; break;
    case VLQT_VALUE_TYPE_U64: v.u = r.u64(); break;
    case VLQT_VALUE_TYPE_STRING: {
        const quint32 n = r.u32();
        v.utf8 = QByteArray::fromRawData(reinterpret_cast<const char*>(r.bytes(n)), qsizetype(n));
        break;
    }
    case VLQT_VALUE_TYPE_F32_ARRAY:
        v.floatCount = r.u32();
        v.floats = r.cursor();
        break;
    case VLQT_VALUE_TYPE_PATH: {
        v.verbCount = r.u32();
        v.pointCount = r.u32();
        v.verbs = r.bytes(v.verbCount, true);
        v.points = r.cursor();
        quint32 points = 0;
        for (quint32 i = 0; i < v.verbCount; ++i) {
            const int n = verbPoints(v.verbs[i]);
            if (n < 0) {
                err = "path verb #" + std::to_string(i) + ": unknown verb " + std::to_string(v.verbs[i]);
                return ApplyBadValue;
            }
            points += quint32(n);
        }
        if (points != v.pointCount) {
            err = "path: " + std::to_string(v.pointCount) + " points, the verbs take " + std::to_string(points);
            return ApplyBadValue;
        }
        break;
    }
    case VLQT_VALUE_TYPE_REF: {
        const quint32 ref = r.u32();
        if (ref != VLQT_ROOT_ID) {
            auto rit = nodes.find(ref);
            if (rit == nodes.end()) {
                err = "unknown ref id " + std::to_string(ref);
                return ApplyUnknownId;
            }
            v.ref = rit->second.item;
        }
        break;
    }
    }

    if (hit != handlerNodes.end())
        return setHandler(*this, hit->second, prop, v, err);
    return elements.set(it->second, prop, v, err);
}

int Scene::opBind(WireReader& r, std::string& err)
{
    const quint32 id = r.u32();
    const quint32 feedbackId = r.u32();
    if (auto it = nodes.find(id); it != nodes.end()) {
        bindNode(*this, it->second, feedbackId);
        return ApplyOk;
    }
    if (auto hit = handlerNodes.find(id); hit != handlerNodes.end()) {
        bindHandler(*this, hit->second, feedbackId);
        return ApplyOk;
    }
    err = "unknown id " + std::to_string(id);
    return ApplyUnknownId;
}

int Scene::opSetClearColor(WireReader& r, std::string&)
{
    float f[4];
    r.copy(f, sizeof f);
    clearState.setColor(toColor(f), window);
    return ApplyOk;
}

int Scene::opReset(WireReader&, std::string&)
{
    reset();
    return ApplyOk;
}

namespace {

/* The SET_STYLE entries (u32 id, u16 count, u16 flags, then count x (u16 field, u8 state, u8 value type, value, padded
 * to 4 bytes), protocol.h "Style") on the Controls element item. payload starts at the id; its length is checked
 * against the minimum by the op table. The whole command is checked before the table is touched, so a bad entry
 * leaves the style as it was: pass 0 checks every entry, pass 1 writes the table. */
int applyStyleEntries(QQuickItem* item, WireReader payload, std::string& err)
{
    WireReader head = payload;
    head.u32(); // id, the caller has looked it up
    const int count = head.u16();
    const quint16 flags = head.u16();
    if (flags & ~quint16(VLQT_STYLE_SET_CLEAR)) {
        err = "unknown SET_STYLE flags " + std::to_string(flags);
        return ApplyUnknownProperty;
    }
    VlStyle* style = nullptr;
    for (int pass = 0; pass < 2; ++pass) {
        if (pass == 1) {
            style = qobject_cast<VlStyle*>(qmlAttachedPropertiesObject<VlStyle>(item, true));
            if (!style) {
                err = "VlStyle attached object unavailable (type not registered)";
                return ApplyInvalid;
            }
            if (flags & VLQT_STYLE_SET_CLEAR)
                style->clear();
        }
        WireReader r = head;
        for (int e = 0; e < count; ++e) {
            const int off = r.offset();
            auto at = [&] { return "entry " + std::to_string(e) + " (offset " + std::to_string(off) + ")"; };
            if (r.remaining() < VLQT_ENTRY_HEADER_SIZE) {
                err = at() + ": truncated (" + std::to_string(r.remaining()) + " bytes left)";
                return ApplyBadLength;
            }
            const quint16 field = r.u16();
            const int state = r.u8();
            Value v;
            v.type = r.u8();
            const std::optional<VlStyle::Slot> slot = VlStyle::slotOf(field);
            if (!slot) {
                err = at() + ": unknown style field " + std::to_string(field);
                return ApplyUnknownProperty;
            }
            if (state >= VlStyle::stateCount) {
                err = at() + ": unknown style state " + std::to_string(state);
                return ApplyUnknownProperty;
            }
            const quint16 expected = VlStyle::valueTypeOf(*slot);
            if (v.type != expected) {
                err = at() + ": value type " + std::to_string(v.type) + " for style field " + std::to_string(field)
                    + ", expected " + std::to_string(expected);
                return ApplyBadValue;
            }
            qint64 size = 0;
            switch (v.type) {
            case VLQT_VALUE_TYPE_COLOR: size = 16; break;
            case VLQT_VALUE_TYPE_F32: size = 4; break;
            case VLQT_VALUE_TYPE_I32: size = 4; break;
            case VLQT_VALUE_TYPE_STRING: size = 4 + qint64(r.peek<quint32>()); break; // peek: 0 when the length is cut off
            default: break;
            }
            if (size > r.remaining()) {
                err = at() + ": value needs " + std::to_string(size) + " bytes, " + std::to_string(r.remaining()) + " left";
                return ApplyBadLength;
            }
            WireReader value = r.sub(size);
            if (pass == 1) {
                switch (v.type) {
                case VLQT_VALUE_TYPE_COLOR: value.copy(v.f, 16); break;
                case VLQT_VALUE_TYPE_F32: value.copy(v.f, 4); break;
                case VLQT_VALUE_TYPE_I32: v.i = value.i32(); break;
                case VLQT_VALUE_TYPE_STRING: {
                    const quint32 n = value.u32();
                    v.utf8 = QByteArray::fromRawData(reinterpret_cast<const char*>(value.bytes(n)), qsizetype(n));
                    break;
                }
                default: break;
                }
                style->setCell(*slot, state, v);
            }
            r.align();
        }
        // payloadLen excludes the padding of the last entry (like every command); the padded end is accepted too.
        if (pass == 0 && r.remaining() != 0) {
            err = "payloadLen " + std::to_string(payload.remaining()) + " does not match " + std::to_string(count)
                + " entries (" + std::to_string(align4(r.offset())) + " bytes incl. padding)";
            return ApplyBadLength;
        }
    }
    style->commit();
    return ApplyOk;
}

} // namespace

int Scene::opSetStyle(WireReader& r, std::string& err)
{
    int rc = ApplyOk;
    ElementEntry* entry = findElement(r.peek<quint32>(), "style (Controls elements only)", rc, err);
    if (!entry)
        return rc;
    const quint16 type = entry->type;
    if (!entry->def->styled) {
        err = "type " + std::to_string(type) + " has no style (Controls elements only)";
        return ApplyUnknownProperty;
    }
    return applyStyleEntries(entry->item, r, err);
}

int Scene::opSetClear(WireReader& r, std::string&)
{
    clearState.setClear(r.u32() != 0, window, root);
    return ApplyOk;
}

int Scene::opSetSpace(WireReader& r, std::string& err)
{
    return setSpace(r.u32(), err);
}

int Scene::opSetUniforms(WireReader& r, std::string& err)
{
    // Validates all entries first (wire format: protocol.h "SET_UNIFORMS"), then hands them to the ShaderItem;
    // unknown names or wrong types become the element's error text (ShaderItem::rebuild).
    const quint32 id = r.u32();
    const int count = r.u16();
    const quint16 flags = r.u16();
    int rc = ApplyOk;
    ElementEntry* entry = findElement(id, "uniforms (ShaderEffect only)", rc, err);
    if (!entry)
        return rc;
    if (entry->type != VLQT_ELEMENT_TYPE_SHADER) {
        err = "type " + std::to_string(entry->type) + " has no uniforms (ShaderEffect only)";
        return ApplyUnknownProperty;
    }
    NamedEntryRules rules;
    rules.noun = "uniform";
    rules.sampler = true;
    entryScratch.clear();
    for (int i = 0; i < count; ++i) {
        NamedEntry e;
        rc = readNamedEntry(r, rules, i, e, err);
        if (rc != ApplyOk)
            return rc;
        if (e.type == VLQT_VALUE_TYPE_REF) {
            const quint32 ref = readLE<quint32>(e.value);
            if (ref != VLQT_ROOT_ID && !nodes.count(ref)) {
                err = "uniform #" + std::to_string(i) + ": unknown ref id " + std::to_string(ref);
                return ApplyUnknownId;
            }
        }
        entryScratch.push_back(e);
    }
    if (r.remaining() != 0) {
        err = "SET_UNIFORMS: " + std::to_string(r.remaining()) + " bytes after the last entry";
        return ApplyBadLength;
    }
    // Valid as a whole: apply (content problems become the element's error text, ShaderItem::rebuild).
    auto* item = static_cast<ShaderItem*>(entry->item);
    item->beginUniforms((flags & VLQT_UNIFORM_SET_CLEAR) != 0);
    for (const NamedEntry& e : entryScratch) {
        QQuickItem* ref = nullptr;
        if (e.type == VLQT_VALUE_TYPE_REF) {
            const auto it = nodes.find(readLE<quint32>(e.value));
            ref = it != nodes.end() ? it->second.item : nullptr; // 0 (root) = no texture
        }
        item->setUniform(reinterpret_cast<const char*>(e.name), e.nameLen, e.type, e.value, ref);
    }
    item->endUniforms();
    elements.syncTextureGroups(*entry); // Groups as textures go through their frame proxy
    item->refreshLayerRequests(true); // their proxies carry the layer mipmaps / format of the samplers
    return ApplyOk;
}

int Scene::opSetQmlProperties(WireReader& r, std::string& err)
{
    // Wire format: protocol.h head comment (entries like SET_UNIFORMS; a STRING value is u32 length + UTF-8, a LIST value is
    // checked with the entry, readListLength). The command is validated as a whole first; content problems (unknown name,
    // failed conversion, unsupported value type) do not fail the apply: they are error lines of the element (QmlItem::error,
    // EVENT_ERROR).
    const quint32 id = r.u32();
    const int count = r.u16();
    r.u16(); // flags, reserved
    int rc = ApplyOk;
    ElementEntry* entry = findElement(id, "QML properties (QmlElement only)", rc, err);
    if (!entry)
        return rc;
    if (entry->type != VLQT_ELEMENT_TYPE_QML) {
        err = "type " + std::to_string(entry->type) + " has no QML properties (QmlElement only)";
        return ApplyUnknownProperty;
    }
    NamedEntryRules rules;
    rules.noun = "QML property";
    rules.string = true;
    rules.list = true;
    rules.qml = true;
    entryScratch.clear();
    for (int i = 0; i < count; ++i) {
        NamedEntry e;
        rc = readNamedEntry(r, rules, i, e, err);
        if (rc != ApplyOk)
            return rc;
        entryScratch.push_back(e);
    }
    if (r.remaining() != 0) {
        err = "SET_QML_PROPERTIES: " + std::to_string(r.remaining()) + " bytes after the last entry";
        return ApplyBadLength;
    }
    auto* item = static_cast<QmlItem*>(entry->item);
    for (const NamedEntry& e : entryScratch) {
        const QByteArrayView name(reinterpret_cast<const char*>(e.name), e.nameLen);
        WireReader v(e.value, e.valueLen);
        float f[4] = {};
        switch (e.type) {
        case VLQT_VALUE_TYPE_F32: item->setRootProperty(name, double(v.f32())); break;
        case VLQT_VALUE_TYPE_I32: item->setRootProperty(name, int(v.i32())); break;
        case VLQT_VALUE_TYPE_BOOL: item->setRootProperty(name, v.u8() != 0); break;
        case VLQT_VALUE_TYPE_STRING: {
            const quint32 n = v.u32();
            item->setRootProperty(name, QString::fromUtf8(reinterpret_cast<const char*>(v.bytes(n)), qsizetype(n)));
            break;
        }
        case VLQT_VALUE_TYPE_COLOR:
            v.copy(f, 16);
            item->setRootProperty(name, QColor::fromRgbF(f[0], f[1], f[2], f[3]));
            break;
        case VLQT_VALUE_TYPE_RECTF:
            v.copy(f, 16);
            item->setRootProperty(name, QRectF(f[0], f[1], f[2], f[3]));
            break;
        case VLQT_VALUE_TYPE_VEC2:
            v.copy(f, 8);
            item->setRootProperty(name, QVector2D(f[0], f[1]));
            break;
        case VLQT_VALUE_TYPE_VEC3:
            v.copy(f, 12);
            item->setRootProperty(name, QVector3D(f[0], f[1], f[2]));
            break;
        case VLQT_VALUE_TYPE_VEC4:
            v.copy(f, 16);
            item->setRootProperty(name, QVector4D(f[0], f[1], f[2], f[3]));
            break;
        case VLQT_VALUE_TYPE_LIST: item->setRootProperty(name, listFromWire(v)); break; // checked by readNamedEntry
        case VLQT_VALUE_TYPE_DATE_TIME: item->setRootProperty(name, dateTimeFromWire(qint64(v.u64()))); break;
        case VLQT_VALUE_TYPE_FONT: item->setRootProperty(name, fontFromWire(v)); break; // length checked by readNamedEntry
        default:
            item->rejectRootProperty(QString::fromUtf8(name), QStringLiteral("value type %1 is not supported for QML properties").arg(e.type));
            break;
        }
    }
    return ApplyOk;
}

int Scene::opSetEffectChain(WireReader& r, std::string& err)
{
    // Validates the whole command first (wire format: protocol.h "Effect chain", ids, masks), so a rejected one changes
    // nothing. A texture problem is no rejection but an EVENT_ERROR line of the chain. Elements::setEffectChain fails
    // only when an item of the chain cannot be created (QML error); the old chain is gone by then, so the chain is
    // switched off and the masks and textures this command parked leave the tree again.
    const quint32 id = r.u32();
    const int linkCount = r.u16();
    r.u16(); // flags, reserved
    int rc = ApplyOk;
    ElementEntry* entry = findElement(id, "effect chain (elements only)", rc, err);
    if (!entry)
        return rc;
    auto knownRef = [&](quint32 ref) { return ref == VLQT_ROOT_ID || nodes.count(ref) != 0; };
    EffectChainDesc chain;
    chain.links.reserve(size_t(linkCount));
    for (int l = 0; l < linkCount; ++l) {
        const std::string at = "link #" + std::to_string(l);
        if (r.remaining() < VLQT_EFFECT_CHAIN_LINK_HEADER_SIZE) {
            err = at + ": truncated link header";
            return ApplyBadLength;
        }
        EffectLinkDesc link;
        link.kind = r.u16();
        const int count = r.u16();
        const quint32 bodyLen = r.u32();
        if ((bodyLen & (VLQT_COMMAND_ALIGN - 1)) != 0 || bodyLen > quint32(r.remaining())) {
            err = at + ": bodyLen " + std::to_string(bodyLen) + " not aligned or exceeds the payload";
            return ApplyBadLength;
        }
        WireReader body = r.sub(bodyLen);
        if (link.kind == VLQT_EFFECT_CHAIN_LINK_MULTI) {
            for (int i = 0; i < count; ++i) {
                // Built on the error path only: the text is longer than the small-string buffer.
                auto ie = [&] { return at + " entry #" + std::to_string(i); };
                if (body.remaining() < 4) {
                    err = ie() + ": truncated entry";
                    return ApplyBadLength;
                }
                const quint16 prop = body.u16();
                const quint16 vt = body.u16();
                const quint16 expected = effectValueType(prop);
                if (!expected) {
                    err = ie() + ": property " + std::to_string(prop) + " is no MULTI key (EFFECT_* 833..855)";
                    return ApplyUnknownProperty;
                }
                if (vt != expected) {
                    err = ie() + ": value type " + std::to_string(vt) + ", property " + std::to_string(prop) + " takes "
                        + std::to_string(expected);
                    return ApplyBadValue;
                }
                const unsigned char* vp = body.bytes(fixedValueSize(vt), true);
                if (!vp) {
                    err = ie() + ": entry exceeds the body";
                    return ApplyBadLength;
                }
                EffectMultiEntry multi{prop, readEffectValue(vt, vp)};
                if (vt == VLQT_VALUE_TYPE_REF && !knownRef(multi.value.ref)) {
                    err = ie() + ": unknown ref id " + std::to_string(multi.value.ref);
                    return ApplyUnknownId;
                }
                link.multi.push_back(multi);
            }
        } else if (link.kind == VLQT_EFFECT_CHAIN_LINK_SHADER) {
            if (body.remaining() < 4) {
                err = at + ": truncated SHADER head";
                return ApplyBadLength;
            }
            link.templ = body.u16();
            const int shaderCount = body.u16();
            int roles = 0;
            switch (link.templ) {
            case VLQT_EFFECT_TEMPLATE_SINGLE:
            case VLQT_EFFECT_TEMPLATE_TWO_PASS: roles = 1; break;
            case VLQT_EFFECT_TEMPLATE_PYRAMID: roles = 2; break;
            default:
                err = at + ": unknown template " + std::to_string(link.templ);
                return ApplyBadValue;
            }
            if (shaderCount != roles) {
                err = at + ": template " + std::to_string(link.templ) + " takes " + std::to_string(roles)
                    + " shader id(s), got " + std::to_string(shaderCount);
                return ApplyBadValue;
            }
            static_assert(VLQT_EFFECT_CHAIN_LINK_SHADER_FIXED_SIZE == sizeof(link.padding) + sizeof(float), "padding and strength");
            if (body.remaining() < shaderCount * 8 + VLQT_EFFECT_CHAIN_LINK_SHADER_FIXED_SIZE) {
                err = at + ": truncated SHADER head";
                return ApplyBadLength;
            }
            for (int s = 0; s < shaderCount; ++s)
                link.shaders.push_back(body.u64());
            body.copy(link.padding, sizeof link.padding);
            link.strength = body.f32();
            NamedEntryRules rules;
            rules.noun = "uniform";
            rules.scope = &at;
            rules.region = "body";
            rules.sampler = true;
            for (int i = 0; i < count; ++i) {
                NamedEntry e;
                rc = readNamedEntry(body, rules, i, e, err);
                if (rc != ApplyOk)
                    return rc;
                EffectUniform u{QByteArray(reinterpret_cast<const char*>(e.name), e.nameLen), readEffectValue(e.type, e.value)};
                if (e.type == VLQT_VALUE_TYPE_REF && !knownRef(u.value.ref)) {
                    err = at + " uniform #" + std::to_string(i) + ": unknown ref id " + std::to_string(u.value.ref);
                    return ApplyUnknownId;
                }
                link.uniforms.push_back(std::move(u));
            }
        } else {
            err = at + ": unknown kind " + std::to_string(link.kind);
            return ApplyBadValue;
        }
        if (body.remaining() != 0) {
            err = at + ": " + std::to_string(body.remaining()) + " bytes after the last entry of the body";
            return ApplyBadLength;
        }
        chain.links.push_back(std::move(link));
    }
    if (r.remaining() != 0) {
        err = "SET_EFFECT_CHAIN: " + std::to_string(r.remaining()) + " bytes after the last link";
        return ApplyBadLength;
    }
    // Masks (protocol.h "Effect chain", one per MULTI run): all checked before anything changes.
    ElementEntry& host = *entry;
    std::vector<QQuickItem*> masks(chain.links.size(), nullptr);
    for (size_t l = 0; l < chain.links.size(); ++l)
        for (const EffectMultiEntry& e : chain.links[l].multi) {
            if (e.prop != VLQT_MULTI_EFFECT_KEY_MASK_SOURCE || e.value.ref == VLQT_ROOT_ID)
                continue;
            const ElementEntry& mask = nodes[e.value.ref]; // existence checked above (knownRef)
            // Built on the error path only (a heap allocation per mask otherwise).
            auto at = [&] { return "link #" + std::to_string(l) + ": mask " + std::to_string(e.value.ref); };
            for (QQuickItem* a = host.item; a; a = a->parentItem())
                if (a == mask.item) {
                    err = at() + " is the element itself or one of its ancestors";
                    return ApplyInvalid;
                }
            if (mask.maskHost && mask.maskHost != host.item) {
                err = at() + " is attached to another element";
                return ApplyInvalid;
            }
            if (!mask.maskHost && mask.item->parentItem() && !mask.maskHidden) {
                err = at() + " already has a parent (REPARENT)";
                return ApplyInvalid;
            }
            if (std::find(masks.begin(), masks.end(), mask.item) != masks.end()) {
                err = at() + " is the mask of another link";
                return ApplyInvalid;
            }
            masks[l] = mask.item;
        }
    const std::vector<QQuickItem*> before = elements.effectMasks(host);
    // Elements this command parks that were not parked before, with their visibility. The checks above leave only
    // elements without a parent for that, so a failed apply puts them back out of the tree as they were.
    std::vector<std::pair<QQuickItem*, bool>> parked;
    auto park = [&](ElementEntry& e) {
        if (!e.maskHidden)
            parked.emplace_back(e.item, e.item->isVisible());
        if (e.item->parentItem() != root)
            e.item->setParentItem(root);
        e.item->setVisible(false);
        e.maskHidden = true;
    };
    // A new mask is parked hidden under the root: outside the element's children, so it never counts for its layout,
    // childrenRect/auto size, hit-test or REPARENT indices. Elements moves it into its frame there.
    for (QQuickItem* m : masks)
        if (m && std::find(before.begin(), before.end(), m) == before.end())
            park(nodes[ids[m]]);
    // Texture uniforms of SHADER links (REF): an element of its own that the host never REPARENTs, parked hidden under the
    // root like a mask (rendered through its layer, ShaderItem::setUniform); problems are EVENT_ERROR lines.
    for (EffectLinkDesc& link : chain.links)
        for (EffectUniform& u : link.uniforms) {
            if (u.value.type != VLQT_VALUE_TYPE_REF || u.value.ref == VLQT_ROOT_ID)
                continue;
            ElementEntry& tex = nodes[u.value.ref]; // existence checked above (knownRef)
            bool ancestor = false;
            for (QQuickItem* a = host.item; a; a = a->parentItem())
                ancestor = ancestor || a == tex.item;
            if (ancestor)
                u.refError = "the texture element must not be the element or one of its ancestors";
            else if (tex.maskHost || std::find(masks.begin(), masks.end(), tex.item) != masks.end())
                u.refError = "the texture element is the mask of an element";
            else if (tex.item->parentItem() && !tex.maskHidden)
                u.refError = "the texture element has a parent (REPARENT); a texture of a chain link is an element of its own";
            else if (isGroup(tex.item))
                u.refError = "a Group as texture of a chain link is not supported yet";
            if (u.refError)
                continue;
            park(tex); // until the chain lets it go and a REPARENT or DESTROY comes (deleted one frame late, like a former source)
            tex.chainTexture = true;
            u.refItem = tex.item;
        }
    rc = elements.setEffectChain(host, chain, masks, err);
    if (rc != ApplyOk) {
        elements.releaseEffectLayer(host); // detaches the masks: parked next to their frames again
        for (const auto& [item, visible] : parked) {
            item->setParentItem(nullptr);
            item->setVisible(visible);
            nodes[ids[item]].maskHidden = false;
        }
    }
    listPolled(*this, host); // a chain reports its content errors (EVENT_ERROR)
    const std::vector<QQuickItem*> after = elements.effectMasks(host);
    for (QQuickItem* m : before)
        if (std::find(after.begin(), after.end(), m) == after.end())
            if (auto mit = ids.find(m); mit != ids.end())
                nodes[mit->second].maskHost = nullptr; // detached: stays maskHidden (parked) until REPARENT or DESTROY
    for (QQuickItem* m : after)
        nodes[ids[m]].maskHost = host.item;
    return rc;
}

} // namespace vlqt
