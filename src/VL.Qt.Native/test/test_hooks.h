// Internal seam between the product files and the test exports of vlqt_test.h (test/test_*.cpp). Not part of the C ABI.
// Product files call into the tests only at the places marked "test hook:"; the window handle parts the tests read come
// from window/window.cpp and app/fonts.cpp below and from offscreen/offscreen.h (quickWindow, stateOf).
#pragma once

#include <QtCore/QElapsedTimer>
#include <QtCore/QPointF>
#include <QtCore/qnamespace.h>

#include <memory>
#include <utility>
#include <vector>

class QQuickWindow;
struct vlqt_scene;

namespace vlqt {
class PerfMonitor;
class Scene;
}

namespace vlqt::test {

// Synthetic input state of one scene handle, created by its first test input (test/test_input.cpp).
struct TestInput
{
    Qt::MouseButtons buttons = Qt::NoButton;          // vlqt_test_send_mouse: buttons held between test events
    QElapsedTimer clock;                              // vlqt_test_send_* / _queue_*: event timestamps
    std::vector<std::pair<int, QPointF>> touch;       // vlqt_test_send_touch: active points (id, window DIP)
    Qt::MouseButtons queuedButtons = Qt::NoButton;    // vlqt_test_queue_mouse
    std::vector<std::pair<int, QPointF>> queuedTouch; // vlqt_test_queue_touch: active points (id, window DIP)
};

// --- window/window.cpp: parts of a scene handle (a window or an offscreen scene); null for a null handle ---
Scene* scene(vlqt_scene* handle);
PerfMonitor* perf(vlqt_scene* handle);
// The handle's slot for its test input state, empty until test/test_input.cpp fills it.
std::unique_ptr<TestInput>* input(vlqt_scene* handle);
// app/fonts.cpp: references of a font handle of vlqt_font_add, 0 = removal pending (end of pump), -9 = removed or unknown.
int fontRefs(unsigned long long handle);

// --- test/test_input.cpp, for window/window.cpp ---
// With VLQT_TEST_SYNTHETIC_INPUT=1 in the process environment (SmokeTest): installs on the on-screen window a filter
// that drops spontaneous (OS) pointer input, so a real cursor resting over a test window cannot change hover or press
// state. Input of the test exports passes. No-op otherwise.
void installInputFilter(QQuickWindow* window);

} // namespace vlqt::test
