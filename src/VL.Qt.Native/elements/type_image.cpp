#include "elements/element_types.h"
#include "qtimageitem.h"

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Image: QtImageItem draws an image resource (vlqt_res_*, image_resource.h) instead of a file URL, so one
// upload serves every element and window. It takes no file SOURCE (-6); load errors belong to the resource (RESOURCE_EVENT_ERROR).

QQuickItem* makeImage() { return new QtImageItem(); }

bool setImageResource(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    return static_cast<QtImageItem*>(n.item)->setResource(v.u, err);
}

std::vector<PropDef> imageProps()
{
    return {
        {VLQT_PROPERTY_IMAGE, VLQT_VALUE_TYPE_U64, setImageResource, nullptr},
        // Stretch/PreserveAspectFit/PreserveAspectCrop/Tile (= C# ImageFillMode, values of QML Image.fillMode)
        {VLQT_PROPERTY_FILL_MODE, VLQT_VALUE_TYPE_I32, setQmlEnum<0, 1, 2, 3>, "fillMode"},
        {VLQT_PROPERTY_SMOOTH, VLQT_VALUE_TYPE_BOOL, setQmlBool, "smooth"},
    };
}

} // namespace

TypeDef imageType()
{
    return {VLQT_ELEMENT_TYPE_IMAGE, "IMAGE", nullptr, imageProps(), makeImage};
}

} // namespace vlqt
