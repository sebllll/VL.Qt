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
- **Decided:** A6, C3 and P5; catalog: all imported types / category, Initialization; S1 and TARGET.md 2.1/2.2 → item 1 without the Hidden-smell alternative: the sixteen element classes are internal (A6), and `Element` itself becomes the internal base class with `IElement` the only public element type on every input and output (C3); `IStylable.Build` returns `IElement?` and a returned stylable is resolved in turn (P5); `VL.Qt.HDE.vl` retypes its viewer from `Element`. The feedback types are internal under their A1 names (`ElementFeedback`, `FeedbackState`, `FeedbackSlot`). Item 2: `src/VL.Qt/Properties/AssemblyInfo.cs` with `[assembly: ImportAsIs(Namespace = "VL.Qt", Category = "Qt")]`; namespaces follow S1 (e.g. `QtImage` moves to `VL.Qt.Images`). Item 3: the three enums go to `src/VL.Qt/Elements/Enums/`, one file each. Item 4: `Initialization` Hidden (catalog). Item 5 stays the check in vvvv; if it fails, rule 3 of TARGET.md 2.1 is raised again. Item 6: the AGENTS.md line also loses the `Hidden` rule for `NodeContext` (D2).
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
- **Decided:** C11, C14, C15; catalog: Categories, ToTexturePixels / category → as proposed, except: `ShaderEffect` is renamed `Shader` (C14); the uniform nodes go to the subcategory `Qt.Elements.Shaders.Uniform`, `Sampler` to `Qt.Elements.Shaders` (C15); `ToTexturePixels` goes to `Stride.Input` next to `MouseRay`, only `QtTexture` to `Stride.Textures.Source`.
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
- **Decided:** catalog: QmlElement / Size; Group, Row, Column, their Spectral nodes, ShaderEffect / Size → one rule for every node whose `Auto Size` is on by default: `Size` optional directly after `Auto Size`, on the five widgets, QmlElement, the containers and their Spectral nodes, and also `Shader` (was ShaderEffect), against "ShaderEffect is fine".
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
- **Decided:** catalog: Row, Column … / Style; Group (Spectral) … / Children; Labeled / pin order, Clip, Debug, Element; QmlElement / pin order; C2 → as proposed, except: Labeled's first pin is `Input` (was `Element`), order `Input, Text, Label Position, Label Width, Label Height, Spacing, Transform, Anchor, …`; QmlElement `Auto Size, Size, Transform, Anchor, File, Properties, …` (not `Size, Auto Size`); every element node gets `Enabled` (optional, default true) as the very last input pin (C2), and the order rule written down says so.
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
- **Decided:** C4 → (a) `Fill Color` on the four shapes, Polygon `Stroke Color` default `0, 0, 0, 1`; Text keeps `Color`; the alternative `Color` is not taken.
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
- **Decided:** C10; catalog: RadialBlur / Angle; P22 → cycles on both pins; RadialBlur default `0.05` cycles (not `0.02`), so the effect shows at once. One issue with NODX-01 (P22).
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
- **Decided:** C8; catalog: TextInput, Slider; P22 → as proposed; `Input (String)` gets the tags `text, textfield`. One issue with NODW-08 (P22).
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
- **Decided:** catalog: Pinch / Minimum Point Count, Maximum Point Count; P1 → as proposed, and in the same issue Pinch `Minimum Point Count`/`Maximum Point Count` → `Min Point Count`/`Max Point Count`. The Button channel pin becomes `On Click` (NODW-02 is overturned by this row, P1).
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
- **Decided:** C15; catalog: Float, Int … → not taken: the uniform nodes keep their names (`Float` …) and only move to the category `Qt.Elements.Shaders.Uniform`; `Sampler` keeps its name and goes to `Qt.Elements.Shaders`. No overload or rename experiment in vvvv is needed.
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
- **Decided:** catalog: Image / Fill Mode type, Video / Fill Mode type, Text, FontWeight / Font Weight type, QtTexture / Multisample Count type; TARGET.md 2.2; G5 → one `FillMode` (`Stretch, Fit, Crop, Tile`) in `src/VL.Qt/Elements/Enums/FillMode.cs`; Video draws `Tile` as `Stretch` with a warning on the node and says so in the pin help. Font Weight the other way round: `int` everywhere, also on the Text node (default 400), the enum `FontWeight` goes; the pin help lists the named steps (100 Thin … 400 Normal … 700 Bold … 900 Black). QtTexture takes `Stride.Graphics.MultisampleCount`, the Renderer keeps VL.Qt's enum. The `Qt` prefix rule goes into `docs/design/` (G5), not `docs/decisions/`.
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
- **Decided:** C12, C7; P22 → as proposed (normalise by A7); one issue with NODX-13 (P22). The handling of too large radii on AddRoundRect follows Skia (C7, SIP-02).
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
- **Decided:** catalog: VideoSourceToQtImage, Snapshot / Element, FileWriter (Element) / Element, Labeled / Element, Touch Points → `VideoSourceToQtImage` keeps its name (as VL.Video's `VideoSourceToSKImage`) and only moves to `Qt.Images`; `TouchPoints` and `Element` → `Input` on Snapshot and FileWriter (Element) as proposed, and on Labeled too. The two FileWriters also get the output `Success` next to `Is Done` (P10).
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
- **Decided:** catalog: Blur / Max, Blur / Multiplier, DropShadow / Blur; E1; P12 → `Max Radius` and `Radius Multiplier` as proposed; DropShadow `Blur` → `Blur Amount` (parallel to `Amount` on Blur), not `Softness`. Units by E1: shader parameters are in DIP, so Glow and InnerShadow `Radius` do not stay in units of the space, and whether `Max Radius` is in pixels or DIP is a row of `review/28_shader-reference.md` (P12).
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
- **Decided:** C2, P11; "No extra effort for breaking changes" and G2 in DECISIONS.md; G5 → no `Visible` pin: `Enabled` (optional, default true, last input pin) as C2; with `Enabled` off the element takes no space in Row/Column (P11). The page moves to `docs/design/` (G5). The Obsolete rule reads "from 1.0.0 on" in versioning.md; AGENTS.md keeps no version rule of its own (G2).
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

### Added by the pass of P1/P2

Rows collected from the other 22 reports by the pass of P1 and P2 ([PLAN-CHECK.md](../PLAN-CHECK.md)), merged and de-duplicated, 2026-11-19. Three kinds: **open** rows need a decision in the next round; **principle** rows follow from a decided principle and are only read against; **fix** rows are fixes a patch can see, need no decision and are the list for the release notes. The rows of the shader comparison are in [28_shader-reference.md](28_shader-reference.md) and are decided in the same round. "Finding" names the report finding(s) the row comes from.

#### Open rows, decided in the round (17)

| Category | Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|---|
| Qt (root) → Qt.Images, Stride | FileWriter, FileWriter (Element) / 16-bit and float images written as PNG or TIFF | reduced to 8 bit without any warning | either a warning in the log or a real 16-bit PNG written through `Format_RGBA64` | set_pixels warns for the same reduction; the file content differs between the two ways | NRES-12 | 16-bit where the format can hold it, a warning otherwise (Sebastian, 2026-11-19, D6 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Effects | generated shader effect nodes / uniforms named `base`, `glow`, `shadow` (reserved names) | QtNative reports an error line "is reserved (set by VL.Qt)"; docs/effects-and-shaders.md:132 says "no pin and a warning" | one list of reserved names; warning or error to be chosen and the docs aligned | docs and behaviour disagree | NFX-14 | error at the node, one list (also E24) (Sebastian, 2026-11-19, D8 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Elements.Qml | generated QML nodes / properties and signal arguments of QML type `point` and `size` | no pin (the parser reports an info message; QmlTypes.TryMap knows no `point`/`size`); QtNative reports "type 'QPointF' cannot be sent to the patch", a signal argument arrives as a string | `point` and `size` map to `Vector2` (x, y / width, height) for pins, outputs and signal arguments | the wire has `VLQT_VT_VEC2` already; breaking: a signal argument of that type changes from string to Vector2 | NFX-21 | as proposed (Sebastian, 2026-11-19, D7 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Elements.Qml | generated QML nodes / order of the common input pins | after the property pins and `Properties`: Size, Auto Size, Transform, Anchor, Opacity, Clip, Debug, Interaction, Effect | same order as QmlElement after the catalog (Auto Size, Size, Transform, Anchor, … Enabled), or keep the old order | the docs promise that the common pins are exactly those of QmlElement; the catalog changed QmlElement's order | QML-04 | as proposed, one table for both (Sebastian, 2026-11-19, D9 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Images | QtImage / State, Error after its node released it | State stays Ready with Width and Height; an Image element drawing it shows nothing | State Failed, Error 'Released by its node (it loaded another image or was deleted)' | the patch gets no hint why the image vanished | SIP-14 | as proposed; the message goes to the log, the state carries no text (no `Error` pins) (Sebastian, 2026-11-19, D13 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Images | VideoSourceToQtImage / output pin | `Output` | `Image` | ToQtImage, LoadImage and LoadImage (Async) call their output `Image`; a patcher swapping nodes loses the link | NODW-05 | as proposed (Sebastian, 2026-11-19, D15 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Interaction | Touch Points / TouchPoint (type) | `sealed record TouchPoint(int Id, Vector2 Position, float Pressure, Vector2 Size)`, a class | `readonly record struct` with the same four values: same pins on Split, no null | one allocation per finger per event during a moving touch | ELM-13 | as proposed (Sebastian, 2026-11-19, D5 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Interaction | Drag / Delta | `Delta` (offset since the press, in the parent's space) | `Translation` (same word as Pinch); `Delta` stays for per-frame motion (Wheel) | `Delta` has two meanings in the family | NODX-02 | as proposed (Sebastian, 2026-11-19, D16 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Interaction | Hover / Devices | missing | optional `Devices` (needs HoverInteraction to carry devices; skip if the native handler does not read it) | every other interaction node filters devices | NODX-02 | as proposed (Sebastian, 2026-11-19, D17 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Paths | AddArc / start of a full sweep (\|Sweep Angle\| >= 1) | always the oval starting at the right middle, the Start Angle is ignored | the oval starts at the Start Angle (Skia: addArc); other angles use arcTo with forceMoveTo | the start point matters for dashes, trim and caps | SIP-02 | as proposed (A7) (Sebastian, 2026-11-19, D10 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Paths | AddRoundRect / start point of the contour | after the top-left corner | Skia's start (left edge, index 6 of the rrect iterator) if the SkiaSharp comparison confirms it, otherwise listed in docs/shapes.md | suspicion of the report, not confirmed; patch-visible for dashes and trim | SIP-02 | as proposed (Sebastian, 2026-11-19, D11 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Paths | Spline (CatmullRom) / Tension (and Alpha NaN) | not clamped; above 1 the tangents flip, NaN gives NaN points; Alpha NaN silently 0.5 | Tension clamped to 0..1 (documented), NaN rule documented | the pin help promises '1 gives straight lines' | SIP-15 | as proposed (Sebastian, 2026-11-19, D12 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Paths | AddCircle / pin order | `Center, Dir, Radius` (`Dir` becomes `Direction` by C12) | `Center, Radius, Direction` if Skia's AddCircle has that order (not verified by the report) | direction after the geometry, as the SkiaSharp call takes X, Y, Radius, Dir | NODX-13 | check `VL.Skia.vl`, then follow Skia (Sebastian, 2026-11-19, D18 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Qt.Widgets | Button / `On Click` pin position | `Is Clicked` sits after `Auto Repeat` (before `Opacity`), the other widgets have `Channel` directly after `Anchor` | `On Click` directly after `Anchor` | a patcher who knows one widget finds the channel pin in the same place on the others | NODW-02 | as proposed (Sebastian, 2026-11-19, D14 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Stride | QtTexture / Perf | optional output `Perf` (PerfStats), always `PerfStats.Empty` | removed until QtNative has a perf ring for offscreen scenes (alternative: keep it and implement the ring) | a pin that never carries data | COMP-06 | remove; the pin can come back as an addition (Sebastian, 2026-11-19, D1 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Stride | QtRenderer / draw into two targets in one frame | one target is remembered; drawn into two targets, every draw rebuilds the view and the input subscription and the scene renders at the size of the last target, stretched in the other | the second, different target of the same frame is not drawn; a hint says 'QtRenderer draws into one target per frame; use one QtRenderer per window' | state is kept for one target; per-draw cost and wrong size | COMP-05 | as proposed (Sebastian, 2026-11-19, D2 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Types and enums | PerfStats and its four groups / names of the values (the pins of Split) | as listed above; PerfSample.frame in QtNative | RUN-10 names BudgetMs, FrameMsP50…, PumpMsP50…, Pumps…, RecentMultiSwaps, VvvvMs…, Ops, VsyncMs, Gen0PerSecond…, AllocatedBytesPerFrame; group types HostTimes and GcActivity (RUN-10) against HostPerfStats and GcPerfStats (TARGET 2.2); NDIAG-10: frame becomes frameInterval | each name is a pin of a Split in patches | RUN-10, NDIAG-10, NDIAG-11, NDIAG-01: `WorkMs` (Qt work = pump - vsync - present) and `VsyncMs` in the main record; `BudgetMs` becomes the budget of `PerfPercentiles`; renames `refreshBudgetMs` (screen) and `FrameClockMs` (C#); `PerfSample.frame` becomes `frameInterval`; exact pin names and order are proposals | RUN-10's value names, TARGET's type names; as proposed, budget name after C4 (D4) (Sebastian, 2026-11-19, D3 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |

#### Consequences of decided principles (43)

| Category | Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|---|
| Qt (root) → Qt.Images, Stride | Renderer / Error | output `Error` (string, visible, not Optional): NodeErrors.Current ?? QtNative error, so a warning text can hide a native error | pin removed; all messages are shown at the node (errors first, one line each) and logged | no Error output pins; the node-message mechanism is one list with one entry per phase | RUN-01, NDIAG-07, NDIAG-11 | follows from 'No Error output pins' (DECISIONS) |
| Qt (root) → Qt.Images, Stride | Renderer / exception inside the reconcile (for example a value the wire cannot carry) | propagates: ids, native objects and registry entries leak every frame, partial batches are applied | treated like a failed apply: reset, resync next frame; exception logged and the Renderer node marked | mirror and QtNative stay consistent | REC-01 | follows from B1: reset and resync, log and mark the node instead of an Error output |
| Qt (root) → Qt.Images, Stride | Renderer / vlqt.dll and VL.Qt.dll of different versions | no check: every frame fails and resyncs in a loop, or a renumbered property is set silently | no windows, the log names both versions, the Renderer node is marked | a version check at start | PROTO-01 | follows from B3: refuse |
| Qt (root) → Qt.Images, Stride | FileWriter, FileWriter (Element) / Success | missing; outputs are `Is Done` and `Error` (FileWriterNode.cs, FileWriterElementNode.cs) | new output `Success` (bool) next to `Is Done`: says how the last write ended and holds until the next write; `Is Done` stays true in the frame the write ended | without the `Error` pin a patch could not tell a finished write from a failed one | NRES-12 (P10), NODW-04 | follows from P10: second output `Success` on both FileWriters (already decided) |
| Qt (root) → Qt.Images, Stride | Renderer / Enable Keyboard Shortcuts | missing; F2, F5, F6 and F7 are swallowed in every window and cannot be turned off | new pin `Enable Keyboard Shortcuts` (bool, default true), named as in VL.Stride's renderer; false removes the event filter, disables every diagnostic key and passes the keys to the patch; visibility Optional is proposed (C18 does not say) | a patch with its own F5 shortcut cannot work today | NDIAG-07 | follows from C18: pin `Enable Keyboard Shortcuts`, default true; no `Overlay` pin |
| Qt, Qt.Images | LoadImage, LoadImage (Async), LoadFont, ToQtImage, VideoSourceToQtImage, Snapshot, FileWriter, FileWriter (Element) / Error | output `Error` (string) on each | removed; every error goes to the log, a lasting load failure also marks the node | the pins duplicate the central log and are rarely linked | NODW-04, NRES-12 | follows from 'No Error output pins' (DECISIONS) and B7: lasting failures (LoadImage, LoadImage (Async), LoadFont, ToQtImage) mark the node, one-shot failures (Snapshot, FileWriter) only log |
| Qt.Effects | generated shader effect nodes (chain node of a filter .frag) / Error | output `Error` (string) next to `Output` (ShaderLinkDescription.s_outputs) | removed; the error shows at the node and in the log | one rule for every node; the principle's list does not name generated nodes | SHD-09 | confirmed by A3 of [DECISION-ROUND.md](../DECISION-ROUND.md): "No `Error` output pins" holds for the generated nodes too |
| Qt.Elements | Video (VL.Audio) / aspect | no `[Smell]` attribute (stable) | `[Smell(SymbolSmell.Experimental)]` until it is measured on x64 | QtTexture, QtRenderer and ToTexturePixels are experimental; the audio node was measured on win-arm64 only | COMP-13 / 22 Q2 | follows from C19: experimental until measured on x64 |
| Qt.Elements | Text, Button, TextField, Video, QmlElement (string values); Shape, Path (point lists) / size limit; Input (String) | a string over about 65,527 UTF-8 bytes throws from the reconciler (u16 command length); Shape and Path draw only the first 8,190 points, with a warning (ShapeNodeCore.MaxPoints) | no limit from the wire: long texts and large shapes work (u32 length, subject to the P8 measurement); if P8 falls back to u16 the clamp with a node warning stays | a patch sees the limit and the warning go; the 64 KB limit, point caps, QML chunking and native string caps are removed | REC-01, PROTO-02, NCMD-08 | follows from A4: u32 length in command and event headers; the limit and the caps go (P4 step 4) |
| Qt.Elements | Hover / Is Hovered, Click / Is Pressed, widget hover and press states, of an element shown at several places | last record wins (IsHovered = payload for every handle), so the value depends on Qt's delivery order | true while any occurrence is hovered or pressed (OR over all occurrences) | events.md promises OR; the Button shown twice | ELM-02 | follows from B6: OR over all occurrences |
| Qt.Elements | all element nodes / identity of one node in the scene registry (owner of Measured, Scene Bounds, Media; Video player) | keyed by element instance: a node is a new key after every change, the owner is the first renderer to update after a change, a Video shown in two renderers can restart in the other | keyed by the node's feedback slot (instance only for elements without a slot): stable owner; two instances of one node in one tree are two occurrences of one element, a Video node has one player | removes the churn and the slot fallback of Snapshot | REC-10, REC-06 | follows from A2: key by slot |
| Qt.Elements | Row, Column / alignment of children and invisible children | cross-axis alignment is JavaScript in the element's QML (`vlqtAlign()`, `elements.cpp:432-458`); not stated whether it skips invisible children | alignment is computed in C++ and skips invisible children as Qt's positioners do: `Enabled` off takes no space and the siblings move up (pin help names `Opacity` 0 for keeping the gap) | no JavaScript in built-in elements; Enabled off acts like taking the element out of the spread | NELM-13 | follows from E12 and P11 |
| Qt.Elements.Qml | QmlElement / animations of the user's QML when the clock jumps back or pauses | `docs/architecture.md:31-32` promises 'Scrubbing and pause'; Qt animations are delta-based and do not rewind (suspicion, not yet run) | documented: QML animations follow the clock forward and stop with it, a jump back does not rewind them (wave 1 confirms with a scrub check first) | a patch with a scrubbing clock sees animations hold until the clock passes the old time again | NAPP-10 | follows from E11: document, no rewind |
| Qt.Elements.Qml | QmlElement / Error | output `Error` (string), last output pin (QmlElementNode.cs) | removed; errors go to the log and mark the node | no Error output pins; QmlElement is on the decided list | QML-07 | follows from 'No Error output pins' (DECISIONS.md): QmlElement is named there |
| Qt.Elements.Qml | generated QML nodes / Error | output `Error` (string), last output pin (QmlNodeLayout.s_trailingOutputs) | removed, like on QmlElement | the docs promise that the common pins are exactly those of QmlElement; the principle's list names QmlElement but not the generated nodes | QML-04 | confirmed by A3 of [DECISION-ROUND.md](../DECISION-ROUND.md): "No `Error` output pins" holds for the generated nodes too |
| Qt.Elements.Qml | generated QML nodes / Enabled | missing | `Enabled` (bool, optional, default true), last input pin | C2 applies to every element; P6 says Enabled also comes to the generated nodes | QML-04 | follows from C2: Enabled optional, default true, last input pin (P6: also on generated nodes) |
| Qt.Elements.Qml | generated QML nodes / Size | visible (Vector2, default 1, 1), before `Auto Size` | optional, after `Auto Size` | Size is ignored while the hidden Auto Size (on by default) is on | QML-04 | follows from the rule 'Size optional on every node whose Auto Size is on by default' (catalog review) |
| Qt.Elements.Qml | generated QML nodes / pins while the file cannot be read or parsed | the node is rebuilt with only the common pins, links on property pins are lost | the last good pins and values stay, the error shows at the node | keep the last good layout | QML-05 | follows from E2: keep last good pins while the file does not parse (QML and shaders alike) |
| Qt.Elements.Shaders | Shader (was ShaderEffect) / Error | output `Error` (string), last output pin | removed; load, uniform and bake errors go to the log and mark the node | no Error output pins; ShaderEffect is on the decided list | SHD-02 | follows from 'No Error output pins' (DECISIONS.md): ShaderEffect is named there |
| Qt.Elements.Shaders | generated shader nodes (element node of a .frag file) / Error | output `Error` (string), last output pin (ShaderNodeDescription.s_outputs) | removed, like on Shader | the fixed pins are copied from ShaderEffect; the principle's list does not name generated nodes | SHD-08 | confirmed by A3 of [DECISION-ROUND.md](../DECISION-ROUND.md): "No `Error` output pins" holds for the generated nodes too |
| Qt.Elements.Shaders | generated shader nodes (element node) / Enabled | missing | `Enabled` (bool, optional, default true), last input pin | as above | SHD-08 | follows from C2 (P6: also on generated nodes) |
| Qt.Elements.Shaders | generated shader nodes (element node) / Size | visible (Vector2, default 1, 1), before `Auto Size` | optional, after `Auto Size` | same rule; the row for Shader (ShaderEffect) exists, the generated nodes copy its fixed pins | SHD-08 | follows from the rule 'Size optional on every node whose Auto Size is on by default' (catalog review) |
| Qt.Elements.Shaders | generated shader nodes and generated shader effect nodes / pins while the file does not compile | all uniform pins disappear, only Input and the fixed pins stay | the last good pins stay, the compile error shows at the node, the last good shader keeps drawing | keep the last good layout | SHD-05 | follows from E2: keep last good pins while a shader file does not compile |
| Qt.Elements.Shaders | Shader / Fragment Code default text | the template comment says 'a Uniform node (Qt.Elements)' (ShaderTemplates.cs:17) | 'a Float or Color node of Qt.Elements.Shaders.Uniform' | the uniform nodes move category | SHD-16 | follows from C15: uniform nodes in Qt.Elements.Shaders.Uniform |
| Qt.Images | LoadImage, LoadImage (Async), ToQtImage, VideoSourceToQtImage / Upload | no pin; the upload path is forced by the environment variable `VLQT_IMG_UPLOAD=stripes\|worker` (image_resource.cpp:106) | hidden pin `Upload` (enum `Auto`, `Main Thread`, `Worker`), default `Auto`; the variable goes; enum type name not named by the decision | whatever a patch may want to set is a pin; tests set the pin from C# | NRES-05 | follows from E10 and 'Pins instead of environment variables': hidden pin `Upload`, `VLQT_IMG_UPLOAD` goes |
| Qt.Interaction | Pinch / Is Active | `Is Active` | `Is Pinching` | specific state names: Is Hovered, Is Pressed, Is Dragging, Is Pinching; Point keeps Is Active | NODX-02 | follows from C13 (a): specific names |
| Qt.Paths | AddRoundRect / drawing of radii larger than half the rectangle | each radius is limited on its own (min(\|rx\|, w/2), min(\|ry\|, h/2)); the corners can be elliptical (stadium) | both radii scaled by one factor min(w/2rx, h/2ry) as Skia does; a pill gets semicircular ends | the docs promise that Skia patches carry over; a patch whose radii exceed half the size draws differently | SIP-02 | follows from C7 (a): follow Skia |
| Qt.Style | Text / Font Size (pin help) and the sentence 'Sizes in a style are px' | pin help 'Font size in px.' (StyleNodes.cs:235), docs/styles.md:28 'Sizes in a style are px' | the help and the page say DIP; the values do not change (Qt's logical pixel) | px means a device pixel only in the glossary | DOC-10 (24 section 6, API / styles) | follows from E1 and glossary 1.2 (DIP, px): style sizes are DIP |
| Qt.Style | Colors [Qt.Style], Frame [Qt.Style.Frame], Text [Qt.Style.Text], Indicator [Qt.Style.Indicator], Track [Qt.Style.Track] (bundle setters) | Colors, Frame, Text, Indicator, Track | SetColors, SetFrame, SetText, SetIndicator, SetTrack | vvvv convention: a record property reads as `Radius` and writes as `SetRadius`. Rename order matters for the migration script: `Frame` (setter) must become `SetFrame` before `GetFrame` becomes `Frame` (P19) | SIP-01, SIP-10 | follows from C5 (b): bundles `SetFrame` / `Frame`. P2 lists the names of the six process nodes as a question (`SetFrame` or `Frame`?); C5 names the setter `SetFrame`, so it is entered that way |
| Qt.Style | GetColors, GetFrame, GetText, GetIndicator, GetTrack (bundle getters) | GetColors, GetFrame, GetText, GetIndicator, GetTrack | Colors, Frame, Text, Indicator, Track | the getter takes the plain name, as in C5 (b) | SIP-01, SIP-10 | follows from C5 (b): getters `Radius`, bundles `Frame` |
| Qt.Style | the 20 single Advanced setters: Frame (Color, BorderColor, Radius, BorderWidth, PaddingX, PaddingY, MinWidth, MinHeight), Text (Color, FontFamily, FontSize, FontWeight), Indicator (Color, Size, Radius, BorderWidth), Track (Color, FillColor, Thickness, Radius) | <Field> (e.g. `Radius [Qt.Style.Frame]`) | Set<Field> (e.g. `SetRadius`) | as above; the single setters stay (C5 keeps them), only their names change | SIP-01, SIP-10 | follows from C5 (b): setters `SetRadius`, the single setters and getters stay |
| Qt.Style | the 20 single Advanced getters (Get<Field> of the list above) | Get<Field> (e.g. `GetRadius`) | <Field> (e.g. `Radius`) | as above | SIP-01, SIP-10 | follows from C5 (b): getters `Radius` |
| Qt.Style | all setters (the five bundles and the 20 singles) / State | `State` (StyleStates, a set) | `States` | the pin holds a set of states; the getters keep `State` (one StyleState) | SIP-01 | follows from C5 (b): pin `State` becomes `States` |
| Qt.Style | Colors, Frame, Text, Indicator, Track, Merge (the six bundles) / node kind | static operation nodes (new Style object every frame) | process nodes with a cached output, same categories and pins; the single setters and getters stay static | no allocation per frame, the reconciler skips unchanged widgets; patches see a process node instead of an operation node (help patches migrate through the mapping file) | SIP-01 | follows from C6: process nodes for the six bundles only |
| Qt.Widgets | Slider, Input (Int) / Value (behaviour with a channel outside Min..Max) | the widget keeps its clamped copy; after Min/Max widen it stays at the old bound while the channel holds the other value | the widget reads the channel every frame and clamps only what it shows; widening the range shows the channel value again | widget, channel and other widgets on the channel agree, as in VL.ImGui | NODW-01 | follows from C9 (a): the channel is the truth |
| Stride | QtTexture / Error | output pin `Error` (string; QtTextureNode.cs `out string error`) | removed; the message goes to the vvvv log and a lasting error marks the node | no Error output pins | COMP-06 (DECISIONS: No `Error` output pins) | follows from 'No `Error` output pins' (DECISIONS, B7): the pin goes, error to log and node |
| Stride | QtRenderer / Error | output pin `Error` (string; also carries the blend hint when the target has no UNorm view) | removed; the hint and errors go to the log once and show as a warning at the node | no Error output pins | COMP-05 (DECISIONS: No `Error` output pins) | follows from 'No `Error` output pins' (DECISIONS, B7): the pin goes, hint to log and node |
| Stride | QtTexture, QtRenderer / Render Threads | missing (pool size only through the environment variable `VLQT_OFFSCREEN_THREAD`) | new hidden pin `Render Threads` (default 1; 0 = main-thread mode; the maximum over all nodes wins; at runtime the pool only grows, a smaller value and 0 apply to scenes created afterwards); `VLQT_OFFSCREEN_THREAD` goes | pins instead of environment variables; decided in E6/E7 but no catalog row | DOC-07, DOCD-05, DOCD-08, NDIAG-06, NRES-01, NDEV-01, NDEV-05 | follows from E7 (with E6 and P7): pin on QtTexture and QtRenderer, hidden |
| Stride.Textures.Source (QtTexture) and Qt.Images | QtTexture, QtRenderer, LoadImage, ToQtImage / behaviour when QtNative cannot create its GPU device | a failed device creation or rebuild is never retried; the process stays without a shared device for its life: image uploads return null, new offscreen scenes fail at create, existing scenes fall back to private devices that nobody watches for loss (`device.cpp:229-233, 265-271, 521-552`) | QtNative retries once per second and logs once; meanwhile windows keep their own devices, images do not upload and offscreen scenes report `DEVICE_LOST`, which the C# side already handles by retrying | a transient device failure no longer lasts until restart | NDEV-02 | follows from B8: retry once per second, log once |
| Types and enums | PerfStats / Split (Renderer Perf output) | record with 11 positional values (Fps, FrameMs, FrameMsMax, PumpMs, PumpMsMax, SyncMs, RenderMs, PresentMs, GpuMs, GpuMsMax, SwapsPerPump) and about 30 init-only values that a patch reads only through five Split… operations (SplitPercentiles, SplitCounters, SplitVsync, SplitHost, SplitGc) | main values positional with documentation plus four value groups as record structs (PerfPercentiles, PerfCounters, HostPerfStats, GcPerfStats) with nested Split; the Split… operations go | VL shows only positional values as Split pins; patches that read percentiles, counters, host or GC values are rewired | RUN-10, NDIAG-11, NDIAG-11 (shape) | follows from C17: grouped record structs with nested Split; output keeps updating every frame |
| Types and enums | PerfStats / OverBudget, RecentOverBudget | pumps longer than the budget, counted over the whole pump including the vsync wait | counts only Qt work (events, sync, render) above the budget; C# and QtNative use one definition | a static scene with vsync on counted as over budget | RUN-10 (NDIAG-01), NDIAG-01 | follows from E13: (a), only Qt work counts, as OverBudget |
| Types and enums | Interaction / Sink (public member of the public base type); ElementSink members (TakeClicks, Resize), Video.MinRate, ClampRate, ClampVolume, Shape.ExtentOf, Text.DefaultFontSize | public; `public SinkSlot Sink` on Interaction; whether vvvv imports the statics as nodes is unknown (to be checked in the node browser) | internal (`Interaction.Feedback` internal); no node for them | plumbing and constants of the library, not API; a public member cannot have an internal type | ELM-04 | follows from the rows 'ElementSink, SinkState, SinkSlot …' and 'Element.Type, Element.Sink' (A6, C3, A1) |
| Types and enums | Geometry / ToQtAlignment, AnchorOffset (nodes) | two nodes in the node browser (static methods of the public class Geometry, imported by ImportAsIs); no help patch uses them | removed with Geometry internal | protocol helpers for the ANCHOR command | RUN-13 | follows from the row 'ElementSink, SinkState, SinkSlot, ElementType, Geometry': Geometry becomes internal |

#### Fixes a patch sees (release notes) (31)

| Category | Node / pin | Current | Proposed | Reason | Finding | Decision |
|---|---|---|---|---|---|---|
| Qt (root) → Qt.Images, Stride | Renderer / messages of several phases (samples lowered, icon not loadable, exception in AfterEvents) | the last message replaces the others and a recovering phase clears a warning that still applies; 23 node classes share this NodeErrors | one entry per phase, all shown, clearing one phase leaves the others | messages were lost | RUN-01 | release notes |
| Qt (root) → Qt.Images, Stride | Renderer / Bounds (Save Bounds) | a patch that sets Bounds through WindowSettings is marked changed 0.5 s after opening (the BoundsSaver is created with the pin value) | the BoundsSaver starts with the resolved create bounds: no change on open | document dirty on open | RUN-07 | release notes |
| Qt (root) → Qt.Images, Stride | Renderer, QtTexture / animation clock with two vvvv apps in one process | the native driver time can go back by up to a frame when one app uses QtTexture (no single owner of the time) | QtApp owns the driver time and never moves it back | Qt animations jump back | RUN-02 | release notes |
| Qt (root) → Qt.Images, Stride | exported apps that use only QtTexture or FileWriter | no SIGTERM handling and no Linux _exit (ExportedApp.Attach is called only from Renderer) | every exported app gets them (attached from Initialization.Configure) | exit code and WSL arm64 hang protection | RUN-08 | release notes |
| Qt (root) → Qt.Images, Stride | Renderer used from a thread other than the Qt thread; failed Qt init | Debug.Assert only, a no-op in Release: QtNative is called from the wrong thread; a failed init is retried on every Get | InvalidOperationException shown at the node; an init failure is remembered and rethrown | undefined behaviour in Qt | RUN-11 | release notes |
| Qt.Effects | Blend, Mask, Displace, Blur, DropShadow … (SET_EFFECT_CHAIN) / a chain that is rejected by `EffectChain::apply` | masks and textures of the chain are parked hidden under the root before `elements.setEffectChain` can fail, so an element disappears because an unrelated chain was rejected (`commands.cpp:1194-1239`, `elements.cpp:1653`) | park masks and textures only after validation passed; undo the parking if the apply still fails; the comment names exactly what is atomic | a rejected chain must change nothing in the patch's picture | NCMD-03 | release notes |
| Qt.Effects | generated shader effect nodes (filter .frag with @padding) / effect area | @padding is applied in renderer units: `@padding 6` is 600 DIP per side in the default space | @padding is DIP, as documented | unit fix; E1 | SHD-01 | release notes |
| Qt.Effects | generated shader effect nodes / pin names that collide with the element node's fixed pins | a uniform `size`, `opacity` or `transform` shows as 'Size 2' on a chain node that has no Size pin | plain name on the chain node; 'Size 2' only on the element node; uniform `inputSampler` made unique | uniqueness per node kind | SHD-03 | release notes |
| Qt.Effects | generated shader effect nodes / a `@chainonly` file without `source` | no node, only a log warning | the chain node appears and shows the 'not a filter' error | the message shows where the user looks | SHD-14 | release notes |
| Qt.Elements | Video (VL.Audio) / sound latency (Audio, Sync Error) | WASAPI latency read from `AudioClient.MinimumDevicePeriod` (typically 3 ms), fallback 10 ms (per COMP-04, code not re-read) | the latency VL.Audio configured, else `DefaultDevicePeriod` plus `StreamLatency`; fallback 10 ms stays | the target delay and Sync Error inherit the wrong latency; the sound may shift by a few ms, Audio Offset calibrations change by that amount | COMP-04 | release notes |
| Qt.Elements | Video (VL.Audio) / a second item with the same audio tap | the item is ignored and plays Qt's own sound at full volume (suspicion, unconfirmed) | the item is muted and the node shows an error (use one Video (VL.Audio) per item) | sound from two places | COMP-03 | release notes |
| Qt.Elements | element nodes / warnings and build errors shown at the node (effect fold warning, Video and interaction duplicates, build error of a stylable) | a changed fold warning is never shown and a stale one stays; a duplicate warning hides the fold warning; the build error of a second sibling and of a root widget is never shown | every writer's message is kept and shown at the node; the build error of a root widget marks the Renderer | one message store per slot with a source per message | REC-02 | release notes |
| Qt.Elements | stylable widget (IStylable) used as an effect mask source or uniform texture | Build stops running once the host element is skipped | Build runs on every pass, as for stylable children | the host's ContainsStylable also covers effect objects | REC-12 | release notes |
| Qt.Elements | QmlElement / node message for unsupported property values | when an unsupported value changes to another unsupported type, the node keeps showing the old reason (QmlProperties.Matches does not compare the refused entries) | the message follows the new value | keep the names and types of refused entries | ELM-14 | release notes |
| Qt.Elements | Group, Row, Column (and their Spectral nodes) / bounds of a Group that contains an auto-sized Group (hit area, clip, SCENE_BOUNDS, effect padding, debug overlay) | `GroupItem::updatePolish` maps every child as `QRectF(0, 0, w, h)` (`elements.cpp:575-581`), so a nested Group whose content reaches left of or above its origin shifts the outer bounds; a change of only the inner origin does not invalidate the outer Group | the outer Group uses the child Group's own bounds (`itemBounds`, was `itemFrame`) and is invalidated when the inner origin moves | the drawn area of a nested Group was wrong; a patch sees it in hit area, clip, effect padding and the debug overlay | NELM-01 | release notes |
| Qt.Elements | all element nodes / non-finite float values (NaN, infinity) on any numeric pin: Opacity, Radius, Border Width, Spacing, Padding, Line Height, Slider bounds, Font Size, Letter Spacing, Transform | `setFontPixelSize` checks only `!(v.f[0] > 0)`, so `+inf` and values above INT_MAX reach `qRound` (undefined behaviour); the other F32 setters write NaN/inf to Qt unchecked; only the Shape setters and Video reject them (`elements.cpp:255-259`, `132`, `186-189`, `277-280`, `711-728`, `965-971`) | one check in `Elements::set`: a non-finite component is rejected with `ApplyBadValue`; the reconciler resyncs, logs it and marks the Renderer (B1); font size is clamped before rounding | undefined result instead of a clear error; three validation styles for one rule | NELM-03 | release notes |
| Qt.Elements | Labeled / warning or error of the inactive layout | a message shown by the Row stays at the node after Label Position switches to Top, until it switches back | the inactive layout's messages are cleared when the position changes | stale message | NODE-05 | release notes |
| Qt.Elements.Qml | generated QML nodes / pins of a file that uses a regex literal | the lexer reads the regex as code and fails, so the node keeps only the common pins and every link on a property pin is lost | the file parses and all property pins appear | lexer skips regex literals | QML-01 | release notes |
| Qt.Elements.Qml | generated QML nodes and QmlElement / a root with `required property` | the file fails to load with an error | loads; the property is set at creation | createWithInitialProperties | QML-08 | release notes |
| Qt.Elements.Qml | generated QML nodes / a file whose root is its own type, or that redeclares an inherited property with a type that gets no pin | self root: pins plus a warning about a cycle; redeclared as `var`: the inherited float pin stays | self root: an error 'cannot have its own type as root type'; redeclared: the inherited pin goes | match what QML accepts | QML-09 | release notes |
| Qt.Elements.Shaders | Sampler / mip filter when two samplers of one shader read the same element | the mip request of the last sampler overwrites the first one: mipmaps flip off after the second refresh and the mip filter is silently lost | mip flags of all samplers on one element are ORed, one request per element | fixes a wrong render | NFX-02 | release notes |
| Qt.Elements.Shaders | generated shader nodes / uniform block not at binding 0 | accepted; surfaces as a QRhi pipeline failure in the log (suspicion, not reproduced) | error at the node: "the uniform block must use binding 0" | the sampler/block binding collision gets a message at the node | NFX-19 | release notes |
| Qt.Elements.Shaders | generated shader nodes / image pins (sampler2D other than source) | a false 'Uniform … is set more than once' message at every node with an image pin | no message | sampler entries are keyed apart | SHD-02 | release notes |
| Qt.Elements.Shaders | generated shader nodes / change of `@category` in a file | no effect until a file is added, deleted or renamed or vvvv restarts | takes effect at once | factory compares the whole head | SHD-04 | release notes |
| Qt.Images | ToQtImage / Image output instance | a new QtImage for every new input instance | one QtImage per node, its content is replaced (as VideoSourceToQtImage) | no allocation per frame for an upstream that yields a new image every frame; an S+H or change detection downstream no longer sees a new instance | NODW-06 | release notes |
| Qt.Images | LoadImage (Async) / output when the create fails and Keep Previous is off | the previous image stays at the output | output switches at once (null), as the pin help promises | matches the doc and the synchronous LoadImage | NODW-07 | release notes |
| Qt.Images | FileWriter (Element) / file content when Write is triggered twice before the first grab settled | probably the older grab is written to the newer path (suspicion, not confirmed) | only the newest request is written | the second file would otherwise hold the wrong element | NODW-03 | release notes |
| Qt.Interaction | Drag / Delta channel on connect (behaviour) | a freshly connected channel is set to (0, 0) before any drag | written only after the first drag update (as Pinch and TouchPoints) | a channel holding a value (global channel, preset) is no longer reset at node creation | NODX-03 | release notes |
| Qt.Style | Style / ToString (tooltip of a link or IOBox) | `Style { }` for every style | compact list of the set entries, e.g. `Frame.Color[Normal,Hovered], Text.FontSize[All]`; `Style (empty)` when nothing is set | a patcher can see what a chain has set | SIP-16 | release notes |
| Stride | QtRenderer / layer that is no longer drawn | once drawn, `_drawn` stays true: a disconnected layer keeps updating and rendering its scene | the scene freezes (`enabled && drawnLastFrame`) while the layer is not drawn | renders for nobody | COMP-05 | release notes |
| Types and enums | PerfStats / Fps, FrameMs, FrameMsMax, SyncMs, RenderMs, PresentMs, VsyncMs (values) | averaged over every pump with a frame, so the first interval after idle pumps counts as a frame time; phase averages divide by pumps with a frame | frame statistics from consecutive frames only, phase averages per pump that rendered; `Fps` = 1000 / `FrameMs`, 0 (not a misleading low number) when the scene is idle | on-demand scenes showed '2 fps' and a `FrameMsMax` of seconds while the percentiles were right | NDIAG-02 | release notes |
