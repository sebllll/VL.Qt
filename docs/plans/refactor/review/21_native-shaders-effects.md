# Review 21 – QtNative: shaders, effect chain, user QML item, style

Reviewer: Claude Opus 5.5 (claude-opus-5-5), 2026-11-19.
Files read in full: `src/VL.Qt.Native/shader_resource.h/.cpp`, `shadereffectitem.h/.cpp`, `effectchain.h/.cpp`, `linear_pass.h/.cpp`, `qmlitem.h/.cpp`, `vlstyle.h/.cpp`, `docs/effects-and-shaders.md`, `style/VLQt/qmldir`, `shaders/pyramid_up.frag`.
Skimmed: `style/VLQt/SpinBox.qml` (lines 1–60), `shaders/glow_composite.frag` (lines 1–30), targeted excerpts of `CMakeLists.txt`, `vlqt.cpp`, `elements.cpp`, `qtimageitem.h`, `docs/architecture.md`, `src/VL.Qt/Reconciler/EffectDiff.cs`, test file names in `src/VL.Qt.Tests` and SmokeTest scenario names.
Not read: the other eleven shaders in `shaders/`, the other five style files (`Button`, `CheckBox`, `Slider`, `Switch`, `TextField`.qml).

## 1 Summary

The area works and is carefully reasoned. Three things stand out: shader edits never go black because the last good version keeps drawing; uniforms are matched by name through reflection; and the effect chain rewrites only values when its structure is unchanged. The problems are mostly about maintenance. The large files mix the item, the material, a global layer registry and inline QML. Design notes sit in headers and point to documents the community cannot read ("doc 21", "plan effects/08"). Some contracts are duplicated (Qt's default shader lookup, reserved names, style field ids in QML). Real defects are few: a mip request is lost when two samplers read the same element, and some GUI-side deletes are safe only because QtNative runs Qt's `basic` render loop. A value change in the effect chain also breaks the "no allocation per frame" rule: every re-send of the chain allocates and repaints every pass.

The changes that matter most:
1. Make the same-structure path of `EffectChain::apply` allocation-free and update only on change (NFX-01).
2. Fix the layer request merge for several samplers on one element (NFX-02).
3. Split `shadereffectitem.cpp` and `effectchain.cpp` into item, render side, registry and QML files, and give `EffectChain::Link` one struct per kind (NFX-05, NFX-06, NFX-07).
4. Move the design notes into `docs/` and replace the private doc references (NFX-04).
5. Make the few places that rely on the basic render loop independent of it, or state the dependency in one place (NFX-03).

## 2 What works and must survive

**The threading rule, once.** In Qt Quick an item (`QQuickItem`, the GUI-side object) and its scene-graph node (`QSGNode`, with its material and material shader, the GPU-side objects) live on different sides. The item's state may only be changed on the GUI thread. The node and the material may only be touched in `updatePaintNode` (the "sync": the GUI side is blocked and the item copies its state into the node), in `preprocess` and in the material shader during rendering. QtNative runs Qt's `basic` render loop (`vlqt.cpp:1047`, `docs/architecture.md:48`): GUI work, sync and render run one after another on QtNative's own thread. The area follows the threaded rule anyway, which is right. Only the places named in NFX-03 depend on the basic loop.

Keep:
- **Last good shader** (`ShaderEffectItem::ShaderSlot` want/shown/content, `shadereffectitem.cpp:454-555`): a failed or still-baking shader leaves the last Ready one drawing; uniforms the new shader may declare are not reported while it bakes (`:846-850`).
- **Bake in the resource worker, one at a time** (`s_bakeMutex`, `shader_resource.cpp:276`): glslang is not documented as thread safe. **Cache** per stage + backend + SHA-1 of the source, errors included (`:223-272`). **Targets** only for the process backend (`:180-199`).
- **Reflection-driven layout, rebuilt only on structural change** (`rebuild`, `shadereffectitem.cpp:704`). `updatePaintNode` copies values into a buffer that is sized only when the shader changes (`:1037-1044`). This is the hot path: no allocation per frame.
- **One `QSGMaterialType` per shader pair, never freed** (`shadereffectitem.cpp:119-128`). The batch renderer caches material shaders by the address of the type. A freed type whose address is reused would pick up a stale shader (see NFX-17).
- **`SamplerView`** (`:133-162`): sampler state per draw without touching the shared texture of a layer or an image.
- **Pass-through of a chain pass** while its shader is not Ready (`:726-727`): a chain never goes black while a shader bakes.
- **`preprocess` updates dynamic textures and then `texelSize`** (`:284-290`): a layer resized in this frame gives the right texel size.
- **The direct `destroyed` → `update()` connection of a texture provider** (`:905`). It is load-bearing: in the basic loop the provider dies on the GUI thread, and `update()` makes the next sync drop the dead texture before anything renders.
- **Effect chain:** same structure → values only, no new item, no shader switch (`effectchain.cpp:384-407`). MultiEffect values are written only on change, because `blurEnabled`, `shadowEnabled` and `blurMax` switch its shaders (`:541-545`). Retired stages are hidden and deleted after a frame. `sourceRect` is always written through `layer.sourceRect`, never on the ShaderEffectSource (reason at `:118-119`). Pyramid sizes follow only the stage size and dpr; the radius writes only weights (`:677-768`).
- **`linear_pass::run`** sets every state it depends on and unbinds the source view afterwards (`linear_pass.cpp:148-170`). The context is shared with Qt's QRhi.
- **QmlItem:** the patch's own set is not echoed back (`qmlitem.cpp:555-561`). The instance has C++ ownership and is deleted one frame after a reload. Properties are applied again after a reload. The canonical path avoids Qt's case-mismatch error.
- **VlStyle:** signals fire only on a real change. `SET_STYLE` is validated completely before the table is touched (`vlstyle.cpp:311-388`). C# resolves the inheritance.
- **Hot paths:** `ShaderEffectItem::updatePaintNode`, `EffectShader::updateUniformData`/`updateSampledImage`, `EffectNode::preprocess`, `ShaderEffectItem::afterResourceFrame` (returns early on an unchanged epoch), `VlStyle::setState` (every hover), `QmlWatcher::onProperty`/`onSignal`, `linear_pass::run`, and `EffectChain::apply` on the same structure (NFX-01).

## 3 Findings

### NFX-01 Same-structure effect chain updates: no allocation, no repaint without a change
- **Kind:** performance
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/effectchain.cpp:328`, `:334-335`, `:361`, `:585`, `:597`, `:770-797`; `src/VL.Qt.Native/shadereffectitem.cpp:624`, `:692-702`
- **Finding:** C# re-sends the whole chain whenever the chain object differs (`src/VL.Qt/Reconciler/EffectDiff.cs:26`). A value missing from a send goes back to its default (`effectchain.cpp:173`), so an animated pin such as a Blur radius or a shader uniform reaches `apply` every frame. Every such call does the following:
  - allocates a fresh `std::vector<Link>`, each element with 23 `QVariant`s, a `QStringList` and a uniform vector (`:328`);
  - builds a `QString` per uniform even when it has no problem (`:361`);
  - builds a two-element `std::vector<QByteArray>` per pass (`:585`);
  - rewrites `elementRect` with `beginUniforms`/`endUniforms`, which calls `update()` unconditionally (`:597`, `shadereffectitem.cpp:701`);
  - rebuilds the error text with `QString::arg`, splits each pass error and joins (`:770-797`).

  Each SAMPLER entry also sets `m_layersDirty` even when the state is unchanged (`shadereffectitem.cpp:624`), so `refreshLayerRequests` allocates its `now` vector on every send. AGENTS.md asks for no allocation per frame in hot paths.
- **Proposal:**
  - Keep a member `std::vector<Link> m_scratch` whose capacity survives between calls, and parse the desc into it.
  - Check reserved names on the `QByteArray`; build the problem `QString` only when there is a problem.
  - Make the optional-name lists `static const std::vector<QByteArray>`.
  - Let `ShaderEffectItem::setUniform` return `bool changed`, and let `endUniforms` call `update()` only if a value or the structure changed.
  - Store the last written `elementRect` in `Link` and write it only on change.
  - Set `m_layersDirty` in the SAMPLER branch only when the state differs (`:621`), and reuse a member vector in `refreshLayerRequests`.
  - Call `updateError()` only when `problems`/`shaderError` changed, or from the pass `errorChanged` signal that already exists (`:476`).
- **Decided:** O5, S2 → `ShaderEffectItem` is `ShaderItem` (`effects/shader_item.*`) and `effectchain.*` is `effects/effect_chain.*`; `Link` stays the name of one entry of a chain
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/effectchain.h`, `effectchain.cpp`, `shadereffectitem.h`, `shadereffectitem.cpp`
- **Depends on:** NFX-07 (easier after it, not required)
- **Verification:** SmokeTest `effect-chain`, `effect-passes`, `effect-glow`, `effect-innershadow` (pixels unchanged); a `docs/profiling.md` capture of `HowTo Chain Effects` with an animated uniform shows no allocations in `EffectChain::apply`.

### NFX-02 Several samplers on one element: merge the mip requests before sending them
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no (fixes a wrong render)
- **Where:** `src/VL.Qt.Native/shadereffectitem.cpp:930-952`, `:82-94`
- **Finding:** `refreshLayerRequests` calls `requestLayer(item, this, mip)` once per sampler, and `requestLayer` overwrites the single entry of this owner (`u->mip = mip`, `:92`). Suppose two samplers of one ShaderEffect read the same element, the lower binding with a mip filter and the higher without one. On the first refresh the second sampler is skipped (`:943`, the element is not yet in `m_layerItems`), so mipmaps turn on. On the next refresh the element is in `m_layerItems`, the second call writes `false` last, and mipmaps turn off: the mip filter is silently lost. `now` also collects the element twice.
- **Proposal:** In `refreshLayerRequests`, first build a small list of `(QQuickItem*, bool mip)` with the mip flags ORed per item, then call `requestLayer` once per item. Keep the rule that an item with only defaults gets no request unless it already holds one.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/shadereffectitem.cpp`
- **Depends on:** none
- **Verification:** new SmokeTest scenario `sampler-mip-shared` (one element read by two samplers, mip on the lower binding only, two frames, mip level visible in a minified read); existing `shader-effect`, `qtshaders`.

### NFX-03 Make the GUI-side texture delete independent of the basic render loop
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shadereffectitem.cpp:425`, `:980-991`, `:1016-1020`, `:903-905`; `shadereffectitem.h:36`
- **Finding:** The 1×1 transparent `QSGTexture` is created in `updatePaintNode` but deleted in the destructor and in `releaseResources`, both on the GUI side. At that point the node's material still points to it (`mat.tex`, `:1053`). This is safe only because the `basic` loop never renders between that delete and the next sync. Under a threaded loop it would be a use-after-free, and with OpenGL a texture deleted without a current context. The provider `destroyed` connection (`:905`) is also correct only for the basic loop; that is the right choice, but nothing says so. The header comment speaks of "GUI blocked" and the render thread without naming the loop actually used. A reader cannot tell which places would break if the loop changed.
- **Proposal:** Let `EffectNode` own the transparent texture: `std::unique_ptr<QSGTexture> transparent`, created on the node's first `updatePaintNode` and destroyed with the node by the scene graph. Then the item's `m_transparent`, the `delete` in the destructor and `releaseResources` all go; `invalidateSceneGraph` only resets `imageTexture`. Add one sentence to the header (and to `docs/architecture.md` under the render loop): "QtNative runs Qt's basic render loop; the provider `destroyed` connection relies on it."
- **Decided:** E4, O5, G5 → (a) for the texture as proposed (`EffectNode` owns the 1×1 texture); (b) the basic-loop sentence is stated once, in the design note of E4 under `docs/design/`, and the header comment points to it; the render-side file is `effects/shader_material.*` (was `shadereffectmaterial`)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/shadereffectitem.h`, `shadereffectitem.cpp`, `docs/architecture.md`
- **Depends on:** NFX-05 (if done first, the change lands in `shadereffectmaterial.cpp`)
- **Verification:** SmokeTest `shader-effect`, `group-shader-window-destroy`, `effect-chain`; deleting a ShaderEffect while it renders (window close) in `HowTo Write a ShaderEffect`.

### NFX-04 Move the design notes to docs and replace references to private documents
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/effectchain.h:1-5`, `:95-119`; `effectchain.cpp:110`, `:153`, `:284`, `:752` ("doc 21", "Blur-Pyramide"); `shadereffectitem.h:1-38`; `shader_resource.h:1-22`; `qmlitem.h:1-15`; `linear_pass.h:8-13` ("render-texture.md")
- **Finding:** The headers carry 15 to 40 lines of design notes each. They cover the pyramid, the stage geometry, the chain-pass rules and the bake cache. "doc 21 'Blur-Pyramide'" and "plan effects/08" exist nowhere in `docs/` (search for "Blur-Pyramide" in `docs/` finds nothing), so the pyramid formulas (`pyramidLevel`, the step sizes) cannot be traced to a source. K1 covers issue numbers, but these document references are not covered.
- **Proposal:** Add a section "Internals" to `docs/effects-and-shaders.md` with four parts:
  - "ShaderEffect item": the slots, the uniform block merge, the samplers, the chain-pass auto uniforms and the threading sentence from NFX-03.
  - "Effect chain": stages, padding, `sourceRect`, TWO_PASS.
  - "Blur pyramid": the step sizes $p_k = \lceil p_{k-1}/2 \rceil$, $\sigma = r \cdot dpr / 2$, $L = \tfrac12 \log_2(1 + \tfrac67 \sigma^2)$, weights $w_k = \operatorname{clamp}(L - k, 0, 1)$, and why it is $\tfrac67$. If the derivation was in the private doc 21, copy it here.
  - "Runtime bake": targets, cache, the GLSL rename check.

  Each header keeps three to five lines: what the type is, who calls it, which thread, and a link to the section. Replace "doc 21" with that link and "render-texture.md" with its real path.
- **Decided:** O5, E4, G5 → the section "ShaderEffect item" is titled "Shader item" (the element is `Shader`; "shader effect" names an effect of the chain); the render-loop sentence is the design note of E4 in `docs/design/`; `effectchain.*`, `shadereffectitem.h`, `linear_pass.h` are `effects/effect_chain.*`, `effects/shader_item.h`, `offscreen/linear_pass.h`; C14 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the internals go to a design note in `docs/design/`, not into the user page `docs/effects-and-shaders.md`
- **Effort:** M, mechanical: no
- **Touches:** `docs/effects-and-shaders.md`, `src/VL.Qt.Native/effectchain.h`, `effectchain.cpp`, `shadereffectitem.h`, `shader_resource.h`, `qmlitem.h`, `linear_pass.h`
- **Depends on:** none
- **Verification:** review by reading; the links resolve.

### NFX-05 Split shadereffectitem.cpp into item, render side and layer registry; split rebuild()
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shadereffectitem.cpp:39-104` (global layer registry), `:113-293` (material, shader, view, node), `:704-893` (`rebuild`, 190 lines), `:847-848` and `:875-876` (the same "pending" expression twice)
- **Finding:** One 1,100-line file holds three things:
  - a process-wide registry of layer mipmap requests (free functions over `s_layers`);
  - the render-side classes (`EffectMaterial`, `EffectShader`, `SamplerView`, `EffectNode`, `typeFor`);
  - the GUI-side item.

  The threading rule (section 2) splits exactly along these lines, but the file does not show it. `rebuild()` merges the reflection of two stages, assigns auto uniforms, binds the table and checks sampler states in one function. A person who wants to add a uniform type has to read all of it.
- **Proposal:**
  - `shadereffectmaterial.h/.cpp` (namespace `vlqt`, internal): `EffectMaterial`, `EffectShader`, `SamplerView`, `EffectNode`, `materialTypeFor(vs, fs)`, `writeTexelSize`. Render side only; its head comment says so.
  - `layerrequests.h/.cpp`: `class LayerRequests` (static `request(item, owner, mip)`, `release(item, owner)`, `releaseAllQueued(owner)`), replacing `s_layers`/`requestLayer`/`releaseLayer`/`settleLayer` and the copy of the release loop in the destructor (`:412-422`).
  - In the item: `rebuild()` calls `collectLayout(frag, vert)`, which returns `{members, samplers, errors}`, then `assignAutoUniforms()`, `bindTable()` and `checkSamplerStates()`. Add `bool newShaderPending() const` for the expression at `:847`/`:875`.
- **Decided:** O5, S2, P14, TARGET 3.3 → the files are in `effects/`: `shader_item.*` (was `shadereffectitem`), `shader_material.*` (was `shadereffectmaterial`), `layer_requests.*` (was `layerrequests`); the split is a pure move in the first round of native cuts, created in the final folder with the final names at once
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/shadereffectitem.h`, `shadereffectitem.cpp`, new `shadereffectmaterial.h/.cpp`, new `layerrequests.h/.cpp`, `src/VL.Qt.Native/CMakeLists.txt`
- **Depends on:** none
- **Verification:** SmokeTest `shader-effect`, `shader-source`, `qtshaders`, `effect-chain`, `effect-passes`, `group-shader-source`; unit tests `ShaderEffectTests`, `SamplerTests`.

### NFX-06 Effect chain stage QML: from C++ strings into embedded .qml files
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/effectchain.cpp:82-112`, `:122-151`, `:157-160`, `:198-209`
- **Finding:** Three QML documents are C++ string literals with `\n` per line. They cannot be read with highlighting, checked by `qmllint` or edited without escaping. The base style already shows the better way: real `.qml` files embedded through `qt_add_resources` (`CMakeLists.txt:22-37`).
- **Proposal:** Create `src/VL.Qt.Native/effectchain/MultiStage.qml`, `PassStage.qml` and `ChainRoot.qml`, embedded under `/qt/qml/VLQt/EffectChain` in the same way as `style/VLQt`. `compile()` loads `QUrl("qrc:/qt/qml/VLQt/EffectChain/<name>.qml")`. The comments above the strings (`:71-81`, `:114-121`) move into the QML files as `//` comments. The module stays `VLQt.EffectChain` (the C++ types stay registered by `registerTypes`).
- **Decided:** D8, P22, TARGET 2.3 → the three QML files are in `src/VL.Qt.Native/qml/effects/` (not `effectchain/`), and this is one issue with NELM-06; C16 of [DECISION-ROUND.md](../DECISION-ROUND.md) → QML type files keep Qt's PascalCase (`MultiStage.qml`), a named exception of the snake_case rule of S2
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/effectchain.cpp`, new `src/VL.Qt.Native/effectchain/*.qml`, `src/VL.Qt.Native/CMakeLists.txt`
- **Depends on:** none
- **Verification:** SmokeTest `effect-chain`, `chain-forward`, `chain-reverse`, `effect-mask`, `effects-basic`, `effect-layer`.

### NFX-07 EffectChain::Link: one struct per link kind, and an API for Elements
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/effectchain.h:124-146`, `:171-173`; users `src/VL.Qt.Native/elements.cpp:1636-1791`
- **Finding:** `Link` has 22 fields for three kinds (MULTI; SHADER with SINGLE/TWO_PASS; PYRAMID), and most of them are meaningless for a given kind. A comment prefix says which kind uses each field. `links` and `built` are public, and Elements reads and writes `links[i].mask`, `maskFrame` and `maskFrameXf` directly. The header also defines five desc structs, `EffectComponents` and `EffectChain` (K3).
- **Proposal:**
  - `struct MultiPart { QVariant values[kEffectMultiKeys]; QQuickItem* mask; QQuickItem* maskFrame; QObject* maskFrameXf; }`
  - `struct ShaderPart { quint16 templ; quint64 shader, shader2; float strength; std::vector<EffectUniform> uniforms; QStringList problems; QString shaderError; }`
  - `struct PyramidPasses { std::vector<ShaderEffectItem*> down, up; int levels; QSize px; qreal dpr; }`
  - `Link { quint16 kind; QRectF padding; QPointer<QQuickItem> stage; QQuickItem* fx; QQuickItem* fx2; MultiPart multi; ShaderPart shader; PyramidPasses pyramid; }`

  Make `links` private with `size_t linkCount()`, `Link& link(size_t)` for the mask code, and `setBuiltHandler(std::function<…>)`. File layout: see section 4.
- **Decided:** O5, S2, TARGET 3.3 → the files are in `effects/`: `effect_chain_desc.h`, `effect_chain.*`, `effect_chain_item.*`; `ShaderEffectItem*` in `PyramidPasses` is `ShaderItem*`; `Link` keeps its name ("link" stays only for one entry of a chain as sent); the issue runs in the second round of native cuts, after NELM-05 has cut `elements.cpp` (`elements/`), and `commands.cpp` is in `scene/`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/effectchain.h`, `effectchain.cpp`, `elements.cpp`, `commands.cpp` (include of the desc header), new `effectchaindesc.h`
- **Depends on:** none
- **Verification:** SmokeTest `effect-chain`, `effect-mask`, `effect-glow`, `effect-innershadow`, `effect-passes`, `glow`; unit tests `EffectChainTests`, `EffectChainReconcilerTests`.

### NFX-08 Style field ids in QML from a Q_ENUM, not as numbers
- **Kind:** api
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/style/VLQt/SpinBox.qml:13-18`; `src/VL.Qt.Native/vlstyle.h:132-140`, `:164`; `vlstyle.cpp:47-55`
- **Finding:** SpinBox copies five protocol field ids as literals (`fieldIndicatorRadius: 97`). `--drift` checks only `protocol.h` against `Protocol.cs`, so a renumbering breaks the arrow buttons silently. `VlStyle::value(int field, int state)` takes protocol ids where the rest of the class uses `Slot`. `stateCount = 5` restates the protocol range in a comment (`vlstyle.h:140`).
- **Proposal:** Add `enum Field : quint16 { FrameColor = VLQT_STYLE_FIELD_FRAME_COLOR, … }; Q_ENUM(Field)` to `VlStyle`. In QML, use `VlStyle.value(VlStyle.IndicatorRadius, state)` and delete the five properties. Add `static_assert(stateCount == VLQT_STYLE_STATE_CHECKED + 1)` and `static_assert(std::size(s_fieldOfSlot) == SlotCount)`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlstyle.h`, `vlstyle.cpp`, `style/VLQt/SpinBox.qml`, `docs/styles.md` (QML API)
- **Depends on:** NFX-10 (same files; order is free)
- **Verification:** SmokeTest `style`, `styles`; unit test `BaseStyleTests`; help patch with a styled SpinBox (hover and press each arrow).

### NFX-09 One lookup of Qt's default ShaderEffect shaders
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shader_resource.cpp:526-551`, `src/VL.Qt.Native/shadereffectitem.cpp:358-384`
- **Finding:** The same 20-line lookup (known resource path, then a `QDirIterator` over `:/qt-project.org`) exists twice, for `.vert.qsb` and `.frag.qsb`.
- **Proposal:** In `shader_resource.h`, add `const QShader& qtDefaultShader(int stage)` (two function-local statics) and delete `defaultVertexShader` and the local `defaultFragmentShader`. Keep one warning per stage.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/shader_resource.h`, `shader_resource.cpp`, `shadereffectitem.cpp`
- **Depends on:** none
- **Verification:** SmokeTest `shader-effect` (default vertex shader), `effect-chain` (pass-through while baking).

### NFX-10 VlStyle members: `m_` prefix like the rest of QtNative
- **Kind:** naming
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/vlstyle.h:59`, `:75`, `:97`, `:117`, `:188-198`; `vlstyle.cpp` throughout
- **Finding:** `vlstyle` uses `_style`, `_cells`, `_state`; every other file of the layer (`shadereffectitem.h`, `qmlitem.h`, `effectchain.h`, `shader_resource.h`) uses `m_`. The brief puts consistency inside the layer first.
- **Proposal:** Rename to `m_style`, `m_cells`, `m_state`, `m_revision`, `m_dirty`, `m_colors`, `m_numbers`, `m_font`, `m_frame`, `m_text`, `m_indicator`, `m_track`.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/vlstyle.h`, `vlstyle.cpp`
- **Depends on:** none
- **Verification:** build; SmokeTest `style`.

### NFX-11 linear_pass::Pass: ComPtr members, move-only
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/linear_pass.h:14-23`, `:36`; `linear_pass.cpp:65-71`, `:172-180`
- **Finding:** `Pass` owns six COM objects through raw pointers but is an implicitly copyable struct. A copy released twice is a double `Release`. `create()` already builds everything in `ComPtr` and `Detach`es at the end. The ownership model is half done.
- **Proposal:** Give `Pass` `Microsoft::WRL::ComPtr<…>` members and make it non-copyable (`Pass(const Pass&) = delete`, move allowed). `release(pass)` becomes `pass = {}` and goes away together with `releaseCom`; `run` uses `.Get()`/`.GetAddressOf()`.
- **Decided:** TARGET 2.7 → `linear_pass.*` is in `offscreen/` (its only user is the offscreen scene), not in `effects/`
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/linear_pass.h`, `linear_pass.cpp`, `offscreen.cpp`
- **Depends on:** none
- **Verification:** SmokeTest `srgb`; unit test `OffscreenRendererTests`.

### NFX-12 Non-GL backends: one bake with the GLSL check target instead of two compiles
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shader_resource.cpp:358`, `:368-378`, `:180-199`
- **Finding:** On D3D, Vulkan and Metal every new source runs `QShaderBaker` twice under `s_bakeMutex`. The second run compiles the full glslang front end again, only to get a GLSL 150 translation for the rename warning. Live editing pays twice per save, and a reflect request (NFX-13) waits for both.
- **Proposal:** For non-GL backends, `bakeTargets` adds `GLSL 150` to the target list. `bake()` runs `glslRenames(s, where)` on the one result and `glslCheckBake` is deleted. Trade-off: if SPIRV-Cross cannot translate to GLSL 150, the whole bake fails where it now succeeds on D3D. The check exists for exported patches running on Linux, so failing early is arguably correct; mention it in the error text.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/shader_resource.cpp`
- **Depends on:** none
- **Verification:** SmokeTest `shader-source`, `qtshaders` (the rename warning still appears for a uniform named `length`).

### NFX-13 vlqt_shader_reflect: say which thread calls it, and use the bake cache
- **Kind:** threading
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shader_resource.h:75-77`, `shader_resource.cpp:478-519`; caller `src/VL.Qt/Native.cs:464`
- **Finding:** `reflect` bakes synchronously on the calling thread under `s_bakeMutex`, so it also waits for any running worker bake. The C# caller builds the pin model of shader nodes. If that runs on vvvv's thread (not confirmed here, see section 6), a save of a `.frag` stalls vvvv for up to two bakes (NFX-12), and the same source is then baked again by the resource worker. The results do not share the cache.
- **Proposal:** Cache the reflection text under the same key scheme (`stage:-2:sha1`, a separate entry). Document the calling thread in `vlqt.h` next to the function. If the caller is vvvv's thread, consider an async variant: the resource kind already reports results by event.
- **Decided:** C9 of [DECISION-ROUND.md](../DECISION-ROUND.md) → document the calling thread in `vlqt.h` and use the bake cache; an async variant only if a measurement asks for it
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/shader_resource.h`, `shader_resource.cpp`, `vlqt.h`
- **Depends on:** none
- **Verification:** unit tests `EffectShaderNodesTests`, `ShaderLinkNodeTests`; SmokeTest `qtshaders`.

### NFX-14 Reserved uniform names: one list, and docs that match
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/effectchain.cpp:226-231`, `src/VL.Qt.Native/shadereffectitem.cpp:386-389`, `:772-786`; `docs/effects-and-shaders.md:132`
- **Finding:** Three places define reserved names: the chain list, the item list, and the item's own `texelSize`/`dpr` handling. The docs say that `base`, `glow` and `shadow` produce "no pin and a warning", but QtNative reports an error line ("is reserved (set by VL.Qt)", `effectchain.cpp:363`).
- **Proposal:** In `shadereffectitem.h`, add `bool isItemReserved(const QByteArray&)` (`qt_Matrix`, `qt_Opacity`, `source`) and `bool isChainReserved(const QByteArray&)` (adds the chain names), with the list in one place. Decide warning or error with the C# area and align the docs sentence.
- **Decided:** O5, S2 → `shadereffectitem.h` is `effects/shader_item.h`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/effectchain.cpp`, `shadereffectitem.h`, `shadereffectitem.cpp`, `docs/effects-and-shaders.md`
- **Depends on:** none
- **Verification:** unit test `EffectChainTests`; SmokeTest `effect-passes`.

### NFX-15 Remove fields that are written but never read
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shadereffectitem.h:160` with write `shadereffectitem.cpp:966` (`Sampler::provider`); `shadereffectitem.cpp:175` with write `:1059` (`EffectMaterial::dprOffset`)
- **Finding:** `Sampler::provider` is assigned in `samplerTexture` and read nowhere (`m_watched` does that job). `EffectMaterial::dprOffset` is assigned in `updatePaintNode`; dpr is written straight into `ubuf` from `m_dprOffset` and nothing reads the material field.
- **Proposal:** Delete both fields and their assignments.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/shadereffectitem.h`, `shadereffectitem.cpp`
- **Depends on:** NFX-05 (same lines; order free)
- **Verification:** build; SmokeTest `shader-effect`.

### NFX-16 Test shaders out of the shipped builtin set
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shaders/gradient.frag`, `shaders/wave.frag`, `src/VL.Qt.Native/CMakeLists.txt:104`
- **Finding:** `CMakeLists.txt` globs every file in `shaders/` into the package. Nothing in `src` or `help` loads `gradient`. `wave` is used only by the SmokeTest (`EffectPassesTest.cs:113`, `GroupShaderSourceTest.cs:10`). Both ship to every user as `builtin:` shaders (a K6 kind of test hook).
- **Proposal:** Move both to `src/VL.Qt.SmokeTest/shaders/` and let the SmokeTest load them through `vlqt_res_load_shader_source` (runtime bake, which the SmokeTest then covers too). Delete `gradient` if no scenario needs it.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/shaders/`, `src/VL.Qt.SmokeTest/EffectPassesTest.cs`, `GroupShaderSourceTest.cs`, SmokeTest project file
- **Depends on:** none
- **Verification:** SmokeTest `effect-passes`, `group-shader-source`.

### NFX-17 Say why the material types are never freed
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shadereffectitem.cpp:113-128`
- **Finding:** `s_types` grows with every distinct shader pair, so a long live-editing session adds one type, two `QShader` copies and one renderer-cached pipeline per save. The comment says "never shrinks" but not why. Qt's batch renderer caches material shaders by `QSGMaterialType*` address, so freeing a type would let a new type at the same address hit a stale shader. A reader who "fixes the leak" introduces a wrong-shader bug.
- **Proposal:** Replace the comment with: "Never freed: Qt's renderer caches the material shader per type address; a freed and reused address would draw with a stale shader. Grows by one per distinct shader pair (one per saved edit)."
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/shadereffectitem.cpp`
- **Depends on:** none
- **Verification:** reading.

### NFX-18 Layer registry: one destroyed connection per element, not per re-request
- **Kind:** lifetime
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shadereffectitem.cpp:84-86`, `:73-75`
- **Finding:** `requestLayer` connects `destroyed` whenever it creates an entry, and `settleLayer` erases the entry when its last user leaves. An element that is requested, released and requested again (a mip filter toggled) collects one more connection each time. This is harmless (each one erases by key) but it grows without bound.
- **Proposal:** Store the `QMetaObject::Connection` in `LayerEntry` and disconnect it when the entry is erased in `settleLayer`. Lands in `layerrequests.cpp` with NFX-05.
- **Decided:** O5, S2 → the code lands in `effects/layer_requests.cpp`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/shadereffectitem.cpp` (or `layerrequests.cpp`)
- **Depends on:** NFX-05
- **Verification:** SmokeTest `shader-effect`; unit test `SamplerTests`.

### NFX-19 Reject a uniform block that is not at binding 0
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shader_resource.cpp:95-127`
- **Finding:** Suspicion, not reproduced. `checkReflection` requires samplers at bindings 1..15 "because binding 0 is the uniform block", but it never checks that the block is at binding 0. Qt's `QSGMaterialShader` takes the binding of the block from reflection. A block declared at `binding = 2` next to a sampler at 2 would collide in the shader resource bindings. That surfaces as a QRhi pipeline failure in the log, not as an error at the node. Confirm with a fragment shader that has `layout(std140, binding = 2) uniform buf` and `layout(binding = 2) uniform sampler2D x`.
- **Proposal:** In `checkReflection`, add `if (!blocks.isEmpty() && blocks.first().binding != 0) return "the uniform block must use binding 0";`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/shader_resource.cpp`
- **Depends on:** none
- **Verification:** SmokeTest `shader-source` with the shader above (error at the node).

### NFX-20 QmlWatcher: guard the id ranges and point at the Qt mechanism
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/qmlitem.cpp:176-236`, `:268-277`
- **Finding:** The watcher connects with raw method ids and dispatches through its own `qt_metacall`. This is clever and documented. But the comment's own limits (Qt stores the id as `ushort`, properties below `kSignalBase`) are not enforced: property `k` must stay below 29,000 and signal `j` below 35,536. Beyond that a signal would be dispatched as a property, or truncated. It is unrealistic but unchecked. The comment also does not say that the trick relies on `QMetaObject::connect` with no receiver meta-object, which calls `qt_metacall` with the absolute id. That is a Qt private detail worth naming for whoever upgrades Qt.
- **Proposal:** Stop collecting (with a `rejectRootProperty`-style error line "too many properties/signals to watch") when `k >= kSignalBase - kPropBase` or `j >= 65536 - kSignalBase`. Add one sentence naming `QMetaObjectPrivate::connect` (rmeta = nullptr → `QMetaObject::metacall` with the absolute index). Move the class to `qmlwatcher.h/.cpp` (section 4).
- **Decided:** S2, D9 → `qml_watcher.h/.cpp` are in `user_qml/`; `QMetaObjectPrivate` is a private Qt class and is listed in the head comment of `elements.h` (D9), next to the sentence in the code
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/qmlitem.cpp`, new `qmlwatcher.h/.cpp`, `CMakeLists.txt`
- **Depends on:** none
- **Verification:** unit tests `QmlTests`, `QmlNodeInstanceTests`; SmokeTest `qml`.

### NFX-21 QML `point` and `size` values to the patch
- **Kind:** api
- **Severity:** could
- **Breaking:** yes: a QML property of type `point`/`size` now arrives as a Vector2 instead of an error line; a signal argument of that type arrives as Vector2 instead of a string
- **Where:** `src/VL.Qt.Native/qmlitem.cpp:70-150`
- **Finding:** `toWireValue` maps `QRectF` and the `QVector*` types but not `QPointF`, `QPoint`, `QSizeF` or `QSize`, which are the QML basic types `point` and `size`. A declared `property point p` reports "type 'QPointF' cannot be sent to the patch". The wire already has `VLQT_VT_VEC2`.
- **Proposal:** Map `QPointF`/`QPoint` → `VEC2 (x, y)` and `QSizeF`/`QSize` → `VEC2 (width, height)`. Check the C# side's mapping of `VEC2` for QML outputs.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/qmlitem.cpp`, `docs` page of the QmlElement
- **Depends on:** none
- **Verification:** unit test `QmlDeclarationsTests`; SmokeTest `qml` with a `point` property.

### NFX-22 VlStyle: no side effect in fontAt; SET_STYLE parsing next to the other commands
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/vlstyle.cpp:199-214` (`warnMissingFontFamily` in `fontAt`), `:299-391` (`applySetStyle`); `vlstyle.h:18` (`#include "elements.h"` for `Value` and `Apply*`)
- **Finding:** `fontAt` runs on every state change (every hover) and calls `warnMissingFontFamily`. That function deduplicates, but it is still a lookup and a logging side effect inside a getter. `applySetStyle` parses wire bytes inside the style class, while every other command parses in `commands.cpp`. The header comment itself says "SET_STYLE (commands.cpp)". Including `elements.h` pulls the whole element model into the style header.
- **Proposal:** Call `warnMissingFontFamily` in `setCell` when a FontFamily cell changes. Move `applySetStyle` into `commands.cpp` as `Scene::setStyle`, using `VlStyle::clear`/`setCell`/`commit`. Forward-declare `Value` from a small `value.h` if one exists, or keep the include in the `.cpp`.
- **Decided:** S2, O2 → `vlstyle.*` is `style/vl_style.*`; `commands.cpp` is `scene/commands.cpp` (cut by NCMD-05, so `Scene::setStyle` goes to the file that holds `Scene` after that cut); `vlqt::Scene` keeps its name
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlstyle.h`, `vlstyle.cpp`, `commands.cpp`
- **Depends on:** NFX-10
- **Verification:** SmokeTest `style`, `styles`; unit test `BaseStyleTests`.

### NFX-23 Native logic without a direct test
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/shader_resource.cpp:201-221` (`cleanBakeErrors`), `:133-174` (`glslRenames`), `:223-272` (cache); `shadereffectitem.cpp:930-952` (layer requests); `vlstyle.cpp:299-391` (`applySetStyle`); `qmlitem.cpp:70-150` (`toWireValue`)
- **Finding:** The unit tests (`ShaderEffectTests`, `SamplerTests`, `EffectChainTests`, `BaseStyleTests`, `QmlTests`) test the C# side: command bytes and node behaviour. The native logic above is covered only end to end by SmokeTest pixel scenarios, which need a GPU. Nothing checks the error text format ("fragment code, line n: …"), the cache hit path, the rename warning, a malformed `SET_STYLE` or the layer request merge (NFX-02 went unnoticed). A refactoring of NFX-01, NFX-05, NFX-07 or NFX-12 runs without a net for these paths.
- **Proposal:** Add SmokeTest scenarios that read events, not pixels:
  - `shader-errors`: a broken source gives one EV_ERROR line per glslang error with a line number. The same source a second time arrives without a bake (cache), which is visible as the same event within one pump. A uniform named `length` gives the rename warning.
  - `style-commands`: bad field, bad state, truncated entry and padded end; each returns the expected ApplyResult and leaves the table unchanged.
  - `sampler-mip-shared` (NFX-02).

  No test hooks in the library (K6): everything goes through the existing `vlqt_*` API.
- **Decided:** F5, PLAN-CHECK wave 1 → the scenarios are NUnit tests (`shader-errors`, `style-commands`, `sampler-mip-shared` are test names), written in wave 1 before NFX-01, NFX-05, NFX-07 and NFX-12 are refactored
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.SmokeTest/` (new test classes, scenario list)
- **Depends on:** none
- **Verification:** the new scenarios pass with `tools/build.ps1 -Test`.

## 4 Target structure

`src/VL.Qt.Native/` (namespace `vlqt`, all internal to the DLL; nothing here is C API except through `vlqt.h`):

| File | Types / responsibility | Change |
|---|---|---|
| `shader_resource.h/.cpp` | `res::shader::ShaderResource`, `ShaderKind`, `load`, `loadSource`, `ready`, `qtDefaultShader(stage)` | `defaultVertexShader` → `qtDefaultShader` (NFX-09) |
| `shader_bake.h/.cpp` | `bakeTargets`, `bake(code, stage, targets)`, `cleanBakeErrors`, `glslRenames`, the bake cache, `reflect` | split out of `shader_resource.cpp`; `glslCheckBake` deleted (NFX-12) |
| `shadereffectitem.h/.cpp` | `ShaderEffectItem`: GUI side (slots, uniform table, layout, errors) | trimmed (NFX-05) |
| `shadereffectmaterial.h/.cpp` | `EffectMaterial`, `EffectShader`, `SamplerView`, `EffectNode`, `materialTypeFor`: render side; the node owns the 1×1 texture | new, split (NFX-05, NFX-03) |
| `layerrequests.h/.cpp` | `LayerRequests`: layer mipmap requests of samplers on elements | new, from `s_layers` functions (NFX-05, NFX-02, NFX-18) |
| `effectchaindesc.h` | `EffectValue`, `EffectMultiEntry`, `EffectUniform`, `EffectLinkDesc`, `EffectChainDesc`: the parsed command | split out of `effectchain.h` (K3, NFX-07) |
| `effectchain.h/.cpp` | `EffectChain` (with `Link` = `MultiPart` + `ShaderPart` + `PyramidPasses`), `EffectComponents` | reshaped (NFX-07, NFX-01) |
| `effectchainitem.h/.cpp` | `EffectChainItem`: QML root of `layer.effect` | moved out of `effectchain.cpp` (moc in the header) |
| `effectchain/ChainRoot.qml`, `MultiStage.qml`, `PassStage.qml` | stage QML, embedded at `qrc:/qt/qml/VLQt/EffectChain/` | new, from C++ strings (NFX-06) |
| `linear_pass.h/.cpp` | `linear_pass::Pass` (ComPtr, move-only), `isLinear`, `create`, `run` | `release` deleted (NFX-11); belongs to the QtTexture area, not to effects |
| `qmlitem.h/.cpp` | `QmlItem` | trimmed |
| `qmlwatcher.h/.cpp` | `QmlWatcher` | split out of `qmlitem.cpp` (NFX-20) |
| `qmlwire.h/.cpp` | `QmlWireValue`, `QmlPropertyEvent`, `QmlSignalEvent`, `toWireValue` | split out of `qmlitem.h/.cpp` |
| `vlstyle.h/.cpp` | `VlStyle` (attached, with `Q_ENUM Field`) and its four groups `VlStyleFrame`, `VlStyleText`, `VlStyleIndicator`, `VlStyleTrack` | members `m_` (NFX-10); `applySetStyle` → `commands.cpp` (NFX-22). The groups stay in this file as a stated exception to K3: they are parts of one QML API and have no behaviour of their own |
| `shaders/*.frag` | builtin shaders of the C# effects and the pyramid | `gradient.frag`, `wave.frag` → `src/VL.Qt.SmokeTest/shaders/` (NFX-16) |
| `style/VLQt/*` | base Controls style | SpinBox uses `VlStyle.<Field>` (NFX-08) |

`docs/effects-and-shaders.md` gets the section "Internals" (NFX-04).

## 5 Questions for Sebastian

**Q1 Should QtNative depend on the `basic` render loop, explicitly?** In a render loop, Qt alternates between preparing the scene (GUI) and drawing it; "basic" does both on one thread, "threaded" draws on a second thread. QtNative forces `basic` (`vlqt.cpp:1047`). A few places in this area rely on that (NFX-03): GUI-side texture deletes and the direct provider `destroyed` connection.
- (a) Write the code so that it is correct under both loops (NFX-03 for the texture; the provider connection is already correct under both). This costs little now.
- (b) Declare `basic` a fixed design decision in `docs/decisions/` and say in each affected header that it relies on it.

Recommendation: (a) for the texture, plus (b) as one decision record. Other areas (images, video, offscreen) probably have the same pattern, and a decision record tells their reviewers which rule to check against.

**Q2 GLSL rename check on Windows: keep it at the price of a bake failure?** The check warns on D3D about uniform names that only break on Linux/OpenGL (`shader_resource.cpp:129-174`).
- (a) Keep it as a second bake (today; two compiles per live edit).
- (b) One bake with an extra GLSL target (NFX-12): half the cost, but a shader that SPIRV-Cross cannot translate to GLSL fails on Windows too.
- (c) Drop the check on non-GL backends.

Recommendation: (b). An exported patch should not break only on Linux, and a failure that shows early is easier to fix.

## 6 For other areas

- **Resources (QtNative `resources.cpp`):** the effect chain creates shader resources inside QtNative (`effectchain.cpp:270-282`, `builtin:pyramid_down/up`). Their RES_EV_* events go to a host that never created those ids; check how `src/VL.Qt/Images/ResourceClient.cs` treats unknown ids.
- **Elements (`elements.cpp:1636-1791`):** reads and writes `EffectChain::links[i].mask*` and `built` directly; with NFX-07 it gets an API.
- **C# reconciler (`src/VL.Qt/Reconciler/EffectDiff.cs:26`):** the whole chain is re-sent when the chain object differs. Check whether an unchanged chain value can skip the send; together with NFX-01 this decides the per-frame cost of animated effects.
- **C# shader nodes (`src/VL.Qt/Native.cs:464`):** which thread calls `vlqt_shader_reflect` (NFX-13)?
- **Docs vs C# (`docs/effects-and-shaders.md:132`):** whether `base`/`glow`/`shadow` produce a warning or an error is decided where the pins are made (NFX-14).
- **QtNative-wide naming:** file names mix snake_case (`shader_resource`, `image_resource`, `linear_pass`) and joined lowercase (`shadereffectitem`, `effectchain`, `qtimageitem`, `vlstyle`); one rule for the layer belongs to the QtNative structure review.
- **Offscreen / QtTexture (`offscreen.cpp`):** sole user of `linear_pass`; NFX-11 touches it.
