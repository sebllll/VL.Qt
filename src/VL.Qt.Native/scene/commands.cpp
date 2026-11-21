#include "scene/commands.h"
#include "effects/effect_chain_desc.h"
#include "effects/shader_item.h"
#include "elements/group_item.h"
#include "qmlitem.h"
#include "scene/scene.h"
#include "vlstyle.h"
#include "wire.h"

#include <QtCore/QRectF>
#include <QtGui/QColor>
#include <QtGui/QVector2D>
#include <QtGui/QVector3D>
#include <QtGui/QVector4D>
#include <QtQuick/QQuickItem>

#include <algorithm>
#include <utility>
#include <vector>

namespace vlqt {
namespace {

const char* opName(quint16 op)
{
    switch (op) {
    case VLQT_OP_CREATE: return "CREATE";
    case VLQT_OP_DESTROY: return "DESTROY";
    case VLQT_OP_REPARENT: return "REPARENT";
    case VLQT_OP_SET: return "SET";
    case VLQT_OP_BIND: return "BIND";
    case VLQT_OP_SET_CLEAR_COLOR: return "CLEAR_COLOR";
    case VLQT_OP_RESET: return "RESET";
    case VLQT_OP_SET_STYLE: return "SET_STYLE";
    case VLQT_OP_SET_CLEAR: return "SET_CLEAR";
    case VLQT_OP_SET_SPACE: return "SET_SPACE";
    case VLQT_OP_SET_UNIFORMS: return "SET_UNIFORMS";
    case VLQT_OP_SET_EFFECT_CHAIN: return "SET_EFFECT_CHAIN";
    case VLQT_OP_SET_QML_PROPERTIES: return "SET_QML_PROPERTIES";
    default: return "?";
    }
}

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
            auto expect = [&](int n) {
                if (plen == n)
                    return true;
                rc = ApplyBadLength;
                reason = "payloadLen " + std::to_string(plen) + ", expected " + std::to_string(n);
                return false;
            };
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
                const unsigned char* p = header.cursor();
                WireReader payload(p, plen);
                // Object addressed by the command, for the structured error: first u32 of every op with an id.
                const bool hasId = op == VLQT_OP_CREATE || op == VLQT_OP_DESTROY || op == VLQT_OP_REPARENT
                    || op == VLQT_OP_SET || op == VLQT_OP_BIND || op == VLQT_OP_SET_STYLE || op == VLQT_OP_SET_UNIFORMS
                    || op == VLQT_OP_SET_EFFECT_CHAIN || op == VLQT_OP_SET_QML_PROPERTIES;
                if (hasId) {
                    WireReader probe = payload; // a payload too short for them leaves the id and the property 0
                    cmdId = probe.u32();
                    if (op == VLQT_OP_SET)
                        cmdProp = probe.u16();
                }
                switch (op) {
                case VLQT_OP_CREATE:
                    if (expect(VLQT_CREATE_PAYLOAD)) {
                        const quint32 id = payload.u32();
                        const quint16 type = payload.u16();
                        rc = create(id, type, reason);
                    }
                    break;
                case VLQT_OP_DESTROY:
                    if (expect(4))
                        rc = destroy(payload.u32(), reason);
                    break;
                case VLQT_OP_REPARENT:
                    if (expect(VLQT_REPARENT_PAYLOAD)) {
                        const quint32 id = payload.u32();
                        const quint32 parent = payload.u32();
                        const quint32 at = payload.u32();
                        rc = reparent(id, parent, at, reason);
                    }
                    break;
                case VLQT_OP_SET:
                    rc = setProperty(p, plen, reason);
                    break;
                case VLQT_OP_SET_STYLE:
                    rc = setStyle(p, plen, reason);
                    break;
                case VLQT_OP_SET_UNIFORMS:
                    rc = setUniforms(p, plen, reason);
                    break;
                case VLQT_OP_SET_EFFECT_CHAIN:
                    rc = setEffectChain(p, plen, reason);
                    break;
                case VLQT_OP_SET_QML_PROPERTIES:
                    rc = setQmlProperties(p, plen, reason);
                    break;
                case VLQT_OP_BIND:
                    if (expect(VLQT_BIND_PAYLOAD)) {
                        const quint32 id = payload.u32();
                        const quint32 feedbackId = payload.u32();
                        auto it = nodes.find(id);
                        auto hit = it == nodes.end() ? handlerNodes.find(id) : handlerNodes.end();
                        if (it != nodes.end()) {
                            bindNode(*this, it->second, feedbackId);
                        } else if (hit != handlerNodes.end()) {
                            bindHandler(*this, hit->second, feedbackId);
                        } else {
                            rc = ApplyUnknownId;
                            reason = "unknown id " + std::to_string(id);
                        }
                    }
                    break;
                case VLQT_OP_SET_CLEAR_COLOR:
                    if (expect(16)) {
                        float f[4];
                        payload.copy(f, sizeof f);
                        clearState.setColor(toColor(f), window);
                    }
                    break;
                case VLQT_OP_SET_CLEAR:
                    if (expect(4))
                        clearState.setClear(payload.u32() != 0, window, root);
                    break;
                case VLQT_OP_SET_SPACE:
                    if (expect(4))
                        rc = setSpace(payload.u32(), reason);
                    break;
                case VLQT_OP_RESET:
                    if (expect(0))
                        reset();
                    break;
                default:
                    rc = ApplyUnknownOp;
                    reason = "unknown opcode";
                    break;
                }
            }
            if (rc == ApplyOk)
                stream.bytes(size); // fits: checked against remaining() above
        }
        if (rc != ApplyOk) {
            err = "apply: command #" + std::to_string(index) + " (op " + std::to_string(op) + " " + opName(op)
                + (cmdId ? ", id " + std::to_string(cmdId) : std::string()) + ", offset " + std::to_string(off)
                + "): " + reason;
            lastApplyError = ApplyError{rc, index, cmdId, op, cmdProp, std::move(reason)};
            return rc;
        }
        ++index;
    }
    return ApplyOk;
}

int Scene::setProperty(const unsigned char* p, int plen, std::string& err)
{
    if (plen < VLQT_SET_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET (>= " + std::to_string(VLQT_SET_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    WireReader r(p, plen);
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

int Scene::setStyle(const unsigned char* p, int plen, std::string& err)
{
    if (plen < VLQT_LIST_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_STYLE (>= " + std::to_string(VLQT_LIST_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    int rc = ApplyOk;
    ElementEntry* entry = findElement(WireReader(p, plen).u32(), "style (Controls elements only)", rc, err);
    if (!entry)
        return rc;
    const quint16 type = entry->type;
    if (type < VLQT_ELEMENT_TYPE_BUTTON || type > VLQT_ELEMENT_TYPE_TEXT_FIELD) {
        err = "type " + std::to_string(type) + " has no style (Controls elements only)";
        return ApplyUnknownProperty;
    }
    return applySetStyle(entry->item, p, plen, err);
}

int Scene::setUniforms(const unsigned char* p, int plen, std::string& err)
{
    // Validates all entries first (wire format: protocol.h "SET_UNIFORMS"), then hands them to the ShaderItem;
    // unknown names or wrong types become the element's error text (ShaderItem::rebuild).
    if (plen < VLQT_LIST_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_UNIFORMS (>= " + std::to_string(VLQT_LIST_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    WireReader r(p, plen);
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

int Scene::setQmlProperties(const unsigned char* p, int plen, std::string& err)
{
    // Wire format: protocol.h head comment (entries like SET_UNIFORMS; a STRING value is u32 length + UTF-8). The command is
    // validated as a whole first; content problems (unknown name, failed conversion, unsupported value type) do not fail
    // the apply: they are error lines of the element (QmlItem::error, EVENT_ERROR).
    if (plen < VLQT_LIST_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_QML_PROPERTIES (>= " + std::to_string(VLQT_LIST_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    WireReader r(p, plen);
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
        const QString name = QString::fromUtf8(reinterpret_cast<const char*>(e.name), e.nameLen);
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
        default:
            item->rejectRootProperty(name, QStringLiteral("value type %1 is not supported for QML properties").arg(e.type));
            break;
        }
    }
    return ApplyOk;
}

int Scene::setEffectChain(const unsigned char* p, int plen, std::string& err)
{
    // Validates the whole command first (wire format: protocol.h "Effect chain", ids, masks), so a rejected one changes
    // nothing. A texture problem is no rejection but an EVENT_ERROR line of the chain. Elements::setEffectChain fails
    // only when an item of the chain cannot be created (QML error); the old chain is gone by then, so the chain is
    // switched off and the masks and textures this command parked leave the tree again.
    if (plen < VLQT_LIST_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_EFFECT_CHAIN (>= " + std::to_string(VLQT_LIST_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    WireReader r(p, plen);
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
                const std::string ie = at + " entry #" + std::to_string(i);
                if (body.remaining() < 4) {
                    err = ie + ": truncated entry";
                    return ApplyBadLength;
                }
                const quint16 prop = body.u16();
                const quint16 vt = body.u16();
                const quint16 expected = effectValueType(prop);
                if (!expected) {
                    err = ie + ": property " + std::to_string(prop) + " is no MULTI key (EFFECT_* 833..855)";
                    return ApplyUnknownProperty;
                }
                if (vt != expected) {
                    err = ie + ": value type " + std::to_string(vt) + ", property " + std::to_string(prop) + " takes "
                        + std::to_string(expected);
                    return ApplyBadValue;
                }
                const unsigned char* vp = body.bytes(fixedValueSize(vt), true);
                if (!vp) {
                    err = ie + ": entry exceeds the body";
                    return ApplyBadLength;
                }
                EffectMultiEntry multi{prop, readEffectValue(vt, vp)};
                if (vt == VLQT_VALUE_TYPE_REF && !knownRef(multi.value.ref)) {
                    err = ie + ": unknown ref id " + std::to_string(multi.value.ref);
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
            const std::string at = "link #" + std::to_string(l) + ": mask " + std::to_string(e.value.ref);
            for (QQuickItem* a = host.item; a; a = a->parentItem())
                if (a == mask.item) {
                    err = at + " is the element itself or one of its ancestors";
                    return ApplyInvalid;
                }
            if (mask.maskHost && mask.maskHost != host.item) {
                err = at + " is attached to another element";
                return ApplyInvalid;
            }
            if (!mask.maskHost && mask.item->parentItem() && !mask.maskHidden) {
                err = at + " already has a parent (REPARENT)";
                return ApplyInvalid;
            }
            if (std::find(masks.begin(), masks.end(), mask.item) != masks.end()) {
                err = at + " is the mask of another link";
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
