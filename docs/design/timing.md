# Timing

How the perf monitor of QtNative measures one pump: which Qt signals bound each phase, how frames reach the sample of a pump, and why the timeline is drawn as it is. Code: `src/VL.Qt.Native/diagnostics/` (`perf_monitor.cpp` for the hooks, `perf_stats.h` for the sample, `perf_items.cpp` for the timeline). The numbers as a user reads them are on [Diagnostics](../diagnostics.md).

## Context

A pump is one call of `vlqt_app_process_events_for`. A window renders inside it (the basic render loop on the vvvv thread); an offscreen scene (`QtTexture`) renders on its own render thread. Qt Quick has signals around the phases of a frame but none for the wait for the display, and the numbers must be cheap enough to stay on in every window, also with the overlays off.

## Design

### Phases

Each phase is the time between two `QQuickWindow` signals, taken with a monotonic clock: sync is `beforeSynchronizing` to `afterSynchronizing`, render is `beforeRendering` to `afterRendering` (command recording on the CPU), present is `afterRendering` to `frameSwapped`. Events are the rest of the pump (pump minus vsync wait, sync, render and present): posted events, input, polish, animation ticks and, with several windows, the frames of the other windows.

### Vsync wait

The wait for the refresh happens in `QRhi::beginFrame`. Qt emits `beforeFrameBegin` right before it and `beforeSynchronizing` right after it, and has no signal at the end of `beginFrame`, so the vsync wait is `beforeFrameBegin` to `beforeSynchronizing`. With D3D11, `beginFrame` waits on the frame latency waitable object (maximum frame latency 2): with VSync on that is where the wait for the refresh lands, and `Present(1)` itself hardly blocks. With VSync off the same wait shows that the GPU is behind. The segment also holds the rest of `beginFrame` (reading back the timestamps of the previous frame), `prepareSync` and the `beforeSynchronizing` slots connected before the monitor's (the update gate's dirty check, which is short). The software backend has no `beforeFrameBegin`, so the segment is 0 there.

### Frame hand-off

Frame signals arrive on the GUI thread for a window and on the scene's render thread for an offscreen scene, concurrently with the end of a pump. The hooks fill a pending sample that only the signal thread touches; `afterFrameEnd` adds the finished frame to a second sample under a mutex, and the end of the pump takes it. A sample therefore holds whole frames only: those finished before the pump ended, for a threaded scene usually the previous host frame's. Sampling and the hand-off allocate nothing and format no text; the pages are built at 4 Hz while visible.

### Timeline geometry

With QRhi the timeline is four geometry nodes drawn in this order: over-budget highlights, bars with peak markers, budget and GPU lines, max-hold line. An update rewrites bars and lines; the highlights and the max-hold line are marked dirty only when they change, because every dirty node makes the batch renderer re-batch. The Qt Quick software backend has no custom geometry and is slow with many rectangle nodes, so the primitives are painted into one image shown by one image node.

## Consequences

- The vsync segment depends on where the platform waits. With VSync on and a scene that changes every frame, the wait fills most of the pump.
- A scene on a render thread shows `sync` and `render` as the segments between Qt's signals, not the whole render job.
- Sampling must not allocate or format text, and a new page must not run per frame.
- `PerfOverlayTest` and `PerfStatsTest` in the SmokeTest protect the attribution of phases and the statistics.

## Alternatives considered

- **A ring of chunk nodes moved by transforms:** uploads only the new samples, but each extra node costs time per frame, also without updates.
- **Opaque materials for the bars:** a cheaper render phase, but a higher pump median.
- **One rectangle node per primitive on the software backend:** its dirty-region pass took more than 100 ms per update with about 1400 nodes.

## Open questions

None.

## Evidence

The behaviour of `QRhi::beginFrame` was read in qtbase v6.12.0 (`qrhid3d11.cpp`, `QRhiD3D11::beginFrame`). The two timeline alternatives were measured on a Snapdragon notebook with D3D11; the Qt version and the date were not recorded, and x64 was not measured. Rerun with the SmokeTest scenario `perfoverlay` and the F5 Timeline page.
