#include "style/vl_style.h"
#include "app/fonts.h"
#include "elements/element_types.h"

#include <array>
#include <cmath>
#include <cstring>

namespace vlqt {

// ---------------------------------------------------------------------------------------------
// Groups: read the resolved current-state values of their VlStyle.

VlStyleFrame::VlStyleFrame(VlStyle* style) : QObject(style), m_style(style) {}
QColor VlStyleFrame::color() const { return m_style->color(VlStyle::Slot::FrameColor); }
QColor VlStyleFrame::borderColor() const { return m_style->color(VlStyle::Slot::BorderColor); }
qreal VlStyleFrame::radius() const { return m_style->number(VlStyle::Slot::FrameRadius); }
qreal VlStyleFrame::borderWidth() const { return m_style->number(VlStyle::Slot::FrameBorderWidth); }
qreal VlStyleFrame::paddingX() const { return m_style->number(VlStyle::Slot::FramePaddingX); }
qreal VlStyleFrame::paddingY() const { return m_style->number(VlStyle::Slot::FramePaddingY); }
qreal VlStyleFrame::minWidth() const { return m_style->number(VlStyle::Slot::FrameMinWidth); }
qreal VlStyleFrame::minHeight() const { return m_style->number(VlStyle::Slot::FrameMinHeight); }

VlStyleText::VlStyleText(VlStyle* style) : QObject(style), m_style(style) {}
QColor VlStyleText::color() const { return m_style->color(VlStyle::Slot::TextColor); }
QFont VlStyleText::font() const { return m_style->font(); }

VlStyleIndicator::VlStyleIndicator(VlStyle* style) : QObject(style), m_style(style) {}
QColor VlStyleIndicator::color() const { return m_style->color(VlStyle::Slot::IndicatorColor); }
// One border color for Frame and Indicator (protocol.h: BORDER_COLOR).
QColor VlStyleIndicator::borderColor() const { return m_style->color(VlStyle::Slot::BorderColor); }
qreal VlStyleIndicator::size() const { return m_style->number(VlStyle::Slot::IndicatorSize); }
qreal VlStyleIndicator::radius() const { return m_style->number(VlStyle::Slot::IndicatorRadius); }
qreal VlStyleIndicator::borderWidth() const { return m_style->number(VlStyle::Slot::IndicatorBorderWidth); }

VlStyleTrack::VlStyleTrack(VlStyle* style) : QObject(style), m_style(style) {}
QColor VlStyleTrack::color() const { return m_style->color(VlStyle::Slot::TrackColor); }
QColor VlStyleTrack::fillColor() const { return m_style->color(VlStyle::Slot::TrackFillColor); }
qreal VlStyleTrack::thickness() const { return m_style->number(VlStyle::Slot::TrackThickness); }
qreal VlStyleTrack::radius() const { return m_style->number(VlStyle::Slot::TrackRadius); }

// ---------------------------------------------------------------------------------------------

namespace {

// Protocol field id of each slot, in the order of VlStyle::Slot.
constexpr auto s_fieldOfSlot = std::to_array<quint16>({
    VLQT_STYLE_FIELD_FRAME_COLOR, VLQT_STYLE_FIELD_BORDER_COLOR, VLQT_STYLE_FIELD_TEXT_COLOR,
    VLQT_STYLE_FIELD_INDICATOR_COLOR, VLQT_STYLE_FIELD_TRACK_COLOR, VLQT_STYLE_FIELD_TRACK_FILL_COLOR,
    VLQT_STYLE_FIELD_FRAME_RADIUS, VLQT_STYLE_FIELD_FRAME_BORDER_WIDTH, VLQT_STYLE_FIELD_FRAME_PADDING_X,
    VLQT_STYLE_FIELD_FRAME_PADDING_Y, VLQT_STYLE_FIELD_FRAME_MIN_WIDTH, VLQT_STYLE_FIELD_FRAME_MIN_HEIGHT,
    VLQT_STYLE_FIELD_TEXT_FONT_FAMILY, VLQT_STYLE_FIELD_TEXT_FONT_SIZE, VLQT_STYLE_FIELD_TEXT_FONT_WEIGHT,
    VLQT_STYLE_FIELD_INDICATOR_SIZE, VLQT_STYLE_FIELD_INDICATOR_RADIUS, VLQT_STYLE_FIELD_INDICATOR_BORDER_WIDTH,
    VLQT_STYLE_FIELD_TRACK_THICKNESS, VLQT_STYLE_FIELD_TRACK_RADIUS,
});
static_assert(s_fieldOfSlot.size() == size_t(VlStyle::Slot::Count), "one protocol field id per slot");

bool isColorSlot(VlStyle::Slot slot) { return slot <= VlStyle::Slot::TrackFillColor; }

} // namespace

VlStyle::VlStyle(QObject* owner)
    : QObject(owner), m_frame(this), m_text(this), m_indicator(this), m_track(this)
{
    m_colors.fill(QColor(Qt::transparent));
    m_numbers.fill(0);
}

VlStyle* VlStyle::qmlAttachedProperties(QObject* owner) { return new VlStyle(owner); }

std::optional<VlStyle::Slot> VlStyle::slotOf(quint16 field)
{
    for (size_t s = 0; s < s_fieldOfSlot.size(); ++s)
        if (s_fieldOfSlot[s] == field)
            return Slot(s);
    return std::nullopt;
}

quint16 VlStyle::valueTypeOf(Slot slot)
{
    if (isColorSlot(slot))
        return VLQT_VALUE_TYPE_COLOR;
    if (slot == Slot::FontFamily)
        return VLQT_VALUE_TYPE_STRING;
    if (slot == Slot::FontWeight)
        return VLQT_VALUE_TYPE_I32;
    return VLQT_VALUE_TYPE_F32;
}

void VlStyle::clear()
{
    for (auto& states : m_cells)
        for (Cell& c : states)
            if (c.set) {
                c = Cell{};
                m_dirty = true;
            }
}

void VlStyle::setCell(Slot slot, int state, const Value& v)
{
    Cell& c = m_cells[size_t(slot)][size_t(state)];
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
        // Here and not in fontAt (which runs at every state change): once per name, when the family is written.
        if (slot == Slot::FontFamily && !c.s.isEmpty())
            warnMissingFontFamily(c.s);
        break;
    }
    default:
        return;
    }
    c.set = true;
    m_dirty = true;
}

void VlStyle::commit()
{
    if (!m_dirty)
        return;
    m_dirty = false;
    refresh();
    ++m_revision;
    emit revisionChanged();
}

void VlStyle::setState(int state)
{
    if (state < 0 || state >= stateCount)
        state = VLQT_STYLE_STATE_NORMAL;
    if (state == m_state)
        return;
    m_state = state;
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

const VlStyle::Cell* VlStyle::resolve(Slot slot, int state) const
{
    const auto& states = m_cells[size_t(slot)];
    if (state >= 0 && state < stateCount && states[size_t(state)].set)
        return &states[size_t(state)];
    if (states[VLQT_STYLE_STATE_NORMAL].set)
        return &states[VLQT_STYLE_STATE_NORMAL];
    return nullptr;
}

QColor VlStyle::colorAt(Slot slot, int state) const
{
    const Cell* c = resolve(slot, state);
    return c ? toColor(c->f) : QColor(Qt::transparent);
}

float VlStyle::numberAt(Slot slot, int state) const
{
    const Cell* c = resolve(slot, state);
    if (!c)
        return 0;
    return slot == Slot::FontWeight ? float(c->i) : c->f[0];
}

QFont VlStyle::fontAt(int state) const
{
    QFont font; // Qt default: family "", size 0 and weight 0 keep its values
    const Cell* family = resolve(Slot::FontFamily, state);
    if (family && !family->s.isEmpty())
        font.setFamilies({family->s});
    const float size = numberAt(Slot::FontSize, state);
    if (size > 0)
        font.setPixelSize(qMax(1, int(std::lround(size))));
    const Cell* weight = resolve(Slot::FontWeight, state);
    if (weight && weight->i > 0)
        font.setWeight(QFont::Weight(qBound(1, int(weight->i), 1000)));
    return font;
}

QVariant VlStyle::value(Field field, int state) const
{
    const std::optional<Slot> found = slotOf(quint16(field));
    if (!found)
        return {};
    const Slot slot = *found;
    if (isColorSlot(slot))
        return colorAt(slot, state);
    if (slot == Slot::FontFamily) {
        const Cell* c = resolve(slot, state);
        return c ? c->s : QString();
    }
    if (slot == Slot::FontWeight) {
        const Cell* c = resolve(slot, state);
        return c ? int(c->i) : 0;
    }
    return qreal(numberAt(slot, state));
}

void VlStyle::refresh()
{
    bool frame = false, text = false, indicator = false, track = false;
    auto mark = [&](Slot slot) {
        switch (slot) {
        case Slot::FrameColor: case Slot::FrameRadius: case Slot::FrameBorderWidth: case Slot::FramePaddingX:
        case Slot::FramePaddingY: case Slot::FrameMinWidth: case Slot::FrameMinHeight:
            frame = true;
            break;
        case Slot::BorderColor: // shared by Frame and Indicator
            frame = indicator = true;
            break;
        case Slot::TextColor:
            text = true;
            break;
        case Slot::IndicatorColor: case Slot::IndicatorSize: case Slot::IndicatorRadius: case Slot::IndicatorBorderWidth:
            indicator = true;
            break;
        case Slot::TrackColor: case Slot::TrackFillColor: case Slot::TrackThickness: case Slot::TrackRadius:
            track = true;
            break;
        default:
            break;
        }
    };
    for (size_t i = 0; i < size_t(Slot::Count); ++i) {
        const Slot s = Slot(i);
        if (s == Slot::FontFamily || s == Slot::FontSize || s == Slot::FontWeight)
            continue; // folded into m_font
        if (isColorSlot(s)) {
            const QColor c = colorAt(s, m_state);
            if (c != m_colors[i]) {
                m_colors[i] = c;
                mark(s);
            }
        } else {
            const qreal n = numberAt(s, m_state);
            if (n != m_numbers[i]) {
                m_numbers[i] = n;
                mark(s);
            }
        }
    }
    const QFont font = fontAt(m_state);
    if (!(font == m_font) || font.families() != m_font.families()) {
        m_font = font;
        text = true;
    }
    if (frame)
        emit m_frame.changed();
    if (text)
        emit m_text.changed();
    if (indicator)
        emit m_indicator.changed();
    if (track)
        emit m_track.changed();
}

// ---------------------------------------------------------------------------------------------

void registerStyleTypes()
{
    qmlRegisterUncreatableType<VlStyle>("VLQt.Style", 1, 0, "VlStyle",
        QStringLiteral("VlStyle is an attached object (VlStyle.state, VlStyle.frame ...)"));
}

} // namespace vlqt
