# Paths 04: `Qt.Paths` builder nodes and the `Path` element node

Milestone 1.0 · Labels: `enhancement`, `area:csharp`, `area:patch`, `manual-check` · Depends on: Paths 03 · Plan: `docs/plans/paths/04_path-builder.md`, overview `docs/plans/paths/00_overview.md`

## Goal
Build a `QtPath` step by step with nodes named and pinned like `Skia.Paths`, and draw it with a `Path` node in `Qt.Elements`.

## Context
- `QtPath` core (immutable chain, `Flatten`, `ContentEquals`, internal `MoveTo/LineTo/QuadTo/CubicTo/Close/Append`) exists from package 02 in `src/VL.Qt/Paths/QtPath.cs`; the `Shape` element takes a `QtPath` in `Path`.
- Skia reference: `C:\vl\VL.StandardLibs\VL.Skia\VL.Skia.vl` (node definitions) and the help patches in `C:\vl\VL.StandardLibs\VL.Skia\help\Topics\Paths\` (e.g. `Explanation An overview of path nodes.vl`). Skia nodes there: `MoveTo`, `LineTo`, `QuadTo`, `CubicTo`, `ConicTo`, `ArcTo`, `Close`, each with a separate `(Relative)` node; `AddRect`, `AddRoundRect`, `AddOval`, `AddCircle`, `AddArc`, `AddPoly`, `AddPath`, … The vvvv Skia nodes operate on a mutable `SKPath`; ours return a new immutable `QtPath`.
- Change detection: static methods would create a new `QtPath` every frame, so the chain would never be reference-stable. Builder nodes are `[ProcessNode]` classes that return the previous output when the input path reference and all parameters are unchanged.
- A static class with `[SkipCategory]` and `[Category("Qt.Style")]` is the existing pattern for a category on static nodes (`src/VL.Qt/Styles/StyleNodes.cs:12-14`); for process nodes the category is set in `[ProcessNode(Category = …)]`. Add `Categories.Paths = "Qt.Paths"` in `src/VL.Qt/Categories.cs` with a one-line summary.

## Specification
- Node set in `Qt.Paths`: `MoveTo`, `LineTo`, `QuadTo`, `CubicTo`, `ArcTo`, `Close`, and `MoveTo (Relative)`, `LineTo (Relative)`, `QuadTo (Relative)`, `CubicTo (Relative)`, `ArcTo (Relative)`; `AddRect`, `AddRoundRect`, `AddOval`, `AddCircle`, `AddArc`, `AddPoly`, `AddPath`. Not `ConicTo` (rejected).
- Pins: names, order, types, defaults and angle units **exactly as the Skia node of the same name** (read them from `VL.Skia.vl`), with `SKPath` replaced by `QtPath`, `SKRect` by `RectangleF` and `SKPoint` by `Vector2`. Where Skia has a pin Qt cannot honour, leave it out and say so in the PR. Every builder node has the input path as first pin (`Input`, default `QtPath.Empty`) and the new path as output (`Output`).
- How VL shows a node version `(Relative)` for a C# process node: check the skills `vvvv-custom-nodes` / `vvvv-node-libraries` and existing libraries (e.g. `[ProcessNode(Name = "LineTo (Relative)")]` or a version property); use what yields exactly the Skia display name.
- `ArcTo`/`AddArc`/`AddOval`/`AddCircle`/`AddRoundRect`: arcs become cubic Béziers in C# (split into segments of at most 90°, control distance `4/3 · tan(θ/4) · r`); QtNative knows only Move, Line, Quad, Cubic, Close. Unit tests: maximum radial error below 0.03 % of the radius.
- `AddPoly` takes `Spread<Vector2>` and `Close` like Skia; compare the spread by reference, then by content.
- `Path` node in `Qt.Elements` (`Nodes/PathNode.cs`, over `ShapeNodeCore` from package 03): pins `Path`, `Fill` (default transparent), `Stroke Color`, `Stroke Width` (default `0.05`), `Transform`, `Opacity`; optional `Cap`, `Join`, `Miter Limit`, `Dash`, `Dash Offset`, `Trim Start`, `Trim End`, `Trim Offset`, `Fill Rule`. Limit: if the flattened path does not fit (`Protocol.PathFits`), draw nothing and show a warning at the node with the counts.
- Docs: `/// <summary>` and `<param>` on every node and pin (Skia wording where it fits).

## Steps
- [ ] T1 Find out how the `(Relative)` version is declared; write the answer into the PR description; `Categories.Paths`.
- [ ] T2 `Paths/ArcGeometry.cs`: arc → cubics, with tests.
- [ ] T3 Builder nodes `Nodes/Paths/*.cs` (one file per node family, e.g. `LineToNode.cs` with the absolute and relative node), process nodes with change detection; tests: unchanged inputs → same output instance; a 10 000-step chain through repeated `Update` calls flattens without stack overflow.
- [ ] T4 `Add…` nodes with tests (bounds and point counts against expected values).
- [ ] T5 `Nodes/PathNode.cs` with tests (path limit warning).
- [ ] T6 Help patches `help/Qt/HowTo Build a Path.vl` (MoveTo → LineTo → CubicTo → Close chain, a relative variant, an `AddRoundRect`; drawn with `Path`) and `HowTo Draw a Path.vl` (fill, stroke, trim animated, fill rule); lines in `help/Help.xml`; `vl-patch-check.py` and `vvvvc.ps1` for each.
- [ ] T7 `docs/shapes.md`: section on paths (builder nodes, Skia correspondence, limits).

## Acceptance criteria
- Every listed node exists in `Qt.Paths` with the Skia name and pins; deviations are listed in the PR.
- Unit tests and `tools/build.ps1 -Test -SkipNative` pass; help patches pass both checks.

## Verification
`dotnet test src/VL.Qt.Tests`, `tools/build.ps1 -Test -SkipNative`, `python tools/vl-patch-check.py "help/Qt/HowTo Build a Path.vl"`, `tools/vvvvc.ps1 -File "help/Qt/HowTo Build a Path.vl"` (same for `HowTo Draw a Path.vl`).

## Task cut (for the orchestrator)
| Task | Layer | Files | Model |
|---|---|---|---|
| T1 | research | `Categories.cs`, PR note | Sonnet |
| T2 | C# geometry | `Paths/ArcGeometry.cs`, tests | Sonnet |
| T3 | C# nodes | `Nodes/Paths/*To*.cs`, `CloseNode.cs`, tests | Sonnet |
| T4 | C# nodes | `Nodes/Paths/Add*.cs`, tests | Sonnet |
| T5 | C# element node | `Nodes/PathNode.cs`, tests | Sonnet |
| T6 | patches | `help/Qt/HowTo Build a Path.vl`, `HowTo Draw a Path.vl`, `help/Help.xml` | Sonnet (skills `vvvv-fileformat`, `vvvv-pad-layout`) |
| T7 | docs | `docs/shapes.md` | Sonnet |

T1 first; T2 → T3/T4 in parallel; T5 parallel to T3; T6 after T3–T5; T7 parallel to T6.
