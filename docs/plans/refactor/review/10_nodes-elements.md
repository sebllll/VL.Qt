# Review 10 – Element nodes and their shared core

Claude Opus 5.5, 2026-11-19. Files read in full (all in `src/VL.Qt/Nodes/`): ElementNode.cs, ChildrenInput.cs, ValueChannel.cs, ShapeNodeCore.cs, LabeledNode.cs, RectangleNode.cs, CircleNode.cs, TextNode.cs, ImageNode.cs, VideoNode.cs, GroupNode.cs, RowNode.cs, ColumnNode.cs, GroupSpectralNode.cs, RowSpectralNode.cs, ColumnSpectralNode.cs, LineNode.cs, PolylineNode.cs, PolygonNode.cs, PathNode.cs, ElementTooltipPreview.cs, QtImageTooltipPreview.cs; section 4 of `06_elements.md`. Looked up (excerpts only): `NodeErrors.cs`, `Elements/Element.cs` (property accessors, `Children`), `Paths/QtPath.cs:155-170`, `VL.Qt.Audio/VideoAudioNode.cs` (use of `VideoNode.Props`), test usage of the test hooks; in `C:\vl\VL.StandardLibs`: `VL.ImGui/src/Widgets/Checkbox.cs`, `VL.ImGui.Generator/SourceGenerator.cs:330-350`, constructors taking `NodeContext`, `VL.Core/src/NodeContext.cs:23`. Not read: the widget nodes that use `ElementNode`/`ValueChannel` (Button, Toggle, Slider, InputInt, TextInput), the tests themselves.

## 1 Summary

The pattern is sound and should stay: a public `[ProcessNode]` shell holds an `ElementNode<TElement, TProps>` field, packs its pins into `CommonInputs` plus a `readonly record struct Props`, and gets back the same description instance as long as nothing changed. Change detection, zero allocation per frame and disposal are correct in every node I read; `ChildrenInput` is a careful piece of work. What makes the area expensive is the boilerplate around the pattern, not the pattern: a 95-line element node carries about 60 lines that are the same in every sibling (two constructors, nine common assignments in the `Create` lambda, ten common pins with attributes, ten common `<param>` docs, the `Describe` + `ReadOutputs` pair). The 16 element nodes of this area hold 148 `<param>` lines for the common pins alone (203 in the library), and those texts have drifted: "Clip children" on nodes that have no children, "Opacity of the element and its children" on leaf nodes, "(see Rectangle)" in a tooltip the user cannot follow, `<returns>` on 4 of 16 nodes.

The changes that matter most:
1. **NODE-01** Shrink the node to its specific part: a `CommonInputs`-taking element constructor, `Describe(…, out measuredSize, out sceneBounds)` in one call, Spectral nodes that share `Props`/`Create` instead of a 13-parameter `Describe`. A newcomer then copies a 40-line node.
2. **NODE-02** Fix the pin docs that are wrong or unusable in vvvv (Clip, Opacity, Debug, `<returns>`).
3. **NODE-03** One source for the common pin texts: `<include>` from one XML file, with shorter texts. Recommended over `<inheritdoc>` (not expanded by the compiler) and over a source generator (VL.ImGui's way, too heavy for 24 nodes).
4. **NODE-04** The default-value rule: AGENTS.md and the code contradict each other on `[DefaultValue(typeof(T), "...")]`; the knowledge sits in a code comment that four nodes point to.
5. **K5 answered:** vvvv does not need `[Pin(Visibility = PinVisibility.Hidden)]` on a `NodeContext` constructor parameter. VL.StandardLibs has 66 such constructors without the attribute and 4 with it (Elias' VL.Skia never uses it). Recommendation in Q2.

## 2 What works and must survive

- **Reference-stable descriptions.** `ElementNode.Describe` (`ElementNode.cs:120-132`) returns the previous instance when `CommonInputs` and `Props` are equal; the reconciler skips reference-equal descriptions. Every refactoring of the nodes must keep "same inputs → same instance".
- **`Props` as `readonly record struct`, `Create` as a `static` lambda in a `static readonly` field.** No closure, no allocation per frame; equality comes for free. Custom equality only where needed (`ShapeNodeCore.Props.Equals`, `ShapeNodeCore.cs:36-41`: spreads by content, because vvvv often hands a new spread with equal content each frame).
- **`ChildrenInput` (hot path).** Pin-group spreads are rebuilt by vvvv every frame; `ChildrenInput.Update` (`ChildrenInput.cs:33-67`) compares element by element and returns the same `ImmutableArray` (reference equality → usable in `Props`). No allocation when nothing changed for `Spread<IElement?>`/`Spread<Element>`; `IStylable` wrappers are reused (`ChildrenInput.cs:130-136`).
- **`ValueChannel<T>`** (pull, no subscription, user wins over patch in the same frame, own copy clamped like Qt, Min/Max from channel attributes as VL.ImGui does). The semantics are documented and tested by the widget node tests; keep them.
- **Composition instead of a public base class** (`ElementNode.cs:53-55`): a public base would show up as a node or need hiding; a field is easy to follow. VL.Skia's hand-written nodes (`VL.Skia/src/Video/VideoSourceToSKImage.cs:20`) also hold their helpers as fields.
- **Warnings that are only formatted on change** (`ShapeNodeCore.cs:166-178`), `LinePoints` that reuses its spread (`ShapeNodeCore.cs:125-134`), `Shape` path check once per path instance (`ShapeNodeCore.cs:145-150`; `QtPath.Flatten` is cached per instance, `QtPath.cs:161-168`).
- **Hot paths of the area** (called every frame per node): every `Update`, `ElementNode.Describe/ReadOutputs`, `ChildrenInput.Update`, `ShapeNodeCore.Describe` and `Props.Equals`, `ValueChannel.Update/Clamp/Min/Max`. Not hot: the two TooltipPreview nodes (run only while the editor shows a tooltip; their per-call string formatting at `ElementTooltipPreview.cs:86,100,129,193` is acceptable there).

## 3 Findings

### NODE-01 Cut the element node down to its specific part
- **Kind:** structure
- **Severity:** should
- **Breaking:** no (C# internals only; pins unchanged)
- **Where:** `RectangleNode.cs:32-47,87-91`, `TextNode.cs:34-58,114-119`, `ImageNode.cs:33-47,82-86`, `VideoNode.cs:36-55,102-105`, `GroupNode.cs:36-50,84-97`, `RowNode.cs:36-52,90-104`, `ColumnNode.cs:36-52,90-104`, `ShapeNodeCore.cs:51-61`, `LabeledNode.cs:67-93`, `GroupSpectralNode.cs:24-31,63`, `RowSpectralNode.cs:24-31,67`, `ColumnSpectralNode.cs:24-31,67`, `ElementNode.cs:152-158`
- **Finding:** To add an element, a newcomer copies a node and must keep in sync, by hand, three places that are identical in every sibling:
  1. The `Create` lambda starts with the same nine lines `Size = c.Size, Transform = c.Transform, Anchor = c.Anchor, Opacity = c.Opacity, Clip = c.Clip, Debug = c.Debug, Interaction = c.Interaction, Effect = c.Effect, Sink = slot` (8 lambdas, ~72 lines). Forgetting one compiles and silently drops a pin. `LabeledNode.CreateRow/CreateColumn` (`LabeledNode.cs:67-93`) already omit `Clip`/`Debug`, on purpose or not, a reader cannot tell.
  2. Every `Update` ends with `_node.Describe(…); _node.ReadOutputs(out measuredSize, out sceneBounds); return element;`, while `ShapeNodeCore.Describe` (`ShapeNodeCore.cs:140`) already does both in one call.
  3. Group/Row/Column carry a second `internal Describe` with 13 positional parameters (`GroupNode.cs:87-90`, `RowNode.cs:93-97`, `ColumnNode.cs:93-97`) only so that the Spectral nodes can wrap the whole public node (`GroupSpectralNode.cs:24`). Swapping two `float`s or two `bool`s in that list compiles.
  Every public node also carries an `internal … Core` accessor and a test constructor (see NODE-06).
- **Proposal:**
  - `Element` gets the constructor that 06 plans anyway (ELM-05, `private protected`), taking the common part: `private protected Element(in CommonInputs common, SinkSlot sink)`. Each concrete element adds `internal Rectangle(in CommonInputs common, SinkSlot sink) : base(common, sink) { }` (one line per element type; Shape, Group, Row, Column, Text, Image, Video, Rectangle, the widgets, QmlElement, ShaderEffect). A `Create` lambda then reads `static (c, p, slot) => new Rectangle(c, slot) { Fill = p.Color, Radius = p.Radius, StrokeColor = p.StrokeColor, StrokeWidth = p.StrokeWidth }`. Make the properties of `CommonInputs` `init` so that `ShapeNodeCore` writes `new Shape(c with { Size = extent, Anchor = RectangleAnchor.TopLeft }, slot)`, and `LabeledNode` states its fixed values in one `CommonInputs` value.
  - `ElementNode.Describe(in CommonInputs common, in TProps props, out Vector2 measuredSize, out RectangleF sceneBounds)` replaces `Describe` + `ReadOutputs`; a node whose element has no outputs (none today) would still call it. `Update` becomes one expression.
  - Spectral nodes hold their own `ElementNode<Group, GroupNode.Props>` and `ChildrenInput` and reuse `GroupNode.Create` (as `CircleNode` reuses `RectangleNode.Create`, `CircleNode.cs:24`). The `internal Describe` overloads and `Inner` go. Make `Create` `internal` in Group/Row/Column for that.
  - Name the private static lambdas `s_create` (AGENTS.md naming); the shared ones stay `internal static readonly … Create`.
  - Write the result down once as the template: a section "Adding an element node" in `docs/architecture.md` (or a new `docs/topics/element-nodes.md`, linked from AGENTS.md "Nodes") that names `RectangleNode.cs` as the file to copy and lists the steps (element class, `Props`, `Create`, pins, help patch, `help/Help.xml`, pin catalog for `tools/vl-patch-check.py`).
  Target size of `RectangleNode` without docs: about 35 lines.
- **Decided:** A1, O3, C2, C3, catalog → `SinkSlot` is `FeedbackSlot` and `Element.Sink` is `Element.Feedback` (A1), so the constructor reads `private protected Element(in CommonInputs common, FeedbackSlot feedback)`; the helper `ElementNode<TElement, TProps>` is `ElementNodeCore<TElement, TProps>` in `Nodes/ElementNodeCore.cs` (O3; `ShapeNodeCore` keeps its name); `CommonInputs` gets `Enabled` (optional, default true, the last input pin of every element node, C2); every element pin of the nodes is `IElement`, while `Element`, `Container` and the concrete classes stay internal (C3, A6); the catalog gives Row, Column and their Spectral nodes an optional `Style` after `Effect`, renames `Children` on the Spectral nodes to `Input`, and gives Labeled optional `Clip` and `Debug`, so `LabeledNode.CreateRow/CreateColumn` pass `Clip` and `Debug` on and the omission the finding saw is gone.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Elements/Element.cs` and every concrete element class (shared with 06, ELM-05), `src/VL.Qt/Nodes/ElementNode.cs`, `CommonInputs.cs` (new, NODE-08), all 16 node files of this area, the widget nodes using `ElementNode` (`ButtonNode.cs`, `ToggleNode.cs`, `SliderNode.cs`, `InputIntNode.cs`, `TextInputNode.cs`, `QmlElementNode.cs`, `ShaderEffectNode.cs`), `src/VL.Qt.Audio/VideoAudioNode.cs`, `docs/architecture.md`
- **Depends on:** ELM-05 (06)
- **Verification:** unit tests `RectangleNodeTests`, `TextImageNodeTests`, `VideoNodeTests`, `ContainerNodeTests`, `SpectralNodeTests`, `LabeledNodeTests`, `ShapeNodesTests`, `PathNodeTests` (the "same instance when unchanged" assertions); SmokeTest `--all`; `tools/vvvvc.ps1` on the help patches of all element nodes.

### NODE-02 Correct the pin docs that are wrong or useless in vvvv
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** Clip: `RectangleNode.cs:60`, `CircleNode.cs:40`, `TextNode.cs:80`, `ImageNode.cs:59`, `VideoNode.cs:71`, `LineNode.cs:42`, `PolylineNode.cs:44`, `PolygonNode.cs:41`, `PathNode.cs:46`. Opacity: `RectangleNode.cs:59`, `TextNode.cs:79`, `ImageNode.cs:58`. Debug: `CircleNode.cs:41`, `LineNode.cs:43`, `PolylineNode.cs:45`, `PolygonNode.cs:42`, `PathNode.cs:47` ("see Rectangle") and the full legend in the other nine nodes. `<returns>`: present only in `LineNode.cs:48`, `PolylineNode.cs:50`, `PolygonNode.cs:47`, `PathNode.cs:52`. Update summaries: every node, e.g. `RectangleNode.cs:51`.
- **Finding:** The XML docs are the pin tooltips in vvvv. Several are wrong for the node they sit on:
  - **Clip** says "Clip children to the element's size" on Rectangle, Circle, Text, Image, Video and the shapes. These elements have no children (`Children` is overridden only in `Group.cs`, `Row.cs`, `Column.cs`, `ShaderEffect.cs`). Qt's `clip` clips the item's own painting as well as its children, so on Text it cuts overflowing text, on Image (Crop) the overhang, on a Shape the stroke and points outside (0,0)–(max X, max Y). The doc describes nothing that happens.
  - **Opacity** "of the element and its children" on leaf nodes (Rectangle, Text, Image); Circle, Video and the shapes say "of the element". Same pin, two texts.
  - **Debug**: nine nodes repeat a 600-character legend that also explains Text baselines and Row/Column spacing on an Image; five nodes say "(see Rectangle)", which a user hovering a pin in vvvv cannot follow.
  - **Output** has a `<returns>` on 4 of 16 nodes; AGENTS.md asks for it.
  - Each `Update` summary says "A new description is only created when an input changes": an implementation fact, not help.
- **Proposal:** Rewrite these texts once (they go into the shared file of NODE-03):
  - Clip: "Clips the drawing of the element and of its children to its size." Shapes: "Clips the drawing to the extent (0, 0) to (max X, max Y) of the points; the stroke and points outside are cut."
  - Opacity: one text for all: "Opacity of the element (and its children). 0 = not drawn and costs no render time, but the element keeps its space in Row/Column and still receives input."
  - Debug: "Draws the debug overlay of this element only (not its children); does not change layout or hit-testing. The colors are explained in the help patch HowTo Debug a Scene." (use the actual help patch name; if none explains the legend, put the legend into `docs/diagnostics.md` and the Renderer's Debug pin doc, the one place users look first).
  - `<returns>`: "The element; connect it to Renderer [Qt] or a Group." on every element node.
  - Update summary: drop, or one sentence of user help where the class summary does not already say it.
- **Decided:** C16, C2, P11, D1 → `Clip` stays on every element, Rectangle and Circle included; wave 1 checks with a SmokeTest grab (Clip on, DropShadow) whether it ever matters there, and the Clip text is written after that grab and says honestly what the pin does; the Opacity text names `Enabled` (optional, last input pin, C2): `Enabled` off takes no space and the siblings in Row/Column move up while the element keeps its identity, Opacity 0 keeps the gap (P11); the texts live in `Nodes/CommonPins.xml` by `<include>` (D1), with an entry for `enabled`.
- **Wave 1 (W1-17):** measured C16 with `ClipVideoPerfTest.ClipGrab` (Windows x64, Release, RTX 5080, on-screen 800x600 window, grabs in `build/smoke/grabs/clip_*`): Rectangle and Circle, each plain, with stroke, with DropShadow and with both, Clip off against Clip on differ in 0 of 480000 pixels in all 8 pairs (the shadow outside the item is not cut). Consequence for the decided rule: on Rectangle and Circle `Clip` never matters, so the pin help says honestly that it has no visible effect there (where it can matter is the elements that can paint outside their size or have children, not measured here); the pin stays (C16 a).
- **Effort:** S if done together with NODE-03, else M; mechanical: no
- **Touches:** the 16 node files of this area; the other element-like nodes with the same pins (widget nodes, `QmlElementNode.cs`, `ShaderEffectNode.cs`, `VL.Qt.Audio/VideoAudioNode.cs`, 24 in total have a `debug` pin)
- **Depends on:** none (better together with NODE-03)
- **Verification:** build with `GenerateDocumentationFile`, open the pin tooltips of Rectangle, Text and Polyline in vvvv (`manual-check`).

### NODE-03 One source for the common pin docs
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** 148 common `<param>` lines in this area (size, transform, anchor, opacity, clip, debug, interaction, effect, measuredSize, sceneBounds), 203 in `src/VL.Qt`; e.g. `RectangleNode.cs:52-65`, `GroupSpectralNode.cs:34-46`
- **Finding:** Every element node repeats ten pin texts. They drifted (NODE-02), and the next edit of, say, the Anchor semantics has to touch 24 files. The options and what each means for the help in vvvv (vvvv reads the XML documentation file that the compiler writes next to `VL.Qt.dll`; `src/VL.Qt/VL.Qt.csproj:12`):
  1. **`<include file="…" path="…"/>`**: the C# compiler resolves `<include>` while it writes the documentation file, so vvvv sees the full text as if it were inline. One line per pin stays in each node, the text lives in one XML file. Node-specific texts (Group's Anchor, Circle's hit-testing note) stay inline. Cost: a newcomer must learn that the file exists; a wrong path is a compiler warning (CS1589), not an error unless warnings are errors.
  2. **`<inheritdoc cref="…"/>`**: Roslyn writes the tag unresolved into the documentation file. Whether vvvv resolves it is not known; VL.Core only uses it on non-node API (`VL.Core/src/Lang.PublicAPI/IDevSession.cs:57`) and has no resolver in the open sources. Risk: empty pin help. Would need one test build and a look in vvvv before relying on it.
  3. **Source generator**, as VL.ImGui does it: `[GenerateNode]` on a widget class (`VL.ImGui/src/Widgets/Checkbox.cs:5`), common pins as properties of the base class `ChannelWidget<T>`/`Widget`, documented once, and the generator copies each property's docs into the node (`VL.ImGui.Generator/SourceGenerator.cs:330-350`, `GetDocumentationCommentXml`). Removes pins, `Props`, `Create` and docs duplication at once. Cost: a generator project, generated code to debug, a pattern a newcomer has to learn before touching any node; for 24 nodes the generator would be larger than what it saves.
  4. **Shorter texts** only: halves the volume, keeps the drift.
  5. **Accept**: 200 lines of identical text, drift continues.
- **Proposal:** Option 1 with the corrected, shorter texts of NODE-02. File `src/VL.Qt/Nodes/CommonPins.xml`:
  ```xml
  <pins>
    <param name="transform">Position, rotation and scale, applied around the Anchor. Inside Row/Column the layout places the element and Transform is only visual.</param>
    <param name="opacity">…</param>
    …
  </pins>
  ```
  In a node: `/// <include file="CommonPins.xml" path="pins/param[@name='opacity']"/>`. Use distinct entries where the meaning differs by family (e.g. `transform-shape` "applied around the origin of the parent space"). Add one line to AGENTS.md "Documentation": common pins take their text from `Nodes/CommonPins.xml`. Verify once that vvvv shows an included text before converting all nodes.
- **Effort:** M, mechanical: yes (after the texts are written)
- **Touches:** `src/VL.Qt/Nodes/CommonPins.xml` (new), the 24 nodes with common pins, `AGENTS.md`
- **Depends on:** NODE-02 (texts)
- **Verification:** inspect `bin/Release/…/VL.Qt.xml` for the expanded text; pin tooltip in vvvv (`manual-check`).

### NODE-04 Make the default-value rule true in one place
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `RectangleNode.cs:66-69` (the rule as a comment), pointers `CircleNode.cs:46`, `TextNode.cs:86`, `ImageNode.cs:65`, `VideoNode.cs:79`; `AGENTS.md:86`; redundant names `LineNode.cs:54-61`, `PolylineNode.cs:56-65`, `PolygonNode.cs:53-59`, `PathNode.cs:58-68`; explicit zero defaults `LineNode.cs:58-61`, `PolygonNode.cs:54,58`, `PolylineNode.cs:54`; implicit ones `RectangleNode.cs:76,78`, `RowNode.cs:80-81`, `TextNode.cs:94,104`, all `clip`/`debug` pins
- **Finding:**
  - AGENTS.md tells contributors to write `[DefaultValue(typeof(Int2), "1920, 1080")]` for structs. The comment in `RectangleNode.cs:66-69` says that exactly this form gives `null` for `Color4`/`Vector2` (no `TypeConverter`) and that the string form `[DefaultValue("1, 1, 1, 1")]` is what works, checked in the C# vvvvc generates. `VideoNode.cs:79` turns it into "typeof(T) defaults are ignored by vvvvc", which is wrong for the enum pins on the same node (`VideoNode.cs:84,91` use `typeof(RectangleAnchor)` and work, enums have a converter). A contributor following AGENTS.md gets silent zero defaults.
  - Shape nodes write `[Pin(Name = "Stroke Color")]`, `"Stroke Width"`, `"Miter Limit"`, `"Dash Offset"`, `"Trim Start/End/Offset"`, `"Fill Rule"`: vvvv derives exactly these names from the camelCase parameter (`RectangleNode.cs:77` has `strokeColor` without and shows "Stroke Color"). AGENTS.md: add `Name` only where the derived name is wrong.
  - Shape nodes spell out `[DefaultValue(0f)]`/`[DefaultValue(false)]`; Rectangle, Row, Text and every `clip`/`debug` pin leave the type default implicit. AGENTS.md says "every default explicitly".
- **Proposal:** Move the rule into AGENTS.md "Nodes" and delete the comments: "Enums: `[DefaultValue(typeof(E), nameof(E.X))]`. Stride structs (`Vector2`, `Color4`, `RectangleF`): the IOBox string form `[DefaultValue("1, 1, 1, 1")]`, invariant culture; the `typeof` form yields null for them. `Matrix`: no attribute, vvvv uses Identity." Confirm the Int2 case with one vvvvc export before writing it. Remove the redundant `Pin(Name = …)` on the four shape nodes and merge their split attribute lists (`[Pin(…)] [DefaultValue(…)]` → `[Pin(…), DefaultValue(…)]` as in the other nodes). Settle explicit-vs-implicit zero defaults with Q3 and apply it to all nodes of the area.
- **Decided:** D3 → the AGENTS.md rule reads "set a default explicitly unless `default(T)` is the intended value"; the explicit `[DefaultValue(0f)]`, `[DefaultValue(false)]` and null defaults of the shape nodes go, `tools/vl_pin_catalog.py` reads a missing default as `default(T)`, and Q3 below is answered (b); A2 of [DECISION-ROUND.md](../DECISION-ROUND.md) → never for `Matrix`: vvvv's default of a `Matrix` pin is the identity, so a `Matrix` pin carries no attribute.
- **Effort:** S (rule, names) + M (defaults, depending on Q3); mechanical: yes
- **Touches:** `AGENTS.md`, the 16 node files of this area
- **Depends on:** Q3
- **Verification:** `python tools/vl-patch-check.py` (pin catalog reads defaults from the C# sources: run its regression test `tools/test-vl-patch-check.py`), `tools/vvvvc.ps1` on the help patches of the shape nodes (pin names must be unchanged, links must survive).

### NODE-05 One helper for node messages; no stale message after a switch
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `ElementNode.cs:75-100,160-182`, `ShapeNodeCore.cs:90-111,166-187`, `LabeledNode.cs:33-53,139-147`
- **Finding:** "Hold a string and the `IDisposable` of its persistent message, replace both when the text changes" is written three times (`ElementNode.UpdateWarning`, `ShapeNodeCore.UpdatePointsWarning`, `ShapeNodeCore.UpdatePathWarning`), and a fourth way exists beside it: errors go through `NodeErrors.Warn/Clear` (logged once, `NodeErrors.cs:53-58`), warnings through `AddPersistentMessage` (not logged). A reader has to work out why the sink error is logged and the sink warning or the points warning is not; I found no reason in the code. Under the test constructor `_errors` is null (`ElementNode.cs:96-100`), so tests never run the error path of production.
  Related defect (could): `LabeledNode` keeps both a Row and a Column `ElementNode`, but only the active one is described. A warning or error shown by the Row stays at the node after Label Position switches to Top, until it switches back (`LabeledNode.cs:139-147`; `UpdateWarning`/`UpdateError` only run inside `Describe`).
- **Proposal:** One internal sealed class `NodeMessage` (file `Nodes/NodeMessage.cs`): `NodeMessage(Func<string, IDisposable> add)`, `void Set(string? text)`, `string? Text`, `Dispose()`. `ElementNode` and `ShapeNodeCore` use it for every message; decide in the same change whether sink warnings are logged (recommendation: yes, through `NodeErrors.Warn`, so all Qt-reported problems are in the log as AGENTS.md "Errors and logging" asks; then `NodeMessage` wraps `NodeErrors` with one phase per message source). `LabeledNode` clears the inactive layout's messages when the position changes (call `Dispose` on its messages, or describe both and drop one).
- **Decided:** P22, "No `Error` output pins" → one issue with RUN-01 and NODW-04, one mechanism for messages at a node (A1 of [DECISION-ROUND.md](../DECISION-ROUND.md): `NodeErrors` is the one store with one entry per phase, `NodeMessage` the handle of one message source on it); every error goes to the vvvv log and a lasting error also marks the node (warning or error, the message as tooltip), no message goes to an `Error` output.
- **Effort:** S, mechanical: no
- **Touches:** `Nodes/ElementNode.cs`, `Nodes/ShapeNodeCore.cs`, `Nodes/LabeledNode.cs`, `Nodes/NodeMessage.cs` (new), possibly `Nodes/NodeErrors.cs`
- **Depends on:** NODE-06 (test seam)
- **Verification:** `LabeledNodeTests` (add: warning disappears after switching Left → Top), `ShapeNodeCoreTests`, `PathNodeTests` (points and path warnings).

### NODE-06 Test hooks in the node area (K6)
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** test constructors `RectangleNode.cs:29`, `CircleNode.cs:27`, `TextNode.cs:31`, `ImageNode.cs:30`, `VideoNode.cs:32`, `GroupNode.cs:33`, `RowNode.cs:33`, `ColumnNode.cs:33`, `GroupSpectralNode.cs:29`, `RowSpectralNode.cs:29`, `ColumnSpectralNode.cs:29`, `LineNode.cs:25`, `PolylineNode.cs:25`, `PolygonNode.cs:25`, `PathNode.cs:26`, `LabeledNode.cs:48`, `ShapeNodeCore.cs:107`, `ElementNode.cs:96`, `ElementTooltipPreview.cs:60`, `QtImageTooltipPreview.cs:25`; accessors `Core` (`RectangleNode.cs:49`, `CircleNode.cs:30`, `TextNode.cs:60`, `ImageNode.cs:49`, `VideoNode.cs:57`, `GroupNode.cs:52`, `RowNode.cs:54`, `ColumnNode.cs:54`, `LineNode.cs:27`, `PolylineNode.cs:27`, `PolygonNode.cs:27`, `PathNode.cs:28`, `ShapeNodeCore.cs:113`), `Inner` (`*SpectralNode.cs:31`), `Children` (`GroupNode.cs:53`, `RowNode.cs:55`, `ColumnNode.cs:55`); counters `ElementNode.Created` (`ElementNode.cs:114`), `ChildrenInput.Built` (`ChildrenInput.cs:31`); state mirrors `ElementNode.Warning/Error` (`:108,111`), `ShapeNodeCore.Warning/PathLimitWarning` (`:116,119`)
- **Finding:** About 45 members exist only for tests. Tests never build a `NodeContext` (no use in `src/VL.Qt.Tests`); `NodeContext.Default` needs `AppHost.Global` (`VL.Core/src/NodeContext.cs:23`), so a constructor seam is needed somewhere. But it is needed once, not in every node, and the counters and accessors are replaceable.
- **Proposal:**
  - `Created` and `Built` go: "no new description" is `Assert.Same(first, second)` on the `Output` of two Updates; "no new children array" is reference equality of `((Group)output).Children` (an `ImmutableArray` compares its backing array).
  - `Core`, `Inner`, `Children` go: the sink a test needs is reachable from the description (`Element.Sink`, `Element.cs:34`); Spectral nodes are tested through their own `Update` (after NODE-01 they have no `Inner`).
  - One test seam: `ElementNode` and `ShapeNodeCore` take an internal `INodeMessages` (implemented by `NodeErrors` in production and by a recording fake in the tests). Public nodes keep one constructor `internal XNode(INodeMessages messages)`, the only test hook per node, and tests then run the same message path as vvvv (NODE-05). `Warning/Error/PathLimitWarning` go; tests read the fake.
  - The tooltip previews keep their injection constructors (fake `ResourceClient`, clock): they replace a GPU and a timer and have no cheaper substitute.
- **Decided:** P22, O3, A1 → worked in the issue of TEST-07 together with the other K6 findings (REC-14, RUN-14, SHD-12 …), after NODE-01 and NODE-05; `Element.Sink` is `Element.Feedback` (A1), the core class is `ElementNodeCore` (O3), and the message seam is the one mechanism of P22 (A1 of [DECISION-ROUND.md](../DECISION-ROUND.md): a seam `INodeMessages` only if the spike of TEST-07 cannot observe the warning through `NodeContext.Create`).
- **Effort:** M, mechanical: no
- **Touches:** the 16 node files, `ElementNode.cs`, `ShapeNodeCore.cs`, `ChildrenInput.cs`, `NodeErrors.cs`, the widget nodes using `ElementNode`; tests `RectangleNodeTests`, `TextImageNodeTests`, `VideoNodeTests`, `ContainerNodeTests`, `SpectralNodeTests`, `LabeledNodeTests`, `ShapeNodesTests`, `ShapeNodeCoreTests`, `PathNodeTests`, `InteractionTests`, `EffectTests`, `EffectPaddingTests`, `ReconcilerSceneTests` (uses `.Children` 44×; check which are node accessors), SmokeTest `AnchorTest`, `ContainerNodeTest`, `EffectChainTest`, `InteractionTest`, `MultiPlacementTest`, `OpacityZeroTest`, `QtImageTest`, `VideoTest`
- **Depends on:** NODE-01, NODE-05
- **Verification:** `dotnet test src/VL.Qt.Tests`, SmokeTest `--all`.

### NODE-07 Move Button-only and widget-only code out of the shared core
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `ElementNode.cs:134-150` (`Author`, `TakeClicks`), `ValueChannel.cs:33`, `Nodes/Interaction/InteractionNode.cs:29`, `ButtonNode.cs:93`
- **Finding:** `ElementNode.TakeClicks` is used by `ButtonNode` alone; the shared core of 24 nodes carries a Button feature. The channel author `"Qt"` is defined three times (`ElementNode.Author`, `ValueChannel.Author`, `InteractionNode.Author`).
- **Proposal:** Move `TakeClicks` into `ButtonNode` (it is `Sink.TakeClicks()` plus a write loop; `ClickNode` already uses `InteractionNode.WriteEvents` for the same loop, reuse that). One constant `internal const string QtChannelAuthor = "Qt"` in one place (e.g. a static class `ChannelAuthors` in `Nodes/`), used by all three.
- **Decided:** A1, O3, catalog → `Sink.TakeClicks()` is `Feedback.TakeClicks()` (A1) and moves into `ButtonNode` as proposed; the Button's channel pin is named `On Click` (catalog); `ChannelAuthors` is `Nodes/ChannelAuthors.cs` and the channel author of `ElementNode` goes with the rename to `ElementNodeCore` (O3).
- **Effort:** S, mechanical: yes
- **Touches:** `Nodes/ElementNode.cs`, `Nodes/ValueChannel.cs`, `Nodes/ButtonNode.cs`, `Nodes/Interaction/InteractionNode.cs`
- **Depends on:** none
- **Verification:** `ButtonNodeTests`, `InteractionTests`.

### NODE-08 File layout and small names in the core
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `ElementNode.cs:16-50` (`CommonInputs`), `LabeledNode.cs:14-20` (`LabelPosition`), `ShapeNodeCore.cs:115-134` (`Warning`, `LinePoints`), `ElementTooltipPreview.cs:20`, `QtImageTooltipPreview.cs:12`
- **Finding:** `CommonInputs` and `LabelPosition` share files with other types (K3). `ShapeNodeCore.Warning` is the points warning beside `PathLimitWarning`; `LinePoints` is used only by `LineNode`. The two TooltipPreview classes are nodes without the `Node` suffix (AGENTS.md) and are editor support, not elements, in the same folder.
- **Proposal:** see section 4: `CommonInputs.cs`, `LabelPosition.cs`; `LinePoints` and its fields move into `LineNode`; `ShapeNodeCore.Warning` goes with NODE-06 (else rename `PointsWarning`). Rename `ElementTooltipPreview` → `ElementTooltipPreviewNode`, `QtImageTooltipPreview` → `QtImageTooltipPreviewNode` (vvvv name "TooltipPreview" unchanged, so `VL.Qt.HDE.vl` keeps working) and move them to `Nodes/Editor/`.
- **Decided:** catalog, S1, TARGET 2.2 → `LabelPosition` moves to `Elements/Enums/LabelPosition.cs`, namespace `VL.Qt.Elements`, category `Qt.Elements` (not `Nodes/LabelPosition.cs`); `CommonInputs.cs` stays in `Nodes/`; the two tooltip nodes are `Nodes/Editor/ElementTooltipPreviewNode.cs` and `QtImageTooltipPreviewNode.cs` and their aspect becomes `Hidden` (catalog: TooltipPreview ×2).
- **Effort:** S, mechanical: yes
- **Touches:** `Nodes/ElementNode.cs`, `Nodes/CommonInputs.cs` (new), `Nodes/LabeledNode.cs`, `Nodes/LabelPosition.cs` (new), `Nodes/ShapeNodeCore.cs`, `Nodes/LineNode.cs`, `Nodes/ElementTooltipPreview.cs`, `Nodes/QtImageTooltipPreview.cs`, `src/VL.Qt.Tests/ElementTooltipPreviewTests.cs`, `src/VL.Qt.Tests/QtImageTooltipPreviewTests.cs`
- **Depends on:** none
- **Verification:** build; `tools/vvvvc.ps1 -File` on `VL.Qt.HDE.vl` (the tooltip nodes are found by name).

## 4 Target structure

Namespace `VL.Qt.Nodes` stays for every file; public are the node classes and `LabelPosition` only.

```
src/VL.Qt/Nodes/
  ElementNode.cs          internal sealed ElementNode<TElement, TProps>: change detection, sink → outputs, messages;
                          Describe(in common, in props, out measuredSize, out sceneBounds) (NODE-01);
                          Created, Warning, Error, TakeClicks, Author, second ctor removed (NODE-06, NODE-07)
  CommonInputs.cs         NEW (split from ElementNode.cs): internal readonly record struct, init properties
  ChildrenInput.cs        internal sealed: pin group / sequence → stable ImmutableArray<Element> (Built removed)
  ValueChannel.cs         internal sealed ValueChannel<T> (Author → ChannelAuthors)
  ChannelAuthors.cs       NEW internal static: QtChannelAuthor = "Qt" (NODE-07)
  NodeMessage.cs          NEW internal sealed: one persistent node message, replaced on change (NODE-05)
  CommonPins.xml          NEW: shared <param> texts for <include> (NODE-03)
  ShapeNodeCore.cs        internal sealed: Props, Create, points/path warnings (LinePoints → LineNode)
  RectangleNode.cs        public: template node to copy (docs/architecture.md "Adding an element node")
  CircleNode.cs  TextNode.cs  ImageNode.cs  VideoNode.cs
  GroupNode.cs  RowNode.cs  ColumnNode.cs           internal static Create, internal Props; no Describe overload
  GroupSpectralNode.cs  RowSpectralNode.cs  ColumnSpectralNode.cs
                          own ElementNode + ChildrenInput, reuse <Container>Node.Create/Props (no Inner)
  LineNode.cs  PolylineNode.cs  PolygonNode.cs  PathNode.cs
  LabeledNode.cs          public LabeledNode
  LabelPosition.cs        NEW (split from LabeledNode.cs): public enum
  Editor/
    ElementTooltipPreviewNode.cs   was ElementTooltipPreview.cs (renamed, moved)
    QtImageTooltipPreviewNode.cs   was QtImageTooltipPreview.cs (renamed, moved)
```

Deleted members: `Core`, `Inner`, `Children` accessors on the nodes; the 13-parameter `Describe` overloads of Group/Row/Column; `ElementNode.ReadOutputs`, `Created`, `Warning`, `Error`, `TakeClicks`, `Author`; `ChildrenInput.Built`; `ShapeNodeCore.Warning`, `PathLimitWarning`, `Core`; the "Pin defaults: see RectangleNode" comments.

## 5 Questions for Sebastian

**Q1 Common pin docs: `<include>`, generator or accept?** Options in NODE-03. `<include>`: text in one file, one doc line per pin stays, vvvv sees the full text, small change. Source generator (VL.ImGui's way): removes pins, `Props`, `Create` and docs duplication together, but adds a generator project and a pattern every contributor must learn first. Accept: 200 duplicated lines that drift. **Recommendation:** `<include>` plus the shorter texts of NODE-02; revisit a generator only if the node count grows well beyond the current 24.

**Q2 K5: keep or drop the rule for `[Pin(Visibility = PinVisibility.Hidden)]` on the `NodeContext` parameter?** Evidence: vvvv recognizes the `NodeContext` constructor parameter by its type. In VL.StandardLibs 66 node constructors take it without the attribute and 4 with it (with: `VL.CoreLib/src/Reactive/PropertyChannel.cs:41`, `VL.IO.Redis/src/BindingNode.cs:27`, `VL.IO.Redis/src/Publish.cs:25`; without, e.g. `VL.Skia/src/SkiaRenderer.cs:47`, `VL.Skia/src/Video/VideoSourceToSKImage.cs:20`, `VL.Skia/src/SkiaRendererNode.cs:42`). In VL.Qt 55 of 61 nodes have no attribute and their help patches compile and run. Alternatives: (a) drop the rule from AGENTS.md and remove the six attributes (e.g. `FileWriterNode.cs:26`, `FileWriterElementNode.cs:27`) for consistency; (b) keep the rule and add 55 attributes that change nothing. **Recommendation:** (a), it matches Elias' code and removes noise.

**Q3 Explicit defaults for zero and false?** AGENTS.md says "every default explicitly"; the element nodes leave `0`, `false` and `null` implicit except the shape nodes. Alternatives: (a) add `[DefaultValue(0f)]`/`[DefaultValue(false)]` everywhere (about 60 attributes in the element and widget nodes); (b) change the rule to "explicit unless `default(T)` is the intended default; never for `Matrix`", and remove the explicit zeros from the shape nodes. **Recommendation:** (b): the rule's reason ("the type's default is rarely what the help patch needs") does not apply when it is what is wanted, and fewer attributes read better. `tools/vl_pin_catalog.py` must then treat a missing attribute as `default(T)`; check that it does.

**Q4 Clip on Rectangle and Circle.** Qt's clip cuts the item's own painting and its children to its size. Rectangle and Circle have no children and draw only inside their size, so the pin probably has no visible effect there (not verified: an effect layer such as DropShadow might interact with it; one SmokeTest grab with Clip on and a DropShadow would confirm). Alternatives: (a) keep Clip on every element for a uniform common pin set and document it honestly; (b) remove it from Rectangle and Circle (`Breaking: yes`, patches with a link to Clip lose it). **Recommendation:** (a) unless the grab shows it is never useful; uniform pins make copying nodes and learning the library easier.

## 6 For other areas

- Elements (06): ELM-05's `private protected Element(...)` constructor should take `in CommonInputs` and `SinkSlot` so the node `Create` lambdas shrink (NODE-01); `CommonInputs` then has to be visible to `Elements` (move it there or keep it `internal` in `Nodes`, same assembly).
- Widgets (Button, Toggle, Slider, InputInt, TextInput): same boilerplate and the same doc drift as here; apply NODE-01 to NODE-03 there too. `ButtonNode.cs:93` is the only user of `ElementNode.TakeClicks`.
- VL.Qt.Audio: `VideoAudioNode.cs:101` reuses `VideoNode.Props` and `VideoNode.Create` and ignores the Volume pin when a tap is set (`_tap is null ? 0f : 1f`); check that this is intended and documented on its pin.
- Interaction nodes: `InteractionNode.Author` (`Nodes/Interaction/InteractionNode.cs:29`) is the third copy of the channel author constant (NODE-07).
- AGENTS.md: the struct default example `[DefaultValue(typeof(Int2), "1920, 1080")]` contradicts the vvvvc finding in `RectangleNode.cs:66-69` (NODE-04).
