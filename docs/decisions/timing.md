# Timing: Qt Animations and the vvvv IFrameClock

How time and the frame loop are shared between vvvv and Qt, so that animation, pause and fixed time steps behave deterministically.

## Design
- **Custom `QAnimationDriver`.** `elapsed()` returns `IFrameClock.Time`; vvvv calls `advance()` once per frame before `processEvents()`. The driver is per thread, not per window, so the time model is fixed before the reconciler makes any assumptions about it. Qt animations run through the driver, so pause and fixed time steps stay consistent.
- **`IFrameClock` is the only time source.** Qt has no clock of its own; the driver only passes vvvv's time through. All Qt scenes share this one global clock. Pause or time offset per scene is done with values from vvvv. Scenes on their own threads are only worth adding for a real need.
- **Division of labour for animation.** vvvv supplies all choreographed and scrubbable values (a deterministic function of time). Qt-native animations (`Behavior`, transitions, states) are meant only for polish such as easing on value changes. Qt-internal animations can only run forward and pause; they cannot scrub backwards, so scrubbing is only guaranteed for values from vvvv.
- **Global pump on `IFrameClock.GetFrameFinished()`.** Each renderer only writes its commands (reconciler) in `Update()`. One global pump on the `QtApp` provider then runs `advance()` and `processEvents` once per vvvv frame for all windows. Reasons: it is independent of the number of nodes, and the order is guaranteed.
- **Order per vvvv frame:** (1) apply vvvv values to the scene (reconciler), (2) `advance()` with `IFrameClock.Time`, (3) render. Qt animations therefore react to new values in the same frame, with no frame of offset to vvvv.
- **`QT_QPA_UPDATE_IDLE_TIME=0`** is set by QtNative. On Windows `requestUpdate()` goes through a timer (default 5 ms); without the setting only about 22 % of the pumps render (about 20 fps at 60 Hz). With it, exactly one swap happens per pump, deterministically.
- **Pause:** while vvvv is paused nothing fires, so there is neither pump nor `advance()`. The Qt window freezes completely (no repaint, resize or input).
- **Dragging/pausing a plain `QQuickWindow`:** standstill or stutter is accepted. Windows' modal move/size loop delivers irregular vvvv frames, and a pause has no pump. The yardstick is the behaviour of the Skia/Stride windows, and the form-based window that replaces the plain window behaves like them. Revisit if the form window is worse than a Skia window while dragging.
- **VSync:** vvvv sets the pace; VSync only prevents tearing. The Renderer's `VSync` pin is on by default (swap interval 1), so a frame waits for the display refresh and the pump can take up to one refresh interval. With D3D11 the wait is at the start of the frame, in `QRhi::beginFrame` (frame latency waitable object, maximum frame latency 2), not in Present: `Present(1)` itself hardly blocks. That wait is never counted as Qt work (no slow-frame warning for it) and shows in the F5 overlay as its own segment "vsync wait". `VSync` off is the opt-out: swap interval 0, the present never blocks and tearing is allowed (with the `basic` loop and D3D11, `swapInterval(0)` becomes `Present(0)` with allow-tearing). Several VSync windows do not halve the frame rate on Windows: Qt runs one VBlank thread per monitor that posts the update requests of its windows, and each window waits only in its own `beginFrame` for the next refresh (measured with 2 and 3 windows: each at the refresh rate, host pump within one refresh). Condition: the host dispatches the Win32 messages of its thread between the pumps, as vvvv's main loop does; a host that only calls the pump renders such a request one pump later. Linux is not measured yet (see Open questions).
- **No real-time elements.** QML `Timer`, JS `Date` and video run on system time, not on our clock, so they are not offered on the element level. Timing comes from vvvv, and video arrives as a texture from vvvv.
- **Frame-exact offline capture** comes with the texture output / render control, not with the plain window. The principle then is a fixed time step of the vvvv clock, which the driver follows.

## Several vvvv apps
- **One pump per app host.** `QtPump` is a service of the `AppHost`, bound to that app's `IFrameClock`; an F8/F5 restart replaces the host, the pump and the clock, while `QtApp` and the Qt application stay. Each pump calls `QtApp.PumpFrame` once per vvvv frame of its app, and only the windows of that app render (`vlqt_app_process_events_for`).
- **The driver is owned by `QtApp.AdvanceDriver`** (since W4-84): it never runs backwards, whoever advances it (a pump, or the offscreen render of `QtTexture`), and QtNative ignores a time below the current one.
- **Offset and resync.** A new app clock may start below the driver time (restart) or fall behind another app's clock. `QtPump` then adds an offset to the clock time (`_offset`, resynced when it falls more than `ResyncSeconds` behind), so the driver keeps moving forward at the frame step of the app that pumps.

## Open questions
- **Restart after idle:** after closing and reopening a window, a new animation starts about one frame (1.5°) off. It is deterministic; observe whether it matters.
- **Particles:** check whether they depend on the driver once they are needed.
- **`Animator` types** (`OpacityAnimator`, …) run on the render thread in the `threaded` loop, bypassing our driver. With the `basic` loop or render control on the main thread this is harmless, so the `threaded` loop must not be enabled unchecked.
- **Several VSync windows on Linux:** on Windows they keep the refresh rate (see VSync above). Linux (OpenGL, X11 and Wayland) is not measured yet, nor are monitors with different refresh rates.

## Rejected alternatives
- Keeping Qt animations in sync while scrubbing by seeking (the state lives inside every running animation; fiddly and error-prone).
- Qt VSync as the clock source: vvvv stays the clock. Qt's swap does not drive the frame loop; with VSync on it only prevents tearing, at the price that the pump can wait up to one refresh interval inside the present.
- A timer-based substitute pump for dragging/pausing a plain window (effort for a transitional window, and a second path into Qt events outside the order reconciler → advance → render).
