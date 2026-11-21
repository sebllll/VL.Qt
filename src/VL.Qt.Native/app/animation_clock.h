// The animation clock: QML animations run on the time of the host (vvvv's IFrameClock), advanced by
// vlqt_app_advance_animations. Qt GUI thread. Private Qt API in animation_clock.cpp: QAbstractAnimationTimer and
// QUnifiedTimer (the keep-alive timer), check on Qt updates (list: elements/elements.h).
#pragma once

namespace vlqt::clock {

// Installs the animation driver bound to vvvv time and the keep-alive timer that keeps it running (vlqt_app_init).
void install();

} // namespace vlqt::clock
