/* wire.h - reads and writes the bytes of the protocol (protocol.h) in QtNative: the one place that knows the byte order,
 * the 4-byte alignment and how to walk a payload without leaving it.
 *
 * Byte order: the protocol is little-endian (C# uses BinaryPrimitives). QtNative copies values in host order, which is
 * the same on every supported RID (win-x64, win-arm64, linux-x64, linux-arm64); readLE/writeLE are the only places
 * to add byte swapping for another one.
 *
 * Reading: WireReader never reads past its region. A read that does not fit returns 0 and fails the reader for good
 * (ok() is false), so a decoder checks ok() once at the end, or remaining() where it has an error text of its own.
 * Sizes are compared as `n > remaining()` in 64 bit, never as `off + n > len`, so no length can overflow the check.
 * readLE on a raw pointer is for fixed layouts whose length the caller has just checked. */
#pragma once

#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QByteArray>
#include <QtCore/QtEndian>
#include <QtCore/QtGlobal>

#include <algorithm>
#include <cstring>
#include <string>

static_assert(Q_BYTE_ORDER == Q_LITTLE_ENDIAN, "the VL.Qt protocol is little-endian; add byte swapping to readLE/writeLE");

namespace vlqt {

constexpr int align4(int n) { return (n + (VLQT_COMMAND_ALIGN - 1)) & ~(VLQT_COMMAND_ALIGN - 1); }
constexpr qint64 align4(qint64 n) { return (n + (VLQT_COMMAND_ALIGN - 1)) & ~qint64(VLQT_COMMAND_ALIGN - 1); }

/* Reads a T from p (no bounds check, no alignment needed). */
template <class T>
T readLE(const unsigned char* p)
{
    T v;
    std::memcpy(&v, p, sizeof(T));
    return v;
}

/* Writes v at p (no bounds check). */
template <class T>
void writeLE(void* p, T v)
{
    std::memcpy(p, &v, sizeof(T));
}

// ---------------------------------------------------------------------------------------------
// Value types (protocol.h "Values")

/* Payload size of a SET value, -1 = unknown value type. Variable length (string, f32 array, path): fixed part only. */
inline int valueSize(quint16 vt)
{
    switch (vt) {
    case VLQT_VALUE_TYPE_LIST: return VLQT_LIST_VALUE_HEADER_SIZE;
    case VLQT_VALUE_TYPE_FONT: return VLQT_FONT_VALUE_HEADER_SIZE;
    case VLQT_VALUE_TYPE_DATE_TIME: return 8;
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

/* Payload size of a fixed-size value, -1 = unknown, variable length (string, f32 array, path, list, font) or a QML value
 * (datetime). The commands with entries of their own (uniforms, effect chain) take only these. */
inline int fixedValueSize(quint16 vt)
{
    switch (vt) {
    case VLQT_VALUE_TYPE_STRING:
    case VLQT_VALUE_TYPE_F32_ARRAY:
    case VLQT_VALUE_TYPE_PATH:
    case VLQT_VALUE_TYPE_LIST:
    case VLQT_VALUE_TYPE_DATE_TIME:
    case VLQT_VALUE_TYPE_FONT:
        return -1;
    default: return valueSize(vt);
    }
}

/* Points a path verb takes, -1 = unknown verb (protocol.h "Values"). */
inline int verbPoints(unsigned char verb)
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

/* Bytes of one element of a LIST of this element type, 0 = STRING (u32 length + UTF-8 + padding to 4), -1 = not an element
 * type (LIST included). */
inline int listElementSize(quint16 elemType)
{
    switch (elemType) {
    case VLQT_VALUE_TYPE_STRING: return 0;
    case VLQT_VALUE_TYPE_BOOL: return 1;
    case VLQT_VALUE_TYPE_F32:
    case VLQT_VALUE_TYPE_I32: return 4;
    case VLQT_VALUE_TYPE_VEC2:
    case VLQT_VALUE_TYPE_DATE_TIME: return 8;
    case VLQT_VALUE_TYPE_VEC3: return 12;
    case VLQT_VALUE_TYPE_COLOR:
    case VLQT_VALUE_TYPE_RECTF:
    case VLQT_VALUE_TYPE_VEC4: return 16;
    default: return -1;
    }
}

/* SAMPLER value (protocol.h "Sampler", 8 bytes at v): bytes filter, mip filter, address U, address V, 0, 0, 0, 0. */
inline bool samplerValueOk(const unsigned char* v)
{
    return v[0] <= 1 && v[1] <= 2 && v[2] <= 2 && v[3] <= 2 && v[4] == 0 && v[5] == 0 && v[6] == 0 && v[7] == 0;
}

// ---------------------------------------------------------------------------------------------

/* Bounds-checked reader over [p, p + len). */
class WireReader
{
public:
    WireReader(const unsigned char* p, int len) : m_begin(p), m_pos(0), m_len(len < 0 ? 0 : len) {}

    bool ok() const { return !m_failed; }
    int offset() const { return m_pos; }
    int remaining() const { return m_len - m_pos; }
    /* Current position; valid for remaining() bytes. */
    const unsigned char* cursor() const { return m_begin + m_pos; }

    template <class T>
    T read()
    {
        if (sizeof(T) > size_t(remaining())) {
            m_failed = true;
            return T{};
        }
        const T v = readLE<T>(cursor());
        m_pos += int(sizeof(T));
        return v;
    }
    quint8 u8() { return read<quint8>(); }
    quint16 u16() { return read<quint16>(); }
    quint32 u32() { return read<quint32>(); }
    qint32 i32() { return read<qint32>(); }
    quint64 u64() { return read<quint64>(); }
    float f32() { return read<float>(); }

    /* The next n bytes (nullptr and failed when fewer are left); the padding up to a multiple of 4 is skipped too
     * but only counts when pad is true. n may be any 64 bit value. */
    const unsigned char* bytes(qint64 n, bool pad = false)
    {
        const qint64 total = pad && n >= 0 ? align4(n) : n;
        if (n < 0 || total > remaining()) {
            m_failed = true;
            return nullptr;
        }
        const unsigned char* p = cursor();
        m_pos += int(total);
        return p;
    }

    /* Skips to the next multiple of 4 of the offset, or to the end when that is closer: the padding of the last value
     * may lie outside a payloadLen, which excludes it (SET_STYLE accepts both). */
    void align() { m_pos = std::min(align4(m_pos), m_len); }

    /* Copies the next n bytes to dst (dst stays untouched when fewer are left). */
    void copy(void* dst, qint64 n)
    {
        if (const unsigned char* p = bytes(n))
            std::memcpy(dst, p, size_t(n));
    }

    /* A reader over the next n bytes, which this reader skips. */
    WireReader sub(qint64 n)
    {
        const unsigned char* p = bytes(n);
        return p ? WireReader(p, int(n)) : WireReader(nullptr, 0);
    }

    /* The next T without consuming it (0 when it does not fit; does not fail the reader). */
    template <class T>
    T peek() const
    {
        return sizeof(T) > size_t(remaining()) ? T{} : readLE<T>(cursor());
    }

private:
    const unsigned char* m_begin;
    int m_pos;
    int m_len;
    bool m_failed = false;
};

/* Appends little-endian values to a QByteArray (event payloads). */
class WireWriter
{
public:
    explicit WireWriter(QByteArray& out) : m_out(out) {}

    template <class T>
    WireWriter& write(T v)
    {
        char tmp[sizeof(T)];
        writeLE(tmp, v);
        m_out.append(tmp, int(sizeof(T)));
        return *this;
    }
    WireWriter& u8(quint8 v) { return write(v); }
    WireWriter& u16(quint16 v) { return write(v); }
    WireWriter& u32(quint32 v) { return write(v); }
    WireWriter& i32(qint32 v) { return write(v); }
    WireWriter& f32(float v) { return write(v); }
    WireWriter& bytes(const QByteArray& b)
    {
        m_out.append(b);
        return *this;
    }
    WireWriter& bytes(const void* p, qsizetype n)
    {
        m_out.append(static_cast<const char*>(p), n);
        return *this;
    }
    /* Zero bytes up to the next multiple of 4 of the whole array. */
    WireWriter& align()
    {
        while (m_out.size() % VLQT_COMMAND_ALIGN)
            m_out.append('\0');
        return *this;
    }

private:
    QByteArray& m_out;
};

// ---------------------------------------------------------------------------------------------
// Named entries: u16 value type, u16 name length, name padded to 4, value padded to 4
// (SET_UNIFORMS, SET_QML_PROPERTIES, the uniforms of a SHADER link of SET_EFFECT_CHAIN; protocol.h).

struct NamedEntry {
    quint16 type = 0;
    int nameLen = 0;
    const unsigned char* name = nullptr; // nameLen bytes of UTF-8, not terminated
    const unsigned char* value = nullptr; // valueLen bytes (STRING: u32 length + UTF-8)
    int valueLen = 0;
};

struct NamedEntryRules {
    const char* noun = "entry"; // "uniform #3: ...", "QML property #3: ..."
    const std::string* scope = nullptr; // in front of the noun, "link #1 uniform #3: ..."
    const char* region = "payload"; // "entry exceeds the payload / the body"
    bool string = false; // a STRING value is allowed (its length follows the u32); other variable-length types never are
    bool sampler = false; // a SAMPLER value is checked (samplerValueOk)
    bool list = false; // a LIST value is allowed (SET_QML_PROPERTIES; its elements are checked by readListLength)
    bool qml = false; // DATE_TIME and FONT values are allowed (SET_QML_PROPERTIES; a FONT has its family length at byte 12)
};

/* Checks the LIST value at the start of r (protocol.h "Values") and gives its length in valueLen (header and elements,
 * without the padding). r is not advanced. VLQT_ERR_BAD_ARGUMENT: elemType not an element type, reserved field not 0 or
 * count above VLQT_LIST_VALUE_MAX_COUNT; VLQT_ERR_BAD_LENGTH: header or elements leave r. */
inline int readListLength(WireReader r, qint64& valueLen, std::string& err)
{
    if (r.remaining() < VLQT_LIST_VALUE_HEADER_SIZE) {
        err = "list: truncated header";
        return VLQT_ERR_BAD_LENGTH;
    }
    const quint16 elemType = r.u16();
    const quint16 reserved = r.u16();
    const quint32 count = r.u32();
    const int elemSize = listElementSize(elemType);
    if (elemSize < 0) {
        err = "list: element type " + std::to_string(elemType) + " not allowed";
        return VLQT_ERR_BAD_ARGUMENT;
    }
    if (reserved != 0) {
        err = "list: reserved field is " + std::to_string(reserved) + ", not 0";
        return VLQT_ERR_BAD_ARGUMENT;
    }
    if (count > quint32(VLQT_LIST_VALUE_MAX_COUNT)) {
        err = "list: " + std::to_string(count) + " elements, more than " + std::to_string(VLQT_LIST_VALUE_MAX_COUNT);
        return VLQT_ERR_BAD_ARGUMENT;
    }
    if (elemSize > 0) {
        valueLen = VLQT_LIST_VALUE_HEADER_SIZE + qint64(count) * elemSize;
        if (qint64(count) * elemSize > r.remaining()) {
            err = "list: " + std::to_string(count) + " elements exceed the entry";
            return VLQT_ERR_BAD_LENGTH;
        }
        return VLQT_OK;
    }
    // STRING elements: u32 length + UTF-8, each padded to 4.
    const int start = r.offset() - VLQT_LIST_VALUE_HEADER_SIZE;
    for (quint32 i = 0; i < count; ++i) {
        const quint32 n = r.u32();
        if (!r.ok() || !r.bytes(n, true)) {
            err = "list: element #" + std::to_string(i) + " exceeds the entry";
            return VLQT_ERR_BAD_LENGTH;
        }
    }
    valueLen = r.offset() - start;
    return VLQT_OK;
}

/* Reads entry number `index` and checks it (value type allowed, name length 1..VLQT_UNIFORM_NAME_MAX, name and value, each
 * with its padding, inside the reader). Returns VLQT_OK, else VLQT_ERR_BAD_LENGTH or VLQT_ERR_BAD_ARGUMENT with the reason
 * in err; the content of a valid entry (a REF target, a name known to the element) is the caller's to check. */
inline int readNamedEntry(WireReader& r, const NamedEntryRules& rules, int index, NamedEntry& out, std::string& err)
{
    auto label = [&] {
        return (rules.scope ? *rules.scope + " " : std::string()) + rules.noun + " #" + std::to_string(index);
    };
    if (r.remaining() < VLQT_ENTRY_HEADER_SIZE) {
        err = label() + ": truncated entry";
        return VLQT_ERR_BAD_LENGTH;
    }
    out.type = r.u16();
    out.nameLen = r.u16();
    if (rules.string) {
        if (valueSize(out.type) < 0) {
            err = label() + ": unknown value type " + std::to_string(out.type);
            return VLQT_ERR_BAD_ARGUMENT;
        }
        if (out.type == VLQT_VALUE_TYPE_F32_ARRAY || out.type == VLQT_VALUE_TYPE_PATH
            || (out.type == VLQT_VALUE_TYPE_LIST && !rules.list)
            || ((out.type == VLQT_VALUE_TYPE_DATE_TIME || out.type == VLQT_VALUE_TYPE_FONT) && !rules.qml)) {
            err = label() + ": value type " + std::to_string(out.type) + " not allowed";
            return VLQT_ERR_BAD_ARGUMENT;
        }
    } else if (fixedValueSize(out.type) < 0) {
        err = label() + ": value type " + std::to_string(out.type) + " not allowed";
        return VLQT_ERR_BAD_ARGUMENT;
    }
    if (out.nameLen < 1 || out.nameLen > VLQT_UNIFORM_NAME_MAX) {
        err = label() + ": name length " + std::to_string(out.nameLen) + " not in 1.." + std::to_string(VLQT_UNIFORM_NAME_MAX);
        return VLQT_ERR_BAD_ARGUMENT;
    }
    // The name and the value each count with their padding (the payload ends after the padding of its last entry).
    out.name = r.bytes(align4(VLQT_ENTRY_HEADER_SIZE + out.nameLen) - VLQT_ENTRY_HEADER_SIZE);
    qint64 valueLen = valueSize(out.type);
    // A STRING holds its length in the first 4 bytes; with fewer left the read below fails like any other value.
    if (out.name && out.type == VLQT_VALUE_TYPE_STRING)
        valueLen += r.peek<quint32>();
    if (out.name && out.type == VLQT_VALUE_TYPE_FONT) {
        WireReader family = r;
        family.bytes(VLQT_FONT_VALUE_HEADER_SIZE - 4);
        valueLen += family.peek<quint32>();
    }
    if (out.name && out.type == VLQT_VALUE_TYPE_LIST) {
        const int rc = readListLength(r, valueLen, err);
        if (rc != VLQT_OK) {
            err = label() + ": " + err;
            return rc;
        }
    }
    out.value = out.name ? r.bytes(valueLen, true) : nullptr;
    if (!out.value) {
        err = label() + ": entry exceeds the " + rules.region;
        return VLQT_ERR_BAD_LENGTH;
    }
    out.valueLen = int(valueLen);
    if (rules.sampler && out.type == VLQT_VALUE_TYPE_SAMPLER && !samplerValueOk(out.value)) {
        err = label() + ": sampler state out of range";
        return VLQT_ERR_BAD_ARGUMENT;
    }
    return VLQT_OK;
}

} // namespace vlqt
