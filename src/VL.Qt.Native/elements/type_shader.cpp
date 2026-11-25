#include "elements/element_types.h"
#include "elements/elements.h"
#include "elements/geometry.h"
#include "effects/shader_item.h"

#include <QtQuick/QQuickItem>

namespace vlqt {

namespace {

// ShaderEffect (protocol.h "ShaderEffect"): ShaderItem (effects/shader_item.h), own QQuickItem with a
// QSGMaterialShader. SOURCE goes through the effect source bookkeeping (layer, hidden, cleared before deletion).
QQuickItem* makeShaderEffect() { return new ShaderItem(); }

ShaderItem* shaderItem(ElementEntry& n) { return static_cast<ShaderItem*>(n.item); }

bool setFragmentShader(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    return shaderItem(n)->setShader(VLQT_SHADER_STAGE_FRAGMENT, v.u, err);
}

bool setVertexShader(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    return shaderItem(n)->setShader(VLQT_SHADER_STAGE_VERTEX, v.u, err);
}

bool setBlending(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    shaderItem(n)->setBlending(v.b);
    return true;
}

bool setMeshResolution(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.i < 1 || v.i2 < 1) {
        err = "mesh resolution must be >= 1, 1";
        return false;
    }
    shaderItem(n)->setMeshResolution(v.i, v.i2);
    return true;
}

bool setEffectSourceRef(Elements& els, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
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

bool setShaderSourceRef(Elements& els, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (!setEffectSourceRef(els, n, rp, v, err))
        return false;
    applySize(n); // Auto Size follows the Input only while there is one
    return true;
}

std::vector<PropDef> shaderEffectProps()
{
    return {
        {VLQT_PROPERTY_FRAGMENT_SHADER, VLQT_VALUE_TYPE_U64, setFragmentShader, nullptr},
        {VLQT_PROPERTY_VERTEX_SHADER, VLQT_VALUE_TYPE_U64, setVertexShader, nullptr},
        {VLQT_PROPERTY_BLENDING, VLQT_VALUE_TYPE_BOOL, setBlending, nullptr},
        {VLQT_PROPERTY_MESH_RESOLUTION, VLQT_VALUE_TYPE_IVEC2, setMeshResolution, nullptr},
        {VLQT_PROPERTY_SOURCE, VLQT_VALUE_TYPE_REF, setShaderSourceRef, "source"},
    };
}

} // namespace

TypeDef shaderType()
{
    TypeDef t{VLQT_ELEMENT_TYPE_SHADER, "SHADER", nullptr, shaderEffectProps(), makeShaderEffect};
    t.autoSize = AutoSize::WithInput; // implicit size = the Input; without Input the SIZE counts
    return t;
}

} // namespace vlqt
