# Review 12 – Shaders: ShaderEffect and shader node factory

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt/Shaders/` (ShaderAnnotations, ShaderReflection, ShaderPinModel, ShaderNodeDescription, ShaderNodeLayout, ShaderNodeFactory, ShaderLinkNode, ShaderUniforms), `src/VL.Qt/Nodes/ShaderEffectNode.cs`, `ShaderTemplates.cs`, `UniformNodes.cs`, `src/VL.Qt/Nodes/Effects/BuiltinShaderLink.cs`, `src/VL.Qt/Elements/ShaderEffect.cs`, `Uniform.cs`, `Sampler.cs`, `docs/effects-and-shaders.md`, section 4 of `11_qml.md`. Looked up with targeted excerpts: `Elements/Effect.cs:125-160` (ShaderLink), `Reconciler/EffectDiff.cs:257-289`, `Reconciler/UniformWriter.cs:24-53`, `Images/ShaderSlot.cs:66-100`, `NodeErrors` (member list), `Space.cs`, `Qml/QmlNodeFactory.cs` (WatchDir, GetNodeName), test file names and counts. Not read: the test bodies (only greps), QtNative's `vlqt_shader_reflect`, the help patches.

## 1 Summary

The pipeline from a `.frag` to a node is complete and works: annotations are parsed without a bake, reflection is a pure text parser over QtNative's output, the pin model is a pure function with good unit tests, and the runtime instance allocates nothing per frame. The weak points are in the middle and at the edges. `ShaderNodeLayout` is three things at once (file loading with logging, the element node's pins, the effect chain node's pins), and five of its eleven fields are documented "effect chain node only". The fixed pins of the element node are kept in three hand-synchronised lists. The element node and the chain node of one file each read and bake the file synchronously in their own init. Two real defects: `@padding` is documented and parsed as DIP but sent as renderer units (100 times too large in the default space), and every generated node with an image pin reports a false "set more than once" error.

The changes that matter most:

1. Fix the `@padding` unit (SHD-01) and the false duplicate-uniform error (SHD-02).
2. Split `ShaderNodeLayout` into one cached `ShaderSource` per file version plus `ShaderElementLayout` and `ShaderLinkLayout`, each of which makes its pin names unique against its own fixed pins (SHD-03, SHD-06, SHD-07).
3. One table for the fixed pins of the element node (SHD-08).
4. One `ShaderLink` type and one `ShaderLinkBuilder` for built-in and file links (SHD-09).
5. Move the folder-factory skeleton shared with QML into `NodeFactories/` (SHD-10, QML-03).

## 2 What works and must survive

- **Scan without bake.** `ShaderNodeFactory.Scan` reads only the file head (category, summary, filter, `@chainonly`). Bake and reflection run lazily in the description's `init`. That keeps the node browser fast with many files.
- **Pure, testable stages.** `ShaderAnnotations.Parse(string)`, `ShaderReflection.Parse(string)` and `ShaderPinModel.Build(...)` take text or records and return records, with no I/O. `ShaderNodeLayout.Load` takes the reflect function as a parameter. This is why 28 pin-model tests and 16 factory tests can run without QtNative. Keep this seam.
- **Non-fragmented descriptions** (`ShaderNodeDescription.cs:115-117`, `ShaderLinkNode.cs:54`). vvvv calls `IVLNode.Update` only for non-fragmented descriptions. The comment says why; keep it.
- **Pin identity through file changes.** Pin names come from uniform names, so vvvv keeps links on a content change. Compile errors keep the Input pin through the regex fallback (`ShaderNodeLayout.cs:49-50, 180`).
- **Hot paths, no allocation per frame:** `ShaderNodeInstance.Update` → `ShaderEffectNode.Update` (`ShaderNodeDescription.cs:237-264`, `ShaderEffectNode.cs:109-146`), `ShaderUniforms.Update` (`ShaderUniforms.cs:123-131`, which builds a new spread only when a pin changed), `ShaderLinkNodeInstance.Update` (`ShaderLinkNode.cs:145-175`), `BuiltinShaderLink.Describe` (`BuiltinShaderLink.cs:73-93`), and `ShaderSlot.Update`, whose `ReferenceEquals` fast path depends on the instance passing the same string object every frame. `CheckUniforms` runs only when the spread reference changes. The reconciler compares links and spreads by reference. A refactoring must keep "new object only on change".
- **Last working shader keeps drawing** while ShaderEffect code bakes (`ShaderSlot`).
- **Messages carry line numbers** (`ShaderAnnotationWarning.ToString`, `ShaderAnnotations.cs:15`), and duplicate-file errors name both paths and the fix ("Rename one of them").
- **Factory in the AppHost's `NodeFactoryCache`** (`ShaderNodeFactory.cs:74-78`), so watchers survive recompilation, as in VL.Stride.

## 3 Findings

### SHD-01 `@padding` is DIP in the docs but renderer units on the wire
- **Kind:** correctness
- **Severity:** must
- **Breaking:** yes: the effect area of file-shader chain nodes shrinks to what the docs promise; patches that relied on the oversized area (none known) clip.
- **Where:** `src/VL.Qt/Shaders/ShaderAnnotations.cs:39, 202-218`, `src/VL.Qt/Shaders/ShaderLinkNode.cs:18-19, 107, 178-191`, `src/VL.Qt/Elements/Effect.cs:153-154`, `src/VL.Qt/Reconciler/EffectDiff.cs:257-267`, `docs/effects-and-shaders.md` (Annotations table, `@padding` rows), `src/VL.Qt/Space.cs:17-18`
- **Finding:** The annotation docs and the docs page say `@padding` is "room around the element in DIP", and the `@padding <uniform> [factor]` form computes `abs(value) * factor` "in DIP". `ShaderLinkNodeInstance` puts that number into `ShaderLink.Padding`, which `Effect.cs:153` documents as "in units". `EffectDiff` converts it with `_mapping.ToQt` (units → DIP). In the default space `DIPTopLeft` one unit is 100 DIP, so `@padding 6` becomes 600 DIP per side. `@padding blurLength 0.52` with a 50 px blur becomes 2,600 DIP per side. The effect layer, and its memory and fill cost, grow by a factor of 10,000 in area, and large values may exceed the texture size limit. The uniforms of a file shader are sent raw (not `IsLength`), so the shader works in pixels, and the padding that follows from them is in pixels too. The help templates (`ExampleDirectionalBlur.frag` and others) are affected. No test checks the unit: `ShaderLinkNodeTests` only compares the parsed rectangle.
- **Proposal:** Keep DIP (it matches the shader's pixel uniforms and the docs). Give `ShaderLink` a padding unit: `ShaderLink(…, RectangleF padding, bool paddingInDip, float strength)` or, better, a small `EffectPadding` value type `{ RectangleF Value; bool IsDip; }`. `EffectDiff.WriteShaderLink` skips `_mapping.ToQt` for DIP padding (still `EffectPadding.Ceil`). `ShaderLinkNodeInstance` sets DIP. The built-in links keep units.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Elements/Effect.cs`, `src/VL.Qt/Reconciler/EffectDiff.cs`, `src/VL.Qt/Shaders/ShaderLinkNode.cs` (or its successors, SHD-09), `src/VL.Qt.Tests/ShaderLinkNodeTests.cs`, `src/VL.Qt.Tests/EffectChainReconcilerTests.cs`
- **Depends on:** none (Q3 confirms the direction)
- **Verification:** a new reconciler test: a file link with `@padding 6` in `DIPTopLeft` writes 6 DIP to `BeginShaderLink`. Help patch `HowTo Chain Effects` looks the same. A SmokeTest grab of a chain with `ExampleDirectionalBlur` stays the same visually.

### SHD-02 Image pins of generated nodes raise a false "set more than once" error
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderPinModel.cs:124-126`, `src/VL.Qt/Shaders/ShaderUniforms.cs:65-66`, `src/VL.Qt/Nodes/ShaderEffectNode.cs:135-139, 168-187`, `src/VL.Qt/Reconciler/UniformWriter.cs:45-48`
- **Finding:** Every `sampler2D` other than `source` gets two pins with the same `UniformName`: the image pin (`Uniform.FromImage`) and the hidden sampler pin (`Uniform.FromSampler`). The generated element node hands both to `ShaderEffectNode.Update`. `CheckUniforms` adds every name to `seen`, without skipping `Protocol.Vt.Sampler`, so it reports "Uniform 'lut' is set more than once, the last one counts" on the node's Error output and in the log. The claim is false: `UniformWriter` keys sampler entries separately (`name + SamplerKey`), and both values reach QtNative. The only test of the check (`ShaderEffectTests.cs:363`) uses two plain uniforms.
- **Proposal:** In `CheckUniforms`, key sampler-only entries apart, the same way `UniformWriter` does: `var key = u.ValueType == Protocol.Vt.Sampler ? u.Name + "\u0001" : u.Name;`. Better, move the key rule into one `UniformWriter.Key(Uniform)` used by both. Add a test with an image pin.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/ShaderEffectNode.cs`, `src/VL.Qt/Reconciler/UniformWriter.cs`, `src/VL.Qt.Tests/ShaderEffectTests.cs` or `ShaderNodeFactoryTests.cs`
- **Depends on:** none
- **Verification:** new unit test: a `ShaderNodeInstance` for a layout with one image pin has an empty `Error` after `Update`. Help patch `HowTo Blur an Image with a Control Mask` shows no error.

### SHD-03 Pin names are made unique against the wrong set of fixed pins
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes: chain nodes whose uniforms are named `size`, `opacity`, `transform` and so on lose the " 2" suffix, and their pins get the plain name.
- **Where:** `src/VL.Qt/Shaders/ShaderNodeLayout.cs:44-47, 198-218`, `src/VL.Qt/Shaders/ShaderNodeDescription.cs:97-101`, `src/VL.Qt/Shaders/ShaderPinModel.cs:124-126`, `src/VL.Qt/Shaders/ShaderLinkNode.cs:40-46, 58`
- **Finding:** `ReservedPinNames` lacks `Input Sampler`, which the element node adds as a hidden pin when it has an Input. A uniform `inputSampler`, or a `sampler2D input` (whose derived sampler pin is called "Input Sampler"), produces two pins with the same name in one description. The sampler pin's name is derived before `UniquePinNames` runs, so an image pin renamed to "Size 2" keeps a sampler pin called "Size Sampler". The chain node takes the element node's renamed pins (`ForChain` runs after `Load`), so a `size` uniform shows as "Size 2" on a chain node that has no Size pin. The user sees a number in the pin name with no reason they can find.
- **Proposal:** Uniqueness belongs to the layout of each node kind. `ShaderElementLayout` makes the names unique against `ShaderElementPins.Names` (SHD-08, including `Input Sampler`). `ShaderLinkLayout` makes them unique against `Input`, `Output`, `Error`. Derive the sampler pin's name from the image pin's final name (`"{imageName} Sampler"`) after uniqueness, and include it in the same pass.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/ShaderNodeLayout.cs` (→ SHD-07 files), `src/VL.Qt/Shaders/ShaderPinModel.cs`, `src/VL.Qt.Tests/ShaderNodeFactoryTests.cs`, `src/VL.Qt.Tests/ShaderLinkNodeTests.cs`
- **Depends on:** SHD-07, SHD-08
- **Verification:** unit tests: a uniform `inputSampler` on a filter is unique. A `size` uniform is "Size" on the chain node and "Size 2" on the element node.

### SHD-04 Changing `@category` in a file has no effect until another file is added
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderNodeFactory.cs:112-116, 166-173`, `src/VL.Qt/Shaders/ShaderNodeDescription.cs:112-114`, `src/VL.Qt/Shaders/ShaderLinkNode.cs:52-53`
- **Finding:** Name and category of a description are fixed when the factory scans. A content change only re-runs the description's `init`, and the factory invalidates itself on a content change only if the filter state or `@chainonly` flips. Editing `// @category Foo` therefore leaves the node in its old category until a `.frag` is added, deleted or renamed, or vvvv restarts. The docs page says the head category decides.
- **Proposal:** The factory's content filter compares the whole scanned head: store the `ShaderFile` head facts as one record (`ShaderFileHead(Category, IsFilter, ChainOnly)`) and invalidate when `ReadHead(file) != file.Head`. This also removes the two separate reads per event (`ReadIsFilter` + `ReadHead`, `:115-116`): one read, one parse.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/ShaderNodeFactory.cs` (or `ShaderFile.cs` after the split), `src/VL.Qt.Tests/ShaderNodeFactoryTests.cs`
- **Depends on:** none (fits into SHD-10 if that lands first)
- **Verification:** a factory test with a temp folder: rewrite the head category, and the factory's `invalidated` fires.

### SHD-05 A compile error removes every uniform pin
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: while a shader file does not compile, the node keeps its last pins (a behaviour change, Q4).
- **Where:** `src/VL.Qt/Shaders/ShaderPinModel.cs:88-96`, `src/VL.Qt/Shaders/ShaderNodeLayout.cs:49-50, 175-181`, `src/VL.Qt/Shaders/ShaderNodeDescription.cs:119-130`
- **Finding:** When frag or vert fail to bake, the pin model is empty and the node gets only Input (kept by a regex) and the fixed pins. Saving a typo takes away every uniform pin with its value and links; the comment at `:49` shows the problem is known and patched for Input only. The runtime instance is rebuilt from the new description, so the "last working version keeps drawing" promise of ShaderEffect does not hold for file nodes either: the new `ShaderSlot` has nothing that worked. The QML factory solves the same problem by keeping the last good layout (QML-05 in review 11).
- **Proposal:** The description keeps the last `ShaderSource` that compiled (per file, in the cache of SHD-06). On a compile error the layout uses the last good pins and adds the compile error as a red message. The instance draws the last good fragment source, and the Error output shows the new error. Without an earlier good version, the current behaviour stays (Input by regex). Put the "keep last good" rule in the shared `NodeFactories/` skeleton if QML-05 does so.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/` (description, source cache, layouts), `src/VL.Qt.Tests/ShaderNodeFactoryTests.cs` (`Layout_CompileError_OnlyFixedPins_KeepsInput` changes)
- **Depends on:** SHD-06, SHD-07, Q4
- **Verification:** unit test: a good layout, then a broken source: the pins stay and there is one error message. Manual: help patch `HowTo Create Shader Nodes from Files`, type an error into `RGBSplit.frag` and check that the links stay.

### SHD-06 Each file is read and baked twice per change, synchronously in `init`
- **Kind:** performance
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderNodeDescription.cs:121`, `src/VL.Qt/Shaders/ShaderLinkNode.cs:58`, `src/VL.Qt/Shaders/ShaderNodeLayout.cs:137-195`, `src/VL.Qt/Shaders/ShaderReflection.cs:49-61`, `src/VL.Qt/Shaders/ShaderNodeFactory.cs:115-116, 166-167, 190-213`
- **Finding:** For a filter shader the element description and the chain description each call `ShaderNodeLayout.Load`. Each reads the file, parses the annotations and bakes frag (and vert) through `vlqt_shader_reflect`, synchronously on vvvv's thread and serialised with the runtime bakes in QtNative. Each placed instance then bakes a third time through `ShaderSlot`. The same compile error is logged by both loads and again by every instance (`LogOnly`). The scan reads every file twice (`ReadHead`, `ReadIsFilter`), and so does every watcher event.
- **Proposal:** A `ShaderSourceCache` per factory sub-folder: `ShaderSource Get(ShaderFile file)` returns the loaded source (texts, annotations, pin model, read error) for the current file version, keyed by the last-write times of frag and vert, and reflects only on a miss. Both descriptions use it, and the log lines are written once per version, by the cache. `ReadHead` and `ReadIsFilter` become one `ShaderFileHead.Read(path)` (SHD-04). Not proposed: sharing the runtime bake between instances; that belongs to `ResourceClient`.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/` (new `ShaderSource.cs`, `ShaderSourceCache.cs`; `ShaderNodeLayout.cs`, both descriptions, factory), tests of the factory
- **Depends on:** SHD-07
- **Verification:** unit test with a counting fake `reflect`: one filter file with both descriptions initialised gives exactly one reflection per stage. The existing factory tests pass.

### SHD-07 Split `ShaderNodeLayout`; one padding type; one message type
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderNodeLayout.cs:13-41, 77-134, 137-195`, `src/VL.Qt/Shaders/ShaderAnnotations.cs:10-16, 32-67, 185-219`, `src/VL.Qt/Shaders/ShaderPinModel.cs:64, 69`
- **Finding:** `ShaderNodeLayout` holds the file sources, the element node's pins and messages, and five fields that only the chain node uses (`Padding`, `Passes`, `PaddingUniform`, `PaddingFactor`, `ChainErrors`). `ForChain` turns one layout into the other with `this with { … }` and re-sorts messages. `Load` does I/O, logging, reflection and naming in one method. The padding exists as three loose fields twice (annotations and layout), plus the `IsUniformPadding` guess. `ShaderAnnotationWarning` also carries errors (`ShaderAnnotations.Errors`) and pin-model warnings ("uniform is an array"), so its name is wrong in two of three uses. The optional record parameters with `default` arrays force `IsDefault` checks (`ShaderNodeLayout.cs:94, 190`). A reader has to keep the whole file in their head to know which field counts for which node.
- **Proposal:** Files and types (all `internal`, namespace `VL.Qt.Shaders`):
  - `ShaderMessage` (was `ShaderAnnotationWarning`): `(int Line, string Text)`, used for warnings and errors. Severity is given by the list it is in.
  - `ShaderPadding`: `abstract record ShaderPadding { None; Fixed(RectangleF Dip); FromUniform(string Uniform, float Factor) }` with `static ShaderPadding Parse(string, int line, List<ShaderMessage> errors)`. Replaces `Padding`, `PaddingUniform`, `PaddingFactor` in annotations and layout.
  - `ShaderAnnotations`: positional record without optional parameters: `Category, Summary, ChainOnly, Passes, Padding (ShaderPadding), Uniforms, BoolUniforms, Warnings, Errors`.
  - `ShaderSource` (new, from `Load`): `FragmentSource, VertexSource, FragmentAnnotations, VertexAnnotations, ShaderPinModel PinModel, string? ReadError`; `static ShaderSource Load(ShaderFile, Func<string, int, ShaderReflection> reflect)`, which does no logging.
  - `ShaderElementLayout` (from `Load` + `UniquePinNames`): `HasInput, Pins, Messages, Summary`; `static From(ShaderSource, ShaderFile)`.
  - `ShaderLinkLayout` (from `ForChain`): `Pins, Messages, Summary, ushort Template, ShaderPadding Padding`; `static From(ShaderSource, ShaderFile)`. `Padding` holds only a valid uniform.
  - Logging moves to the cache (SHD-06), once per version.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/*.cs`, `src/VL.Qt.Tests/ShaderNodeFactoryTests.cs`, `ShaderLinkNodeTests.cs`, `ShaderPinModelTests.cs`
- **Depends on:** none (SHD-03, SHD-05, SHD-06 build on it)
- **Verification:** all existing shader tests, adapted. SmokeTest `--all`. Help patches `HowTo Create Shader Nodes from Files`, `HowTo Chain Effects`.

### SHD-08 The fixed pins of the element node live in three hand-synchronised lists
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderNodeDescription.cs:67-89` (descriptions), `:171-185` (pins and defaults again), `:207-232` (order again), `src/VL.Qt/Shaders/ShaderNodeLayout.cs:44-47` (names again), `src/VL.Qt/Nodes/ShaderEffectNode.cs:86-130` (the same help texts, worded differently)
- **Finding:** Adding a pin to ShaderEffect means editing four places, and nothing fails if one is missed. The pin order of the description must match `ShaderNodeInstance.Inputs` by position. The help texts have already drifted from the `ShaderEffectNode` XML docs: Effect's text lists different effects, and Opacity and Anchor are shortened.
- **Proposal:** `ShaderElementPins` (new file): one static table of entries `(string Name, Type Type, object? Default, string Summary, bool Visible)`, from which come the descriptions, the reserved-name set (SHD-03) and a test that compares it with the parameters of `ShaderEffectNode.Update` (name and default by reflection over the method's `[Pin]`/`DefaultValue`). The instance keeps typed `ValuePin` fields, but builds its `Inputs` sequence in table order with one assert in DEBUG. Keep help texts in one place: the table copies the `ShaderEffectNode` texts, and the test checks they are equal.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/ShaderNodeDescription.cs` (→ `ShaderElementDescription.cs`, `ShaderElementInstance.cs`), new `ShaderElementPins.cs`, `src/VL.Qt/Shaders/ShaderNodeLayout.cs`, a test file
- **Depends on:** none
- **Verification:** new unit test: table vs. `ShaderEffectNode.Update` parameters. The existing instance tests pass.

### SHD-09 One `ShaderLink` type and one link builder for built-in and file shaders
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/Effect.cs:131-160`, `src/VL.Qt/Shaders/ShaderLinkNode.cs:16-22, 75-198`, `src/VL.Qt/Nodes/Effects/BuiltinShaderLink.cs:15-28, 37-117`
- **Finding:** `ShaderLink` is abstract with two empty subclasses (`ShaderFileLink`, `BuiltinShaderLinkEffect`) that only forward constructor arguments. Nothing switches on the subtype: `Effect.Segment` and `EffectDiff` use `ShaderLink`. `ShaderLinkNodeInstance` repeats the logic of `BuiltinShaderLink<TProps>`: own the shader slots, build a new link only when the input, an id or the props changed, pass the input on while an id is 0, count `Created`. The two report errors differently: built-in uses `NodeErrors.Warn` (yellow at the node), file uses `LogOnly` plus the Error pin. The names are also swapped: `BuiltinShaderLink` is the builder and `BuiltinShaderLinkEffect` the link.
- **Proposal:** Make `ShaderLink` a `sealed` concrete class (public constructor of today's protected one, plus the padding unit of SHD-01). Delete `ShaderFileLink` and `BuiltinShaderLinkEffect`. Rename `BuiltinShaderLink<TProps>` → `ShaderLinkBuilder<TProps>` in `Nodes/Effects/ShaderLinkBuilder.cs`, constructed from shader sources instead of built-in names: `ShaderLinkBuilder(ResourceClient?, ImmutableArray<ShaderSource> shaders, Func<Effect?, ImmutableArray<ulong>, TProps, ShaderLink> create, NodeErrors? errors, ShaderErrorDisplay display)`, where the source is `readonly record struct ShaderSlotSource(string? Path, string? Code)` (so `ShaderSource` above stays the file model) and `ShaderErrorDisplay` is `Warn | ErrorPin`. Built-in nodes pass `Path = "builtin:<name>"`, and the file link passes `Code = fragmentSource`. `ShaderLinkInstance` then holds a `ShaderLinkBuilder<FileLinkProps>` with `readonly record struct FileLinkProps(Spread<Uniform> Uniforms, RectangleF Padding)` and keeps only the padding-from-uniform rule. Because `Spread<T>`'s equality is not documented here, compare `Uniforms` by reference explicitly in `FileLinkProps.Equals`.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Elements/Effect.cs`, `src/VL.Qt/Nodes/Effects/BuiltinShaderLink.cs` (→ `ShaderLinkBuilder.cs`), the seven shader effect nodes in `src/VL.Qt/Nodes/Effects/` (Glow, InnerShadow, DirectionalBlur, ZoomBlur, RadialBlur, Displace, Blend), `src/VL.Qt/Shaders/ShaderLinkNode.cs`, `src/VL.Qt.Tests/ShaderLinkNodeTests.cs`, effect node tests
- **Depends on:** SHD-01 (same constructor)
- **Verification:** `ShaderLinkNodeTests`, `EffectChainReconcilerTests`, `EffectTemplateShadersTests`. SmokeTest effect scenarios. Help patches `HowTo Add a Glow`, `HowTo Chain Effects`.

### SHD-10 Move the folder-factory skeleton shared with QML into `NodeFactories/`
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderNodeFactory.cs:74-127, 217-227, 230-256`, `src/VL.Qt/Shaders/ShaderNodeDescription.cs:15-57, 136-156`, `src/VL.Qt/Shaders/ShaderPinModel.cs:282-287`; QML counterparts `src/VL.Qt/Qml/QmlNodeFactory.cs:82, 100, 199, 214-239`, `src/VL.Qt/Qml/QmlNodeDescription.cs:52`
- **Finding:** Both factories do the same things, each in its own copy: register in the `NodeFactoryCache`, `ForPath` (watch `path` for the folder, scan recursively, watch for new, deleted and renamed files by extension), `GetNodeName` (`Name_Variant` → `Name (Variant)`), duplicate detection, the per-file content watch, `OpenEditor`, the pin description class and `ValuePin`. The QML side already uses `ShaderPinDescription` and `ValuePin` from `Shaders/` (review 11, section 4), which is a dependency in the wrong direction. Review 11 proposes `NodeFactories/` (QML-03).
- **Proposal:** Implement QML-03's `NodeFactories/` once, for both: `FolderNodeFactory` (parameters: folder name, extensions that count as "new/deleted", a scan delegate, a delegate that compares the head of a changed file, SHD-04), `FileNodeNames.Split(fileNameWithoutExtension)`, `FileEditor.Open(params string[] paths, ILogger)`, `PinNames.FromIdentifier` (from `ShaderPinModel.PinName`), `FilePinDescription` (from `ShaderPinDescription`), `ValuePin<T>`. Duplicate detection should compare node identity, not file names (SHD-15). The shader factory keeps only `ShaderFile`, `Scan` and the description creation.
- **Effort:** M, mechanical: no
- **Touches:** new `src/VL.Qt/NodeFactories/*.cs`, `src/VL.Qt/Shaders/ShaderNodeFactory.cs`, `ShaderNodeDescription.cs`, `ShaderPinModel.cs`, `src/VL.Qt/Qml/QmlNodeFactory.cs`, `QmlNodeDescription.cs`, `QmlNodeLayout.cs`, tests of both factories
- **Depends on:** QML-03 (one issue for both, or QML-03 first)
- **Verification:** `ShaderNodeFactoryTests`, the QML factory tests. Help patches `HowTo Create Shader Nodes from Files` and the QML one.

### SHD-11 Names: file and type names that say which node they belong to
- **Kind:** naming
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderLinkNode.cs` (no type of that name), `src/VL.Qt/Shaders/ShaderNodeDescription.cs:65, 163`, `src/VL.Qt/Shaders/ShaderLinkNode.cs:29, 75`, `src/VL.Qt/Shaders/ShaderNodeFactory.cs:13-39`
- **Finding:** A generated file makes two nodes, an element node and an effect chain node. The code calls them `ShaderNode…` and `ShaderLink…`, and the docs call them "element node" and "effect chain node". `ShaderNodeDescription` and `ShaderNodeInstance` say nothing about the element. `ShaderLinkNode.cs` contains `ShaderFileLink`, `ShaderLinkDescription` and `ShaderLinkNodeInstance`. `ShaderFile` lives in the factory file. The word "effect" means three things here: the `ShaderEffect` element, the effect chain (`Effect`) and the "shader effects" (Glow and others) of the docs, see Q1.
- **Proposal:** Use "element" and "link" consistently: `ShaderNodeDescription` → `ShaderElementDescription`, `ShaderNodeInstance` → `ShaderElementInstance`, `ShaderLinkNodeInstance` → `ShaderLinkInstance`, `ShaderLinkDescription` stays, and `ShaderFileLink` is deleted (SHD-09). One type per file as in section 4. In XML docs and the docs page, write "element node" and "link node (effect chain)".
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Shaders/*.cs`, shader tests
- **Depends on:** SHD-07, SHD-09 (do the renames together with the splits)
- **Verification:** build and tests.

### SHD-12 Test hooks in the shader nodes (K6 for this area)
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/ShaderEffectNode.cs:49-61, 84`, `src/VL.Qt/Shaders/ShaderNodeDescription.cs:187-204`, `src/VL.Qt/Shaders/ShaderLinkNode.cs:95-127`, `src/VL.Qt/Nodes/Effects/BuiltinShaderLink.cs:69-70`
- **Finding:** `ShaderEffectNode` has two test constructors (without client, with fake client) beside the production one, which builds `ElementNode` through a different path. It also has `internal Core` for tests. `ShaderNodeInstance` has a test constructor and a `Uniforms` property "(tests)". `ShaderLinkNodeInstance` has a test constructor, `Created` and `Uniforms` "(tests)". `BuiltinShaderLink` has a public `Created` counter. The production constructors are not covered by the tests that use the hooks.
- **Proposal:** One internal constructor per type that takes its dependencies (`ResourceClient?`, an `INodeHost`-like pair of logger and warning sink, as review 10 proposes for element nodes), and a production constructor that only resolves them from `NodeContext`. Replace `Created` with reference checks: two `Update` calls without a change return the same `Output` object, which is what the reconciler depends on. Replace `Uniforms` with the `Inputs` sequence, setting values through `IVLPin.Value` as vvvv does. `ShaderNodeLayout.Load`'s `reflect` parameter is a proper seam and stays.
- **Effort:** M, mechanical: no
- **Touches:** the files above, `src/VL.Qt.Tests/ShaderEffectTests.cs`, `ShaderLinkNodeTests.cs`, `ShaderNodeFactoryTests.cs`, effect node tests
- **Depends on:** the element-node constructor pattern of review 10
- **Verification:** the same tests, rewritten, pass.

### SHD-13 Dead weight and leftovers in the pipeline
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderPinModel.cs:76-77` (parameter `name` unused), `:110` (deconstructed `annotations` unused; `vertA ?? ShaderAnnotations.Empty` only feeds it), `src/VL.Qt/Shaders/ShaderReflection.cs:10-14, 65`, `src/VL.Qt/Shaders/ShaderNodeFactory.cs:219-221`, `src/VL.Qt/Shaders/ShaderPinModel.cs:73` with `ShaderNodeLayout.cs:50` and `Reconciler/UniformWriter.cs:53`
- **Finding:** `Build(string name, …)` never uses `name`. `ShaderReflection.Parse`'s doc says "task 3 decides", a leftover of a plan. The internal enum `ShaderStage` mirrors `Protocol.ShaderStage` (int constants), while `ShaderLinkNodeInstance` and `ShaderSlot` use the protocol constants directly, so a reader meets two names for the same value. `GetNodeName` strips `.frag`, but its production caller passes `GetFileNameWithoutExtension`. The sampler name `"source"` is spelled in three places.
- **Proposal:** Remove the `name` parameter and the unused deconstruction. Delete the history sentence. Use one stage type: keep the enum `ShaderStage` in `Shaders/` and have `ShaderSlot` take it, or delete it and use `Protocol.ShaderStage`; recommended is keeping the enum, for type safety. `GetNodeName` moves to `FileNodeNames` (SHD-10) without extension handling. Keep one `InputSamplerName` constant, used by the regex and by `UniformWriter`.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Shaders/ShaderPinModel.cs`, `ShaderReflection.cs`, `ShaderNodeFactory.cs`, `ShaderNodeLayout.cs`, `src/VL.Qt/Images/ShaderSlot.cs`, `src/VL.Qt/Reconciler/UniformWriter.cs`, tests that call `Build`
- **Depends on:** none
- **Verification:** build and the shader tests.

### SHD-14 Messages a user can act on: name the declared type
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderPinModel.cs:150`, `src/VL.Qt/Shaders/ShaderReflection.cs:17-18`, `src/VL.Qt/Shaders/ShaderAnnotations.cs:149-156`, `src/VL.Qt/Shaders/ShaderNodeFactory.cs:135-136`
- **Finding:** QtNative reports any unsupported type as `"other"`, so the node says "uniform 'offsets' of type 'other' is not supported" for a `uvec2`. The annotation parser has already matched the declared type, but it keeps only the bools. A `@chainonly` file without `source` makes no node and only logs a warning, so a user who looks at the node browser sees nothing.
- **Proposal:** The parser keeps `ImmutableDictionary<string, string> DeclaredTypes` (instead of `BoolUniforms`; bool is then `DeclaredTypes[name] == "bool"`), and the pin model uses it in the message: "type 'uvec2' is not supported; use float, vec2, vec3, vec4, int, bool or mat4". For `@chainonly` without `source`, still create the link node, with the existing "not a filter" error (`ShaderNodeLayout.cs:97-98`) shown on it, so the message appears where the user looks.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/ShaderAnnotations.cs`, `ShaderPinModel.cs`, `ShaderNodeFactory.cs`, `src/VL.Qt.Tests/ShaderPinModelTests.cs`, `ShaderNodeFactoryTests.cs`
- **Depends on:** none
- **Verification:** unit tests for both messages.

### SHD-15 Node identity: duplicates by file name, collisions with built-in effects undetected
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Shaders/ShaderNodeFactory.cs:59-64, 153-160, 168-173`
- **Finding:** Duplicates are found by file name within one `qtshaders/` tree. Two `Blur.frag` files with different `@category` are flagged although their nodes differ. `Glow_.frag` and `Glow.frag` both give the node `Glow` and are not flagged. A filter named like a built-in effect node (`Blur.frag`, `Glow.frag`) gets a link node with the same name and category (`Qt.Effects`) as the built-in node; the templates avoid this only by their `Example` prefix. I did not check how vvvv resolves two nodes with the same identity. A test with a `Blur.frag` in a help folder would show it.
- **Proposal:** Detect duplicates by the identity of the descriptions the scan will create, `(FullName, Category)` per node kind, and check link nodes against the names of the built-in effect nodes, which the effects folder can export as a list. The error names both sources and says "rename the file".
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Shaders/ShaderNodeFactory.cs` (or `NodeFactories/FolderNodeFactory.cs`), `src/VL.Qt.Tests/ShaderNodeFactoryTests.cs`
- **Depends on:** SHD-10 (if the detection moves to the shared skeleton)
- **Verification:** factory unit tests for the three cases.

### SHD-16 Outdated or misplaced XML docs
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/ShaderEffect.cs:7-11`, `src/VL.Qt/Nodes/UniformNodes.cs:9-13`, `src/VL.Qt/Nodes/ShaderTemplates.cs:17-18`, `src/VL.Qt/Nodes/ShaderEffectNode.cs:22-25`, `src/VL.Qt/Shaders/ShaderNodeFactory.cs:41-46, 58-64`
- **Finding:** `ShaderEffect` says it "draws a pre-baked .qsb fragment shader". Today it draws any shader resource, mostly GLSL text baked at runtime. The `UniformNodes` summary speaks for the class, but the class also holds the `Sampler` node, which is neither a uniform nor advanced. The fragment template tells the user that uniforms come from "a Uniform node (Qt.Elements)", but the category is `Qt.Elements.Uniform`. The comment block above `ShaderEffectNode` mixes ownership notes with the builtin-path workaround of `ResourceClient`. The `LinkCategory` summary needs three sentences to explain one rule. That rule belongs in `docs/effects-and-shaders.md` (where it is already stated) plus one line here.
- **Proposal:** `ShaderEffect`: "Draws a fragment shader (and optional vertex shader) over its bounds; `Input` is sampled as `source`." `UniformNodes`: summary per node only, with the class summary reduced to one line; move `Sampler` per Q2. Template: "a Float or Color node of Qt.Elements.Uniform with the same name". Move the `builtin:` path note to `ResourceClient.TryGetBuiltinShader`. Reduce `LinkCategory` to "Category of the link node of a filter; see docs/effects-and-shaders.md".
- **Effort:** S, mechanical: no
- **Touches:** the files listed
- **Depends on:** Q1, Q2 for the final names
- **Verification:** read in vvvv's node help, `manual-check`.

## 4 Target structure

`src/VL.Qt/Shaders/` (namespace `VL.Qt.Shaders`, everything `internal`)

| File | Type, responsibility | Change |
|---|---|---|
| `ShaderFile.cs` | record: one `.frag` (+ `.vert`) found by the scan, with its `ShaderFileHead` | split from `ShaderNodeFactory.cs`; head as one record (SHD-04) |
| `ShaderFileHead.cs` | record `(Category, Summary, IsFilter, ChainOnly)`, `static Read(path)` | new; replaces `ReadHead` + `ReadIsFilter` |
| `ShaderNodeFactory.cs` | registration, `Scan`, which descriptions a file gets; built on `NodeFactories/FolderNodeFactory` | smaller (SHD-10) |
| `ShaderMessage.cs` | record `(Line, Text)` | renamed from `ShaderAnnotationWarning` |
| `ShaderAnnotations.cs` | record + `Parse(source)`; private nested `TagReader`, `Tag` | without optional parameters; `DeclaredTypes` (SHD-14) |
| `UniformAnnotation.cs` | record: tags of one uniform | split |
| `ShaderPadding.cs` | `None`, `Fixed(Dip)`, `FromUniform(name, factor)`, `Parse` | new (SHD-07) |
| `ShaderReflection.cs` | record + `Reflect`, `Parse` | unchanged apart from SHD-13 |
| `ReflectedUniform.cs`, `ReflectedSampler.cs` | records | split |
| `ShaderStage.cs` | enum | split |
| `ShaderPinType.cs` | enum | split |
| `ShaderPin.cs` | record: one uniform/image/sampler pin | split |
| `ShaderPinModel.cs` | pin rules from reflection + annotations | `name` parameter gone |
| `ShaderSource.cs` | one loaded file version: texts, annotations, pin model, read error | new, from `ShaderNodeLayout.Load` |
| `ShaderSourceCache.cs` | one `ShaderSource` per file version, keeps the last good one, logs once | new (SHD-05, SHD-06) |
| `ShaderElementPins.cs` | table of the fixed pins of the element node | new (SHD-08) |
| `ShaderElementLayout.cs` | pins + messages of the element node | from `ShaderNodeLayout` |
| `ShaderLinkLayout.cs` | pins + messages + template + padding of the link node | from `ShaderNodeLayout.ForChain` |
| `ShaderElementDescription.cs` | node description of the element node | renamed from `ShaderNodeDescription` |
| `ShaderElementInstance.cs` | running element node over `ShaderEffectNode` | renamed from `ShaderNodeInstance`, split |
| `ShaderLinkDescription.cs` | node description of the link node | split from `ShaderLinkNode.cs` |
| `ShaderLinkInstance.cs` | running link node over `ShaderLinkBuilder` | renamed from `ShaderLinkNodeInstance` |
| `UniformPin.cs` | abstract `UniformPin` + `UniformPin<T>` (one file, base and its only generic) | split from `ShaderUniforms.cs` |
| `ShaderUniforms.cs` | pins → `Spread<Uniform>` on change | unchanged |
| ~~`ShaderNodeLayout.cs`~~ | | deleted (split) |
| ~~`ShaderLinkNode.cs`~~ | | deleted (split); `ShaderFileLink` deleted |

`src/VL.Qt/NodeFactories/` (shared with QML, see review 11): `FolderNodeFactory`, `FileNodeNames` (from `GetNodeName`), `FileEditor` (from `OpenEditor`), `PinNames` (from `ShaderPinModel.PinName`), `FilePinDescription` (from `ShaderPinDescription`), `ValuePin<T>` (moved from `ShaderNodeDescription.cs`).

`src/VL.Qt/Nodes/` (namespace `VL.Qt.Nodes`):

| File | Type | Change |
|---|---|---|
| `ShaderEffectNode.cs` | `public` node (name per Q1) | one internal constructor (SHD-12), `CheckUniforms` fixed (SHD-02) |
| `ShaderTemplates.cs` | default fragment text | text fix (SHD-16) |
| `UniformNodes.cs` | `public` uniform nodes | `Sampler` moved per Q2 |
| `Effects/ShaderLinkBuilder.cs` | builder of `ShaderLink` for built-in and file shaders | renamed from `BuiltinShaderLink.cs`; `BuiltinShaderLinkEffect` deleted (SHD-09) |

`src/VL.Qt/Elements/`: `ShaderEffect.cs` (summary, name per Q1), `Uniform.cs` (unchanged), `Sampler.cs` → `Sampler.cs`, `SamplerFilter.cs`, `SamplerMipFilter.cs`, `SamplerAddress.cs` (K3). `Effect.cs`: `ShaderLink` becomes `sealed`, with a padding unit (SHD-01, SHD-09).

## 5 Questions for Sebastian

**Q1 Rename `ShaderEffect`?** In VL.Qt, "effect" means the `Effect` chain (Blur, Glow, the link nodes), and the docs call Glow and the other effects "shader effects". `ShaderEffect` is Qt's name for an element that draws a shader (in QML, Qt's UI language, `ShaderEffect` is that item), and it has nothing to do with the chain. (a) Keep `ShaderEffect`: Qt users recognise it, and the docs explain the difference. (b) Rename the element and node to `Shader` (node in `Qt.Elements.Shaders`, next to the generated element nodes); "effect" then always means the chain. The node is Advanced and used in few help patches, so the cost is small. Recommendation: (b).

**Q2 Where does the `Sampler` node live?** It sits in `Qt.Elements.Uniform` (an Advanced category) but is used by `Blend`, `Displace`, the image pins of generated nodes and ShaderEffect. (a) Keep. (b) `Qt.Elements.Shaders` together with ShaderEffect, with the uniform nodes in `Qt.Elements.Shaders.Uniform`, so everything about shaders is in one place. (c) Next to the image nodes. Recommendation: (b), coordinated with the API review (01).

**Q3 `@padding` unit (SHD-01).** (a) DIP, as documented: fix the code. The padding follows the shader's pixel uniforms and does not depend on the renderer's space. (b) Renderer units: fix the docs. Then `@padding 6` means something else in every space, and `@padding <uniform>` mixes pixel uniforms with unit padding. Recommendation: (a).

**Q4 Keep the last good pins while a shader file does not compile (SHD-05)?** (a) Yes: links and values survive a typo, and the last good shader keeps drawing. The node shows the compile error in red. (b) No, as today: all uniform pins disappear until the file compiles. Recommendation: (a), the same rule as QML-05.

## 6 For other areas

- Effects/Reconciler: padding is a `RectangleF` with `Width` = right and `Height` = bottom, against the meaning of its constructor (`Elements/Effect.cs:229-251`, `ShaderLink.Padding`). A `Thickness`-like `EffectPadding(Left, Top, Right, Bottom)` would read correctly.
- Runtime/Nodes: `NodeErrors` has three reporting styles (`Report`, `Warn`, `LogOnly`), and nodes with an Error output choose differently (`BuiltinShaderLink.cs:95-109` vs `ShaderLinkNode.cs:169-174`). One documented rule per node kind is needed.
- Reconciler: `UniformWriter.cs:24, 53` spells the sampler key and `"source"` locally. The key rule should be one method used by `ShaderEffectNode.CheckUniforms` too (SHD-02).
- QML (review 11): `QmlNodeFactory` depends on `Shaders/` types (`ShaderPinDescription`, `ValuePin`); SHD-10 and QML-03 are one issue.
