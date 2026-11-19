# Review 08 – Wire protocol between C# and QtNative

Reviewer: Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/protocol.h`, `src/VL.Qt/Protocol.cs`, `src/VL.Qt/CommandWriter.cs`, `src/VL.Qt/EventReader.cs`, `src/VL.Qt/Reconciler/ICommandSink.cs`, `src/VL.Qt.SmokeTest/ProtocolDrift.cs`. Skimmed (excerpts): `src/VL.Qt.Native/commands.cpp` (apply loop 330–455, `valueSize` 65–113, `set` 678–778, `setStyle`/`setUniforms` 780–875, `setEffectChain` 1006–1135, `poll`/`pushEvent` 1260–1288), `events.cpp` (caps 746–851, EV_ERROR 1004), `elements.h` 40–53, `SceneCore.cs` 95–160 and 245–268, `UniformWriter.cs` 80–130, `QmlPropertyWriter.cs` 20–65, `SinkRegistry.cs` 140–190, `WindowPlacement.cs` 123–130. Not read: `events.h`, `vlqt.h` beyond a search, the tests (searched only).

## 1 Summary

The protocol is a compact, well-checked binary format: 4-byte header, explicit little-endian, 4-byte alignment, fixed opcode/property/value-type tables, and a native decoder that validates every length before it touches the scene. The C# writer does not allocate per frame, and a drift check compares every constant of `protocol.h` with `Protocol.cs`. The weak points are around that core:

1. **No version check.** `VLQT_PROTOCOL_VERSION` and `Protocol.ProtocolVersion` exist but nobody reads them. A stale or mismatched `vlqt.dll` is not detected (PROTO-01).
2. **The 16-bit payload length is a user-visible limit that throws.** A Text, TextField, Video source or QML string over about 64 KB throws from the reconciler. Several caps and chunking schemes exist only because of this limit (PROTO-02).
3. **`protocol.h` is a 245-line design note.** It mixes the wire format with element semantics, C ABI constants and test-only constants. `Protocol.cs` repeats parts of that prose, and the copies already disagree (PROTO-04, PROTO-05, PROTO-06).
4. **`CommandWriter`'s begin/end pairs are not enforced.** A plain command can be written inside an open compound command, compounds can nest, and `Reset` does not close them (PROTO-03).
5. **The hand mirror is sound but incomplete.** It covers constants, not layouts or apply result codes, and it runs only in the SmokeTest (PROTO-07, PROTO-08).

## 2 What works and must survive

- **One buffer per window per frame, applied in one call** (`SceneCore.Apply`, `SceneCore.cs:136`). The protocol's batching is what makes the render-thread hand-off cheap. Keep it.
- **Writer is allocation-free in steady state.** It grows a `byte[]` only when needed (`CommandWriter.cs:618`), writes with `BinaryPrimitives`, and gets UTF-8 lengths with `GetByteCount` + `GetBytes` into the span. Hot path: every `Set*` / `Uniform*` / `Effect*` / `Style*` call, every frame, for every changed property. A refactoring must not add boxing, `params`, LINQ or string formatting there. Exception messages may format because they are thrown only on misuse.
- **Reader is a `ref struct` and allocation-free** (`EventReader.cs:13`). Only a malformed buffer allocates its `Error` string. Hot path: `SinkRegistry.Dispatch` and `WindowPlacement` every frame.
- **The native decoder validates before it applies.** Every opcode's length is checked (`commands.cpp:356` `expect`). Variable values use 64-bit arithmetic against overflow (`commands.cpp:696`). `SET_UNIFORMS` and `SET_EFFECT_CHAIN` validate the whole command before they change anything (`commands.cpp:1008`). On a failure, the structured error (`ApplyError{rc, index, id, op, prop, reason}`) drives the C# resync. Keep "validate whole, then apply" for every compound command.
- **"Content problems are not apply errors."** An unknown uniform name or a failed QVariant conversion becomes an `EV_ERROR` line, not a failed batch. This keeps one bad pin from triggering a scene resync. Keep it.
- **No echo.** `EV_VALUE` and `EV_QML_PROPERTY` are not sent for values the host set. The reconciler relies on this.
- **Ids are chosen by C#, 0 is the root.** CREATE/DESTROY/REPARENT are idempotent enough for the resync (RESET + full rebuild).
- **Golden-byte unit tests** pin the C# encoding: `CommandWriterTests`, `EffectChainTests`, `ShaderEffectTests`, `QmlTests`, `SamplerTests` compare hex dumps. They are the net for any re-encoding in PROTO-02/09.

## 3 Findings

### PROTO-01 Check that vlqt.dll and VL.Qt.dll speak the same protocol
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/protocol.h:251`, `src/VL.Qt/Protocol.cs:10`, `src/VL.Qt.Native/vlqt.h` (no version export), `src/VL.Qt/Native.cs`
- **Finding:** `VLQT_PROTOCOL_VERSION 14` and `Protocol.ProtocolVersion = 14` are defined, but a search over `src` finds no reader on either side. `vlqt.h` has no version function. When `vlqt.dll` and `VL.Qt.dll` do not belong together, nothing reports it. This happens with a native build that was not redone after a protocol change (AGENTS.md: "Rebuild QtNative whenever you touch src/VL.Qt.Native"), with a vvvv that still has the old DLL loaded, or with a package mixed from two builds. The symptoms depend on what changed. If an opcode or length changed, every frame fails with -2/-3 and the scene resyncs in a loop. If a property id or enum value was renumbered with the same value type, the wrong property is set silently. The ABI of `vlqt.h` (struct layouts such as `vlqt_scene_metrics` and `vlqt_audio_tap_state`, function signatures) has the same problem.
- **Proposal:** Add `int vlqt_abi_version(void)` to `vlqt.h`. It returns `VLQT_ABI_VERSION`, which replaces `VLQT_PROTOCOL_VERSION` and covers both the command protocol and the C ABI. Before the first window or resource is created, `QtApp` reads the export once. On a mismatch, or when the export is missing (`EntryPointNotFoundException` from an older DLL), it logs one error ("vlqt.dll {path} has ABI {n}, VL.Qt.dll expects {m}: rebuild QtNative or reinstall the package") and puts that text on every Renderer's error output. It creates no windows. In `protocol.h`, document the rule: bump the version on every change to a constant, layout or exported signature. The drift check keeps the two numbers equal.
- **Decided:** B3 → (a), refuse: no windows, the log names both versions and the Renderer node is marked, not an `Error` output (replaces 'puts that text on every Renderer's error output'); P4 step 1 → one of the three checks that come first in wave 3 (with PROTO-08 and CAPI-06); later additions to the protocol raise the version (DECISIONS: the native layer breaks once); B4 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the export is `vlqt_app_get_abi_version` (pattern of CAPI-03); one number `VLQT_ABI_VERSION` for protocol and C ABI replaces `VLQT_PROTOCOL_VERSION`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/protocol.h`, `src/VL.Qt.Native/vlqt.h`, `src/VL.Qt.Native/vlqt.cpp` (or wherever the exports live), `src/VL.Qt/Native.cs`, `src/VL.Qt/QtApp.cs`, `src/VL.Qt/Protocol.cs`
- **Depends on:** none
- **Verification:** new SmokeTest check `Native.vlqt_abi_version() == Protocol.AbiVersion`. Manual: load an older `vlqt.dll` and confirm that the error appears in the vvvv log.

### PROTO-02 Lift the 64 KB command limit (u32 payload length) instead of throwing
- **Kind:** correctness
- **Severity:** must
- **Breaking:** yes: wire format (both sides change together). For patches: long texts and large shapes work instead of throwing. The 8190-point limit of Shape and Path goes away.
- **Where:** `src/VL.Qt.Native/protocol.h:12`, `src/VL.Qt/CommandWriter.cs:587`, `:599-602`, `:171-177`, `:287-292`, `:417-422`, `:539-544`, `src/VL.Qt/Protocol.cs:17-29`, `src/VL.Qt/Reconciler/PropertyDiff.cs:241`, `:268-291`, `:334`, `:364`, `:437`, `src/VL.Qt/Reconciler/QmlPropertyWriter.cs:20-45`, `src/VL.Qt/Nodes/ShapeNodeCore.cs:22`, `src/VL.Qt.Native/events.cpp:748`, `:801`, `:1004`, `src/VL.Qt.Native/commands.cpp:1278`
- **Finding:** The header field `payloadLen` is a u16, so no command can be larger than 65,535 bytes. `CommandWriter.Begin` throws `ArgumentOutOfRangeException` above that. `SetString` has no cap, and no caller caps either: `PropertyDiff.cs:334` (Text content), `:364` (Button text), `:437` (TextField value), `:133`/`:470` (paths). A Text with more than 65,527 UTF-8 bytes, for example a file read into a Text node, therefore throws from `Reconciler.Update`, and the `Reconciler` folder has no `catch`. A single QML string property over the limit throws `InvalidOperationException` from `EndQmlProperties`. Everywhere else, the limit is handled by special code that a newcomer has to understand:
  - `MaxF32ArrayCount` and `PathFits` (`Protocol.cs:19-29`)
  - point truncation in `PropertyDiff.cs:241`, `:268-291`
  - `ShapeNodeCore.MaxPoints`
  - chunking in `QmlPropertyWriter`
  - native caps: `kMaxValueString = 60000`, `kMaxEventPayload = 65000`, and `EV_ERROR` truncated to 4096 bytes
  - `pushEvent`'s `quint16(8 + payloadLen)`, which overflows silently if a caller ever passes more than 65,527 bytes (`commands.cpp:1278`)
- **Proposal:** Change the command header to 8 bytes: `u16 op, u16 flags (0, reserved), u32 payloadLen`. Change the event record header to `u16 event, u16 flags (0), u32 len, u32 handle` (see PROTO-09 for the index). Effect-chain links already have a u32 `bodyLen`. In C#, `Begin` keeps a sanity cap that is a protocol constant (`VLQT_MAX_COMMAND_PAYLOAD`, for example 64 MB) and has no other limit. Then delete `MaxF32ArrayCount`, `PathFits`, the truncation in `PropertyDiff`, `ShapeNodeCore.MaxPoints`, the chunking in `QmlPropertyWriter` and the native string caps, or keep each one only where it protects something other than the header field. Keep the u16 counts inside the compound commands (entries, links): 65,535 uniforms per command is not a real limit. If Sebastian keeps u16 (question Q1), the minimum fix is that `SetString` truncates to the payload on a UTF-8 boundary and the element shows a warning. It must never throw from the reconciler.
- **Decided:** A4 → (a): `u32` length in command and event headers (the 8-byte command header, and `u16 event, u16 flags, u32 len, u32 feedbackId` for events with O7 and PROTO-09), on the condition of P8 (median of five runs, limit 3 % of the apply time and never below the spread of the runs before; a failed pair is measured once more, then the issue stops and shows the numbers); fallback: `u32` for events only, then `u16` as today, where the 'if Sebastian keeps u16' minimum fix applies; P4 step 3 → the header change is part of the wire-format pull request with PROTO-09, B5, REC-13 and the measurement; P4 step 4 → deleting `MaxF32ArrayCount`, `PathFits`, the truncation in `PropertyDiff`, `ShapeNodeCore.MaxPoints`, the QML chunking and the native string caps is a pull request after it; B7 of [DECISION-ROUND.md](../DECISION-ROUND.md) → above the sanity cap `VLQT_MAX_COMMAND_PAYLOAD` the reconcile fails as in B1 of QUESTIONS.md (log, mark the Renderer, resync); nothing is truncated
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/protocol.h`, `src/VL.Qt.Native/commands.cpp` (apply loop, `poll`, `pushEvent`), `src/VL.Qt.Native/events.cpp`, `src/VL.Qt.Native/resources.cpp` (resource record header, if aligned with the same rule), `src/VL.Qt/Protocol.cs`, `src/VL.Qt/CommandWriter.cs`, `src/VL.Qt/EventReader.cs`, `src/VL.Qt/Reconciler/PropertyDiff.cs`, `src/VL.Qt/Reconciler/QmlPropertyWriter.cs`, `src/VL.Qt/Nodes/ShapeNodeCore.cs`, `src/VL.Qt.Tests/CommandWriterTests.cs` and the other golden-hex tests (`EffectChainTests`, `ShaderEffectTests`, `QmlTests`, `SamplerTests`, `ReconcilerPropertyTests`, `ShapeNodeCoreTests`, `PathNodeTests`)
- **Depends on:** PROTO-01 (bump the version), best done together with PROTO-09
- **Verification:** unit tests: a Text with 100 KB of content produces one SET; a Shape with 50,000 points is not truncated. SmokeTest `--all` (every scenario exercises the new header). Help patch `HowTo Draw Text.vl` with a long text.

### PROTO-03 Make CommandWriter's compound commands impossible to misuse
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/CommandWriter.cs:46-50`, `:238-241`, `:316-318`, `:332-342`, `:431-435`, `:457-464`, `:599`, `src/VL.Qt/Reconciler/ICommandSink.cs:52-155`
- **Finding:** The writer has four independent "open" markers: `_styleStart`, `_uniformsStart`, `_chainStart` and `_linkStart`/`_linkKind`. The contract "no other command in between" (ICommandSink docs, `:54`, `:71`, `:123`) is not enforced:
  - `Begin` (every plain command) does not check for an open compound. A `SetF32` written between `BeginStyle` and `EndStyle` is counted into the style's payload, and QtNative rejects the batch.
  - `BeginUniforms` inside an open style or chain, and `BeginStyle` inside open uniforms, are accepted.
  - `UniformEntry` (`:433`) decides by `_uniformsStart < 0` whether the entry belongs to a shader link. A `Uniform*` call inside an open shader link while uniforms are also open goes to the wrong count.
  - `Reset` (`:46`) clears `_length` but not the open markers. If an exception leaves a compound open, for example `UniformEntry` throwing `ArgumentException` for a bad name (`:437`), every later `BeginUniforms` throws "already open" until the writer is recreated. A later `EndUniforms` would patch bytes at a stale offset.
  - `QmlString` and `QmlRect` can be called inside `SET_UNIFORMS` or a shader link, where QtNative rejects strings (-7).
  - `EndQmlProperties` and `EndUniforms` are interchangeable.

  Today every caller validates names first (`UniformWriter.cs:28`, `QmlProperties.cs:105`), so this is latent. But a newcomer who adds a compound command has no guard rails.
- **Proposal:** Replace the four markers with one state: `enum Open { None, Style, Uniforms, QmlProperties, EffectChain, MultiLink, ShaderLink }` plus `_openStart`, `_openCount`, `_linkStart`, `_linkCount`. Rules:
  - `Begin` throws `InvalidOperationException` unless the state is `None`.
  - Each entry method checks its own state: `Style*` → Style; `Uniform*` → Uniforms or ShaderLink; `Qml*` and the named entries → QmlProperties; `Effect*` → MultiLink.
  - Each `End*` checks its own begin.
  - `Reset` sets the state to `None`.
  - An entry that throws leaves the state as it was. Callers that cannot guarantee valid input wrap the compound in `try { … } catch { writer.Abort(); throw; }`, where `Abort()` drops the open command (`_length = _openStart`, `_count--`).

  Give QML entries their own methods (`QmlF32`, `QmlI32`, …) that share the private encoder with `Uniform*`, so that ICommandSink says which entries belong to which command (see PROTO-10 for names). Remove the default interface members that throw `NotSupportedException` (`ICommandSink.cs:107-117`, "so fakes that predate #104 keep compiling"). Implement them in the recording fake instead.
- **Decided:** P4 step 3 → PROTO-10 (names) lands first in wave 3 and PROTO-03 follows in wave 4, so the entry methods use its names (`QmlProperty*` not `QmlF32` …, `MultiEffect*`, `Clear()` for `Reset()`); REC-13 → the default members of `ICommandSink` become abstract (tests derive from a null sink); S3 → `ICommandSink.cs` is in `Wire/`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/CommandWriter.cs`, `src/VL.Qt/Reconciler/ICommandSink.cs`, `src/VL.Qt/Reconciler/QmlPropertyWriter.cs`, the recording fake in `src/VL.Qt.Tests` (search for `: ICommandSink`), `src/VL.Qt.Tests/CommandWriterTests.cs`
- **Depends on:** none (PROTO-10 renames the same methods; do them in one issue or PROTO-03 first)
- **Verification:** new `CommandWriterTests`: a plain command inside an open style throws; nested begins throw; `Reset` after a thrown entry allows a new `BeginUniforms`. The existing golden-hex tests must stay byte-identical.

### PROTO-04 Cut protocol.h down to the wire format and move the semantics to docs
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/protocol.h:1-247` (head comment), `:325-354` (geometry, blocks), `:694-754` (pixel formats, snapshots, audio tap)
- **Finding:** The head comment of `protocol.h` is about 245 lines. Of that, about 60 lines are the wire format: header, the payload of each op, value encodings and the poll record. The rest is element behaviour:
  - geometry and the Group frame
  - the effect-chain pass driver, mask placement and the software backend
  - ShaderEffect shader loading, bake cache, `builtin:` paths and the uniform block layout
  - sampler semantics
  - QmlElement reload and echo rules
  - the value-input user-change rules
  - the audio-tap clock, producer and reader

  A reader who opens the header to find the layout of `SET` has to scroll past all of this. A reader who wants to understand the effect chain does not look in a C header. Each paragraph is also a single place where element docs (`docs/effects-and-shaders.md`, `docs/styles.md`) and code comments can disagree. K4 covers the long lines, not the location.
- **Proposal:**
  - **`docs/protocol.md`** (new) gets the wire format: one table per opcode (field, type, meaning, error code), the value encodings, the poll record, the resource record, the alignment and endianness rule, the versioning rule (PROTO-01), and how to add a command or property (header → mirror → drift check → decode case → test).
  - **The element sections move to the topic docs:**
    - Geometry and the Group frame → `docs/architecture.md` or a new `docs/geometry.md`
    - Effect chain, mask, ShaderEffect, Sampler → `docs/effects-and-shaders.md`
    - QmlElement → `docs/decisions/user-qml.md` or a new `docs/qml-element.md`
    - Input handlers and value inputs → the input doc of review 0x
    - Audio tap → `docs/` next to VL.Qt.Audio
  - **The header keeps** a 10-line head comment (purpose, "constants only, one per line" for the drift check, link to `docs/protocol.md`). It also keeps a one-line comment per constant (type and unit, as most already have) and one line per block saying which element uses it.

  K1 removes the issue numbers. Write each moved paragraph in the present tense, without "replaces …" and "was …" (for example `protocol.h:73`, `:114-115`, `:296-297`).
- **Decided:** G5 → `docs/decisions/user-qml.md` is `docs/design/user-qml.md`; G4 → the audio-tap text goes to the page of its package (`docs/video-audio.md`); O6 → the geometry text says 'bounds' for the rectangle of an element, not 'frame'; O5 → the shader-loading text is written for the `Shader` element
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/protocol.h`, `docs/protocol.md` (new), `docs/effects-and-shaders.md`, `docs/architecture.md`, `docs/README.md` (index), `docs/decisions/user-qml.md` or a new QML doc, the doc that receives the audio tap
- **Depends on:** none. Coordinate with PROTO-05 (the C# side points to the same doc).
- **Verification:** `--drift` still passes (the parser only reads constants). A reviewer can find the layout of every opcode in `docs/protocol.md`.

### PROTO-05 Protocol.cs: one line per constant, no second copy of the prose
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Protocol.cs:40-77`, `:170-176`, `:254-255`, `:348-356`, `:438`; `src/VL.Qt/EventReader.cs:10`, `:37`; `src/VL.Qt/CommandWriter.cs:221`, `:459`; `src/VL.Qt/Reconciler/ICommandSink.cs:43`
- **Finding:** `Protocol.cs` repeats parts of the header prose in XML docs, and the two copies have drifted. The drift check cannot see prose. Mismatches found:
  1. `Protocol.cs:254-255`: "Not settable by SET (-6) once plan effects/08 task 2 lands; until then the old single layer still takes them." The header (`protocol.h:114`) and the decoder say that `SET` with `EFFECT_*` is already -6.
  2. `Protocol.cs:60`: "Writer API (task 4) like the style …" refers to a finished plan task.
  3. `Protocol.cs:438`: the payload of `Ev.WindowGeometry` is given as "f32 x, y, w, h, u32 flags" (20 bytes). `WindowPlacement.cs:130` reads two more floats at offsets 20 and 24 (client position, 28 bytes). `protocol.h` does not document this payload at all.
  4. `EventReader.cs:37` says `Index` is the "Instance index within the sink (e.g. spread slice)". `protocol.h:137-138` says it is an offset to the slot index and that QtNative always sends 0.
  5. `EventReader.cs:10` and `protocol.h:540`: "Payloads per event are defined in plan 06". Payloads are actually spread over `protocol.h` (head comment and enum comments), `events.h` and the C# docs.
  6. `CommandWriter.cs:221`: the summary "ivec2, e.g. MeshResolution" sits on `SetVec2`. `SetIvec2` (`:229`) has none.
  7. `CommandWriter.cs:459`: the comment on `ShaderLinkFixedSize` says "u16 nameLen, u16 0". The field is reserved (0) since the strength uniform name was dropped (`protocol.h:93`, `CommandWriter.cs:513`).
  8. `ICommandSink.cs:43` cites #362 for `Prop.Image`, `protocol.h:382` cites #361. K1 removes both.
  9. `protocol.h:48-49`: `SET_QML_PROPERTIES` entries are "encoded exactly like SET_UNIFORMS entries". They also allow `STRING` (variable length), which `SET_UNIFORMS` rejects with -7 (`commands.cpp:837`, `fixedValueSize`).
- **Proposal:** Give each nested class of `Protocol.cs` a one-line summary that names the section of `docs/protocol.md` (PROTO-04). Give each constant a summary only where the C# user needs the type or unit (`/// <summary>u64 image resource id, 0 = none.</summary>`). Delete everything else, so the prose lives only in the doc and the header. Fix items 3, 4, 6, 7 and 9 at the source. For 3, document the 28-byte payload in `docs/protocol.md`. For 9, write "like SET_UNIFORMS entries, and STRING is allowed".
- **Decided:** B5 and PROTO-09 (wire-format pull request, P4 step 3) → items 4 and 7 vanish at the source: the record index and the reserved SHADER-link fields are deleted, so nothing is documented for them; S3 → `Protocol.cs` is `Wire/Protocol.cs`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Protocol.cs`, `src/VL.Qt/EventReader.cs`, `src/VL.Qt/CommandWriter.cs`, `src/VL.Qt/Reconciler/ICommandSink.cs`, `src/VL.Qt.Native/protocol.h`
- **Depends on:** PROTO-04
- **Verification:** `dotnet build` without new XML-doc warnings, `--drift` passes.

### PROTO-06 Separate the wire protocol from C ABI and test-only constants
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/protocol.h:563-765`, `src/VL.Qt/Protocol.cs:474-647`
- **Finding:** Besides the command and poll buffers, `protocol.h` and `Protocol.cs` hold three other kinds of constants:
  - **Arguments and return codes of `vlqt.h` functions:**
    - window state flags and window modes
    - offscreen result codes, pointer events, pointer devices, key events
    - resource kinds, resource states
    - shader stages, image flags
    - audio-tap flags and limits
  - **Constants of TEST exports, with their own comments saying "not part of the command buffer":**
    - `vlqt_device_lost`, `vlqt_mouse`, `vlqt_touch`
    - `VLQT_RES_KIND_TEST` / `Protocol.ResKind.Test`
  - **Pixel-format and snapshot documentation** without constants (`protocol.h:694-709`).

  A reader looking for "the protocol" finds 26 groups and cannot tell the wire format from the rest. The test constants are K6 occurrences inside the library: they ship in `VL.Qt.dll` and in the package header.
- **Proposal:** Keep one drift-checked header pair, organised by section: `/* === Command buffer === */`, `/* === Poll buffer === */`, `/* === Resource queue === */`, `/* === Function arguments (vlqt.h) === */`. Move the TEST constants to `src/VL.Qt.Native/vlqt_test.h`, next to the TEST exports. Their C# mirror becomes `src/VL.Qt.SmokeTest/TestProtocol.cs` (or `VL.Qt.Tests`, wherever the TEST exports are called from), and `ProtocolDrift` parses both headers. `ResKind.Test` follows if `vlqt_test_res_start` moves with the TEST exports; otherwise keep it with a "test only" line. In C#, split `Protocol.cs` into `Protocol.cs` (sections 1–3) and `NativeConstants.cs` (section 4, `internal static class NativeConstants`), so the cross-assembly users (VL.Qt.Stride: `Offscreen`, `Pointer`; VL.Qt.Audio: `AudioTap`, `ResKind`) see where they belong. The alternative, splitting the native header in two (`protocol.h` + `vlqt_constants.h`), also works if `ProtocolDrift` takes a list of headers. I recommend the sections because they keep a single include.
- **Decided:** P22 and P4 step 2 → one issue with CAPI-04 (`vlqt_test.h`), before the renames; PROTO-08 (P4 step 1) comes before it; TARGET 2.2, 2.5 and 2.7 → the C# side is `Wire/Protocol.cs` and `Interop/NativeConstants.cs` (namespace `VL.Qt.Interop`), and the mirror of `vlqt_test.h` is `src/VL.Qt.TestSupport/TestProtocol.cs` (linked into both test projects), not `SmokeTest/TestProtocol.cs`
- **Effort:** M, mechanical: yes (move and rename, then follow the compiler)
- **Touches:** `src/VL.Qt.Native/protocol.h`, `src/VL.Qt.Native/vlqt_test.h` (new) or the existing test-export header, `src/VL.Qt/Protocol.cs`, `src/VL.Qt/NativeConstants.cs` (new), `src/VL.Qt.SmokeTest/ProtocolDrift.cs`, users of the moved groups (about 100 files by search, mostly SmokeTest: `HandlerTest*.cs`, `InteractionTest*.cs`, `DeviceLostTest.cs`, `QtTextureTest*.cs`, `ResourceTest.cs`; library: `Images/ResourceClient.cs`, `WindowPlacement.cs`, `Native.cs`, `Renderer.cs`, `VL.Qt.Stride/*`, `VL.Qt.Audio/AudioTapPlayer.cs`)
- **Depends on:** PROTO-08 (the drift check moves at the same time)
- **Verification:** `--drift` (or its unit-test successor) passes. Full build of all projects.

### PROTO-07 Name the apply result codes in the protocol
- **Kind:** api
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/elements.h:43-53`, `src/VL.Qt.Native/protocol.h` (bare "-6", "-7", "-8", "-2", "-4" throughout the head comment), `src/VL.Qt/Reconciler/ResyncController.cs:65`, `src/VL.Qt/SceneCore.cs:136-160`
- **Finding:** The return codes of `vlqt_window_apply` (`ApplyOk = 0` … `ApplyInvalid = -8`, plus -9 for resources) are defined in the internal native header `elements.h`. The protocol document refers to them only by number ("Unknown field or state -6, value type not the field's type -7"). C# has no named constant for them, and a search for `BadValue`/`UnknownProperty` in `src/VL.Qt` finds nothing. The offscreen codes, by contrast, are in `protocol.h` and in `Protocol.Offscreen`. A newcomer reading "-7" has to find `elements.h`.
- **Proposal:** Move `enum ApplyResult` to `protocol.h` as `enum vlqt_apply_result { VLQT_APPLY_OK = 0, VLQT_APPLY_NULL_WINDOW = -1, VLQT_APPLY_BAD_LENGTH = -2, VLQT_APPLY_UNKNOWN_OP = -3, VLQT_APPLY_UNKNOWN_ID = -4, VLQT_APPLY_UNKNOWN_TYPE = -5, VLQT_APPLY_UNKNOWN_PROPERTY = -6, VLQT_APPLY_BAD_VALUE = -7, VLQT_APPLY_INVALID = -8 }`, with the resource code -9 as `VLQT_RES_UNKNOWN_ID` in the resource section. Keep `elements.h` using the C names, or have it alias them. Mirror the enum as `Protocol.ApplyResult` and use the names in `docs/protocol.md` and in `SceneCore`/`ResyncController` wherever a code is compared or logged.
- **Decided:** B1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → one enum `vlqt_result` (CAPI-02) for every export; the apply codes are its range -2 … -8 with the meanings of this finding, `VLQT_ERR_NULL_HANDLE` (not `NULL_WINDOW`), the resource code is `VLQT_ERR_UNKNOWN_ID`; no second table in `protocol.h`
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/protocol.h`, `src/VL.Qt.Native/elements.h`, `src/VL.Qt.Native/*.cpp` (if renamed, about 10 files by search for `Apply[A-Z]`), `src/VL.Qt/Protocol.cs`, `src/VL.Qt/SceneCore.cs`, `src/VL.Qt/Reconciler/ResyncController.cs`
- **Depends on:** none
- **Verification:** `--drift` passes. The SmokeTest's apply-error scenarios still report the same codes.

### PROTO-08 Keep the hand mirror, but check it in the unit tests and check layouts too
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.SmokeTest/ProtocolDrift.cs:1-96`, `src/VL.Qt.SmokeTest/Program.cs:150-155`, `src/VL.Qt/Protocol.cs:16-19`, `src/VL.Qt/CommandWriter.cs:238-239`, `:316`, `:457-459`, `src/VL.Qt/EventReader.cs:15`
- **Finding:** The mirror is sound as far as it goes. A regex parses `#define`/enum lines, reflection reads public literals, names are compared through `ToUpperSnake`, and a self-test injects one mismatch of each kind. Its limits:
  - It runs only in the SmokeTest (`--drift`, or as the first step of `--all`), not in `dotnet test src/VL.Qt.Tests`, which a contributor runs first.
  - It compares constants but not layouts. The fixed sizes exist as private C# constants and as numbers in the native decoder (`commands.cpp:378` `expect(6)`, `:386` `expect(12)`, `:405` `expect(10)`, `:1100` `+ 24`) with nothing tying them together: `SetFieldsSize = 8`, `StyleFieldsSize`, `StyleEntryHeaderSize`, `UniformFieldsSize`, `ChainFieldsSize`, `LinkHeaderSize`, `ShaderLinkFixedSize = 28`, `RecordFieldsSize = 8`.
  - It requires the nested classes of `Protocol` to be `public` (`GetNestedTypes(BindingFlags.Public)`, `ProtocolDrift.cs:67`). This is the only reason they are `public` inside an `internal` class.
  - The name mapping produces names a reader would not choose (`Rectf`, `Ivec2`, `Dip`, `Vt`, `Ev`) because it has to round-trip `ToUpperSnake`.
- **Proposal:** Move `ProtocolDrift` to `src/VL.Qt.Tests/ProtocolDriftTests.cs` as an NUnit test that reads `src/VL.Qt.Native/protocol.h` relative to the repository (copy it to the output as the SmokeTest does), and keep `--drift` as a thin call. Add the fixed sizes to `protocol.h` as `#define`s (`VLQT_SET_FIELDS_SIZE 8`, `VLQT_CREATE_PAYLOAD 6`, `VLQT_REPARENT_PAYLOAD 12`, `VLQT_BIND_PAYLOAD 10`, `VLQT_ENTRY_HEADER_SIZE 4`, `VLQT_LINK_HEADER_SIZE 8`, `VLQT_SHADER_LINK_FIXED_SIZE 24`, `VLQT_EVENT_FIELDS_SIZE 8`), use them in `commands.cpp`'s `expect(...)`, and mirror them so the drift check covers them. Let the drift check also accept `internal` nested types (`BindingFlags.Public | BindingFlags.NonPublic`), so that the nested classes can be `internal` (see section 4).

  On generation: generating `Protocol.cs` from `protocol.h`, or both from one description, would remove the mirror. It adds a generator that a newcomer has to find and run, and a build step on two toolchains (CMake and MSBuild). The constants change a few times per release, and the drift check catches every mistake. I recommend the hand mirror plus the unit-test drift check. Revisit when the protocol grows a second consumer, such as a Linux-only or a Python test harness. Question Q2 records the alternative.
- **Decided:** D4 → (a), hand mirror plus the stricter drift check as a unit test, as proposed; P4 step 1 → done first in wave 3, before any rename of the break; C1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the custom runner's options go: `--drift` and the check of `vlqt.h` against the `DllImport`s (CAPI-06 (c)) are unit tests, `--only`/`--list` are NUnit filters, `--replay`, `--tearing` and `--update-refs` are explicit NUnit tests or test parameters; the docs follow in wave 6
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.SmokeTest/ProtocolDrift.cs` → `src/VL.Qt.Tests/ProtocolDriftTests.cs`, `src/VL.Qt.SmokeTest/Program.cs`, `src/VL.Qt.Tests/VL.Qt.Tests.csproj` (copy `protocol.h`), `src/VL.Qt.Native/protocol.h`, `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt/Protocol.cs`, `src/VL.Qt/CommandWriter.cs`, `src/VL.Qt/EventReader.cs`, `AGENTS.md` (Tests section: `--drift` line)
- **Depends on:** none
- **Verification:** `dotnet test src/VL.Qt.Tests` fails when a constant in `protocol.h` is changed by hand, and passes again after the mirror is fixed.

### PROTO-09 Remove dead protocol fields and constants
- **Kind:** dead-code
- **Severity:** should
- **Breaking:** yes: wire format only (no patch notices)
- **Where:** `src/VL.Qt.Native/protocol.h:17`, `:136-138`, `:472-477`, `:413`; `src/VL.Qt/Protocol.cs:348-357`, `:256-257`; `src/VL.Qt/CommandWriter.cs:89-96`, `:513-514`; `src/VL.Qt/EventReader.cs:37-44`; `src/VL.Qt.Native/commands.cpp:410-412`, `:1108-1113`; `src/VL.Qt/Elements/SinkRegistry.cs:150-170`
- **Finding:**
  - **BIND `u16 flags`.** QtNative ignores the field (`commands.cpp:410` "flags ignored", `:412` "none for handlers"). The only flag, `CANCEL_PRESS = 2`, is "reserved … not sent. Kept for the writer tests and the drift check" (`Protocol.cs:355`).
  - **Event record `u32 index`.** "QtNative always sends 0" (`protocol.h:138`). Yet `SinkRegistry.Target` adds it to the slot index with overflow checks (`SinkRegistry.cs:166-171`), and `EventReader.Index` documents a different meaning (PROTO-05, item 4).
  - **SHADER link reserved `u16 0, u16 0`** (`CommandWriter.cs:513`, `commands.cpp:1108`). Left over from the removed strength uniform name.
  - **`Prop.EffectEnabled = 832`**, "Reserved (was: …)".
  - **`EventReader.RecordOffset`.** No reader outside the struct (search).
  - **`CommandWriter.SetMat4(uint, ushort, ReadOnlySpan<float>)` and the `in Color4`/`in RectangleF`/`string?` overloads on `CommandWriter`.** They duplicate `CommandSinkExtensions` (`ICommandSink.cs:159-166`).
- **Proposal:**
  - BIND becomes `u32 id, u32 sinkHandle` (payload 8). Delete `vlqt_bind` / `Protocol.Bind` and the `flags` parameter of `ICommandSink.Bind`.
  - The event record drops `index` (header `u16 event, u16 flags, u32 len, u32 handle` with PROTO-02, or `u16 event, u16 len, u32 handle` without it). Delete `EventReader.Index` and the offset logic in `SinkRegistry.Target`; the record goes to the bound slot.
  - The SHADER link head becomes `u16 template, u16 shaderCount, u64 ids[], 4 x f32 padding, f32 strength` (20 bytes after the ids).
  - Delete `EffectEnabled` and keep "832 unused" as a comment, like the other freed ids.
  - Delete `RecordOffset` and the duplicate overloads (callers use the extensions).

  Bump the version (PROTO-01).
- **Decided:** B5 → (a), and the field is removed from the protocol with the native break; A4 → the event header is `u16 event, u16 flags, u32 len, u32 feedbackId` (the `u16`-length variant only if P8 falls back to `u16`); O7 → BIND is `u32 id, u32 feedbackId` (was `sinkHandle`) and `EventReader.Handle` is `EventReader.FeedbackId`; A1 → `SinkRegistry.Target` is in `FeedbackRouter`; P4 step 3 → one wire-format pull request with PROTO-02, B5, REC-13
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/protocol.h`, `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/events.cpp` (`pushEvent` callers pass `index`), `src/VL.Qt.Native/commands.h`, `src/VL.Qt/Protocol.cs`, `src/VL.Qt/CommandWriter.cs`, `src/VL.Qt/EventReader.cs`, `src/VL.Qt/Reconciler/ICommandSink.cs`, the reconciler's BIND callers (search `\.Bind\(`), `src/VL.Qt/Elements/SinkRegistry.cs`, the recording fake and the golden-hex tests in `src/VL.Qt.Tests`, the SmokeTest scenarios that call `Bind` with flags
- **Depends on:** PROTO-01. Best in one change with PROTO-02 (both rewrite the headers).
- **Verification:** golden-hex tests updated. SmokeTest `--all` (Handler, Interaction, ValueInputs and Effect scenarios).

### PROTO-10 Names a newcomer can read
- **Kind:** naming
- **Severity:** should
- **Breaking:** no (internal API only)
- **Where:** `src/VL.Qt/Protocol.cs` (groups), `src/VL.Qt/CommandWriter.cs:46`, `:76`, `:321-330`, `:344-407`, `:482-495`, `src/VL.Qt/Reconciler/ICommandSink.cs`, `src/VL.Qt.Native/protocol.h:269`, `:288`
- **Finding:** The names follow the C header's abbreviations and the history of the code, not what a C# reader expects:
  - **`Protocol.Vt`, `Ev`, `Prop`, `Img`, `ResEv`.** Abbreviations that VL.StandardLibs does not use.
  - **`Protocol.Type`.** Shadows `System.Type` inside the namespace. `ProtocolDrift.cs:58` has to write `System.Type`.
  - **`Protocol.Window`.** Holds window *state* flags.
  - **`CommandWriter.Reset()` and `ResetScene()`.** The first clears the buffer, the second writes the RESET opcode.
  - **QML property entries are written with `Uniform*` methods** (`QmlPropertyWriter.cs:55-61`), except rect and string, which are `QmlRect`/`QmlString`. `EndUniforms` closes a QML command.
  - **`Prop.Effect*` and `vlqt_prop` 832–855 are no longer properties.** They are keys of MultiEffect entries (`protocol.h:114`).
  - **Opcode and value-type blocks start at 16 "to keep merges with other packages simple"** (`protocol.h:269`, `:288`). This explains an incident of history, not the contract.
  - **`Op.SetClear` vs `Op.ClearColor`.** Two names for the same concept, one with `Set`, one without.
- **Proposal:** In C#, rename the groups:

  | Old | New |
  |---|---|
  | `Vt` | `ValueType` |
  | `Ev` | `EventId` |
  | `Prop` | `PropertyId` |
  | `Type` | `ElementType` |
  | `Window` | `WindowState` |
  | `Img` | `ImageLoadFlags` |
  | `ResEv` | `ResourceEventId` |
  | `ResState` | `ResourceState` |
  | `ResKind` | `ResourceKind` |
  | `Prop.Effect*` | `MultiEffectKey.*` |

  The C names change with them (`VLQT_VALUE_TYPE_*`, `VLQT_EVENT_*`, `VLQT_PROPERTY_*`, `VLQT_ELEMENT_TYPE_*`, `VLQT_MULTI_EFFECT_KEY_*`), so the drift mapping stays mechanical. In C#, also rename `Rectf` → `RectF`, `Ivec2` → `IVec2`, `Dip` → `DIP`; the drift check maps these once through a small exception table. `CommandWriter.Reset()` becomes `Clear()`. Writer entry methods by command:
  - SET_STYLE: `Style*`
  - SET_UNIFORMS: `Uniform*`
  - SET_QML_PROPERTIES: `QmlProperty*`
  - MULTI link: `MultiEffect*`
  - SHADER link: `Uniform*`

  These share private encoders, and each `Begin*` has a matching `End*` (`EndQmlProperties` stops being an alias). Rename `Op.ClearColor` → `Op.SetClearColor`. Delete the "merge" comments; the numbering blocks stay as they are (renumbering buys nothing).
- **Decided:** P4 step 3 → the names pull request is the second of step 3 (mechanical, after the return-code pull request, before the wire-format one) and runs in wave 3, so 'after PROTO-03' does not hold: PROTO-06 comes before it (step 2) and PROTO-03 after it (wave 4); TARGET 2.2 → `Protocol.cs`, `CommandWriter.cs`, `EventReader.cs` are in `Wire/`, `NativeConstants.cs` in `Interop/`; O5 → `VLQT_TYPE_SHADER_EFFECT` is `VLQT_ELEMENT_TYPE_SHADER`; A5 and NELM-11 → protocol type `ITEM` is `GROUP` (`VLQT_ELEMENT_TYPE_GROUP`); O7 → `sinkHandle` is `feedbackId`; B9 of [DECISION-ROUND.md](../DECISION-ROUND.md) → only `Protocol.ElementType` in `Wire/`; the mapping element → type id lives in `Reconciliation/`, `Elements/ElementType.cs` goes
- **Effort:** L, mechanical: yes (rename, follow the compiler, about 100 files)
- **Touches:** `src/VL.Qt.Native/protocol.h`, every native file that uses the renamed C constants (`commands.cpp`, `events.cpp`, `elements*.cpp`, `effectchain.cpp`, `vlstyle.cpp`, `resources.cpp`, `image_resource.cpp`, `audio_tap.cpp` and others by search for `VLQT_(VT|EV|PROP|TYPE)_`), `src/VL.Qt/**` (search `Protocol\.`), `src/VL.Qt.Stride/**`, `src/VL.Qt.Audio/**`, `src/VL.Qt.Tests/**`, `src/VL.Qt.SmokeTest/**`, `src/VL.Qt.Probe/Program.cs`, `src/VL.Qt.SmokeTest/ProtocolDrift.cs`
- **Depends on:** PROTO-06 (same groups move), PROTO-03 (same writer methods). Do it after both, in one mechanical issue; it conflicts with every open branch, so schedule it when few issues are in flight.
- **Verification:** full build, `dotnet test`, SmokeTest `--all`, `--drift`.

### PROTO-11 Document and fix the UTF-8 truncation of EV_ERROR
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/events.cpp:1004`, `:746-790`, `src/VL.Qt.Native/protocol.h:550`
- **Finding:** `EV_ERROR` text is cut at 4096 bytes with `qMin(utf8.size(), 4096)`, which can split a multi-byte character. C# then decodes a replacement character at the end. `EV_VALUE` strings are cut on a character boundary (`events.cpp:782`). The header documents neither cap for `EV_ERROR`. A long QML compile log or shader bake log can reach the cap.
- **Proposal:** Use the same boundary-safe truncation helper for both (one function in `events.cpp`). Document "at most 4096 bytes, cut on a character boundary, ends with '…'" at `VLQT_EV_ERROR`, or drop the cap with PROTO-02.
- **Decided:** A4 and P4 step 4 → the native string caps go (PROTO-02 lists the 4096 of `EV_ERROR` among them), so the 'drop the cap with PROTO-02' branch holds and no truncation helper or '…' rule is written; with the P8 fallback to `u16` the boundary-safe helper branch applies
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/events.cpp`, `src/VL.Qt.Native/protocol.h`
- **Depends on:** PROTO-02 (may make the cap unnecessary)
- **Verification:** SmokeTest QmlTest with a QML file that produces more than 4 KB of errors with non-ASCII text.

### PROTO-12 State and assert the byte order on the native side
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:36-41`, `:1276-1288`, `src/VL.Qt.Native/vlstyle.cpp:60-65`, `src/VL.Qt.Native/protocol.h:11`
- **Finding:** The header says "little-endian". C# writes and reads explicitly little-endian (`BinaryPrimitives`). QtNative reads and writes with `memcpy` in host order (`rd<T>`, `pushEvent`), which is right only because every supported RID (win-x64, win-arm64, linux-x64, linux-arm64) is little-endian. Nothing states or checks this assumption. `rd<T>` also exists twice (`commands.cpp:36`, `vlstyle.cpp:60`).
- **Proposal:** Add `static_assert(Q_BYTE_ORDER == Q_LITTLE_ENDIAN, "the VL.Qt protocol is little-endian; add byte swapping in rd/wr");` to one shared native header (`wire.h`) that holds `rd<T>`, `wr<T>` and `align4`, and use it in `commands.cpp`, `events.cpp` and `vlstyle.cpp`.
- **Decided:** P22 → PROTO-12 = NCMD-06, one issue (`wire.h`); S2 → `wire.h` stays at the top of `src/VL.Qt.Native` with `vlqt.h`, `vlqt_test.h` and `protocol.h`
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/wire.h` (new), `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/events.cpp`, `src/VL.Qt.Native/vlstyle.cpp`, `src/VL.Qt.Native/CMakeLists.txt` (if headers are listed)
- **Depends on:** none
- **Verification:** native build on both architectures (CI).

## 4 Target structure

```
src/VL.Qt.Native/
  protocol.h          wire protocol only: version, sizes, opcodes, value types, element types, property ids,
                      MultiEffect keys, flags, style fields/states, event ids, resource queue ids/states,
                      apply result codes; a section "function arguments (vlqt.h)" with window state/mode,
                      offscreen codes, pointer/key events, resource kinds, shader stages, image flags,
                      audio-tap flags/limits. Head comment about 10 lines + link to docs/protocol.md   (PROTO-04/06/07)
  vlqt_test.h         (new, or the existing TEST-export header) device-lost modes, mouse, touch, RES_KIND_TEST  (PROTO-06)
  wire.h              (new) rd<T>, wr<T>, align4, static_assert little-endian                              (PROTO-12)

src/VL.Qt/
  Protocol.cs         internal static class Protocol: mirror of the wire sections; nested classes internal:
                        AbiVersion, sizes, Op, ValueType (was Vt), Verb, ElementType (was Type), PropertyId (was Prop),
                        MultiEffectKey (split from Prop.Effect*), EffectLink, EffectTemplate, UniformSet, StyleSet,
                        StyleState, StyleField, EventId (was Ev), ApplyResult (new), ResourceEventId (was ResEv),
                        ResourceState (was ResState)                                                       (PROTO-07/10)
  NativeConstants.cs  (new) internal static class: WindowState (was Protocol.Window), WindowMode, Space, Offscreen,
                        Pointer, PointerDevice, KeyEvent, ResourceKind, ShaderStage, ImageLoadFlags (was Img), AudioTap (PROTO-06)
  CommandWriter.cs    internal sealed: one open-command state; Clear() (was Reset); Style*/Uniform*/QmlProperty*/MultiEffect*
                        entry methods; Begin*/End* pairs; Abort(); trace stays                                   (PROTO-03/10)
  EventReader.cs      internal ref struct: Event, Handle, Payload, Error (Index and RecordOffset deleted)            (PROTO-09)
  Reconciler/ICommandSink.cs  internal interface, no default members; CommandSinkExtensions stays (one type per file
                        per K3: CommandSinkExtensions.cs)

src/VL.Qt.Tests/
  ProtocolDriftTests.cs  (moved from SmokeTest/ProtocolDrift.cs) constants + sizes, both headers                  (PROTO-08)

src/VL.Qt.SmokeTest/
  TestProtocol.cs     (new) mirror of vlqt_test.h                                                                   (PROTO-06)
  Program.cs          --drift calls the shared drift code

docs/
  protocol.md         (new) wire format tables, versioning rule, how to add a command or property                  (PROTO-04)
```

Public: nothing in this area is public API. `Protocol` is `internal`, and its nested classes are `public` only because `ProtocolDrift` reflects with `BindingFlags.Public`. With PROTO-08 they become `internal`. `VL.Qt.Space` (the public enum used by pins) stays public. Its numeric mirror `Protocol.Space` is only needed for the drift check; `SetSpace` casts the enum directly (`CommandWriter.cs:111`). `VL.Qt.Stride`, `VL.Qt.Audio` and the test projects reach `Protocol` through `InternalsVisibleTo` (`VL.Qt.csproj:35-40`), which is correct for constants shared between the assemblies of one package.

## 5 Questions for Sebastian

**Q1 – 16-bit or 32-bit command length (PROTO-02).**
- **(a) u32 length in the command and event headers.** Removes the 64 KB limit, the point caps of Shape/Path, the QML chunking and three native caps. Costs one rewrite of both headers and the golden-hex tests (4 extra bytes per command, negligible).
- **(b) Keep u16 and truncate strings in C# with a node warning.** Smallest change, but the caps and chunking stay and each new variable-length value needs its own cap.

Recommendation: (a), together with PROTO-09, in one protocol-version bump.

**Q2 – Hand mirror or generated mirror (PROTO-08).**
- **(a) Hand mirror + drift check in the unit tests** (today's approach, made stricter).
- **(b) Generate `Protocol.cs` (constants only) from `protocol.h`** with a script in `tools/` and a unit test that fails when the checked-in file is stale.
- **(c) One description file (for example `protocol.toml`) that generates both.** It needs a generator on both toolchains, and it makes the header, which is today the readable source, an artifact.

Recommendation: (a) now. (b) if the mirror becomes a chore. Not (c).

**Q3 – Version check on mismatch: refuse or warn (PROTO-01).** Refusing (no windows, error at the Renderer) is safe and clear. Warning and continuing lets an expert run a newer native DLL with an older C# build, but the result is undefined. Recommendation: refuse.

## 6 For other areas

- **Reconciler (03?):** the `Reconciler` folder has no exception handling around sink calls. A throwing `ICommandSink` call (PROTO-02, PROTO-03) leaves the remembered state ahead of what was sent. Check how `Renderer`/`SceneCore` recover from an exception inside `Reconciler.Update` (`SceneCore.cs:109`).
- **Elements/input:** `SinkRegistry.Target` (`src/VL.Qt/Elements/SinkRegistry.cs:166`) implements an index offset that QtNative never sends (PROTO-09).
- **Native:** `rd<T>` is duplicated in `commands.cpp:36` and `vlstyle.cpp:60` (PROTO-12). `Scene::setUniforms` still carries the comment "Interface stub (#437 task 1) … applies nothing yet", although it applies (`commands.cpp:807-808`).
- **Window/placement:** `WindowPlacement.cs:130` depends on an undocumented 28-byte `EV_WINDOW_GEOMETRY` payload (client position at 20/24).
- **Tests (K6):** `CommandWriter.Capacity` (`CommandWriter.cs:40`) and `ReadI32` (`EventReader.cs:87`) are used only by `CommandWriterTests`. Keep `Capacity` as the no-growth assertion; `ReadI32` can go.
