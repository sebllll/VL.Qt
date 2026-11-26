# Offscreen input

How pointer, touch and keyboard input reaches a Qt scene that has no window: the optional pin `Input Notifications` of `QtTexture` and `QtRenderer`, the helper node `ToTexturePixels`, and the native injection into the scene's `QQuickWindow`. Code: `src/VL.Qt/Scene/OffscreenInput.cs`, `NotificationQueue.cs`, `InputMapping.cs`, `src/VL.Qt.Stride/ToTexturePixelsNode.cs`, `TexturePixelMapping.cs`, and `src/VL.Qt.Native/offscreen/offscreen_input.cpp`. Signatures are in `vlqt.h` (`vlqt_offscreen_send_pointer`, `_send_wheel`, `_send_key`, `_send_text`, `_set_focus`) and the code. How events become handlers and channels is in [events-and-interaction.md](events-and-interaction.md); the Stride nodes are in [stride-output.md](stride-output.md).

## Context

A window gets its input from the operating system. An offscreen scene is a `QQuickWindow` driven by a `QQuickRenderControl` (the object that lets the host render a Qt Quick scene into a texture); it is no native window and never becomes the application's focus window. The host has to feed it. In vvvv, input sources give one stream of `INotification` (mouse, touch, keyboard, focus), which the patch can merge, filter and transform with the reactive nodes.

## Design

### The pin and its coordinates

- The optional pin `Input Notifications` has the type `IObservable<INotification>` (`VL.Lib.IO.Notifications`). That is what `InputManager.GetNotifications` in VL.Stride and `SkiaControl.Notifications` give.
- Positions in the notifications are texture pixels with the origin at the top left (1 DIP = 1 pixel, as in the scene). `ClientArea` is ignored. Positions outside 0 … Size are valid and forwarded: hover ends, a drag goes on, a press there takes the focus away.
- Without the pin, Interaction nodes in the scene are inert.

### ToTexturePixels

`ToTexturePixels` (category `Stride.Input`, next to `MouseRay`, experimental) turns the notifications of a window into texture pixels for a texture on Stride's unit quad (−0.5 … 0.5 in its local XY plane, +Y up) that is placed by `World` and seen through `View` and `Projection`.

- Per notification a ray from `Position` and `ClientArea` goes through the inverse of World·View·Projection, is cut with the plane z = 0, and u = x + 0.5, v = 0.5 − y, times `Texture Size`.
- Fullscreen is `World` = Scaling (2, 2, 1) with identity View and Projection. A zero matrix counts as identity (an unconnected Matrix pin).
- It maps through `INotification.Transform`, so every notification type keeps its kind: `ClientArea` becomes the texture size, a touch's contact area scales with it. A position without a hit (ray parallel to the quad, singular matrix) becomes (−1e6, −1e6). Keyboard and focus notifications pass unchanged.
- The output is the same observable instance while `Input` stays the same; the matrices are read when a notification arrives.
- It covers fullscreen, a quad in 3D and, approximately, any flat placement. A texture on an arbitrary mesh needs the patch's own raycast to UV, which then feeds pixel positions directly.

### Translation

`OffscreenInput.Notify` (in `VL.Qt`, so a later Skia package can reuse it) maps notifications to the native calls, without an allocation per event:

- Mouse down, up, move, wheel, horizontal wheel and lost, touch down, move and up, key down, up and press. WinForms `MouseButtons` and `Keys` become Qt's `MouseButtons`, `KeyboardModifiers` and `Qt::Key`. `Notify` keeps the held buttons and passes them as Qt's `buttons`.
- `KeyPress` gives its character as text (`send_text`, at most 64 characters per call) unless it is a control character: Enter, Tab, Backspace and Ctrl+letter arrive as key events. A character outside the BMP is joined from its two notifications. A `KeyDown` that repeats a key already down gives `autoRepeat`.
- `MouseLostNotification` gives `LEAVE` (held buttons stay), `KeyboardLostNotification` only forgets pressed keys, `LostFocusNotification` gives `set_focus(0)`.
- `MouseClickNotification` is ignored, because Qt derives clicks and double clicks itself; so are `GotFocusNotification` (a press gives the scene focus) and gestures.
- vvvv has no pen notification: a pen arrives as touch or mouse from Windows. The native side delivers a pen like a window on Windows does, for a host that sends one.
- The sign of the horizontal wheel is set here, because no vvvv input source creates `MouseHorizontalWheelNotification`: positive `WheelDelta` = left, as Qt's `angleDelta().x()` and VL.ImGui, passed through unchanged.

### Native injection

QtNative sends `QMouseEvent`, `QWheelEvent`, `QTouchEvent`, `QTabletEvent` and `QKeyEvent` with plain `QCoreApplication::sendEvent` to the scene's `QQuickWindow`: synchronous and not marked spontaneous (like the `vlqt_test_send_*` functions), so handlers, `vlqt_scene_poll`, `EventReader`, `FeedbackRouter` and channels work unchanged.

- Each scene has its own mouse, touchscreen and pen `QPointingDevice`, because point ids and grabs live in the device: a press sent to two scenes does not cancel the other's grab. The touchscreen device sends all active points per event; `CANCEL` applies to every point of a device.
- The pen is a stylus (pointer type Pen, so `Devices = Pen` of the Interaction nodes matches) that sends tablet events and, when nobody accepts one, the mouse event from the stylus, synthesized like `QGuiApplication` does for Qt's Windows plugin. Its `id` is ignored (one point), `CANCEL` drops its grabs without a release, `LEAVE` ends its hover.
- A mouse event gets `buttons` = the buttons held after the event. A press or release without a changed button is `BAD_ARGUMENT`, and so is `LEAVE` on touch.
- `MouseButtonDblClick` is synthesized natively from two presses.
- Text goes as a `QKeyEvent` press and release with the text and `Qt::Key_unknown`. Key events reach the focus item only; `QShortcut` does not see them.

### Keyboard focus

An offscreen `QQuickWindow` never becomes `QGuiApplication::focusWindow()`. QtNative gives a scene focus by sending `QFocusEvent` `FocusIn` to its window, and `FocusOut` takes it, so `activeFocusOnPress` of TextInput and the active focus item work.

Qt Quick gives the root item focus only while the application's focus window is the window, its render window or none, and a scene has no render window. QtNative therefore uses a render control with private API: `SceneRenderControlPrivate`, derived from `QQuickRenderControlPrivate`, whose `isRenderWindow` is also true for the current focus window while the scene is focused (the pattern of `QQuickWidget`). The "render control" is the `QQuickRenderControl` above. This needs `qquickrendercontrol_p.h` and `qquickwindow_p.h` and **must be checked on every Qt update**. `FocusOut` alone keeps the active focus item when no window has focus, so a focus loss first releases it with `releaseFocus`.

The rule is that of a window:

- A press (mouse or touch) inside 0 … Size focuses the scene before the press is delivered; a press outside, or `set_focus(0)` (the host window lost focus), unfocuses it.
- Key and text events of an unfocused scene are dropped (the result is OK).
- The `FocusRelease` filter of windows (Esc reverts and releases, a press outside the focused element releases) is installed on scenes too.
- Several textures fed by one window: only the last one pressed has focus.

### Timing and threads

- The node subscribes to the pin and queues the notifications (a short lock and reused lists, because they may arrive off the main thread). It drains them at the start of `Update`, before the scene's apply: the events act on the items of the previous apply, and the render at the draw start of the same frame shows the result. The events of the scene (`PollEvents` after the batch) reach the element nodes and channels in the next frame, as with windows.
- A new observable instance on the pin re-subscribes and keeps what is queued: a patch that builds its source every frame (`GetNotifications` returns a new observable per call) would otherwise lose every click. Dispose unsubscribes.
- `Enabled` false drains and drops the queue: a frozen scene takes no input.
- Events are delivered on the main thread, where the items live, and need no lock: the render thread touches items only in `sync`, while the main thread waits ([render-thread.md](render-thread.md)). Input code never calls into the scene from a render thread or a Qt render signal, and uses item geometry, never the scene graph or `QRhi`, for mapping and hit tests. It takes the scene size from the binding set on the main thread.

## Consequences

- Qt updates can break the focus path, because it relies on private headers. The SmokeTest scenario `QtTextureInput` (click, drag, hover enter and leave, wheel, two-finger pinch, Slider value, TextInput focus by click and typing, Esc and press-outside release) protects it.
- Input in the Stride scene is only as good as the placement: a mesh needs its own mapping to texture pixels.
- Touch and mouse of one source are separate devices; multi-touch works per scene.

## Alternatives considered

- **An own pointer struct:** users would build it from notifications themselves, and keyboard would need a second type.
- **A 2D `Matrix` variant of `ToTexturePixels`:** the quad node covers it. **A UV-function delegate pin:** not patch-friendly.
- **`QQuickRenderControl::renderWindow`:** needs a real `QWindow` of the host; vvvv's Stride window is not a Qt window.
- **Delivering through the pump at the end of the frame:** the events would act one frame late.

## Open questions

None.

## Evidence

SmokeTest scenario `QtTextureInput` (`src/VL.Qt.SmokeTest/QtTextureTest.Input.cs`), run with `tools/build.ps1 -Test -Only QtTextureInput`; unit tests of the mapping in `src/VL.Qt.Tests`.
