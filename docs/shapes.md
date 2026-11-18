# Shapes

`Line`, `Polyline` and `Polygon` (category `Qt.Elements`) draw strokes and filled outlines as vector shapes on Qt Quick Shapes. Edges are anti-aliased without MSAA, curves are exact, and moving, rotating or scaling a shape through `Transform` costs almost nothing, because the path stays untouched. `Path` (curves, built with the nodes of `Qt.Paths`) follows with package 04 and uses the same pins and rules.

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

## Limits

- **At most 8190 points per element.** More points are cut off at the 8190th, and the node shows a warning. The limit comes from the size of one command to QtNative.
- **About 4000 points** for a polyline whose points change every frame. Above that the frame time grows steeply (measured on a loaded arm64 notebook: 1.3 ms at 2000 points, 5.3 ms at 4000, 16 ms at 8190). Static shapes and shapes that only move through `Transform` use the full 8190 points at no cost.
- Prefer `Transform`, `Trim` and `Opacity` to changing points for animation; animated trim costs 2.5 ms at 8190 points, 0.3 ms at 100.
- Many small shapes are fine: 100 polylines of 100 points, each rotated every frame, take 0.3 ms.

Numbers and method: [research/02_shapes-spike.md](research/02_shapes-spike.md). Design: [decisions/paths.md](decisions/paths.md).

## Help patches

`HowTo Draw a Line`, `HowTo Draw a Polyline`, `HowTo Draw a Polygon`. See [Help patches](help-patches.md).
