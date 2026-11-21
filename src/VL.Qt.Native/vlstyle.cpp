#include "vlstyle.h"
#include "app/fonts.h"

#include "protocol.h"

#include <QtQuick/QQuickItem>

#include <cmath>
#include <cstring>

namespace vlqt {

// ---------------------------------------------------------------------------------------------
// Groups: read the resolved current-state values of their VlStyle.

VlStyleFrame::VlStyleFrame(VlStyle* style) : QObject(style), _style(style) {}
QColor VlStyleFrame::color() const { return _style->color(VlStyle::FrameColor); }
QColor VlStyleFrame::borderColor() const { return _style->color(VlStyle::BorderColor); }
qreal VlStyleFrame::radius() const { return _style->number(VlStyle::FrameRadius); }
qreal VlStyleFrame::borderWidth() const { return _style->number(VlStyle::FrameBorderWidth); }
qreal VlStyleFrame::paddingX() const { return _style->number(VlStyle::FramePaddingX); }
qreal VlStyleFrame::paddingY() const { return _style->number(VlStyle::FramePaddingY); }
qreal VlStyleFrame::minWidth() const { return _style->number(VlStyle::FrameMinWidth); }
qreal VlStyleFrame::minHeight() const { return _style->number(VlStyle::FrameMinHeight); }

VlStyleText::VlStyleText(VlStyle* style) : QObject(style), _style(style) {}
QColor VlStyleText::color() const { return _style->color(VlStyle::TextColor); }
QFont VlStyleText::font() const { return _style->font(); }

VlStyleIndicator::VlStyleIndicator(VlStyle* style) : QObject(style), _style(style) {}
QColor VlStyleIndicator::color() const { return _style->color(VlStyle::IndicatorColor); }
// One border color for Frame and Indicator (protocol.h: BORDER_COLOR).
QColor VlStyleIndicator::borderColor() const { return _style->color(VlStyle::BorderColor); }
qreal VlStyleIndicator::size() const { return _style->number(VlStyle::IndicatorSize); }
qreal VlStyleIndicator::radius() const { return _style->number(VlStyle::IndicatorRadius); }
qreal VlStyleIndicator::borderWidth() const { return _style->number(VlStyle::IndicatorBorderWidth); }

VlStyleTrack::VlStyleTrack(VlStyle* style) : QObject(style), _style(style) {}
QColor VlStyleTrack::color() const { return _style->color(VlStyle::TrackColor); }
QColor VlStyleTrack::fillColor() const { return _style->color(VlStyle::TrackFillColor); }
qreal VlStyleTrack::thickness() const { return _style->number(VlStyle::TrackThickness); }
qreal VlStyleTrack::radius() const { return _style->number(VlStyle::TrackRadius); }

// ---------------------------------------------------------------------------------------------

namespace {

constexpr quint16 s_fieldOfSlot[VlStyle::SlotCount] = {
    VLQT_STYLE_FIELD_FRAME_COLOR, VLQT_STYLE_FIELD_BORDER_COLOR, VLQT_STYLE_FIELD_TEXT_COLOR,
    VLQT_STYLE_FIELD_INDICATOR_COLOR, VLQT_STYLE_FIELD_TRACK_COLOR, VLQT_STYLE_FIELD_TRACK_FILL_COLOR,
    VLQT_STYLE_FIELD_FRAME_RADIUS, VLQT_STYLE_FIELD_FRAME_BORDER_WIDTH, VLQT_STYLE_FIELD_FRAME_PADDING_X,
    VLQT_STYLE_FIELD_FRAME_PADDING_Y, VLQT_STYLE_FIELD_FRAME_MIN_WIDTH, VLQT_STYLE_FIELD_FRAME_MIN_HEIGHT,
    VLQT_STYLE_FIELD_TEXT_FONT_FAMILY, VLQT_STYLE_FIELD_TEXT_FONT_SIZE, VLQT_STYLE_FIELD_TEXT_FONT_WEIGHT,
    VLQT_STYLE_FIELD_INDICATOR_SIZE, VLQT_STYLE_FIELD_INDICATOR_RADIUS, VLQT_STYLE_FIELD_INDICATOR_BORDER_WIDTH,
    VLQT_STYLE_FIELD_TRACK_THICKNESS, VLQT_STYLE_FIELD_TRACK_RADIUS,
};

bool isColorSlot(int slot) { return slot <= VlStyle::TrackFillColor; }

template <class T>
T rd(const unsigned char* p)
{
    T v;
    std::memcpy(&v, p, sizeof(T));
    return v;
}

constexpr int align4(int n) { return (n + 3) & ~3; }

} // namespace

VlStyle::VlStyle(QObject* owner)
    : QObject(owner), _frame(this), _text(this), _indicator(this), _track(this)
{
    _colors.fill(QColor(Qt::transparent));
    _numbers.fill(0);
}

VlStyle* VlStyle::qmlAttachedProperties(QObject* owner) { return new VlStyle(owner); }

int VlStyle::slotOf(quint16 field)
{
    for (int s = 0; s < SlotCount; ++s)
        if (s_fieldOfSlot[s] == field)
            return s;
    return -1;
}

quint16 VlStyle::valueTypeOf(int slot)
{
    if (isColorSlot(slot))
        return VLQT_VALUE_TYPE_COLOR;
    if (slot == FontFamily)
        return VLQT_VALUE_TYPE_STRING;
    if (slot == FontWeight)
        return VLQT_VALUE_TYPE_I32;
    return VLQT_VALUE_TYPE_F32;
}

void VlStyle::clear()
{
    for (auto& states : _cells)
        for (Cell& c : states)
            if (c.set) {
                c = Cell{};
                _dirty = true;
            }
}

void VlStyle::setCell(int slot, int state, const Value& v)
{
    Cell& c = _cells[size_t(slot)][size_t(state)];
    switch (v.type) {
    case VLQT_VALUE_TYPE_COLOR:
    case VLQT_VALUE_TYPE_F32: {
        const size_t n = v.type == VLQT_VALUE_TYPE_COLOR ? 4 : 1;
        if (c.set && std::memcmp(c.f, v.f, n * sizeof(float)) == 0)
            return;
        std::memcpy(c.f, v.f, n * sizeof(float));
        break;
    }
    case VLQT_VALUE_TYPE_I32:
        if (c.set && c.i == v.i)
            return;
        c.i = v.i;
        break;
    case VLQT_VALUE_TYPE_STRING: {
        if (c.set && c.s.toUtf8() == v.utf8) // only while the command is applied, not per frame
            return;
        c.s = QString::fromUtf8(v.utf8);
        break;
    }
    default:
        return;
    }
    c.set = true;
    _dirty = true;
}

void VlStyle::commit()
{
    if (!_dirty)
        return;
    _dirty = false;
    refresh();
    ++_revision;
    emit revisionChanged();
}

void VlStyle::setState(int state)
{
    if (state < 0 || state >= stateCount)
        state = VLQT_STYLE_STATE_NORMAL;
    if (state == _state)
        return;
    _state = state;
    refresh();
    emit stateChanged();
}

int VlStyle::stateOf(bool enabled, bool pressed, bool hovered, bool checked) const
{
    if (!enabled)
        return VLQT_STYLE_STATE_DISABLED;
    if (pressed)
        return VLQT_STYLE_STATE_PRESSED;
    if (hovered)
        return VLQT_STYLE_STATE_HOVERED;
    if (checked)
        return VLQT_STYLE_STATE_CHECKED;
    return VLQT_STYLE_STATE_NORMAL;
}

const VlStyle::Cell* VlStyle::resolve(int slot, int state) const
{
    const auto& states = _cells[size_t(slot)];
    if (state >= 0 && state < stateCount && states[size_t(state)].set)
        return &states[size_t(state)];
    if (states[VLQT_STYLE_STATE_NORMAL].set)
        return &states[VLQT_STYLE_STATE_NORMAL];
    return nullptr;
}

QColor VlStyle::colorAt(int slot, int state) const
{
    const Cell* c = resolve(slot, state);
    return c ? toColor(c->f) : QColor(Qt::transparent);
}

float VlStyle::numberAt(int slot, int state) const
{
    const Cell* c = resolve(slot, state);
    if (!c)
        return 0;
    return slot == FontWeight ? float(c->i) : c->f[0];
}

QFont VlStyle::fontAt(int state) const
{
    QFont font; // Qt default: family "", size 0 and weight 0 keep its values
    const Cell* family = resolve(FontFamily, state);
    if (family && !family->s.isEmpty()) {
        warnMissingFontFamily(family->s); // once per name
        font.setFamilies({family->s});
    }
    const float size = numberAt(FontSize, state);
    if (size > 0)
        font.setPixelSize(qMax(1, int(std::lround(size))));
    const Cell* weight = resolve(FontWeight, state);
    if (weight && weight->i > 0)
        font.setWeight(QFont::Weight(qBound(1, int(weight->i), 1000)));
    return font;
}

QVariant VlStyle::value(int field, int state) const
{
    const int slot = field >= 0 && field <= 0xFFFF ? slotOf(quint16(field)) : -1;
    if (slot < 0)
        return {};
    if (isColorSlot(slot))
        return colorAt(slot, state);
    if (slot == FontFamily) {
        const Cell* c = resolve(slot, state);
        return c ? c->s : QString();
    }
    if (slot == FontWeight) {
        const Cell* c = resolve(slot, state);
        return c ? int(c->i) : 0;
    }
    return qreal(numberAt(slot, state));
}

void VlStyle::refresh()
{
    bool frame = false, text = false, indicator = false, track = false;
    auto mark = [&](int slot) {
        switch (slot) {
        case FrameColor: case FrameRadius: case FrameBorderWidth: case FramePaddingX: case FramePaddingY:
        case FrameMinWidth: case FrameMinHeight:
            frame = true;
            break;
        case BorderColor: // shared by Frame and Indicator
            frame = indicator = true;
            break;
        case TextColor:
            text = true;
            break;
        case IndicatorColor: case IndicatorSize: case IndicatorRadius: case IndicatorBorderWidth:
            indicator = true;
            break;
        case TrackColor: case TrackFillColor: case TrackThickness: case TrackRadius:
            track = true;
            break;
        default:
            break;
        }
    };
    for (int s = 0; s < SlotCount; ++s) {
        if (s == FontFamily || s == FontSize || s == FontWeight)
            continue; // folded into _font
        if (isColorSlot(s)) {
            const QColor c = colorAt(s, _state);
            if (c != _colors[size_t(s)]) {
                _colors[size_t(s)] = c;
                mark(s);
            }
        } else {
            const qreal n = numberAt(s, _state);
            if (n != _numbers[size_t(s)]) {
                _numbers[size_t(s)] = n;
                mark(s);
            }
        }
    }
    const QFont font = fontAt(_state);
    if (!(font == _font) || font.families() != _font.families()) {
        _font = font;
        text = true;
    }
    if (frame)
        emit _frame.changed();
    if (text)
        emit _text.changed();
    if (indicator)
        emit _indicator.changed();
    if (track)
        emit _track.changed();
}

// ---------------------------------------------------------------------------------------------

void registerStyleTypes()
{
    qmlRegisterUncreatableType<VlStyle>("VLQt.Style", 1, 0, "VlStyle",
        QStringLiteral("VlStyle is an attached object (VlStyle.state, VlStyle.frame ...)"));
}

int applySetStyle(QQuickItem* item, const unsigned char* p, int plen, std::string& err)
{
    if (plen < VLQT_LIST_FIELDS_SIZE) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_STYLE (>= " + std::to_string(VLQT_LIST_FIELDS_SIZE) + ")";
        return ApplyBadLength;
    }
    const int count = rd<quint16>(p + 4);
    const quint16 flags = rd<quint16>(p + 6);
    if (flags & ~quint16(VLQT_STYLE_SET_CLEAR)) {
        err = "unknown SET_STYLE flags " + std::to_string(flags);
        return ApplyUnknownProperty;
    }
    // Pass 0 checks every entry, pass 1 writes the table (a bad command leaves the style unchanged).
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
        int off = VLQT_LIST_FIELDS_SIZE;
        for (int e = 0; e < count; ++e) {
            auto at = [&] { return "entry " + std::to_string(e) + " (offset " + std::to_string(off) + ")"; };
            if (plen - off < VLQT_ENTRY_HEADER_SIZE) {
                err = at() + ": truncated (" + std::to_string(plen - off) + " bytes left)";
                return ApplyBadLength;
            }
            const quint16 field = rd<quint16>(p + off);
            const int state = p[off + 2];
            Value v;
            v.type = p[off + 3];
            const unsigned char* vp = p + off + VLQT_ENTRY_HEADER_SIZE;
            const int slot = VlStyle::slotOf(field);
            if (slot < 0) {
                err = at() + ": unknown style field " + std::to_string(field);
                return ApplyUnknownProperty;
            }
            if (state >= VlStyle::stateCount) {
                err = at() + ": unknown style state " + std::to_string(state);
                return ApplyUnknownProperty;
            }
            const quint16 expected = VlStyle::valueTypeOf(slot);
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
            case VLQT_VALUE_TYPE_STRING:
                size = 4;
                if (plen - off - 4 >= 4)
                    size += rd<quint32>(vp);
                break;
            default: break;
            }
            const qint64 left = plen - off - VLQT_ENTRY_HEADER_SIZE;
            if (size < 0 || size > left) {
                err = at() + ": value needs " + std::to_string(size) + " bytes, " + std::to_string(left) + " left";
                return ApplyBadLength;
            }
            if (pass == 1) {
                switch (v.type) {
                case VLQT_VALUE_TYPE_COLOR: std::memcpy(v.f, vp, 16); break;
                case VLQT_VALUE_TYPE_F32: std::memcpy(v.f, vp, 4); break;
                case VLQT_VALUE_TYPE_I32: v.i = rd<qint32>(vp); break;
                case VLQT_VALUE_TYPE_STRING:
                    v.utf8 = QByteArray::fromRawData(reinterpret_cast<const char*>(vp + 4), qsizetype(size - 4));
                    break;
                default: break;
                }
                style->setCell(slot, state, v);
            }
            off += align4(VLQT_ENTRY_HEADER_SIZE + int(size));
        }
        // payloadLen excludes the padding of the last entry (like every command); the padded end is accepted too.
        const int end = count > 0 ? off : VLQT_LIST_FIELDS_SIZE;
        if (pass == 0 && plen != end && !(count > 0 && plen < end && align4(plen) == end)) {
            err = "payloadLen " + std::to_string(plen) + " does not match " + std::to_string(count)
                + " entries (" + std::to_string(end) + " bytes incl. padding)";
            return ApplyBadLength;
        }
    }
    style->commit();
    return ApplyOk;
}

} // namespace vlqt
