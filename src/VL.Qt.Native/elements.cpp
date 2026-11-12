#include "elements.h"
#include "effectchain.h"

#include "qtimageitem.h"
#include "qmlitem.h"
#include "shadereffectitem.h"

#include <QtCore/QFileInfo>
#include <QtCore/QLoggingCategory>
#include <QtCore/QSet>
#include <QtCore/QUrl>
#include <QtGui/QColor>
#include <QtGui/QFontDatabase>
#include <QtGui/QFontInfo>
#include <QtGui/QGuiApplication>
#include <QtGui/QMatrix4x4>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQml/QQmlListReference>
#include <QtQml/QQmlProperty>
#include <QtQuick/QQuickItem>
#include <QtQuick/private/qquickitem_p.h>               // explicitVisible of Group children (#486)
#include <QtQuick/private/qquickrectangle_p.h>          // typed color setters (#560)
#include <QtQuick/private/qquicktext_p.h>               // typed color setter (#560)
#include <QtQuick/private/qquicktranslate_p.h>          // QQuickMatrix4x4::setMatrix without QVariant (#560)
#include <QtQuick/private/qquickshadereffectsource_p.h> // Group frame proxy (#486)

#include <algorithm>
#include <cmath>
#include <functional>
#include <typeinfo>
#include <cstring>

Q_LOGGING_CATEGORY(lcElements, "vlqt.elements")
Q_LOGGING_CATEGORY(lcFont, "vlqt.font")

namespace vlqt {

// The family names of Text and Style are free strings; Qt silently takes another font for one it does not know (empty
// boxes on a Linux without fonts), so the log says it once per name. hasFamily covers installed and app fonts
// (LoadFont); QFontInfo catches names Qt maps itself (aliases, generic families like "Sans Serif").
void warnMissingFontFamily(const QString& family)
{
    if (family.isEmpty())
        return;
    static QSet<QString> checked; // GUI thread only, one entry per name ever set
    if (checked.contains(family))
        return;
    checked.insert(family);
    if (QFontDatabase::hasFamily(family))
        return;
    const QString actual = QFontInfo(QFont(family)).family();
    if (actual.compare(family, Qt::CaseInsensitive) == 0)
        return;
    qCWarning(lcFont, "Font family '%s' not found, Qt uses '%s' instead. Install it on this system or load the file with LoadFont.",
              qPrintable(family), qPrintable(actual));
}
namespace {

QColor toColor(const float* f) { return QColor::fromRgbF(f[0], f[1], f[2], f[3]); }

// ---------------------------------------------------------------------------------------------
// Common properties (ids 1..255): C++ QQuickItem API, no QML lookup.

// Other types (Rectangle, Text, Image) always take SIZE, also 0 (not drawn, not clickable).
// Button and the value inputs take part in AUTO_SIZE like the containers (implicit size = content + style padding,
// #213, #210, #301).
void setGroupAutoSize(QQuickItem* item, bool on); // GroupItem, below

void applySize(Node& n)
{
    if (n.type == VLQT_TYPE_ITEM)
        setGroupAutoSize(n.item, n.autoSize); // frame origin: extent with Auto Size, (0, 0) with SIZE
    if (n.type == VLQT_TYPE_QML)
        static_cast<QmlItem*>(n.item)->setAutoSize(n.autoSize); // root = implicit size of the QML, or this item's SIZE (#104)
    if (isAutoSized(n)) {
        n.item->resetWidth();
        n.item->resetHeight();
    } else {
        n.item->setWidth(n.size[0]);
        n.item->setHeight(n.size[1]);
    }
}

// SIZE (#469): width/height only; the position comes from ANCHOR + TRANSFORM (see "Geometry" in protocol.h). The
// anchor placement follows the size change by itself (Elements::initGeometry), also for implicit sizes.
bool setSize(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string&)
{
    n.size[0] = v.f[0];
    n.size[1] = v.f[1];
    applySize(n);
    return true;
}

// AUTO_SIZE (containers only, default on): SIZE is ignored and the implicit size applies (Row/Column:
// content + padding, Group: extent of the children). Off: SIZE counts, 0 stays 0.
bool setAutoSize(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string&)
{
    n.autoSize = v.b;
    applySize(n);
    return true;
}

bool setTransform(Elements& els, Node& n, const ResolvedProp&, const Value& v, std::string& err)
{
    // Wire: row-major like Stride, row vectors (translation in M41..M43). QMatrix4x4(const float*)
    // reads row-major too, but Qt uses column vectors -> transpose (translation lands in column 4).
    if (!els.setMatrix(n, QMatrix4x4(v.f).transposed(), err))
        return false;
    // Pin wins (#209, #469): every TRANSFORM (also a resync) puts the item back where the pin says, the drag offset
    // goes to 0. SIZE/ANCHOR keep it.
    ++n.transformEpoch;
    return els.setDragOffset(n, 0, 0, err);
}

// ANCHOR: Qt::Alignment with exactly one horizontal (Left/Right/HCenter) and one vertical flag (Top/Bottom/VCenter).
// Outside Row/Column it places the item (x/y = -anchor) and is the pivot of TRANSFORM, inside only the pivot.
bool setAnchor(Elements& els, Node& n, const ResolvedProp&, const Value& v, std::string& err)
{
    const int h = v.i & 0x0F, vert = v.i & 0xF0;
    const float fx = h == 0x1 ? 0.f : h == 0x2 ? 1.f : h == 0x4 ? 0.5f : -1.f;
    const float fy = vert == 0x20 ? 0.f : vert == 0x40 ? 1.f : vert == 0x80 ? 0.5f : -1.f;
    if (fx < 0 || fy < 0 || (v.i & ~0xFF)) {
        err = "anchor must be one horizontal and one vertical Qt::AlignmentFlag";
        return false;
    }
    return els.setAnchor(n, fx, fy, err);
}
bool setOpacity(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string&) { n.item->setOpacity(v.f[0]); return true; }
bool setClip(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string&) { n.item->setClip(v.b); return true; }
// DEBUG (#279): only a flag for the debug overlay (debug.h), the item itself is not touched.
bool setDebug(Elements& els, Node& n, const ResolvedProp&, const Value& v, std::string&)
{
    if (n.debug != v.b)
        els.debugCount += v.b ? 1 : -1;
    n.debug = v.b;
    return true;
}

const std::vector<PropDef>& commonProps()
{
    static const std::vector<PropDef> t = {
        {VLQT_PROP_SIZE, VLQT_VT_VEC2, setSize, nullptr},
        {VLQT_PROP_TRANSFORM, VLQT_VT_MAT4, setTransform, nullptr},
        {VLQT_PROP_OPACITY, VLQT_VT_F32, setOpacity, nullptr},
        {VLQT_PROP_CLIP, VLQT_VT_BOOL, setClip, nullptr},
        {VLQT_PROP_ANCHOR, VLQT_VT_I32, setAnchor, nullptr},
        {VLQT_PROP_DEBUG, VLQT_VT_BOOL, setDebug, nullptr},
    };
    return t;
}

// EFFECT_* (block 832): keys of SET_EFFECT_CHAIN MULTI entries only (#489, effectchain.cpp); SET with one is -6.
bool isEffectProp(quint16 prop) { return prop >= VLQT_PROP_EFFECT_ENABLED && prop <= VLQT_PROP_EFFECT_MASK_INVERTED; }

// ---------------------------------------------------------------------------------------------
// Generic setters for QML properties (resolved QMetaProperty, optionally grouped).

bool setQmlColor(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    // Typed setters (perf, #560): QMetaProperty::write(QVariant(QColor)) per element and frame costs several percent
    // CPU. The Qt setters compare first, so an unchanged color stays a no-op. Everything else takes the QML property.
    const QColor c = toColor(v.f);
    if (auto* r = qobject_cast<QQuickRectangle*>(n.item)) {
        if (rp.sub.isValid())
            r->border()->setColor(c); // "border.color"
        else
            r->setColor(c);
        return true;
    }
    if (auto* t = qobject_cast<QQuickText*>(n.item); t && !rp.sub.isValid()) {
        t->setColor(c);
        return true;
    }
    return writeQml(n.item, rp, c, err);
}

bool setQmlBool(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, v.b, err);
}

bool setQmlReal(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, qreal(v.f[0]), err);
}

bool setQmlRect(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, QRectF(v.f[0], v.f[1], v.f[2], v.f[3]), err);
}

bool setQmlInt(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, int(v.i), err);
}

// ---------------------------------------------------------------------------------------------
// Rectangle. border.width 0 matches the C# default StrokeWidth 0: Qt's default width is 1 and
// draws a border as soon as border.color is set, so a lone SET border color must not show one.

constexpr const char* kRectangleQml = "import QtQuick\nRectangle { border.width: 0 }\n";

std::vector<PropDef> rectangleProps()
{
    return {
        {VLQT_PROP_COLOR, VLQT_VT_COLOR, setQmlColor, "color"},
        {VLQT_PROP_RADIUS, VLQT_VT_F32, setQmlReal, "radius"},
        {VLQT_PROP_BORDER_COLOR, VLQT_VT_COLOR, setQmlColor, "border.color"},
        {VLQT_PROP_BORDER_WIDTH, VLQT_VT_F32, setQmlReal, "border.width"},
    };
}

// ---------------------------------------------------------------------------------------------
// Text. textFormat PlainText: the C# element is plain text, Qt's default AutoText would interpret
// markup. minimumPixelSize 1 so fontSizeMode Fit can shrink freely; lineHeight is proportional.
// font.* fields go through QFont read-modify-write (modifyFont).

constexpr const char* kTextQml =
    "import QtQuick\nText { textFormat: Text.PlainText; minimumPixelSize: 1; lineHeightMode: Text.ProportionalHeight }\n";

QString toQString(const Value& v) { return QString::fromUtf8(v.utf8.constData(), v.utf8.size()); }

bool setQmlString(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, toQString(v), err);
}

/* i32 enum property: only the listed Qt values are accepted (others -> ApplyBadValue). */
template <int... Allowed>
bool setQmlEnum(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (((v.i != Allowed) && ...)) {
        err = "invalid enum value " + std::to_string(v.i);
        return false;
    }
    return writeQml(n.item, rp, v.i, err);
}

bool setFontFamily(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    // Empty = default family (QGuiApplication font, like a fresh Text).
    const QString family = v.utf8.isEmpty() ? QGuiApplication::font().family() : toQString(v);
    if (!v.utf8.isEmpty())
        warnMissingFontFamily(family);
    return modifyFont(n.item, rp, [&](QFont& f) { f.setFamily(family); }, err);
}

bool setFontPixelSize(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    // QFont pixel sizes are integers; fractional sizes are rounded (scale via TRANSFORM for smooth zooms).
    if (!(v.f[0] > 0)) {
        err = "font size must be > 0";
        return false;
    }
    const int px = qMax(1, qRound(v.f[0]));
    return modifyFont(n.item, rp, [&](QFont& f) { f.setPixelSize(px); }, err);
}

bool setFontWeight(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (v.i < 1 || v.i > 1000) {
        err = "font weight must be 1..1000";
        return false;
    }
    return modifyFont(n.item, rp, [&](QFont& f) { f.setWeight(QFont::Weight(v.i)); }, err);
}

bool setFontItalic(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return modifyFont(n.item, rp, [&](QFont& f) { f.setItalic(v.b); }, err);
}

bool setLetterSpacing(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return modifyFont(n.item, rp, [&](QFont& f) { f.setLetterSpacing(QFont::AbsoluteSpacing, v.f[0]); }, err);
}

std::vector<PropDef> textProps()
{
    return {
        {VLQT_PROP_TEXT, VLQT_VT_STRING, setQmlString, "text"},
        {VLQT_PROP_FONT_FAMILY, VLQT_VT_STRING, setFontFamily, "font"},
        {VLQT_PROP_FONT_SIZE, VLQT_VT_F32, setFontPixelSize, "font"},
        {VLQT_PROP_FONT_WEIGHT, VLQT_VT_I32, setFontWeight, "font"},
        {VLQT_PROP_FONT_ITALIC, VLQT_VT_BOOL, setFontItalic, "font"},
        {VLQT_PROP_COLOR, VLQT_VT_COLOR, setQmlColor, "color"},
        // Qt::AlignLeft/Right/HCenter/Justify, Qt::AlignTop/Bottom/VCenter
        {VLQT_PROP_HORIZONTAL_ALIGNMENT, VLQT_VT_I32, setQmlEnum<0x1, 0x2, 0x4, 0x8>, "horizontalAlignment"},
        {VLQT_PROP_VERTICAL_ALIGNMENT, VLQT_VT_I32, setQmlEnum<0x20, 0x40, 0x80>, "verticalAlignment"},
        // Text.NoWrap/WordWrap/WrapAnywhere/Wrap
        {VLQT_PROP_WRAP_MODE, VLQT_VT_I32, setQmlEnum<0, 1, 3, 4>, "wrapMode"},
        // Text.ElideLeft/Right/Middle/None
        {VLQT_PROP_ELIDE, VLQT_VT_I32, setQmlEnum<0, 1, 2, 3>, "elide"},
        {VLQT_PROP_LINE_HEIGHT, VLQT_VT_F32, setQmlReal, "lineHeight"},
        {VLQT_PROP_LETTER_SPACING, VLQT_VT_F32, setLetterSpacing, "font"},
        // Text.FixedSize/HorizontalFit/VerticalFit/Fit
        {VLQT_PROP_FONT_SIZE_MODE, VLQT_VT_I32, setQmlEnum<0, 1, 2, 3>, "fontSizeMode"},
    };
}

// ---------------------------------------------------------------------------------------------
// Error state of an element (#273): a QML string property vlqtError on the root item, empty = no error. Read by
// the poll (Elements::readError) and sent as EV_ERROR to the bound sink, i.e. shown at the element's vvvv node.
// vlqtPath: local path of a file URL for messages (file:///C:/a%20b.png -> C:/a b.png, file:///home/x -> /home/x).
#define VLQT_QML_PATH_FN \
    "  function vlqtPath(u) { return decodeURIComponent(String(u).replace(/^file:\\/\\/(\\/(?=[A-Za-z]:))?/, \"\")); }\n"

// Video source: empty = no media. Relative paths resolve against the process working directory (C# sends
// absolute paths); QUrl::fromLocalFile handles drive letters, spaces and non-ASCII names.
bool setMediaSource(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    const QUrl url = v.utf8.isEmpty() ? QUrl() : QUrl::fromLocalFile(QFileInfo(toQString(v)).absoluteFilePath());
    return writeQml(n.item, rp, url, err);
}

// ---------------------------------------------------------------------------------------------
// Image (#361): QtImageItem draws an image resource (vlqt_res_*, image_resource.h) instead of a file URL, so one
// upload serves every element and window. No SOURCE any more (-6); load errors belong to the resource (RES_EV_ERROR).

QQuickItem* makeImage() { return new QtImageItem(); }

bool setImageResource(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string& err)
{
    return static_cast<QtImageItem*>(n.item)->setResource(v.u, err);
}

// ShaderEffect (#437, protocol.h "ShaderEffect"): ShaderEffectItem (shadereffectitem.h), own QQuickItem with a
// QSGMaterialShader. SOURCE goes through the effect source bookkeeping (layer, hidden, cleared before deletion).
QQuickItem* makeShaderEffect() { return new ShaderEffectItem(); }

// QmlElement (#104, protocol.h "QmlElement"): QmlItem (qmlitem.h), own QQuickItem that holds the instance of a user .qml file.
QQuickItem* makeQml() { return new QmlItem(); }

bool setQmlSource(Elements& els, Node& n, const ResolvedProp&, const Value& v, std::string&)
{
    static_cast<QmlItem*>(n.item)->setSource(els.qmlEngine(), QString::fromUtf8(v.utf8));
    applySize(n); // Auto Size / SIZE reach the new root
    return true;
}

bool setQmlReload(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string&)
{
    static_cast<QmlItem*>(n.item)->setReload(v.i);
    return true;
}

std::vector<PropDef> qmlProps()
{
    return {
        {VLQT_PROP_QML_SOURCE, VLQT_VT_STRING, setQmlSource, nullptr},
        {VLQT_PROP_QML_RELOAD, VLQT_VT_I32, setQmlReload, nullptr},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

ShaderEffectItem* shaderItem(Node& n) { return static_cast<ShaderEffectItem*>(n.item); }

bool setFragmentShader(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string& err)
{
    return shaderItem(n)->setShader(VLQT_SHADER_STAGE_FRAGMENT, v.u, err);
}

bool setVertexShader(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string& err)
{
    return shaderItem(n)->setShader(VLQT_SHADER_STAGE_VERTEX, v.u, err);
}

bool setBlending(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string&)
{
    shaderItem(n)->setBlending(v.b);
    return true;
}

bool setMeshResolution(Elements&, Node& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.i < 1 || v.i2 < 1) {
        err = "mesh resolution must be >= 1, 1";
        return false;
    }
    shaderItem(n)->setMeshResolution(v.i, v.i2);
    return true;
}

bool setEffectSourceRef(Elements& els, Node& n, const ResolvedProp& rp, const Value& v, std::string& err);

bool setShaderSourceRef(Elements& els, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (!setEffectSourceRef(els, n, rp, v, err))
        return false;
    applySize(n); // Auto Size follows the Input only while there is one
    return true;
}

std::vector<PropDef> shaderEffectProps()
{
    return {
        {VLQT_PROP_FRAGMENT_SHADER, VLQT_VT_U64, setFragmentShader, nullptr},
        {VLQT_PROP_VERTEX_SHADER, VLQT_VT_U64, setVertexShader, nullptr},
        {VLQT_PROP_BLENDING, VLQT_VT_BOOL, setBlending, nullptr},
        {VLQT_PROP_MESH_RESOLUTION, VLQT_VT_IVEC2, setMeshResolution, nullptr},
        {VLQT_PROP_SOURCE, VLQT_VT_REF, setShaderSourceRef, "source"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

std::vector<PropDef> imageProps()
{
    return {
        {VLQT_PROP_IMAGE, VLQT_VT_U64, setImageResource, nullptr},
        // Stretch/PreserveAspectFit/PreserveAspectCrop/Tile (= C# ImageFillMode, values of QML Image.fillMode)
        {VLQT_PROP_FILL_MODE, VLQT_VT_I32, setQmlEnum<0, 1, 2, 3>, "fillMode"},
        {VLQT_PROP_SMOOTH, VLQT_VT_BOOL, setQmlBool, "smooth"},
    };
}

// ---------------------------------------------------------------------------------------------
// Row/Column: Qt positioners (QtQuick Row/Column, not QtQuick.Layouts). They only set the main-axis
// coordinate (Column: y, Row: x) and never touch child sizes, so the SIZE of a child stays its size.
// Positioners have no cross-axis alignment: the one-liner adds an int property childAlignment
// (Qt::AlignmentFlag, = C# HorizontalAlignment / VerticalAlignment) and sets the cross-axis coordinate
// of every child after each positioning pass (positioningComplete), on a size change of the container
// and on an alignment change. Reference extent = own width/height (implicit = content with AUTO_SIZE
// on) minus padding. Children with width or height 0 or visible false are skipped by Qt (not laid
// out). The layout owns x/y of its children (no anchor placement, ANCHOR is only the pivot, #469).
// TRANSFORM of a child stays visual: it acts in the child's local coordinates after positioning
// and does not move siblings.

constexpr const char* kColumnQml =
    "import QtQuick\n"
    "Column {\n"
    "  property int childAlignment: Qt.AlignLeft\n"
    "  function vlqtAlign() {\n"
    "    const w = width - leftPadding - rightPadding;\n"
    "    const f = childAlignment === Qt.AlignRight ? 1 : childAlignment === Qt.AlignHCenter ? 0.5 : 0;\n"
    "    for (let i = 0; i < children.length; ++i) { const c = children[i]; c.x = leftPadding + (w - c.width) * f; }\n"
    "  }\n"
    "  onPositioningComplete: vlqtAlign()\n"
    "  onWidthChanged: vlqtAlign()\n"
    "  onChildAlignmentChanged: vlqtAlign()\n"
    "}\n";

constexpr const char* kRowQml =
    "import QtQuick\n"
    "Row {\n"
    "  property int childAlignment: Qt.AlignTop\n"
    "  function vlqtAlign() {\n"
    "    const h = height - topPadding - bottomPadding;\n"
    "    const f = childAlignment === Qt.AlignBottom ? 1 : childAlignment === Qt.AlignVCenter ? 0.5 : 0;\n"
    "    for (let i = 0; i < children.length; ++i) { const c = children[i]; c.y = topPadding + (h - c.height) * f; }\n"
    "  }\n"
    "  onPositioningComplete: vlqtAlign()\n"
    "  onHeightChanged: vlqtAlign()\n"
    "  onChildAlignmentChanged: vlqtAlign()\n"
    "}\n";

std::vector<PropDef> columnProps()
{
    return {
        {VLQT_PROP_SPACING, VLQT_VT_F32, setQmlReal, "spacing"},
        {VLQT_PROP_PADDING, VLQT_VT_F32, setQmlReal, "padding"},
        // Qt::AlignLeft/Right/HCenter (= C# HorizontalAlignment)
        {VLQT_PROP_CHILD_ALIGNMENT, VLQT_VT_I32, setQmlEnum<0x1, 0x2, 0x4>, "childAlignment"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

std::vector<PropDef> rowProps()
{
    return {
        {VLQT_PROP_SPACING, VLQT_VT_F32, setQmlReal, "spacing"},
        {VLQT_PROP_PADDING, VLQT_VT_F32, setQmlReal, "padding"},
        // Qt::AlignTop/Bottom/VCenter (= C# VerticalAlignment)
        {VLQT_PROP_CHILD_ALIGNMENT, VLQT_VT_I32, setQmlEnum<0x20, 0x40, 0x80>, "childAlignment"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

// ---------------------------------------------------------------------------------------------
// Group (#469, "Group frame" in protocol.h): an Item whose implicit size is its frame = bounding box of the children
// (mapped through their x/y AND transform, unlike Qt's childrenRect) and of its own origin. With Auto Size on the frame
// may start left of / above the origin (xf->ox/oy < 0): width/height = frame size, hit area (contains) and clip follow
// the frame, the anchor resolves against it (Left/Top stay at the origin, see anchorPoint). Recomputed in
// updatePolish (once per frame, before sync) after a child moved, resized, was shown/hidden, added/removed or got a
// new matrix (Elements::writeTransform pokes the parent, Qt has no public signal for transform changes).
class GroupItem final : public QQuickItem
{
public:
    std::shared_ptr<TransformState> xf; // Elements::initGeometry
    std::function<void()> placed;       // re-places the group after a frame origin change (initGeometry)
    QPointer<QObject> effectHolder;     // first stage of the effect chain (Elements::setEffectChain): frameLeft/frameTop follow the frame
    QPointer<QQuickShaderEffectSource> proxy; // texture of the whole frame for ShaderEffects (#486, Elements::acquireGroupProxy)
    int proxyRefs = 0;                  // Input/texture uniform uses of proxy
    QMetaObject::Connection proxyRetire; // destroyed -> retire proxy (acquireGroupProxy), dropped on release

    // The effect chain's padding covers the part of the frame left of / above the origin (#475, effectchain.cpp); the
    // ShaderEffect proxy samples exactly the frame (#486). Called on every frame change (origin, size, anchor, Auto Size).
    void syncEffectFrame()
    {
        if ((!effectHolder && !proxy) || !xf)
            return;
        const QRectF f = itemFrame(*xf, this);
        if (effectHolder) {
            effectHolder->setProperty("frameLeft", std::max<qreal>(0, -f.left()));
            effectHolder->setProperty("frameTop", std::max<qreal>(0, -f.top()));
        }
        if (proxy) {
            proxy->setSourceRect(f);
            proxy->setSize(f.size()); // = implicit size of the ShaderEffect (Auto Size)
        }
    }

    ~GroupItem() override
    {
        // ~QQuickItem detaches the children after this part is gone: no polish() from their signals any more.
        for (QQuickItem* c : childItems())
            QObject::disconnect(c, nullptr, this, nullptr);
    }

    void setAutoSize(bool on)
    {
        if (autoSize_ == on)
            return;
        autoSize_ = on;
        if (xf) {
            xf->sizedFrame = !on;
            if (placed)
                placed(); // anchor placement and pivot change with the frame kind
            syncEffectFrame(); // the frame kind changes even if origin and size stay
            markGeometry(*xf);
        }
        invalidate();
    }

    // The frame has to be recomputed (next polish).
    void invalidate()
    {
        dirty_ = true;
        polish();
    }

protected:
    void itemChange(ItemChange change, const ItemChangeData& data) override
    {
        if (change == ItemChildAddedChange) {
            QQuickItem* c = data.item;
            auto again = [this] { invalidate(); };
            QObject::connect(c, &QQuickItem::xChanged, this, again);
            QObject::connect(c, &QQuickItem::yChanged, this, again);
            QObject::connect(c, &QQuickItem::widthChanged, this, again);
            QObject::connect(c, &QQuickItem::heightChanged, this, again);
            QObject::connect(c, &QQuickItem::visibleChanged, this, again);
            invalidate();
        } else if (change == ItemChildRemovedChange) {
            QObject::disconnect(data.item, nullptr, this, nullptr);
            invalidate();
        }
        QQuickItem::itemChange(change, data);
    }

    void updatePolish() override
    {
        // Only after a change: Scene::polish (poll without a render) calls ensurePolished, i.e. updatePolish, on every
        // item twice per poll, changed or not (perf, #469).
        if (!dirty_)
            return;
        dirty_ = false;
        // Children with an empty rect (size 0 = not drawn) and hidden ones do not count. Hidden = the child's own flag:
        // isVisible() is false for every child of a hidden Group, e.g. the Input of a ShaderEffect (#486).
        QRectF e;
        bool any = false;
        for (QQuickItem* c : childItems()) {
            if (!QQuickItemPrivate::get(c)->explicitVisible || c->width() <= 0 || c->height() <= 0)
                continue;
            const QRectF r = c->mapRectToItem(this, QRectF(0, 0, c->width(), c->height()));
            e = any ? e.united(r) : r;
            any = true;
        }
        const qreal l = any ? std::min<qreal>(0, e.left()) : 0, t = any ? std::min<qreal>(0, e.top()) : 0;
        const qreal r = any ? std::max<qreal>(0, e.right()) : 0, b = any ? std::max<qreal>(0, e.bottom()) : 0;
        // Origin first: the width/height connection of initGeometry re-places with the new frame.
        bool moved = false;
        if (xf) {
            const float ox = autoSize_ ? float(l) : 0.f, oy = autoSize_ ? float(t) : 0.f;
            moved = xf->ox != ox || xf->oy != oy;
            xf->ox = ox;
            xf->oy = oy;
        }
        setImplicitSize(r - l, b - t);
        if (moved) {
            syncEffectFrame();
            if (placed)
                placed();
            markGeometry(*xf); // the frame moved inside the item: no x/y/size signal for that (#20)
        }
    }

    bool contains(const QPointF& point) const override
    {
        if (containmentMask() || !xf)
            return QQuickItem::contains(point);
        return itemFrame(*xf, this).contains(point);
    }

    QRectF clipRect() const override { return xf ? itemFrame(*xf, this) : QQuickItem::clipRect(); }

private:
    bool autoSize_ = true;
    bool dirty_ = true;
};

QQuickItem* makeGroup() { return new GroupItem(); }

void setGroupAutoSize(QQuickItem* item, bool on)
{
    if (auto* g = dynamic_cast<GroupItem*>(item))
        g->setAutoSize(on);
}

// A child's matrix changed: its parent Group recomputes the frame (no-op for other parents).
void pokeGroup(const QQuickItem* item)
{
    // typeid instead of dynamic_cast: hot path (every TRANSFORM, 10000 per apply in the perf test), GroupItem is final.
    QQuickItem* p = item->parentItem();
    if (p && typeid(*p) == typeid(GroupItem))
        static_cast<GroupItem*>(p)->invalidate();
}

bool setEffectSourceRef(Elements& els, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    QQuickItem* src = qobject_cast<QQuickItem*>(v.ref);
    if (v.ref && !src) {
        err = "source is not an item";
        return false;
    }
    // The effect cannot render itself or one of its ancestors (the layer would contain the effect).
    for (QQuickItem* a = n.item; src && a; a = a->parentItem())
        if (a == src) {
            err = "source must not be the effect itself or one of its ancestors";
            return false;
        }
    return els.setEffectSource(n, rp, src, err);
}

void setLayerEnabled(QQuickItem* item, bool on)
{
    // Once per source change, not per frame: QQmlProperty resolves the grouped "layer.enabled".
    QQmlProperty(item, QStringLiteral("layer.enabled")).write(on);
}

// ---------------------------------------------------------------------------------------------
// Video (QtMultimedia, FFmpeg backend): MediaPlayer + AudioOutput + VideoOutput in a wrapper Item, like the
// MultiEffect wrapper. Deliberate exception from "vvvv drives the clock": the player runs on its own clock, no
// seek, no sync. playing true -> play(), false -> pause() (shows the current frame; after loading the first one).
// Loop off during playback (#125): the running pass ends, then playback stops. The FFmpeg backend has already read
// ahead into the next passes; a seek to the current position drops them, then loops = 1 applies.
// End of stream (#136): keepLastFrame -> VideoOutput.endOfStreamPolicy KeepLastFrame, else ClearOutput (Qt default).
// A source change (incl. empty) always clears the output, otherwise KeepLastFrame would keep the old file's frame.
// A source change stops Qt's player, so the wrapper re-applies playing right away (play() during loading starts once
// loaded; not on LoadedMedia: the FFmpeg backend reports it again after the end and would restart). Loading
// is asynchronous: implicit size (natural video size), position and duration are 0 until then. Player errors go
// to vlqtError (shown + logged at the Video node, #273) until the next source change. The player and its
// decoder threads are children of the item and go with DESTROY.

constexpr const char* kVideoQml =
    "import QtQuick\n"
    "import QtMultimedia\n"
    "Item {\n"
    "  id: root\n"
    "  implicitWidth: out.sourceRect.width\n"
    "  implicitHeight: out.sourceRect.height\n"
    "  property alias source: player.source\n"
    "  property bool playing: false\n"
    "  property bool loop: false\n"
    "  property bool keepLastFrame: false\n"
    "  property alias playbackRate: player.playbackRate\n"
    "  property alias volume: audio.volume\n"
    "  property alias fillMode: out.fillMode\n"
    "  readonly property alias vlqtPosition: player.position\n"
    "  readonly property alias vlqtDuration: player.duration\n"
    "  property string vlqtError: \"\"\n"
    VLQT_QML_PATH_FN
    "  function vlqtSync() { if (playing) player.play(); else player.pause(); }\n"
    "  function vlqtRelease() { playing = false; player.stop(); player.source = \"\"; }\n"
    "  onPlayingChanged: vlqtSync()\n"
    "  onLoopChanged: {\n"
    "    player.loops = loop ? MediaPlayer.Infinite : 1;\n"
    "    if (!loop && player.playbackState === MediaPlayer.PlayingState) player.position = player.position;\n"
    "  }\n"
    "  MediaPlayer {\n"
    "    id: player\n"
    "    loops: 1\n"
    "    audioOutput: AudioOutput { id: audio }\n"
    "    videoOutput: out\n"
    "    onSourceChanged: { root.vlqtError = \"\"; out.clearOutput(); root.vlqtSync(); }\n"
    "    onErrorOccurred: (error, errorString) => root.vlqtError = errorString + \" (\" + root.vlqtPath(source) + \")\"\n"
    "  }\n"
    "  VideoOutput {\n"
    "    id: out\n"
    "    anchors.fill: parent\n"
    "    endOfStreamPolicy: root.keepLastFrame ? VideoOutput.KeepLastFrame : VideoOutput.ClearOutput\n"
    "  }\n"
    "}\n";

/* f32 that must be finite and > 0 (playback rate; reverse/zero playback is not supported by the backend). */
bool setQmlPositiveReal(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (!(v.f[0] > 0) || !std::isfinite(v.f[0])) {
        err = "value must be finite and > 0";
        return false;
    }
    return writeQml(n.item, rp, qreal(v.f[0]), err);
}

/* Linear volume 0..1 (Qt AudioOutput); outside -> ApplyBadValue. */
bool setVolume(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (!(v.f[0] >= 0 && v.f[0] <= 1)) {
        err = "volume must be 0..1";
        return false;
    }
    return writeQml(n.item, rp, qreal(v.f[0]), err);
}

std::vector<PropDef> videoProps()
{
    return {
        {VLQT_PROP_SOURCE, VLQT_VT_STRING, setMediaSource, "source"},
        // VideoOutput.Stretch/PreserveAspectFit/PreserveAspectCrop (= C# VideoFillMode)
        {VLQT_PROP_FILL_MODE, VLQT_VT_I32, setQmlEnum<0, 1, 2>, "fillMode"},
        {VLQT_PROP_PLAYING, VLQT_VT_BOOL, setQmlBool, "playing"},
        {VLQT_PROP_LOOP, VLQT_VT_BOOL, setQmlBool, "loop"},
        // VideoOutput.endOfStreamPolicy (Qt 6.9+): KeepLastFrame / ClearOutput (Qt default)
        {VLQT_PROP_KEEP_LAST_FRAME, VLQT_VT_BOOL, setQmlBool, "keepLastFrame"},
        {VLQT_PROP_PLAYBACK_RATE, VLQT_VT_F32, setQmlPositiveReal, "playbackRate"},
        {VLQT_PROP_VOLUME, VLQT_VT_F32, setVolume, "volume"},
    };
}

// ---------------------------------------------------------------------------------------------
// Button (QtQuick.Controls, first Controls element, #213). Base style VLQt (style/VLQt/Button.qml, #292), values from
// SET_STYLE (vlstyle.h); style picked once at startup (vlqt.cpp). Clicks are
// Qt's clicked signal (release over the button; autoRepeat repeats it while held; disabled = none), counted in
// vlqtClicks and sent as EV_CLICK by the poll for every bound Button (input handlers report their own clicks)
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
        {VLQT_PROP_TEXT, VLQT_VT_STRING, setQmlString, "text"},
        {VLQT_PROP_ENABLED, VLQT_VT_BOOL, setQmlBool, "enabled"},
        {VLQT_PROP_AUTO_REPEAT, VLQT_VT_BOOL, setQmlBool, "autoRepeat"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

// ---------------------------------------------------------------------------------------------
// Value inputs (SpinBox #210; CheckBox, Switch, Slider, TextField #301). Generic user-value channel: the QML
// snippet declares
//   property int vlqtUserCount   user changes since the last poll (reset to 0 by Elements::takeUserValue)
//   property <T> vlqtUserValue   value of the last user change, T = int | real | bool | string (= EV_VALUE value type)
// and sets both in the handler of the control's user-only signal (SpinBox valueModified, CheckBox/Switch toggled,
// Slider moved, TextField editingFinished with changed text). SET VALUE from C# never fires these signals (no echo), and the value is
// captured when the signal fires: a later SET VALUE with a stale value cannot hide it from the poll. The poll sends
// EV_VALUE for every bound item of such a type (events.cpp collectMeasurements), independent of input handlers.
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
        {VLQT_PROP_VALUE, VLQT_VT_I32, setQmlInt, "value"},
        {VLQT_PROP_MINIMUM, VLQT_VT_I32, setQmlInt, "from"},
        {VLQT_PROP_MAXIMUM, VLQT_VT_I32, setQmlInt, "to"},
        {VLQT_PROP_STEP, VLQT_VT_I32, setQmlInt, "stepSize"},
        {VLQT_PROP_EDITABLE, VLQT_VT_BOOL, setQmlBool, "editable"},
        {VLQT_PROP_ENABLED, VLQT_VT_BOOL, setQmlBool, "enabled"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

// Toggle (#301): one vvvv node, two looks = two types, CheckBox and Switch. No text (labels are separate Text
// elements): spacing 0, so the implicit size is the indicator + style padding (style VLQt: INDICATOR_SIZE + padding, Switch lane 2 x size) and the
// indicator is centred in a larger SIZE. toggled = user only (click, Space); SET VALUE (checked) never fires it.
// Tristate and dragging the Switch handle are not exposed; a drag that flips the Switch also sends toggled.

constexpr const char* kCheckBoxQml =
    "import QtQuick\n"
    "import QtQuick.Controls\n"
    "CheckBox {\n"
    "  spacing: 0\n"
    "  property int vlqtUserCount: 0\n"
    "  property bool vlqtUserValue: false\n"
    "  onToggled: { vlqtUserValue = checked; ++vlqtUserCount }\n"
    "}\n";

constexpr const char* kSwitchQml =
    "import QtQuick\n"
    "import QtQuick.Controls\n"
    "Switch {\n"
    "  spacing: 0\n"
    "  property int vlqtUserCount: 0\n"
    "  property bool vlqtUserValue: false\n"
    "  onToggled: { vlqtUserValue = checked; ++vlqtUserCount }\n"
    "}\n";

std::vector<PropDef> toggleProps()
{
    return {
        {VLQT_PROP_VALUE, VLQT_VT_BOOL, setQmlBool, "checked"},
        {VLQT_PROP_ENABLED, VLQT_VT_BOOL, setQmlBool, "enabled"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

// Slider (#301): f32 value, Qt defaults 0..1, step 0 (continuous). moved = user only (press/drag, arrow keys, wheel;
// live, so every drag step), SET VALUE/MINIMUM/MAXIMUM never fire it (also not Qt's clamp). Several moves between two
// polls give one EV_VALUE with the last value. snapMode SnapAlways: with STEP > 0 a drag lands on from + k * step like
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
        {VLQT_PROP_VALUE, VLQT_VT_F32, setQmlReal, "value"},
        {VLQT_PROP_MINIMUM, VLQT_VT_F32, setQmlReal, "from"},
        {VLQT_PROP_MAXIMUM, VLQT_VT_F32, setQmlReal, "to"},
        {VLQT_PROP_STEP, VLQT_VT_F32, setQmlReal, "stepSize"},
        {VLQT_PROP_ORIENTATION, VLQT_VT_I32, setQmlEnum<Qt::Horizontal, Qt::Vertical>, "orientation"},
        {VLQT_PROP_ENABLED, VLQT_VT_BOOL, setQmlBool, "enabled"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

// TextField (#301): single line, string value. Commit on Enter or focus loss (editingFinished), but only if the text
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
bool setTextFieldValue(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    const QString s = toQString(v);
    if (!writeQml(n.item, rp, s, err))
        return false;
    n.item->setProperty("vlqtCommitted", s);
    return true;
}

bool setQmlBoolNot(Elements&, Node& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, !v.b, err);
}

std::vector<PropDef> textFieldProps()
{
    return {
        {VLQT_PROP_VALUE, VLQT_VT_STRING, setTextFieldValue, "text"},
        {VLQT_PROP_PLACEHOLDER, VLQT_VT_STRING, setQmlString, "placeholderText"},
        {VLQT_PROP_EDITABLE, VLQT_VT_BOOL, setQmlBoolNot, "readOnly"},
        {VLQT_PROP_ENABLED, VLQT_VT_BOOL, setQmlBool, "enabled"},
        {VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr},
    };
}

// ---------------------------------------------------------------------------------------------
// Type table. Plan 05b-f add types and properties here (no new exports).

const std::unordered_map<quint16, TypeDef>& typeTable()
{
    static const std::unordered_map<quint16, TypeDef> t = {
        // Group (C#) = GroupItem: implicit size/frame from the children incl. their transform (#469).
        {VLQT_TYPE_ITEM, {VLQT_TYPE_ITEM, "ITEM", nullptr, {{VLQT_PROP_AUTO_SIZE, VLQT_VT_BOOL, setAutoSize, nullptr}}, makeGroup}},
        {VLQT_TYPE_RECTANGLE, {VLQT_TYPE_RECTANGLE, "RECTANGLE", kRectangleQml, rectangleProps()}},
        {VLQT_TYPE_TEXT, {VLQT_TYPE_TEXT, "TEXT", kTextQml, textProps()}},
        {VLQT_TYPE_IMAGE, {VLQT_TYPE_IMAGE, "IMAGE", nullptr, imageProps(), makeImage}},
        {VLQT_TYPE_ROW, {VLQT_TYPE_ROW, "ROW", kRowQml, rowProps()}},
        {VLQT_TYPE_COLUMN, {VLQT_TYPE_COLUMN, "COLUMN", kColumnQml, columnProps()}},
        {VLQT_TYPE_VIDEO, {VLQT_TYPE_VIDEO, "VIDEO", kVideoQml, videoProps()}},
        {VLQT_TYPE_BUTTON, {VLQT_TYPE_BUTTON, "BUTTON", kButtonQml, buttonProps()}},
        {VLQT_TYPE_SPIN_BOX, {VLQT_TYPE_SPIN_BOX, "SPIN_BOX", kSpinBoxQml, spinBoxProps()}},
        {VLQT_TYPE_CHECK_BOX, {VLQT_TYPE_CHECK_BOX, "CHECK_BOX", kCheckBoxQml, toggleProps()}},
        {VLQT_TYPE_SWITCH, {VLQT_TYPE_SWITCH, "SWITCH", kSwitchQml, toggleProps()}},
        {VLQT_TYPE_SLIDER, {VLQT_TYPE_SLIDER, "SLIDER", kSliderQml, sliderProps()}},
        {VLQT_TYPE_TEXT_FIELD, {VLQT_TYPE_TEXT_FIELD, "TEXT_FIELD", kTextFieldQml, textFieldProps()}},
        {VLQT_TYPE_SHADER_EFFECT, {VLQT_TYPE_SHADER_EFFECT, "SHADER_EFFECT", nullptr, shaderEffectProps(), makeShaderEffect}},
        {VLQT_TYPE_QML, {VLQT_TYPE_QML, "QML", nullptr, qmlProps(), makeQml}},
    };
    return t;
}

const PropDef* findProp(const TypeDef& type, quint16 prop)
{
    if (isEffectProp(prop))
        return nullptr; // only via SET_EFFECT_CHAIN (#489)
    const auto& list = prop >= 256 ? type.props : commonProps();
    for (const PropDef& p : list)
        if (p.id == prop)
            return &p;
    return nullptr;
}

} // namespace

bool isGroup(const QQuickItem* item)
{
    return item && typeid(*item) == typeid(GroupItem);
}

QQuickItem* groupTextureSource(const QQuickItem* item)
{
    return isGroup(item) ? static_cast<const GroupItem*>(item)->proxy.data() : nullptr;
}

const TypeDef* findType(quint16 type)
{
    const auto& t = typeTable();
    auto it = t.find(type);
    return it != t.end() ? &it->second : nullptr;
}

bool writeQml(QQuickItem* item, const ResolvedProp& rp, const QVariant& v, std::string& err)
{
    QObject* target = item;
    const QMetaProperty* p = &rp.prop;
    if (rp.sub.isValid()) {
        target = rp.prop.read(item).value<QObject*>();
        p = &rp.sub;
    }
    if (!target || !p->write(target, v)) {
        err = std::string("writing QML property '") + rp.def->qmlName + "' failed";
        return false;
    }
    return true;
}

// ---------------------------------------------------------------------------------------------

Elements::Elements(QQmlEngine* e) : engine(e) {}

Elements::~Elements()
{
    // Items are deleted by the Scene before; components only hold compiled QML.
    for (auto& kv : types)
        delete kv.second.component;
    delete matrixComponent;
}

QQuickItem* Elements::create(const TypeDef& type, std::string& err)
{
    TypeCache& cache = types[type.id];
    QQuickItem* item = nullptr;
    if (type.make) {
        item = type.make();
    } else if (!type.qml) {
        item = new QQuickItem();
    } else {
        if (!cache.component) {
            cache.component = new QQmlComponent(engine);
            cache.component->setData(QByteArray(type.qml), QUrl(QStringLiteral("vlqt_type_%1.qml").arg(type.id)));
        }
        QQmlComponent* comp = cache.component;
        QObject* obj = comp->isError() ? nullptr : comp->create();
        item = qobject_cast<QQuickItem*>(obj);
        if (!item) {
            delete obj;
            err = std::string("type ") + type.name + " could not be created: " + comp->errorString().toStdString();
            return nullptr;
        }
        QQmlEngine::setObjectOwnership(item, QQmlEngine::CppOwnership);
    }
    if (!cache.resolved)
        resolve(type, cache, item);
    if (type.id == VLQT_TYPE_VIDEO && !mediaPosition.isValid()) {
        const QMetaObject* mo = item->metaObject();
        const int ip = mo->indexOfProperty("vlqtPosition"), idur = mo->indexOfProperty("vlqtDuration");
        if (ip >= 0 && idur >= 0) {
            mediaPosition = mo->property(ip);
            mediaDuration = mo->property(idur);
        }
    }
    return item;
}

bool Elements::readMedia(const Node& node, float out[2]) const
{
    if (node.type != VLQT_TYPE_VIDEO || !mediaPosition.isValid())
        return false;
    // MediaPlayer reports milliseconds (qint64); 0 until the media is loaded.
    out[0] = float(mediaPosition.read(node.item).toLongLong() / 1000.0);
    out[1] = float(mediaDuration.read(node.item).toLongLong() / 1000.0);
    return true;
}

QString Elements::readError(const Node& node) const
{
    const auto it = types.find(node.type);
    QString own;
    if (it != types.end() && it->second.error.isValid())
        own = it->second.error.read(node.item).toString(); // implicitly shared, no copy of the text
    // Content problems of the effect chain (#489, "link N: ..."); joined only while both have text.
    if (!node.effectChain || node.effectChain->error().isEmpty())
        return own;
    return own.isEmpty() ? node.effectChain->error() : own + u'\n' + node.effectChain->error();
}

quint32 Elements::takeClicks(const Node& node)
{
    const auto it = types.find(node.type);
    if (it == types.end() || !it->second.clicks.isValid())
        return 0;
    const int c = it->second.clicks.read(node.item).toInt();
    if (c > 0)
        it->second.clicks.write(node.item, 0);
    return c > 0 ? quint32(c) : 0;
}

bool Elements::pollsState(quint16 type) const
{
    if (type == VLQT_TYPE_VIDEO)
        return true;
    const auto it = types.find(type);
    if (it == types.end())
        return false;
    const TypeCache& c = it->second;
    return c.error.isValid() || c.clicks.isValid() || (c.userCount.isValid() && c.userValue.isValid());
}

bool Elements::takeUserValue(const Node& node, QVariant& out)
{
    const auto it = types.find(node.type);
    if (it == types.end() || !it->second.userCount.isValid() || !it->second.userValue.isValid())
        return false;
    const TypeCache& c = it->second;
    if (c.userCount.read(node.item).toInt() <= 0)
        return false;
    c.userCount.write(node.item, 0);
    out = c.userValue.read(node.item);
    return true;
}

void Elements::resolve(const TypeDef& type, TypeCache& cache, QQuickItem* first)
{
    // All instances of a QML component share one meta object: resolve each table property once.
    cache.resolved = true;
    const QMetaObject* mo = first->metaObject();
    if (const int ie = mo->indexOfProperty("vlqtError"); ie >= 0)
        cache.error = mo->property(ie);
    if (const int ic = mo->indexOfProperty("vlqtClicks"); ic >= 0)
        cache.clicks = mo->property(ic);
    if (const int iu = mo->indexOfProperty("vlqtUserCount"); iu >= 0)
        cache.userCount = mo->property(iu);
    if (const int iv = mo->indexOfProperty("vlqtUserValue"); iv >= 0)
        cache.userValue = mo->property(iv);
    for (const PropDef& p : type.props) {
        if (!p.qmlName)
            continue;
        ResolvedProp rp;
        rp.def = &p;
        const char* dot = std::strchr(p.qmlName, '.');
        const QByteArray head = dot ? QByteArray(p.qmlName, int(dot - p.qmlName)) : QByteArray(p.qmlName);
        const int idx = mo->indexOfProperty(head.constData());
        if (idx >= 0)
            rp.prop = mo->property(idx);
        if (rp.prop.isValid() && dot) {
            QObject* group = rp.prop.read(first).value<QObject*>();
            const int sidx = group ? group->metaObject()->indexOfProperty(dot + 1) : -1;
            if (sidx >= 0)
                rp.sub = group->metaObject()->property(sidx);
            else
                rp.prop = QMetaProperty();
        }
        if (rp.prop.isValid())
            cache.props[p.id] = rp;
        else
            qCWarning(lcElements, "type %s: QML property '%s' (id %d) not found, SET will fail", type.name, p.qmlName, p.id);
    }
}

int Elements::set(Node& node, quint16 prop, const Value& v, std::string& err)
{
    const TypeDef* type = findType(node.type);
    const PropDef* def = type ? findProp(*type, prop) : nullptr;
    TypeCache& cache = types[node.type];
    const ResolvedProp* rp = nullptr;
    ResolvedProp api; // properties without a QML name (C++ API): only def is set
    if (def && def->qmlName) {
        auto it = cache.props.find(prop);
        if (it != cache.props.end())
            rp = &it->second;
    } else if (def) {
        api.def = def;
        rp = &api;
    }
    if (!rp) {
        err = "property " + std::to_string(prop) + " not supported by type " + (type ? type->name : "?")
            + (isEffectProp(prop) ? " (EFFECT_* only via SET_EFFECT_CHAIN)" : "");
        if (cache.warned.insert(prop).second)
            qCWarning(lcElements, "%s", err.c_str());
        return ApplyUnknownProperty;
    }
    if (def->valueType != v.type) {
        err = "property " + std::to_string(prop) + " expects value type " + std::to_string(def->valueType)
            + ", got " + std::to_string(v.type);
        return ApplyBadValue;
    }
    std::string reason;
    if (!def->set(*this, node, *rp, v, reason)) {
        err = "property " + std::to_string(prop) + ": " + reason;
        return ApplyBadValue;
    }
    return ApplyOk;
}

QObject* Elements::createTransform(std::string& err)
{
    if (!matrixComponent) {
        matrixComponent = new QQmlComponent(engine);
        matrixComponent->setData(QByteArrayLiteral("import QtQuick\nMatrix4x4 {}\n"), QUrl(QStringLiteral("vlqt_matrix4x4.qml")));
    }
    QObject* obj = matrixComponent->isError() ? nullptr : matrixComponent->create();
    if (!obj) {
        err = "Matrix4x4 could not be created: " + matrixComponent->errorString().toStdString();
        return nullptr;
    }
    if (!qobject_cast<QQuickMatrix4x4*>(obj)) {
        delete obj;
        err = "Matrix4x4 is not a QQuickMatrix4x4";
        return nullptr;
    }
    QQmlEngine::setObjectOwnership(obj, QQmlEngine::CppOwnership);
    return obj;
}

namespace {
// Qt column vectors: v' = T(d) * T(p) * M * T(-p) * v with p = anchor in the item's local coordinates (actual size)
// and d = drag offset (#209): the offset comes last, i.e. it moves the item in the parent's space. One Matrix4x4 for
// both instead of a Matrix4x4 + Translate pair: the order in item.transform can never be wrong.
// Together with the anchor placement (x/y = -p outside Row/Column, placeItem) Qt maps v -> d + M * (v - p), i.e.
// (v - anchor) * M in row vectors as in protocol.h; in a Row/Column v -> layoutPos + d + p + M * (v - p).
QMatrix4x4 pivoted(const TransformState& s, const QQuickItem* item)
{
    const QPointF a = anchorPoint(s, item);
    const float px = float(a.x()), py = float(a.y());
    QMatrix4x4 r;
    r.translate(s.dx + px, s.dy + py);
    r *= s.base;
    r.translate(-px, -py);
    return r;
}

// A Row/Column (Qt positioner) owns the position of its children: writing x/y there would overwrite the layout, and
// the positioner only lays out again on a size change (#346). The lookup runs once per meta object (hot path: every
// size change of every element, 10000 per apply in the perf test).
bool inPositioner(const QQuickItem* item)
{
    static thread_local const QMetaObject* lastMeta = nullptr;
    static thread_local bool lastPositioned = false;
    const QQuickItem* parent = item->parentItem();
    if (!parent)
        return false;
    const QMetaObject* meta = parent->metaObject();
    if (meta != lastMeta) {
        lastMeta = meta;
        lastPositioned = meta->indexOfSignal("positioningComplete()") >= 0;
    }
    return lastPositioned;
}

// TRANSFORM is a pure x/y translation (Qt column vectors: identity except m(0,3), m(1,3)).
bool isTranslation2D(const QMatrix4x4& m)
{
    return m(0, 0) == 1 && m(0, 1) == 0 && m(0, 2) == 0
        && m(1, 0) == 0 && m(1, 1) == 1 && m(1, 2) == 0
        && m(2, 0) == 0 && m(2, 1) == 0 && m(2, 2) == 1 && m(2, 3) == 0
        && m(3, 0) == 0 && m(3, 1) == 0 && m(3, 2) == 0 && m(3, 3) == 1;
}

// Anchor placement (#469): outside a Row/Column the anchor point (actual size) lies at the parent origin before
// TRANSFORM, i.e. x/y = -anchor. Inside one the layout keeps x/y.
// Fast path (perf, #469): as long as no Matrix4x4 is attached (TRANSFORM was only ever a pure x/y translation, no drag
// offset, never in a Row/Column) the translation goes into x/y as well: x/y = t - anchor is the same mapping as
// T(t) * T(-anchor), without a QML transform object, its QVariant writes and the general matrix in every
// mapToScene/sync. Elements::setMatrix decides when to attach the Matrix4x4 (from then on it carries TRANSFORM).
void placeItem(const TransformState& s, QQuickItem* item)
{
    if (inPositioner(item))
        return;
    QPointF p = -anchorPoint(s, item);
    if (!s.matrix)
        p += QPointF(s.base(0, 3), s.base(1, 3));
    item->setPosition(p);
}

// Matrix4x4 objects only come from Elements::createTransform (checked with qobject_cast there). setMatrix compares
// first and emits matrixChanged only on a real change; no QVariant/QMetaProperty detour (perf, #560).
void setMatrixValue(QObject* t, const QMatrix4x4& m)
{
    static_cast<QQuickMatrix4x4*>(t)->setMatrix(m);
}

void writeMatrix(const TransformState& s, QQuickItem* item)
{
    if (s.matrix)
        setMatrixValue(s.matrix, pivoted(s, item));
}
} // namespace

bool takesAutoSize(const Node& n)
{
    return n.type == VLQT_TYPE_ITEM || n.type == VLQT_TYPE_ROW || n.type == VLQT_TYPE_COLUMN
        || n.type == VLQT_TYPE_BUTTON || n.type == VLQT_TYPE_SPIN_BOX
        || n.type == VLQT_TYPE_CHECK_BOX || n.type == VLQT_TYPE_SWITCH || n.type == VLQT_TYPE_SLIDER
        || n.type == VLQT_TYPE_TEXT_FIELD || n.type == VLQT_TYPE_QML
        || (n.type == VLQT_TYPE_SHADER_EFFECT && n.effectSource); // implicit size = Input; without Input the SIZE
}

QRectF itemFrame(const TransformState& s, const QQuickItem* item)
{
    const qreal w = item->width(), h = item->height();
    if (s.sizedFrame)
        return QRectF(-s.fx * w, -s.fy * h, w, h);
    return QRectF(s.ox, s.oy, w, h);
}

QPointF anchorPoint(const TransformState& s, const QQuickItem* item)
{
    // Group with Auto Size off (#475): the frame is placed around the origin, the item and its children stay.
    if (s.sizedFrame)
        return QPointF(0, 0);
    // Left/Top (0) is always the origin: an auto-sized Group stays a pure transform container (its origin = that of
    // its children) even when they reach into negative coordinates; Center/Right/Bottom resolve against the frame.
    const qreal x = s.fx == 0 ? 0 : s.ox + s.fx * item->width();
    const qreal y = s.fy == 0 ? 0 : s.oy + s.fy * item->height();
    return QPointF(x, y);
}

void Elements::initGeometry(Node& node)
{
    node.xf = std::make_shared<TransformState>();
    // Default ANCHOR AlignLeft|AlignTop for all elements (fx = fy = 0, TransformState default; #471).
    auto* group = dynamic_cast<GroupItem*>(node.item);
    placeItem(*node.xf, node.item);
    // Placement and the anchor of TRANSFORM follow every size change (Auto Size, layout, text, children extent), not
    // only the SIZE sent by C#. Connected at creation, i.e. before the mask frame's connections (createMaskFrame), so
    // the frame always sees the final x/y and matrix. Context = the item: the connection ends with it, xf is shared.
    auto update = [xf = node.xf, item = node.item]() {
        placeItem(*xf, item);
        writeMatrix(*xf, item);
        markGeometry(*xf); // size and frame changed (#20)
    };
    QObject::connect(node.item, &QQuickItem::widthChanged, node.item, update);
    QObject::connect(node.item, &QQuickItem::heightChanged, node.item, update);
    if (group) {
        group->xf = node.xf;
        group->placed = update; // a mask frame follows through x/y (the anchor moves with the frame origin)
        // Auto Size off: the frame origin depends on the size (-fx * w), the effect padding follows (#475).
        QObject::connect(group, &QQuickItem::widthChanged, group, [group] { group->syncEffectFrame(); });
        QObject::connect(group, &QQuickItem::heightChanged, group, [group] { group->syncEffectFrame(); });
    }
}

bool Elements::placeAfterReparent(Node& node, std::string& err)
{
    if (!node.xf)
        return true;
    // Into a Row/Column with a translation still folded into x/y (placeItem): the layout owns x/y now, the
    // translation moves to the Matrix4x4.
    if (!node.transform && !node.xf->base.isIdentity() && inPositioner(node.item))
        return writeTransform(node, err);
    placeItem(*node.xf, node.item);
    return true;
}

bool Elements::setMatrix(Node& node, const QMatrix4x4& m, std::string& err)
{
    TransformState& s = *node.xf;
    if (!node.transform && isTranslation2D(m) && s.dx == 0 && s.dy == 0 && (!inPositioner(node.item) || m.isIdentity())) {
        // Fast path (placeItem): x/y only. The x/y change itself pokes a parent Group and a mask frame.
        s.base = m;
        placeItem(s, node.item);
        return true;
    }
    s.base = m;
    return writeTransform(node, err);
}

bool Elements::setDragOffset(Node& node, float dx, float dy, std::string& err)
{
    if (node.xf->dx == dx && node.xf->dy == dy)
        return true; // unchanged: no Matrix4x4 for items that were never transformed or dragged
    node.xf->dx = dx;
    node.xf->dy = dy;
    return writeTransform(node, err);
}

bool Elements::writeTransform(Node& node, std::string& err)
{
    if (!node.transform) {
        QObject* t = createTransform(err);
        if (!t)
            return false;
        t->setParent(node.item); // deleted together with the item
        QQmlListReference list(node.item, "transform");
        if (!list.isValid() || !list.append(t)) {
            delete t;
            err = "appending Matrix4x4 to 'transform' failed";
            return false;
        }
        node.transform = t;
        node.xf->matrix = t; // from now on written by the size connection of initGeometry too
        placeItem(*node.xf, node.item); // the translation leaves x/y (fast path) and goes into the matrix below
    }
    setMatrixValue(node.transform, pivoted(*node.xf, node.item));
    updateMaskFrame(node);
    pokeGroup(node.item);
    markGeometry(*node.xf); // Qt has no public signal for a transform change (#20)
    return true;
}

bool Elements::setAnchor(Node& node, float fx, float fy, std::string& err)
{
    if (node.xf->fx == fx && node.xf->fy == fy)
        return true;
    node.xf->fx = fx;
    node.xf->fy = fy;
    placeItem(*node.xf, node.item);
    if (auto* group = dynamic_cast<GroupItem*>(node.item))
        group->syncEffectFrame(); // Auto Size off: the frame moves with the anchor (#475)
    if (node.transform)
        setMatrixValue(node.transform, pivoted(*node.xf, node.item));
    updateMaskFrame(node);
    pokeGroup(node.item);
    markGeometry(*node.xf); // matrix pivot or Group frame (Auto Size off) moved, maybe without an x/y change (#20)
    return true;
}
bool Elements::setEffectSource(Node& effect, const ResolvedProp& rp, QQuickItem* source, std::string& err)
{
    QQuickItem* old = effect.effectSource;
    if (old == source)
        return true;
    // A Group goes through its frame proxy (#486): the layer would cover only (0, 0, width, height), not the part of the
    // frame left of / above the origin. Its own layer (Effect pin) stays untouched.
    QQuickItem* input = isGroup(source) ? acquireGroupProxy(source) : source;
    if (!writeQml(effect.item, rp, QVariant::fromValue(input), err)) {
        if (input != source)
            releaseGroupProxy(source);
        return false;
    }
    effect.effectSource = source;
    if (old) {
        if (isGroup(old))
            releaseGroupProxy(old); // the proxy goes after the next frame (#123)
        auto it = sourceRefs.find(old);
        if (it != sourceRefs.end() && --it->second <= 0) {
            sourceRefs.erase(it);
            // Free the layer texture, but only after the next frame (#123): disabling the layer deletes its texture
            // provider right away, while the effect still renders from it until it polishes onto the new source
            // (use after free, crash 0xC0000409 when the input changes every frame). Visibility stays with the host.
            if (!isGroup(old))
                layerOff.emplace_back(old);
        }
    }
    if (source && ++sourceRefs[source] == 1) {
        if (!isGroup(source))
            setLayerEnabled(source, true);
        source->setVisible(false); // the effect draws it
    }
    return true;
}

int Elements::setEffectChain(Node& node, const EffectChainDesc& chain, const std::vector<QQuickItem*>& masks, std::string& err)
{
    if (chain.links.empty()) {
        releaseEffectLayer(node);
        return ApplyOk;
    }
    if (!effectComponents)
        effectComponents = std::make_unique<EffectComponents>(engine);
    EffectChain* ec = node.effectChain;
    if (!ec) {
        ec = node.effectChain = new EffectChain(*effectComponents, node.item); // dies with the item at the latest
        if (auto* group = dynamic_cast<GroupItem*>(node.item))
            ec->built = [group](QQuickItem* first) { // the chain is a child of the group: never outlives it
                group->effectHolder = first;
                group->syncEffectFrame();
            };
    }
    std::vector<QPointer<QQuickItem>> retired;
    std::vector<EffectChain::DroppedMask> dropped;
    const bool ok = ec->apply(chain, masks, retired, dropped, err);
    for (const QPointer<QQuickItem>& stage : retired)
        frameDelete.emplace_back(stage); // may still be sampled this frame (#123)
    for (const EffectChain::DroppedMask& d : dropped)
        retireMaskFrame(node, d.mask, d.frame);
    if (!ok)
        return ApplyBadValue;
    for (size_t i = 0; i < ec->links.size(); ++i) {
        EffectChain::Link& l = ec->links[i];
        if (!l.mask || l.maskFrame)
            continue;
        l.maskFrame = createMaskFrame(node, l.mask, l.maskFrameXf, err);
        if (!l.maskFrame) {
            l.mask = nullptr;
            return ApplyBadValue;
        }
        ec->syncMaskFrame(i);
    }
    if (!effectLayers.count(node.item)) {
        QQmlComponent* root = ec->rootComponent(err);
        // Component first, then enabled: activating the layer creates the root once, the root builds the stages.
        if (!root || !QQmlProperty(node.item, QStringLiteral("layer.effect")).write(QVariant::fromValue(root))) {
            if (err.empty())
                err = "setting layer.effect failed";
            return ApplyBadValue;
        }
        setLayerEnabled(node.item, true);
        effectLayers.insert(node.item);
    }
    updateMaskFrame(node);
    return ApplyOk;
}

void Elements::releaseEffectLayer(Node& node)
{
    EffectChain* ec = node.effectChain;
    if (!ec)
        return;
    for (QQuickItem* mask : effectMasks(node))
        detachEffectMask(node, mask);
    effectLayers.erase(node.item);
    // A SHADER_EFFECT source or an attached mask keeps its layer (an effect samples it), only the layer effect goes;
    // a Group source is sampled through its frame proxy (#486), not its layer. The root and its stages go with it.
    if ((!isEffectSource(node.item) || isGroup(node.item)) && !isMaskLayer(node.item))
        setLayerEnabled(node.item, false);
    QQmlProperty(node.item, QStringLiteral("layer.effect")).write(QVariant::fromValue<QQmlComponent*>(nullptr));
    if (auto* group = dynamic_cast<GroupItem*>(node.item))
        group->effectHolder = nullptr;
    node.effectChain = nullptr;
    delete ec;
}

void Elements::detachEffectMask(Node& node, QQuickItem* mask)
{
    if (!node.effectChain || !mask)
        return;
    std::vector<EffectChain::Link>& links = node.effectChain->links;
    for (size_t i = 0; i < links.size(); ++i) {
        EffectChain::Link& l = links[i];
        if (l.mask != mask)
            continue;
        QQuickItem* frame = l.maskFrame;
        l.mask = nullptr;
        l.maskFrame = nullptr;
        l.maskFrameXf = nullptr;
        node.effectChain->syncMaskFrame(i); // detach before the frame goes: MultiEffect keeps a raw maskSource pointer
        retireMaskFrame(node, mask, frame);
    }
}

std::vector<QQuickItem*> Elements::effectMasks(const Node& node) const
{
    std::vector<QQuickItem*> masks;
    if (node.effectChain)
        for (const EffectChain::Link& l : node.effectChain->links)
            if (l.mask)
                masks.push_back(l.mask);
    return masks;
}

QQuickItem* Elements::createMaskFrame(Node& node, QQuickItem* mask, QObject*& xfOut, std::string& err)
{
    // Mask rule (protocol.h): the mask lies in the element's parent coordinates like a sibling, with its own SIZE,
    // ANCHOR and TRANSFORM. frame (hidden, layer on, the link's maskSource) > inner (Matrix4x4 = inverse of the
    // element's parent mapping, updateMaskFrame) > mask: frame coordinates are the element's own, so the layer's
    // sourceRect is the link's effect area as it is (stage Binding) and the texture lands exactly where a sibling would be.
    QObject* xf = createTransform(err);
    if (!xf)
        return nullptr;
    auto* frame = new QQuickItem(mask->parentItem()); // next to the parked mask (window root): outside every layout
    frame->setVisible(false);                         // texture source only, the effect draws it as alpha
    frame->setSize(QSizeF(1, 1)); // a 0-sized source item gives no texture, whatever sourceRect says
    auto* inner = new QQuickItem(frame);
    xf->setParent(inner);
    QQmlListReference list(inner, "transform");
    if (!list.isValid() || !list.append(xf)) {
        delete frame;
        err = "appending Matrix4x4 to the mask frame failed";
        return nullptr;
    }
    mask->setParentItem(inner);
    mask->setVisible(true); // inside the hidden frame: rendered into its layer only
    maskLayers.insert(frame);
    // Linear filtering: a rotated/scaled element samples the texture off the pixel grid.
    QQmlProperty(frame, QStringLiteral("layer.smooth")).write(true);
    setLayerEnabled(frame, true);
    // Position (anchor placement or Row/Column layout) and size (anchor) of the element; TRANSFORM/ANCHOR/drag offset call
    // updateMaskFrame themselves. Context = frame: disconnected on detach, gone with it. The Scene detaches the
    // masks before it erases the node, so the pointer never dangles.
    Node* n = &node;
    auto update = [this, n]() { updateMaskFrame(*n); };
    QObject::connect(node.item, &QQuickItem::xChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::yChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::widthChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::heightChanged, frame, update);
    xfOut = xf;
    return frame;
}

void Elements::retireMaskFrame(Node& node, QQuickItem* mask, QQuickItem* frame)
{
    if (!frame)
        return;
    maskLayers.erase(frame);
    QObject::disconnect(node.item, nullptr, frame, nullptr);
    if (mask && frame->isAncestorOf(mask)) {
        mask->setParentItem(frame->parentItem()); // parked next to it again, hidden
        mask->setVisible(false);
    }
    // The effect may sample the frame until it polishes (#123): layer off and delete after the next frame.
    layerOff.emplace_back(frame);
    frameDelete.emplace_back(frame);
}

void Elements::updateMaskFrame(Node& node)
{
    if (!node.effectChain)
        return;
    const auto& links = node.effectChain->links;
    if (std::none_of(links.begin(), links.end(), [](const EffectChain::Link& l) { return l.maskFrameXf; }))
        return;
    // Element -> parent, as Qt maps it: T(x, y) * item.transform (x/y = -anchor or the layout position; our Matrix4x4:
    // TRANSFORM around the anchor + drag offset). Its inverse brings parent coordinates (the mask's) into the element's (the effect area's).
    QMatrix4x4 m;
    m.translate(float(node.item->x()), float(node.item->y()));
    if (node.transform && node.xf)
        m *= pivoted(*node.xf, node.item);
    const QMatrix4x4 inv = m.inverted();
    for (const EffectChain::Link& l : links)
        if (l.maskFrameXf)
            setMatrixValue(l.maskFrameXf, inv);
}

QQuickItem* Elements::effectLayerItem(const Node& node)
{
    return node.effectChain ? node.effectChain->root() : nullptr;
}

void Elements::flushLayerOff()
{
    for (const QPointer<QQuickItem>& item : layerOff)
        if (item && !isEffectSource(item) && !effectLayers.count(item) && !maskLayers.count(item))
            setLayerEnabled(item, false);
    layerOff.clear();
    for (const QPointer<QQuickItem>& frame : frameDelete)
        delete frame.data(); // the mask itself was moved out on detach
    frameDelete.clear();
}

QQuickItem* Elements::acquireGroupProxy(QQuickItem* item)
{
    auto* group = static_cast<GroupItem*>(item);
    ++group->proxyRefs;
    if (group->proxy)
        return group->proxy;
    // A ShaderEffectSource renders its source item (also a hidden one: it refs it, refFromEffectItem) into its own
    // texture with sourceRect = the frame (like the snapshot's frameTarget, #475). Hidden itself, so it never draws on
    // screen; it still gets updatePaintNode and its texture is rendered by the consuming ShaderEffectItem's preprocess
    // without an extra refFromEffectItem on the proxy (checked by grab in the smoke test group-shader-source, also
    // live changes). Under the root, never under the Group (it would be part of its own texture) or an element (layout,
    // REPARENT indices count tracked items only). hideSource off: the host hides the Group already (REPARENT under the
    // ShaderEffect, setEffectSource). Not smooth, like the item layers used before (layer.smooth default false).
    auto* proxy = new QQuickShaderEffectSource();
    proxy->setParent(proxyParent); // deleted with the root at the latest
    proxy->setVisible(false);
    proxy->setLive(true);
    proxy->setHideSource(false);
    proxy->setSmooth(false);
    proxy->setParentItem(proxyParent);
    proxy->setSourceItem(group);
    group->proxy = proxy;
    group->syncEffectFrame();
    // A destroyed Group (DESTROY of a texture element, RESET) retires its proxy: the effects still sample it this frame.
    group->proxyRetire = QObject::connect(group, &QObject::destroyed, proxy, [this, proxy] { frameDelete.emplace_back(proxy); });
    return proxy;
}

void Elements::releaseGroupProxy(QQuickItem* item)
{
    auto* group = static_cast<GroupItem*>(item);
    if (--group->proxyRefs > 0 || !group->proxy)
        return;
    group->proxyRefs = 0;
    QQuickShaderEffectSource* proxy = group->proxy;
    group->proxy = nullptr;
    // Only our own connection (#534): disconnecting every destroyed -> proxy connection would also drop the proxy's
    // sourceItemDestroyed, and ~QQuickShaderEffectSource would then use the deleted Group (window_destroy, or the Input
    // released and the Group destroyed in one apply).
    QObject::disconnect(group->proxyRetire);
    frameDelete.emplace_back(proxy); // the effect may sample it until it syncs onto the new texture (#123)
}

void Elements::syncTextureGroups(Node& effect)
{
    auto* fx = static_cast<ShaderEffectItem*>(effect.item);
    if (!fx->takeRefsChanged())
        return;
    std::vector<QPointer<QQuickItem>> now;
    fx->forEachRef([&](QQuickItem* ref) {
        if (isGroup(ref) && std::find(now.begin(), now.end(), ref) == now.end())
            now.emplace_back(ref);
    });
    // Acquire first: a Group that stays keeps its proxy. A destroyed Group (its uniform only loses the pointer) must
    // re-sync the effect before the proxy goes: Qt deletes the proxy's texture after the next sync, the effect's material
    // would still hold it (use after free in EffectNode::preprocess, seen in the smoke test).
    for (const QPointer<QQuickItem>& g : now)
        if (std::find(effect.textureGroups.begin(), effect.textureGroups.end(), g) == effect.textureGroups.end()) {
            acquireGroupProxy(g);
            QObject::connect(g, &QObject::destroyed, effect.item, &QQuickItem::update, Qt::UniqueConnection);
        }
    for (const QPointer<QQuickItem>& g : effect.textureGroups)
        if (g && std::find(now.begin(), now.end(), g) == now.end()) {
            QObject::disconnect(g, &QObject::destroyed, effect.item, &QQuickItem::update);
            releaseGroupProxy(g);
        }
    effect.textureGroups.swap(now);
}

void Elements::releaseTextureGroups(Node& effect)
{
    for (const QPointer<QQuickItem>& g : effect.textureGroups)
        if (g) { // a deleted Group retired its proxy already
            QObject::disconnect(g, &QObject::destroyed, effect.item, &QQuickItem::update);
            releaseGroupProxy(g);
        }
    effect.textureGroups.clear();
}

void Elements::clearEffectSource(Node& effect)
{
    if (!effect.effectSource)
        return;
    auto it = types[effect.type].props.find(VLQT_PROP_SOURCE);
    std::string err;
    if (it == types[effect.type].props.end() || !setEffectSource(effect, it->second, nullptr, err)) {
        // Should not happen (the source was set through the same property); never keep a dangling pointer.
        qCWarning(lcElements, "clearing effect source failed: %s", err.c_str());
        sourceRefs.erase(effect.effectSource);
        effect.effectSource = nullptr;
    }
}

} // namespace vlqt
