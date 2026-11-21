# Lines, Paths and Splines (brainstorm)

Status: planned, see [plans/paths/00_overview.md](../plans/paths/00_overview.md). Shapes and paths were out of scope in [element-catalog-v1.md](../design/element-catalog-v1.md).

## Decided
- **Both use cases are in scope from the start** (2026-11-18): motion graphics (few paths, animated trim, crisp edges) and data-heavy content (many points that change every frame). Categories and aspects (Advanced, Experimental) are designed together with the node set.
- **Bézier paths in several variants are in scope** (2026-11-18), among them anchors with handles, besides splines through points.
- **Scene content keeps the one route C# → reconciler → command writer → protocol → QtNative** (2026-11-18). A path on an element is plain immutable C# data (points, segments), sent as a property value; no native handles in element descriptions.
- **First step: `Line`, `Polyline` and `Polygon` on Qt Quick Shapes** (2026-11-18): one element type (`Shape` + `ShapePath` + the custom path element below; refined in the plan: points go through the custom element too, because it is needed for curves anyway; one route and one place for `Close`, see [research/02_shapes-spike.md](../research/02_shapes-spike.md)), 2 or n points, `Polygon` closed and filled. Pins: `Points`, `Stroke Color`, `Stroke Width`, `Closed` (Polyline), `Fill` (Polygon); optional `Cap`, `Join`, `Dash`, `Trim`. Needs `QuickShapes` linked and deployed and a protocol value type for point lists (`u32 count` + `f32` pairs). Help patch included.
- **No limit on the number of points from the protocol** (2026-11-18 limit of about 8000 points, lifted 2026-11-21): the command header carries a `u32` `payloadLen` (at most 64 MB), so nothing is cut. For points that change every frame the cost is the limit (spike: 5.3 ms per frame with the `CurveRenderer` on arm64 at 4000 points, 16 ms at 8190 points); static or only transformed shapes can have many more.
- **Curves reach Qt through a custom path element** (2026-11-18): a subclass of the private `QQuickCurve` (as Qt's own `PathPolyline`) receives a binary array of verbs and points (Move, Line, Quad, Cubic, Close) and builds the path in `addToPath`. A spike first confirms that the `CurveRenderer` still draws its cubics exactly.
- **Two ways to build a path** (2026-11-18): from a `Spread<Vector2>` (generators such as `Bezier (Handles)` and the `Spline` variants) and step by step, each node taking a path and returning a new one. The path value is an immutable chain (each step references the previous path), so appending costs O(1), also in loops with an accumulator; the element flattens the chain once into the verb array. Arcs become cubics in C#, so QtNative knows only five verbs.
- **The builder nodes in `Qt.Paths` follow `Skia.Paths`** (2026-11-18): same names and pins (`MoveTo`, `LineTo`, `QuadTo`, `CubicTo`, `ArcTo`, `Close`, the `Add…` nodes such as `AddRect`, `AddOval`, `AddCircle`, `AddPoly`, `AddPath`, and `FromSVGPathData`), relative variants as separate `(Relative)` nodes as in Skia. Patchers know them already. Queries and operations (`Op`, `Simplify`, `SamplePath`, …) are not rebuilt.
- **Adapter `SKPath` → Qt path in an optional assembly `VL.Qt.Skia`, milestone 2.0** (2026-11-18), like `VL.Qt.Stride`. The conversion runs in C# and produces the plain path value, so the protocol route stays intact. It brings Skia's path math (boolean operations, sampling, simplify, SVG) and is the preferred route for path math. `SKPath` is mutable, so the adapter converts each frame or compares content.

- **Miter limit: pin `float`, QtNative rounds to an integer** (2026-11-18): `QQuickShapePath::miterLimit` is an `int` (spike Q6); the default 4 is exact, fractional values are rounded.
- **First version renders with the `CurveRenderer` only; the `GeometryRenderer` follows in milestone 2.0** (2026-11-18). A benchmark in the spike measures where the `CurveRenderer` reaches its limits (points changed per frame, animated trim, many elements); its numbers go into the tooltips and decide whether a custom `QSGGeometryNode` element is needed.
- **In 2.0 the renderer is a pin on the same nodes** (2026-11-18): Advanced pin `Renderer`, default `Curve`, so existing patches keep working; pins only the `GeometryRenderer` uses (`Asynchronous`) come as optional pins. Rejected: a category per renderer (doubled nodes and help patches, switching means re-patching).

- **Node set of the first version, milestone 1.0** (2026-11-18):

  | Category | Nodes | Aspect |
  |---|---|---|
  | `Qt.Elements` | `Line`, `Polyline`, `Polygon`, `Path` | normal |
  | `Qt.Paths` (new) | `MoveTo`, `LineTo`, `QuadTo`, `CubicTo`, `ArcTo`, `Close`, each with its `(Relative)` node | normal |
  | `Qt.Paths` | `AddRect`, `AddRoundRect`, `AddOval`, `AddCircle`, `AddArc`, `AddPoly`, `AddPath` | normal |
  | `Qt.Paths` | `Bezier (Handles)`, `Spline (CatmullRom)`, `Spline (BSpline)` | normal; handle mode per anchor as an Advanced pin |

  Element pins: normal `Points` or `Path`, `Stroke Color`, `Stroke Width`, `Fill` (Polygon, Path), plus `Transform`, `Anchor`, `Opacity` as on all elements; optional `Cap`, `Join`, `Miter Limit`, `Dash`, `Dash Offset`, `Trim Start`/`End`/`Offset`, `Fill Rule` (Path).
- **Generators: end tangents of `Spline (CatmullRom)` and handle modes of `Bezier (Handles)`** (2026-11-18): an open Catmull-Rom curve has tangent 0 at its end points for every `Alpha`, as `curveCatmullRom` in d3. At `Alpha` 0 this differs from the classic end tangent (P2 − P1) / 2; with it, the end of the curve would jump between `Alpha` 0 and a value just above it. `Handle Modes` apply cyclically like any spread (anchor *i* uses entry *i* mod count, empty = `Free`); `Mirrored` ignores the given in handle.
- **Later (2.0):** `FromSVGPathData` (through the adapter or a parser of its own), `AddStar`, `AddSpiral`, `AddArrow`, `AddGrid`, the `GeometryRenderer`, the Skia adapter.

## Open
- Mass data: how the renderers scale above about 8000 points that change every frame (a cost question, no size limit).
- Gradient fills (Qt Shapes: linear, radial, conical, `fillItem`): its own topic, see [../topics/gradients.md](../topics/gradients.md).

## Rejected
- Path value as a C# handle around a native `QPainterPath` (2026-11-18): it bypasses the reconciler and protocol route. Path math on `QPainterPath` falls behind the Skia adapter; it comes back only if the adapter does not suffice.
- "No converter from `SKPath`" (2026-11-18, reversed the same day): the conversion runs in C#, so the objection of a second route does not apply; see the adapter above.
- `Relative` as a pin on `LineTo` and the others (2026-11-18, reversed the same day): separate `(Relative)` nodes as in Skia.
- `ConicTo` (2026-11-18): conics exist only in Skia; Qt has none. The adapter converts them to quads.
- `PathSvg` for curves (2026-11-18): parsed at each change. One `PathCubic` QObject per segment: slow.
- `Line` as a rotated `Rectangle` (2026-11-18): batches well, but no joins, dash, trim or polylines.
- QML `Canvas` and `QQuickPaintedItem` for animated content: they raster on the CPU at every change.
