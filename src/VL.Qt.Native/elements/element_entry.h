/* elements/element_entry.h - the record of a scene for one created element (owned by the Scene's id table). */
#pragma once

#include "events.h"

#include <QtCore/QPointer>
#include <QtCore/QtTypes>

#include <memory>
#include <vector>

class QObject;
class QQuickItem;

namespace vlqt {

class EffectChain;     // effectchain.h
struct TransformState; // elements.h

/* State of one created element (owned by the Scene's id table). */
struct ElementEntry {
    QQuickItem* item = nullptr;
    quint16 type = 0;
    quint32 feedbackId = 0;            // BIND feedbackId (0 = unbound), see events.h
    QObject* transform = nullptr; // QML Matrix4x4, appended to item.transform on the first TRANSFORM or drag offset
    std::shared_ptr<TransformState> xf; // TRANSFORM + ANCHOR as sent (Elements::initGeometry, right after CREATE)
    SentState sent;              // last EVENT_MEASURED / EVENT_SCENE_BOUNDS sent for sink
    QQuickItem* effectSource = nullptr; // SHADER: current source item (see Elements::clearEffectSource)
    std::vector<QPointer<QQuickItem>> textureGroups; // SHADER: Groups of texture uniforms, one proxy reference each
    std::vector<quint32> handlers;      // ids of the input handlers attached to this item, events.h
    QObject* mask = nullptr;            // TextMask (Text items with input handlers), events.h updateTextMask
    EffectChain* effectChain = nullptr; // SET_EFFECT_CHAIN: QObject child of item, null = no chain (layer off)
    QQuickItem* maskHost = nullptr;     // this node is attached as mask of a link of that element's chain (nullptr = not)
    bool maskHidden = false;            // attached, or detached and parked hidden under the root until REPARENT/DESTROY
    bool autoSize = true;        // AUTO_SIZE (containers + Button, see applySize in elements.cpp)
    float size[2] = {0, 0};      // last SIZE (w, h), so AUTO_SIZE can re-apply it
    quint32 transformEpoch = 0;  // counts SET TRANSFORM: a running drag re-bases its offset on 0 after one
    bool debug = false;          // DEBUG: drawn by the window's debug overlay (debug.h), counted in Elements::debugCount
    bool polled = false;         // Elements::pollsState: the poll reads media/error/clicks/user value of this type
    bool pollListed = false;     // its id is in Scene::pollIds (listPolled, events.h)
    quint32 measuredStamp = 0;   // Scene::measureStamp of the last poll that visited it (one visit per poll)
    quint32 polledStamp = 0;     // Scene::pollStamp of the last poll that read its state
};

} // namespace vlqt
