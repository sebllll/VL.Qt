#include "elements/element_types.h"
#include "elements/geometry.h"

#include <QtQuick/QQuickItem>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Button (QtQuick.Controls, first Controls element). Base style VLQt (style/VLQt/Button.qml), values from
// SET_STYLE (style/vl_style.h); style picked once at startup (app/app.cpp). Clicks are
// Qt's clicked signal (release over the button; autoRepeat repeats it while held; disabled = none), counted in
// vlqtClicks and sent as EVENT_CLICK by the poll for every bound Button (input handlers report their own clicks)
// (Elements::takeClicks). AUTO_SIZE on (default): implicit size from text + padding, else the SIZE.

constexpr const char* kButtonQml =
    "import QtQuick\n"
    "import QtQuick.Controls\n"
    "Button {\n"
    "  property int vlqtClicks: 0\n"
    "  onClicked: ++vlqtClicks\n"
    "}\n";

std::vector<PropDef> buttonProps()
{
    return {
        {VLQT_PROPERTY_TEXT, VLQT_VALUE_TYPE_STRING, setQmlString, "text"},
        {VLQT_PROPERTY_ENABLED, VLQT_VALUE_TYPE_BOOL, setQmlBool, "enabled"},
        {VLQT_PROPERTY_AUTO_REPEAT, VLQT_VALUE_TYPE_BOOL, setQmlBool, "autoRepeat"},
    };
}

// ---------------------------------------------------------------------------------------------
// Value inputs (SpinBox; CheckBox, Switch, Slider, TextField). Generic user-value channel: the QML
// snippet declares
//   property int vlqtUserCount   user changes since the last poll (reset to 0 by Elements::takeUserValue)
//   property <T> vlqtUserValue   value of the last user change, T = int | real | bool | string (= EVENT_VALUE value type)
// and sets both in the handler of the control's user-only signal (SpinBox valueModified, CheckBox/Switch toggled,
// Slider moved, TextField editingFinished with changed text). SET VALUE from C# never fires these signals (no echo), and the value is
// captured when the signal fires: a later SET VALUE with a stale value cannot hide it from the poll. The poll sends
// EVENT_VALUE for every bound item of such a type (scene/measure.cpp collectMeasurements), independent of input handlers.
//
// SpinBox: full int range instead of Qt's 0..99, editable and wheel on; text <-> value locale-free (plain digits,
// optional '-', no group separators, like vvvv's IOBoxes), the validator too (locale C). Qt commits a typed value on
// Enter or focus loss (valueModified); +/-, arrow keys and wheel commit at once. Qt clamps the value to [from, to]
// without a signal (C# clamps its own copy). AUTO_SIZE on (default): implicit size, else the SIZE.

constexpr const char* kSpinBoxQml =
    "import QtQuick\n"
    "import QtQuick.Controls\n"
    "SpinBox {\n"
    "  from: -2147483648\n"
    "  to: 2147483647\n"
    "  editable: true\n"
    "  wheelEnabled: true\n"
    "  locale: Qt.locale(\"C\")\n"
    "  textFromValue: function(v, l) { return v.toString() }\n"
    "  valueFromText: function(t, l) { var v = parseInt(t, 10); return isNaN(v) ? value : v }\n"
    "  property int vlqtUserCount: 0\n"
    "  property int vlqtUserValue: 0\n"
    "  onValueModified: { vlqtUserValue = value; ++vlqtUserCount }\n"
    "}\n";

std::vector<PropDef> spinBoxProps()
{
    return {
        {VLQT_PROPERTY_VALUE, VLQT_VALUE_TYPE_I32, setQmlInt, "value"},
        {VLQT_PROPERTY_MINIMUM, VLQT_VALUE_TYPE_I32, setQmlInt, "from"},
        {VLQT_PROPERTY_MAXIMUM, VLQT_VALUE_TYPE_I32, setQmlInt, "to"},
        {VLQT_PROPERTY_STEP, VLQT_VALUE_TYPE_I32, setQmlInt, "stepSize"},
        {VLQT_PROPERTY_EDITABLE, VLQT_VALUE_TYPE_BOOL, setQmlBool, "editable"},
        {VLQT_PROPERTY_ENABLED, VLQT_VALUE_TYPE_BOOL, setQmlBool, "enabled"},
    };
}

// Toggle: one vvvv node, two looks = two types, CheckBox and Switch. No text (labels are separate Text
// elements): spacing 0, so the implicit size is the indicator + style padding (style VLQt: INDICATOR_SIZE + padding, Switch lane 2 x size) and the
// indicator is centred in a larger SIZE. toggled = user only (click, Space); SET VALUE (checked) never fires it.
// Tristate and dragging the Switch handle are not exposed; a drag that flips the Switch also sends toggled.

// The two toggles differ only in the QML type (a macro: the text stays one literal with static storage).
#define VLQT_TOGGLE_QML(Type)                                        \
    "import QtQuick\n"                                               \
    "import QtQuick.Controls\n" Type " {\n"                          \
    "  spacing: 0\n"                                                 \
    "  property int vlqtUserCount: 0\n"                              \
    "  property bool vlqtUserValue: false\n"                         \
    "  onToggled: { vlqtUserValue = checked; ++vlqtUserCount }\n"    \
    "}\n"

constexpr const char* kCheckBoxQml = VLQT_TOGGLE_QML("CheckBox");
constexpr const char* kSwitchQml = VLQT_TOGGLE_QML("Switch");
#undef VLQT_TOGGLE_QML

std::vector<PropDef> toggleProps()
{
    return {
        {VLQT_PROPERTY_VALUE, VLQT_VALUE_TYPE_BOOL, setQmlBool, "checked"},
        {VLQT_PROPERTY_ENABLED, VLQT_VALUE_TYPE_BOOL, setQmlBool, "enabled"},
    };
}

// Slider: f32 value, Qt defaults 0..1, step 0 (continuous). moved = user only (press/drag, arrow keys, wheel;
// live, so every drag step), SET VALUE/MINIMUM/MAXIMUM never fire it (also not Qt's clamp). Several moves between two
// polls give one EVENT_VALUE with the last value. snapMode SnapAlways: with STEP > 0 a drag lands on from + k * step like
// keys and wheel; STEP 0 stays continuous. Wheel on (like SpinBox). ORIENTATION Qt::Horizontal 1 | Qt::Vertical 2.

constexpr const char* kSliderQml =
    "import QtQuick\n"
    "import QtQuick.Controls\n"
    "Slider {\n"
    "  wheelEnabled: true\n"
    "  snapMode: Slider.SnapAlways\n"
    "  property int vlqtUserCount: 0\n"
    "  property real vlqtUserValue: 0\n"
    "  onMoved: { vlqtUserValue = value; ++vlqtUserCount }\n"
    "}\n";

std::vector<PropDef> sliderProps()
{
    return {
        {VLQT_PROPERTY_VALUE, VLQT_VALUE_TYPE_F32, setQmlReal, "value"},
        {VLQT_PROPERTY_MINIMUM, VLQT_VALUE_TYPE_F32, setQmlReal, "from"},
        {VLQT_PROPERTY_MAXIMUM, VLQT_VALUE_TYPE_F32, setQmlReal, "to"},
        {VLQT_PROPERTY_STEP, VLQT_VALUE_TYPE_F32, setQmlReal, "stepSize"},
        {VLQT_PROPERTY_ORIENTATION, VLQT_VALUE_TYPE_I32, setQmlEnum<Qt::Horizontal, Qt::Vertical>, "orientation"},
        {VLQT_PROPERTY_ENABLED, VLQT_VALUE_TYPE_BOOL, setQmlBool, "enabled"},
    };
}

// TextField: single line, string value. Commit on Enter or focus loss (editingFinished), but only if the text
// differs from the last set or committed one (vlqtCommitted): a focus loss or Enter without an edit is no user change
// and would otherwise echo the value C# set (no echo, like SpinBox). SET VALUE writes text and vlqtCommitted together,
// so a SET while the user types replaces the edit and counts as the new reference. EDITABLE = !readOnly.

constexpr const char* kTextFieldQml =
    "import QtQuick\n"
    "import QtQuick.Controls\n"
    "TextField {\n"
    "  property string vlqtCommitted: \"\"\n"
    "  property int vlqtUserCount: 0\n"
    "  property string vlqtUserValue: \"\"\n"
    "  onEditingFinished: { if (text !== vlqtCommitted) { vlqtCommitted = text; vlqtUserValue = text; ++vlqtUserCount } }\n"
    "}\n";

// SET VALUE of TextField: text + vlqtCommitted (by name; only on SET, not per frame).
bool setTextFieldValue(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    const QString s = toQString(v);
    if (!writeQml(n.item, rp, s, err))
        return false;
    n.item->setProperty("vlqtCommitted", s);
    return true;
}

bool setQmlBoolNot(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, !v.b, err);
}

std::vector<PropDef> textFieldProps()
{
    return {
        {VLQT_PROPERTY_VALUE, VLQT_VALUE_TYPE_STRING, setTextFieldValue, "text"},
        {VLQT_PROPERTY_PLACEHOLDER, VLQT_VALUE_TYPE_STRING, setQmlString, "placeholderText"},
        {VLQT_PROPERTY_EDITABLE, VLQT_VALUE_TYPE_BOOL, setQmlBoolNot, "readOnly"},
        {VLQT_PROPERTY_ENABLED, VLQT_VALUE_TYPE_BOOL, setQmlBool, "enabled"},
    };
}

// What the Controls share: the implicit size (text + style padding) and SET_STYLE.
TypeDef control(quint16 id, const char* name, const char* qml, std::vector<PropDef> props)
{
    TypeDef t{id, name, qml, std::move(props)};
    t.autoSize = AutoSize::Always;
    t.styled = true;
    return t;
}

} // namespace

TypeDef buttonType()
{
    return control(VLQT_ELEMENT_TYPE_BUTTON, "BUTTON", kButtonQml, buttonProps());
}

TypeDef spinBoxType()
{
    return control(VLQT_ELEMENT_TYPE_SPIN_BOX, "SPIN_BOX", kSpinBoxQml, spinBoxProps());
}

TypeDef checkBoxType()
{
    return control(VLQT_ELEMENT_TYPE_CHECK_BOX, "CHECK_BOX", kCheckBoxQml, toggleProps());
}

TypeDef switchType()
{
    return control(VLQT_ELEMENT_TYPE_SWITCH, "SWITCH", kSwitchQml, toggleProps());
}

TypeDef sliderType()
{
    return control(VLQT_ELEMENT_TYPE_SLIDER, "SLIDER", kSliderQml, sliderProps());
}

TypeDef textFieldType()
{
    return control(VLQT_ELEMENT_TYPE_TEXT_FIELD, "TEXT_FIELD", kTextFieldQml, textFieldProps());
}

} // namespace vlqt
