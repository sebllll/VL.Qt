# Paths 05: Generators `Bezier (Handles)`, `Spline (CatmullRom)`, `Spline (BSpline)`

Milestone 1.0 · Labels: `enhancement`, `area:csharp`, `area:patch`, `manual-check` · Depends on: Paths 04 · Plan: `docs/plans/paths/05_generators.md`, overview `docs/plans/paths/00_overview.md`

## Goal
Curves from point spreads in `Qt.Paths`: Bézier paths with handles, and splines through or along points. All three produce exact cubic Béziers in a `QtPath`; the `CurveRenderer` draws them without flattening.

## Context
- `QtPath` and the builder nodes from packages 02 and 04; the generators append to an input path like the `Add…` nodes (`Input` default `QtPath.Empty`, `Output`).
- Process nodes with change detection (reference, then content for spreads), as in package 04.
- Qt's `PathCurve` (Catmull-Rom in QML) does not work in Shapes; that is why splines are converted in C#.

## Specification

### `Bezier (Handles)`
- Pins: `Input`, `Anchors` (`Spread<Vector2>`), `In Handles` and `Out Handles` (`Spread<Vector2>`, relative to their anchor; missing entries count as zero = corner), `Closed` (false); Advanced pin `Handle Modes` (`Spread<HandleMode>`, default empty = `Free` for all).
- `HandleMode`: `Free` (both handles as given), `Mirrored` (in = −out), `Aligned` (in points opposite to out, keeps its own length). The out handle leads.
- Segment i: cubic from anchor i, control anchor i + out i, control anchor i+1 + in i+1, to anchor i+1; `Closed` adds the segment back to anchor 0 and a Close verb.

### `Spline (CatmullRom)`
- Pins: `Input`, `Points`, `Closed` (false), `Alpha` (0.5 = centripetal, no loops or cusps; 0 uniform, 1 chordal), `Tension` (0; 1 = straight lines).
- Passes through every point. Open ends: duplicate the end points as phantom points. Convert each span exactly to a cubic (Barry–Goldman / tangent form, scaled by `1 − Tension`). Coincident points: no division by zero (skip zero-length spans).

### `Spline (BSpline)`
- Pins: `Input`, `Points` (control polygon), `Closed` (false).
- Uniform cubic B-spline, converted exactly to cubics (Böhm conversion); open: clamped so the curve starts at the first and ends at the last point.

### Common
- Fewer than 2 points: output the input path unchanged. Docs on every pin, with a sentence on what the curve does (through the points vs. along the control polygon).
- Public enum `HandleMode` in `Paths/HandleMode.cs`.

## Steps
- [ ] T1 `Paths/SplineGeometry.cs`: Catmull-Rom (alpha, tension, closed/open) and B-spline → cubics; tests: the curve passes through the points (Catmull-Rom), endpoint clamp (B-spline), no NaN with coincident points, closed curves are continuous at the seam.
- [ ] T2 `Paths/BezierGeometry.cs` + `HandleMode`: segments and handle modes; tests per mode.
- [ ] T3 Nodes `Nodes/Paths/BezierHandlesNode.cs`, `SplineCatmullRomNode.cs`, `SplineBSplineNode.cs` (display names `Bezier (Handles)`, `Spline (CatmullRom)`, `Spline (BSpline)` as determined in package 04); tests for change detection.
- [ ] T4 Help patches `help/Qt/HowTo Draw a Bezier Curve with Handles.vl` (anchors and handles shown as small circles and lines, handle modes compared) and `HowTo Draw Splines.vl` (the same points as Catmull-Rom with alpha 0/0.5/1 and as B-spline, control polygon drawn with `Polyline`); lines in `help/Help.xml`; both checks.
- [ ] T5 `docs/shapes.md`: section on generators.

## Acceptance criteria
- The three nodes exist in `Qt.Paths` with documented pins; geometry tests pass; help patches pass both checks; `tools/build.ps1 -Test -SkipNative` passes.

## Verification
`dotnet test src/VL.Qt.Tests`, `tools/build.ps1 -Test -SkipNative`, `vl-patch-check.py` and `vvvvc.ps1 -File` for both patches.

## Task cut (for the orchestrator)
| Task | Layer | Files | Model |
|---|---|---|---|
| T1 | C# geometry | `Paths/SplineGeometry.cs`, tests | Opus (numerics) |
| T2 | C# geometry | `Paths/BezierGeometry.cs`, `Paths/HandleMode.cs`, tests | Sonnet |
| T3 | C# nodes | `Nodes/Paths/BezierHandlesNode.cs`, `SplineCatmullRomNode.cs`, `SplineBSplineNode.cs`, tests | Sonnet |
| T4 | patches | two help patches, `help/Help.xml` | Sonnet (skills `vvvv-fileformat`, `vvvv-pad-layout`) |
| T5 | docs | `docs/shapes.md` | Sonnet |

T1 and T2 in parallel; T3 after both; T4 after T3; T5 parallel to T4.
