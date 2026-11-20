// Internal seam between the product files and the test exports of vlqt_test.h (test/test_*.cpp). Not part of the C ABI.
// Product files call into the tests only at the places marked "test hook:"; the window handle parts the tests read come
// from vlqt.cpp below and from offscreen.h (quickWindow, stateOf).
#pragma once

#include <QtCore/QElapsedTimer>
#include <QtCore/QPointF>
#include <QtCore/qnamespace.h>

#include <utility>
#include <vector>

class PerfMonitor;
class QQuickWindow;

namespace vlqt {
struct Scene;
}

namespace vlqt::test {

// Synthetic input state of one window handle (test/test_input.cpp); every handle carries one.
struct InputState
{
    Qt::MouseButtons buttons = Qt::NoButton;          // vlqt_test_send_mouse: buttons held between test events
    QElapsedTimer clock;                              // vlqt_test_send_* / _queue_*: event timestamps
    std::vector<std::pair<int, QPointF>> touch;       // vlqt_test_send_touch: active points (id, window DIP)
    Qt::MouseButtons queuedButtons = Qt::NoButton;    // vlqt_test_queue_mouse
    std::vector<std::pair<int, QPointF>> queuedTouch; // vlqt_test_queue_touch: active points (id, window DIP)
};

// --- vlqt.cpp: parts of a window handle (a window or an offscreen scene); null for a null handle ---
Scene* scene(void* handle);
PerfMonitor* perf(void* handle);
InputState* input(void* handle);
// References of a font handle of vlqt_font_add, 0 = removal pending (end of pump), -9 = removed or unknown.
int fontRefs(unsigned long long handle);

// --- test/test_input.cpp, for vlqt.cpp ---
// With VLQT_TEST_SYNTHETIC_INPUT=1 in the process environment (SmokeTest): installs on the on-screen window a filter
// that drops spontaneous (OS) pointer input, so a real cursor resting over a test window cannot change hover or press
// state. Input of the test exports passes. No-op otherwise.
void installInputFilter(QQuickWindow* window);

} // namespace vlqt::test
