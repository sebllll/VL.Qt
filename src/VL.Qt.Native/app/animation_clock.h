// The animation clock: QML animations run on the time of the host (vvvv's IFrameClock), advanced by
// vlqt_app_advance_animations. Qt GUI thread. Private Qt API in animation_clock.cpp: QAbstractAnimationTimer and
// QUnifiedTimer (the keep-alive timer), check on Qt updates (the list of the private classes is the head comment of
// elements/elements.h).
//
// The clock moves forward and holds. The driver ignores a time below the current one (several apps and offscreen renders
// share it), and Qt never rewinds an animation: after the clock has jumped back, a running QML animation holds its value
// until the clock passes the old time again (checked by the SmokeTest AnimationScrub). Only values that vvvv computes
// from the time are scrubbable.
#pragma once

namespace vlqt::clock {

// Installs the animation driver bound to vvvv time and the keep-alive timer that keeps it running (vlqt_app_init).
void install();

// Advances the animations to the host time (vlqt_app_advance_animations). VLQT_OK; VLQT_ERR_NOT_INITIALIZED before
// install(); VLQT_ERR_BAD_ARGUMENT for a time that is NaN, infinite or does not fit the millisecond counter of Qt.
int advance(double seconds);
} // namespace vlqt::clock
