# Help patches

The help patches in `help/Qt/` show one topic each; open them from the vvvv Help Browser (F1).

## Drawing

- `HowTo Draw a Rectangle` — basic shapes, colors, radius
- `HowTo Draw a Circle` — Circle element
- `HowTo Draw Text` — typography, font families, alignment, wrapping
- `HowTo Measure Text` — measured size of text
- `HowTo Draw an Image` — image loading, fill modes, smooth filtering
- `HowTo Play a Video` — playback and controls
- `Demo Typo Motion` — animated typography

## Structure and layout

- `HowTo Group Elements` — grouping, nesting, transforms
- `HowTo Reuse an Element` — one element at several places of the tree, Interaction per place
- `HowTo Layout with Row and Column` — auto-layout with spacing and padding
- `HowTo Layout a Spread with Row and Column (Spectral)` — Row/Column fed with a spread of elements
- `HowTo Label an Element` — label left or above any element, shared Label Width for aligned panels

## Interaction

- `HowTo React to Hover and Click` — interaction channels, pointer position
- `HowTo Track the Pointer` — Point node, hover position vs. pressed position
- `HowTo Drag an Element` — move an element with the pointer
- `HowTo Pinch to Zoom and Rotate` — two-finger pinch and rotate
- `HowTo Use Touch Points` — all touch points, pen and finger apart
- `HowTo Use the Mouse Wheel` — Wheel node, zoom via the Value channel, reset
- `HowTo Stack Interactive Elements` — the topmost element wins hover and click, elements without an Interaction node let input through

## Widgets and styles

- `HowTo Use a Button` — click as Bang and channel
- `HowTo Use a Toggle`, `HowTo Use a Slider`, `HowTo Use a TextInput`, `HowTo Use Input (Int)` — input widgets with value channels
- `HowTo Style a Widget` — chain style nodes into the Style pin, per state
- `HowTo Style Control Parts` — indicator and track of Toggle/Slider, Merge with a preset
- `HowTo Style Widgets with a Group` — one style for everything inside a Group, overrides per widget
- `HowTo Patch a Stylable Widget` — own widget (XY slider) via IStylable that follows the surrounding style

See [Styles](styles.md).

## Images

- `HowTo Preload Images` — LoadImage (Async) for a spread of files, switching without stutter
- `HowTo Use an IImage` — ToQtImage for images from other libraries (e.g. Skia)
- `HowTo Take a Snapshot` — Snapshot of an element as IImage
- `HowTo Show a VL.Video player in Qt` — VideoSourceToQtImage: a VL.Video source in the Image element

See [Images](images.md).

## Effects and shaders

- `HowTo Chain Effects` — Blur, DropShadow, ColorAdjust, Colorize on the Effect pin
- `HowTo Chain Shader Effects` — filter shaders from `qtshaders/` as links of the effect chain, order matters
- `HowTo Combine Effects` — MultiEffect nodes (`Qt.Effects.MultiEffect`) share one layer, a shader node splits the run
- `HowTo Mask an Element` — any element (e.g. Text) as alpha mask, Inverted
- `HowTo Add a Glow` — Glow and InnerShadow
- `HowTo Use Motion Blurs` — DirectionalBlur, ZoomBlur, RadialBlur
- `HowTo Displace an Element` — Displace with a ShaderEffect as map
- `HowTo Blend Elements` — Blend: an element as layer over the base, blend modes
- `HowTo Set a Sampler` — `Sampler` node: filter, mip filter and wrap of a texture input
- `HowTo Write a ShaderEffect` — fragment shader as GLSL string or `.qsb` file, uniforms, an element as source texture
- `HowTo Create Shader Nodes from Files` — `qtshaders/` folder, one node per `.frag`, pins from uniforms
- `HowTo Blur an Image with a Control Mask` — two chained ShaderEffects (horizontal/vertical blur), a QtImage as mask uniform

See [Effects and shaders](effects-and-shaders.md).

## Window and diagnostics

- `HowTo Set a Window Icon` — `WindowSettings` node on the `Window` pin of the Renderer: icon, title, frame, fullscreen
- `HowTo Read the Window Bounds` — window and client bounds as outputs
- `HowTo Debug overlay` — layout overlay via the `Debug` pin
- `HowTo Read Performance` — perf numbers as node outputs

See [Diagnostics](diagnostics.md).

## Minimal patches for exports

These test patches live in the repository only; the NuGet package contains neither `help/HelloWorld*` nor `help/linux/`. With the package, export one of the `help/Qt/` patches instead (for Linux with a `KeepAppAlive` added, see [Linux](linux.md)).

- `help/HelloWorld.vl` — Windows (repo only)
- `help/HelloWorldLinux.vl` — Linux console export, see [Linux](linux.md) (repo only)
- `help/linux/HelloConsole.vl` — console export without VL.Qt (repo only)
