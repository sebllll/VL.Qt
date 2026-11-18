# Paths 01: Spike – Qt Quick Shapes, custom path element, CurveRenderer benchmark

Milestone 1.0 · Labels: `question`, `area:native`, `topic:performance` · Depends on: – · Plan: `docs/plans/paths/01_spike.md`, decisions: `docs/decisions/paths.md`

## Goal
Answer with evidence whether the planned native route works and where the `CurveRenderer` reaches its limits, before packages 02–05 build on it. The result is a research note with numbers; the prototype code stays on the spike branch for package 02 to reuse.

## Context
- VL.Qt is on Qt 6.12 (`tools/setup-qt.ps1`). Qt Quick Shapes is installed (`C:\Qt\6.12.0\<arch>\lib\cmake\Qt6QuickShapes`, `...Qt6QuickShapesPrivate`, `qml\QtQuick\Shapes`), not linked yet.
- Planned route: `QQuickShape` (`preferredRendererType: CurveRenderer`) with one `QQuickShapePath` whose only path element is `VlPathElement`, our subclass of the private `QQuickCurve` (`include/QtQuick/6.12.0/QtQuick/private/qquickpath_p.h:86`, virtual `addToPath(QPainterPath&, const QQuickPathData&)`). Qt's own `QQuickPathPolyline` (`qquickpath_p.h:549`) works the same way.
- `QQuickShapePath` (`include/QtQuickShapes/6.12.0/QtQuickShapes/private/qquickshape_p.h:216`) has strokeColor, strokeWidth, fillColor, fillRule, joinStyle, miterLimit, capStyle, strokeStyle, dashOffset, dashPattern, `trim` (`QQuickShapeTrim`, 6.10).
- The existing perf overlay (`src/VL.Qt.Native/perf.cpp`) and `vlqt_window_get_perf_stats` give frame timings; `docs/profiling.md` describes CPU profiling.
- On the arm64 dev machine Creative Cloud causes ~55 % background CPU: repeat timing outliers before trusting them.

## Questions to answer (each with evidence: PNG, number or code reference)
- [ ] Q1 Linking: do `Qt6::QuickShapes` and `Qt6::QuickShapesPrivate` link into `vlqt` and does `windeployqt` deploy `Qt6QuickShapes.dll` and `qml/QtQuick/Shapes` with `import QtQuick.Shapes` in `src/VL.Qt.Native/qml/deploy.qml`?
- [ ] Q2 Custom element: does a `QQuickCurve` subclass inside a `QQuickShapePath` render with the `CurveRenderer`? Are cubics drawn exactly (grab at 8× scale through the item transform: no facets)? Which signal makes Qt re-process the path after the data changed (`QQuickPathElement::changed()`)?
- [ ] Q3 Closing join: closed polygon via our element with a Close verb vs. `PathPolyline` with the first point repeated: is there a seam (cap instead of join) at the start point with a thick stroke and `Miter` join? PNG of both.
- [ ] Q4 Trim: does `shapePath->trim()->setStart/setEnd/setOffset` work with the `CurveRenderer` and the custom element? Cost of animating only the trim (CPU per frame).
- [ ] Q5 Dash: confirm that `dashPattern`/`dashOffset` are in stroke-width units and that dashes work with the `CurveRenderer`.
- [ ] Q6 Size: does a `Shape` with width/height 0 draw? Does geometry with negative coordinates draw outside the item bounds? Effect of MSAA on/off (Renderer MSAA pin, #118) on quality and cost.
- [ ] Q7 Benchmark (`CurveRenderer`, and `GeometryRenderer` for comparison as input for the 2.0 issue), each 300 frames after warm-up, report mean and p95 of CPU time per frame on the render thread and the GUI thread, on arm64 (x64 if available):

  | Case | 100 | 1000 | 8190 points |
  |---|---|---|---|
  | Polyline, all points change every frame | | | |
  | Cubic path (points/3 segments), all change every frame | | | |
  | Static path, only `Transform` animated | | | |
  | Static path, only trim animated | | | |
  | 100 elements × 100 points, static, `Transform` animated | | | |
  | `GeometryRenderer`, polyline changing, sync / `asynchronous` | | | |

## Steps
- [ ] Branch `spike/<issue>-shapes` from `develop`.
- [ ] Link `QuickShapes` and `QuickShapesPrivate` in `src/VL.Qt.Native/CMakeLists.txt` (`find_package` line 17, link line 63; comment what the private part is for, like the existing `QuickPrivate` comment), add `import QtQuick.Shapes` to `qml/deploy.qml`.
- [ ] Prototype `VlPathElement` (points + closed, or verbs + points) and a temporary test entry point or SmokeTest scenario that builds the shapes for Q2–Q7 directly in QtNative (no C# element needed).
- [ ] Grab PNGs into `build/smoke/` for Q2, Q3, Q5, Q6.
- [ ] Measure Q7.
- [ ] Write `docs/research/02_shapes-spike.md`: per question source, finding, consequence; the benchmark table; recommended limits for tooltips and the 2.0 issues (renderer, mass data); link to the spike branch and the prototype files.
- [ ] If a finding contradicts `docs/decisions/paths.md` or `docs/plans/paths/00_overview.md` (e.g. Q2 fails): write it there as open, stop and report instead of working around it.

## Acceptance criteria
- `docs/research/02_shapes-spike.md` answers Q1–Q7 with evidence.
- The PR against `develop` contains only the research note (and decision/plan updates if a finding requires them); the prototype stays on the spike branch.

## Verification
- `tools/build-native.ps1` builds; the spike scenario runs; PNGs exist and match the claims.

## Task cut (for the orchestrator)
1. Linking + deploy check (Q1) – Sonnet – `CMakeLists.txt`, `qml/deploy.qml`, `tools/build-native.ps1` (read).
2. `VlPathElement` prototype + render checks Q2, Q3, Q5, Q6 with PNGs – Opus – new `src/VL.Qt.Native/pathelement.h/.cpp` (spike branch), test entry.
3. Trim + benchmark Q4, Q7 – Opus – same files, `perf.cpp` (read).
4. Research note – Sonnet – `docs/research/02_shapes-spike.md`.
