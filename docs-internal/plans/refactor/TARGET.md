# Target – refactor (release 0.10.0)

Claude Fable 5, 2026-11-19, on `refactor` at daefc65. Draft after P3 of [PLAN-CHECK.md](PLAN-CHECK.md): the words, the folders and namespaces, and the files that several findings share. Put together from sections 4 and 6 of the 23 reports in [review/](review/README.md) and from the decisions in [DECISIONS.md](DECISIONS.md) and [QUESTIONS.md](QUESTIONS.md). Nothing was built or run.

Every line has one of three states:

- **decided**: follows from a decision, which is named.
- **merged**: one report proposes it and no other report or decision contradicts; it holds unless it is struck here.
- **open**: reports or decisions clash. There were ten such questions: seven words (O1 to O7, in 1.4) and three of structure (S1 to S3, in 2.8). All are decided (Sebastian, 2026-11-19); each carries its decision and what was not taken, and section 4 lists them.

Checked in the code: every name that carries a `path:line`. Not checked: the other claims of the reports, and part 3, which is computed from the `Touches` lines of the findings as written.

## 1 Glossary

This part becomes `docs/glossary.md` (DOC-09). Section 1.2 and 1.3 move there as they are; the columns "Names after the refactoring" and "Today" stay here.

### 1.1 Rules for words

1. One word per concept in the C ABI, in C++, in C#, in the docs and in node names. Where two layers hold two halves of one thing, both halves carry the word: `vlqt_scene`, `vlqt::Scene`, `Scene`.
2. Where a user reads it, vvvv's word wins over Qt's (A7). Qt's word names Qt's own thing and appears in QtNative and in 1.3: item, handler, control, layer.
3. A word that vvvv already uses keeps vvvv's meaning: node, link, pin, slice, frame, renderer, feedback, and "layer" as VL.Skia uses it. Most open rows come from this rule.
4. A suffix says what a class is: in C# `…Node` is a vvvv node (AGENTS.md); in C++ `…Item` derives from `QQuickItem` and `…Node` from `QSGNode` (O3).
5. No abbreviations in C# names (PROTO-10). The C names follow the C# names, so that the drift check stays a mechanical mapping.

### 1.2 VL.Qt terms

| Word | Means | Names after the refactoring | Today and in the reports | State |
|---|---|---|---|---|
| **QtNative** | the C++ layer that owns Qt: `vlqt.dll`, `libvlqt.so` | folder `src/VL.Qt.Native`, namespace `vlqt` | "native layer", "the C++ layer" | merged (DOC-10) |
| **C ABI** | the `extern "C"` functions of QtNative | `vlqt.h`, names `vlqt_<group>_<verb>`, groups of CAPI-03 | "C API", "exports" | decided (A5) |
| **protocol** | the contract of the binary records between C# and QtNative: versions, ids, layouts | `protocol.h`, `Protocol.cs`, `docs/protocol.md` | "wire protocol" | merged (PROTO-04) |
| **wire** | the bytes of the protocol and the code that reads and writes them | `wire.h`, C# folder `Wire/` (S3) | "wire format" | merged |
| **command** | one record from C# to QtNative | `CommandWriter`, `ICommandSink`, `vlqt_scene_apply` | `vlqt_window_apply` | decided (A5) |
| **event** | one record from QtNative to C# | `EventQueue`, `EventReader`, `VLQT_EVENT_*`, `vlqt_scene_poll` | `EV_*`, `Protocol.Ev`, "poll record" | merged (PROTO-10) |
| **element** | the immutable description of one thing to show; what the element pins carry | `IElement` (only public type, C3); internal `Element`, `Container` and the concrete classes (A6) | "item" for Group in the protocol (`protocol.h:299`) | decided (C3, A6) |
| **element tree** | the elements linked to one renderer in one frame | docs only | "scene" in some docs; `docs/architecture.md:7` says "Element tree" | merged |
| **container** | an element with children: Group, Row, Column | internal `Container` (ELM-10); protocol type `GROUP` | `VLQT_TYPE_ITEM` | decided (A5, NELM-11) |
| **widget** | an element the user operates: Button, Toggle, Slider, Input | category `Qt.Widgets` | "control" (Qt's word, stays in QtNative: `type_controls.cpp`) | merged |
| **stylable widget** | a widget patched by the user, an implementation of `IStylable` | `IStylable`, `StylableElement` | "self-made widget", "stylable" | merged (P5) |
| **node** | a vvvv node, nothing else; in C++ the word belongs to Qt's scene-graph nodes (`QSGNode`) | C# classes `…Node` | also `Node` in the reconciler (`src/VL.Qt/Reconciler/Node.cs:13`) and in QtNative (`src/VL.Qt.Native/elements.h:114`) | decided (O3) |
| **element node** | a vvvv node that outputs an element | `RectangleNode` …; their shared helpers end in `…NodeCore`: `ElementNodeCore`, `InteractionNodeCore`, `ShapeNodeCore` | `ElementNode<TElement, TProps>` is the helper, not a node (`src/VL.Qt/Nodes/ElementNode.cs:69`) | decided (O3) |
| **entry** | the record of a scene in QtNative for one element or one handler | `ElementEntry`, `HandlerEntry` | `Node`, `HandlerNode` (`elements.h:114`, `events.h:211`) | decided (O3) |
| **renderer** | a node that shows an element tree: Renderer, QtTexture, QtRenderer | `RendererNode`, `QtTextureNode`, `QtRendererNode` | C# classes `Renderer` (`src/VL.Qt/Renderer.cs:24`), `WindowRenderer`, `OffscreenRenderer` | merged (RUN-06) |
| **scene** | what one renderer owns on the Qt side: its elements and the target they are drawn to, a window or an offscreen texture | C `vlqt_scene`; C++ and C# alike: `SceneHandle` (what the C handle points to), `Scene` (the shared part), `WindowScene`, `OffscreenScene` | C++ `Window`, `Scene`, `offscreen::State`; C# `SceneCore`, `WindowRenderer`, `OffscreenRenderer`, `SceneRegistry`; "scene" for the element tree in some docs | decided (A5, O2) |
| **effect** | one step of the chain at the `Effect` pin; "effect" never means anything outside that chain | `Effect` (public base), `BlurEffect` …, category `Qt.Effects` | `ShaderEffect` for the element | decided (C14, C11) |
| **effect chain** | the effects of one element in order | `EffectChain` (C++), `EffectChainMirror`, `EffectChainWriter` (C#) | "chain", "run" | merged |
| **merged effects** | Blur, DropShadow, ColorAdjust, Colorize, Mask: neighbours in a chain merge into one layer (Qt's MultiEffect) | docs and summaries only | "layer effects" (DOC-10), "MultiEffect nodes", category `Qt.Effects.MultiEffect` | decided (C11, O4) |
| **shader effects** | the other effects: each is one shader with a layer of its own | docs and summaries only | — | decided (O4) |
| **layer** | Qt's meaning only (1.3): an item drawn into a texture of its own. What VL.Skia calls a layer is an element here. Named exception: the Blend pin `Layer`, which takes an element (catalog) | `LayerRequests` (C++) | `FeedbackLayer` (O1), "layer effects" | decided (O4) |
| **shader** (element) | the element that draws a fragment shader | node `Shader [Qt.Elements.Shaders]`; `Shader`, `ShaderNode`, `ShaderMirror` (C#), `VLQT_ELEMENT_TYPE_SHADER`, `ShaderItem` (C++) | `ShaderEffect`, `ShaderEffectNode`, `VLQT_TYPE_SHADER_EFFECT` (`protocol.h:312`), `ShaderEffectItem` | decided (C14, O5) |
| **shader effect** (code) | an effect of a chain that runs one shader; from 0.10.0 on `ShaderEffect` names this, no longer the element | `ShaderEffect : Effect`, `ShaderEffectBuilder` | `ShaderLink` (`Effect.cs:131`), `BuiltinShaderLink` | decided (O5) |
| **link** | a vvvv link between two pins; in code only one entry of an effect chain as it is sent | `EffectChainLink`, `links` in QtNative | `ShaderLink`, "link node" | decided (O5) |
| **uniform**, **sampler** | a value and a texture input of a shader | `Uniform`, `Sampler`, nodes in `Qt.Elements.Shaders(.Uniform)` | `Qt.Elements.Uniform` | decided (C15) |
| **interaction** | the description of an input behaviour at the `Interaction` pin: Click, Drag, Hover … | `Interaction` (public base), `ClickInteraction` …, category `Qt.Interaction` | — | merged (ELM) |
| **handler** | Qt's object that an interaction creates (1.3) | `VLQT_ELEMENT_TYPE_*_HANDLER`, `HandlerMirror` (C#), `HandlerEntry` (C++) | `HandlerNode` in both layers (`Node.cs:131`, `events.h:211`) | decided (O3) |
| **style** | the look of widgets as a value: the `Style` record and the style nodes | `Style`, category `Qt.Style`, native `VlStyle` | — | decided (catalog) |
| **path** | an immutable 2D path | `QtPath`, category `Qt.Paths` | — | merged |
| **image** | a picture held by QtNative, as a value in the patch | `QtImage`, category `Qt.Images` | category `Qt` | decided (catalog) |
| **resource** | whatever QtNative loads and holds under an id: image, shader, snapshot, audio tap | C `vlqt_res_*` (lifecycle), `vlqt_image_*`, `vlqt_shader_*`, `vlqt_snapshot_*`, `vlqt_audio_tap_*`; C++ `vlqt::res`; C# `QtResource`, `ResourceClient` | `ImageResource` (`src/VL.Qt/Images/QtImage.cs:54`), `vlqt_res_load_image` … | decided (A5), merged (SIP-06) |
| **snapshot** | the picture of an element as an image resource | node `Snapshot`, `vlqt::res::snapshot` | `vlqt::res::snap` (`snapshot_resource.cpp:28`) | merged (NRES) |
| **node-API snapshot** | the text file of the public C# surface that a test compares (TEST-01, P6) | `node-api.txt`, `NodeApiSnapshotTests` | — | decided (P6); always written in full |
| **frame** | one frame of vvvv's mainloop, nothing else. Named exception: the style group `Frame` (background and border of a widget), written "the Frame style" | `frameInterval` for the time between two frames (NDIAG-10) | also the rectangle of an element in QtNative; `PerfSample.frame` | decided (O6) |
| **pump** | Qt's work for one vvvv frame: events, animations, rendering | `QtPump`, `PumpFrame`, `vlqt_app_process_events_for` | "Qt pump", "native pump" | merged (DOC-10) |
| **host** | the vvvv app that embeds QtNative | `vlqt_host_pump`, `HostPumpMeter` (1.6) | `vlqt_host_frame` (`vlqt.h:79`), `HostFrameHistory` (`QtApp.cs:243`) | decided (A5) |
| **animation clock** | the time Qt's animations run on, set by vvvv | `vlqt::clock::Driver`, `vlqt_app_advance_animations` | `VlqtAnimationDriver` (`vlqt.cpp:131`), `vlqt_driver_advance` | merged (NAPP-01, CAPI-03) |
| **render thread** | a thread of the pool that renders offscreen scenes | pin `Render Threads`, `vlqt::render_thread` | `VLQT_OFFSCREEN_THREAD` | decided (E6, E7) |
| **unit** | a length in the space of the renderer (`Space` pin) | `Space`, `SpaceMapping` | "renderer units" | merged (DOC-10) |
| **DIP** | Qt's logical pixel; shader parameters and style sizes are in DIP | — | "px" in `docs/styles.md:28` | decided (E1); the style sizes are a catalog row for the pass of P2 |
| **px** | a device pixel, only that | — | "px" for DIP | merged (DOC-10) |
| **bounds** | the rectangle of an element | `itemBounds`, the bounds of a `GroupItem`, mask bounds | "frame" in QtNative: `itemFrame`, "mask frame" (`debug.h:13`) | decided (O6) |
| **id** | a number that C# chooses and sends: element id, handler id, resource id, feedback id | `IdAllocator`; `feedbackId` in BIND and in the event header, `EventReader.FeedbackId` | `sinkHandle` (`protocol.h:17`), `feedbackHandle` (ELM-06) | decided (O7) |
| **handle** | a pointer to a native object, never a number | `vlqt_scene*`, `SceneHandle` | also the number that routes events | decided (O7) |
| **occurrence** | one place where an element appears in a tree; the first is the primary, the others show a placeholder | `OccurrenceRegistry`, `PrimaryOf`, `IsPlaceholder` | "owner", "duplicate", "ignored", "waiting" (`SceneRegistry.cs:47`) | merged (REC-06) |
| **mirror** | the reconciler's record of what it sent for one occurrence | `ItemMirror`, `HandlerMirror`, `EffectChainMirror` | `Node`, `HandlerNode` | decided (REC-07, O3) |
| **feedback** | the way back from Qt to the node that made an element | `ElementFeedback`, `FeedbackSlot`, `FeedbackState`, `FeedbackRouter` | `ElementSink`, `SinkSlot`, `SinkState` (`ElementSink.cs:114`, `:350`, `:10`), `SinkRegistry` | decided (A1, O1) |
| **previous-frame layer** | the layer that keeps the previous frame of a scene while `Clear` is off; never called "feedback" | `PreviousFrameLayer`, owned by `ClearState` (NCMD-05) | `FeedbackLayer` (`feedback.h:40`), `Scene::releaseFeedback`, `restoreFeedback` | decided (O1) |
| **test export** | a function of the C ABI that only tests call | `vlqt_test_*` in `vlqt_test.h`, `test_*.cpp` | mixed into `vlqt.h` | decided (F1) |
| **testkit** | the native library with the stand-in D3D11 helpers of the SmokeTest, not packed | `vlqt_testkit` | `test_offscreen.cpp` in `vlqt.dll` | decided (F2) |
| **SmokeTest**, **help patch** | as written | — | "smoke test", "HowTo" | merged (DOC-10) |

### 1.3 Qt terms

One sentence each, in vvvv's words. The page of DOC-09 adds the link to the Qt documentation and the terms of the user pages (`qsb`, platform plugin, swap chain …).

| Qt term | Meaning |
|---|---|
| Qt Quick | Qt's retained 2D scene: a tree of items that Qt draws with the GPU. |
| QML | the language Qt Quick scenes are written in; QtNative creates its items from QML. |
| item (`QQuickItem`) | one object in a Qt Quick scene: Qt's counterpart of an element. |
| engine (`QQmlEngine`) | loads and runs QML; one per scene. |
| `QQuickWindow` | the window that draws a tree of items; an offscreen scene has one too, without a native window. |
| render control (`QQuickRenderControl`) | lets the application decide when and where a `QQuickWindow` renders: the base of offscreen scenes. |
| scene graph (`QSGNode`) | the render-side tree Qt builds from the items each frame; not the scene of 1.2. |
| render loop | how Qt schedules sync and render; QtNative runs `basic`, everything on one thread (E4). |
| QRhi | Qt's layer over Direct3D, Vulkan, Metal and OpenGL. |
| animation driver (`QAnimationDriver`) | the clock of all QML animations; QtNative replaces it with vvvv's time. |
| layer (`layer.enabled`) | an item drawn into a texture of its own first; what every effect needs. |
| MultiEffect | Qt's item that applies blur, shadow, colour changes and a mask to one layer in one pass. |
| ShaderEffect | Qt's item that draws a custom shader; QtNative has an item of its own for it. |
| pointer handler | a Qt object attached to an item that reacts to input: `TapHandler`, `DragHandler` … |
| Qt Quick Controls, Controls style | Qt's widgets, and a set of QML files that draws them; VL.Qt ships the style `VLQt`. |
| attached property | a property a QML type adds to other items; `VlStyle` reaches the widgets that way. |
| device pixel ratio | px per DIP of a screen. |
| windeployqt | Qt's tool that copies the Qt files an application needs next to it. |
| private API | Qt headers without a compatibility promise; allowed (D9). |

### 1.4 Open words

Each row: the clash, the ways, the recommendation. The first way is the recommended one.

**O1 feedback.** A1 names the way back from Qt "feedback" in C#. QtNative has the `FeedbackLayer` (`src/VL.Qt.Native/feedback.h:40`), which keeps the previous frame while `Clear` is off, and to a vvvv user "feedback" is frame feedback first.

- Decision: (a) (Sebastian, 2026-11-19). "Feedback" is the way back only, A1 stays. The layer is named for what it does: `PreviousFrameLayer` in `previous_frame_layer.h/.cpp`; `Scene::releaseFeedback`/`restoreFeedback` go into the `ClearState` of NCMD-05. User pages describe the pin: "with `Clear` off the previous frame stays". After A6 the feedback types are internal, so a user never reads the word.
- Not taken: (b) the layer keeps "feedback" and the way back becomes "event" (`ElementEvents`, `EventSlot`, `EventRouter`), because these objects mostly hold state, not events; (c) both keep the word with a qualifier (`FrameFeedbackLayer`), because a search for "feedback" would find both forever.

**O2 scene.** The C handle is `vlqt_scene` (A5). Behind it QtNative has three structs: `Window` (`vlqt.cpp:554`: engine, window, scene, perf), `Scene` (`commands.h:30`: the elements, commands and events) and `offscreen::State` (`offscreen.cpp:73`). NAPP-07 renames the first to `vlqt::Handle`, NDEV-04 the third to `vlqt::offscreen::Scene`, so QtNative would have two `Scene`. In C# RUN-06 proposes `Scene` (was `SceneCore`), `WindowScene`, `OffscreenScene`; REC-06 takes "scene" out of the registry (`OccurrenceRegistry`, `InteractionRegistry`).

- Decision: (a) (Sebastian, 2026-11-19). One pattern in both layers: `SceneHandle` is what a `vlqt_scene*` points to (C++ `vlqt::SceneHandle`, was `Window`; C# `SceneHandle`, CAPI-03); `Scene` is the shared part (C++ `vlqt::Scene` stays, C# `Scene`); the two kinds are `WindowScene` and `OffscreenScene` (C# as RUN-06; C++ `vlqt::OffscreenScene`, was `offscreen::State`). C++ has no struct of its own for the window half today: `Window` holds it. If NAPP-07 separates it, it is `vlqt::WindowScene`; otherwise the name stays unused in C++. VL.Qt.Stride keeps `SharedTextureScene`. The docs say "element tree" for the tree of the patch, never "scene".
- Not taken: (b) the names of the reports (`vlqt::Handle`, `vlqt::Scene`, `vlqt::offscreen::Scene`), which leave two `Scene` in QtNative; (c) `vlqt::Scene` for the struct behind the handle and `vlqt::ElementTable` for today's `Scene`, which changes every user of `Scene&`.

**O3 node.** Besides the vvvv node there are: `Node` and `HandlerNode` in the reconciler (`Node.cs:13`, `:131`), `Node` and `HandlerNode` in QtNative (`elements.h:114`, `events.h:211`), the helpers `ElementNode<,>`, `InteractionNode<>` and `EffectNode<>` that are not nodes (`ElementNode.cs:69`, `InteractionNode.cs:25`, `EffectNode.cs:13`), and Qt's scene-graph nodes (`feedback.cpp:39`).

- Decision: (a) (Sebastian, 2026-11-19). "Node" is a vvvv node in C# and a `QSGNode` subclass in C++. Reconciler: `ItemMirror`, `HandlerMirror` (REC-07). QtNative: `ElementEntry`, `HandlerEntry`, the entries of the scene's table. Helpers: `ElementNodeCore`, `InteractionNodeCore`, next to the existing `ShapeNodeCore`; `EffectNode<>` goes (NODX-05).
- Not taken: (b) the names of the reports, where QtNative keeps `Node` and `HandlerNode` (NELM, NCMD) and the helper stays `ElementNode<,>`.

**O4 layer.** A vvvv user knows "layer" from VL.Skia, where it is what VL.Qt calls an element. In Qt a layer is an item drawn into a texture (1.3). DOC-10 proposes "layer effects" for Blur, DropShadow, ColorAdjust, Colorize and Mask, and "shader effects" for the others; read with Skia's meaning, "layer effects" says nothing.

- Decision: (a) (Sebastian, 2026-11-19). "Layer" has Qt's meaning only and is defined once in the glossary, with the sentence that a Skia layer corresponds to an element. The five are the **merged effects** (they merge into one layer, as their summaries say after C11), the others the **shader effects**. The Blend pin `Layer` stays (catalog) and is the one named exception.
- Not taken: (b) "layer effects" and "shader effects" as DOC-10.

**O5 the names behind `Shader`.** C14 renames the node. The element class `ShaderEffect` (`src/VL.Qt/Elements/ShaderEffect.cs:12`), `ShaderEffectNode`, the protocol type and the native `ShaderEffectItem` (`shadereffectitem.h:68`) still carry the old word, and `ShaderLink` (`Effect.cs:131`) names an effect with vvvv's word for a connection.

- Decision: (a) (Sebastian, 2026-11-19). Code follows the node: `Shader`, `ShaderNode`, `ShaderMirror`, `VLQT_ELEMENT_TYPE_SHADER`, native `ShaderItem` in `shader_item.*` and `shader_material.*`. The effect that runs one shader is `ShaderEffect : Effect`, next to `BlurEffect` (was `ShaderLink`; `ShaderLinkBuilder` → `ShaderEffectBuilder`). "Link" stays only for one entry of a chain as sent: `EffectChainLink`, `links` in QtNative. The generated nodes of a `.frag` file are a *shader node* and, for a filter, a *shader effect node*; the types of the shader area follow (`ShaderLinkLayout` → `ShaderEffectLayout`, `ShaderLinkDescription`, `ShaderLinkInstance` alike). Accepted: the name `ShaderEffect` changes its meaning with 0.10.0, from the element to the effect; all these types are internal (A6).
- Not taken: (b) the chain keeps `ShaderLink` and "link node" (as SHD wrote it); (c) only the node is renamed and the code keeps `ShaderEffect*`.

**O6 frame.** Three meanings: a frame of time; the rectangle of an element in QtNative (`itemFrame`, the frame of a `GroupItem`, "mask frame", `debug.h:13`); the style group `Frame` (background and border of a widget: `FrameStyle`, `Qt.Style.Frame`). NDIAG-10 adds `PerfSample.frame`, which is an interval.

- Decision: (a) (Sebastian, 2026-11-19). "Frame" is time. The rectangle is **bounds** (`itemBounds`, group bounds), vvvv's word; the rename comes with NELM-01, which repairs these functions. The style group keeps `Frame`, because the catalog leaves `Qt.Style` unchanged; docs write "the Frame style". `PerfSample.frame` → `frameInterval` (NDIAG-10).
- Not taken: (b) QtNative keeps "frame" for the rectangle.

**O7 handle and id.** `handle` is the pointer of the C ABI, and also the number in BIND that routes events to a feedback slot (`sinkHandle`, `protocol.h:17`; `feedbackHandle` after ELM-06).

- Decision: (a) (Sebastian, 2026-11-19). A handle is a pointer, an id is a number: BIND and the event header carry `feedbackId`, C# reads `EventReader.FeedbackId`.
- Not taken: (b) `feedbackHandle` as ELM-06.

### 1.5 Words that stay overloaded

Tolerated, because the qualifier is always written and the meanings never meet in one file: **sample** (multisample count, `PerfSample`) next to **sampler** (shader); **slot** (`FeedbackSlot`, `ShaderSlot`, the table of the resource manager); **state** (`QtImageState`, `StyleState`, `FeedbackState`); **kind** (resource kind, `EffectKind`, `QmlValueKind`); **snapshot** (the node and the node-API snapshot); **entry** (besides `ElementEntry` and `HandlerEntry` also `NamedEntry`, `FontEntry`: each a record in a table).

### 1.6 Names the native break takes from this page

Step 3 of P4 renames "after the glossary of P3". Besides CAPI-03, NAPP-17, NELM-11, NDIAG-10 and PROTO-10 it takes:

- A5 through to the end: `vlqt_window_report_host_frame`, `_get_host_frame` → `vlqt_scene_report_host_pump`, `_get_host_pump` (CAPI-03 still writes `host_frame`); C# `HostPumpMeter` (RUN-05 writes `HostFrameMeter`).
- O1: `FeedbackLayer` → `PreviousFrameLayer`. O5: `VLQT_TYPE_SHADER_EFFECT` → `VLQT_ELEMENT_TYPE_SHADER`, `ShaderEffectItem` → `ShaderItem`. O7: `sinkHandle` → `feedbackId` in BIND and in the event header.
- Block B of [DECISION-ROUND.md](DECISION-ROUND.md): one result table `vlqt_result` (B1, B2); `vlqt_window_create` with the position (B3); `vlqt_app_get_abi_version`, `VLQT_ABI_VERSION` (B4); `BeginShaderLink` → `BeginEffectChainLink` (B8); C# `Protocol.ElementType` only (B9); shader element side `ShaderLayout`, `ShaderDescription`, `ShaderInstance` (B10).
- One suffix for export files: `<group>_exports.cpp` (NAPP-01), so `offscreen_api.cpp` of NDEV-04 is `offscreen_exports.cpp`.

## 2 Structure

### 2.1 Namespace rule

Decided (S1, Sebastian, 2026-11-19): namespaces follow the categories, folders follow the reader.

1. **A public type that is not a node sits in the namespace of its category.** With `[assembly: ImportAsIs(Namespace = "VL.Qt", Category = "Qt")]` (API-01) the type `VL.Qt.A.B.T` appears under `Qt.A.B`. Where a namespace cannot carry the category, the type says it with `[Category("…")]`, as the style types do today (`src/VL.Qt/Styles/Style.cs:9`).
2. **A folder directly under the project has one namespace, and its subfolders share it.** Subfolders are for finding files: `Nodes/Effects/` is `VL.Qt.Nodes`, `Qml/Parsing/` is `VL.Qt.Qml`, `Elements/Enums/` is `VL.Qt.Elements`.
3. **Nodes set their category with `Categories.*`** (as `src/VL.Qt/Renderer.cs:23` does), so the namespace of a node class is free; all node classes are `VL.Qt.Nodes`. NODX-11 is then: `Nodes/Paths/` moves from `VL.Qt.Nodes.Paths` (`AddArcNode.cs:6`) to `VL.Qt.Nodes`; `Effects/` and `Interaction/` stay.
4. **The root namespace `VL.Qt` holds the types of the category `Qt` and the runtime**, also in the folders `Runtime/`, `Scene/` and `Window/`. A namespace `VL.Qt.Scene` beside a class `Scene` would be a trap.

Not checked: that an explicit node category stays absolute once the assembly attribute has a `Category` (step 5 of API-01), and whether vvvv offers node classes as types under their namespace. The issue of API-01 checks both in vvvv; if one fails, rule 3 is raised again.

| Folder | Namespace | Category of its public types |
|---|---|---|
| `src/VL.Qt/` root, `Runtime/`, `Scene/`, `Window/` | `VL.Qt` | `Qt` |
| `Elements/` and subfolders | `VL.Qt.Elements` | `Qt.Elements`; `Sampler`, `Uniform` and the sampler enums `[Category("Qt.Elements.Shaders")]` |
| `Images/` | `VL.Qt.Images` (today `VL.Qt`, `QtImage.cs:4`) | `Qt.Images` |
| `Paths/` | `VL.Qt.Paths` | `Qt.Paths` |
| `Styles/` | `VL.Qt.Styles` | `[Category("Qt.Style")]`, as today |
| `Nodes/` and subfolders | `VL.Qt.Nodes` | by `Categories.*` |
| `Reconciliation/` | `VL.Qt.Reconciliation` (today `VL.Qt`, `Reconciler.cs:7`) | internal |
| `Wire/`, `Interop/`, `Qml/`, `Shaders/`, `NodeFactories/` | `VL.Qt.<Folder>` | internal |
| `src/VL.Qt.Stride/`, `src/VL.Qt.Audio/` | `VL.Qt.Stride`, `VL.Qt.Audio` | by `Categories.*` |

### 2.2 `src/VL.Qt`

```
src/VL.Qt/                                 VL.Qt
  Initialization.cs  Categories.cs  SpreadEquality.cs (NODX-06)
  Properties/AssemblyInfo.cs               the assembly attributes (API-01, RUN-13)
  Runtime/                                 VL.Qt                (RUN-01, -03, -05, -08)
    QtApp.cs  QtPump.cs  IQtFrameParticipant.cs  IQtPumpTarget.cs  HostPumpMeter.cs
    PumpDiagnostics.cs  QtLog.cs  ExportedApp.cs  NodeErrors.cs
  Scene/                                   VL.Qt                (RUN-06, O2)
    Scene.cs  WindowScene.cs  OffscreenScene.cs  OffscreenInput.cs  NotificationQueue.cs (COMP-14)
    ResyncController.cs (REC-04)  InputMapping.cs  Space.cs  SpaceMapping.cs  Geometry.cs
    MultisampleCount.cs  Multisample.cs
  Window/                                  VL.Qt                (RUN-04, RUN-10, NDIAG-11)
    WindowPlacement.cs  WindowGeometry.cs  BoundsSaver.cs  WindowSettings.cs
    PerfStats.cs  PerfPercentiles.cs  PerfCounters.cs  HostPerfStats.cs  GcPerfStats.cs
  Interop/                                 VL.Qt.Interop        (CAPI-07, after the break)
    QtNativeLibrary.cs  QtTreePathCheck.cs  SceneHandle.cs  ApplyError.cs  NativeConstants.cs (PROTO-06)
    Native.cs  Native.App.cs  .Scene  .Perf  .Window  .Offscreen  .Resources  .Images  .Shaders
    .Snapshots  .AudioTap  .Fonts
  Wire/                                    VL.Qt.Wire           (S3)
    Protocol.cs  CommandWriter.cs  ICommandSink.cs  CommandSinkExtensions.cs  EventReader.cs
    QmlEventDecoder.cs (was Elements/QmlEvents.cs, ELM-09)
  Reconciliation/                          VL.Qt.Reconciliation (REC-04 to -11, A3)
    Reconciler.cs  PassInfo.cs  Position.cs  ItemMirror.cs  EffectChainMirror.cs  ShaderMirror.cs
    HandlerMirror.cs  PrimaryOccurrences.cs  HandlerReconciler.cs  PropertyDiff.cs
    HandlerPropertyDiff.cs (ELM-09)  EffectChainWriter.cs  EffectPadding.cs  UniformWriter.cs
    UniformValue.cs  QmlPropertyWriter.cs  OccurrenceRegistry.cs  InteractionRegistry.cs
    FeedbackRouter.cs  DispatchResult.cs (ELM-06)  IdAllocator.cs
  Elements/                                VL.Qt.Elements       (ELM-01 to -13)
    IElement.cs  Element.cs  Container.cs  StylableElement.cs  (no `ElementType.cs`: `Protocol.ElementType`, B9 of DECISION-ROUND.md)
    Group.cs Row.cs Column.cs Rectangle.cs Text.cs Image.cs Video.cs Shape.cs
    Button.cs CheckBox.cs Slider.cs SpinBox.cs Switch.cs TextField.cs
    Enums/        one public enum per file; FillMode.cs, BlendMode.cs, LabelPosition.cs, ToggleLook.cs (catalog)
    Effects/      Effect.cs  ShaderEffect.cs (O5)  EffectChainLink.cs  EffectKind.cs  EffectDesc.cs
                  IEffectPadding.cs  BlurEffect.cs  DropShadowEffect.cs  ColorAdjustEffect.cs
                  ColorizeEffect.cs  MaskEffect.cs
    Interactions/ Interaction.cs  the seven interactions  DragLimits.cs  PointerButtons.cs
                  PointerDevices.cs  DragAxis.cs  IPointerFilter.cs  TouchPoint.cs
    Feedback/     ElementFeedback.cs  FeedbackState.cs  FeedbackSlot.cs
    Shaders/      Shader.cs (O5)  Uniform.cs  Sampler.cs  SamplerFilter.cs  SamplerMipFilter.cs  SamplerAddress.cs
    Qml/          QmlElement.cs  QmlSignal.cs  QmlProperties.cs  QmlValue.cs
  Styles/                                  VL.Qt.Styles         (SIP-01, -07 to -09)
    Style.cs  StyleState.cs  StateValues.cs  FrameStyle.cs  TextStyle.cs  IndicatorStyle.cs
    TrackStyle.cs  StyleMerge.cs  StyleWriter.cs  BaseStyle.cs  IStylable.cs
    StyleNodes.cs  FrameNodes.cs  TextNodes.cs  IndicatorNodes.cs  TrackNodes.cs
  Images/                                  VL.Qt.Images         (SIP-03 to -06, -13, NODW-03, -12)
    QtImage.cs  QtImageState.cs  QtResource.cs  IResourceApi.cs  ResourceClient.cs
    ByteArrayImage.cs  PixelFormats.cs  ImageFileWriter.cs  ResourceNodeReport.cs
    VideoFrameStream.cs  SnapshotGrab.cs
  Paths/                                   VL.Qt.Paths          (SIP-02, -11)
    QtPath.cs  FlatPath.cs  ArcGeometry.cs  BezierGeometry.cs  SplineGeometry.cs
    HandleMode.cs  PathAddMode.cs  PathDirection.cs
  Qml/                                     VL.Qt.Qml            (QML-01 to -12): the table of 11_qml.md section 4
    Parsing/      QmlLexer.cs  QmlHeadReader.cs  QmlDeclarationParser.cs  QmlLiteral.cs
  Shaders/                                 VL.Qt.Shaders        (SHD-01 to -16): the table of 12_shaders.md section 4
                                           plus ShaderSlot.cs (from Images/, SIP)
  NodeFactories/                           VL.Qt.NodeFactories  (D6: QML-03 = SHD-10)
    FolderNodeFactory.cs  FileNodeNames.cs  FileEditor.cs  PinNames.cs  FilePinDescription.cs  ValuePin.cs
  Nodes/                                   VL.Qt.Nodes
    RendererNode.cs  WindowSettingsNode.cs  LoadFontNode.cs              category Qt
    ElementNodeCore.cs  CommonInputs.cs  ChildrenInput.cs  ValueChannel.cs  ClickChannel.cs
    ChannelAuthors.cs  NodeMessage.cs  ChainMemo.cs  CommonPins.xml       shared
    Style/        SetColorsNode  SetFrameNode  SetTextNode  SetIndicatorNode  SetTrackNode  MergeNode (C6, C13)
    Elements/     the 16 element nodes, ShapeNodeCore.cs                  (NODE)
    Widgets/      ButtonNode  ToggleNode  SliderNode  InputIntNode  InputStringNode (NODW)
    Images/       LoadImageNode  LoadImageAsyncNode  ToQtImageNode  VideoSourceToQtImageNode
                  SnapshotNode  FileWriterNode  FileWriterElementNode
    Shaders/      ShaderNode.cs (O5)  ShaderTemplates.cs  UniformNodes.cs
    Qml/          QmlElementNode.cs  QmlFileWatcher.cs
    Effects/      the 12 effect nodes  ShaderEffectBuilder.cs (O5)       (NODX, SHD-09)
    Interaction/  InteractionNodeCore.cs  the 7 interaction nodes        (NODX)
    Paths/        the path nodes, one node class per file (K3)           (NODX)
    Editor/       ElementTooltipPreviewNode.cs  QtImageTooltipPreviewNode.cs
```

Deleted: `Native.cs` (split), `Reconciler/Node.cs`, `SceneRegistry.cs`, `EffectDiff.cs`, `Elements/Alignment.cs`, `ElementSink.cs`, `SinkRegistry.cs`, `Nodes/Effects/EffectNode.cs`, `BuiltinShaderLink.cs`, `Shaders/ShaderNodeLayout.cs`, `ShaderLinkNode.cs`; the enum `FontWeight` (catalog).

New in this page, in no report, and decided with S3: the folder `Wire/`, and the subfolders `Elements/`, `Widgets/`, `Images/`, `Shaders/`, `Qml/` under `Nodes/`. The folder of a node under `Nodes/` is the tail of its category; the three nodes of the category `Qt` and the shared helpers stay at the top. Exception: the static style nodes stay in `Styles/` beside their records (SIP); the six style process nodes of C6 are `SetColorsNode` … `MergeNode` in `Nodes/Style/` (C13 of [DECISION-ROUND.md](DECISION-ROUND.md)).

### 2.3 `src/VL.Qt.Native`

Namespace `vlqt`; sub-namespaces only where they exist today (`vlqt::device`, `vlqt::render_thread`, `vlqt::res`, `vlqt::offscreen`) and `vlqt::clock`, `vlqt::log`, `vlqt::platform` (NAPP-01). The groups below are folders, and file names are snake_case (S2). Includes name the folder (`"scene/commands.h"`). The four headers of the contract stay at the top.

```
src/VL.Qt.Native/
  vlqt.h  vlqt_test.h  protocol.h  wire.h         the contract (CAPI-03/04, PROTO-06/12, NCMD-06)
  CMakeLists.txt  CMakePresets.json  VL.Qt.Native.csproj                          (NDEV-08)
  app/        exports.h  last_error  log  app.cpp  animation_clock  render_gate  scene_change
              input_delivery.cpp  fonts  thread_name.h                             (NAPP-01 to -06)
  platform/   platform.h  platform_win.cpp  platform_linux.cpp                     (NAPP-08)
  window/     scene_handle.h (O2)  window  window_exports.cpp  window_filters  focus_release
              touch_input                                                          (NAPP-01, -07)
  scene/      scene  commands  clear_state  previous_frame_layer (O1)  event_queue  handlers
              measure  touch_handler (was touch.*)                                 (NCMD-05, -06)
  elements/   element_entry.h (O3)  element_types  elements  geometry  group_item  effect_sources.cpp
              type_rectangle  type_text  type_image  type_layout  type_video  type_controls
              type_shader  type_qml  type_shape  shape_path_element                (NELM-05, -07, -11)
  effects/    effect_chain  effect_chain_desc.h  effect_chain_item  layer_requests
              shader_item (O5)  shader_material                                    (NFX-05, -07)
  user_qml/   qml_item  qml_watcher  qml_wire                                      (NFX-20)
  style/      vl_style  VLQt/*.qml                                                 (NFX-08, -10, -22)
  resources/  resource_kind.h  resource_events  resource_workers  resource_manager
              resource_registry.cpp  resources  pixel_format  gpu_texture.h
              image_resource.h  image_kind.cpp  image_upload_d3d11  image_upload_rhi.cpp
              image_texture_cache  qt_image_item  image_writer  snapshot_resource
              shader_resource  shader_bake  audio_tap                              (NRES-02 to -08, NFX-12, COMP-02)
  device/     device  context_lock  render_thread  renderer_info                  (NDEV-03, -11, NDIAG-08)
  offscreen/  offscreen.h  offscreen_scene  offscreen_binding.cpp  offscreen_render.cpp
              offscreen_input.cpp  offscreen_exports.cpp  linear_pass              (NDEV-04, -05)
  diagnostics/ perf.h  perf_stats  perf_monitor  perf_pages  perf_items  perf_exports.cpp
              overlay_software.h  overlay_geometry.h  debug_overlay                (NDIAG-04, -05)
  test/       test_input.cpp  test_probes.cpp  test_device  test_resource_kind.cpp (F1)
  testkit/    testkit.cpp, target vlqt_testkit, not packed                         (F2, NDEV-09)
  qml/        elements/*.qml  effects/*.qml  handlers/*.qml  perf/perf_overlay.qml  deploy.qml   (D8, NELM-06, NFX-06)
              QML type files keep PascalCase (`MultiStage.qml`), the one exception of snake_case (C16)
  shaders/*.frag  fonts/  icon/  tools/glprobe.c (Linux, NAPP-12)
```

Deleted: `vlqt.cpp`, `offscreen.cpp`, `events.*`, `feedback.*`, `perf.cpp`, `pathelement.*`, `test_offscreen.cpp` (all split or moved); `gradient.frag` and `wave.frag` move to the SmokeTest (NFX-16).

### 2.4 `src/VL.Qt.Stride` and `src/VL.Qt.Audio`

As 22_stride-audio.md section 4, flat, no subfolders.

- `src/VL.Qt.Stride/`: `QtTextureNode.cs`, `QtRendererNode.cs`, `ToTexturePixelsNode.cs` (public nodes); `TexturePixelMapping.cs`, `SharedTextureScene.cs`, `QtTextureRenderer.cs`, `StrideInterop.cs`, `TextureFormats.cs`, `LayerComposition.cs` (internal); `Properties/AssemblyInfo.cs`. `NotificationQueue.cs` moves to `src/VL.Qt/Scene/`.
- `src/VL.Qt.Audio/`: `VideoAudioNode.cs` (public node); `VideoAudioSignal.cs`, `AudioTapPlayer.cs`, `IAudioTapReader.cs`, `NativeAudioTapReader.cs` (internal); `Properties/AssemblyInfo.cs`.
- Both reach `VL.Qt` through `InternalsVisibleTo` with a documented seam (D7).

### 2.5 Tests and docs

Not part of the four projects; the trees are in 27_tests.md, 24_docs-pages.md and 25_docs-design-notes.md, section 4. What this page fixes for them: the C# test hooks live in `src/VL.Qt.TestSupport/` (`NativeTest.cs`, `TestProtocol.cs`), linked into both test projects; design notes are in `docs/design/` (G5); this glossary is `docs/glossary.md`.

### 2.6 Public surface

- **C ABI:** `vlqt.h`, about 67 exports in the groups app, scene, window, offscreen, res, image, shader, snapshot, audio tap, font; `vlqt_test.h`, about 31 test exports in every build (F1). Nothing else leaves the library. `vlqt_testkit` is not packed (F2).
- **VL.Qt, nodes:** the classes in `Nodes/`, and the static node classes `StyleNodes`, `FrameNodes`, `TextNodes`, `IndicatorNodes`, `TrackNodes`, `UniformNodes`.
- **VL.Qt, pin types:** `IElement`, `Effect`, `Interaction`, `Style`, `IStylable`, `QtImage`, `QtPath`, `Sampler`, `Uniform`, `TouchPoint`, `QmlSignal`, `WindowSettings`, `PerfStats` and its four groups.
- **VL.Qt, enums:** `Space`, `MultisampleCount`, `FillMode`, `TextWrap`, `TextElide`, `TextAutofit`, `TextHorizontalAlignment`, `HorizontalAlignment`, `VerticalAlignment`, `SliderOrientation`, `ShapeCap`, `ShapeJoin`, `ShapeFillRule`, `BlendMode`, `LabelPosition`, `ToggleLook`, `PointerButtons`, `PointerDevices`, `DragAxis`, `SamplerFilter`, `SamplerMipFilter`, `SamplerAddress`, `StyleState`, `StyleStates`, `QtImageState`, `HandleMode`, `PathAddMode`, `PathDirection`.
- **VL.Qt, other:** `Initialization` (Hidden).
- **VL.Qt.Stride:** `QtTextureNode`, `QtRendererNode`, `ToTexturePixelsNode`. **VL.Qt.Audio:** `VideoAudioNode`.
- **Internal:** everything else, among it `Element`, `Container`, the sixteen element classes, the concrete effects and interactions, the feedback types, `ElementType`, `Geometry`.

The node-API snapshot (P6) is the test of this list.

### 2.7 Where the reports disagree, and what this page takes

| Topic | The reports | Taken | Reason |
|---|---|---|---|
| `Element`, `Container` | public (ELM section 4, API section 4) | internal | C3, A6 |
| Namespace of `Images/` | `VL.Qt` (SIP); category `Qt.Images` (catalog) | `VL.Qt.Images` | rule 1. The catalog row "all imported types" lists `VL.Qt.Images` as a current category; today it is `VL.Qt` (`QtImage.cs:4`) |
| `Sampler`, `Uniform`, sampler enums | `VL.Qt.Elements` (ELM); category `Qt.Elements.Shaders` (catalog) | namespace `VL.Qt.Elements`, `[Category]` | rule 1 and 2 |
| `BlendMode`, `LabelPosition`, `ToggleLook` | stay in `Nodes/` (NODX, NODE, NODW) | `Elements/Enums/` | catalog |
| `ImageFillMode`, `VideoFillMode`, `FontWeight` | listed (ELM) | `FillMode`; `FontWeight` goes | catalog |
| Namespace of the node folders | `VL.Qt.Nodes.Effects` … (NODX-11); `VL.Qt.Nodes` (NODE, NODW) | `VL.Qt.Nodes` | rule 3 (S1) |
| `ICommandSink.cs` | next to `CommandWriter.cs` (REC); stays in `Reconciler/` (PROTO) | `Wire/` | it is the wire contract |
| `QmlEvents.cs` | next to `EventReader.cs` (ELM-09); unchanged in `Elements/` (QML) | `Wire/QmlEventDecoder.cs` | A3: the element model knows no protocol |
| `BuiltinShaderLink.cs` | stays (NODX); → `ShaderLinkBuilder.cs` (SHD-09) | `Nodes/Effects/ShaderEffectBuilder.cs` (SHD-09, O5) | the shader area owns it |
| Error reporting of resource nodes | `ImageNodeReport` (SIP), `ResourceNodeReport` (NODW-04), `NodeMessage` (NODE-05), `NodeErrors` (RUN-01) | `NodeErrors.cs` is the store, `NodeMessage.cs` the handle, `ResourceNodeReport.cs` uses it | A1 of [DECISION-ROUND.md](DECISION-ROUND.md) |
| Perf records in C# | `PerfStats.cs` + `PerfDetails.cs` with four records (NDIAG-11) | one file per record | K3 |
| C# test hooks | `src/VL.Qt.TestSupport/NativeTest.cs` (CAPI-04), `NativeTestHooks.cs` (TEST), `SmokeTest/TestProtocol.cs` (PROTO-06) | `src/VL.Qt.TestSupport/` | one place for both test projects |
| Native test files | `test_input.cpp`, `test_probes.cpp` (NAPP-09); `device_testing.*` (NDEV-03); `resource_test_kind.cpp` (NRES-09); `testhooks.cpp` (TEST-08) | `test_*.cpp` | F1 |
| `wire.h` | created by PROTO-12 and by NCMD-06 | one file | one issue (3.2) |
| `fonts.h/.cpp` | font registry (NAPP-01); missing-family warning (NELM-05) | one file | same subject |
| Effect chain code in `elements.cpp` | `effectchain.cpp` or `elements/effect_layer.cpp` (NELM-05); `effectchain.*` reshaped (NFX-07) | `effects/effect_chain.*` | the effects area owns it |
| Embedded QML | `qml/elements/` (NELM-06), `effectchain/*.qml` (NFX-06), `perf_overlay.qml` (NDIAG-04) | all under `qml/` | D8: one resource folder |
| `typeName` of an element type | new function in `elements.h` (NDIAG-03); `TypeDef::name` (NELM-04) | `TypeDef::name` | one table of types |
| Renderer description | `renderer_info.*` or `device.cpp` (NDIAG-08) | `device/renderer_info.*` | `device.cpp` is cut smaller, not larger |
| `linear_pass.*` | effects (today), QtTexture (NFX section 4) | `offscreen/` | its only user is `offscreen.cpp` |
| Layout of `src/VL.Qt.Native` | subfolder `elements/` (NELM); flat (NAPP, NDEV, NRES, NDIAG) | one folder per area | S2: merged, the layer has about 150 files |
| File names in `src/VL.Qt.Native` | snake_case (`offscreen_scene`, `perf_stats`) and joined (`clearstate`, `groupitem`, `layerrequests`) | snake_case | S2 |

### 2.8 Open structure questions

**S1 the namespace rule of 2.1.** Decision: (a) (Sebastian, 2026-11-19), the rule as written: namespaces follow the categories, subfolders add nothing, all nodes in `VL.Qt.Nodes`. This answers NODX-11: `Nodes/Paths/` moves to `VL.Qt.Nodes`. Not taken: (b) namespace = folder path, strictly, because `Elements/` could then have no subfolders without sub-categories in the node browser; (c) one rule per area, as the reports wrote it.

**S2 the layout and file names of QtNative.** The reports looked at 10 to 25 files each and four of six stay flat. Merged, the folder holds about 150 source files (counted in 2.3) instead of 51 today. File names are mixed today (`shader_resource.cpp`, `shadereffectitem.cpp`), and so are the proposed ones (`offscreen_scene`, `clearstate`). Decision: (a) (Sebastian, 2026-11-19), the folders of 2.3 and snake_case names; includes become `"scene/commands.h"`. A folder is created by the cut that fills it, not in a step of its own, so that no file moves twice. Seven existing files change their name (`shadereffectitem`, `effectchain`, `qtimageitem`, `qmlitem`, `pathelement`, `vlstyle`, `threadname`), five of them in a cut that happens anyway. Not taken: (b) flat with the group as prefix (`offscreen_*`, `perf_*`, `resource_*`), as most reports wrote it; (c) flat with Qt's joined lower case (`shadereffectitem`).

**S3 two folders that no report proposes.** `Wire/` for `Protocol.cs`, `CommandWriter.cs`, `ICommandSink.cs`, `EventReader.cs` (PROTO leaves them in the root), and one subfolder per category under `Nodes/`. Decision: (a) both (Sebastian, 2026-11-19). `Wire/` makes the boundary of A3 visible: a `using VL.Qt.Wire` in `Elements/` stands out. The node files move in one mechanical step of wave 3, about 35 files without a change of content, before the node issues start; later the move would collide with every open node branch. Not taken: (b) only `Wire/`; (c) neither.

## 3 File overlaps

Computed from the `Touches` lines of all 340 findings; `*` marks a finding of kind structure or naming, the ones that cut, move or rename. Not checked against the code, and not yet corrected for the findings that a decision overturned (P1).

### 3.1 Files that many findings touch

| File | Findings | Cut or moved by | Other areas that edit it |
|---|---|---|---|
| `vlqt.cpp` | 38 | NAPP-01 (the file dissolves) | CAPI-01 to -06, NCMD-04/-05/-15, NDEV-03/-04/-11, NRES-05/-09, NDIAG-04/-07 to -10/-14, PROTO-01, RUN-02, TEST-08 |
| `offscreen.cpp` | 27 | NDEV-04 (five files) | NAPP-01/-02/-03/-07/-10/-15, CAPI-01 to -03, NRES-01/-07, NFX-11, NDIAG-06, RUN-02, TEST-08 |
| `commands.cpp` | 26 | NCMD-05 (decoder, scene, clear state, event queue) | PROTO-02/-08/-09/-10/-12, REC-13, NELM-02/-04/-09/-15, NFX-07/-22, NDIAG-09 |
| `vlqt.h` | 24 | CAPI-03/-04 (wave 3) | NAPP, NCMD-04, NRES-09/-12, NFX-13, NDIAG-01/-07/-10/-14/-16, RUN-02 |
| `CMakeLists.txt` | 24 | NDEV-08 (one source per line) | every native cut adds or removes lines |
| `Native.cs` | 23 | CAPI-07 (after the break) | PROTO-01/-06, SIP-05, NAPP-03/-09/-17, NCMD-04, NDEV-09, NRES-09, NDIAG-01/-07/-10/-14/-16, RUN-02, TEST-08 |
| `protocol.h`, `Protocol.cs` | 21, 20 | PROTO-06/-10 (wave 3) | REC-01/-13, ELM-06/-08/-09, CAPI-02/-03, SIP-09, NAPP-03, NCMD-08, NELM-11, NDEV-12, NRES-15, COMP-12 |
| `perf.cpp` | 18 | NDIAG-04 (five files) | CAPI-05, NAPP-06, NCMD-05, NELM-04/-05/-11, TEST-08 |
| `elements.cpp`, `elements.h` | 18, 13 | NELM-05 (the `elements/` group) | NCMD-03/-05/-12/-14/-17, NFX-07, PROTO-07, NDIAG-03 |
| `shadereffectitem.cpp` | 15 | NFX-05 (item and material) | NELM-05, NRES-01/-05/-06/-07/-11 |
| `Reconciler.cs` | 15 | REC-04 (the whole folder), REC-06, REC-07 | ELM-03/-07, RUN-06, SIP-09, DOCD-07, TEST-07 |
| `Renderer.cs` | 15 | RUN-06 (→ `Nodes/RendererNode.cs`) | REC-06, PROTO-06, CAPI-03, NAPP-03, API-01, NDIAG-07/-11 |
| `SceneCore.cs` | 14 | RUN-06 (→ `Scene/Scene.cs`) | REC-01/-02/-04/-06/-14, ELM-06, PROTO-07, CAPI-02/-03/-10, NAPP-03, NCMD-04, TEST-07 |
| `events.cpp` | 14 | NCMD-05 (handlers, measure) | PROTO-02/-09 to -12 |
| `test_offscreen.cpp` | 13 | NDEV-09 (→ testkit) | CAPI-01 to -04, NAPP-01/-02/-03/-07/-09 |
| `WindowRenderer.cs`, `OffscreenRenderer.cs` | 11, 8 | RUN-06 | REC-04/-06, CAPI-02/-03, NAPP-03, NDIAG-10/-11/-15, COMP-07, TEST-07 |
| `device.cpp` | 10 | NDEV-03, NDEV-11 | NAPP-08, NRES-07, NDIAG-08, TEST-08 |
| `ResourceClient.cs` | 10 | SIP-06 (rename), SIP-13 | RUN-03, PROTO-06, CAPI-02/-03 |
| `QtApp.cs` | 9 | RUN-05 | PROTO-01, CAPI-03, NDIAG-10, TEST-07 |
| `resources.cpp` | 9 | NRES-03 | PROTO-02/-10 |
| `effectchain.cpp` | 9 | NFX-06, NFX-07 | PROTO-10, NCMD-03/-05, NELM-05 |
| `SnapshotNode.cs`, `FileWriterElementNode.cs` | 8, 8 | NODW-12 | REC-04/-06, SIP-06 |
| `SinkRegistry.cs`, `ElementSink.cs` | 8, 4 | A1 (wave 3), then → `Reconciliation/`, `Feedback/` | REC-04, PROTO-09, DOCD-02 |
| `ElementNode.cs` | 7 | NODE-01, NODE-08 | ELM-07, NODW-02 |
| `Help.xml` | 6 | — | RUN-10, NODW-05/-08, NODX-02, API-07/-12 (P19, P20) |

`OffscreenRenderer.cs` is read as binary by search tools (09_c-api.md section 6), so searches miss it until the stray byte is removed.

### 3.2 One file or one type from two findings

To add to the duplicates of P22:

- `wire.h`: PROTO-12 and NCMD-06 create it. One issue.
- `fonts.h/.cpp`: NAPP-01 moves the font registry there, NELM-05 the missing-family warning. The second one adds to the file of the first.
- `vlqt_test.h`: CAPI-04 = NAPP-09 = TEST-08 (P22) create it; PROTO-06 moves the test constants of `protocol.h` into it. Same issue.
- The handle struct: NAPP-07 names it, NDEV-04 changes its offscreen half, CAPI-03 types the C side. All three use the names of O2.
- Messages at a node: NODE-05 (`NodeMessage`), RUN-01 (`NodeErrors`), NODW-04 (`ResourceNodeReport`), SHD section 6 (three reporting styles). One mechanism, one issue, written from "No `Error` output pins".
- `EventQueue`: NCMD-05 makes `vlqt::EventQueue`, NRES-03 `vlqt::res::EventQueue`. Two types with one name; the second becomes `ResourceEventQueue`.
- `SameContent` for spreads: NODX-06 (`SpreadEquality`) also replaces the copies in `PropertyDiff.cs` and `ShapeNodeCore.cs`.

### 3.3 What may run in parallel in wave 4

Three kinds of issue, in this order.

**1 Cuts, as pure moves.** A cut moves code into new files and changes no behaviour, so that its review is "nothing changed" and every later issue starts in the new files. A native cut puts its files into the folder of 2.3 with their final names at once (S2). `CMakeLists.txt` first (NDEV-08), alone, because every native cut edits it. Then:

| Group | Cuts | Why they do not meet |
|---|---|---|
| native, first round | NAPP-01 (`vlqt.cpp`), NCMD-05 (`commands.*`, `events.*`, `feedback.*`), NRES-03 (`resources.*`), NFX-05 (`shadereffectitem.*`) | one area each; they meet in `CMakeLists.txt` and in the include lines that NCMD-05 edits in `vlqt.cpp`, `elements.cpp`, `perf.cpp`, `debug.cpp` and `effectchain.cpp` |
| native, second round | NDIAG-04 (`perf.cpp`, after NAPP-01: both move the perf exports out of `vlqt.cpp`), NDEV-04 (`offscreen.cpp`, after NAPP-01: both own halves of `offscreen.h`), NELM-05 (`elements.*`, after NCMD-05 and NFX-05: it also edits `shadereffectitem.cpp`), NFX-07 (`effectchain.*`, after NELM-05) | each waits for the cut named |
| C#, parallel to native | REC-04 (`Reconciler/`), RUN-05 with RUN-06 (`QtApp.cs`, the scene types), CAPI-07 (`Native.cs`), ELM file splits, QML-02, QML-03 = SHD-10 (`NodeFactories/`), SIP file splits | one folder each; RUN-06 renames what REC-04 references, so RUN-06 goes first |

**2 Issues that cross every node file, one after the other.** API-01 (types internal, the assembly attribute) → C3 with P5 (`IElement` on every pin) → NODE-01 (`CommonInputs`) → C2 (`Enabled` on every element node and on the generated nodes). They change the signatures that all node issues build on, and they run while the cuts run, since the cuts do not touch `Nodes/`.

**3 Fixes per lane.** Inside a lane one issue after the other; lanes in parallel.

| Lane | Files | Findings | Crossings into the lane |
|---|---|---|---|
| N-app | `app/`, `window/`, `platform/` | NAPP | NDIAG-07/-09, NCMD-04/-15 |
| N-scene | `scene/` | NCMD | NELM-02/-04/-09/-15, NFX-22 |
| N-elements | `elements/` | NELM | NCMD-03/-12/-14/-17 |
| N-effects | `effects/`, `user_qml/`, `style/` | NFX | NRES-01/-05/-06/-07/-11 (the shader item), NELM-06 (QML files) |
| N-resources | `resources/` | NRES, COMP-02/-03 | NDEV-06, NFX-09/-12/-13 |
| N-device | `device/`, `offscreen/` | NDEV | NRES-01/-07, NFX-11, NDIAG-06, NAPP-10/-15 |
| N-diagnostics | `diagnostics/` | NDIAG | NELM-04/-11 |
| C-reconciler | `Reconciliation/`, `Scene/` | REC, ELM-03/-07 | SHD-01, SIP-09 |
| C-runtime | `Runtime/`, `Window/` | RUN, NDIAG-10/-11 (C# half) | — |
| C-elements | `Elements/` | ELM | NODE section 6 |
| C-nodes | `Nodes/` root, `Elements/`, `Widgets/`, `Images/` | NODE, NODW | SIP-06, REC-06 |
| C-chains | `Nodes/Effects/`, `Interaction/`, `Paths/` | NODX | SHD-09 |
| C-files | `Qml/`, `Shaders/`, `NodeFactories/` | QML, SHD | — |
| C-values | `Styles/`, `Images/`, `Paths/` | SIP | NODW-03/-12 |
| companions | `src/VL.Qt.Stride`, `src/VL.Qt.Audio` | COMP | RUN-02/-06 |

A crossing finding is worked in the lane of the file it edits, not in the lane of its report. The lanes N-effects and N-resources share the shader item, and C-nodes and C-values share `Images/`: these two pairs run one after the other where the table names a crossing.

Outside the lanes: help patches and `Help.xml` (P19, P20), the protocol after the break (additions only: C2, `FillMode`, B5, NRES-15, COMP-12, each raises the version), tests and docs (waves 5 and 6).

## 4 The ten questions

All decided on 2026-11-19; nothing on this page is open.

| # | Question | Recommended |
|---|---|---|
| O1 | "feedback": the way back from Qt, or the layer of `Clear` off | decided: (a) the way back; `PreviousFrameLayer` |
| O2 | "scene": the native names behind `vlqt_scene` | decided: (a) `SceneHandle`, `Scene`, `WindowScene`, `OffscreenScene` in both layers |
| O3 | "node": the mirrors, the native records, the helpers | decided: (a) `ItemMirror`, `ElementEntry`, `ElementNodeCore` |
| O4 | "layer": the name of the two effect groups | decided: (a) merged effects, shader effects |
| O5 | the code names behind `Shader` | decided: (a) `Shader`, `ShaderItem`; `ShaderEffect` for the effect |
| O6 | "frame" for time only | decided: (a) bounds for the rectangle |
| O7 | handle and id | decided: (a) `feedbackId` |
| S1 | the namespace rule | decided: (a) as 2.1 |
| S2 | layout and file names of QtNative | decided: (a) folders, snake_case |
| S3 | `Wire/` and the subfolders of `Nodes/` | decided: (a) both |
