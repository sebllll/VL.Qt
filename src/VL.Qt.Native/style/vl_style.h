/* style/vl_style.h - attached object VlStyle: the resolved style of one Controls element as a table field x state,
 * written by SET_STYLE (scene/commands.cpp) and read by the VL.Qt base style (style/VLQt/*.qml, embedded module "VLQt").
 *
 * QML API (see docs/styles.md):
 *   VlStyle.state                 int, writable, default STYLE_STATE_NORMAL; the style binds it to stateOf(...)
 *   VlStyle.stateOf(enabled, pressed, hovered, checked)  rank Disabled > Pressed > Hovered > Checked > Normal
 *   VlStyle.frame.{color, borderColor, radius, borderWidth, paddingX, paddingY, minWidth, minHeight}
 *   VlStyle.text.{color, font}    font: QFont from FontFamily ("" = Qt default), FontSize (px), FontWeight
 *   VlStyle.indicator.{color, borderColor, size, radius, borderWidth}
 *   VlStyle.track.{color, fillColor, thickness, radius}
 *   VlStyle.value(VlStyle.<Field>, state)   one value for a part with a state of its own (SpinBox arrows), see
 *                                 revision; Field = the protocol's style field ids as a Q_ENUM (VlStyle.IndicatorRadius)
 *   VlStyle.revision              int, counts table changes (NOTIFY), for bindings on value()
 * The groups hold the values for the current state. Unset (field, state) -> (field, NORMAL) -> 0 / transparent / "".
 * Signals only on a real change; nothing is allocated per frame (cells are compared before they are written).
 * Attached per object, never inherited by children (C# resolves the inheritance). */
#pragma once

#include "protocol.h"

#include <QtCore/QObject>
#include <QtCore/QString>
#include <QtCore/QVariant>
#include <QtGui/QColor>
#include <QtGui/QFont>
#include <QtQml/qqml.h>

#include <array>
#include <optional>

namespace vlqt {

class VlStyle;
struct Value; // elements/element_types.h

/* VlStyle.frame */
class VlStyleFrame : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QColor color READ color NOTIFY changed)
    Q_PROPERTY(QColor borderColor READ borderColor NOTIFY changed)
    Q_PROPERTY(qreal radius READ radius NOTIFY changed)
    Q_PROPERTY(qreal borderWidth READ borderWidth NOTIFY changed)
    Q_PROPERTY(qreal paddingX READ paddingX NOTIFY changed)
    Q_PROPERTY(qreal paddingY READ paddingY NOTIFY changed)
    Q_PROPERTY(qreal minWidth READ minWidth NOTIFY changed)
    Q_PROPERTY(qreal minHeight READ minHeight NOTIFY changed)
public:
    explicit VlStyleFrame(VlStyle* style);
    QColor color() const;
    QColor borderColor() const;
    qreal radius() const;
    qreal borderWidth() const;
    qreal paddingX() const;
    qreal paddingY() const;
    qreal minWidth() const;
    qreal minHeight() const;
signals:
    void changed();
private:
    const VlStyle* m_style;
};

/* VlStyle.text */
class VlStyleText : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QColor color READ color NOTIFY changed)
    Q_PROPERTY(QFont font READ font NOTIFY changed)
public:
    explicit VlStyleText(VlStyle* style);
    QColor color() const;
    QFont font() const;
signals:
    void changed();
private:
    const VlStyle* m_style;
};

/* VlStyle.indicator */
class VlStyleIndicator : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QColor color READ color NOTIFY changed)
    Q_PROPERTY(QColor borderColor READ borderColor NOTIFY changed)
    Q_PROPERTY(qreal size READ size NOTIFY changed)
    Q_PROPERTY(qreal radius READ radius NOTIFY changed)
    Q_PROPERTY(qreal borderWidth READ borderWidth NOTIFY changed)
public:
    explicit VlStyleIndicator(VlStyle* style);
    QColor color() const;
    QColor borderColor() const;
    qreal size() const;
    qreal radius() const;
    qreal borderWidth() const;
signals:
    void changed();
private:
    const VlStyle* m_style;
};

/* VlStyle.track */
class VlStyleTrack : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QColor color READ color NOTIFY changed)
    Q_PROPERTY(QColor fillColor READ fillColor NOTIFY changed)
    Q_PROPERTY(qreal thickness READ thickness NOTIFY changed)
    Q_PROPERTY(qreal radius READ radius NOTIFY changed)
public:
    explicit VlStyleTrack(VlStyle* style);
    QColor color() const;
    QColor fillColor() const;
    qreal thickness() const;
    qreal radius() const;
signals:
    void changed();
private:
    const VlStyle* m_style;
};

class VlStyle : public QObject
{
    Q_OBJECT
    Q_PROPERTY(int state READ state WRITE setState NOTIFY stateChanged)
    Q_PROPERTY(int revision READ revision NOTIFY revisionChanged)
    Q_PROPERTY(vlqt::VlStyleFrame* frame READ frame CONSTANT)
    Q_PROPERTY(vlqt::VlStyleText* text READ text CONSTANT)
    Q_PROPERTY(vlqt::VlStyleIndicator* indicator READ indicator CONSTANT)
    Q_PROPERTY(vlqt::VlStyleTrack* track READ track CONSTANT)
    QML_ATTACHED(VlStyle)
public:
    /* Protocol field ids (protocol.h vlqt_style_field) for VlStyle.value() in QML: VlStyle.IndicatorRadius. */
    enum Field : quint16 {
        FrameColor = VLQT_STYLE_FIELD_FRAME_COLOR,
        BorderColor = VLQT_STYLE_FIELD_BORDER_COLOR,
        TextColor = VLQT_STYLE_FIELD_TEXT_COLOR,
        IndicatorColor = VLQT_STYLE_FIELD_INDICATOR_COLOR,
        TrackColor = VLQT_STYLE_FIELD_TRACK_COLOR,
        TrackFillColor = VLQT_STYLE_FIELD_TRACK_FILL_COLOR,
        FrameRadius = VLQT_STYLE_FIELD_FRAME_RADIUS,
        FrameBorderWidth = VLQT_STYLE_FIELD_FRAME_BORDER_WIDTH,
        FramePaddingX = VLQT_STYLE_FIELD_FRAME_PADDING_X,
        FramePaddingY = VLQT_STYLE_FIELD_FRAME_PADDING_Y,
        FrameMinWidth = VLQT_STYLE_FIELD_FRAME_MIN_WIDTH,
        FrameMinHeight = VLQT_STYLE_FIELD_FRAME_MIN_HEIGHT,
        FontFamily = VLQT_STYLE_FIELD_TEXT_FONT_FAMILY,
        FontSize = VLQT_STYLE_FIELD_TEXT_FONT_SIZE,
        FontWeight = VLQT_STYLE_FIELD_TEXT_FONT_WEIGHT,
        IndicatorSize = VLQT_STYLE_FIELD_INDICATOR_SIZE,
        IndicatorRadius = VLQT_STYLE_FIELD_INDICATOR_RADIUS,
        IndicatorBorderWidth = VLQT_STYLE_FIELD_INDICATOR_BORDER_WIDTH,
        TrackThickness = VLQT_STYLE_FIELD_TRACK_THICKNESS,
        TrackRadius = VLQT_STYLE_FIELD_TRACK_RADIUS,
    };
    Q_ENUM(Field)

    /* Row of the table: one per field, in the order of s_fieldOfSlot (vl_style.cpp), colors first. */
    enum class Slot {
        FrameColor, BorderColor, TextColor, IndicatorColor, TrackColor, TrackFillColor,
        FrameRadius, FrameBorderWidth, FramePaddingX, FramePaddingY, FrameMinWidth, FrameMinHeight,
        FontFamily, FontSize, FontWeight,
        IndicatorSize, IndicatorRadius, IndicatorBorderWidth,
        TrackThickness, TrackRadius,
        Count
    };
    static constexpr int stateCount = VLQT_STYLE_STATE_CHECKED + 1; // NORMAL .. CHECKED

    explicit VlStyle(QObject* owner);
    static VlStyle* qmlAttachedProperties(QObject* owner);

    /* The slot of a protocol field id, nullopt = unknown field. valueTypeOf: the slot's fixed VLQT_VALUE_TYPE_*. */
    static std::optional<Slot> slotOf(quint16 field);
    static quint16 valueTypeOf(Slot slot);

    /* SET_STYLE (scene/commands.cpp): clear() and setCell() edit the table, commit() refreshes the groups and signals.
     * v must have the slot's value type (checked by the caller). A new font family is reported once if the system
     * does not have it. */
    void clear();
    void setCell(Slot slot, int state, const Value& v);
    void commit();

    int state() const { return m_state; }
    void setState(int state);
    int revision() const { return m_revision; }
    VlStyleFrame* frame() { return &m_frame; }
    VlStyleText* text() { return &m_text; }
    VlStyleIndicator* indicator() { return &m_indicator; }
    VlStyleTrack* track() { return &m_track; }

    Q_INVOKABLE int stateOf(bool enabled, bool pressed, bool hovered, bool checked) const;
    Q_INVOKABLE QVariant value(Field field, int state) const;

    /* Resolved values of the current state (read by the groups). */
    QColor color(Slot slot) const { return m_colors[size_t(slot)]; }
    qreal number(Slot slot) const { return m_numbers[size_t(slot)]; }
    const QFont& font() const { return m_font; }

signals:
    void stateChanged();
    void revisionChanged();

private:
    struct Cell {
        bool set = false;
        float f[4] = {};  // color (rgba) or f32 (f[0])
        qint32 i = 0;     // i32
        QString s;        // string
    };
    const Cell* resolve(Slot slot, int state) const;
    QColor colorAt(Slot slot, int state) const;
    float numberAt(Slot slot, int state) const;
    QFont fontAt(int state) const;
    void refresh(); // current-state values from the table, signals per changed group

    std::array<std::array<Cell, stateCount>, size_t(Slot::Count)> m_cells;
    int m_state = 0;
    int m_revision = 0;
    bool m_dirty = false;
    std::array<QColor, size_t(Slot::Count)> m_colors{};
    std::array<qreal, size_t(Slot::Count)> m_numbers{};
    QFont m_font;
    VlStyleFrame m_frame;
    VlStyleText m_text;
    VlStyleIndicator m_indicator;
    VlStyleTrack m_track;
};

/* Registers VlStyle (uncreatable, attached only) as module "VLQt.Style" (C++ only, imported by the base style).
 * Once at app start. */
void registerStyleTypes();

} // namespace vlqt
