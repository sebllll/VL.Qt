#include "elements/element_types.h"
#include "resources/qt_image_item.h"

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

/* PROP_FILL_MODE: every vlqt_fill_mode. */
bool setImageFillMode(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.i < VLQT_FILL_MODE_STRETCH || v.i > VLQT_FILL_MODE_TILE) {
        err = "invalid fill mode " + std::to_string(v.i);
        return false;
    }
    static_cast<QtImageItem*>(n.item)->setFillMode(static_cast<vlqt_fill_mode>(v.i));
    return true;
}

std::vector<PropDef> imageProps()
{
    return {
        {VLQT_PROPERTY_IMAGE, VLQT_VALUE_TYPE_U64, setImageResource, nullptr},
        {VLQT_PROPERTY_FILL_MODE, VLQT_VALUE_TYPE_I32, setImageFillMode, nullptr},
        {VLQT_PROPERTY_SMOOTH, VLQT_VALUE_TYPE_BOOL, setQmlBool, "smooth"},
    };
}

} // namespace

TypeDef imageType()
{
    return {VLQT_ELEMENT_TYPE_IMAGE, "IMAGE", nullptr, imageProps(), makeImage};
}

} // namespace vlqt
