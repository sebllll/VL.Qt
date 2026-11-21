/* scene/commands.h - decoder of the command buffer (vlqt_scene_apply): Scene::apply and the members of Scene that decode
 * a payload (SET, SET_STYLE, SET_UNIFORMS, SET_EFFECT_CHAIN, SET_QML_PROPERTIES) are in scene/commands.cpp, the
 * structural ops in scene/scene.cpp. The wire format is in protocol.h. */
#pragma once

#include <QtCore/QtTypes>

#include <string>

namespace vlqt {

/* Details of the last apply: rc 0 = it succeeded. id = object id the bad command addresses (0 = none:
 * CLEAR_COLOR, RESET, truncated header, root), prop = property of a bad SET (else 0), reason = message without
 * the "apply: command #n ..." prefix. Read by vlqt_scene_get_apply_error. */
struct ApplyError {
    int rc = 0;
    int command = 0;
    quint32 id = 0;
    quint16 op = 0;
    quint16 prop = 0;
    std::string reason;
};

} // namespace vlqt
