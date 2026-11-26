# Timing

How time and the frame loop are shared between vvvv and Qt, and how the perf monitor measures one pump. Code: `src/VL.Qt.Native/app/animation_clock.cpp` (the animation driver), `src/VL.Qt/Runtime/QtPump.cs` and `QtApp.cs` (the pump), `src/VL.Qt.Native/diagnostics/` (`perf_monitor.cpp` for the hooks, `perf_stats.h` for the sample, `perf_items.cpp` for the timeline). Animation behaviour for users is on [QML elements](../user-qml.md); the numbers of the perf monitor as a user reads them are on [Diagnostics](../diagnostics.md).

## Context

vvvv runs the frame loop and Qt wants to run its own: it ticks animations from a timer and renders when a window asks for it. For deterministic animation, pause and fixed time steps, vvvv must stay the only clock and the only pace. A pump is one call of `vlqt_app_process_events_for`: it delivers events and lets the windows of one vvvv app render. Qt Quick has signals around the phases of a frame but none for the wait for the display, and the perf numbers must be cheap enough to stay on in every window, also with the overlays off.

## Design

### Clock and driver

- **A custom `QAnimationDriver`** (`VlqtAnimationDriver`). `elapsed()` is the vvvv time counted from the start of the driver; vvvv calls `vlqt_app_advance_animations` once per frame before the events are processed. Qt animations run through the driver, so pause and fixed time steps stay consistent. A keep-alive animation timer keeps Qt from stopping the driver when no animation runs, because Qt takes the wall clock for a stopped driver.
- **`IFrameClock` is the only time source.** Qt has no clock of its own; the driver passes vvvv's time through. All scenes share this one clock. A pause or time offset per scene is done with values from vvvv.
- **The driver never runs backwards.** QtNative ignores a time below the current one, whoever advances it (a pump, or the offscreen render of `QtTexture`). QML animations follow the clock forward; a backward jump does not rewind them, they hold until the clock passes the old time again.
- **Division of labour for animation.** vvvv supplies all choreographed and scrubbable values (a deterministic function of time). Qt-native animations (`Behavior`, transitions, states) are for polish such as easing on value changes: they run forward and pause but cannot scrub backwards, so scrubbing is only guaranteed for values from vvvv.
- **No real-time elements.** QML `Timer`, JS `Date` and video run on system time, not on the clock, so the built-in elements do not offer them. Timing comes from vvvv, and video arrives as a texture from vvvv.

### Pump

- **One pump per app host.** `QtPump` is a service of the `AppHost`, bound to that app's `IFrameClock` and run on `GetFrameFinished()`; an F8/F5 restart replaces the host, the pump and the clock, while `QtApp` and the Qt application stay. Each renderer only writes its commands (reconciler) in `Update()`; the pump then calls `QtApp.PumpFrame` once per vvvv frame, and only the windows of that app render. This is independent of the number of nodes and fixes the order.
- **Order per vvvv frame:** (1) apply the vvvv values to the scene (reconciler), (2) advance the driver with the clock time, (3) render. Qt animations react to new values in the same frame, with no frame of offset to vvvv.
- **Offset and resync.** A new app clock may start below the driver time (restart) or fall behind another app's clock. `QtPump` then adds an offset to the clock time (resynced when the clock falls more than `ResyncSeconds` behind), so the driver keeps moving forward at the frame step of the app that pumps.
- **`QT_QPA_UPDATE_IDLE_TIME=0`** is set by QtNative. On Windows `requestUpdate()` goes through a timer (default 5 ms); without the setting only about 22 % of the pumps render (about 20 fps at 60 Hz). With it, exactly one swap happens per pump, deterministically.
- **Pause:** while vvvv is paused nothing fires, so there is neither pump nor advance. A Qt window freezes completely (no repaint, resize or input).
- **Dragging a plain `QQuickWindow`:** standstill or stutter is accepted. Windows' modal move/size loop delivers irregular vvvv frames, and a pause has no pump. The yardstick is the behaviour of the Skia/Stride windows.

### Render threads

QtNative runs Qt's `basic` render loop on purpose ([render-loop.md](render-loop.md)): windows render inside the pump on the vvvv thread. Offscreen scenes (`QtTexture`) render on a pool of render threads, sized by the pin `Render Threads` (default 1, 0 = main-thread mode, the largest value of all nodes counts, at runtime the pool only grows; see [render-thread.md](render-thread.md)). Time and animation stay on the main thread: the driver is installed and advanced there, items are touched only in `sync` while the main thread waits, and a render thread only renders what the main thread applied for the frame.

### VSync and the wait for the display

vvvv sets the pace; VSync only prevents tearing. The Renderer's `VSync` pin is on by default (swap interval 1), so a frame waits for the refresh and the pump can take up to one refresh interval. `VSync` off is the opt-out: swap interval 0, the present never blocks and tearing is allowed (with the `basic` loop and D3D11, `swapInterval(0)` becomes `Present(0)` with allow-tearing).

With D3D11 the wait is at the start of the frame, in `QRhi::beginFrame` (frame latency waitable object, maximum frame latency 2), not in Present: `Present(1)` itself hardly blocks. With VSync off the same wait shows that the GPU is behind. Qt emits `beforeFrameBegin` right before `beginFrame` and `beforeSynchronizing` right after it and has no signal at its end, so the perf monitor takes the vsync wait as `beforeFrameBegin` to `beforeSynchronizing`. The segment also holds the rest of `beginFrame` (reading back the timestamps of the previous frame), `prepareSync` and the `beforeSynchronizing` slots connected before the monitor's (the dirty check of the scene change counter, which is short). The software backend has no `beforeFrameBegin`, so the segment is 0 there. The wait is never counted as Qt work (no slow-frame warning for it) and shows in the F5 overlay as its own segment "vsync wait".

Several VSync windows do not halve the frame rate on Windows: Qt runs one VBlank thread per monitor that posts the update requests of its windows, and each window waits only in its own `beginFrame` for the next refresh. This needs the host to dispatch the Win32 messages of its thread between the pumps, as vvvv's main loop does; a host that only calls the pump renders such a request one pump later.

### Perf monitor: phases

Each phase is the time between two `QQuickWindow` signals, taken with a monotonic clock: sync is `beforeSynchronizing` to `afterSynchronizing`, render is `beforeRendering` to `afterRendering` (command recording on the CPU), present is `afterRendering` to `frameSwapped`. Events are the rest of the pump (pump minus vsync wait, sync, render and present): posted events, input, polish, animation ticks and, with several windows, the frames of the other windows.

### Perf monitor: frame hand-off

Frame signals arrive on the GUI thread for a window and on the scene's render thread for an offscreen scene, concurrently with the end of a pump. The hooks fill a pending sample that only the signal thread touches; `afterFrameEnd` adds the finished frame to a second sample under a mutex, and the end of the pump takes it. A sample therefore holds whole frames only: those finished before the pump ended, for a threaded scene usually the previous host frame's. Sampling and the hand-off allocate nothing and format no text; the pages are built at 4 Hz while visible.

### Timeline geometry

With QRhi the timeline is four geometry nodes drawn in this order: over-budget highlights, bars with peak markers, budget and GPU lines, max-hold line. An update rewrites bars and lines; the highlights and the max-hold line are marked dirty only when they change, because every dirty node makes the batch renderer re-batch. The Qt Quick software backend has no custom geometry and is slow with many rectangle nodes, so the primitives are painted into one image shown by one image node.

## Consequences

- Scrubbing and fixed time steps are exact for values from vvvv only; a Qt animation that has run past a point does not come back.
- The vsync segment depends on where the platform waits. With VSync on and a scene that changes every frame, the wait fills most of the pump.
- A scene on a render thread shows `sync` and `render` as the segments between Qt's signals, not the whole render job.
- Sampling must not allocate or format text, and a new page must not run per frame.
- `PerfOverlayTest` and `PerfStatsTest` in the SmokeTest protect the attribution of phases and the statistics.

## Alternatives considered

- **Keeping Qt animations in sync while scrubbing by seeking:** the state lives inside every running animation; fiddly and error-prone.
- **Qt VSync as the clock source:** vvvv stays the clock. Qt's swap does not drive the frame loop; with VSync on it only prevents tearing, at the price that the pump can wait up to one refresh interval inside the present.
- **A timer-based substitute pump for dragging or pausing a plain window:** a second path into Qt events outside the order reconciler → advance → render, for a case that Skia windows share.
- **A ring of chunk nodes moved by transforms (timeline):** uploads only the new samples, but each extra node costs time per frame, also without updates.
- **Opaque materials for the bars:** a cheaper render phase, but a higher pump median.
- **One rectangle node per primitive on the software backend:** its dirty-region pass took more than 100 ms per update with about 1400 nodes.

## Open questions

- **`Animator` types** (`OpacityAnimator`, `XAnimator`, …) in a user QML file may not follow the driver in a scene on a render thread. QtNative installs its driver once, for the main thread (`vlqt::clock::install`), and nothing in QtNative handles animators; Qt may time animator jobs with the animation timer of the thread that renders, which would be the wall clock. This was not run: the code does not guarantee that animators follow the driver, and property animations (`NumberAnimation`, `Behavior`) are the ones that do. A check needs a `QmlElement` with an `OpacityAnimator` in a `QtTexture` scene with `Render Threads` 1 and a paused clock.
- **Several VSync windows on Linux** (OpenGL, X11 and Wayland) and monitors with different refresh rates are not measured.

## Evidence

- The behaviour of `QRhi::beginFrame` was read in qtbase v6.12.0 (`qrhid3d11.cpp`, `QRhiD3D11::beginFrame`).
- Several VSync windows were measured on Windows with 2 and 3 windows: each at the refresh rate, host pump within one refresh interval. Linux was not measured.
- The two timeline alternatives were measured on a Snapdragon notebook with D3D11; the Qt version and the date were not recorded, and x64 was not measured. Rerun with the SmokeTest scenario `perfoverlay` and the F5 Timeline page.
