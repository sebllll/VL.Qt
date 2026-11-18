# Shapes

`Line`, `Polyline` and `Polygon` (category `Qt.Elements`) draw strokes and filled outlines as vector shapes on Qt Quick Shapes. Edges are anti-aliased without MSAA, curves are exact, and moving, rotating or scaling a shape through `Transform` costs almost nothing, because the path stays untouched. `Path` draws curves built with the nodes of `Qt.Paths` (see [Paths](#paths) and [Generators](#generators)) and uses the same pins and rules.

## Elements

| Node | Draws | Element pins | Optional pins |
|---|---|---|---|
| `Line` | a straight line between two points | `From`, `To`, `Stroke Color`, `Stroke Width`, `Transform`, `Opacity` | `Cap`, `Dash`, `Dash Offset`, `Trim Start`, `Trim End`, `Trim Offset` |
| `Polyline` | a stroked chain of points, open or closed | `Points`, `Closed`, `Stroke Color`, `Stroke Width`, `Transform`, `Opacity` | `Cap`, `Join`, `Miter Limit`, `Dash`, `Dash Offset`, `Trim Start`, `Trim End`, `Trim Offset` |
| `Polygon` | a closed, filled outline with an optional stroke | `Points`, `Fill`, `Stroke Color`, `Stroke Width`, `Transform`, `Opacity` | `Join`, `Miter Limit`, `Dash`, `Dash Offset`, `Fill Rule` |

- The common pins (`Clip`, `Debug`, `Interaction`, `Effect`) and the outputs (`Output`, measured size, scene bounds) are the same as on `Rectangle` and `Circle`.
- `Line` is a two-point `Polyline`; `Polygon` is always closed.
- `Stroke Width` 0 draws no stroke. A `Line` or `Polyline` needs a width above 0 to be visible, a `Polygon` is filled by default and has no stroke.

## Geometry space

- Points are in the parent space, in units of the scene `Space` (like Skia's path layers); `0, 0` is the origin of the parent, not a corner of the shape.
- `Transform` applies around the origin of the parent, not around the center of the shape. To rotate or scale a shape around its own center, build the points around `0, 0` and move them with `Transform` (translation).
- There is no `Anchor` and no `Size` pin. The size of the element is the extent of its geometry (`max(0, maxX)` by `max(0, maxY)` of the points). Row, Column and the tooltip preview use it; coordinates to the left of or above the origin do not enlarge it.

## Stroke and fill

Defaults follow Skia, so Skia patches keep their look:

| Pin | Values | Default |
|---|---|---|
| `Cap` | `Flat` (Skia Butt), `Square`, `Round` | `Flat` |
| `Join` | `Miter`, `Bevel`, `Round` | `Miter` |
| `Miter Limit` | Float; Qt only knows whole numbers, so QtNative rounds it | 4 |
| `Fill Rule` | `NonZero` (Skia Winding), `EvenOdd` | `NonZero` |

- **Dash** takes lengths of dashes and gaps in units, alternating; `Dash Offset` shifts the pattern along the line. An empty `Dash` draws a solid stroke.
- **Trim** takes fractions of the path length: `Trim Start` 0 and `Trim End` 1 draw everything, `Trim Offset` shifts the window along the path. Animating `Trim End` lets a line draw itself.
- The renderer is Qt's `CurveRenderer`; there is no pin to choose another one.

## Paths

`Path` (category `Qt.Elements`) draws a path built with the nodes of `Qt.Paths`. A path is an immutable value (`QtPath`); each builder node takes a path and returns a new one, so the nodes chain from `MoveTo` to the `Path` element.

| Pin | Values | Default |
|---|---|---|
| `Path` | the path to draw | none (draws nothing) |
| `Transform` | as on the other shapes | identity |
| `Fill` | fill color of the enclosed area | transparent |
| `Stroke Color`, `Stroke Width` | as on `Line` | white, 0.05 |

The optional pins (`Cap`, `Join`, `Miter Limit`, `Dash`, `Dash Offset`, `Trim Start`, `Trim End`, `Trim Offset`, `Fill Rule`, `Opacity`, …) work as described under [Stroke and fill](#stroke-and-fill).

**Builder nodes** in `Qt.Paths` (the first pin is `Input`, the path so far; the output is `Output`):

| Nodes | Pins besides `Input` |
|---|---|
| `MoveTo`, `LineTo` | `Point` |
| `QuadTo` | `Control Point`, `End Point` |
| `CubicTo` | `Control Point 1`, `Control Point 2`, `EndPoint` |
| `Close` | none |
| `ArcTo` (tangent) | `Corner`, `Destination Point`, `Corner Radius` |
| `ArcTo` (oval) | `Bounds`, `Start Angle`, `Sweep Angle`, `Force Move To` |
| `ArcTo` (SVG) | `Radius`, `X Axis Rotate`, `Large Arc`, `Sweep`, `Destination Point` |
| `AddRect`, `AddRoundRect`, `AddOval`, `AddCircle`, `AddArc`, `AddPoly`, `AddPath` | the shape (`Rectangle`, `Radius`, `Bounds`, `Center`, `Points`, `Other`, …) and, as in Skia, `Direction` (`PathDirection`) or `Mode` (`PathAddMode`) |

- `MoveTo`, `LineTo`, `QuadTo` and `CubicTo` have a `(Relative)` variant that takes its points relative to the current point; of `ArcTo` only the SVG form has one. `Close` and the `Add…` nodes have none (as in Skia).
- `AddPath` appends another path; `AddPoly` takes a `Spread<Vector2>` and an optional close.

**Skia correspondence.** The nodes in `Qt.Paths` have the names and pins of `Skia.Paths`, so Skia patches carry over. Differences:

- A `QtPath` is immutable: each node returns a new path and leaves its input unchanged. An unconnected `Input` is an empty path.
- There is no `ConicTo`; Qt has no conics.
- `PathDirection` and `PathAddMode` replace `SKPathDirection` and `SKPathAddMode`.
- Angles are in cycles (1 is a full turn), as on Skia's nodes.
- Queries and operations (`Op`, `Simplify`, sampling) are not part of `Qt.Paths`.

**Limits.** A path that does not fit one command to QtNative (about 7270 line segments) is not drawn; `Path` shows a warning. Arcs and ovals become cubics and count as such.

Help patches: `HowTo Build a Path` (the builder nodes), `HowTo Draw a Path` (the element).

## Generators

Three nodes in `Qt.Paths` create curves from spreads of points. Like the builder nodes they take a path (`Input`, default empty) and return the extended path (`Output`) with a new contour; `Path` draws it. All three produce exact cubics, so the `CurveRenderer` draws them without flattening.

| Node | Pins besides `Input` | Curve |
|---|---|---|
| `Bezier (Handles)` | `Anchors`, `In Handles`, `Out Handles`, `Closed`; optional `Handle Modes` | through every anchor, shaped by handles |
| `Spline (CatmullRom)` | `Points`, `Closed`, `Alpha`, `Tension` | through every point |
| `Spline (BSpline)` | `Points`, `Closed` | along the control polygon, not through the inner points |

**`Bezier (Handles)`.** Handles are relative to their anchor. The segment from anchor *i* to anchor *i+1* is a cubic with the controls *anchor i + out i* and *anchor i+1 + in i+1*; `Closed` adds the segment back to anchor 0 and a `Close`. A missing handle (a spread shorter than `Anchors`) counts as zero, so the anchor is a corner.

`Handle Modes` (`Spread<HandleMode>`) sets how the in handle of each anchor follows its out handle. The out handle always leads. The modes apply cyclically like any spread; an empty spread means `Free` for all.

| `HandleMode` | In handle |
|---|---|
| `Free` | as given |
| `Mirrored` | the out handle reversed; `In Handles` is ignored |
| `Aligned` | points opposite to the out handle and keeps the length of the given in handle (as given when the out handle is zero) |

**`Spline (CatmullRom)`.** Passes through every point, one cubic per span.

- `Alpha` sets the spacing of the knots: 0.5 (default) is centripetal and draws no loops or cusps; 0 is uniform, 1 chordal. Values outside 0 to 1 are clamped.
- `Tension` scales the tangents: 0 (default) is the plain spline, 1 gives straight lines.
- An open curve has tangent 0 at its end points, for every `Alpha` (as `curveCatmullRom` in d3). This differs from the classic (P2 − P1) / 2 at `Alpha` 0 on purpose: the end of the curve then does not jump between `Alpha` 0 and a value just above it.
- Coincident points do not break the curve: spans of length zero are skipped. Points that all coincide leave `Input` unchanged.

**`Spline (BSpline)`.** `Points` is the control polygon; the uniform cubic B-spline follows it and stays inside its convex hull, smoother than Catmull-Rom but not through the inner points. Open, the curve is clamped: it starts at the first and ends at the last point, tangent to the first and last edge of the polygon. Closed, it is periodic, with one cubic per point.

**Few points.** With fewer than two points (anchors) all three return `Input` unchanged. An open spline with two points is a straight line.

**Limits.** The limit of the `Path` element (about 7270 line segments) applies to the result: every span is one cubic, and a closed curve counts its closing segment.

Help patches: `HowTo Draw a Bezier Curve with Handles` (handles and modes), `HowTo Draw Splines` (Catmull-Rom with `Alpha` 0, 0.5 and 1, and B-spline, with the control polygon).

## Limits

- **At most 8190 points per element.** More points are cut off at the 8190th, and the node shows a warning. The limit comes from the size of one command to QtNative.
- **About 4000 points** for a polyline whose points change every frame. Above that the frame time grows steeply (measured on a loaded arm64 notebook: 1.3 ms at 2000 points, 5.3 ms at 4000, 16 ms at 8190). Static shapes and shapes that only move through `Transform` use the full 8190 points at no cost.
- Prefer `Transform`, `Trim` and `Opacity` to changing points for animation; animated trim costs 2.5 ms at 8190 points, 0.3 ms at 100.
- Many small shapes are fine: 100 polylines of 100 points, each rotated every frame, take 0.3 ms.

Numbers and method: [research/02_shapes-spike.md](research/02_shapes-spike.md). Design: [decisions/paths.md](decisions/paths.md).

## Help patches

`HowTo Draw a Line`, `HowTo Draw a Polyline`, `HowTo Draw a Polygon`, `HowTo Draw a Path`, `HowTo Build a Path`, `HowTo Draw a Bezier Curve with Handles`, `HowTo Draw Splines`. See [Help patches](help-patches.md).
