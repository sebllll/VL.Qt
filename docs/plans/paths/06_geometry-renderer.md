# Paths 06: `Renderer` pin with `GeometryRenderer`

Milestone 2.0 · Labels: `enhancement`, `area:native`, `area:csharp`, `topic:performance` · Depends on: Paths 05 · Plan: `docs/plans/paths/06_geometry-renderer.md`

## Goal
An Advanced pin `Renderer` (`Curve` default, `Geometry`) on `Line`, `Polyline`, `Polygon` and `Path`, plus an optional pin `Asynchronous` for the `GeometryRenderer`. Existing patches keep working (default `Curve`).

## Context
- Decision: renderer as a pin on the same nodes, not a category (`docs/decisions/paths.md`).
- Benchmark numbers from `docs/research/02_shapes-spike.md` (Q7) say when `Geometry` pays off (points changing every frame); `Geometry` needs MSAA for smooth edges (Renderer MSAA pin, #118).

## Steps
- [ ] Protocol: `VLQT_PROP_SHAPE_RENDERER` (I32: 0 Curve, 1 Geometry) and `VLQT_PROP_SHAPE_ASYNC` (BOOL) in block 960+, mirror, `--drift`.
- [ ] QtNative: typed setters for `preferredRendererType` and `asynchronous` on the `QQuickShape`.
- [ ] C#: `ShapeRenderer` enum, element properties, `DiffShape`, pins (`Renderer` with `[Smell(SymbolSmell.Advanced)]`, `Asynchronous` optional) with tooltips that name the trade-off and the measured limits.
- [ ] SmokeTest `shapes`: one shape with `Geometry`; unit tests for the diff.
- [ ] Help patch `HowTo Draw a Polyline.vl`: a short note on the pin in `Notes:`.

## Acceptance criteria
`tools/build.ps1 -Test` passes; both renderers draw in the SmokeTest PNG; old help patches unchanged in behavior.

## Task cut
Protocol (Sonnet) → native setters (Sonnet) → C# + tests (Sonnet) → SmokeTest + patch note (Sonnet).
