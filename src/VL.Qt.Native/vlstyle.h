/* vlstyle.h - attached object VlStyle: the resolved style of one Controls element as a table field x state,
 * written by SET_STYLE (scene/commands.cpp) and read by the VL.Qt base style (style/VLQt/*.qml, embedded module "VLQt").
 *
 * QML API (see docs/styles.md):
 *   VlStyle.state                 int, writable, default STYLE_STATE_NORMAL; the style binds it to stateOf(...)
 *   VlStyle.stateOf(enabled, pressed, hovered, checked)  rank Disabled > Pressed > Hovered > Checked > Normal
 *   VlStyle.frame.{color, borderColor, radius, borderWidth, paddingX, paddingY, minWidth, minHeight}
 *   VlStyle.text.{color, font}    font: QFont from FontFamily ("" = Qt default), FontSize (px), FontWeight
 *   VlStyle.indicator.{color, borderColor, size, radius, borderWidth}
 *   VlStyle.track.{color, fillColor, thickness, radius}
 *   VlStyle.value(field, state)   one value for a part with a state of its own (SpinBox arrows), see revision
 *   VlStyle.revision              int, counts table changes (NOTIFY), for bindings on value()
 * The groups hold the values for the current state. Unset (field, state) -> (field, NORMAL) -> 0 / transparent / "".
 * Signals only on a real change; nothing is allocated per frame (cells are compared before they are written).
 * Attached per object, never inherited by children (C# resolves the inheritance). */
#pragma once

#include "elements/element_types.h"

#include <QtCore/QObject>
#include <QtCore/QString>
#include <QtCore/QVariant>
#include <QtGui/QColor>
#include <QtGui/QFont>
#include <QtQml/qqml.h>

#include <array>
#include <string>

namespace vlqt {

class VlStyle;

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
    const VlStyle* _style;
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
    const VlStyle* _style;
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
    const VlStyle* _style;
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
    const VlStyle* _style;
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
    /* Slots of the table: one per VLQT_STYLE_FIELD_* (slotOf maps the protocol id). */
    enum Slot {
        FrameColor, BorderColor, TextColor, IndicatorColor, TrackColor, TrackFillColor,
        FrameRadius, FrameBorderWidth, FramePaddingX, FramePaddingY, FrameMinWidth, FrameMinHeight,
        FontFamily, FontSize, FontWeight,
        IndicatorSize, IndicatorRadius, IndicatorBorderWidth,
        TrackThickness, TrackRadius,
        SlotCount
    };
    static constexpr int stateCount = 5; // VLQT_STYLE_STATE_NORMAL .. CHECKED

    explicit VlStyle(QObject* owner);
    static VlStyle* qmlAttachedProperties(QObject* owner);

    /* Protocol field id -> slot, -1 = unknown field. valueTypeOf: the field's fixed VLQT_VALUE_TYPE_*. */
    static int slotOf(quint16 field);
    static quint16 valueTypeOf(int slot);

    /* SET_STYLE (scene/commands.cpp): clear() and setCell() edit the table, commit() refreshes the groups and signals.
     * v must have the slot's value type (checked by the caller). */
    void clear();
    void setCell(int slot, int state, const Value& v);
    void commit();

    int state() const { return _state; }
    void setState(int state);
    int revision() const { return _revision; }
    VlStyleFrame* frame() { return &_frame; }
    VlStyleText* text() { return &_text; }
    VlStyleIndicator* indicator() { return &_indicator; }
    VlStyleTrack* track() { return &_track; }

    Q_INVOKABLE int stateOf(bool enabled, bool pressed, bool hovered, bool checked) const;
    Q_INVOKABLE QVariant value(int field, int state) const;

    /* Resolved values of the current state (read by the groups). */
    QColor color(int slot) const { return _colors[slot]; }
    qreal number(int slot) const { return _numbers[slot]; }
    const QFont& font() const { return _font; }

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
    const Cell* resolve(int slot, int state) const;
    QColor colorAt(int slot, int state) const;
    float numberAt(int slot, int state) const;
    QFont fontAt(int state) const;
    void refresh(); // current-state values from the table, signals per changed group

    std::array<std::array<Cell, stateCount>, SlotCount> _cells;
    int _state = 0;
    int _revision = 0;
    bool _dirty = false;
    std::array<QColor, SlotCount> _colors{};
    std::array<qreal, SlotCount> _numbers{};
    QFont _font;
    VlStyleFrame _frame;
    VlStyleText _text;
    VlStyleIndicator _indicator;
    VlStyleTrack _track;
};

/* Registers VlStyle (uncreatable, attached only) as module "VLQt.Style" (C++ only, imported by the base style).
 * Once at app start. */
void registerStyleTypes();

/* SET_STYLE payload (u32 id, u16 count, u16 flags, entries) on the element item; returns an ApplyResult. The whole
 * command is checked before the table is touched, so a bad entry leaves the style as it was. */
int applySetStyle(QQuickItem* item, const unsigned char* p, int plen, std::string& err);

} // namespace vlqt
