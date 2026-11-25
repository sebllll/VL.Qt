#include "elements/element_types.h"
#include "app/fonts.h"

#include <QtGui/QFont>
#include <QtGui/QGuiApplication>
#include <QtQml/QQmlParserStatus>
#include <QtQuick/private/qquicktext_p.h> // QQuickText: built in C++, typed setters

#include <algorithm>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Text: a QQuickText built in C++, written through its typed setters (no QVariant per SET). A font field is a copy of
// the item's QFont (implicitly shared) with the one field changed, written back only when the field differs.

QQuickItem* makeText()
{
    // The QML creation order, as for a Text {} element: classBegin, properties, componentComplete (both are protected
    // in the class, public in QQmlParserStatus). Text lays out only after componentComplete.
    auto* t = new QQuickText();
    QQmlParserStatus* status = t;
    status->classBegin();
    // PlainText: the C# element is plain text, Qt's default AutoText would interpret markup. minimumPixelSize 1 lets
    // fontSizeMode Fit shrink freely; lineHeight is proportional.
    t->setTextFormat(QQuickText::PlainText);
    t->setMinimumPixelSize(1);
    t->setLineHeightMode(QQuickText::ProportionalHeight);
    status->componentComplete();
    return t;
}

QQuickText* text(ElementEntry& n) { return static_cast<QQuickText*>(n.item); }

// The one place that writes the font: drops the metrics the debug overlay cached for it (diagnostics/debug_overlay.cpp).
void setFont(ElementEntry& n, const QFont& font)
{
    text(n)->setFont(font);
    n.fontMetricsValid = false;
}

bool setText(Elements&,ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    text(n)->setText(toQString(v));
    return true;
}

bool setColor(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    text(n)->setColor(toColor(v.f));
    return true;
}

bool setFontFamily(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    // Empty = default family (QGuiApplication font, like a fresh Text).
    const QString family = v.utf8.isEmpty() ? QGuiApplication::font().family() : toQString(v);
    if (!v.utf8.isEmpty())
        warnMissingFontFamily(family);
    QFont font = text(n)->font();
    if (font.family() != family) {
        font.setFamily(family);
        setFont(n, font);
    }
    return true;
}

bool setFontPixelSize(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    // QFont pixel sizes are integers; fractional sizes are rounded (scale via TRANSFORM for smooth zooms). Clamped
    // before the rounding: qRound of a float above INT_MAX is undefined.
    if (!(v.f[0] > 0)) {
        err = "font size must be > 0";
        return false;
    }
    const int px = qMax(1, qRound(std::min(v.f[0], 1e6f)));
    QFont font = text(n)->font();
    if (font.pixelSize() != px) {
        font.setPixelSize(px);
        setFont(n, font);
    }
    return true;
}

bool setFontWeight(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.i < 1 || v.i > 1000) {
        err = "font weight must be 1..1000";
        return false;
    }
    QFont font = text(n)->font();
    if (font.weight() != QFont::Weight(v.i)) {
        font.setWeight(QFont::Weight(v.i));
        setFont(n, font);
    }
    return true;
}

bool setFontItalic(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    QFont font = text(n)->font();
    if (font.italic() != v.b) {
        font.setItalic(v.b);
        setFont(n, font);
    }
    return true;
}

bool setLetterSpacing(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    QFont font = text(n)->font();
    if (font.letterSpacingType() != QFont::AbsoluteSpacing || font.letterSpacing() != v.f[0]) {
        font.setLetterSpacing(QFont::AbsoluteSpacing, v.f[0]);
        setFont(n, font);
    }
    return true;
}

bool setLineHeight(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    text(n)->setLineHeight(v.f[0]);
    return true;
}

/* i32 enum property of QQuickText: only the listed Qt values are accepted. */
template <class E, void (QQuickText::*Set)(E), int... Allowed>
bool setTextEnum(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (!checkEnum<Allowed...>(v, err))
        return false;
    (text(n)->*Set)(static_cast<E>(v.i));
    return true;
}

std::vector<PropDef> textProps()
{
    return {
        {VLQT_PROPERTY_TEXT, VLQT_VALUE_TYPE_STRING, setText, nullptr},
        {VLQT_PROPERTY_FONT_FAMILY, VLQT_VALUE_TYPE_STRING, setFontFamily, nullptr},
        {VLQT_PROPERTY_FONT_SIZE, VLQT_VALUE_TYPE_F32, setFontPixelSize, nullptr},
        {VLQT_PROPERTY_FONT_WEIGHT, VLQT_VALUE_TYPE_I32, setFontWeight, nullptr},
        {VLQT_PROPERTY_FONT_ITALIC, VLQT_VALUE_TYPE_BOOL, setFontItalic, nullptr},
        {VLQT_PROPERTY_COLOR, VLQT_VALUE_TYPE_COLOR, setColor, nullptr},
        // Qt::AlignLeft/Right/HCenter/Justify, Qt::AlignTop/Bottom/VCenter
        {VLQT_PROPERTY_HORIZONTAL_ALIGNMENT, VLQT_VALUE_TYPE_I32,
         setTextEnum<QQuickText::HAlignment, &QQuickText::setHAlign, Qt::AlignLeft, Qt::AlignRight, Qt::AlignHCenter, Qt::AlignJustify>, nullptr},
        {VLQT_PROPERTY_VERTICAL_ALIGNMENT, VLQT_VALUE_TYPE_I32,
         setTextEnum<QQuickText::VAlignment, &QQuickText::setVAlign, Qt::AlignTop, Qt::AlignBottom, Qt::AlignVCenter>, nullptr},
        // Text.NoWrap/WordWrap/WrapAnywhere/Wrap
        {VLQT_PROPERTY_WRAP_MODE, VLQT_VALUE_TYPE_I32,
         setTextEnum<QQuickText::WrapMode, &QQuickText::setWrapMode, QQuickText::NoWrap, QQuickText::WordWrap, QQuickText::WrapAnywhere, QQuickText::Wrap>, nullptr},
        // Text.ElideLeft/Right/Middle/None
        {VLQT_PROPERTY_ELIDE, VLQT_VALUE_TYPE_I32,
         setTextEnum<QQuickText::TextElideMode, &QQuickText::setElideMode, QQuickText::ElideLeft, QQuickText::ElideRight, QQuickText::ElideMiddle, QQuickText::ElideNone>, nullptr},
        {VLQT_PROPERTY_LINE_HEIGHT, VLQT_VALUE_TYPE_F32, setLineHeight, nullptr},
        {VLQT_PROPERTY_LETTER_SPACING, VLQT_VALUE_TYPE_F32, setLetterSpacing, nullptr},
        // Text.FixedSize/HorizontalFit/VerticalFit/Fit
        {VLQT_PROPERTY_FONT_SIZE_MODE, VLQT_VALUE_TYPE_I32,
         setTextEnum<QQuickText::FontSizeMode, &QQuickText::setFontSizeMode, QQuickText::FixedSize, QQuickText::HorizontalFit, QQuickText::VerticalFit, QQuickText::Fit>, nullptr},
    };
}

} // namespace

TypeDef textType()
{
    return {VLQT_ELEMENT_TYPE_TEXT, "TEXT", nullptr, textProps(), makeText};
}

} // namespace vlqt
