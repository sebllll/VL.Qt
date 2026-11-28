# Node set

Which parts of Qt Quick VL.Qt offers as vvvv nodes, which it leaves to user QML, which it does not offer, and by what rules a new node is judged. The nodes are in `src/VL.Qt/Nodes/` (and `src/VL.Qt.Stride/`, `src/VL.Qt.Audio/`), their pins in the snapshot `src/VL.Qt.Tests/Api/node-api.txt`, the examples in `help/Qt/`. The user's view of the nodes is [help-patches.md](../help-patches.md).

## Context

VL.Qt is made for three use cases of media scenography: motion graphics, typography and layout, plus interactive UI on top of them. A patch builds an element tree each frame ([element-model.md](element-model.md)); Qt Quick is the renderer, not the model of the application.

Qt Quick offers far more than a patch needs as a node: visual items, controls, an animation system with states and transitions, models and views, particles, a canvas, 3D and charts. Wrapping all of it would give a node browser nobody can search, and a large part of it assumes that QML owns the state and the time, which is the opposite of a vvvv patch. So the node set is a selection, and the selection needs rules. The libraries a patcher knows set the expectations. VL.Skia has layers as one output type, a group with a spread input, a `Transform` pin on every layer and `Enabled` as the last pin. VL.Stride is the host of the companion package `VL.Qt.Stride`. A patcher who knows them must find the same pin positions and names here.

## Design

### Rules for a node

A Qt feature becomes a node when all of these hold:

- **The patch can describe it each frame.** It has typed pins with defaults that show an effect, and the node outputs an immutable value (an element, an effect, a style, a path, a handle). A generic `Properties` pin does not count ([element-model.md](element-model.md#pins)).
- **The patch owns the state and the time.** A feature whose state lives in Qt and moves by itself (a QML `State`, a `Transition`, a `Behavior`, a running `NumberAnimation`) is no node. Values that change over time come from vvvv (`IFrameClock`, an animator node), because only then they scrub and pause with the patch ([timing.md](timing.md)). What must come back from Qt (hover, pressed, value of a widget) comes back through feedback and channels ([events-and-interaction.md](events-and-interaction.md)).
- **The tree stays flat.** A container takes a spread of elements, with a `Spectral` variant where a layout needs one. Nodes that wrap one element to move it (transform nodes in the Skia style) are not offered; every element has `Transform`, `Anchor`, `Opacity`, `Clip` and `Enabled` instead.
- **It is a thing of one type.** The same word means the same everywhere ([glossary.md](../glossary.md)): Qt's item is an element, and an effect is only what hangs at the `Effect` pin.
- **It is not cheaper as a file.** A feature that one `.qml` file or one `.frag` file expresses with named pins is left to the node factories ([user-qml.md](user-qml.md), [effects.md](effects.md)), so that a user can add it without a new VL.Qt release.

### What Qt Quick has and what VL.Qt does with it

| Area of Qt Quick | In VL.Qt | Why |
|---|---|---|
| Rectangle, rounded corners, border | `Rectangle`, `Circle` | the basic shapes of layout |
| Text, fonts | `Text` with font, wrap, elide, autofit, `measuredSize`; `LoadFont` | typography is a use case; measuring comes back as pins |
| Image, video | `Image`, `Video` with `QtImage` handles, loader and converter nodes | resources need one route with a state ([images.md](../images.md)) |
| Shapes (paths, lines, curves) | `Line`, `Polyline`, `Polygon`, `Path`, the `Qt.Paths` chain, Bezier and spline nodes | needed for drawing; [paths.md](paths.md) |
| Positioners and layouts | `Row`, `Column`, their `Spectral` variants | alignment is computed in QtNative |
| Grid, Flow | not yet | gap that patches hit, see [#445](https://github.com/sebllll/VL.Qt/issues/445) |
| Repeater, ListView, GridView, models | none | a spread of elements is the model; virtualised views would hide which elements exist |
| Anchors between items | none | references between elements are hard to express as dataflow |
| Controls: Button, Switch, Slider, TextField, SpinBox | `Button`, `Toggle`, `Slider (Float)`, `Input (String)`, `Input (Int)`, `Style` pin, `Set …` nodes | the widgets of media and kiosk UIs ([styles.md](styles.md)) |
| Controls: ComboBox, RadioButton, ProgressBar, TextArea, Dial | not yet | added one by one when a patch needs them, see [#446](https://github.com/sebllll/VL.Qt/issues/446); until then `IStylable` or user QML |
| Dialogs, popups, menus, tab bars, scroll views | none | they bring their own windows or navigation state |
| Pointer, hover, tap, drag, pinch, wheel, touch handlers | `Interaction` chain: `Click`, `Hover`, `Drag`, `Pinch`, `Wheel`, `Point`, `TouchPoints` | the patch needs the values; [input.md](input.md) |
| Keyboard (`Keys`, focus) | only in widgets and user QML | gap, see [#448](https://github.com/sebllll/VL.Qt/issues/448) |
| Styled and rich text | none, one style per `Text` | gap, see [#447](https://github.com/sebllll/VL.Qt/issues/447) |
| Layer effects (`MultiEffect`), `ShaderEffect` | the `Effect` chain, `ShaderEffect`, shader nodes from `.frag` files | effects are chain steps, not wrapper elements ([effects.md](effects.md)) |
| Animation, `Behavior`, `State`, `Transition` | no nodes; animations in user QML follow `IFrameClock` | the patch owns the time (rule above) |
| Particles, Canvas, Qt Quick 3D, Charts | none | outside the use cases; Canvas needs JavaScript drawing code, Particles and 3D carry their own simulation and clock |
| Anything else in QML | `QmlElement` and nodes from `qml/` files | the escape hatch, one leaf in the tree |

### Layers of the set

The set has five layers, and a new node goes into the one it belongs to:

1. **Elements** describe what is drawn: shapes, text, image, video, containers.
2. **Chains** hang at a pin of an element and are ordered: `Effect` (effects, shaders) and `Interaction` (handlers). A chain node never draws and is never a child.
3. **Values** feed pins: styles, fonts, paths, `QtImage`, samplers and uniforms.
4. **Renderers** show a tree: `Renderer` (a window), `QtTexture` and `QtRenderer` (in `VL.Qt.Stride`).
5. **Factories** make nodes from files: `qml/` and `qtshaders/` folders.

A package boundary follows a dependency: `VL.Qt.Stride` exists because it needs Stride, `VL.Qt.Audio` because it needs VL.Audio. A feature that needs no extra library goes into `VL.Qt`.

### Compared to VL.Skia and VL.Stride

| | VL.Skia | VL.Qt |
|---|---|---|
| Output type | layer | `Element` (what VL.Skia calls a layer is an element here) |
| Many children | `Group` with a spread | `Group`, `Row`, `Column` with a spread, `Spectral` variants for layout |
| Placement | `Transform` pin | `Transform` and `Anchor` pin on every element; in `Row` and `Column` the layout owns the position |
| Last input pin | `Enabled` | `Enabled` (the same position) |
| What is redrawn | the whole scene each frame, no identity | the reconciler diffs the tree; Qt keeps its items between frames |
| Show it | `Renderer` | `Renderer` in a Qt window |

VL.Stride is not a model for the node set but a host: `QtTexture` gives a Stride `Texture` and `QtRenderer` is a layer in a Stride render context ([stride-output.md](stride-output.md)). There is no Skia host; the interop issue that listed it was closed without one.

The consequence is that a Skia patcher finds `Transform` and `Enabled` where they expect them, and that the differences are those of the engine: state kept by Qt (hover, focus, loading), the identity of an element between frames ([reconciler-identity.md](reconciler-identity.md)) and the chain pins `Interaction` and `Effect`.

## Consequences

- A request for "the Qt X" is answered with the rules above: can the patch describe it, does it own the time, is it flat. Features that fail the second rule are not offered in a lighter form; the patcher animates the value in vvvv or writes the animation in a QML file.
- User QML is the first answer for everything the table marks as "none" or "not yet", so its limits (experimental, one root item, no engine-level features such as models) bound what the set can be without VL.Qt releases.
- Four gaps are known and have issues; the rest of the table is decided. A node added later is entered in the table of this note and in the node-API snapshot.
- A node that is renamed or removed follows [versioning.md](../versioning.md).

## Alternatives considered

- **Mirror Qt Quick one to one:** a node per QML type and a property pin for every property. The browser would hold hundreds of nodes, most with pins that do nothing in a patch, and Qt's state model would come with them.
- **A thin set plus QML only:** elements for shapes and text, everything else as user QML. Interaction and effects would then need one QML file per use, and the patch could not read hover or value without writing glue; the widgets and the chains pay back.
- **Qt animation nodes (a `State` or `Behavior` node):** a second clock in the patch. They duplicate vvvv's animators and their state cannot be scrubbed by the patch. See the open point below.
- **A generic `Properties` pin on every element:** no pin types, nothing to discover in the node browser ([element-model.md](element-model.md)).
- **Skia-style wrapper nodes** (a `Transform` node around an element): the tree gets deeper for no gain, as Qt has a matrix per item.

## Open questions

- Grid and Flow containers: [#445](https://github.com/sebllll/VL.Qt/issues/445).
- Widgets beyond the five: [#446](https://github.com/sebllll/VL.Qt/issues/446).
- Styled text: [#447](https://github.com/sebllll/VL.Qt/issues/447).
- Keyboard and focus as an `Interaction` node: [#448](https://github.com/sebllll/VL.Qt/issues/448).
- Gradient fills, lines and the Skia adapter: [#155](https://github.com/sebllll/VL.Qt/issues/155).
- Animation events and a set of nodes on Qt's animation system are open only if a use case appears that vvvv's animators cannot cover; there is no issue for it.

## Evidence

The set was checked on 2026-11-28 against the nodes in `node-api.txt`, the HowTo patches in `help/Qt/` (drawing, text, layout, widgets, interaction, effects, images, video) and the types of the Qt Quick, Qt Quick Controls and Qt Quick Shapes modules of Qt 6. This is a review of the node set and of what Qt offers, not a measurement. To rerun it, list the public types of those modules and compare them with the table above.
