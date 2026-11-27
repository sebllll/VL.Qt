# Command protocol: semantics

What the commands, events and values of the command buffer mean. The protocol itself (header, field order, sizes, alignment, constants) is in [`protocol.h`](../../src/VL.Qt.Native/protocol.h), mirrored by hand in [`Protocol.cs`](../../src/VL.Qt/Wire/Protocol.cs); the drift tests compare constants and layouts, never prose. This page is for contributors who change QtNative or the reconciler. For why the layers are cut this way see [architecture.md](architecture.md) and [design/reconciler.md](../design/reconciler.md); for the elements see [design/element-model.md](../design/element-model.md).

Error codes (`-2` bad length, `-4` unknown object, `-6` unknown property, `-7` bad value, `-8` invalid) are `VLQT_ERR_*` of `vlqt.h`. A command that fails stops the apply; commands before it stay applied, and the C# side resyncs with `RESET`.

## Buffer and header

- A command is a header (`u16 op`, `u16 flags`, `u32 payloadLen`) and its payload, 4-byte aligned. `flags` is reserved: any other value than 0 is `-7`. A `payloadLen` above `VLQT_MAX_COMMAND_PAYLOAD` is `-2`, and nothing of that command is applied.
- Ids are `u32`, assigned by the C# reconciler; `0` is the window root.
- `RESET` destroys every object of the window, like one `DESTROY` per top-level object, so the ids are free again. The window root, clear color, `SET_CLEAR` and `SET_SPACE` stay.
- `DESTROY` is recursive: child ids are released too.
- `REPARENT` takes `parent 0` for the window root.

## Window state

- `SET_CLEAR`: `1` clears every frame to the clear color (the default), `0` keeps the previous frame and draws the new one over it (a previous-frame layer in the scene, in windows and offscreen scenes alike). The first frame after switching to `0` and after a resize starts from the clear color. See [design/clear-and-msaa.md](../design/clear-and-msaa.md).
- `SET_SPACE`: the `vlqt_space` of the scene, in the enum order of `VL.Qt.Space`; the default is `DIP_TOP_LEFT`, an unknown value is `-7`. QtNative only moves the origin: the window root sits at the top left of the scene (`DIP_TOP_LEFT`, `PIXEL_TOP_LEFT`) or at its centre (`NORMALIZED`, `DIP`; it follows resizes). The scale (units to logical pixels) is C#'s: every length arrives in logical pixels. Values that leave QtNative are in the root's coordinates (`EVENT_SCENE_BOUNDS` is mapped from the scene into the root). Size and dpr of the scene come from `vlqt_scene_get_metrics` (`vlqt.h`).

## Geometry

There is no position property: position comes from `TRANSFORM`. Lengths are logical pixels.

- `SIZE` (vec2): the item's width and height. Rectangle, Text, Image and the like always take it (0 = not drawn, not clickable). Containers, Button, value inputs and a ShaderEffect with an Input take it only with `AUTO_SIZE` off; otherwise the size is implicit.
- `ANCHOR` (`i32` `Qt::Alignment`, one of left/right/hcenter and one of top/bottom/vcenter; default `AlignLeft | AlignTop` = `0x21`): the point of the item that lies at the origin of its parent. It is resolved against the item's actual width and height, so it follows every size change.
- `TRANSFORM` (mat4) is applied around the anchor point: a point `v` in item coordinates lands at `(v - anchor) * M` in parent coordinates (row vectors, like Skia and Stride). Identity with `AlignLeft | AlignTop` puts the top left corner at the parent origin; `AlignCenter` puts the item's centre there.
- Children of a Row or Column: the layout owns x and y; `ANCHOR` is only the pivot of `TRANSFORM` (`v -> layoutPos + anchor + (v - anchor) * M`), so the item stays in its cell.
- The drag offset of a Drag handler comes last, in parent space.
- `VISIBLE` (the `Enabled` pin, default true): false takes the item out of the scene (not drawn, no input, no space in Row or Column). QtNative hides children of a ShaderEffect, parked masks and chain textures itself; for those the value is only remembered.
- `DEBUG`: draws the element in the window's debug overlay, not recursive.

### Group frame

A Group (C# default anchor TopLeft) is a pure transform container like the Skia group. Its frame is what the other elements call their size:

- With `AUTO_SIZE` on, the frame is the bounding box of the group origin and of all visible children with a non-empty size, each mapped with its x/y and `TRANSFORM` (not Qt's `childrenRect`). Children left of or above the origin make the frame start in negative group coordinates. A child counts by its own visibility, also inside a hidden Group (for example the Input of a ShaderEffect).
- With `AUTO_SIZE` off, the frame is `SIZE` placed by `ANCHOR` around the group origin: `(-fx * w, -fy * h, w, h)`. The origin, and so the children, does not move; `TRANSFORM` pivots around the origin.
- Per axis, `ANCHOR` Left/Top means the group origin (never shifted by the content); HCenter/VCenter/Right/Bottom mean that point of the frame. Content at non-negative coordinates gives the frame `(0, 0, right, bottom)`, the plain `f * size` of every other element.
- The frame is the group's size (`EVENT_MEASURED`), hit area (the free area for handlers of the group), clip rect, scene bounds and debug overlay frame. It is recomputed once per frame (polish) after a child changed position, size, visibility or `TRANSFORM`.
- A snapshot (`vlqt_snapshot_start`) of a Group covers the frame, also the part in negative coordinates. An effect layer on a Group covers the whole frame (that part is added to the left and top padding of the MultiEffect), and so does a Group as ShaderEffect Input or texture uniform.

## Property blocks

Property ids `1..255` are common to all items; the type-specific blocks start at 256. QtNative looks ids up per type, so one id can serve several types (`COLOR`: Rectangle fill and Text; `SOURCE`: Video string, ShaderEffect ref). A property an element type does not have is `-6`.

| Block | Used by |
|---|---|
| 256 | Rectangle |
| 512 | Text |
| 544 | Image (`SOURCE` and `FILL_MODE` are shared with Video; Image itself has no `SOURCE`, it takes an image resource, so `SOURCE` on it is `-6`) |
| 576 | Row, Column |
| 608, 800 | reserved (removed elements), keep unused |
| 640 | `AUTO_SIZE` of containers (Item/Group, Row, Column) and ShaderEffect; others `-6` |
| 672 | Video |
| 704 | Controls: Button (`TEXT` and `AUTO_SIZE` are shared; `AUTO_SIZE` on, the default, takes the implicit size from the text); from 706 the value inputs |
| 768 | input handlers, only on handler objects (others `-6`); 768 itself is reserved: positions come from the Point handler only |
| 832 | keys of `MULTI` links, not settable by `SET` (`-6`) |
| 896 | ShaderEffect (it also takes `SOURCE` and `AUTO_SIZE`) |
| 928 | QmlElement |
| 960 | Shape: lengths in logical pixels, dash and offset in stroke-width units |

Element type ids `2`, `8` and `16` are removed; keep them unused.

Defaults that QtNative sets, where they differ from Qt's: `MINIMUM` of a SpinBox is `INT32_MIN` (Qt: 0), `MAXIMUM` is `INT32_MAX` (Qt: 99); Slider `MINIMUM` 0, `MAXIMUM` 1, `STEP` 0 (continuous; above 0 it also snaps the drag).

`FILL_MODE` values are the order of `VL.Qt.Elements.FillMode`. Image takes all four, centered like QML Image. Video takes `STRETCH`, `FIT` and `CROP`; `TILE` is `-7`, so the Video node sends `STRETCH` for a Tile pin value and warns.

`IMAGE` is a `u64` image resource id, 0 = none; a new id keeps the old image until it is Ready ([design/resources.md](../design/resources.md)).

## Values

- A value is encoded like `SET` in every command that carries one. A value type the property does not take is `-7`.
- `COLOR` has straight alpha on the wire. For shader uniforms QtNative premultiplies it, like QML; `VEC4` is not premultiplied.
- `MAT4` is row-major like Stride; QtNative transposes it for uniforms.
- A path is valid only when `pointCount` equals the points its verbs take. Verbs: `Move` 1 point, `Line` 1, `Quad` 2 (control, end), `Cubic` 3 (control 1, control 2, end), `Close` 0. See [design/paths.md](../design/paths.md).
- `U64` is a resource id (`IMAGE`, shader ids, audio tap) or, in a uniform, an image resource.

## Input handlers

Input handlers are objects of their own in the same id space (types from 32). The events they produce are listed under [Events](#events); the design is in [design/events-and-interaction.md](../design/events-and-interaction.md).

- `CREATE` reserves the id and remembers type and properties; the QML handler is created by the `REPARENT`.
- `REPARENT` (handler, parent, index) attaches the handler to the element item `parent`; the root `0` and another handler are not valid parents, and `index` is ignored. A second `REPARENT` of an attached handler is `-8`; the host re-attaches by `DESTROY` and `CREATE`.
- `SET` takes the handler properties of block 768 (set before the `REPARENT` = applied on creation). Element properties on a handler and handler properties on an element are `-6`.
- `BIND` sends the handler's events to the `feedbackId`. A new id forgets the sent state and the current state is sent again; the old id gets `HOVER`/`PRESS`/`ACTIVE` 0 if 1 was sent and `WHEEL (0, 0)` after motion. A running press of a Tap handler is cancelled for the new id.
- `DESTROY` of a handler removes only the handler. `DESTROY` or `RESET` of an element removes its handlers too.
- A Text item with at least one handler hit-tests against its painted text.
- A Drag handler moves the item by its drag offset. `SET TRANSFORM` resets the offset to 0: the pin wins, so every `TRANSFORM`, also a resync, puts the item back where the pin says. `SIZE` and `ANCHOR` keep the offset.

## Binding and feedback

`BIND` (`id`, `feedbackId`): `feedbackId 0` unbinds. Every poll record carries the `feedbackId` of the `BIND`, and goes to the slot bound to it. `EVENT_WINDOW_GEOMETRY` has `feedbackId 0`: it is about the window, not an element.

## Events

Records of the poll buffer, per record 4-byte aligned. Positions are item-local logical pixels unless said otherwise.

| Event | Meaning |
|---|---|
| `MEASURED` | size `(w, h)` of the element, logical pixels |
| `SCENE_BOUNDS` | `(x, y, w, h)` in root coordinates, logical pixels |
| `HOVER`, `PRESS` | `u8` 0/1 on change |
| `CLICK` | `u32`: Tap handler: taps since the last poll; Button: clicked signals |
| `POINTER` | `(x, y)` while a Point handler is active |
| `MEDIA` | `(position, duration)` in seconds, Video only, 0 until loaded |
| `WINDOW_GEOMETRY` | 28 bytes: `f32 x, y` (outer top left; the client position in fullscreen), `f32 w, h` (window size), `u32` flags (`vlqt_window_state`), `f32` client x, y (top left of the client area). `WindowPlacement` also reads the 20-byte form, with client = outer position |
| `ERROR` | UTF-8 error text of the element, empty = cleared |
| `TAP_COUNT` | `u32` tap count of the last tap, on change (never 0; 2 = double click) |
| `LONG_PRESS` | `u32` long presses since the last poll |
| `ACTIVE` | `u8` on change |
| `WHEEL` | `(x, y)` notches (`angleDelta / 120`) since the last poll; only when not 0, once `(0, 0)` after motion |
| `DRAG` | translation of the running drag since the press, parent space after axis and limits, on change |
| `TOUCH_POINTS` | `u32 count`, then per point `u32 id`, `f32 x, y, pressure, w, h`; on change; count 0 once when the last point goes; at most 64 points |
| `PINCH` | scale, rotation (degrees, clockwise), translation x, y, pivot x, y (parent space), while active; the last one comes before `ACTIVE 0` (never the 1/0/0 Qt resets to) |
| `VALUE` | `u16 valueType`, `u16 0`, value like `SET` |
| `QML_PROPERTY`, `QML_SIGNAL` | see [QmlElement](#qmlelement) |

Per handler:

- Hover: `HOVER` on change.
- Tap: `PRESS`, `CLICK`, `TAP_COUNT` on change, `LONG_PRESS`.
- Point: `ACTIVE` on change and `POINTER` while active. With `ACCEPTED_BUTTONS` 0 the mouse is active while hovering; touch and pen while down.
- Wheel: `WHEEL`.
- Drag: `ACTIVE` on change and `DRAG` on change.
- Touch Points: `TOUCH_POINTS` on change.
- Pinch: `ACTIVE` on change and `PINCH` while active.

### Value inputs

SpinBox (`i32`), CheckBox and Switch (`bool`), Slider (`f32`) and TextField (`string`) send `EVENT_VALUE` for every bound value input, independent of input handlers, and only after a user change:

- SpinBox: `valueModified` (plus/minus, arrow keys, wheel at once; typed text on Enter or focus loss).
- CheckBox, Switch: `toggled` (click, Space).
- Slider: `moved` (press, drag at every step, arrow keys, wheel).
- TextField: Enter or focus loss, only if the text differs from the last set or committed one.

`SET VALUE`, `MINIMUM` and `MAXIMUM` never echo, also not when Qt clamps the value. The payload is the value the user produced, captured when the signal fires, so a `SET VALUE` applied between the user change and the poll does not hide it. Several user changes between two polls give one `EVENT_VALUE` with the last one. A new `feedbackId` drops a change that was not polled.

## SET_STYLE

The resolved style of a widget, on Controls types (10..15) only; other types are `-6`. QtNative keeps a table of field by state per element and exposes it to the VL.Qt base style through the attached object `VlStyle`. There is no inheritance to children: C# resolves ([styles.md](../styles.md)).

- Without `STYLE_SET_CLEAR` the entries are laid over the table (diff); with it the table is emptied first (full send).
- An unset (field, state) falls back to (field, `NORMAL`), then to 0, transparent or `""` (the Qt default font family). The base style values live only in C#.
- An unknown field or state is `-6`, a value type other than the field's is `-7`.
- When several states apply, the base style picks one by rank: `DISABLED` > `PRESSED` > `HOVERED` > `CHECKED` > `NORMAL`.
- Fields are grouped by part: colors 1.., Frame 32.., Text 64.., Indicator 96.., Track 128... Lengths are logical pixels. Frame is the background of the control (Button face, TextField and SpinBox box, the padding area of CheckBox, Switch and Slider); Indicator is the Slider handle, CheckBox check mark, Switch knob and SpinBox buttons; Track is the Slider groove, Switch track and CheckBox box, and `TRACK_FILL` the filled part (Slider value, Switch on) and the TextField selection.

## ShaderEffect

An own `QQuickItem` with a `QSGMaterialShader` (not the QML ShaderEffect). Uniforms are mapped by name through the reflection of the shaders: the members of the uniform block (std140 offsets) and the `sampler2D` bindings. See [effects-and-shaders.md](../effects-and-shaders.md) for the nodes.

### Shaders

- Shaders are resources (`VLQT_RESOURCE_KIND_SHADER`, `vlqt_shader_load`): window independent, loaded in a worker (never in the frame), ids with generation like images, element references through acquire and release ([design/resources.md](../design/resources.md)).
- Path `builtin:<name>` is a shader shipped with VL.Qt: `<QtNative dir>/shaders/<name>.frag.qsb` (stage FRAGMENT) or `<name>.vert.qsb` (stage VERTEX); in the package `qt/<rid>/shaders/`, in an app `runtimes/<rid>/native/shaders/` next to `qml/`. The sources are in `src/VL.Qt.Native/shaders/`; the build bakes them with `qsb` (build time only, `qsb` is not shipped). Any other path is a file.
- Failed with `RESOURCE_EVENT_ERROR`: not a `.qsb`, the stage of the `.qsb` is not the requested one, no variant for the backend of the process device, or a missing file. `RESOURCE_EVENT_INFO` of a shader is all 0 (no upload; each item builds its pipeline itself).
- `vlqt_shader_load_source` takes full Qt GLSL (Vulkan-style GLSL 440 as `qsb` takes it, no preamble added), copied at the call and baked by `QShaderBaker` in the worker, only for the shader language of the process backend (plus the reflection). Bakes are cached per (stage, backend, hash of the source) in the process, so going back to an earlier text is Ready without a bake. The same checks as for a `.qsb` follow the bake. A bake error ends in Failed with the compiler message (glslang, with line numbers of the source), one line per problem.

### Properties

- `FRAGMENT_SHADER` (`u64`, 0 = none: the element draws nothing) and `VERTEX_SHADER` (`u64`, 0 = Qt's default vertex shader: `qt_Vertex` location 0, `qt_MultiTexCoord0` location 1 to `qt_TexCoord0`).
- A new id keeps the old shader until it is Ready (like `IMAGE`). If the new id fails, the last Ready shader of that slot keeps drawing (live editing never goes black); a Failed shader without an earlier Ready one draws nothing.
- `BLENDING` (bool, default true, premultiplied: One, OneMinusSrcAlpha). `MESH_RESOLUTION` (ivec2, grid subdivisions x, y >= 1, default 1, 1; below 1 is `-7`).
- `SOURCE` (ref) is the Input: the element bound to the `sampler2D` named `source`, 0 = none (generative). `AUTO_SIZE` (default on): implicit size is the size of the Input; without an Input the `SIZE` counts anyway.
- Input and the elements of texture uniforms are children of the ShaderEffect (C#: Input at index 0, then the texture elements in uniform order, each once). QtNative hides them on `REPARENT` and renders them as layers (texture providers). A Group is sampled as its whole frame (also the part left of and above its origin, see [Group frame](#group-frame)) through one hidden `ShaderEffectSource` per Group, shared by Input and texture uniforms; with Input = Group the implicit size is the frame size and follows it live.

### Uniform block

As in Qt's ShaderEffect: std140, identical in the vertex and the fragment shader, starting with `mat4 qt_Matrix` (offset 0) and `float qt_Opacity` (offset 64), both set by QtNative; the shader's own members follow.

### SET_UNIFORMS

- Only on ShaderEffect elements, else `-6`. Entries without `UNIFORM_SET_CLEAR` are laid over the element's table by name (diff); with it the table is emptied first (full send). Values stay by name across shader changes.
- A member without a value is 0, a sampler without a texture samples transparent.
- Value types match exactly, no conversion: `float` F32, `int` I32, `bool` BOOL, `vec2` VEC2, `vec3` VEC3, `vec4` VEC4 or COLOR (premultiplied), `ivec2` IVEC2, `mat4` MAT4, `sampler2D` REF (an element id, a child; 0 = no texture) or U64 (image resource id). `STRING` is not a uniform type (`-7`).
- Apply errors only for malformed entries: bad lengths `-2`, unknown value type or `nameLen` 0 or above `VLQT_UNIFORM_NAME_MAX` `-7`, unknown ref id `-4`. The whole command is validated before the table is touched.
- Content problems never fail the apply: unknown name, wrong type, arrays, the reserved names `qt_Matrix`, `qt_Opacity` and `source`, and differing uniform blocks of the two shaders go as `EVENT_ERROR` text of the element (one line per problem, empty = cleared). The C# node shows it as warning and at its `Error` output. The shader errors themselves come by `RESOURCE_EVENT_ERROR`.

### Sampler

How a `sampler2D` reads its texture. An entry of `SET_UNIFORMS` or of a SHADER link's uniform table, value type `SAMPLER` (20), name = the `sampler2D` it belongs to (`source`, the Input of a ShaderEffect, is allowed here, unlike for a texture). The 8-byte value: byte 0 filter (0 Linear, 1 Nearest; min and mag), byte 1 mip filter (0 None, 1 Nearest, 2 Linear), byte 2 address U, byte 3 address V (0 Clamp = ClampToEdge, 1 Repeat, 2 Mirror = MirroredRepeat), bytes 4..7 reserved = 0. Any other value is `-7`.

- No anisotropy (`QSGTexture` does not support it) and no layer format (a float layer stays empty under the 4x MSAA of the windows).
- The entry lives in its own table next to the uniforms; the rule is that it has the same name as the texture entry. No entry = the texture as it is (the Qt default of its source). Removing the entry needs `UNIFORM_SET_CLEAR`, like removing a uniform.
- QtNative wraps the sampled `QSGTexture` in a view with this state; the texture itself, which other items may share, is never changed.
- A mip filter other than None also acts on the element the sampler reads (`layer.mipmap`; a Group through its frame proxy): its layer is rendered with mipmaps. With several samplers on one element it has mipmaps if any asks, and none when nobody does. An image (`U64`) keeps what its load made (`VLQT_IMAGE_LOAD_MIPMAPS`): the mip filter needs mipmaps there. Without the entry nothing changes at the element.

## Effect chain

The `Effect` pin of an element is a chain of links. `SET_EFFECT_CHAIN` sends the whole chain (always a full send, no diff) in application order: the first link applies first to the element (the start of the chain in vvvv, `Previous == null`), the last one (the link at the `Effect` pin) last, like Stride TextureFX. See [effects-and-shaders.md](../effects-and-shaders.md).

- `linkCount 0` = no chain: the layer is removed. Otherwise QtNative sets `layer.enabled` and one `layer.effect` on the element's own item; the links are an item tree inside that effect item (intermediate stages hidden, each its own layer = texture provider, the last draws). Size, `TRANSFORM`, implicit size, Row and Column layout and hit test stay those of the item.
- Root `0` and input handlers are `-6`, an unknown id `-4`, an unknown link kind `-7`, lengths that do not add up `-2`. `SET` of an `EFFECT_*` key (block 832) is `-6`: the ids are only keys of `MULTI` entries.
- A rejected `SET_EFFECT_CHAIN` changes nothing: layout, ids and masks are checked before a mask or texture is parked.
- Content problems never fail the apply (unknown uniform name, wrong type, shader not ready, reserved names, negative padding or strength, a margin cut by the limit below). They are `EVENT_ERROR` text of the element, one line per problem prefixed with the link index (`link 2: ...`), empty = cleared.
- The Qt Quick software backend (`QT_QUICK_BACKEND=software`) does not draw ShaderEffects, so an element with a chain is not drawn there at all (layout and hit test unchanged).

### MULTI link

One MultiEffect run: consecutive built-in links, folded in C# per run. Entries are `u16 key` (`vlqt_multi_effect_key`, 833..855, else `-6`; 832 is unused), `u16 valueType` (the property's type, else `-7`), value like `SET`, padding to 4.

- Unset keys have Qt's defaults (all parts off, `BLUR_MAX` 32, `SHADOW_BLUR` 1, `SHADOW_SCALE` 1, `COLORIZATION_COLOR` red, `AUTO_PADDING` on, `MASK_THRESHOLD_MAX` 1). A run is applied whole: a value missing in the next send is back at its default.
- The effect area of the run is its input (the element or the previous link) plus the run's padding.

### SHADER link

One shader effect link driven by QtNative's pass driver. Body: `u16 template`, `u16 shaderCount`, `u64 shaderIds[shaderCount]`, padding (4 x f32: left, top, right, bottom in DIP, >= 0), `f32 strength`, then the uniform entries like `SET_UNIFORMS` (`STRING` is `-7`).

- Shader ids are FRAGMENT resources. Roles per template: `SINGLE` and `TWO_PASS` take one (the fragment shader), `PYRAMID` two (prefilter, composite); another count is `-7`. `pyramid_down` and `pyramid_up` are loaded by QtNative as `builtin:`.
- `strength`: unused (0) for `SINGLE` and `TWO_PASS`; the radius in DIP for `PYRAMID` (weights from radius and dpr). The step count follows the pixel size of the effect area only, so a radius change writes weights and a size or dpr change rebuilds the steps.
- `PYRAMID` gets the uniform entries in prefilter and composite alike; each takes the names it declares, and a name neither declares is an `EVENT_ERROR` line. The composite's second input is `glow` or `shadow`, whichever it declares.
- `passIndex`: `SINGLE` and `TWO_PASS` write a `float passIndex` into the shader's uniform block if it declares one: 0 in `SINGLE` and pass 1, 1 in pass 2. A shader that needs a different value per pass derives it (the motion blurs divide their length by their sample cap 64 in pass 2).
- Set by QtNative and reserved for C# (an entry of that name is an `EVENT_ERROR` line): `qt_Matrix`, `qt_Opacity`, `source`, `texelSize` (1 / pixel size of the sampled texture), `dpr`, `weight`, `elementRect`, `passIndex`, and for `PYRAMID` `base`, `glow`, `shadow`. `elementRect` (`SINGLE` and `TWO_PASS`) is a vec4 `(x, y, w, h)` of the element's own rectangle in UV of the pass area, 0 = not set; a texture element that must sit on the element and not on the area grown by earlier links (Blend layer, Displace map) maps itself with it.
- Texture uniforms: `REF` is an element object of its own that the host never reparents (like a mask); QtNative parks it hidden and renders it as a layer. An unknown id is `-4`. `REPARENT` of it while a chain samples it is `-8`; once no chain samples it, a `REPARENT` shows it again as a plain element (its layer goes off one frame later). `U64` is an image resource id.
- Shader id 0 or not Ready yet: the link passes its input through unchanged. A Failed id keeps the last Ready shader of that link (like ShaderEffect). Padding grows from link to link: each link's effect area is its input's plus its padding. The margins of all links together (padding, `PADDING_RECT`, the MULTI auto padding `BLUR_MAX` x (1 + `BLUR_MULTIPLIER`)) are cut per side to the larger side of the window, at least 1024 DIP.

### Mask

A mask applies to the neighbouring merged effects of one `MULTI` link. The mask element is an element object of its own (`CREATE`, its `SET`s and its children) that the host does not reparent. A `MULTI` entry `MASK_SOURCE` with `ref <maskId>` attaches it to those merged effects (effect area = theirs); `ref 0` detaches it.

- QtNative puts the mask item into a hidden frame item under the window root that serves as texture source (`visible` false, `layer.enabled` true), so it never counts for the element's layout, auto size, `childrenRect`, hit test or `REPARENT` indices.
- Position: the mask lies in the coordinates of the element's parent, like a sibling (its `SIZE`, `ANCHOR` and `TRANSFORM` as for an element in that parent, unstretched): a mask covers exactly the pixels it would cover as a sibling. Moving the element (`TRANSFORM`, `ANCHOR`, a size change with an anchor other than top left, a drag offset, a Row or Column layout position) leaves the mask window in place.
- Inside the frame, the mask sits in an item whose matrix is the inverse of the element's parent mapping (x/y, anchor placement, `TRANSFORM` around the anchor, drag offset), updated on change. The frame's `layer.sourceRect` is bound to the effect area (element plus padding: `AUTO_PADDING` grows it for blur and shadow, `PADDING_RECT` otherwise), so the texture covers exactly the effect area. A rotated or scaled element therefore samples the mask exactly as well; the texture is rendered in the element's own units, so a scaled-up element gets a correspondingly coarser mask.
- Outside the mask's content alpha is 0 (the element is invisible there, `MASK_INVERTED` flips it). Content of the mask's children outside its item rectangle counts.
- `ref 0` detaches the mask; it stays parked and hidden until a `REPARENT`, which shows it again, or its `DESTROY`. `DESTROY` of an attached mask detaches it first; `DESTROY` or `RESET` of the element destroys its attached mask too. A used mask item is deleted one frame later.
- `REPARENT` of an attached mask, and attaching the element itself, an ancestor, an element that has a parent or one attached elsewhere, is `-8`.

## QmlElement

`VLQT_ELEMENT_TYPE_QML` loads a user `.qml` file into the element tree; see [user-qml.md](../user-qml.md) and [design/user-qml.md](../design/user-qml.md). The element is an item that holds the instance of the file's root, which must be a `QQuickItem` (else `EVENT_ERROR`).

- `QML_SOURCE`: absolute file path, empty = nothing loaded. It is compiled from a `file:` URL, so relative imports resolve.
- `QML_RELOAD`: `i32` counter, initially 0. A new value clears the engine's component cache for the URL and rebuilds the instance, re-applying every property set so far. The C# side bumps it when the file or its neighbours change.
- Size: `AUTO_SIZE` on takes the root's `implicitWidth` and `implicitHeight`; off, `SIZE` sets width and height of the root.
- Compile, load and type errors arrive as `EVENT_ERROR` (file:line), empty = cleared.
- Patch to QML: `SET_QML_PROPERTIES` sets properties of the root by name. Only on QML elements, else `-6`. Entries are encoded like `SET_UNIFORMS` entries, and `STRING` is allowed (`u32` byte length plus UTF-8, padded to 4, as in `SET`). Allowed types: F32, I32, BOOL, STRING, COLOR, VEC2, VEC3, VEC4, RECTF and LIST (a `list<T>` property or a `Spread`; the layout is in `protocol.h`, the rules in [List values](../design/user-qml.md#list-values)). An unknown name, an unsupported type or a failed `QVariant` conversion does not fail the apply: it is an `EVENT_ERROR` line of the element and the value is ignored.
- QML to patch: of the properties and signals declared in the root of the user file (not those inherited from Item):
  - `EVENT_QML_PROPERTY`: one per property per poll, with its last value. Not sent when the change came from `SET_QML_PROPERTIES` (no echo, like `EVENT_VALUE`).
  - `EVENT_QML_SIGNAL`: every emitted signal is kept, in order (cap 256 per poll, then one `EVENT_ERROR` line "signals dropped"). A list property or argument is sent as `LIST` when its elements have one wire type, a list of mixed elements as its JSON string (`VALUE_TYPE_STRING`); arguments of other unsupported types are sent as their string form.

## Resources

Resources are window independent and have their own status queue (`vlqt_res_poll`). Ids are `u64`, assigned by QtNative: index (low 32 bits) and generation (high 32 bits). See [design/resources.md](../design/resources.md).

- States: Pending, Decoding, Uploading, Ready or Failed, then Released; Released is never sent, because the host released the id before. No record comes after `vlqt_res_release`.
- `RESOURCE_EVENT_ERROR` (Failed) is sent before its `STATE`. `RESOURCE_EVENT_INFO` (width, height, format, uploaded bytes) is sent before `STATE Ready` and after each replacement. `RESOURCE_EVENT_WARNING`: at most one per resource.
- Replacement: new content on a Ready id keeps it Ready (the old texture is visible) until the new upload is complete.

### Pixel formats

`vlqt_image_set_pixels` takes VL `PixelFormat` numbers (VL.Core `PixelFormat.cs`, append-only):

- Direct: `R8G8B8A8` 7, `B8G8R8A8` 9, `R8G8B8X8` 6, `B8G8R8X8` 8 (X: alpha forced to 255).
- Converted to RGBA8: `R8G8B8` 4, `B8G8R8` 5, `R8` 1 (grey).
- Reduced to 8 bit, with one `RESOURCE_EVENT_WARNING` per resource: `R16` 2 (grey), `R32F` 3 (grey, [0,1]), `R32G32F` 11 (R, G, 0), `R32G32B32A32F` 10, `R16G16B16A16F` 12.
- Anything else (Unknown 0, newer numbers): Failed with `RESOURCE_EVENT_ERROR`.

Input that is not premultiplied is premultiplied in the worker; `row_bytes > width * bpp` (padding) is copied row by row. The texture is RGBA8 or BGRA8, 8 bit premultiplied (`RESOURCE_EVENT_INFO` format 7 or 9).

### Snapshots

`vlqt_snapshot_start` grabs an element with `QQuickItem::grabToImage`: asynchronous, done after the next render of its window, never `grabWindow`. States: Pending, Decoding (grab in flight), Ready or Failed; a new snapshot on a Ready id keeps it Ready (the previous pixels stay readable) until the new one is done. `RESOURCE_EVENT_INFO`: width, height, format 9 (B8G8R8A8, premultiplied), bytes of the CPU copy (`width * height * 4`). Failed with `RESOURCE_EVENT_ERROR` when the element is unknown in the window, the window is not exposed (an offscreen scene: not bound), the element or window is destroyed during the grab, or the grab is refused.

### Audio tap

`VLQT_RESOURCE_KIND_AUDIO_TAP` (`resources/audio_tap.h`) delivers the sound of a Video element as interleaved float samples for VL.Audio ([video-audio.md](../video-audio.md)). The contract between QtNative and C#:

- **Lifetime.** C# creates the resource (`vlqt_res_create`) and fixes its format once with `vlqt_audio_tap_init` (channels 1..`VLQT_AUDIO_TAP_MAX_CHANNELS`, sample rate `MIN` to `MAX`, always float). It goes Pending, Decoding, Ready at once (external content: no worker, no upload); `RESOURCE_EVENT_INFO` has width = channels, height = sample rate, format 0, bytes = ring size. The format never changes: a new VL.Audio sample rate means a new tap; C# sets the new id on the Video and releases the old one. A tap is freed like any resource (host release and no element reference); a read of a freed id returns `VLQT_ERR_UNKNOWN_ID` (`-9`) and never touches freed memory. At most 64 taps live at once; init refuses more (`VLQT_ERR_BAD_ARGUMENT`).
- **`VLQT_PROPERTY_AUDIO_TAP` (Video).** A tap id other than 0 attaches a `QAudioBufferOutput` in the tap's format to the element's `QMediaPlayer` and sets the player's `audioOutput` to null: Qt's own sound is off and the player runs on the system clock (with an output, even at volume 0, Qt syncs the picture to the audio device with about 200 ms start offset and seconds of slewing). There is no separate flag: a tap always means Qt is silent, and 0 restores the `AudioOutput`. C# swaps from one tap to the next without a 0 in between, so Qt never plays a burst. The tap hangs on the player of the item, never on the `QQuickWindow` (the window is replaced on an MSAA change; items move with it). The element holds an element reference on the tap.
- **One player per tap.** A second element with a tap id another element writes into is muted (no Qt output, no tap) and the property is refused (`ApplyBadValue`); it stays muted until its property changes. Bad ids (unknown, wrong kind, not Ready) are `ApplyBadValue`, and Qt's output stays as it was.
- **Clock.** All times are ns of one monotonic clock, `vlqt_audio_tap_now_ns()`: `std::chrono::steady_clock`. On Windows that is the QueryPerformanceCounter, so it equals `Stopwatch.GetTimestamp()` converted to ns (`ticks / f * 1e9 + ticks % f * 1e9 / f`, `f = Stopwatch.Frequency`); on Linux `CLOCK_MONOTONIC`, also Stopwatch's clock. C# may use either.
- **Producer (QtNative).** `audioBufferReceived` runs on a worker thread of the player (`Qt::DirectConnection`; the thread changes with the source, still one producer per tap). It copies the samples into a lock-free SPSC ring (about 500 ms) and stores per buffer: emit time (`vlqt_audio_tap_now_ns` at the signal), media start time (us), playback rate and frame count. No QObjects, no locks, no allocation on that thread. Sample `k` of a buffer is due (leaves the speaker, before any output latency) at `emitTime + k * 1e9 / (sampleRate * rate)` ns: Qt emits a buffer when its start is "now" on the player clock, in step with the matching video frame (within about 1.5 ms in the spike). Its media time is `startTime + k * 1e6 / sampleRate` us (the samples are media content, not rescaled by the rate).
- **Discontinuity.** The producer raises the discontinuity counter and starts a new segment; frames of the old segment still in the ring are dropped by the reader before it delivers new ones (flush). Raised on: source change (media start time jumps back), rate change, the first buffer after a pause (gap in wall time), ring overrun (the buffer that did not fit is dropped, `overrun_frames` grows by its frames) and attach of the tap. Not on a loop wrap: Qt's audio start time keeps counting across passes without a gap.
- **Reader (C#, audio thread).** `vlqt_audio_tap_read` (`vlqt.h`) takes no lock, allocates nothing, never frees a tap and never blocks on the main thread; any thread, one reader per tap at a time (the ring has one reader). One call delivers frames of one segment only (it stops at a segment boundary). `dst != NULL` copies and consumes up to `maxFrames` frames; `dst == NULL` consumes (drops) up to `maxFrames` frames without copying, which is how C# drops samples to catch up; `maxFrames == 0` reads the state only. The state describes the first frame the call delivered or dropped (or the next one in the ring when it moved none). Resampling, inserting silence or samples, the sync loop and Volume are C#'s job; QtNative delivers raw frames.

## Pointer and key input of offscreen scenes

`vlqt_offscreen_send_pointer` and `vlqt_offscreen_send_key` (`vlqt.h`) are not part of the command buffer; their constants (`vlqt_pointer`, `vlqt_pointer_device`, `vlqt_key_event`) sit in `protocol.h` for the mirror. `POINTER_LEAVE` ends hover and ignores x and y; `POINTER_CANCEL` is for touch and pen when the system cancelled the point (`TouchCancel`). A pen is delivered as a stylus, as in a window on Windows. See [design/offscreen-input.md](../design/offscreen-input.md).

## Change the protocol

1. Edit `protocol.h`: constants one per line, explicit values, no expressions; bump `VLQT_ABI_VERSION` on every change to a constant, a struct layout or an exported signature.
2. Mirror it by hand in `Protocol.cs` (naming rule in the head comment of `protocol.h`).
3. Run the drift tests (`dotnet test src/VL.Qt.Tests --filter "FullyQualifiedName~DriftTests|FullyQualifiedName~NativeLayoutTests"`).
4. Add the decoder in `scene/commands.cpp`, the writer in `CommandWriter`, and a test; describe the meaning here, not in the header.
