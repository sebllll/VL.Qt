#include "elements/element_types.h"
#include "elements/elements.h"
#include "elements/geometry.h"
#include "qmlitem.h"

#include <algorithm>

namespace vlqt {

namespace {

// QmlElement (protocol.h "QmlElement"): QmlItem (qmlitem.h), own QQuickItem that holds the instance of a user .qml file.
QQuickItem* makeQml() { return new QmlItem(); }

bool setQmlSource(Elements& els, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    auto* item = static_cast<QmlItem*>(n.item);
    item->setSource(els.qmlEngine(), QString::fromUtf8(v.utf8));
    els.queueQmlBuild(item); // the root comes at the end of the apply: Auto Size / SIZE reach it in QmlItem::rebuild
    return true;
}

bool setQmlReload(Elements& els, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    auto* item = static_cast<QmlItem*>(n.item);
    item->setReload(v.i);
    els.queueQmlBuild(item);
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

void Elements::queueQmlBuild(QmlItem* item)
{
    if (item->buildPending() && std::find(qmlBuilds.begin(), qmlBuilds.end(), item) == qmlBuilds.end())
        qmlBuilds.emplace_back(item);
}

void Elements::buildQml()
{
    // A build can not queue another one; taking the list first keeps it valid whatever the items do.
    std::vector<QPointer<QQuickItem>> items;
    items.swap(qmlBuilds);
    for (const auto& i : items)
        if (i)
            static_cast<QmlItem*>(i.data())->build();
}

TypeDef qmlType()
{
    return {VLQT_ELEMENT_TYPE_QML, "QML", nullptr, qmlProps(), makeQml};
}

} // namespace vlqt
