# Help patches

The help patches in `help/Qt/` show one topic each; open them from the vvvv Help Browser (F1). The Browser groups them in topics, set in [`help/Help.xml`](../help/Help.xml); the patches of the companion packages `VL.Qt.Stride` and `VL.Qt.Audio` (in `VL.Qt.Stride/help/`, `VL.Qt.Audio/help/`) are listed in [`VL.Qt.Stride/help/Help.xml`](../VL.Qt.Stride/help/Help.xml) and [`VL.Qt.Audio/help/Help.xml`](../VL.Qt.Audio/help/Help.xml).

## Help Browser

- `help/Help.xml` is the manifest of the Help Browser: one `<Topic title="…">` per group, one `<VLDocument link="Qt\HowTo ….vl" tags="…" />` per patch (link relative to `help/`, `tags` are search words). The order in the file is the order in the Browser.
- **Every patch under `help/` belongs in `Help.xml`.** vvvv does list patches that are missing from an existing manifest (below the listed ones, ordered alphabetically; found in the Help Browser code of vvvv 8.0), but without a topic and in an order nobody chose. That is why the export patches (`HelloWorld*`, `HelloConsole`) have a topic of their own and nothing is left out by accident.
- Name patches `HowTo <Task in title case>`; the prefix is the role in the Browser (`Example`, `Explanation`, … for the other roles). Rename or delete a patch together with its line in `Help.xml`, in the docs and in the patches that refer to it.
- Check after a change: every `link` exists and every `.vl` below `help/` is listed once.

## Text scheme

Every help patch has the same four parts, top to bottom:

1. **Title** – `How To …`, font size 25.
2. **Features** – ONE comment (font size 9) directly below the title: a short headline with the node(s) shown, then 3 to 5 terse bullets (pin names, no prose, no sentences that repeat the title).
3. **Content** – the patch itself: IOBoxes and nodes as described in `AGENTS.md` ("Checking patches"), with short labels where a picture needs them.
4. **Notes** – one comment at the very bottom, below all content: `Notes:` and the longer explanations, one bullet per point, nothing that is already in the features.

Example, `HowTo Draw a Rectangle`:

```text
How To Draw a Rectangle                                  (title, size 25)

Rectangle [Qt]                                           (features, size 9)
- Size, Radius, Color
- Stroke Color and Stroke Width
- Anchor: Center or TopLeft
- Position and rotation via Transform

… the Rectangle nodes, IOBoxes, Renderer …               (content)

Notes:                                                   (notes, size 9, bottom)
- Size is width and height in units (1 unit = 100 DIP). A Rectangle with width or height 0 is not drawn.
- Position, rotation and scale come only from Transform. The Anchor is the point of the rectangle that sits at the
  origin and the pivot of Transform: TopLeft (orange) puts the top left corner on the Translation, Center (default, blue)
  centers the rectangle on it.
- Connect it to Renderer [Qt] or a Group [Qt].
```

## Renames

A change that renames a node or a pin or moves a category does not edit the patches by hand. It adds rows to [`tools/vl-patch-mapping.txt`](../tools/vl-patch-mapping.txt) and runs `python tools/vl-patch-migrate.py`, which rewrites every patch of the repository (`help/`, `VL.Qt.vl`, `VL.Qt.HDE.vl`, `VL.Qt.Stride/`, `VL.Qt.Audio/`, `src/`). A change that adds a pin or rebuilds a patch still edits the `.vl` file directly.

- One row per rename: `step | kind | scope | old | new`, kinds `node`, `pin` and `category`. The header of the file explains the columns. Pin names are written as vvvv shows them (`Fill Color`) or as the C# parameter (`fillColor`), which the rule of `tools/vl_node_api.py` turns into the same name.
- The rows of one step apply together: `Radius → Set Radius` and `Get Radius → Radius` in one step never turn a setter into a getter. Steps apply in the order of the file.
- The script touches only VL.Qt references: the node name, its `Pin` and `PinReference` names, the category (`LastCategoryFullName`, the `CategoryReference` chain) and, for a category move, the type references. CRLF, the BOM, Ids, positions and every other byte stay as they are.
- It records nothing in the patches. Each name a row gives lives over a range of steps (an old name until its step, a new name from its step on), and the names of a node tell which steps it has seen; the script applies only the steps after that. So a second run changes nothing, and a patch whose nodes stand at different steps after a git merge is still migrated correctly. A node whose names do not tell the steps apart (a swap where the node shows only `Radius`), or that mixes old and new names, is reported instead of guessed.
- On a merge conflict in a `.vl` file: merge the mapping file (keep the rows of both sides, the side merged first comes first), take either side of the patch and run the script again. Never change or remove a merged row, since the script reads the steps from it.
- `--check` lists what would change and exits with 1 if anything would. Exit code 2: a bad or conflicting row, a new name that is not in `src/VL.Qt.Tests/Api/node-api.txt` after all steps (`--no-api-check` skips that lookup), or a node the script cannot place; nothing is written then.
- Afterwards `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File` run as after any patch change. `tools/test-vl-patch-migrate.py` is the regression test.

## Basics

- `HowTo Draw a Rectangle` — basic shapes, colors, radius
- `HowTo Draw a Circle` — Circle element
- `HowTo Draw a Line` — Line element: drag both end points in the Renderer; Cap, Trim Start and Trim End
- `HowTo Draw a Polyline` — open or closed point list with draggable corners, caps and joins
- `HowTo Draw a Polygon` — filled point list with draggable corners, fill rule on a self-intersecting polygon
- `HowTo Build a Path` — Qt.Paths builder chain (MoveTo, LineTo, CubicTo, Close, a Relative variant, two ArcTo forms, AddRoundRect) drawn with Path; every position and parameter is an IOBox at its pin
- `HowTo Draw a Path` — Path element: fill and stroke, Trim Start and Trim End, Fill Rule EvenOdd on a path with a hole
- `HowTo Draw a Bezier Curve with Handles` — Bezier (Handles): drag anchors and handle ends; Handle Modes switches between Free, Mirrored and Aligned
- `HowTo Draw Splines` — Spline (CatmullRom) with Alpha 0, 0.5 and 1 and Spline (BSpline) on the same draggable points, the control polygon drawn with Polyline
- `HowTo Group Elements` — grouping, nesting, transforms
- `HowTo Reuse an Element` — one element at several places of the tree, Interaction per place
- `HowTo Choose a Space` — the `Space` pin: origin and unit of Normalized, DIP, DIPTopLeft and PixelTopLeft

## Text

- `HowTo Draw Text` — typography, font families, alignment, wrapping
- `HowTo Use custom fonts` — `LoadFont` with a font file shipped next to the patch
- `HowTo Measure Text` — measured size of text

## Layout

- `HowTo Layout with Row and Column` — auto-layout with spacing and padding
- `HowTo Layout a Spread with Row and Column (Spectral)` — Row/Column fed with a spread of elements
- `HowTo Label an Element` — label left or above any element, shared Label Width for aligned panels

## Interaction

- `HowTo React to Hover and Click` — interaction channels, pointer position
- `HowTo Track the Pointer` — Point node, hover position vs. pressed position
- `HowTo Drag an Element` — move an element with the pointer
- `HowTo Use the Mouse Wheel` — Wheel node, zoom via the Value channel, reset
- `HowTo Pinch to Zoom and Rotate` — two-finger pinch and rotate
- `HowTo Use Touch Points` — all touch points, pen and finger apart
- `HowTo Stack Interactive Elements` — the topmost element wins hover and click, elements without an Interaction node let input through

## Widgets and Styles

- `HowTo Use a Button` — click as Bang and channel
- `HowTo Use a Toggle`, `HowTo Use a Slider`, `HowTo Use Input (String)`, `HowTo Use Input (Int)` — input widgets with value channels
- `HowTo Style a Widget` — chain style nodes into the Style pin, per state
- `HowTo Style Control Parts` — indicator and track of Toggle/Slider, Merge with a preset
- `HowTo Style Widgets with a Group` — one style for everything inside a Group, overrides per widget
- `HowTo Patch a Stylable Widget` — own widget (XY slider) via IStylable that follows the surrounding style

See [Styles](styles.md).

## Images and Video

- `HowTo Draw an Image` — image loading, fill modes, smooth filtering
- `HowTo Preload Images` — LoadImage (Async) for a spread of files, switching without stutter
- `HowTo Use an IImage` — ToQtImage for images from other libraries (e.g. Skia)
- `HowTo Take a Snapshot` — Snapshot of an element as IImage
- `HowTo Write an Element to a File` — FileWriter (Element) and Snapshot → FileWriter: PNG/JPG files
- `HowTo Play a Video` — playback and controls
- `HowTo Show a VL.Video player in Qt` — VideoSourceToQtImage: a VL.Video source in the Image element

See [Images](images.md).

## Effects

- `HowTo Chain Effects` — Blur, DropShadow, ColorAdjust and a shader (RGBSplit) on the Effect pin: one layer per run of merged effects, a shader node splits the run, order matters
- `HowTo Mask an Element` — any element (e.g. Text) as alpha mask, Inverted
- `HowTo Add a Glow` — Glow and InnerShadow
- `HowTo Blur An Element` — DirectionalBlur, ZoomBlur, RadialBlur
- `HowTo Displace an Element` — Displace with a ShaderEffect as map
- `HowTo Blend Elements` — Blend: an element as layer over the base, blend modes
- `HowTo Draw Trails` — `Clear` pin of the Renderer off, the alpha of `Clear Color` sets how fast the previous frame fades

See [Effects and shaders](effects-and-shaders.md).

## Shaders

- `HowTo Set a Sampler` — `Sampler` node: filter, mip filter and wrap of a texture input
- `HowTo Write a ShaderEffect` — fragment shader as GLSL string or `.qsb` file, uniforms, an element as source texture
- `HowTo Create Shader Nodes from Files` — `qtshaders/` folder, one node per `.frag`, pins from uniforms
- `HowTo Blur an Image with a Control Mask` — two chained ShaderEffects (horizontal/vertical blur), a QtImage as mask uniform

## QML

- `HowTo Use a QML file` — `QmlElement` with the sample `QmlElement Example.qml`: properties in by name, changed properties and signals out, hot reload on save

See [QML files as elements](user-qml.md).

## Window and Diagnostics

- `HowTo Set a Window Icon` — `WindowSettings` node on the `Window` pin of the Renderer: icon and initial bounds
- `HowTo Read the Window Bounds` — window and client bounds as outputs
- `HowTo Debug Overlay` — layout overlay via the `Debug` pin
- `HowTo Read Performance` — perf numbers as node outputs

See [Diagnostics](diagnostics.md).

## Stride

In `VL.Qt.Stride/help/` (package `VL.Qt.Stride`, Windows only), see [Stride texture](../README.md#stride-texture):

- `HowTo Render Qt into a Stride Texture` — `QtTexture`: a Qt scene (text, shapes) rendered offscreen into a Stride `Texture` with a transparent `Clear Color`, composed over a 3D background with `AlphaBlendPremultiplied`
- `HowTo Interact with a QtTexture` — `ToTexturePixels` feeds the window input into the scene of a `QtTexture`
- `HowTo Draw a QtTexture with Transparency` — half-transparent elements over a 3D scene, premultiplied alpha, `Format`, the same scene in a Qt window to compare
- `HowTo Render many QtTextures` — one `QtTexture` per slice of a `ForEach`, created and disposed with the spread

## Audio

In `VL.Qt.Audio/help/` (package `VL.Qt.Audio`, Windows only), see [Video sound through VL.Audio](video-audio.md):

- `HowTo Play a Video with VL.Audio` — `Video (VL.Audio)`: the sound of a video as VL.Audio signals at `Audio`, played by `AudioOut`, a `Meter` for the level, `Sync Error` as output, `Audio Offset` to calibrate

## Export

Minimal patches for exports, see [Export](export.md):

These test patches live in the repository only; the NuGet package contains neither `help/HelloWorld*` nor `help/linux/`. With the package, export one of the `help/Qt/` patches instead (for Linux with a `KeepAppAlive` added, see [Linux](linux.md)).

- `help/HelloWorld.vl` — Windows (repo only)
- `help/HelloWorldLinux.vl` — Linux console export, see [Linux](linux.md) (repo only)
- `help/linux/HelloConsole.vl` — console export without VL.Qt (repo only)
