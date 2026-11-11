/* qmlitem.h - the QmlElement (#104, protocol.h "QmlElement"): loads a user .qml file into the element tree.
 *
 * Stub of package 104/1: the empty item so that CREATE of VLQT_TYPE_QML works. Packages 104/2 (load, set properties)
 * and 104/3 (events back) fill it in.
 */
#pragma once

#include <QtQuick/QQuickItem>

namespace vlqt {

class QmlItem : public QQuickItem
{
    Q_OBJECT
public:
    explicit QmlItem(QQuickItem* parent = nullptr) : QQuickItem(parent) {}
};

} // namespace vlqt
