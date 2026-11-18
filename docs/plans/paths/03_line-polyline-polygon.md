# Paths 03: Nodes `Line`, `Polyline`, `Polygon` with help patches

Milestone 1.0 · Labels: `enhancement`, `area:csharp`, `area:patch`, `manual-check` · Depends on: Paths 02 · Plan: `docs/plans/paths/03_line-polyline-polygon.md`, overview `docs/plans/paths/00_overview.md`

## Goal
Three nodes in `Qt.Elements` over the `Shape` element from package 02, with documentation, tests and one help patch each.

## Context
- Template: `src/VL.Qt/Nodes/CircleNode.cs` (a node over a shared element and `Props`, reusing `RectangleNode.Create`), `RectangleNode.cs:168-242` (`Props` record struct, `Create`, `Update`, defaults as strings: `[DefaultValue("1, 1, 1, 1")] Color4`, the comment at :216-219), `ElementNode.cs` (`CommonInputs` :16-50, `Describe` :120-132 creates a new element only when inputs differ).
- `Spread<Vector2>` inside a record struct compares by reference; vvvv often creates a new spread with the same content every frame. Compare by reference, then by content (`MemoryMarshal.Cast` to `float` and `SequenceEqual`; cheap even at 8190 points).
- Geometry space (from the overview): points in the parent space, `Transform` around the origin, no `Anchor` and no `Size` pin; the node sets the element's `Size` to (`max(0, maxX)`, `max(0, maxY)`) of the points and `Anchor` to TopLeft.
- Limit: at most 8190 points (`Protocol.MaxF32ArrayCount / 2`). More: the node draws the first 8190 and shows a warning at the node ("Points: only the first 8190 of {Count} points are drawn."), through the existing warning helper of `ElementNode`.
- Defaults are chosen so the effect is visible (`docs/help-patches.md`): units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).

## Specification

| Node | Element pins (normal) | Optional pins | Defaults |
|---|---|---|---|
| `Line` | `From`, `To`, `Stroke Color`, `Stroke Width`, `Transform`, `Opacity` | `Cap`, `Dash`, `Dash Offset`, `Trim Start`, `Trim End`, `Trim Offset` | From `0.5, 0.5`, To `2.5, 1.5`, Stroke Color white, Stroke Width `0.05` |
| `Polyline` | `Points`, `Closed`, `Stroke Color`, `Stroke Width`, `Transform`, `Opacity` | `Cap`, `Join`, `Miter Limit`, `Dash`, `Dash Offset`, `Trim Start`, `Trim End`, `Trim Offset` | Points empty, Closed false, Stroke Width `0.05` |
| `Polygon` | `Points`, `Fill`, `Stroke Color`, `Stroke Width`, `Transform`, `Opacity` | `Join`, `Miter Limit`, `Dash`, `Dash Offset`, `Fill Rule` | Fill white, Stroke Width 0 (no stroke, as `Rectangle`) |

- Plus the common pins as on `Circle` (`Clip`, `Debug`, `Interaction`, `Effect` with the same visibility as there).
- Outputs as on `Rectangle`: `Output` (Element), measured size and scene bounds if `Rectangle` has them.
- `Polygon` is always closed; `Line` is a two-point polyline.
- Docs: `/// <summary>` per node, `<param>` per pin (the `Points` pin names the 8190 limit and that `Transform` applies around the origin; `Dash`: "lengths of dashes and gaps in units, alternating"; trim pins: "fraction of the path length"), `<returns>`.
- `Categories.Elements`; no new category in this package.

## Steps
- [ ] T1 `Nodes/ShapeNodeCore.cs`: shared `Props` record struct (with content equality for the spreads), static `Create`, bounds and limit handling; unit tests.
- [ ] T2 `Nodes/LineNode.cs`, `PolylineNode.cs`, `PolygonNode.cs` with docs and both constructors (NodeContext and the test constructor with `addWarning`, as `CircleNode`); node tests next to `RectangleNodeTests.cs` (same content → same element instance; 9000 points → warning, 8190 sent).
- [ ] T3 Help patches `help/Qt/HowTo Draw a Line.vl` (trim animated with an LFO: the line draws itself), `HowTo Draw a Polyline.vl` (zigzag, joins and caps visible, closed toggle), `HowTo Draw a Polygon.vl` (star-shaped points, fill + stroke, fill rule on a self-intersecting polygon); `Notes:` names the 8190 limit; lines in `help/Help.xml` (tags e.g. `line stroke shape`); `python tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File` for each.
- [ ] T4 Docs: new `docs/shapes.md` (user view: the elements, geometry space, limits, defaults like Skia), entry in `docs/README.md`; in `docs/decisions/element-catalog-v1.md` replace "shapes and paths" under out of scope with a link to `docs/decisions/paths.md`.

## Acceptance criteria
- The three nodes appear in `Qt.Elements` with documented pins; the help patches pass `vl-patch-check.py` and `vvvvc.ps1`.
- Unit tests and `tools/build.ps1 -Test` pass.
- PR description lists the help patches; the issue keeps `manual-check` until someone has opened them in vvvv.

## Verification
`dotnet test src/VL.Qt.Tests`, `tools/build.ps1 -Test -SkipNative`, `python tools/vl-patch-check.py "help/Qt/HowTo Draw a Line.vl"` (and the other two), `tools/vvvvc.ps1 -File "help/Qt/HowTo Draw a Line.vl"` (and the other two).

## Task cut (for the orchestrator)
| Task | Layer | Files | Model |
|---|---|---|---|
| T1 | C# core | `Nodes/ShapeNodeCore.cs`, tests | Sonnet |
| T2 | C# nodes | `Nodes/LineNode.cs`, `PolylineNode.cs`, `PolygonNode.cs`, tests | Sonnet |
| T3 | patches | `help/Qt/HowTo Draw a Line.vl`, `…Polyline.vl`, `…Polygon.vl`, `help/Help.xml` | Sonnet (skills `vvvv-fileformat`, `vvvv-pad-layout`) |
| T4 | docs | `docs/shapes.md`, `docs/README.md`, `docs/decisions/element-catalog-v1.md` | Sonnet |

T1 → T2 → T3; T4 in parallel to T2.
