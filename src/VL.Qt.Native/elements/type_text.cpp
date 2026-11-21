#include "elements/element_types.h"
#include "app/fonts.h"

#include <QtGui/QFont>
#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickItem>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Text. textFormat PlainText: the C# element is plain text, Qt's default AutoText would interpret
// markup. minimumPixelSize 1 so fontSizeMode Fit can shrink freely; lineHeight is proportional.
// font.* fields go through QFont read-modify-write (modifyFont).

constexpr const char* kTextQml =
    "import QtQuick\nText { textFormat: Text.PlainText; minimumPixelSize: 1; lineHeightMode: Text.ProportionalHeight }\n";

/* Read-modify-write of a QFont property (rp.prop = "font"). */
template <class F>
bool modifyFont(QObject* item, const ResolvedProp& rp, F&& f, std::string& err)
{
    QFont font = rp.prop.read(item).value<QFont>();
    f(font);
    if (!rp.prop.write(item, QVariant::fromValue(font))) {
        err = "writing QML property 'font' failed";
        return false;
    }
    return true;
}

bool setFontFamily(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    // Empty = default family (QGuiApplication font, like a fresh Text).
    const QString family = v.utf8.isEmpty() ? QGuiApplication::font().family() : toQString(v);
    if (!v.utf8.isEmpty())
        warnMissingFontFamily(family);
    return modifyFont(n.item, rp, [&](QFont& f) { f.setFamily(family); }, err);
}

bool setFontPixelSize(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    // QFont pixel sizes are integers; fractional sizes are rounded (scale via TRANSFORM for smooth zooms).
    if (!(v.f[0] > 0)) {
        err = "font size must be > 0";
        return false;
    }
    const int px = qMax(1, qRound(v.f[0]));
    return modifyFont(n.item, rp, [&](QFont& f) { f.setPixelSize(px); }, err);
}

bool setFontWeight(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (v.i < 1 || v.i > 1000) {
        err = "font weight must be 1..1000";
        return false;
    }
    return modifyFont(n.item, rp, [&](QFont& f) { f.setWeight(QFont::Weight(v.i)); }, err);
}

bool setFontItalic(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return modifyFont(n.item, rp, [&](QFont& f) { f.setItalic(v.b); }, err);
}

bool setLetterSpacing(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return modifyFont(n.item, rp, [&](QFont& f) { f.setLetterSpacing(QFont::AbsoluteSpacing, v.f[0]); }, err);
}

std::vector<PropDef> textProps()
{
    return {
        {VLQT_PROPERTY_TEXT, VLQT_VALUE_TYPE_STRING, setQmlString, "text"},
        {VLQT_PROPERTY_FONT_FAMILY, VLQT_VALUE_TYPE_STRING, setFontFamily, "font"},
        {VLQT_PROPERTY_FONT_SIZE, VLQT_VALUE_TYPE_F32, setFontPixelSize, "font"},
        {VLQT_PROPERTY_FONT_WEIGHT, VLQT_VALUE_TYPE_I32, setFontWeight, "font"},
        {VLQT_PROPERTY_FONT_ITALIC, VLQT_VALUE_TYPE_BOOL, setFontItalic, "font"},
        {VLQT_PROPERTY_COLOR, VLQT_VALUE_TYPE_COLOR, setQmlColor, "color"},
        // Qt::AlignLeft/Right/HCenter/Justify, Qt::AlignTop/Bottom/VCenter
        {VLQT_PROPERTY_HORIZONTAL_ALIGNMENT, VLQT_VALUE_TYPE_I32, setQmlEnum<0x1, 0x2, 0x4, 0x8>, "horizontalAlignment"},
        {VLQT_PROPERTY_VERTICAL_ALIGNMENT, VLQT_VALUE_TYPE_I32, setQmlEnum<0x20, 0x40, 0x80>, "verticalAlignment"},
        // Text.NoWrap/WordWrap/WrapAnywhere/Wrap
        {VLQT_PROPERTY_WRAP_MODE, VLQT_VALUE_TYPE_I32, setQmlEnum<0, 1, 3, 4>, "wrapMode"},
        // Text.ElideLeft/Right/Middle/None
        {VLQT_PROPERTY_ELIDE, VLQT_VALUE_TYPE_I32, setQmlEnum<0, 1, 2, 3>, "elide"},
        {VLQT_PROPERTY_LINE_HEIGHT, VLQT_VALUE_TYPE_F32, setQmlReal, "lineHeight"},
        {VLQT_PROPERTY_LETTER_SPACING, VLQT_VALUE_TYPE_F32, setLetterSpacing, "font"},
        // Text.FixedSize/HorizontalFit/VerticalFit/Fit
        {VLQT_PROPERTY_FONT_SIZE_MODE, VLQT_VALUE_TYPE_I32, setQmlEnum<0, 1, 2, 3>, "fontSizeMode"},
    };
}

} // namespace

TypeDef textType()
{
    return {VLQT_ELEMENT_TYPE_TEXT, "TEXT", kTextQml, textProps()};
}

} // namespace vlqt
