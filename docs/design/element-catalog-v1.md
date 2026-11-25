# Element Model: Geometry, Space and Pins

Conventions shared by all elements: geometry pins, coordinate space, colours, image sources and pin design.

## Design
### Pins
- Each node has a fixed, typed set of pins with sensible defaults. There is no generic `Properties` pin. Reason: typed pins keep nodes discoverable in vvvv.
- Pins common to all elements: Size, Transform, Anchor, Opacity, Clip, Debug, Interaction, Effect and, as the last input pin, the optional Enabled. Children exist only on Group and Row/Column.
- Colours use Stride `Color4`, so `RGBA`/`HSVA` nodes connect directly.

### Geometry
- **`Size`** (Stride `Vector2`, units), **`Transform`** (Stride `Matrix`, 4×4) and **`Anchor`** (vvvv enum `RectangleAnchor`, default TopLeft). The vvvv transform nodes plug in directly, and the anchor points are the familiar ones.
- `Size` is the layout value; the position comes from `Transform`. A point v of the element lands at (v − anchor) · `Transform` in its parent, as in VL.Skia. Inside Row/Column the layout places the element.
- `Anchor` is the pivot of `Transform`: rotation and scale act around it. The reconciler folds the anchor in, because Qt matrix transforms rotate around the top-left corner. This is why there are no separate rotation, scale or transform-origin pins.
- There is no `Visible` pin. The optional `Enabled` pin (default true, the last input pin, as on a VL.Skia layer) removes the element from the scene, which also stops its input; in Row/Column it takes no space and the siblings move up. `Opacity` 0 still costs, still takes input and keeps the gap. The widgets do not have it yet: their `Enabled` pin greys the control out (Qt's `enabled`), and a second pin of the same name is not possible.
- The transform is a pin on the element rather than a set of transform wrapper nodes. Qt supports it natively per item, so no wrapper items are needed, and the type is compatible with the vvvv transform nodes.

### Coordinate space
- The `Space` pin offers the four values of Skia's `CommonSpace`. y points down in all of them (Skia 2D, not Stride's y-up). `W`/`H` = size of the scene in DIP (Qt's logical pixels), `dpr` = device pixel ratio of the window:

  | Space | Origin | DIP per unit |
  |---|---|---|
  | `Normalized` | center | `H / 2` (height −1 top … +1 bottom, width ±`W / H`) |
  | `DIP` | center | `100` |
  | `DIPTopLeft` (default) | top left | `100` |
  | `PixelTopLeft` | top left | `100 / dpr` (= 100 device pixels, independent of the DPI scale) |

  Offscreen scenes (`QtTexture`, `QtRenderer`) have dpr 1 (1 DIP = 1 pixel), so `PixelTopLeft` equals `DIPTopLeft` there; `W`/`H` are the texture size or the viewport.
- **Decision (plan B, #143):** C# converts every length with the scale of the scene (`SpaceMapping`, DIP per unit), QtNative only moves the origin of the root item (top left, or the center of the scene for `Normalized` and `DIP`). Rejected: one scaled root item (plan A). Qt renders effect layers at item size × dpr without the scale of an ancestor, so they become blurry (the spike of #143, T0), and `ToFontPixels` rounds to root pixels instead of screen pixels. With plan B the elements, the reconciler diff and the layers always work in logical pixels. The scale changes with `Normalized` (the scene height) and `PixelTopLeft` (dpr), which rewrites the tree once.
- The `Space` pin sits on both renderers (`Renderer [Qt]` and `QtRenderer [Stride]`) and on `QtTexture`, not as a node in the tree. A scene has one space, which keeps the reconciler simple.
- The enum mirrors the values of Skia's `CommonSpace` without depending on VL.Skia: that enum lives in `VL.Core.Skia`, and Stride's `CommonSpace` has different semantics.

### Text
- A single style per node; no rich text and no spans.

### Images
- Image content comes from an image handle, not from a file path on the element. Loading on a path change stalls the frame, and Qt's `asynchronous` mode still stutters with large images. One route gives one behaviour. Redirecting a path internally to asynchronous loading was rejected because it hides delayed behaviour.

## Out of scope / possible extensions
- Anchors: they need references to sibling elements, which is hard to express as dataflow.
- Grid layouts and gradients. Shapes and paths: see [paths.md](../decisions/paths.md).
- Generic property access, possibly through a node factory that creates pins from Qt metadata.

## Rejected alternatives
- Transform nodes in the Skia style that wrap elements.
- Generic pins only: no pin types and poor discoverability in vvvv.
