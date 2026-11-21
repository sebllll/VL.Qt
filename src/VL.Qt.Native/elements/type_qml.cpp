#include "elements/element_types.h"
#include "elements/elements.h"
#include "elements/geometry.h"
#include "qmlitem.h"

namespace vlqt {

namespace {

// QmlElement (protocol.h "QmlElement"): QmlItem (qmlitem.h), own QQuickItem that holds the instance of a user .qml file.
QQuickItem* makeQml() { return new QmlItem(); }

bool setQmlSource(Elements& els, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    static_cast<QmlItem*>(n.item)->setSource(els.qmlEngine(), QString::fromUtf8(v.utf8));
    applySize(n); // Auto Size / SIZE reach the new root
    return true;
}

bool setQmlReload(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    static_cast<QmlItem*>(n.item)->setReload(v.i);
    return true;
}

std::vector<PropDef> qmlProps()
{
    return {
        {VLQT_PROPERTY_QML_SOURCE, VLQT_VALUE_TYPE_STRING, setQmlSource, nullptr},
        {VLQT_PROPERTY_QML_RELOAD, VLQT_VALUE_TYPE_I32, setQmlReload, nullptr},
        {VLQT_PROPERTY_AUTO_SIZE, VLQT_VALUE_TYPE_BOOL, setAutoSize, nullptr},
    };
}

} // namespace

TypeDef qmlType()
{
    return {VLQT_ELEMENT_TYPE_QML, "QML", nullptr, qmlProps(), makeQml};
}

} // namespace vlqt
