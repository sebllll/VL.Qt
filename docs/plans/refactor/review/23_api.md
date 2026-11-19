# Review 23 – API as vvvv shows it

Claude Opus 5.5 (claude-opus-5-5), 2026-11-19.
Files read in full: output of `python tools/vl_pin_catalog.py` (every process node with its pins), `src/VL.Qt/Categories.cs`, `docs/versioning.md`, `docs/decisions/element-catalog-v1.md`, skill `vvvv-node-libraries/design-guidelines.md`.
Skimmed (declarations, attributes and XML docs only, found by search): every `[Smell]`, `[ImportAsIs]`, `[Category]`, `[SkipCategory]`, `[ProcessNode]`; every `public` type declaration in `src/VL.Qt`, `src/VL.Qt.Stride`, `src/VL.Qt.Audio`; `src/VL.Qt/Styles/StyleNodes.cs`, `src/VL.Qt/Nodes/UniformNodes.cs`, `src/VL.Qt/Styles/Style.cs`, `src/VL.Qt/Styles/IStylable.cs`, `src/VL.Qt/Elements/IElement.cs`, the enums in `src/VL.Qt/Elements/*.cs`, the naming parts of `src/VL.Qt/Shaders/ShaderNodeFactory.cs`, `src/VL.Qt/Qml/QmlNodeFactory.cs`, `src/VL.Qt/Shaders/ShaderNodeLayout.cs`; canvas structure of `VL.Qt.vl`; `<param>` docs of selected pins; `LastCategoryFullName` references in `help/**/*.vl`.
Reference (targeted searches in `C:/vl/VL.StandardLibs`): `VL.Core/src/Import/ImportAsIsAttribute.cs`, `SkipCategoryAttribute.cs`, `ProcessNodeAttribute.cs`; `VL.Skia/VL.Skia.vl` (pins of Rectangle, Circle, Text, Polygon, Group (Spectral), ArcTo, AddCircle, AddRoundRect, QuadTo, ImageWriter); `VL.Skia/src/SkiaRendererNode.cs`; `VL.ImGui/src/Widgets/**` (`[GenerateNode]` names); `VL.Stride/help` (category of `SkiaTexture`).
Not read: the node implementations (by the prompt).

## 1 Summary

The node set is small, its names are mostly vvvv-like, and the path builder mirrors Skia.Paths almost pin for pin. The weak points are at the edges:

1. **The import puts every public type into a second category tree `VL.Qt.*`** next to the intended `Qt.*` tree, including about twenty implementation types (concrete element classes, `ElementSink`, `SinkState`, `ElementType`, `Geometry`). The enums on pins live there too, so IOBoxes in the help patches already reference `VL.Qt.Elements`, `VL.Qt.Paths`, `VL.Qt.Nodes` (API-01).
2. **The category tree follows the implementation, not the user:** `Qt.Effects.MultiEffect` is a Qt rendering detail, the root `Qt` collects 14 unrelated nodes, shader and QML nodes are split across three categories, the Stride nodes sit in two (API-02).
3. **The common pins are not common:** `Style` exists on Group but not on Row/Column, Labeled lacks Clip/Debug and breaks the order, the spectral containers call their input `Children` where Skia says `Input`, and widgets show a `Size` pin that the hidden `Auto Size = true` ignores (API-03, API-04).
4. **Names that a vvvv user reads wrongly:** fill colour is `Color` on two shapes and `Fill` on two others; `Bang` means "clicked" on Button and "changed" on Slider; event channels are called `Is Clicked`; two effects take degrees where vvvv uses cycles; uniform nodes are named `Float`, `Int`, `Color` (API-05 to API-09).
5. `docs/decisions/element-catalog-v1.md` describes pins that do not exist (`Bounds`, `Pivot` default Center, `Visible`) (API-14).

## 2 What works and must survive

- **Process node shape:** every element node has the same tail `Opacity, Clip, Debug, Interaction, Effect [, Style]` and the same outputs `Measured Size, Scene Bounds (optional), Output`. Keep the tail and its order; the findings below only complete it.
- **`Interaction` optional without IOBox** (catalog: `(optional, no IOBox)` on every element). An IOBox there makes the element opaque to input (AGENTS.md); `tools/vl-patch-check.py` enforces it.
- **Chain pins `Input`/`Output`** on Effect, Interaction and Path nodes, so they chain like Skia's paint and path nodes. Interaction order does not matter (`<param name="input">` of DragNode).
- **Path builder = Skia.Paths:** names, versions `(Relative)`, the three `ArcTo` overloads with the same name (Skia has the same three, `VL.Skia.vl`), pins `Corner`, `Destination Point`, `Corner Radius`, `X Axis Rotate`, `Large Arc`, `Sweep`. Angles in cycles (AddArcNode `<param name="startAngle">`), which is the vvvv rule. Keep this mirror; API-11 only fixes where Skia is inconsistent with itself.
- **`Space` enum** mirrors Skia's `CommonSpace` in names, order and meaning (`src/VL.Qt/Space.cs:3`), default `DIPTopLeft` on all three renderers.
- **Widget pins after ImGui:** `Channel` input plus `Value` output, `Input (Int)` named exactly as in ImGui (`VL.ImGui/src/Widgets/Input/InputInt.cs:7`).
- **Generated nodes:** file `Name_Variant.frag`/`.qml` becomes `Name (Variant)` (`ShaderNodeFactory.cs:35`, `QmlNodeFactory.cs:31`), `@category` overrides the default, and a filter shader gets an element node and a chain node with the same name in two categories (`ShaderNodeFactory.cs:56-64`). This is the vvvv naming scheme and must stay.
- **Style setters as a chain** with `Optional<T>` pins ("unconnected = not set"), advanced per-field setters under `[Smell(Advanced)]`, and categories per part (`Qt.Style.Frame`, `.Text`, `.Indicator`, `.Track`, `StyleNodes.cs:12-401`).
- **Aspects:** `Experimental` on QmlElement, QtTexture, QtRenderer, ToTexturePixels; `Advanced` on ShaderEffect and the uniform nodes. These are the right symbols to carry them.

## 3 Findings

### API-01 Import only the intended types, under `Qt.*`
- **Kind:** api
- **Severity:** must
- **Breaking:** yes: the category of every imported type changes from `VL.Qt.*` to `Qt.*` (IOBoxes of enums re-resolve; help patches need their `LastCategoryFullName` updated); implementation types disappear from the node browser.
- **Where:** `src/VL.Qt/Renderer.cs:14`, `src/VL.Qt.Stride/QtTextureNode.cs:13`, `src/VL.Qt.Audio/VideoAudioNode.cs:14`; public implementation types `src/VL.Qt/Elements/Button.cs:8`, `CheckBox.cs:7`, `Column.cs:6`, `Group.cs:7`, `Image.cs:13`, `QmlElement.cs:8`, `Rectangle.cs:6`, `Row.cs:6`, `ShaderEffect.cs:12`, `Shape.cs:12` (with `ExtentOf` at :65, :78), `Slider.cs:14`, `SpinBox.cs:8`, `Switch.cs:7`, `Text.cs:48`, `TextField.cs:8`, `Video.cs:17`, `ElementSink.cs:10,114,350`, `ElementType.cs:7`, `Element.cs:17` (`Type`), `Element.cs:34` (`Sink`), `src/VL.Qt/Geometry.cs:11`, `src/VL.Qt/Initialization.cs:13`; enums in the node namespace `src/VL.Qt/Nodes/Effects/BlendNode.cs:13`, `LabeledNode.cs:14`, `ToggleNode.cs:15`.
- **Finding:** `[assembly: ImportAsIs]` without `Namespace`/`Category` uses the full namespace as category (`VL.Core/src/Import/ImportAsIsAttribute.cs:42-50`: `if (string.IsNullOrEmpty(Namespace)) cat = ns`). So every public type of `VL.Qt.Elements` appears under `VL.Qt.Elements`, of `VL.Qt` under `VL.Qt`, of `VL.Qt.Nodes` under `VL.Qt.Nodes`, and so on: a second top-level tree `VL` next to `Qt`. The help patches prove it: 27 IOBox type references point there (23 × `VL.Qt.Elements`, e.g. `ShapeCap`, `PointerButtons`, `ImageFillMode`; `VL.Qt.Paths`; `VL.Qt.Nodes` for `LabelPosition`; `VL.Qt` for `Space`). Besides the pin types, this tree offers the concrete element classes (`Button`, `CheckBox`, `Switch`, `SpinBox`, `TextField`, `Rectangle`, `Shape`, … with a public constructor and `init` properties), `ElementSink` with `TakeClicks`/`Resize`, `SinkState` with public setters, `SinkSlot`, `ElementType` (the protocol's type ids), `Geometry.ToQtAlignment` (returns a Qt flag int) and `Shape.ExtentOf`. A user who searches "Rectangle" finds `Rectangle [Qt.Elements]` (the node) and the type `Rectangle [VL.Qt.Elements]`; `CheckBox`/`Switch`/`SpinBox`/`TextField` are Qt names the nodes deliberately avoid (Toggle, Input (Int), TextInput). The three enums of `VL.Qt.Nodes` sit in a category of their own, away from the other enums. The `[Smell]` rule of AGENTS.md cannot fix this alone: a smell on a class does not cascade to its members (design-guidelines.md, "Applying Aspects" item 4).
- **Proposal:**
  1. Make the implementation types `internal`: the sixteen concrete element classes, `ElementSink`, `SinkState`, `SinkSlot`, `ElementType`, `Geometry`; make `Element.Type` and `Element.Sink` `internal`. `InternalsVisibleTo` already covers VL.Qt.Stride, VL.Qt.Audio, the tests, SmokeTest and Probe (`src/VL.Qt/VL.Qt.csproj:35-40`). If Sebastian wants C# users of other libraries to build elements (Q5), keep the classes public and put `[Smell(SymbolSmell.Hidden)]` on each class and on each member instead.
  2. Move the assembly attribute out of `Renderer.cs` into `src/VL.Qt/Properties/AssemblyInfo.cs` (as `VL.Stride.Runtime/src/Properties/AssemblyInfo.cs` does) and write it as `[assembly: ImportAsIs(Namespace = "VL.Qt", Category = "Qt")]`. Same in the two other assemblies (`Namespace = "VL.Qt.Stride"` / `"VL.Qt.Audio"`, `Category = "Qt"`). Then `VL.Qt.Elements` → `Qt.Elements`, `VL.Qt.Paths` → `Qt.Paths`, `VL.Qt.Images` → `Qt.Images`, `VL.Qt` → `Qt`.
  3. Move `BlendMode` to `VL.Qt.Elements` (or a new `Effects` namespace beside `Effect`), `LabelPosition` and `ToggleLook` to `VL.Qt.Elements`, each into its own file (K3), so all pin enums share `Qt.Elements`.
  4. `Initialization` stays public (StandardLibs convention, comment at `Initialization.cs:11`) and gets `[Smell(SymbolSmell.Hidden)]`.
  5. Before merging, check in vvvv that explicit `ProcessNode(Category = …)` and `[Category]`/`[SkipCategory]` categories stay absolute when the assembly attribute has a `Category` root (`ProcessNodeAttribute.cs:29` only says "leave empty to use the one from the class"). If they are prefixed, drop `Category = "Qt"` and keep `Namespace = "VL"` instead (`VL.Qt.Elements` → `Qt.Elements` as well).
  6. Update AGENTS.md ("The assembly imports its public types with `[assembly: ImportAsIs]` (`src/VL.Qt/Renderer.cs`)").
- **Effort:** M, mechanical: partly (accessibility and the attribute are mechanical; the help patch references need vvvv or a script)
- **Touches:** `src/VL.Qt/Renderer.cs`, new `src/VL.Qt/Properties/AssemblyInfo.cs`, `src/VL.Qt/Elements/{Button,CheckBox,Column,Group,Image,QmlElement,Rectangle,Row,ShaderEffect,Shape,Slider,SpinBox,Switch,Text,TextField,Video,Element,ElementSink,ElementType}.cs`, `src/VL.Qt/Geometry.cs`, `src/VL.Qt/Initialization.cs`, `src/VL.Qt/Nodes/Effects/BlendNode.cs`, `src/VL.Qt/Nodes/LabeledNode.cs`, `src/VL.Qt/Nodes/ToggleNode.cs` (+ new enum files), `src/VL.Qt.Stride/QtTextureNode.cs`, `src/VL.Qt.Audio/VideoAudioNode.cs`, `help/Qt/*.vl` with `LastCategoryFullName="VL.Qt…"`, `tests/qttexture/QtTexture01_Basic.vl`, `AGENTS.md`
- **Depends on:** none (do it before API-02, which moves categories again)
- **Verification:** build, unit tests and SmokeTest (they use `InternalsVisibleTo`); `tools/vvvvc.ps1` on every help patch; in vvvv the node browser shows no `VL` top-level category and no `CheckBox`/`ElementSink`; `grep -r 'LastCategoryFullName="VL.Qt' help tests` is empty.

### API-02 Category tree by what the user looks for
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: the nodes listed below change category (saved patches re-resolve by name and pins; help patches must be re-saved).
- **Where:** `src/VL.Qt/Categories.cs:9-27`; `src/VL.Qt/Nodes/Effects/{Blur,ColorAdjust,Colorize,DropShadow,Mask}Node.cs` (category `MultiEffect`); `src/VL.Qt/Nodes/{LoadImageNode,LoadImageAsyncNode,ToQtImageNode,VideoSourceToQtImageNode,SnapshotNode,FileWriterNode,FileWriterElementNode}.cs` (category `Qt`); `src/VL.Qt/Nodes/ShaderEffectNode.cs:26`, `src/VL.Qt/Nodes/UniformNodes.cs:15`, `src/VL.Qt/Nodes/QmlElementNode.cs:28`; `src/VL.Qt.Stride/QtTextureNode.cs:21`, `ToTexturePixelsNode.cs:17`, `QtRendererNode.cs:27`.
- **Finding:**
  - `Qt.Effects.MultiEffect` names a Qt implementation detail (Qt merges these five into one `MultiEffect` item, `Categories.cs:26`). A user looking for "Blur" does not know that Blur is a MultiEffect and DirectionalBlur is not; Skia keeps all image filters in one category. That several effects merge into one layer is worth saying, but in the node help, not as a category.
  - The root `Qt` holds Renderer, WindowSettings, LoadFont, LoadImage, LoadImage (Async), ToQtImage, VideoSourceToQtImage, Snapshot, FileWriter, FileWriter (Element), two TooltipPreview, and from VL.Qt.Stride QtTexture and ToTexturePixels: rendering, files, images and Stride in one list.
  - Shader-based nodes are in three places: `ShaderEffect [Qt.Elements]`, uniform nodes and `Sampler` in `Qt.Elements.Uniform`, generated shader nodes in `Qt.Elements.Shaders` (`ShaderNodeFactory.cs:56`). QML: `QmlElement [Qt.Elements]`, generated nodes in `Qt.Elements.Qml` (`QmlNodeFactory.cs:56`).
  - VL.Qt.Stride puts `QtRenderer` into `Stride.Rendering` (the category of Stride's renderers) but `QtTexture` into `Qt`. VL.Stride puts its Skia counterpart `SkiaTexture` into `Stride.Textures.Source` (`VL.Stride/help/Materials/…`: `LastCategoryFullName="Stride.Textures.Source"`).
- **Proposal:** the tree in section 4: merge `Qt.Effects.MultiEffect` into `Qt.Effects` and delete the constant `Categories.MultiEffect` (mention the merging in the `<summary>` of the five nodes: "Merges with Blur, DropShadow, ColorAdjust, Colorize and Mask of the same chain into one layer"); new `Categories.Images = "Qt.Images"` for the seven image/file nodes; `ShaderEffect`, the uniform nodes and `Sampler` into `Qt.Elements.Shaders` (constant `Categories.Shaders`, used by `ShaderNodeFactory.DefaultCategory`); `QmlElement` into `Qt.Elements.Qml` (constant `Categories.Qml`, used by `QmlNodeFactory.DefaultCategory`); `QtTexture` and `ToTexturePixels` into `Stride.Textures.Source`. Remove the `Categories` doc comment that lists widget names (`Categories.cs:14`), it goes stale with API-07.
- **Effort:** M, mechanical: yes
- **Touches:** `src/VL.Qt/Categories.cs`, the 15 node files listed under Where, `src/VL.Qt/Shaders/ShaderNodeFactory.cs`, `src/VL.Qt/Qml/QmlNodeFactory.cs`, help patches that contain these nodes, `docs/` pages naming the categories (search `Qt.Effects.MultiEffect`, `Qt.Elements.Uniform`).
- **Depends on:** API-01
- **Verification:** `python tools/vl_pin_catalog.py` shows the new categories; `tools/vvvvc.ps1 -File` on HowTo Blur, HowTo Set a Sampler, HowTo Write an Element to a File, the shader and QML help patches; unit tests of the factories that assert `DefaultCategory`.

### API-03 Widgets: no visible pin that the default ignores
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: pin visibility only (patches keep working; IOBoxes on `Size` stay).
- **Where:** `src/VL.Qt/Nodes/ButtonNode.cs`, `SliderNode.cs`, `ToggleNode.cs`, `InputIntNode.cs`, `TextInputNode.cs`, `QmlElementNode.cs` (catalog: `Size` visible, `Auto Size = True (optional)`).
- **Finding:** on the five widgets and QmlElement, `Size` is a visible pin with a default (`1, 0.4`, `2, 0.28`, …) while `Auto Size` is hidden and on. Its doc says "On: Size is ignored" (`ButtonNode.cs:56`, `SliderNode.cs:60`, `ToggleNode.cs:80`). A user who changes `Size` sees nothing happen and has to find a hidden pin. The containers solve the same pair the other way: `Size` and `Auto Size` are both optional (Group, Row, Column).
- **Proposal:** on the widgets and QmlElement make `Size` optional, like on the containers, and keep `Auto Size` optional directly before it in the order `Auto Size, Size`. Update `<param name="size">`: "Used when Auto Size is off." ShaderEffect is fine: there Auto Size only acts with an Input (`ShaderEffectNode.cs:97`).
- **Effort:** S, mechanical: yes
- **Touches:** the six node files, help patches HowTo Button/Slider/Toggle/Input/TextInput/QmlElement (only if they show an IOBox on `Size` without `Auto Size`)
- **Depends on:** none
- **Verification:** `python tools/vl-patch-check.py` (hidden-non-default on `Size` where a patch sets it → `--fix` makes it visible); `tools/vvvvc.ps1` on the widget help patches.

### API-04 Complete the common pin set and its order
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: renamed pin `Children` → `Input` on three nodes; new optional pins; changed pin order (patches keep their links).
- **Where:** `src/VL.Qt/Nodes/RowNode.cs`, `ColumnNode.cs`, `RowSpectralNode.cs`, `ColumnSpectralNode.cs` (no `Style`); `GroupSpectralNode.cs`, `RowSpectralNode.cs`, `ColumnSpectralNode.cs` (`Children: IEnumerable<IElement?>`); `LabeledNode.cs` (order, no `Clip`/`Debug`); `QmlElementNode.cs` (order).
- **Finding:**
  - `Style` (optional) is on Group and Group (Spectral) and on every widget, but not on Row, Column and their spectral versions. Style is inherited down the tree (`IStylable.cs:7-9`: "every Group's style on the way"), so a user who lays out widgets in a Column has to wrap it in a Group only to set a style.
  - The spectral containers call their input `Children`; the non-spectral ones and Skia's `Group (Spectral)` call it `Input` (`VL.Skia.vl`, Group (Spectral): `Input, Output`).
  - Labeled's order is `Element, Text, Label Width, Label Position, Spacing, Transform, Anchor, Label Height, Opacity, Interaction, Effect`: the label pins are split by Transform/Anchor, and Clip and Debug are missing although every other element has them.
  - QmlElement puts its content (`File, Properties`) before the geometry; Image and Video put their content (`Image`, `Path`) after `Anchor`. Rule in the code today, and in Skia's Rectangle and Text: geometry-defining pins first (Size, Radius, Points, From/To, Path), then `Transform, Anchor`, then content and look, then the tail.
- **Proposal:** add `[Pin(Visibility = PinVisibility.Optional)] Style? style` after `Effect` on the four Row/Column nodes (the element type already has `Style`, `Element.cs:52`). Rename the parameter `children` → `input` on the three spectral nodes (keep type `IEnumerable<IElement?>`). Labeled: `Element, Text, Label Position, Label Width, Label Height (optional), Spacing (optional), Transform, Anchor, Opacity, Clip (optional, new), Debug (optional, new), Interaction, Effect`. QmlElement: `Size, Auto Size, Transform, Anchor, File, Properties, …tail`. Write the order rule into `docs/help-patches.md` or the element catalog (API-14) so new nodes follow it.
- **Effort:** M, mechanical: no
- **Touches:** the eight node files, `src/VL.Qt/Elements/Labeled*` if Clip/Debug need wiring (Labeled is built from a Row, check), help patches of Row/Column, Spectral, Labeled, QmlElement
- **Depends on:** API-03 (QmlElement order)
- **Verification:** unit test that a style on a Column reaches a Button child (as the existing Group style tests); `tools/vl-patch-check.py` (missing-pin for `Children`); `tools/vvvvc.ps1` on HowTo Layout a Spread with Row and Column (Spectral), HowTo Label an Element.

### API-05 One name for the fill colour, sensible stroke defaults
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: pin rename on four nodes, one default change.
- **Where:** `src/VL.Qt/Nodes/RectangleNode.cs`, `CircleNode.cs` (`Color`), `PolygonNode.cs`, `PathNode.cs` (`Fill`); `src/VL.Qt/Elements/Rectangle.cs:11` (property `Fill`).
- **Finding:** the same thing, the colour that fills a shape, is `Color` on Rectangle and Circle and `Fill` on Polygon and Path; the element property behind Rectangle's `Color` is called `Fill`. Next to it all four have `Stroke Color` and `Stroke Width`, so `Fill` reads like a bool and `Color` like the only colour. Skia avoids the question with a `Paint` pin. Polygon defaults to a white fill and a white stroke (`1, 1, 1, 1` both): raising `Stroke Width` shows nothing, while Rectangle and Circle default the stroke to black.
- **Proposal:** `Fill Color` on all four (pairs with `Stroke Color`); Polygon `Stroke Color` default `0, 0, 0, 1` like Rectangle and Circle. Text keeps `Color` (it is the text colour, there is no stroke). Alternative: `Color` on all four; see Q4.
- **Effort:** S, mechanical: yes
- **Touches:** the four node files, help patches HowTo Draw a Rectangle/Circle/Polygon/Path and every patch that links these pins
- **Depends on:** none
- **Verification:** `tools/vl-patch-check.py` (missing-pin finds every link to the old names), `tools/vvvvc.ps1` on the four help patches, SmokeTest PNGs unchanged.

### API-06 Angles in cycles on DirectionalBlur and RadialBlur
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: the value of `Angle` means something else (a patch with `8` turns eight times).
- **Where:** `src/VL.Qt/Nodes/Effects/DirectionalBlurNode.cs:52` ("in degrees"), `src/VL.Qt/Nodes/Effects/RadialBlurNode.cs:49` ("Total rotation of the blur in degrees", default `8`).
- **Finding:** vvvv's standard unit for angles is cycles (design-guidelines.md, "Standard Types and Units"). VL.Qt follows it in the path nodes (`AddArcNode` start/sweep, `ArcTo` `X Axis Rotate`), so the same library takes cycles for an arc and degrees for a blur. A user connecting an LFO to `Angle` gets a near-invisible rotation.
- **Proposal:** take cycles on both pins and convert once in the node; RadialBlur default `0.02` (≈ 8°). Docs: "Direction of the blur in cycles: 0 = horizontal, 0.25 = vertical, increases clockwise on screen."
- **Effort:** S, mechanical: no
- **Touches:** the two node files, their help patches
- **Depends on:** none
- **Verification:** SmokeTest/help PNG of HowTo Blur (or the effect help patch) looks the same with the converted default.

### API-07 Widget names after ImGui
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: two node renames.
- **Where:** `src/VL.Qt/Nodes/TextInputNode.cs:20`, `src/VL.Qt/Nodes/SliderNode.cs:20`, `src/VL.Qt/Nodes/InputIntNode.cs:20`.
- **Finding:** VL.Qt already took ImGui's `Input (Int)`, but the text field is `TextInput` where ImGui has `Input (String)` (`VL.ImGui/src/Widgets/Input/InputText.cs:6`), and the slider is `Slider` where ImGui has `Slider (Float)` (`VL.ImGui/src/Widgets/Slider/SliderFloat.cs:5`). A vvvv user types "Input" and gets one of the two VL.Qt input widgets; adding `Slider (Int)` later would leave `Slider` as the odd float one.
- **Proposal:** `TextInput` → `Input (String)`; `Slider` → `Slider (Float)`. Toggle stays (it covers ImGui's Checkbox and a switch with `Look`), Button stays. Rename the help patches and `help/Help.xml` entries with them.
- **Effort:** S, mechanical: yes
- **Touches:** the two node files, `help/Qt/HowTo … TextInput/Slider …vl`, `help/Help.xml`, docs naming the nodes, `VL.Qt.vl` if it references them
- **Depends on:** none
- **Verification:** `tools/vvvvc.ps1` on the renamed help patches; Help Browser lists them.

### API-08 Event pin names say what they carry
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: pin renames.
- **Where:** `src/VL.Qt/Nodes/ButtonNode.cs` (`Is Clicked: IChannel<Unit>`, out `Bang`), `SliderNode.cs:76`, `ToggleNode.cs:93`, `InputIntNode.cs`, `TextInputNode.cs` (out `Bang`), `src/VL.Qt/Nodes/Interaction/ClickNode.cs` (`Is Clicked: IChannel<Unit>`, `Long Pressed: IChannel<Unit>`), `DragNode.cs` (`Minimum`, `Maximum`).
- **Finding:** `Bang` means "clicked since the last frame" on Button (`ButtonNode.cs:69`) and "the user changed the value" on the four value widgets (`SliderNode.cs:76`, `ToggleNode.cs:93`); the same name for two meanings next to each other in one category. Skia names the second meaning `Has Changed` (output of many Skia nodes in `VL.Skia.vl`). `Is Clicked` and `Long Pressed` are `IChannel<Unit>`, i.e. events, but `Is …` reads as a bool state, as it rightly does on `Is Pressed`, `Is Hovered`, `Is Dragging`, `Is Active` (all `IChannel<bool>`). vvvv names events `On …` (design-guidelines.md, async pattern `On Completed`). Drag says `Minimum`/`Maximum`, the widgets say `Min`/`Max` (ImGui: `Min`/`Max`).
- **Proposal:** value widgets: `Bang` → `Has Changed`; Button keeps `Bang`. Button and Click: `Is Clicked` → `On Click`; Click: `Long Pressed` → `On Long Press`. Drag: `Minimum`/`Maximum` → `Min`/`Max`. Set the names with `[Pin(Name = …)]` only where the C# name cannot give them.
- **Effort:** S, mechanical: yes
- **Touches:** the seven node files, help patches HowTo React to Hover and Click, HowTo Drag an Element, the widget help patches
- **Depends on:** API-07 (same files)
- **Verification:** `tools/vl-patch-check.py` (missing-pin), `tools/vvvvc.ps1` on the affected help patches.

### API-09 Uniform nodes named `Uniform (Float)`, not `Float`
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: node renames (Advanced nodes).
- **Where:** `src/VL.Qt/Nodes/UniformNodes.cs:22-78` (`Float`, `Int`, `Bool`, `Vector2`, `Vector3`, `Vector4`, `Color`, `Matrix`, `Texture`, `Image`).
- **Finding:** the node names are the names of the value types. In the node browser `Float [Qt.Elements.Uniform]` and `Color [Qt.Elements.Uniform]` stand next to the core `Float`/`Color` nodes, and a node in a patch shows only its name, so the reader of a patch sees a node "Color" that returns a `Uniform`. vvvv shows the variant in the version: `Name (Version)` (design-guidelines.md).
- **Proposal:** one static method per type with the C# name `Float` etc. stays, and the category becomes `Qt.Elements.Shaders` (API-02) with names `Uniform (Float)`, `Uniform (Int)`, … `Uniform (Image)`. In C# that is either a `[Category]` on the class plus a version through the method name (`Uniform_Float` is not vvvv style), or, cleaner, the class `UniformNodes` becomes a `[ProcessNode]`-free static class in category `Qt.Elements.Shaders` whose methods are named `Uniform` with overloads, which vvvv shows as `Uniform` with differing pin types; check in vvvv which form gives `Uniform (Float)`. `Sampler` (same class, `UniformNodes.cs:89`) moves with it and keeps its name.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/UniformNodes.cs`, help patches HowTo Write a Shader / Set a Sampler (whatever uses uniform nodes)
- **Depends on:** API-02
- **Verification:** `tools/vvvvc.ps1` on the shader help patches; node browser search "Uniform".

### API-10 Type and enum names: one concept, one type
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: pin types change.
- **Where:** `src/VL.Qt/Elements/Image.cs:4` (`ImageFillMode` Stretch, Fit, Crop, Tile), `src/VL.Qt/Elements/Video.cs:6` (`VideoFillMode` Stretch, Fit, Crop); `src/VL.Qt/Styles/StyleNodes.cs:271,292,309` (`fontWeight` as `int`) vs `src/VL.Qt/Elements/Text.cs:6` (`FontWeight` enum on the Text node); `src/VL.Qt.Stride/QtTextureNode.cs` (`Multisample Count: VL.Qt.MultisampleCount` next to Stride's `Format: PixelFormat`).
- **Finding:** two fill-mode enums with the same values minus `Tile` (and different defaults: Image `Stretch`, Video `Fit`, which may be intended). The style `Text`/`FontWeight` setters and getters take an `int` while the Text node takes the `FontWeight` enum, so a user cannot connect the same IOBox to both. `MultisampleCount` deliberately copies `Stride.Graphics.MultisampleCount` (`src/VL.Qt/MultisampleCount.cs:3-6`); on `QtTexture`, which lives in a Stride patch, the user then has two enums of the same name and members in the IOBox type list.
- **Proposal:** one `FillMode` enum (Stretch, Fit, Crop, Tile) in `src/VL.Qt/Elements/FillMode.cs`, Video rejects or maps `Tile` with a node warning (or keep two enums if Video cannot tile, but then name the shared values in one place). Style: `Optional<FontWeight>` / `FontWeight` instead of `int` in `TextNodes.Text`, `FontWeight`, `GetText`, `GetFontWeight` and in `Style.TextStyle.FontWeight` (`Style.cs:107`). QtTexture: take `Stride.Graphics.MultisampleCount` and convert in the node; Renderer keeps VL.Qt's enum. Keep the `Qt` prefix rule as it is: only where the name would collide with a type a vvvv user already has on the same kind of pin (`QtImage` vs `IImage`/`SKImage`, `QtPath` vs `Path`/`SKPath`); `Element`, `Effect`, `Interaction`, `Style`, `Sampler`, `Uniform`, `Space` need no prefix. Write that rule into `docs/decisions/` (one sentence).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Elements/Image.cs`, `Video.cs`, new `FillMode.cs`, `src/VL.Qt/Nodes/ImageNode.cs`, `VideoNode.cs`, `src/VL.Qt.Audio/VideoAudioNode.cs`, the reconciler's mapping of fill modes (search `VideoFillMode`), `src/VL.Qt/Styles/StyleNodes.cs`, `Style.cs`, style application code (search `FontWeight`), `src/VL.Qt.Stride/QtTextureNode.cs`, help patches with `ImageFillMode` IOBoxes (4) and style text patches
- **Depends on:** API-01
- **Verification:** unit tests of the style merge/getters; SmokeTest image and video scenarios; `tools/vvvvc.ps1` on HowTo Use an IImage, HowTo Preload Images, HowTo Show a VL.Video player in Qt, the style help patches.

### API-11 Path builder: fix where Skia is inconsistent with itself
- **Kind:** naming
- **Severity:** could
- **Breaking:** yes: pin renames and defaults.
- **Where:** `src/VL.Qt/Nodes/Paths/AddCircleNode.cs:21`, `AddRoundRectNode.cs` (`Dir`), `CubicToNode.cs:24,60` (`EndPoint`), `AddPolyNode.cs` (`Close`), `AddArcNode.cs`, `AddOvalNode.cs`, `AddRectNode.cs`, `AddRoundRectNode.cs` (default `0, 0, 0, 0`).
- **Finding:** VL.Qt copied Skia's names, including Skia's own inconsistencies: `Dir` on AddCircle and AddRoundRect but `Direction` on AddOval and AddRect (all four in Skia too, `VL.Skia.vl`); `EndPoint` on CubicTo but `End Point` on QuadTo (Skia's QuadTo says `End Point`); `Close` on AddPoly but `Closed` on Spline, Bezier (Handles), Polyline. The rectangle-based builders default to an empty rectangle and AddArc to a sweep of 0, so a freshly placed node adds nothing (AGENTS.md: "Set every default explicitly … rarely what the help patch needs"; "Choose defaults so the effect is visible").
- **Proposal:** `Direction` everywhere, `End Point` on both CubicTo nodes, `Closed` on AddPoly; defaults `0, 0, 1, 1` for `Bounds`/`Rectangle`, `0.25` for AddArc's `Sweep Angle`, `0.1, 0.1` for AddRoundRect's `Radius`. Keep everything else of the Skia mirror (section 2).
- **Effort:** S, mechanical: yes
- **Touches:** the six node files, HowTo Build a Path and other path help patches
- **Depends on:** none
- **Verification:** `tools/vl-patch-check.py` (missing-pin, default-iobox hints after the default change), `tools/vvvvc.ps1` on the path help patches, path unit tests.

### API-12 Image and file nodes: names and first pin
- **Kind:** naming
- **Severity:** could
- **Breaking:** yes: one node rename, pin renames.
- **Where:** `src/VL.Qt/Nodes/VideoSourceToQtImageNode.cs:14`, `SnapshotNode.cs`, `FileWriterElementNode.cs` (`Element` input), `src/VL.Qt/Nodes/Interaction/TouchPointsNode.cs:21` (`Touch Points`).
- **Finding:** `VideoSourceToQtImage` is a conversion to the same type as `ToQtImage`; vvvv names conversions `To…` and puts the source type in the version (design-guidelines.md, "From.. / To.."). `Touch Points` is the only node name with a space (node names are CamelCase without spaces, design-guidelines.md). Snapshot and FileWriter (Element) call their main input `Element` where vvvv nodes (and Skia's `ImageWriter`, `VL.Skia.vl`) call it `Input`; FileWriter (image) has `Image`.
- **Proposal:** `VideoSourceToQtImage` → `ToQtImage (VideoSource)`; `Touch Points` → `TouchPoints`; `Element` → `Input` on Snapshot and FileWriter (Element). The async outputs (`Is Ready`, `Is Done`, `Is Loaded`) stay; they are each clear on their node.
- **Effort:** S, mechanical: yes
- **Touches:** the four node files, HowTo Show a VL.Video player in Qt, HowTo Use Touch Points, the snapshot/file writer help patches, `help/Help.xml` if titles name the node
- **Depends on:** API-02
- **Verification:** `tools/vvvvc.ps1` on the affected help patches.

### API-13 Effect pins: say what Max and Blur are
- **Kind:** naming
- **Severity:** could
- **Breaking:** yes: pin renames.
- **Where:** `src/VL.Qt/Nodes/Effects/BlurNode.cs:28-29` (`Max`, `Multiplier`), `DropShadowNode.cs:30` (`Blur`).
- **Finding:** `Max` alone reads like the upper end of a range; it is the maximum blur radius in pixels, and expensive to change (`BlurNode.cs:28`). `Multiplier` multiplies that radius. DropShadow's `Blur` is a 0..1 softness that depends on `Max` of a Blur node in the same chain (`DropShadowNode.cs:30`); in a chain next to a node called Blur, a pin called Blur is ambiguous.
- **Proposal:** `Max` → `Max Radius`, `Multiplier` → `Radius Multiplier`, DropShadow `Blur` → `Softness`. Glow and InnerShadow `Radius` stay (units of the space).
- **Effort:** S, mechanical: yes
- **Touches:** the two node files, the effect help patches
- **Depends on:** API-02
- **Verification:** `tools/vl-patch-check.py`, `tools/vvvvc.ps1` on the effect help patches.

### API-14 Rewrite the element model page to the pins that exist
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/element-catalog-v1.md:8-14`; `docs/versioning.md:24-26`; `AGENTS.md` (Nodes, "A node or pin is renamed or removed only after `[Smell(SymbolSmell.Obsolete)]`").
- **Finding:** the page says the common pins are "Bounds, Transform, Pivot, Opacity, Visible, Clip" and that `Pivot` defaults to Center. The nodes have `Size` (plus `Auto Size`), `Transform`, `Anchor` (default `TopLeft`), `Opacity`, `Clip`, `Debug`, `Interaction`, `Effect`, `Style`, and no `Bounds`, `Pivot` or `Visible` (catalog). A contributor who adds an element from this page builds the wrong pins. The "Obsolete first" rule of versioning.md and AGENTS.md is stated without the pre-1.0 exception that the refactor relies on (versioning.md:3 says 0.9.x has no guarantees, but the Obsolete section reads as unconditional).
- **Proposal:** rewrite "Pins" and "Geometry" of `element-catalog-v1.md` to the present: the pin order rule of API-04, the common tail, Size/Auto Size, Anchor default TopLeft and why (the reconciler folds the anchor into the transform), no Visible pin (or the decision of Q2). In versioning.md and AGENTS.md add "from 1.0.0 on" to the Obsolete rule.
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/element-catalog-v1.md`, `docs/versioning.md`, `AGENTS.md`
- **Depends on:** API-04, Q2
- **Verification:** read against `python tools/vl_pin_catalog.py`.

### API-15 Hide what no patch should place
- **Kind:** api
- **Severity:** could
- **Breaking:** no (aspects only filter the node browser; existing patches keep resolving, design-guidelines.md "Aspects").
- **Where:** `src/VL.Qt/Nodes/ElementTooltipPreview.cs:19`, `src/VL.Qt/Nodes/QtImageTooltipPreview.cs:11` (`Advanced`), `src/VL.Qt/Initialization.cs:13`.
- **Finding:** the two `TooltipPreview [Qt]` nodes exist for vvvv's tooltips (used by the patched viewers in `VL.Qt.HDE.vl`: `ElementViewer`, `QtImageViewer`); a user never places them, yet `Advanced` still lists them with the advanced filter. `Initialization` is an imported public type without a smell.
- **Proposal:** `[Smell(SymbolSmell.Hidden)]` on both TooltipPreview classes and on `Initialization` (together with API-01 item 4).
- **Effort:** S, mechanical: yes
- **Touches:** the three files
- **Depends on:** none
- **Verification:** tooltip of an Element pin in vvvv still shows the preview.

## 4 Target structure

Category tree after API-01/02/07/09/12 (nodes; `→` = renamed or moved):

```
Qt                      Renderer, WindowSettings, LoadFont
                        TooltipPreview ×2 (Hidden)
Qt.Elements             Rectangle, Circle, Line, Polyline, Polygon, Path, Text, Image, Video,
                        Video (VL.Audio), Group, Group (Spectral), Row, Row (Spectral),
                        Column, Column (Spectral), Labeled
                        types: Element, IElement, Effect, Interaction, TouchPoint, QmlSignal,
                        all pin enums (incl. BlendMode, LabelPosition, ToggleLook ← VL.Qt.Nodes;
                        FillMode ← ImageFillMode + VideoFillMode)
Qt.Elements.Shaders     ShaderEffect (Advanced) ← Qt.Elements
                        Uniform (Float … Image) (Advanced) ← Float … Image [Qt.Elements.Uniform]
                        Sampler ← Qt.Elements.Uniform; types Sampler, Uniform, Sampler enums
                        generated shader element nodes (default category)
Qt.Elements.Qml         QmlElement (Experimental) ← Qt.Elements; generated QML nodes (default)
Qt.Widgets              Button, Toggle, Slider (Float) ← Slider, Input (Int), Input (String) ← TextInput
Qt.Interaction          Click, Drag, Hover, Pinch, Point, TouchPoints ← Touch Points, Wheel
Qt.Effects              Blend, Blur, ColorAdjust, Colorize, DirectionalBlur, Displace, DropShadow,
                        Glow, InnerShadow, Mask, RadialBlur, ZoomBlur (MultiEffect merged in),
                        generated shader chain nodes
Qt.Paths                path builder (unchanged names), types QtPath, PathDirection, PathAddMode, HandleMode
Qt.Images (new)         LoadImage, LoadImage (Async), ToQtImage, ToQtImage (VideoSource) ← VideoSourceToQtImage,
                        Snapshot, FileWriter, FileWriter (Element); types QtImage, QtImageState
Qt.Style (+ .Frame .Text .Indicator .Track)   unchanged (Dark, Light patched in VL.Qt.vl)
Stride.Rendering        QtRenderer (Experimental)
Stride.Textures.Source  QtTexture (Experimental) ← Qt, ToTexturePixels (Experimental) ← Qt
```

Public C# surface of `VL.Qt` after API-01: node classes (`*Node`, `Renderer`), static node classes (`StyleNodes`, `FrameNodes`, `TextNodes`, `IndicatorNodes`, `TrackNodes`, `UniformNodes`), pin types (`Element`, `IElement`, `Effect`, `Interaction`, `Style`, `IStylable`, `QtImage`, `QtPath`, `Sampler`, `Uniform`, `TouchPoint`, `QmlSignal`, `PerfStats`, `WindowSettings`), pin enums, `Initialization` (Hidden). Internal: the sixteen concrete element classes, `ElementSink`, `SinkState`, `SinkSlot`, `ElementType`, `Geometry`, `Element.Type`, `Element.Sink`. Assembly attributes in `src/VL.Qt/Properties/AssemblyInfo.cs` (and the same file in VL.Qt.Stride and VL.Qt.Audio) instead of `Renderer.cs`, `QtTextureNode.cs`, `VideoAudioNode.cs`.

## 5 Questions for Sebastian

**Q1 Renderer window pins and WindowSettings: one way or two?** Renderer has `Title`, `Borderless`, `Fullscreen` and a `Window: WindowSettings` pin whose `Title`, `Borderless`, `Fullscreen` "win over the matching pin of this node" (`Renderer.cs:119`, `WindowSettingsNode.cs:23-25`). Two ways to set the same value, with a precedence rule the user must know. (a) Keep both (today). (b) Renderer keeps Title/Borderless/Fullscreen (as Skia's Renderer has Title, `SkiaRendererNode.cs:157`), WindowSettings keeps only what Renderer has no pin for (`Icon`, `Bounds` on create). (c) Remove the three pins from Renderer, everything goes through WindowSettings. Recommendation: (b): the common pins stay where a Skia user looks, no precedence rule.

**Q2 An `Enabled` (or `Visible`) pin on elements?** Every Skia layer node has `Enabled` (`VL.Skia.vl`: Rectangle, Circle, Text, Group … `Enabled`), and element-catalog-v1.md lists `Visible` as a common pin, but no VL.Qt element has one; a user switches an element off with Opacity 0 (still hit-tested, still costs) or by removing it from the spread. (a) No pin (today; document Opacity/spread). (b) Optional `Enabled` pin in the common tail, default true, that removes the element from the scene (Qt term: the item's `visible` property, which also stops input). Recommendation: (b), named `Enabled` as in Skia, optional, placed before `Opacity`.

**Q3 `IElement` or `Element` on element inputs?** Containers take `Spread<IElement?>` (so a self-made `IStylable` widget fits), while `Renderer.Input`, `Labeled.Element`, `ShaderEffect.Input`, `Snapshot`, Blend `Layer`, Mask `Mask`, Displace `Map` take `Element`, and every output is `Element`. A user sees two type names for one concept, and a stylable widget cannot go straight into Labeled or the Renderer. Skia uses `ILayer` on both ends. (a) Keep (today). (b) `IElement` on every element input; the reconciler builds an `IStylable` there with the base style. Recommendation: (a) for 0.10.0, with the difference explained in the summary of `IElement`; (b) is a reconciler change (review 05) and should only be done if stylable widgets at the root are wanted.

**Q4 Fill colour pin name (API-05):** `Fill Color` (pairs with `Stroke Color`, recommended) or `Color` (shorter, as on Text and as the Skia `Fill` paint's pin)?

**Q5 Concrete element classes public for C# users?** Making `Rectangle`, `Text`, `Group`, … internal (API-01) means another C# library cannot build elements without the nodes. (a) Internal (recommended for 0.10.0: they carry `ElementSink`/`SinkSlot` plumbing that is not an API, and nobody outside uses them today). (b) Public with `[Smell(Hidden)]` on each class and member, and a documented C# API later.

## 6 For other areas

- Tools: `tools/vl_pin_catalog.py` prints ShaderEffect `Fragment Code: string = '2'`; the default is a template constant, the catalog parses it wrongly (also affects `vl-patch-check.py` default checks on that pin).
- Interaction (review 15): the unit of Pinch `Rotation` has no `<param>` doc in `src/VL.Qt/Nodes/Interaction/PinchNode.cs` (only the class summary mentions rotation); check that it is cycles like the path nodes.
- Nodes/elements (review 10): the `Debug` doc string is repeated verbatim on every element node (`ButtonNode.cs:65`, `SliderNode.cs:71`, `GroupNode.cs:63`, …) and differs slightly between leaves and containers; a shared `<include>` or a shorter text would keep it in one place.
- Shaders (review 12): `QmlNodeDescription.cs:19-20` states that generated nodes cannot carry the Experimental aspect; if VL.Core has no smell on `NewNodeDescription`, the experimental state of generated QML nodes should at least be in their summary.

## 7 Catalog

One row per change, for a decision line by line. Unchanged nodes and pins are not listed. "Finding" points to the reasoning above.

### Qt.Elements

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| Rectangle / Color | `Color` | `Fill Color` | same meaning as Polygon/Path `Fill`; pairs with `Stroke Color` | API-05 | yes (C4) |
| Circle / Color | `Color` | `Fill Color` | as above | API-05 | yes (C4) |
| Polygon / Fill | `Fill` | `Fill Color` | as above | API-05 | yes (C4) |
| Polygon / Stroke Color default | `1, 1, 1, 1` | `0, 0, 0, 1` | white on a white fill shows nothing; Rectangle/Circle use black | API-05 | yes (Sebastian, 2026-11-19) |
| Path / Fill | `Fill` | `Fill Color` | as above | API-05 | yes (C4) |
| Row, Column, Row (Spectral), Column (Spectral) / Style | missing | `Style` (optional, after `Effect`) | Group has it; style inherits down the tree | API-04 | yes (Sebastian, 2026-11-19) |
| Group (Spectral), Row (Spectral), Column (Spectral) / Children | `Children` | `Input` | Skia `Group (Spectral)` and the non-spectral nodes say `Input` | API-04 | yes (Sebastian, 2026-11-19) |
| Labeled / pin order | `Element, Text, Label Width, Label Position, Spacing, Transform, Anchor, Label Height, …` | `Element, Text, Label Position, Label Width, Label Height, Spacing, Transform, Anchor, …` | label pins together, geometry after content as on the other containers | API-04 | yes (Sebastian, 2026-11-19) |
| Labeled / Clip, Debug | missing | `Clip`, `Debug` (optional) | common tail of every element | API-04 | yes (Sebastian, 2026-11-19) |
| QmlElement / pin order | `File, Properties, Size, Auto Size, Transform, Anchor, …` | `Auto Size, Size, Transform, Anchor, File, Properties, …` | content after geometry, as Image/Video | API-03, API-04 | yes (Sebastian, 2026-11-19) |
| QmlElement / Size | visible | optional | ignored while the hidden `Auto Size` is on | API-03 | yes, right after `Auto Size` (Sebastian, 2026-11-19) |
| Group, Row, Column, their Spectral nodes, ShaderEffect / Size | visible | optional, after `Auto Size` | same pattern: `Auto Size` optional and on by default; added during the catalog review | API-03 | yes: one rule for every node with `Auto Size` on by default (Sebastian, 2026-11-19) |
| QmlElement / category | `Qt.Elements` | `Qt.Elements.Qml` | with the generated QML nodes | API-02 | yes (Sebastian, 2026-11-19) |
| ShaderEffect / category | `Qt.Elements` | `Qt.Elements.Shaders` | with uniforms, Sampler, generated shader nodes | API-02 | yes, and renamed to `Shader` (C14) |
| Image / Fill Mode type | `ImageFillMode` | `FillMode` | one enum for Image and Video | API-10 | yes: `Stretch, Fit, Crop, Tile` (`Tile` kept, not `Wrap`: a fill mode at the image's own size, not a sampler address mode) (Sebastian, 2026-11-19) |
| Video, Video (VL.Audio) / Fill Mode type | `VideoFillMode` | `FillMode` | as above | API-10 | yes; Video draws `Tile` as `Stretch` with a warning on the node, the pin help says so (Sebastian, 2026-11-19) |
| (all elements) / Enabled | missing | `Enabled` (optional, before `Opacity`)? | Skia layers have it; see Q2 | Q2 | `Enabled`, optional, default true, as the last input pin (C2) |

### Qt.Elements.Uniform → Qt.Elements.Shaders

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| Float, Int, Bool, Vector2, Vector3, Vector4, Color, Matrix, Texture, Image | `Float [Qt.Elements.Uniform]` … | `Uniform (Float) [Qt.Elements.Shaders]` … | the node name is a type name; vvvv puts the variant in the version | API-09 | no: names stay (`Float` …), category `Qt.Elements.Shaders.Uniform` (C15) (Sebastian, 2026-11-19) |
| Sampler / category | `Qt.Elements.Uniform` | `Qt.Elements.Shaders` | belongs with the shader nodes | API-02 | yes (C15) |

### Qt.Widgets

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| TextInput | `TextInput` | `Input (String)` | ImGui name; VL.Qt already has `Input (Int)` | API-07 | yes, tags `text, textfield` (C8) |
| Slider | `Slider` | `Slider (Float)` | ImGui name; room for `Slider (Int)` | API-07 | yes (Sebastian, 2026-11-19) |
| Button, Slider, Toggle, Input (Int), TextInput / Size | visible | optional, after `Auto Size` | ignored while the hidden `Auto Size` is on | API-03 | yes (Sebastian, 2026-11-19) |
| Slider, Toggle, Input (Int), TextInput / Bang | `Bang` | `Has Changed` | `Bang` means "clicked" on Button; Skia's name for "changed" | API-08 | yes; Button keeps `Bang` (Sebastian, 2026-11-19) |
| Button / Is Clicked | `Is Clicked` | `On Click` | an event channel (`IChannel<Unit>`), not a bool state | API-08 | yes: `Is …` for states, `On …` for events (Sebastian, 2026-11-19) |

### Qt.Interaction

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| Touch Points | `Touch Points` | `TouchPoints` | node names have no spaces | API-12 | yes (Sebastian, 2026-11-19) |
| Click / Is Clicked | `Is Clicked` | `On Click` | event channel | API-08 | yes (Sebastian, 2026-11-19) |
| Click / Long Pressed | `Long Pressed` | `On Long Press` | event channel | API-08 | yes (Sebastian, 2026-11-19) |
| Drag / Minimum, Maximum | `Minimum`, `Maximum` | `Min`, `Max` | widgets and ImGui say `Min`/`Max` | API-08 | yes (Sebastian, 2026-11-19) |
| Pinch / Minimum Point Count, Maximum Point Count | `Minimum Point Count`, `Maximum Point Count` | `Min Point Count`, `Max Point Count` | one term in the library; added during the catalog review | API-08 | yes (Sebastian, 2026-11-19) |

### Qt.Effects

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| Blur, ColorAdjust, Colorize, DropShadow, Mask / category | `Qt.Effects.MultiEffect` | `Qt.Effects` | MultiEffect is a Qt implementation detail; say it in the node help | API-02 | yes (C11) |
| DirectionalBlur / Angle | degrees | cycles | vvvv angle unit; paths already use cycles | API-06 | yes (C10) |
| RadialBlur / Angle | degrees, default `8` | cycles, default `0.02` | as above | API-06 | cycles (C10), default `0.05` so the effect shows at once (Sebastian, 2026-11-19) |
| Blur / Max | `Max` | `Max Radius` | reads like a range end | API-13 | yes (Sebastian, 2026-11-19); note: the value is in pixels, check against "shader parameters in DIP" (E1) |
| Blur / Multiplier | `Multiplier` | `Radius Multiplier` | says what it multiplies | API-13 | yes (Sebastian, 2026-11-19) |
| DropShadow / Blur | `Blur` | `Softness` | ambiguous next to the Blur node | API-13 | `Blur Amount`, parallel to `Amount` on Blur (Sebastian, 2026-11-19) |

### Qt.Paths

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| AddCircle / Dir | `Dir` | `Direction` | AddOval/AddRect say `Direction` | API-11 | yes (C12) |
| AddRoundRect / Dir | `Dir` | `Direction` | as above | API-11 | yes (C12) |
| CubicTo, CubicTo (Relative) / EndPoint | `EndPoint` | `End Point` | QuadTo and Skia say `End Point` | API-11 | yes (C12) |
| AddPoly / Close | `Close` | `Closed` | Spline, Bezier (Handles), Polyline say `Closed` | API-11 | yes (Sebastian, 2026-11-19) |
| AddArc, AddOval / Bounds default | `0, 0, 0, 0` | `0, 0, 1, 1` | a new node adds nothing | API-11 | yes (Sebastian, 2026-11-19) |
| AddRect, AddRoundRect / Rectangle default | `0, 0, 0, 0` | `0, 0, 1, 1` | as above | API-11 | yes (Sebastian, 2026-11-19) |
| AddArc / Sweep Angle default | `0` | `0.25` | as above | API-11 | yes (Sebastian, 2026-11-19) |
| AddRoundRect / Radius default | `0, 0` | `0.1, 0.1` | shows the rounding | API-11 | yes (Sebastian, 2026-11-19) |

### Qt (root) → Qt.Images, Stride

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| LoadImage, LoadImage (Async), ToQtImage, Snapshot, FileWriter, FileWriter (Element) / category | `Qt` | `Qt.Images` | the root holds 14 unrelated nodes | API-02 | yes (Sebastian, 2026-11-19) |
| VideoSourceToQtImage | `VideoSourceToQtImage [Qt]` | `ToQtImage (VideoSource) [Qt.Images]` | conversion naming `To…` with the source in the version | API-02, API-12 | no: name kept, as VL.Video's `VideoSourceToSKImage`, `VideoSourceToTexture`, `VideoSourceToImage`; category `Qt.Images` (Sebastian, 2026-11-19) |
| Snapshot / Element | `Element` | `Input` | main input of a vvvv node; Skia ImageWriter says `Input` | API-12 | yes (Sebastian, 2026-11-19) |
| FileWriter (Element) / Element | `Element` | `Input` | as above | API-12 | yes (Sebastian, 2026-11-19) |
| Labeled / Element | `Element` | `Input` | as above; added during the catalog review | API-12 | yes (Sebastian, 2026-11-19) |
| TooltipPreview (×2) / aspect | `Advanced` | `Hidden` | only for vvvv's tooltips | API-15 | yes (Sebastian, 2026-11-19) |
| Renderer / Title, Borderless, Fullscreen vs Window | both, Window wins | see Q1 | two ways for one value | Q1 | (b): pins on the Renderer, WindowSettings keeps only `Icon`, `Bounds` (C1) |
| QtTexture / category | `Qt` | `Stride.Textures.Source` | VL.Stride's `SkiaTexture` is there | API-02 | yes (Sebastian, 2026-11-19) |
| ToTexturePixels / category | `Qt` | `Stride.Textures.Source` | belongs with QtTexture | API-02 | `Stride.Input`, next to `MouseRay`: Stride sorts by task, the node maps input and is no texture source (Sebastian, 2026-11-19) |
| QtTexture / Multisample Count type | `MultisampleCount [VL.Qt]` | `Stride.Graphics.MultisampleCount` | same-named enum twice in a Stride patch | API-10 | yes; the Qt Renderer keeps VL.Qt's own enum (Sebastian, 2026-11-19) |

### Qt.Style

| Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| Text, FontWeight [Qt.Style.Text] / Font Weight type | `Optional<int>` | `Optional<FontWeight>` | the Text node takes the enum | API-10 | no, the other way round: `int` everywhere, also on the Text node (default 400); the enum `FontWeight` goes. Reason: Qt and CSS take any weight 1–1000 (variable fonts), an enum would break later. The pin help lists the named steps (100 Thin … 400 Normal … 700 Bold … 900 Black) (Sebastian, 2026-11-19) |
| GetText, GetFontWeight [Qt.Style.Text] / Font Weight type | `int` | `FontWeight` | as above | API-10 | no: stays `int` (see above) |

### Types and enums

| Type | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| all imported types / category | `VL.Qt`, `VL.Qt.Elements`, `VL.Qt.Paths`, `VL.Qt.Nodes`, `VL.Qt.Images`, `VL.Qt.Stride`, `VL.Qt.Audio` | `Qt`, `Qt.Elements`, `Qt.Paths`, (moved), `Qt.Images`, … (`Qt.Style` stays) | `ImportAsIs` without Namespace/Category | API-01 | yes (Sebastian, 2026-11-19) |
| Button, CheckBox, Column, Group, Image, QmlElement, Rectangle, Row, ShaderEffect, Shape, Slider, SpinBox, Switch, Text, TextField, Video (element classes) | public, in the node browser | internal | implementation of the nodes; Qt names the nodes avoid | API-01, Q5 | yes (A6) |
| ElementSink, SinkState, SinkSlot, ElementType, Geometry | public | internal | plumbing between nodes and reconciler | API-01 | yes, follows from A6 and C3; `ElementSink`, `SinkState`, `SinkSlot` are renamed by A1 |
| Element.Type, Element.Sink | public | internal | protocol id and plumbing on the public pin type | API-01 | yes, follows from C3 (`Element` becomes internal) |
| Initialization | public, no aspect | public, `Hidden` | StandardLibs convention, not for patches | API-01, API-15 | yes, with TooltipPreview (Sebastian, 2026-11-19) |
| BlendMode, LabelPosition, ToggleLook | namespace `VL.Qt.Nodes` (category `VL.Qt.Nodes`) | namespace `VL.Qt.Elements` (category `Qt.Elements`), own files | together with the other pin enums | API-01 | yes (Sebastian, 2026-11-19) |
| ImageFillMode, VideoFillMode | two enums | `FillMode` | same concept | API-10 | yes (see Image / Fill Mode) |
| IElement / Element on inputs | mixed | see Q3 | two type names for one concept | Q3 | one type on every input and output (C3); its name stays `IElement`, as Skia's `ILayer` (Sebastian, 2026-11-19) |

### Categories

| Category | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|
| `VL.Qt.*` (type tree) | exists | removed (merged into `Qt.*`) | second top-level tree | API-01 | yes (Sebastian, 2026-11-19) |
| `Qt.Effects.MultiEffect` | 5 nodes | removed, nodes in `Qt.Effects` | implementation detail | API-02 | yes (C11) |
| `Qt.Elements.Uniform` | 11 nodes | removed, nodes in `Qt.Elements.Shaders` | one place for shader nodes | API-02 | uniform nodes in `Qt.Elements.Shaders.Uniform`, Sampler in `Qt.Elements.Shaders` (C15) |
| `Qt.Elements.Shaders` | generated nodes only | + ShaderEffect, Uniform (…), Sampler | as above | API-02 | + `Shader` (C14), Sampler; uniforms in the subcategory `Uniform` |
| `Qt.Elements.Qml` | generated nodes only | + QmlElement | one place for QML nodes | API-02 | yes (Sebastian, 2026-11-19) |
| `Qt.Images` | — | new: image and file nodes | the root is too broad | API-02 | yes (Sebastian, 2026-11-19) |
| `Qt` | 14 nodes | Renderer, WindowSettings, LoadFont, TooltipPreview (Hidden) | as above | API-02 | yes (Sebastian, 2026-11-19) |
| `Stride.Textures.Source` | — (QtTexture in `Qt`) | QtTexture, ToTexturePixels | as VL.Stride's SkiaTexture | API-02 | QtTexture only; ToTexturePixels goes to `Stride.Input` (Sebastian, 2026-11-19) |
