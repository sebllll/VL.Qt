# Element model

What an element is in VL.Qt: an immutable description made by a vvvv node, the pins every element node shares, how geometry and the coordinate space work, and where state lives that a description does not hold. The code is `src/VL.Qt/Elements/` (the descriptions), `src/VL.Qt/Nodes/` (`ElementNodeCore`, `CommonInputs`, the element nodes) and `src/VL.Qt/Reconciliation/` (the diff). The term list is in [../glossary.md](../glossary.md).

## Context

vvvv patches run in dataflow on the main thread; Qt objects, the render thread and the GPU resources live in QtNative. A patcher must not deal with lifetimes or resources, and the nodes must stay discoverable in the node browser. So the patch only describes the scene, and a reconciler (see [reconciler.md](reconciler.md)) turns the descriptions into Qt items.

## Design

### Immutable descriptions
**vvvv describes, Qt works.** Element nodes output `IElement`; the concrete element classes (`Rectangle`, `Group`, …) are internal immutable records (`init` properties). Qt objects, GPU resources and runtime state belong to the reconciler or to the node that created them. So nothing needs disposing, and a description can be passed around freely and hung into the tree several times.

- Change detection happens in the node: `ElementNodeCore` keeps the last description and builds a new one only if `CommonInputs` or the element-specific inputs changed (value equality; the `Interaction` and `Effect` chains by reference).
- The reconciler uses reference equality as a shortcut: a reference-equal description means the subtree is unchanged ([reconciler-identity.md](reconciler-identity.md)). The diff and the knowledge of the wire protocol live in `Reconciliation/`, not in the element records.

### Deliberate exceptions
Some state exists only in Qt and changes without a new description arriving:

- Video position.
- Interaction state: hover, pressed, focus, text field input. It flows back through feedback or channels ([events.md](events.md)).
- `QtImage` as a handle: its loading state changes. It belongs to the node that created it and needs no dispose.

The same instance therefore means "the same description", not "the same picture". Code that must detect visual change, for example a tooltip that follows an interaction state, cannot rely on reference equality alone. New features are measured against the principle: an exception needs a reason and must not impose lifetime duties on the patcher.

### Pins
- Each node has a fixed, typed set of pins with defaults that show an effect. There is no generic `Properties` pin: typed pins keep nodes discoverable in vvvv.
- The pins every element node shares (`CommonInputs`), in this order:

  | Pin | Type, default | Why |
  |---|---|---|
  | `Size` | `Vector2`, `1, 1` | The layout value of the element, in units. Optional and after `Auto Size` where that is on by default (containers, widgets). |
  | `Transform` | `Matrix`, identity | Vvvv's transform nodes plug in directly. |
  | `Anchor` | `RectangleAnchor`, `TopLeft` | The point of the element that lies at the origin of its parent, and the pivot of `Transform`. |
  | `Opacity` | `float`, 1 | Of the element including its children. |
  | `Clip` | `bool`, false | Clips the children to the frame of the element. |
  | `Debug` | `bool`, false | Draws a frame at the element. |
  | `Interaction` | chain, none | Input handlers. Connect a node, never an IOBox: an IOBox makes the element opaque to input. |
  | `Effect` | chain, none | Effects such as blur or shaders. |
  | `Enabled` | `bool`, true | Last input pin, as on a VL.Skia layer. |

- Geometry-defining pins come first (`Size`, `Radius`, points, a path), then `Transform` and `Anchor`, then content and look, then `Opacity`, `Clip`, `Debug`, `Interaction`, `Effect` and, on containers and widgets, `Style`, and last `Enabled`. A node that fixes a common pin does not show it (the shape nodes have a fixed top-left anchor and no `Size`). Outputs: `Measured Size` and `Scene Bounds` (both optional, from the last frame, in units) and `Output`. The pin lists are in `src/VL.Qt.Tests/Api/node-api.txt`.
- Children exist only on `Group`, `Row` and `Column` (and their `(Spectral)` variants), as the collection pin `Input`.
- There is no `Visible` pin. `Enabled` off removes the element from the scene: it is not drawn, takes no input and, in Row/Column, takes no space (the siblings move up), while it keeps its state. `Opacity` 0 only hides it: it still takes input and keeps its gap. The widgets have an `Enabled` pin of their own that greys the control out (Qt's `enabled`, no clicks); a second pin of that name is not possible.
- Colours are Stride `Color4`, so `RGBA`/`HSVA` nodes connect directly.
- Image content comes from an image handle (`QtImage`), not from a file path on the element: loading on a path change stalls the frame, and Qt's asynchronous mode still stutters with large images. One route gives one behaviour.
- Text has a single style per node; no rich text and no spans.

### Geometry
- `Transform` is a pin on the element, not a set of wrapper nodes: Qt supports a matrix per item, so no wrapper items are needed.
- A point v of the element lands at (v − anchor) · `Transform` in its parent, as in VL.Skia. In Row/Column the layout owns the position and `Anchor` is only the pivot of `Transform`.
- QtNative resolves the anchor against the actual size of the item, so it follows `Auto Size`, layout and `Size` changes without a resend. `PropertyDiff` sends it separately from the transform. Rotation and scale act around the anchor because Qt's matrix transforms would otherwise act around the top-left corner; that is why there are no pins for rotation, scale or transform origin.

### Coordinate space
- The `Space` pin offers the four values of Skia's `CommonSpace`. y points down in all of them (Skia 2D, not Stride's y-up). `W`/`H` = size of the scene in DIP (Qt's logical pixels), `dpr` = device pixel ratio of the window:

  | Space | Origin | DIP per unit |
  |---|---|---|
  | `Normalized` | center | `H / 2` (height −1 top … +1 bottom, width ±`W / H`) |
  | `DIP` | center | `100` |
  | `DIPTopLeft` (default) | top left | `100` |
  | `PixelTopLeft` | top left | `100 / dpr` (= 100 device pixels, independent of the DPI scale) |

  Offscreen scenes (`QtTexture`, `QtRenderer`) have dpr 1 (1 DIP = 1 pixel), so `PixelTopLeft` equals `DIPTopLeft` there; `W`/`H` are the texture size or the viewport. The table stays here because no user page for coordinates exists yet.
- C# converts every length with the scale of the scene (`SpaceMapping.UnitScale`); QtNative only moves the origin of the root item (top left, or the center of the scene for `Normalized` and `DIP`). Because of that, elements, the diff and the layers always work in logical pixels. The scale changes with `Normalized` (the scene height) and `PixelTopLeft` (dpr), which rewrites the tree once.
- The `Space` pin sits on both renderers (`Renderer [Qt]` and `QtRenderer [Stride]`) and on `QtTexture`, not as a node in the tree: a scene has one space, which keeps the reconciler simple.
- The enum mirrors the values of Skia's `CommonSpace` without depending on VL.Skia: that enum lives in `VL.Core.Skia`, and Stride's `CommonSpace` has different semantics.

## Consequences

- A description holds no lifetime, so patches can share and re-use it freely. Code that wants to detect "looks different" must not compare references alone (video, interaction state, image loading).
- A new element node takes the common pins from `CommonInputs` in the order above and appears in the node API snapshot; `tools/vl-patch-check.py` guards `Interaction` against IOBoxes.
- Changing `Space` rewrites the whole tree once, because every length is converted in C#.
- Qt state that must reach the patch goes through feedback or channels, one frame late ([events.md](events.md)).

## Alternatives considered

- **One scaled root item for the space:** Qt renders effect layers at item size × dpr without the scale of an ancestor, so they become blurry, and `ToFontPixels` would round to root pixels instead of screen pixels.
- **Transform nodes in the Skia style that wrap elements:** Qt has a matrix per item, and the pin keeps the patch flat.
- **Generic pins only (a `Properties` pin):** no pin types and poor discoverability in vvvv.
- **Mirroring Qt state into the description,** so that "same instance" would mean "same picture": it would copy video position, hover state, text input and loading state into C# every frame, for no gain outside a few special cases, and it would blur the line between description and runtime state.
- **Redirecting a file path to asynchronous image loading inside the element:** it hides delayed behaviour; the handle gives one route.

## Open questions

- Anchors between sibling elements (layout relations) need references between elements, which is hard to express as dataflow; no public issue exists.
- Grid layouts and gradient fills are not covered.
- Generic property access, possibly through a node factory that creates pins from Qt metadata.

## Evidence

Pins and defaults: `src/VL.Qt.Tests/Api/node-api.txt`, checked by the node API test. The space scales: `SpaceMappingTests`. The anchor rule: "Geometry" at the property ids in `src/VL.Qt.Native/protocol.h`. Scaling in C# instead of a scaled root was decided after a spike in which effect layers under a scaled root were blurry.
