#include "scene/commands.h"
#include "effects/effect_chain_desc.h"
#include "effects/shader_item.h"
#include "elements/group_item.h"
#include "qmlitem.h"
#include "scene/scene.h"
#include "vlstyle.h"

#include <QtCore/QRectF>
#include <QtGui/QColor>
#include <QtGui/QVector2D>
#include <QtGui/QVector3D>
#include <QtGui/QVector4D>
#include <QtQuick/QQuickItem>

#include <algorithm>
#include <cstring>
#include <utility>
#include <vector>

namespace vlqt {
namespace {

// ---------------------------------------------------------------------------------------------
// Wire helpers (little-endian host: Windows x64/arm64)

template <class T>
T rd(const unsigned char* p)
{
    T v;
    std::memcpy(&v, p, sizeof(T));
    return v;
}

constexpr int align4(int n) { return (n + (VLQT_COMMAND_ALIGN - 1)) & ~(VLQT_COMMAND_ALIGN - 1); }

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

/* Payload size of a SET value, -1 = unknown value type. Variable length (string, f32 array, path): fixed part only. */
int valueSize(quint16 vt)
{
    switch (vt) {
    case VLQT_VALUE_TYPE_F32_ARRAY: return 4;
    case VLQT_VALUE_TYPE_PATH: return 8;
    case VLQT_VALUE_TYPE_F32: return 4;
    case VLQT_VALUE_TYPE_I32: return 4;
    case VLQT_VALUE_TYPE_BOOL: return 1;
    case VLQT_VALUE_TYPE_COLOR: return 16;
    case VLQT_VALUE_TYPE_RECTF: return 16;
    case VLQT_VALUE_TYPE_MAT4: return 64;
    case VLQT_VALUE_TYPE_STRING: return 4;
    case VLQT_VALUE_TYPE_REF: return 4;
    case VLQT_VALUE_TYPE_U64: return 8;
    case VLQT_VALUE_TYPE_SAMPLER: return 8;
    case VLQT_VALUE_TYPE_VEC2: return 8;
    case VLQT_VALUE_TYPE_VEC3: return 12;
    case VLQT_VALUE_TYPE_VEC4: return 16;
    case VLQT_VALUE_TYPE_IVEC2: return 8;
    default: return -1;
    }
}

/* Payload size of a fixed-size value, -1 = unknown or variable length (string, f32 array, path). The commands with
 * entries of their own (uniforms, effect chain) take only these. */
int fixedValueSize(quint16 vt)
{
    switch (vt) {
    case VLQT_VALUE_TYPE_STRING:
    case VLQT_VALUE_TYPE_F32_ARRAY:
    case VLQT_VALUE_TYPE_PATH:
        return -1;
    default: return valueSize(vt);
    }
}

/* Points a path verb takes, -1 = unknown verb (protocol.h "Values"). */
int verbPoints(unsigned char verb)
{
    switch (verb) {
    case VLQT_VERB_MOVE:
    case VLQT_VERB_LINE: return 1;
    case VLQT_VERB_QUAD: return 2;
    case VLQT_VERB_CUBIC: return 3;
    case VLQT_VERB_CLOSE: return 0;
    default: return -1;
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

/* SAMPLER value (protocol.h "Sampler"): bytes filter, mip filter, address U, address V, 0, 0, 0, 0. */
bool samplerValueOk(const unsigned char* v)
{
    return v[0] <= 1 && v[1] <= 2 && v[2] <= 2 && v[3] <= 2 && v[4] == 0 && v[5] == 0 && v[6] == 0 && v[7] == 0;
}

/* Copies a value (type already checked, size = fixedValueSize >= 0) from the wire; refs stay ids. */
EffectValue readEffectValue(quint16 vt, const unsigned char* vp)
{
    EffectValue v;
    v.type = vt;
    switch (vt) {
    case VLQT_VALUE_TYPE_I32: v.i = rd<qint32>(vp); break;
    case VLQT_VALUE_TYPE_IVEC2:
        v.i = rd<qint32>(vp);
        v.i2 = rd<qint32>(vp + 4);
        break;
    case VLQT_VALUE_TYPE_BOOL: v.b = vp[0] != 0; break;
    case VLQT_VALUE_TYPE_U64: v.u = rd<quint64>(vp); break;
    case VLQT_VALUE_TYPE_SAMPLER: v.u = rd<quint64>(vp); break;
    case VLQT_VALUE_TYPE_REF: v.ref = rd<quint32>(vp); break;
    default: {
        // f32, color, rectf, mat4, vec2/3/4; variable-length types never get here (fixedValueSize < 0 is rejected)
        const int size = fixedValueSize(vt);
        if (size > 0)
            std::memcpy(v.f, vp, size_t(size));
        break;
    }
    }
    return v;
}

} // namespace

// ---------------------------------------------------------------------------------------------

int Scene::apply(const unsigned char* buf, int len, std::string& err)
{
    // Former effect sources destroyed in an earlier apply: the frame in between has polished the effects onto
    // their new sources, so nothing refers to them any more (see destroySubtree).
    deleteRetired();
    appliedSincePoll = true; // also a failed apply may have applied its first commands
    lastApplyError = ApplyError{};
    if (len < 0 || (len > 0 && !buf)) {
        err = "invalid buffer (null or negative length)";
        lastApplyError = ApplyError{ApplyBadLength, 0, 0, 0, 0, "invalid buffer (null or negative length)"};
        return ApplyBadLength;
    }
    int off = 0, index = 0;
    while (off < len) {
        quint16 op = 0;
        quint32 cmdId = 0;
        quint16 cmdProp = 0;
        int rc = ApplyOk;
        std::string reason;
        if (len - off < VLQT_HEADER_SIZE) {
            rc = ApplyBadLength;
            reason = "truncated header (" + std::to_string(len - off) + " bytes left)";
        } else {
            op = rd<quint16>(buf + off);
            const quint16 flags = rd<quint16>(buf + off + 2);
            const quint32 plen32 = rd<quint32>(buf + off + 4);
            // Within the cap, so plen and size fit an int (len is an int).
            const bool overCap = plen32 > quint32(VLQT_MAX_COMMAND_PAYLOAD);
            const int plen = overCap ? 0 : int(plen32);
            const unsigned char* p = buf + off + VLQT_HEADER_SIZE;
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
            } else if (size > len - off) {
                rc = ApplyBadLength;
                reason = "command (" + std::to_string(size) + " bytes incl. padding) exceeds buffer ("
                    + std::to_string(len - off) + " bytes left)";
            } else if (flags != 0) {
                rc = ApplyBadValue;
                reason = "header flags " + std::to_string(flags) + ", expected 0 (reserved)";
            } else {
                // Object addressed by the command, for the structured error: first u32 of every op with an id.
                const bool hasId = op == VLQT_OP_CREATE || op == VLQT_OP_DESTROY || op == VLQT_OP_REPARENT
                    || op == VLQT_OP_SET || op == VLQT_OP_BIND || op == VLQT_OP_SET_STYLE || op == VLQT_OP_SET_UNIFORMS
                    || op == VLQT_OP_SET_EFFECT_CHAIN || op == VLQT_OP_SET_QML_PROPERTIES;
                if (hasId && plen >= 4)
                    cmdId = rd<quint32>(p);
                if (op == VLQT_OP_SET && plen >= 6)
                    cmdProp = rd<quint16>(p + 4);
                switch (op) {
                case VLQT_OP_CREATE:
                    if (expect(VLQT_CREATE_PAYLOAD))
                        rc = create(rd<quint32>(p), rd<quint16>(p + 4), reason);
                    break;
                case VLQT_OP_DESTROY:
                    if (expect(4))
                        rc = destroy(rd<quint32>(p), reason);
                    break;
                case VLQT_OP_REPARENT:
                    if (expect(VLQT_REPARENT_PAYLOAD))
                        rc = reparent(rd<quint32>(p), rd<quint32>(p + 4), rd<quint32>(p + 8), reason);
                    break;
                case VLQT_OP_SET:
                    rc = set(p, plen, reason);
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
                        const quint32 id = rd<quint32>(p);
                        auto it = nodes.find(id);
                        auto hit = it == nodes.end() ? handlerNodes.find(id) : handlerNodes.end();
                        if (it != nodes.end()) {
                            bindNode(*this, it->second, rd<quint32>(p + 4));
                        } else if (hit != handlerNodes.end()) {
                            bindHandler(*this, hit->second, rd<quint32>(p + 4));
                        } else {
                            rc = ApplyUnknownId;
                            reason = "unknown id " + std::to_string(id);
                        }
                    }
                    break;
                case VLQT_OP_SET_CLEAR_COLOR:
                    if (expect(16)) {
                        float f[4];
                        std::memcpy(f, p, sizeof f);
                        clearState.setColor(toColor(f), window);
                    }
                    break;
                case VLQT_OP_SET_CLEAR:
                    if (expect(4))
                        clearState.setClear(rd<quint32>(p) != 0, window, root);
                    break;
                case VLQT_OP_SET_SPACE:
                    if (expect(4))
                        rc = setSpace(rd<quint32>(p), reason);
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
                off += size;
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

int Scene::set(const unsigned char* p, int plen, std::string& err)
{
    if (plen < VLQT_SET_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET (>= " + std::to_string(VLQT_SET_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    const quint32 id = rd<quint32>(p);
    const quint16 prop = rd<quint16>(p + 4);
    Value v;
    v.type = rd<quint16>(p + 6);
    const unsigned char* vp = p + VLQT_SET_FIELDS_SIZE;
    const int vlen = plen - VLQT_SET_FIELDS_SIZE;
    const int fixed = valueSize(v.type);
    if (fixed < 0) {
        err = "unknown value type " + std::to_string(v.type);
        return ApplyBadValue;
    }
    // Variable length: the fixed part holds the counts (64 bit, a u32 count must not overflow the comparison).
    qint64 expected = fixed;
    if (v.type == VLQT_VALUE_TYPE_STRING && vlen >= 4)
        expected = 4 + qint64(rd<quint32>(vp));
    else if (v.type == VLQT_VALUE_TYPE_F32_ARRAY && vlen >= 4)
        expected = 4 + 4 * qint64(rd<quint32>(vp));
    else if (v.type == VLQT_VALUE_TYPE_PATH && vlen >= 8)
        expected = 8 + ((qint64(rd<quint32>(vp)) + (VLQT_COMMAND_ALIGN - 1)) & ~qint64(VLQT_COMMAND_ALIGN - 1))
            + 8 * qint64(rd<quint32>(vp + 4));
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

    switch (v.type) {
    case VLQT_VALUE_TYPE_F32:
    case VLQT_VALUE_TYPE_COLOR:
    case VLQT_VALUE_TYPE_RECTF:
    case VLQT_VALUE_TYPE_MAT4:
    case VLQT_VALUE_TYPE_VEC2:
    case VLQT_VALUE_TYPE_VEC3:
    case VLQT_VALUE_TYPE_VEC4:
        std::memcpy(v.f, vp, size_t(fixed));
        break;
    case VLQT_VALUE_TYPE_I32: v.i = rd<qint32>(vp); break;
    case VLQT_VALUE_TYPE_IVEC2:
        v.i = rd<qint32>(vp);
        v.i2 = rd<qint32>(vp + 4);
        break;
    case VLQT_VALUE_TYPE_BOOL: v.b = vp[0] != 0; break;
    case VLQT_VALUE_TYPE_U64: v.u = rd<quint64>(vp); break;
    case VLQT_VALUE_TYPE_STRING:
        v.utf8 = QByteArray::fromRawData(reinterpret_cast<const char*>(vp + 4), vlen - 4);
        break;
    case VLQT_VALUE_TYPE_F32_ARRAY:
        v.floatCount = rd<quint32>(vp);
        v.floats = vp + 4;
        break;
    case VLQT_VALUE_TYPE_PATH: {
        v.verbCount = rd<quint32>(vp);
        v.pointCount = rd<quint32>(vp + 4);
        v.verbs = vp + 8;
        v.points = vp + 8 + align4(int(v.verbCount));
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
        const quint32 ref = rd<quint32>(vp);
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
    const quint32 id = rd<quint32>(p);
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
            err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id))
                + " has no style (Controls elements only)";
            return ApplyUnknownProperty;
        }
        err = "unknown id " + std::to_string(id);
        return ApplyUnknownId;
    }
    const quint16 type = it->second.type;
    if (type < VLQT_ELEMENT_TYPE_BUTTON || type > VLQT_ELEMENT_TYPE_TEXT_FIELD) {
        err = "type " + std::to_string(type) + " has no style (Controls elements only)";
        return ApplyUnknownProperty;
    }
    return applySetStyle(it->second.item, p, plen, err);
}

int Scene::setUniforms(const unsigned char* p, int plen, std::string& err)
{
    // Validates all entries first (wire format: protocol.h "SET_UNIFORMS"), then hands them to the ShaderItem;
    // unknown names or wrong types become the element's error text (ShaderItem::rebuild).
    if (plen < VLQT_LIST_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_UNIFORMS (>= " + std::to_string(VLQT_LIST_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    const quint32 id = rd<quint32>(p);
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
            err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id))
                + " has no uniforms (ShaderEffect only)";
            return ApplyUnknownProperty;
        }
        err = "unknown id " + std::to_string(id);
        return ApplyUnknownId;
    }
    if (it->second.type != VLQT_ELEMENT_TYPE_SHADER) {
        err = "type " + std::to_string(it->second.type) + " has no uniforms (ShaderEffect only)";
        return ApplyUnknownProperty;
    }
    const int count = rd<quint16>(p + 4);
    int off = VLQT_LIST_FIELDS_SIZE;
    for (int i = 0; i < count; ++i) {
        if (plen - off < VLQT_ENTRY_HEADER_SIZE) {
            err = "uniform #" + std::to_string(i) + ": truncated entry";
            return ApplyBadLength;
        }
        const quint16 vt = rd<quint16>(p + off);
        const int nameLen = rd<quint16>(p + off + 2);
        const int size = fixedValueSize(vt);
        if (size < 0) {
            err = "uniform #" + std::to_string(i) + ": value type " + std::to_string(vt) + " not allowed";
            return ApplyBadValue;
        }
        if (nameLen < 1 || nameLen > VLQT_UNIFORM_NAME_MAX) {
            err = "uniform #" + std::to_string(i) + ": name length " + std::to_string(nameLen) + " not in 1.."
                + std::to_string(VLQT_UNIFORM_NAME_MAX);
            return ApplyBadValue;
        }
        const int valueOff = off + align4(VLQT_ENTRY_HEADER_SIZE + nameLen);
        const int next = valueOff + align4(size);
        if (next > plen) {
            err = "uniform #" + std::to_string(i) + ": entry exceeds the payload";
            return ApplyBadLength;
        }
        if (vt == VLQT_VALUE_TYPE_REF) {
            const quint32 ref = rd<quint32>(p + valueOff);
            if (ref != VLQT_ROOT_ID && !nodes.count(ref)) {
                err = "uniform #" + std::to_string(i) + ": unknown ref id " + std::to_string(ref);
                return ApplyUnknownId;
            }
        }
        if (vt == VLQT_VALUE_TYPE_SAMPLER && !samplerValueOk(p + valueOff)) {
            err = "uniform #" + std::to_string(i) + ": sampler state out of range";
            return ApplyBadValue;
        }
        off = next;
    }
    if (off != plen) {
        err = "SET_UNIFORMS: " + std::to_string(plen - off) + " bytes after the last entry";
        return ApplyBadLength;
    }
    // Valid as a whole: apply (content problems become the element's error text, ShaderItem::rebuild).
    auto* item = static_cast<ShaderItem*>(it->second.item);
    item->beginUniforms((rd<quint16>(p + 6) & VLQT_UNIFORM_SET_CLEAR) != 0);
    off = VLQT_LIST_FIELDS_SIZE;
    for (int i = 0; i < count; ++i) {
        const quint16 vt = rd<quint16>(p + off);
        const int nameLen = rd<quint16>(p + off + 2);
        const int valueOff = off + align4(VLQT_ENTRY_HEADER_SIZE + nameLen);
        QQuickItem* ref = nullptr;
        if (vt == VLQT_VALUE_TYPE_REF) {
            const auto r = nodes.find(rd<quint32>(p + valueOff));
            ref = r != nodes.end() ? r->second.item : nullptr; // 0 (root) = no texture
        }
        item->setUniform(reinterpret_cast<const char*>(p + off + VLQT_ENTRY_HEADER_SIZE), nameLen, vt, p + valueOff, ref);
        off = valueOff + align4(fixedValueSize(vt));
    }
    item->endUniforms();
    elements.syncTextureGroups(it->second); // Groups as textures go through their frame proxy
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
    const quint32 id = rd<quint32>(p);
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
            err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id))
                + " has no QML properties (QmlElement only)";
            return ApplyUnknownProperty;
        }
        err = "unknown id " + std::to_string(id);
        return ApplyUnknownId;
    }
    if (it->second.type != VLQT_ELEMENT_TYPE_QML) {
        err = "type " + std::to_string(it->second.type) + " has no QML properties (QmlElement only)";
        return ApplyUnknownProperty;
    }
    const int count = rd<quint16>(p + 4);
    int off = VLQT_LIST_FIELDS_SIZE;
    for (int i = 0; i < count; ++i) {
        if (plen - off < VLQT_ENTRY_HEADER_SIZE) {
            err = "QML property #" + std::to_string(i) + ": truncated entry";
            return ApplyBadLength;
        }
        const quint16 vt = rd<quint16>(p + off);
        const int nameLen = rd<quint16>(p + off + 2);
        const int size = valueSize(vt);
        if (size < 0) {
            err = "QML property #" + std::to_string(i) + ": unknown value type " + std::to_string(vt);
            return ApplyBadValue;
        }
        if (vt == VLQT_VALUE_TYPE_F32_ARRAY || vt == VLQT_VALUE_TYPE_PATH) {
            err = "QML property #" + std::to_string(i) + ": value type " + std::to_string(vt) + " not allowed";
            return ApplyBadValue;
        }
        if (nameLen < 1 || nameLen > VLQT_UNIFORM_NAME_MAX) {
            err = "QML property #" + std::to_string(i) + ": name length " + std::to_string(nameLen) + " not in 1.."
                + std::to_string(VLQT_UNIFORM_NAME_MAX);
            return ApplyBadValue;
        }
        const int valueOff = off + align4(VLQT_ENTRY_HEADER_SIZE + nameLen);
        qint64 vlen = size;
        if (vt == VLQT_VALUE_TYPE_STRING) {
            if (valueOff + 4 > plen) {
                err = "QML property #" + std::to_string(i) + ": entry exceeds the payload";
                return ApplyBadLength;
            }
            vlen += rd<quint32>(p + valueOff);
        }
        const qint64 next = valueOff + ((vlen + (VLQT_COMMAND_ALIGN - 1)) & ~qint64(VLQT_COMMAND_ALIGN - 1));
        if (next > plen) {
            err = "QML property #" + std::to_string(i) + ": entry exceeds the payload";
            return ApplyBadLength;
        }
        off = int(next);
    }
    if (off != plen) {
        err = "SET_QML_PROPERTIES: " + std::to_string(plen - off) + " bytes after the last entry";
        return ApplyBadLength;
    }
    auto* item = static_cast<QmlItem*>(it->second.item);
    off = VLQT_LIST_FIELDS_SIZE;
    for (int i = 0; i < count; ++i) {
        const quint16 vt = rd<quint16>(p + off);
        const int nameLen = rd<quint16>(p + off + 2);
        const QString name = QString::fromUtf8(reinterpret_cast<const char*>(p + off + VLQT_ENTRY_HEADER_SIZE), nameLen);
        const unsigned char* vp = p + off + align4(VLQT_ENTRY_HEADER_SIZE + nameLen);
        int vlen = valueSize(vt);
        if (vt == VLQT_VALUE_TYPE_STRING)
            vlen += int(rd<quint32>(vp));
        off = int(vp - p) + align4(vlen);
        float f[4] = {};
        switch (vt) {
        case VLQT_VALUE_TYPE_F32: item->setRootProperty(name, double(rd<float>(vp))); break;
        case VLQT_VALUE_TYPE_I32: item->setRootProperty(name, int(rd<qint32>(vp))); break;
        case VLQT_VALUE_TYPE_BOOL: item->setRootProperty(name, vp[0] != 0); break;
        case VLQT_VALUE_TYPE_STRING:
            item->setRootProperty(name, QString::fromUtf8(reinterpret_cast<const char*>(vp + 4), qsizetype(rd<quint32>(vp))));
            break;
        case VLQT_VALUE_TYPE_COLOR:
            std::memcpy(f, vp, 16);
            item->setRootProperty(name, QColor::fromRgbF(f[0], f[1], f[2], f[3]));
            break;
        case VLQT_VALUE_TYPE_RECTF:
            std::memcpy(f, vp, 16);
            item->setRootProperty(name, QRectF(f[0], f[1], f[2], f[3]));
            break;
        case VLQT_VALUE_TYPE_VEC2:
            std::memcpy(f, vp, 8);
            item->setRootProperty(name, QVector2D(f[0], f[1]));
            break;
        case VLQT_VALUE_TYPE_VEC3:
            std::memcpy(f, vp, 12);
            item->setRootProperty(name, QVector3D(f[0], f[1], f[2]));
            break;
        case VLQT_VALUE_TYPE_VEC4:
            std::memcpy(f, vp, 16);
            item->setRootProperty(name, QVector4D(f[0], f[1], f[2], f[3]));
            break;
        default:
            item->rejectRootProperty(name, QStringLiteral("value type %1 is not supported for QML properties").arg(vt));
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
    const quint32 id = rd<quint32>(p);
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
            err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id))
                + " has no effect chain (elements only)";
            return ApplyUnknownProperty;
        }
        err = "unknown id " + std::to_string(id);
        return ApplyUnknownId;
    }
    auto knownRef = [&](quint32 ref) { return ref == VLQT_ROOT_ID || nodes.count(ref) != 0; };
    const int linkCount = rd<quint16>(p + 4);
    EffectChainDesc chain;
    chain.links.reserve(size_t(linkCount));
    int off = VLQT_LIST_FIELDS_SIZE;
    for (int l = 0; l < linkCount; ++l) {
        const std::string at = "link #" + std::to_string(l);
        if (plen - off < VLQT_EFFECT_CHAIN_LINK_HEADER_SIZE) {
            err = at + ": truncated link header";
            return ApplyBadLength;
        }
        EffectLinkDesc link;
        link.kind = rd<quint16>(p + off);
        const int count = rd<quint16>(p + off + 2);
        const quint32 bodyLen = rd<quint32>(p + off + 4);
        const int body = off + VLQT_EFFECT_CHAIN_LINK_HEADER_SIZE;
        if ((bodyLen & (VLQT_COMMAND_ALIGN - 1)) != 0 || bodyLen > quint32(plen - body)) {
            err = at + ": bodyLen " + std::to_string(bodyLen) + " not aligned or exceeds the payload";
            return ApplyBadLength;
        }
        const int end = body + int(bodyLen);
        int e = body;
        if (link.kind == VLQT_EFFECT_CHAIN_LINK_MULTI) {
            for (int i = 0; i < count; ++i) {
                if (end - e < 4) {
                    err = at + " entry #" + std::to_string(i) + ": truncated entry";
                    return ApplyBadLength;
                }
                const quint16 prop = rd<quint16>(p + e);
                const quint16 vt = rd<quint16>(p + e + 2);
                const quint16 expected = effectValueType(prop);
                if (!expected) {
                    err = at + " entry #" + std::to_string(i) + ": property " + std::to_string(prop)
                        + " is no MULTI key (EFFECT_* 833..855)";
                    return ApplyUnknownProperty;
                }
                if (vt != expected) {
                    err = at + " entry #" + std::to_string(i) + ": value type " + std::to_string(vt)
                        + ", property " + std::to_string(prop) + " takes " + std::to_string(expected);
                    return ApplyBadValue;
                }
                const int next = e + 4 + align4(fixedValueSize(vt));
                if (next > end) {
                    err = at + " entry #" + std::to_string(i) + ": entry exceeds the body";
                    return ApplyBadLength;
                }
                EffectMultiEntry entry{prop, readEffectValue(vt, p + e + 4)};
                if (vt == VLQT_VALUE_TYPE_REF && !knownRef(entry.value.ref)) {
                    err = at + " entry #" + std::to_string(i) + ": unknown ref id " + std::to_string(entry.value.ref);
                    return ApplyUnknownId;
                }
                link.multi.push_back(entry);
                e = next;
            }
        } else if (link.kind == VLQT_EFFECT_CHAIN_LINK_SHADER) {
            if (end - e < 4) {
                err = at + ": truncated SHADER head";
                return ApplyBadLength;
            }
            link.templ = rd<quint16>(p + e);
            const int shaderCount = rd<quint16>(p + e + 2);
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
            e += 4;
            if (end - e < shaderCount * 8 + VLQT_EFFECT_CHAIN_LINK_SHADER_FIXED_SIZE) {
                err = at + ": truncated SHADER head";
                return ApplyBadLength;
            }
            for (int s = 0; s < shaderCount; ++s, e += 8)
                link.shaders.push_back(rd<quint64>(p + e));
            std::memcpy(link.padding, p + e, sizeof link.padding);
            link.strength = rd<float>(p + e + 16);
            e += VLQT_EFFECT_CHAIN_LINK_SHADER_FIXED_SIZE;
            for (int i = 0; i < count; ++i) {
                const std::string ue = at + " uniform #" + std::to_string(i);
                if (end - e < 4) {
                    err = ue + ": truncated entry";
                    return ApplyBadLength;
                }
                const quint16 vt = rd<quint16>(p + e);
                const int uNameLen = rd<quint16>(p + e + 2);
                const int size = fixedValueSize(vt);
                if (size < 0) {
                    err = ue + ": value type " + std::to_string(vt) + " not allowed";
                    return ApplyBadValue;
                }
                if (uNameLen < 1 || uNameLen > VLQT_UNIFORM_NAME_MAX) {
                    err = ue + ": name length " + std::to_string(uNameLen) + " not in 1.."
                        + std::to_string(VLQT_UNIFORM_NAME_MAX);
                    return ApplyBadValue;
                }
                const int valueOff = e + align4(4 + uNameLen);
                const int next = valueOff + align4(size);
                if (next > end) {
                    err = ue + ": entry exceeds the body";
                    return ApplyBadLength;
                }
                if (vt == VLQT_VALUE_TYPE_SAMPLER && !samplerValueOk(p + valueOff)) {
                    err = ue + ": sampler state out of range";
                    return ApplyBadValue;
                }
                EffectUniform u{QByteArray(reinterpret_cast<const char*>(p + e + 4), uNameLen), readEffectValue(vt, p + valueOff)};
                if (vt == VLQT_VALUE_TYPE_REF && !knownRef(u.value.ref)) {
                    err = ue + ": unknown ref id " + std::to_string(u.value.ref);
                    return ApplyUnknownId;
                }
                link.uniforms.push_back(std::move(u));
                e = next;
            }
        } else {
            err = at + ": unknown kind " + std::to_string(link.kind);
            return ApplyBadValue;
        }
        if (e != end) {
            err = at + ": " + std::to_string(end - e) + " bytes after the last entry of the body";
            return ApplyBadLength;
        }
        chain.links.push_back(std::move(link));
        off = end;
    }
    if (off != plen) {
        err = "SET_EFFECT_CHAIN: " + std::to_string(plen - off) + " bytes after the last link";
        return ApplyBadLength;
    }
    // Masks (protocol.h "Effect chain", one per MULTI run): all checked before anything changes.
    ElementEntry& host = it->second;
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
    const int rc = elements.setEffectChain(host, chain, masks, err);
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
