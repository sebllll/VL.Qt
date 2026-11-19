# Review 11 – User QML: element and node factory

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt/Qml/QmlDeclarations.cs`, `QmlNodeFactory.cs`, `QmlNodeDescription.cs`, `QmlNodeLayout.cs`, `QmlNodeInstance.cs`, `QmlTypes.cs`; `src/VL.Qt/Nodes/QmlElementNode.cs`, `QmlFileWatcher.cs`; `src/VL.Qt/Elements/QmlElement.cs`, `QmlEvents.cs`, `QmlProperties.cs`, `QmlSignal.cs`; `docs/user-qml.md`, `docs/decisions/user-qml.md`. Skimmed (targeted excerpts): `src/VL.Qt/Shaders/ShaderNodeFactory.cs`, `ShaderPinModel.cs:282`, `src/VL.Qt/Elements/ElementSink.cs:200-245`, `src/VL.Qt.Native/qmlitem.cpp:355-485`, `src/VL.Qt/buildTransitive/VL.Qt.Shaders.targets`, the test names of `src/VL.Qt.Tests/Qml*Tests.cs` and `QmlTests.cs`; VL.StandardLibs `VL.Core/src/NodeFactory/NodeBuilding.cs`, `VL.Core/src/FileSystemUtils.cs`, `VL.Stride.Runtime/src/Rendering/Effects/EffectShaderNodes.cs`, `VL.CoreLib/src/Reactive/ChannelHub.NodeBuilding.cs`. Not read: the bodies of the tests, `src/VL.Qt.SmokeTest/QmlTest.cs`, `QmlPropertyWriter`, the rest of `qmlitem.cpp`.

## 1 Summary

The area has two parts. `QmlElement` (`Nodes/QmlElementNode.cs`, `Elements/Qml*.cs`) loads a user `.qml` file into the element tree with a dictionary in and out; it is small, careful about allocation and hot reload, and well tested (`QmlTests.cs`, 83 cases). The node factory (`Qml/`) turns every `qml/**/Upper.qml` into a node with typed pins. Its pins come from `QmlDeclarations.cs`: a hand-written lexer plus a scan of the root object's members at depth 1, with literal evaluation and sibling-file inheritance. The parser is careful and never throws on the inputs I traced, and 51 tests cover it. Its lexer does not know JavaScript regex literals, so a valid file with `/"/` or `/\/\//` in any function loses all its pins. The factory copies the skeleton of the shader factory almost line by line, and borrows its pin types from `Shaders/`. The common pins are defined in four places and have already drifted.

The changes that matter most:
1. Make the lexer skip regex literals and count lines in escapes (QML-01). Add a "parse never throws, any prefix" test before the split (QML-12).
2. Split `QmlDeclarations.cs` into lexer, head reader, member parser, literals and one file per record (QML-02).
3. Extract the shared folder-node-factory parts with the shader factory, and use `AppHost.RegisterNodeFactory(identifier, forPath)` from VL.Core (QML-03).
4. Define the common pins once (QML-04).
5. Make hot reload of generated nodes cheap and safe against half-written files and stale component caches (QML-05, QML-06).

## 2 What works and must survive

- **The parser runs no user code and needs no Qt** (`QmlDeclarations.cs:131`). So the node browser can show pins without a Qt engine, and the parser can be unit-tested. A file that does not parse still gets a node with the common pins, its head tags and its errors (`QmlDeclarations.cs:233-235`, `QmlNodeLayout.cs:151-174`), so a typo does not drop the node and its links.
- **Invalidation by signature** (`QmlDeclarations.Signature`, `QmlNodeFactory.cs:223-227`): only a change of what reaches the description rebuilds the node. Body edits only reload the running instance.
- **Send rule** (`QmlNodeInstance.cs:232-249`): a typed input is silent while it equals the declared default, so a binding in the file stays alive. A refactoring must not "simplify" this to always-send.
- **Input and output of one property share the pin name** (`QmlNodeLayout.cs:227-230`). vvvv tells them apart by kind. Help patches depend on it.
- **Hot paths without allocation:** `QmlNodeInstance.Update` (`QmlNodeInstance.cs:112-138`: `UpdatePlanB`, `InputSlot<T>.Poll`, the early return in `UpdateSignals`), `QmlElementNode.Update` (`QmlElementNode.cs:118-146`: `ResolveSource` cache, `QmlProperties.Update`/`Matches`), and the output dictionary, which is copied only when `QmlPropertyUpdates` changes. The boxing in `TryCoerce` and the `Spread` builders run only on a change or an emission. The test `Update_NothingChanged_SameDescription_AndNoAllocation` guards this.
- **Exact value equality on the wire** (`QmlValue`, `QmlProperties.cs:8-13`). Stride's vector `Equals` is a near-equal compare and would swallow small changes.
- **Content comparison of the input dictionary** (`QmlProperties.cs:86-91`). Patches mutate dictionaries in place.
- **Defensive event decoding** (`QmlEvents.cs:22-75`): bounds checks before every read, at most 64 arguments.
- **One OS watcher per folder:** every watcher goes through `NodeBuilding.WatchDir`/`FileSystemUtils.WatchDir`, which share a `FileSystemWatcher` per (path, filter, recursive) (`VL.Core/src/FileSystemUtils.cs:19-47`).

## 3 Findings

### QML-01 Lexer: skip JavaScript regex literals, count lines inside escapes
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt/Qml/QmlDeclarations.cs:371-379`, `:394-436`, `:437-458`, `:504-509`
- **Finding:** The lexer runs over the whole file, because it must balance the braces of nested objects and functions to find the end of each root member. A JavaScript regex literal is lexed as punctuation, so its content is read as code:
  - `function strip(s) { return s.replace(/"/g, "") }` starts a string at `"` and fails with "unterminated string" (`:427-432`).
  - `/https?:\/\//` contains `//` (characters `\`, `/`, `\`, `/`, `/`), which opens a line comment and swallows the rest of the line (`:371-379`). The result is "unexpected end of file: the '(' … is never closed".
  - `/[{]/` unbalances the brace stack (`:639-656`).

  In each case Qt compiles the file, but the node keeps only the common pins, every link on a property pin is lost, and the error points at a line that is correct. The reader of the error cannot act on it.

  Two smaller defects in the same code:
  - A string with a line continuation (`"abc\` + newline) skips the newline without `line++` (`:406-408`).
  - A template literal skips an escaped newline the same way (`:443-446`).

  Every line number after either one is one too low. A template literal with a nested backtick inside `${ }` ends early.
- **Proposal:** In `Lex`, treat `/` as the start of a regex literal when the previous significant token is absent, is punctuation in `( , = : [ ! & | ? { } ; + - * % < > ~ ^`, or is one of the keywords `return typeof instanceof in of new delete void throw case do else`. Otherwise it is division. Skip to the closing unescaped `/` outside a `[...]` class, then the flag letters, and emit one token of a new `TokenKind.Regex`. An unterminated regex on its line is an error "unterminated regular expression". In the string and template branches, increment `line` when an escape consumes `\n`. Add tests for all three regex examples, a `/` division (`width / 2`) and line numbers after a continued string.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Qml/QmlDeclarations.cs` (or `Qml/Parsing/QmlLexer.cs` after QML-02), `src/VL.Qt.Tests/QmlDeclarationsTests.cs`
- **Depends on:** none (QML-12's robustness test first is better)
- **Verification:** new cases in `QmlDeclarationsTests`; `help/Qt/qml/*.qml` still parse (the existing `ExampleFile_…` tests)

### QML-02 Cut `QmlDeclarations.cs` into records, lexer, head reader, member parser and literals
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Qml/QmlDeclarations.cs:1-1116` (ten types, two `namespace VL.Qt.Qml` blocks at `:12` and `:183`)
- **Finding:** The file is a hand-written parser for the root declarations of a QML file. It has four stages that share nothing but the token list: a lexer (`:337-513`), a head-tag reader (`:517-587`), a member scanner with inheritance (`:224-333`, `:591-872`), and literal evaluation with the colour table (`:876-1074`). It also holds the seven contract records and enums that the factory, the layout and the instance use (`:14-180`), and the signature for invalidation (`:1078-1114`). A reader who wants to know what a `QmlPropertyDecl` is lands in a 1,100-line file. A reader who has to fix the lexer must scroll past the contract. The second `namespace` block exists only to separate the two halves. Nothing in the code is wrong because of the size, but the file cannot be held in one's head.
- **Proposal:** Use the file layout in section 4: one file per record and enum under `Qml/`, and the parser under `Qml/Parsing/`:
  - `QmlLexer` (internal static; `Lex(string, bool headOnly) → QmlLexed`, with `Token`, `TokenKind` and `Comment` as nested internal types)
  - `QmlHeadReader` (`Read(comments, endLine, warnings) → QmlFileHead`, `SkipHeader`, `HeadEndLine`)
  - `QmlDeclarationParser` (`Parse`, `Inherit`, `ScanMember`, `Continues`, `ReadMember`, `ReadProperty`, `Reassign`, `ReadSignal`, `SummaryAbove`)
  - `QmlLiteral` (`TryEvaluate(kind, tokens, s, e, out value)`, `TryNumber`, `TryQtCall`, `TryColor`, the named-colour table)

  Move `Signature` and `Format` from the parser into `QmlDeclarations` itself: they describe the record, not the parsing. Keep the entry points `QmlDeclarations.Parse` and `QmlDeclarations.ParseHead`.
- **Decided:** P14, S1 → this issue is the K3 cut (one type per file) of the file and rewrites its K1/K4 comments, not wave 3, and the wave-4 closing check (no file with two types beyond the named exceptions) holds for it; `Qml/Parsing/` keeps the namespace `VL.Qt.Qml` (S1, rule 2); QML-12 is a wave-1 test and goes first.
- **Effort:** M, mechanical: yes (moves only)
- **Touches:** `src/VL.Qt/Qml/QmlDeclarations.cs`, new files under `src/VL.Qt/Qml/` and `src/VL.Qt/Qml/Parsing/` (section 4)
- **Depends on:** QML-12 (net first)
- **Verification:** `dotnet test src/VL.Qt.Tests --filter QmlDeclarationsTests`, unchanged

### QML-03 Share the folder-node-factory skeleton with the shader factory; register via VL.Core's `forPath` overload
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Qml/QmlNodeFactory.cs:64-72`, `:79-116`, `:119-125`, `:199-209`; `src/VL.Qt/Qml/QmlNodeDescription.cs:52-64`; `src/VL.Qt/Qml/QmlNodeLayout.cs:98-122`, `:214`; `src/VL.Qt/Qml/QmlNodeInstance.cs:9` (`using VL.Qt.Shaders`); counterparts `src/VL.Qt/Shaders/ShaderNodeFactory.cs:74-125`, `:225-245`, `ShaderNodeDescription.cs:136`, `ShaderPinModel.cs:282`
- **Finding:** `QmlNodeFactory` follows the VL.Stride pattern: `EffectShaderNodes.Init` in `VL.Stride.Runtime/src/Rendering/Effects/EffectShaderNodes.cs:43-91` uses `forPath`, `WatchDir(path)` for the folder itself, a recursive `WatchDir` for new, deleted and renamed files, and `NewFactoryImpl(nodes, invalidated)`. That is correct. But it does so by copying `ShaderNodeFactory`:
  - `ForPath` is the same 35 lines with other names.
  - `GetNodeName` is identical.
  - `OpenEditor` is identical, including the catch of every `Exception` at `QmlNodeDescription.cs:59`.

  The QML layer also takes `ShaderPinDescription`, `ValuePin<T>` and `ShaderPinModel.PinName` from `Shaders/`. So a reader of the QML factory needs the shader factory to understand it, and a change to shader pin naming silently changes QML pin names.

  `Register` + `Init` (`QmlNodeFactory.cs:64-72`) re-implement `NodeBuilding.RegisterNodeFactory(this AppHost, string identifier, Func<string, IVLNodeDescriptionFactory, FactoryImpl> forPath)` from `VL.Core/src/NodeFactory/NodeBuilding.cs:51-56`, which does the same `NodeFactoryCache.GetOrAdd` + `NewFactoryImpl(forPath: …)`. `ChannelHub.NodeBuilding.cs:26` writes it out by hand only because it also has nodes of its own.
- **Proposal:** A new folder `src/VL.Qt/NodeFactories/` (namespace `VL.Qt.NodeFactories`, all `internal`):
  - `FilePinDescription` (renamed from `ShaderPinDescription`)
  - `ValuePin<T>` (moved)
  - `PinNames.FromIdentifier(string)` (moved from `ShaderPinModel.PinName`)
  - `FileNodeNames.Split(string fileName, string extension) → (string Name, string? Version)`
  - `FileEditor.Open(string path, ILogger logger)`, catching `Win32Exception`, `InvalidOperationException` and `FileNotFoundException`
  - `FolderNodeFactory.ForPath<TFile>(string path, string folderName, Func<string, ImmutableArray<TFile>> scan, Func<TFile, IVLNodeDescription> describe, Func<FileSystemEventArgs, bool> isNewOrDeleted, Func<TFile, IObservable<object>> moved, Func<TFile, string?> duplicateError, ILogger logger) → NodeBuilding.FactoryImpl`

  `QmlNodeFactory.Register` becomes `appHost.RegisterNodeFactory(Identifier, (path, f) => ForPath(path, f, logger))`, and `Init` is deleted. Its only caller is `Register`; the tests call `ForPath`.
- **Decided:** D6, P22, O5, TARGET 2.2 → one issue with SHD-10 (QML-03 = SHD-10), one shared folder-node-factory layer for both factories; the files are `NodeFactories/FolderNodeFactory.cs`, `FileNodeNames.cs`, `FileEditor.cs`, `PinNames.cs`, `FilePinDescription.cs` and `ValuePin.cs`, namespace `VL.Qt.NodeFactories`, as proposed; the shader files it touches carry the O5 names (`ShaderLinkNode.cs` and `ShaderNodeLayout.cs` are deleted by the shader cut, the `ShaderLink*` types become `ShaderEffect*`).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Qml/QmlNodeFactory.cs`, `QmlNodeDescription.cs`, `QmlNodeLayout.cs`, `QmlNodeInstance.cs`; `src/VL.Qt/Shaders/ShaderNodeFactory.cs`, `ShaderNodeDescription.cs`, `ShaderPinModel.cs`, `ShaderNodeLayout.cs`, `ShaderLinkNode.cs`, the files of `ShaderPinDescription` and `ValuePin`; new `src/VL.Qt/NodeFactories/*.cs`; tests that name the moved types (`QmlNodeFactoryTests.cs`, `EffectTemplateShadersTests.cs`, `ShaderLinkNodeTests.cs`)
- **Depends on:** the shader review's findings on the same files (coordinate: one issue for both factories)
- **Verification:** `QmlNodeFactoryTests`, the shader factory tests; `help/Qt/HowTo Create QML Nodes from Files.vl` and a shader help patch compile with `tools/vvvvc.ps1 -File`

### QML-04 Define the common pins of `QmlElement` and the generated nodes once
- **Kind:** api
- **Severity:** should
- **Breaking:** no (help texts of the generated nodes change)
- **Where:** `src/VL.Qt/Nodes/QmlElementNode.cs:84-117`; `src/VL.Qt/Qml/QmlNodeLayout.cs:85-122`; `src/VL.Qt/Qml/QmlNodeInstance.cs:41-55`, `:87-100`, `:120-122`
- **Finding:** "The common pins are exactly those of `QmlElement`" (`docs/user-qml.md`, `QmlNodeLayout.cs:57`). That promise is kept by hand in four places:
  1. The parameters and XML docs of `QmlElementNode.Update`.
  2. `s_commonInputs` and `s_trailingOutputs` with their own help texts.
  3. `CommonInputNames` and `ReservedPinNames`, which repeat the names.
  4. The ten `ValuePin` fields of `QmlNodeInstance`, with their own defaults, and their order in `_inputs`/`_outputs`.

  Names, defaults, visibility, order and help must agree in all four. The help has already drifted:
  - `transform` in `QmlElementNode.cs:88` says "Inside Row/Column the layout places the element", and `QmlNodeLayout.cs:106` does not.
  - The same holds for `anchor` (`:89` vs `:107`), `opacity` (`:90` vs `:108`), `debug` (`:92` vs `:110`) and `effect` (`:94` vs `:112`).

  A person adding a common pin to `QmlElementNode` gets no compile error and no test failure on the generated nodes until a link breaks. `CommonInputNames` is used only by a test (`QmlNodeFactoryTests.cs:271`).
- **Proposal:** New `src/VL.Qt/Qml/QmlCommonPins.cs` (internal static):
  - `record CommonPin(string Name, Type Type, object? Default, bool IsVisible, string Summary)`
  - `ImmutableArray<CommonPin> Inputs`, `ImmutableArray<CommonPin> TrailingOutputs`, `CommonPin Output`
  - `ImmutableHashSet<string> ReservedNames`, derived from the arrays plus `"Properties"`

  `QmlNodeLayout` builds its `FilePinDescription`s from the table. `QmlNodeInstance` keeps its typed `ValuePin` fields but initialises their defaults from the table. A unit test checks the table against `QmlElementNode.Update` by reflection: name after vvvv's pin naming (or the `[Pin(Name)]`), type, `[DefaultValue]`, and `PinVisibility.Optional` versus `IsVisible`. Copy the help texts from the XML docs of `QmlElementNode.Update`, which are the longer, correct ones. Delete `CommonInputNames`. Shorten the 20-line summary of `QmlNodeLayout` (`:48-67`) to two sentences and a link to `docs/user-qml.md#which-declarations-give-which-pins`, which already lists the pins.
- **Decided:** C2, catalog → the table has `Enabled` (optional, default true, the last input pin, C2) next to the other common pins, and the reflection test compares it with `QmlElement.Update` as it is after the catalog: pin order `Auto Size, Size, Transform, Anchor, File, Properties, …`, `Size` optional, category `Qt.Elements.Qml`; whether the generated nodes follow that order is not decided.; A3 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the generated QML nodes lose their `Error` output
- **Effort:** S, mechanical: no
- **Touches:** new `src/VL.Qt/Qml/QmlCommonPins.cs`, `src/VL.Qt/Qml/QmlNodeLayout.cs`, `src/VL.Qt/Qml/QmlNodeInstance.cs`, `src/VL.Qt.Tests/QmlNodeFactoryTests.cs`
- **Depends on:** QML-03 (pin description type name)
- **Verification:** new reflection test; `Pins_MatchTheDescriptions` and `Layout_PinsFollowDeclarations_CommonPinsAround`

### QML-05 Hot reload of generated nodes: one parse per save, no rebuild from a half-written file
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Qml/QmlNodeFactory.cs:104-105`, `:177-188`, `:223-252`; `src/VL.Qt/Qml/QmlNodeDescription.cs:34-38`; `src/VL.Qt/Qml/QmlNodeLayout.cs:151-174`
- **Finding:** Every description subscribes `WatchSignature` on the folder of its file, and the factory also subscribes `WatchContent(file).Where(HeadChanged)` for every file. Any event on any `*.qml` of a folder with N component files therefore does the following on the `FileSystemWatcher` thread:
  - N runs of `CurrentSignature`, each a full `QmlNodeLayout.Load`: read, parse, sibling reads, and a pin layout built and thrown away just to read `Declarations.Signature` (`:246`).
  - Up to N `HeadChanged` reads. `WatchContent` only filters its own file name, so a save costs one head read for its own file.

  Editors fire two or three `Changed` events per save, so a save costs about 3·N full parses. That is acceptable for five files, but noticeable in a `qml/` folder with 50 components.

  The readability probe at `:243-245` is check-then-act: the file can become unreadable between the probe and `File.ReadAllText` in `Load`. `Load` then returns empty declarations with "cannot be read", and the signature differs. The same holds when the event arrives while the editor is still writing: a truncated file parses with an error. Either case invalidates the description.

  The init (`QmlNodeDescription.cs:37`) has no protection at all. If vvvv runs it while the file is locked or half-written, the node is rebuilt with only the common pins. Suspicion, not confirmed: vvvv then drops the links on the vanished property pins. To confirm, lock the file (`FileShare.None`) or truncate it while saving, in a patch with linked property pins.
- **Proposal:**
  1. New `QmlNodeLayout.ReadDeclarations(QmlFile file) → QmlDeclarations?`: parse only, null on an `IOException` or `UnauthorizedAccessException`. `CurrentSignature` uses it instead of `Load`, and the probe at `:243-245` goes.
  2. In `WatchSignature`, add `.Throttle(TimeSpan.FromMilliseconds(150))` before the comparison, so one save means one parse per description.
  3. `QmlNodeDescription.New` keeps the last good layout in its closure (`QmlNodeLayout? lastGood`). When `ReadDeclarations` returns null at init, or the result has an error while `lastGood` had none and the file is younger than 1 s, init uses `lastGood` with an added warning "file could not be read, showing the last pins". The next event corrects it.
  4. Fold `HeadChanged` into the same throttled stream.
- **Decided:** E2, D6 → keep the last good pins and values while the file does not read or parse, for QML and shaders alike (E2 (a)); the rule is written once in the shared folder-node-factory layer (D6: QML-03 = SHD-10), not in `QmlNodeDescription` alone; the issue compares with VL.Stride's TextureFX; the error shows as a node message and in the log; the 1 s age condition of step 3 is not covered by E2.; C11 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the last good pins stay on every read or parse error, as SHD-05 for shaders, with the throttle; the condition "younger than 1 s" goes
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Qml/QmlNodeFactory.cs`, `src/VL.Qt/Qml/QmlNodeDescription.cs`, `src/VL.Qt/Qml/QmlNodeLayout.cs`, `src/VL.Qt.Tests/QmlNodeFactoryTests.cs`
- **Depends on:** none
- **Verification:** unit test: `WatchSignature` with a subject that fires three events gives one comparison; init with a locked file keeps the last pins. Manual check in vvvv: save a component with linked pins 20 times quickly, and no link is lost.

### QML-06 A new instance of a changed file may get the stale compiled component (suspicion)
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/QmlElementNode.cs:38`, `:122-127`; `src/VL.Qt.Native/qmlitem.cpp:358-360`, `:457-459`
- **Finding:** `QQmlEngine` caches compiled components by URL (Qt's "component cache") and does not look at the file on disk again until `clearComponentCache()` is called. QtNative clears it only when the reload counter of an existing item changes (`qmlitem.cpp:358`, `:459`). A new `QmlElementNode` starts with `_reload = 0`, and a new item treats counter 0 as already applied. So a new item for a file that changed on disk loads the cached old version.

  The generated nodes are where this can happen. After a declaration change, vvvv rebuilds the description and creates a new `QmlNodeInstance` with a fresh `QmlElementNode` (reload 0). This is correct only if the old instance ran one more `Update` after the save, bumped its counter and so cleared the cache. That holds when the old node draws in a running renderer. It does not hold when the old element is not under a rendering window at that moment, for example a disconnected `Group` or a closed renderer window. The new pins then name properties the cached component does not have, and `Error` reports them as unknown. Not confirmed by a run.
- **Proposal:** Confirm with a SmokeTest scenario. Load `A.qml` in item 1. Rewrite `A.qml` with a new property. Create item 2 with the same Source and Reload 0, without bumping item 1. Check whether item 2 sees the property. If it is real, fix it in QtNative: `QmlItem` keeps, per engine, a map from URL to the `QFileInfo::lastModified()` at the last compile. `rebuild` clears the cache when the file on disk is newer. The reload counter then stays the trigger for imported files only.
- **Decided:** wave 1 of PLAN-CHECK, F5, S2 → the suspicion is confirmed or dropped in wave 1 by the SmokeTest scenario of the proposal, written as an NUnit test (F5), before an issue is cut; the native file is `user_qml/qml_item.cpp/.h` (S2, TARGET 2.3).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/qmlitem.cpp`, `qmlitem.h`, `src/VL.Qt.SmokeTest/QmlTest.cs`
- **Depends on:** none
- **Verification:** the new SmokeTest scenario

### QML-07 `QmlProperties.Matches` keeps a stale `Problems` text when a rejected entry changes
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/QmlProperties.cs:122-137`, `:86-91`
- **Finding:** `Matches` compares the valid entries exactly but counts only the rejected ones (`invalid == _invalid`). If a patch renames a rejected key (`"colr": someObject` → `"colour": someObject`) or replaces one unsupported value with another (null → a `Spread`), the count stays the same. `Update` keeps the old snapshot, and `Error` keeps naming the old key or the old reason. The user corrects the dictionary and the message does not follow, which is misleading.
- **Proposal:** Store the rejected entries in the snapshot: `readonly KeyValuePair<string, object?>[] _rejected`, filled in `From`. In `Matches`, look each rejected entry of the source up in `_rejected` by name with `Equals(value)`. This is a linear search over a list that is almost always empty, so there is no allocation. Remove `_invalid`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Elements/QmlProperties.cs`, `src/VL.Qt.Tests/QmlTests.cs`
- **Depends on:** none
- **Verification:** new test in `QmlTests.cs`: rename a rejected key, and `Problems` names the new key

### QML-08 `required property`: create the root with its initial properties
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no (a file that failed now loads)
- **Where:** `src/VL.Qt/Qml/QmlNodeLayout.cs:25-26` (`AlwaysSend`); `src/VL.Qt/Qml/QmlNodeInstance.cs:240`; `src/VL.Qt.Native/qmlitem.cpp:482` (`component.create()`); `docs/user-qml.md` "Limitations"
- **Finding:** The generated node promises that a `required property` "is sent from the first frame", and the parser, layout and instance implement the flag. QtNative creates the root with `QQmlComponent::create()` and sets properties afterwards. Qt refuses a required property that has no value at creation, so every such file fails with an error. The flag makes the pin say something the node cannot keep. The docs list it as a known limitation, which the user can read but cannot act on, other than removing `required`.
- **Proposal:** In `QmlItem::rebuild`, build a `QVariantMap` from the current property values and call `component.createWithInitialProperties(map)`. That Qt function sets the given properties before the object finishes creation, so required properties are satisfied. Then apply the rest as now. This needs the properties of the frame to reach QtNative before or with `QML_SOURCE`/`QML_RELOAD`. Check the order in which `QmlPropertyWriter` and the source command are written in one frame. Remove the limitation from `docs/user-qml.md` and `docs/decisions/user-qml.md`.
- **Decided:** E3, S2, G5 → fix natively with `createWithInitialProperties`, as proposed; the native file is `user_qml/qml_item.cpp/.h` (S2), the C# file `Reconciliation/QmlPropertyWriter.cs`, and the design note is edited under `docs/design/` (G5, was `docs/decisions/`).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/qmlitem.cpp`, `qmlitem.h`, possibly `src/VL.Qt/Reconciler/QmlPropertyWriter.cs`, `src/VL.Qt.SmokeTest/QmlTest.cs`, `docs/user-qml.md`, `docs/decisions/user-qml.md`
- **Depends on:** none
- **Verification:** SmokeTest scenario with `required property real value` set from the first frame: no error, and the value is drawn

### QML-09 Sibling inheritance: self-reference and unsupported redeclaration
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Qml/QmlDeclarations.cs:171`, `:261-262`, `:302-307`, `:764-767`, `:780-784`; test `src/VL.Qt.Tests/QmlDeclarationsTests.cs:545-559`
- **Finding:**
  - `Parse` starts with an empty chain (`:171`). For `Gauge.qml` whose root is `Gauge { }`, `ReadSibling` returns the file itself. Its declarations are inherited from itself and then replaced in place, and the message is a warning about a "cycle". QML rejects the file as recursive instantiation, so the node gets pins and a misleading warning. The test `SelfReference_…_ReadNoSibling` feeds a different text for "Self" (`:556`), does not cover the real case, and its name says the opposite of what happens.
  - A derived file that redeclares an inherited property with a type that gets no pin (`property var level` over the base's `property real level`) returns at `:764-767` before the replacement at `:780-784`. The node keeps the inherited `float` pin for a property that is now `var`.
- **Proposal:**
  - `QmlDeclarations.Parse(string source, string ownTypeName, Func<string, string?> readSibling)` seeds the chain with `ownTypeName`. `QmlNodeLayout.Load` passes `Path.GetFileNameWithoutExtension(file.FilePath)`. A root type equal to `ownTypeName` gives the error "'Gauge.qml' cannot have its own type 'Gauge' as root type".
  - In `ReadProperty`, remove an inherited entry of the same name before returning with the info message.
  - Rename the test after what it checks.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Qml/QmlDeclarations.cs`, `src/VL.Qt/Qml/QmlNodeLayout.cs`, `src/VL.Qt.Tests/QmlDeclarationsTests.cs`
- **Depends on:** QML-02 if done after it
- **Verification:** `QmlDeclarationsTests` with the real self case and the redeclaration case

### QML-10 Messages a user can act on: line numbers, lost read error, plan-B wording
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Qml/QmlDeclarations.cs:32-36`, `:1093-1094`; `src/VL.Qt/Qml/QmlNodeLayout.cs:165`, `:208`; `src/VL.Qt/Qml/QmlNodeLayout.cs:99`, `docs/user-qml.md` ("A pin of the same name wins"); `src/VL.Qt/Qml/QmlNodeInstance.cs:174-178`
- **Finding:**
  - The node messages show line numbers (`QmlParseMessage.ToString`), but the signature leaves them out on purpose. After lines are inserted above a declaration, the info "line 12: property 'model' of type 'var'" points to the wrong line until some declaration changes.
  - `Load` passes `file.DuplicateError ?? readError`, so a read error of a duplicate file is lost.
  - The docs and the `Properties` help say a typed pin wins over a dictionary entry of the same name. The code lets the pin win only once it has been sent (`IsSent`). While the pin still holds its default, the dictionary entry is sent.
- **Proposal:**
  - Infos list names only ("property 'model' (var)"); errors and warnings keep the line, and their line goes into the signature (they are rare, a rebuild then costs nothing).
  - Join both extra errors.
  - Reword the help: "A pin of the node wins over an entry with the same name once the pin differs from its default."
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Qml/QmlDeclarations.cs`, `src/VL.Qt/Qml/QmlNodeLayout.cs`, `docs/user-qml.md`, `src/VL.Qt.Tests/QmlDeclarationsTests.cs`
- **Depends on:** none
- **Verification:** `Signature_*` tests updated; `Layout_*` test for the joined errors

### QML-11 Remove unused members
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Qml/QmlTypes.cs:93-106` (`WireType`); `src/VL.Qt/Qml/QmlNodeFactory.cs:211-217` (`WatchSignature(QmlFile, string)`); `src/VL.Qt/Nodes/QmlFileWatcher.cs:33-34` (`Path`); `src/VL.Qt/Qml/QmlDeclarations.cs:152-153` (`HasErrors`)
- **Finding:** These members have no caller in production code:
  - `WireType` has no caller at all. The search hits in `QmlTests.cs:143` and `:312` are test method names.
  - The `string` overload of `WatchSignature` appears only in `cref`s; the description and the test use the `Func` overload.
  - `QmlFileWatcher.Path` has no caller.
  - `HasErrors` is used only by tests.

  The `cref`s at `QmlNodeDescription.cs:15` and `QmlNodeFactory.cs:76` and `:265` point to the unused overload.
- **Proposal:** Delete `WireType`, the `string` overload of `WatchSignature` (point the `cref`s to the remaining one) and `QmlFileWatcher.Path`. Replace `HasErrors` in the tests with `d.Messages.Any(m => m.Severity == QmlMessageSeverity.Error)`, or keep it: one line, and it reads well. Recommendation: keep `HasErrors`, delete the rest.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Qml/QmlTypes.cs`, `src/VL.Qt/Qml/QmlNodeFactory.cs`, `src/VL.Qt/Qml/QmlNodeDescription.cs`, `src/VL.Qt/Nodes/QmlFileWatcher.cs`
- **Depends on:** none
- **Verification:** build and `dotnet test src/VL.Qt.Tests`

### QML-12 Tests: a robustness net for the parser, and the test hooks of the area
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Tests/QmlDeclarationsTests.cs`. Test hooks (K6):
  - `src/VL.Qt/Nodes/QmlElementNode.cs:51-62` (two constructors), `:81` (`Core`)
  - `src/VL.Qt/Qml/QmlNodeInstance.cs:71-72`
  - `src/VL.Qt/Nodes/QmlFileWatcher.cs:30-31`, `:36-37` (`IsWatching`)
  - `src/VL.Qt/Qml/QmlNodeFactory.cs:131-132` (`Scan(path, parseHead)`)
- **Finding:** The parser tests are good example-based tests: literals, modifiers, summaries, head tags, inheritance, errors and the signature. They miss two kinds of case:
  - The claim that `Parse` and `ParseHead` never throw is tested on a single broken file (`HeadWithoutTags_IsEmpty_AndParseHeadNeverThrowsOnBrokenFiles`). The split of QML-02 and the lexer change of QML-01 rewrite exactly the index arithmetic (`ScanMember`, `TryQtCall`, `ReadProperty`'s `list<` loop) where an off-by-one throws.
  - Regex literals, `component Name: Item { }` inline components, `enum` at the root, `0b`/`0o` numbers and line numbers after escapes have no test.

  Of the test hooks, only `QmlElementNode`'s constructors follow a repository-wide pattern (`ElementNode` has the same `addWarning` constructor). The others exist for one test file each.
- **Proposal:**
  - Add `Parse_NeverThrows_OnAnyPrefixOrSuffix`: for each example file in `help/Qt/qml/` and the test fixtures, call `Parse(source[..i])` and `Parse(source[i..])` for every `i`, and also `ParseHead`, and assert that no exception is thrown and that there is a result or an error message. It runs in milliseconds. Add the cases listed above.
  - Hooks:
    - `Scan(path, parseHead)`: the tests write real head tags into their temp files (they already create files), so delete the overload.
    - `IsWatching`: the tests check `Subject.HasObservers` of the subject they pass in, so delete the property.
    - `QmlNodeInstance`: keep a single constructor `QmlNodeInstance(QmlElementNode element, QmlNodeLayout layout)` and let `QmlNodeDescription` pass `new QmlElementNode(c.NodeContext)`, so the constructor is no longer test-only.
    - `QmlElementNode`'s constructors and `Core`: follow the decision on `ElementNode` (K6).
- **Decided:** wave 1 of PLAN-CHECK, P22 → the parser net (`Parse_NeverThrows_OnAnyPrefixOrSuffix` and the listed cases) is a wave-1 test; the hook part (constructors and `Core` of `QmlElementNode`) follows NODE-06, which is worked in the issue of TEST-07 with the other K6 findings.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Tests/QmlDeclarationsTests.cs`, `QmlNodeFactoryTests.cs`, `QmlTests.cs`, `QmlNodeInstanceTests.cs`; `src/VL.Qt/Qml/QmlNodeFactory.cs`, `QmlNodeInstance.cs`, `QmlNodeDescription.cs`; `src/VL.Qt/Nodes/QmlFileWatcher.cs`
- **Depends on:** none (do it before QML-01 and QML-02)
- **Verification:** the new test passes on the current parser; the hook removals keep all `Qml*` tests green

## 4 Target structure

`src/VL.Qt/Qml/` (namespace `VL.Qt.Qml`, all `internal`)

| File | Type, responsibility | Change |
|---|---|---|
| `QmlDeclarations.cs` | record `QmlDeclarations`: what the parser found; `Parse(source, ownTypeName, readSibling)`, `ParseHead`, `Signature`, `HasErrors` | split from `QmlDeclarations.cs`; `Signature`/`Format` moved in from the parser |
| `QmlPropertyDecl.cs` | record: one property that gets pins | split |
| `QmlPropertyFlags.cs` | enum: readonly, required, default | split |
| `QmlInitializer.cs` | enum: none, literal, binding | split |
| `QmlSignalDecl.cs` | record: one signal | split |
| `QmlSignalArg.cs` | record: one signal argument | split |
| `QmlParseMessage.cs` | record: one parser message | split |
| `QmlMessageSeverity.cs` | enum | split |
| `QmlFileHead.cs` | record: `@category`, `@summary`, `@tags` | split |
| `Parsing/QmlLexer.cs` | tokens and comments of a file; nested `Token`, `TokenKind`, `Comment` | split from `QmlDeclarationParser`; regex literals (QML-01) |
| `Parsing/QmlHeadReader.cs` | head tags before the root object | split |
| `Parsing/QmlDeclarationParser.cs` | root object, members at depth 1, sibling inheritance | split, smaller |
| `Parsing/QmlLiteral.cs` | literal initializers → pin defaults, SVG colour names | split |
| `QmlValueKind.cs` | enum: the value kinds that get a pin | split from `QmlTypes.cs` |
| `QmlSignalShape.cs` | enum: bang, values, signals | split from `QmlTypes.cs` |
| `QmlTypes.cs` | QML type ↔ kind ↔ CLR type, defaults, coercion | `WireType` deleted |
| `QmlFile.cs` | record: one scanned component file | split from `QmlNodeFactory.cs` |
| `QmlNodeFactory.cs` | scan of `qml/`, watchers, registration through `AppHost.RegisterNodeFactory(id, forPath)` | `Init` deleted; skeleton moved to `NodeFactories/` |
| `QmlNodeDescription.cs` | one node description per file; keeps the last good layout | QML-05 |
| `QmlNodeLayout.cs` | pins of one generated node | summary shortened; common pins from `QmlCommonPins` |
| `QmlInputPin.cs`, `QmlOutputPin.cs`, `QmlSignalPin.cs` | records: one pin each | split from `QmlNodeLayout.cs` |
| `QmlCommonPins.cs` | the common pins of `QmlElement` and the generated nodes, one table | new (QML-04) |
| `QmlNodeInstance.cs` | running node: typed pins → dictionary → `QmlElementNode` → typed outputs; private nested slot and output classes stay | one constructor |

`src/VL.Qt/NodeFactories/` (namespace `VL.Qt.NodeFactories`, `internal`), new and shared with `Shaders/` (QML-03):

| File | Type, responsibility | Change |
|---|---|---|
| `FolderNodeFactory.cs` | `ForPath` skeleton: folder watch, scan, duplicates, invalidation | new, from both factories |
| `FileNodeNames.cs` | `"Gauge_Round.qml"` → (`Gauge`, `Round`) | from both `GetNodeName` |
| `FileEditor.cs` | open a file in the default editor | from both `OpenEditor` |
| `PinNames.cs` | `accentColor` → "Accent Color" | from `ShaderPinModel.PinName` |
| `FilePinDescription.cs` | `IVLPinDescription` of a generated pin | `ShaderPinDescription` → `FilePinDescription`, moved |
| `ValuePin.cs` | `IVLPin` holding a value | moved from `Shaders/` |

`src/VL.Qt/Nodes/` (namespace `VL.Qt.Nodes`):

| File | Type, responsibility | Change |
|---|---|---|
| `QmlElementNode.cs` | `public` node `QmlElement` | unchanged apart from K1, K5, K6 |
| `QmlFileWatcher.cs` | hot-reload flag for one file | `Path` and `IsWatching` deleted |

`src/VL.Qt/Elements/` (namespace `VL.Qt.Elements`):

| File | Type, responsibility | Change |
|---|---|---|
| `QmlElement.cs` | `public` element type | unchanged |
| `QmlSignal.cs` | `public` record, pin type | unchanged |
| `QmlEvents.cs` | decoding of `EV_QML_PROPERTY` and `EV_QML_SIGNAL` | unchanged |
| `QmlValue.cs` | one wire value | split from `QmlProperties.cs` |
| `QmlProperties.cs` | converted dictionary snapshot | QML-07 |

## 5 Questions for Sebastian

1. **Keep the hand-written QML declaration parser?**
   - Alternatives:
     - (a) Keep it and fix the lexer gaps (QML-01).
     - (b) Read the declarations through Qt's own tools: `qmllint --json` or the `QmlDom` library, Qt's model of a QML file. This ships an extra Qt tool or a private Qt library, and it runs a process at node-browser time.
     - (c) Ask QtNative. The QmlWatcher in `qmlitem.cpp` already lists the root's declared properties at runtime, but only once a window and engine exist, which is not true in the node browser.
   - Consequences: (b) is exact QML grammar, but brings a deployment and licence question and a slow first browse. (c) cannot give pins before a renderer runs.
   - Recommendation: (a). Later, as a cheap cross-check, let the node warn when the runtime property list of QtNative differs from the parsed one.
2. **One shared folder-node-factory layer for shaders and QML (QML-03)?**
   - Alternatives: (a) extract it now, which touches the shader area too; (b) leave the two copies and only move the pin types out of `Shaders/`.
   - Recommendation: (a), as one issue that the shader review and this one share. The two copies are already 90 % identical, and the next generated-node kind would be a third.
3. **`required property` (QML-08): fix natively for 0.10.0, or drop the "always sent" promise and the flag's special case?**
   - Fixing needs `createWithInitialProperties` in QtNative and a check of the command order.
   - Dropping it is a docs change plus removing `AlwaysSend`; the files still fail as before.
   - Recommendation: fix. It is a few lines in `QmlItem::rebuild`, and otherwise the pin's help promises something false.

## 6 For other areas

- **Shaders:** `ShaderNodeFactory.ForPath`, `GetNodeName` and `ShaderNodeDescription.OpenEditor` (catches every `Exception`) are duplicated in the QML factory. See QML-03 (`src/VL.Qt/Shaders/ShaderNodeFactory.cs:74-125`).
- **API (01):** `QmlElementNode.Update` uses `[DefaultValue("1, 1")]` without the type form for `Vector2 size`, and `Matrix transform` has no explicit default (`src/VL.Qt/Nodes/QmlElementNode.cs:104`, `:106`). AGENTS.md asks for `[DefaultValue(typeof(Vector2), "1, 1")]`. Check across all element nodes.
- **Native / QtNative:** the component cache is cleared only on a reload bump, so a new item can get a stale component (`src/VL.Qt.Native/qmlitem.cpp:358`, `:457-459`). See QML-06.
- **Upstream VL.Core (suspicion):** `FileSystemUtils.WatchDir` caches `…Catch(Observable.Empty).Publish().RefCount()` per path (`VL.Core/src/FileSystemUtils.cs:42-47`). After a watcher error, such as a watched folder deleted and recreated, the cached observable has completed. Later subscribers to the same path then likely get no events for the rest of the process. To confirm, delete and recreate a watched `qml/` folder and save a file. This would affect both node factories and `QmlFileWatcher`.
