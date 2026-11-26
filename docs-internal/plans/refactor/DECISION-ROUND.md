# Decision round – refactor (release 0.10.0)

The round after the pass of P1/P2 and the shader comparison P12 ([PLAN-CHECK.md](PLAN-CHECK.md)), 2026-11-19, on `refactor` at a7d53bd. It collects what the pass left open, in one place, so that the issues can be cut afterwards:

- the open rows of the catalog ([review/23_api.md](review/23_api.md), section 7, "Added by the pass of P1/P2"),
- the open rows of [review/28_shader-reference.md](review/28_shader-reference.md),
- the points the pass found covered by no decision (the sub-agents reported 52; duplicates of rows, and points that the shader comparison answers, are merged here).

All points were decided on 2026-11-19 (Sebastian): blocks B to E as recommended. Each point has a line `Decision:`. A decision is copied afterwards into the Decision column of its row (23_api.md, 28_shader-reference.md) or into a `Decided:` line of the finding; decisions of principle also go into [DECISIONS.md](DECISIONS.md). Block A comes first because the rows of blocks D and E depend on it.

| Block | Topic | Points |
|---|---|---|
| A | Principles that decide rows | 5 |
| B | The C ABI and the protocol, before the native break (P4 step 3) | 12 |
| C | Smaller open points | 18 |
| D | Open catalog rows (node API) | 18 |
| E | Open rows of the shader comparison | 24 |

## A Principles that decide rows

### A1 One mechanism for messages at a node
Four helpers do the same job today: `NodeErrors` (RUN-01, one slot per node), `NodeMessage` (NODE-05, per message source), `ResourceNodeReport` (NODW-04, the image and file nodes), the reporting styles of the shader area (SHD section 6). P22 makes them one issue, TARGET.md 2.7 left the choice to the pass, and no decision names it. Also open: the test seam (TEST-07 G1) and whether VideoSourceToQtImage marks its node on a lasting error (B7 lists only LoadImage, LoadImage (Async), LoadFont, ToQtImage).
- (a) `NodeErrors` is the one store, with one entry per phase (RUN-01); `NodeMessage` is the handle a message source holds on it (NODE-05); `ResourceNodeReport` and the shader styles use it. A test seam `INodeMessages` only if the spike with `NodeContext.Create` (TEST-07) cannot observe the warning.
- (b) Keep separate helpers per area, one rule written down.
- Recommended: (a), and VideoSourceToQtImage marks its node like ToQtImage (a lasting state the patcher must fix).
- Source: RUN-01, NODE-05, NODE-06, NODW-04, TEST-07, SHD-12, TARGET.md 2.7
- Decision: (a) (Sebastian, 2026-11-19), with VideoSourceToQtImage marking its node like ToQtImage.

### A2 `Matrix` in the default rule: "always" or "never"?
QUESTIONS.md D3 decided option (b), whose text says "explicit unless `default(T)` is intended; **never** for `Matrix`". DECISIONS.md and the planned AGENTS.md line say "**always** for `Matrix`". NODE-04 names both.
- (a) Always: a `Matrix` pin always carries an explicit default (identity).
- (b) Never: no attribute on `Matrix` pins.
- Recommended: check first what vvvv shows for a `Matrix` pin without attribute and whether `[DefaultValue]` can express identity at all; then (a) if the type default is the zero matrix, (b) if vvvv substitutes identity.
- Source: D3, NODE-04, DECISIONS.md "Defaults explicit unless the type's default is meant"
- Decision: (b) never for `Matrix` (Sebastian, 2026-11-19): vvvv's default of a `Matrix` pin is the identity, so the pin needs no attribute; the entry in DECISIONS.md is corrected. Sebastian from memory: the issue of D3 checks once in vvvv that a `Transform` pin without attribute shows the identity.

### A3 The generated QML and shader nodes lose `Error` too
"No `Error` output pins" lists the nodes of 0.9.10 with a fixed pin set; the generated nodes (QML, shader element node, shader effect node) carry the same output. The catalog now files them as a consequence of the principle (rows QML-04, SHD-08, SHD-09). It also settles the `Warn | ErrorPin` switch of SHD-09 (only `Warn` remains) and the reserved pin name `Error` of SHD-03.
- (a) Yes, they lose it; errors go to the log and mark the node.
- (b) They keep it.
- Recommended: (a).
- Source: QML-04, SHD-03, SHD-08, SHD-09
- Decision: (a) (Sebastian, 2026-11-19).

### A4 Pin names of the built-in effect nodes: A7 or E1?
Where VL.Skia has the node, A7 gives Skia's names (Blur `Amount`, DropShadow `Offset` as `Vector2`, `Blend Mode`); E1 makes TextureFX the reference for shaders (`Strength`, `Offset` + `Angle`, `Blend Operation`).
- (a) A7 first for node and pin names; TextureFX for the conventions of shader files.
- (b) E1 first: TextureFX's names.
- Recommended: (a); it keeps the decided DropShadow `Blur Amount`.
- Source: question 1 of 28_shader-reference.md
- Decision: (a) (Sebastian, 2026-11-19).

### A5 Do the merged effects count as shaders under E1?
DropShadow `Offset` and the `Padding` of Blur and DropShadow are values of Qt's MultiEffect, not of a VL.Qt shader.
- (a) Yes: DIP like the shader effects, one unit for every length in `Qt.Effects`.
- (b) No: they stay in renderer units like the element's own geometry.
- Recommended: (a); otherwise one chain mixes units (DropShadow `Offset` in units next to InnerShadow `Offset` in DIP). Decides rows E2 to E5. With (a), SHD-01's "built-in links keep units" goes as well.
- Source: question 2 of 28_shader-reference.md, SHD-01
- Decision: (a) (Sebastian, 2026-11-19); E2 to E5 follow.

## B The C ABI and the protocol, before the native break

All of these change a name, a code or a signature, so they are decided before step 3 of P4 ("every name and every field changes at most once").

### B1 One result-code table
PROTO-07 names the apply codes `VLQT_APPLY_*` (-1 `NULL_WINDOW` … -8, resource -9 `VLQT_RES_UNKNOWN_ID`); CAPI-02 makes one enum `vlqt_result` for every export (`VLQT_ERR_NULL_HANDLE` -1, `VLQT_ERR_UNKNOWN_ID` -9 …). NDEV-12 adds `VLQT_OFFSCREEN_INTERNAL_ERROR = -27`, NAPP-02/03 a shared `VLQT_ERR_INTERNAL`.
- (a) One enum `vlqt_result` (CAPI-02); the apply codes are its range -2 … -8 with PROTO-07's meanings; `NULL_HANDLE`, not `NULL_WINDOW`; exceptions everywhere `VLQT_ERR_INTERNAL`, no -27.
- (b) Two tables: apply codes in `protocol.h`, export codes in `vlqt.h`.
- Recommended: (a).
- Source: PROTO-07, CAPI-02, NAPP-02, NAPP-03, NDEV-12
- Decision: as recommended (Sebastian, 2026-11-19).

### B2 The return convention of `poll`
NCMD-04 proposes `>= 0` bytes, `-required` for a small buffer and `VLQT_POLL_ERROR` (`INT_MIN`) for a null handle or an exception; CAPI-02/NAPP-03 give every export the result enum and an `int* required` out parameter.
- (a) `poll` follows the shared convention (result code, byte count and `required` as out parameters).
- (b) `poll` keeps its own scheme, documented.
- Recommended: (a).
- Source: NCMD-04, CAPI-02, NAPP-03
- Decision: as recommended (Sebastian, 2026-11-19).

### B3 `vlqt_window_create` or `vlqt_window_create_at`?
NAPP-17 deletes `vlqt_window_create` and keeps `_create_at`; CAPI-03 renames `_create_at` to `vlqt_window_create` and deletes the short form.
- (a) One function `vlqt_window_create` with the position (CAPI-03).
- (b) One function `vlqt_window_create_at` (NAPP-17).
- Recommended: (a).
- Source: NAPP-17, CAPI-03
- Decision: as recommended (Sebastian, 2026-11-19).

### B4 Name and scope of the version export
PROTO-01 proposes `int vlqt_abi_version(void)` returning `VLQT_ABI_VERSION`, which replaces `VLQT_PROTOCOL_VERSION`; the pattern of CAPI-03 is `vlqt_<group>_<verb>`.
- (a) `vlqt_app_get_abi_version`; one number for protocol and C ABI.
- (b) `vlqt_abi_version` as proposed; one number.
- (c) Two numbers, protocol and ABI.
- Recommended: (a).
- Source: PROTO-01, B3, CAPI-03
- Decision: as recommended (Sebastian, 2026-11-19).

### B5 The log callback as a function pointer joins the break
CAPI-09 turns the log callback of `vlqt_app_init` into an `[UnmanagedCallersOnly]` function pointer: a signature change that P4 does not list.
- (a) Yes, in the names pull request of step 3.
- (b) Later, as an addition that raises the version.
- Recommended: (a).
- Source: CAPI-09, P4
- Decision: as recommended (Sebastian, 2026-11-19).

### B6 The native side of the one owner of the animation clock
RUN-02 makes `QtApp` the owner of the driver time. Natively: (a) `Driver::setTime` ignores times below the current one (no signature change); (b) `renderBatch` stops advancing the driver (changes `vlqt_offscreen_render_batch`).
- Recommended: (a).
- Source: RUN-02
- Decision: as recommended (Sebastian, 2026-11-19).

### B7 What happens above the sanity cap after `u32`
PROTO-02 keeps a protocol constant `VLQT_MAX_COMMAND_PAYLOAD` (for example 64 MB). A user string above it:
- (a) Is a failed reconcile as in B1 (log, mark the Renderer, resync); no truncation code.
- (b) Is cut at a UTF-8 boundary with a warning at the node.
- Recommended: (a); 64 MB is not a real value, and A4 wanted the truncation code gone.
- Source: REC-01, PROTO-02, A4
- Decision: as recommended (Sebastian, 2026-11-19).

### B8 The protocol names of a shader link after O5
O5 renames the element type and keeps "link" for one entry of a chain as sent (`EffectChainLink`). The protocol still has `BeginShaderLink` and `ShaderLink` in `protocol.h`.
- (a) `BeginEffectChainLink` and `EffectChainLink`: it is the entry as sent.
- (b) `BeginShaderEffect`, after the C# type.
- (c) Keep.
- Recommended: (a).
- Source: SHD-01, SHD-09, O5
- Decision: as recommended (Sebastian, 2026-11-19).

### B9 `Protocol.ElementType` and `Elements/ElementType.cs`
PROTO-10 renames the nested `Protocol.Type` to `Protocol.ElementType`; TARGET.md 2.2 keeps `Elements/ElementType.cs`. Two types of one name in two namespaces.
- (a) Only `Protocol.ElementType` in `Wire/`; the element → type-id mapping lives in `Reconciliation/` (A3: the element model knows no protocol), `Elements/ElementType.cs` goes.
- (b) Keep both, the internal one renamed.
- Recommended: (a).
- Source: PROTO-10, ELM-04, NELM-11, A3
- Decision: as recommended (Sebastian, 2026-11-19).

### B10 Type names of the shader element side
O5 names the effect side (`ShaderEffectLayout`, `ShaderEffectDescription`, `ShaderEffectInstance`). SHD-07/11 propose `ShaderElement*` for the element side.
- (a) `ShaderLayout`, `ShaderDescription`, `ShaderInstance`, after the node `Shader`.
- (b) `ShaderElementLayout`, `ShaderElementDescription`, `ShaderElementInstance`.
- Recommended: (a).
- Source: SHD-07, SHD-11, O5
- Decision: as recommended (Sebastian, 2026-11-19).

### B11 The padding type of the shader area
An internal static class `EffectPadding` exists (reconciler); SHD-01 proposes `EffectPadding { Value, IsDip }`, SHD-07 `ShaderPadding`. With E1 every shader padding is DIP, so the unit flag has no use.
- (a) `ShaderPadding` is the annotation model (SHD-07), without a unit flag; `EffectPadding` stays the reconciler helper; `Uniform.Length`/`IsLength` and the `ToQt` of shader lengths go.
- (b) As SHD-01, with the flag.
- Recommended: (a), together with A5 (a).
- Source: SHD-01, SHD-07, E1, note 9 of 28_shader-reference.md
- Decision: as recommended (Sebastian, 2026-11-19).

### B12 Cut of `resources.cpp`: pure move first?
NRES-03 wants the cut in one go with NRES-04 (a behaviour change: a main-thread-only table); TARGET.md 3.3 treats every cut as a pure move in the first round.
- (a) Pure move first (TARGET.md 3.3), NRES-04 afterwards in the lane N-resources.
- (b) One issue.
- Recommended: (a). This also confirms that the order of TARGET.md 3.3 binds where a finding names another one (NDIAG-04 "first" against the second round).
- Source: NRES-03, NRES-04, NDIAG-04, TARGET.md 3.3
- Decision: as recommended (Sebastian, 2026-11-19).

## C Smaller open points

### C1 SmokeTest options after F5
F5 removes the custom runner. Its options are named in AGENTS.md and the docs: `--drift`, `--only`, `--list`, `--replay`, `--tearing`, `--update-refs`.
- (a) `--drift` becomes the unit test of PROTO-08, and the header-against-`DllImport` check of CAPI-06 (c) lives there too; `--only`/`--list` become NUnit filters; `--replay`, `--tearing` and the reference update become explicit NUnit tests or test parameters (runsettings); AGENTS.md and the docs follow in wave 6.
- Recommended: (a).
- Source: DOC-13, DOC-05, PROTO-08, CAPI-06, TEST-11, F5
- Decision: as recommended (Sebastian, 2026-11-19).

### C2 Does the patch checker read `node-api.txt`?
- (a) Yes, once the snapshot of TEST-01 exists; `tools/vl_pin_catalog.py` goes (an issue of wave 5).
- (b) Keep both.
- Recommended: (a).
- Source: TEST-01, P6
- Decision: as recommended (Sebastian, 2026-11-19).

### C3 `VLQT_TRACE_COMMANDS`
Not sorted by "Pins instead of environment variables". It records command traces for tests (TEST-04).
- Recommended: stays an environment variable, as a pure test switch.
- Source: TEST-04
- Decision: as recommended (Sebastian, 2026-11-19).

### C4 The budget of `OverBudget`
E13 asks for one definition of "over budget" in C# and QtNative; NDIAG-10 keeps two budgets (`refreshBudgetMs` of the screen, `FrameClockMs` in C#).
- (a) One budget: vvvv's frame interval, since QtNative renders in step with vvvv (E4).
- (b) The screen refresh.
- Recommended: (a); the names follow from the choice (one field, e.g. `BudgetMs`).
- Source: NDIAG-10, NDIAG-01, E13
- Decision: as recommended (Sebastian, 2026-11-19).

### C5 The unused import `vlqt_window_set_overlay`
C18 gives the Renderer only `Enable Keyboard Shortcuts`, no `Overlay` pin.
- Recommended: move the export to `vlqt_test.h` if a test uses it, otherwise delete it.
- Source: NDIAG-07, C18
- Decision: as recommended (Sebastian, 2026-11-19).

### C6 The "animations" row of the Scene page (private Qt API)
- Recommended: drop it, as NDIAG-17 recommends; D9 would allow it.
- Source: NDIAG-17
- Decision: as recommended (Sebastian, 2026-11-19).

### C7 Label of the F2 series
- Recommended: `work`, the same word as `WorkMs`.
- Source: NDIAG-01
- Decision: as recommended (Sebastian, 2026-11-19).

### C8 Wrapper functions for private Qt API
NRES-13 proposes `qt_private.h/.cpp`; D9 names only the list in the head comment of `elements.h`.
- Recommended: no wrapper file; the list of D9 is enough.
- Source: NRES-13, D9
- Decision: as recommended (Sebastian, 2026-11-19).

### C9 `vlqt_shader_reflect`: thread and async
- Recommended: document the calling thread and use the bake cache (NFX-13); an async variant only if a measurement asks for it.
- Source: NFX-13
- Decision: as recommended (Sebastian, 2026-11-19).

### C10 The layer item of a child in the bounds of a Group
If wave 1 confirms NELM-14:
- Recommended: count only tracked children.
- Source: NELM-14
- Decision: as recommended (Sebastian, 2026-11-19).

### C11 The last-good rule of the generated QML nodes
QML-05 keeps the last good pins on a read error at init, or on a parse error when the file is younger than 1 s.
- Recommended: keep the last good pins on every read or parse error (as SHD-05 for shaders), throttle the watcher; drop the age condition.
- Source: QML-05, E2, SHD-05
- Decision: as recommended (Sebastian, 2026-11-19).

### C12 Where the list of stylable element types lives
SIP-09: `Protocol.Type.TakesStyle(type)`, or `Element.TakesStyle`.
- Recommended: in `Reconciliation/`, next to the style writer, by element class; neither in the protocol nor in the element model (A3).
- Source: SIP-09, A3
- Decision: as recommended (Sebastian, 2026-11-19).

### C13 The six style process nodes: names and place
C5 (b) gives the setters `SetColors`, `SetFrame`, `SetText`, `SetIndicator`, `SetTrack` (getters `Colors` …); P2 had asked "`SetFrame` or `Frame`?". C6 makes the six bundles process nodes; TARGET.md 2.2 keeps the static style nodes in `Styles/`.
- Recommended: confirm the C5 names; the process nodes are `SetColorsNode` … `MergeNode` in `Nodes/Style/` (the folder is the tail of the category, S3); the static single setters and getters stay in `Styles/`.
- Source: SIP-01, SIP-10, C5, C6, P2
- Decision: as recommended (Sebastian, 2026-11-19).

### C14 Three pages: user page or design note?
`docs/topics/reconciler.md` (REC-03), `docs/input.md` (NCMD-11), the "Internals" section of `docs/effects-and-shaders.md` (NFX-04).
- Recommended: all three are contributor content and go to `docs/design/` (G5, DOC-12).
- Source: REC-03, NCMD-11, NFX-04, DOC-12, G5
- Decision: as recommended (Sebastian, 2026-11-19).

### C15 The word "run"
DOC-10 uses "run" for neighbouring merged effects that share one layer; TARGET.md 1.2 lists it only as an old word for "effect chain".
- Recommended: drop the word; write "neighbouring merged effects".
- Source: DOC-10, TARGET.md 1.2
- Decision: as recommended (Sebastian, 2026-11-19).

### C16 QML file names and the snake_case rule
S2 makes every file name in QtNative snake_case; a QML type file must start with an upper-case letter (`MultiStage.qml`).
- Recommended: QML files keep Qt's PascalCase, a named exception of S2.
- Source: NFX (pass note), S2
- Decision: as recommended (Sebastian, 2026-11-19).

### C17 The type parameter of `ChainMemo<TLink, TProps>`
O5 leaves "link" to one entry of a chain as sent.
- Recommended: `ChainMemo<TEffect, TProps>`.
- Source: NODX-05, O5
- Decision: as recommended (Sebastian, 2026-11-19).

### C18 Native string caps besides the 64 KB limit
The pass read "the native string caps go" (A4) as including the EV_ERROR cap of 4,096 bytes and left the 64-point cap of `EV_TOUCH_POINTS`.
- Recommended: confirm; the touch cap is a count of fingers, not a length.
- Source: NCMD-08, A4
- Decision: as recommended (Sebastian, 2026-11-19).

## D Open catalog rows

From [review/23_api.md](review/23_api.md), section 7, "Open (to decide)"; current values and reasons are there. The three `Error` rows of the generated nodes are point A3.

| # | Node / pin | Proposed | Recommended | Finding | Decision |
|---|---|---|---|---|---|
| D1 | QtTexture / Perf | remove (always empty) until QtNative has a perf ring for offscreen scenes | remove; the pin can come back as an addition | COMP-06 | as recommended |
| D2 | QtRenderer / two targets in one frame | the second target is not drawn, a hint at the node | as proposed | COMP-05 | as recommended |
| D3 | PerfStats / names of the values (the pins of Split) | names of RUN-10; group types `HostPerfStats`, `GcPerfStats` (TARGET.md 2.2) or `HostTimes`, `GcActivity` (RUN-10); `PerfSample.frame` → `frameInterval` | RUN-10's value names, TARGET's type names | RUN-10, NDIAG-10 | as recommended |
| D4 | PerfStats / `WorkMs`, `VsyncMs`, `BudgetMs` | `WorkMs` and `VsyncMs` in the main record, `BudgetMs` in `PerfPercentiles` | as proposed, budget name after C4 | NDIAG-11, NDIAG-01 | as recommended |
| D5 | TouchPoint (type) | `readonly record struct`, same four values | as proposed | ELM-13 | as recommended |
| D6 | FileWriter, FileWriter (Element) / 16-bit and float images | a warning, or a real 16-bit PNG (`Format_RGBA64`) | 16-bit where the format can hold it, a warning otherwise | NRES-12 | as recommended |
| D7 | generated QML nodes / `point`, `size` | `Vector2` for pins, outputs and signal arguments | as proposed | NFX-21 | as recommended |
| D8 | generated shader effect nodes / reserved uniform names | one list; warning or error; docs aligned | error at the node, one list (also E24) | NFX-14 | as recommended |
| D9 | generated QML nodes / order of the common pins | the order of QmlElement after the catalog | as proposed, one table for both | QML-04 | as recommended |
| D10 | AddArc / start of a full sweep | the oval starts at the Start Angle (Skia `addArc`) | as proposed (A7) | SIP-02 | as recommended |
| D11 | AddRoundRect / start point of the contour | Skia's start if the SkiaSharp comparison confirms it | as proposed | SIP-02 | as recommended |
| D12 | Spline (CatmullRom) / Tension, Alpha NaN | Tension clamped to 0..1, NaN rule documented | as proposed | SIP-15 | as recommended |
| D13 | QtImage / State after its node released it | a state that says so, instead of `Ready` | as proposed; the message goes to the log, the state carries no text (no `Error` pins) | SIP-14 | as recommended |
| D14 | Button / position of `On Click` | directly after `Anchor`, as `Channel` on the other widgets | as proposed | NODW-02 | as recommended |
| D15 | VideoSourceToQtImage / output | `Output` → `Image` | as proposed | NODW-05 | as recommended |
| D16 | Drag / Delta | `Translation` (as Pinch); `Delta` stays for per-frame motion | as proposed | NODX-02 | as recommended |
| D17 | Hover / Devices | optional `Devices`, if the native handler reads it | as proposed | NODX-02 | as recommended |
| D18 | AddCircle / pin order | `Center, Radius, Direction` if Skia's AddCircle has that order | check `VL.Skia.vl`, then follow Skia | NODX-13 | as recommended |

## E Open rows of the shader comparison

From [review/28_shader-reference.md](review/28_shader-reference.md), section 3, the rows with an empty Decision that propose a change; current values, TextureFX counterparts and reasons are there. Rows E2 to E5 follow A5, the default rows E8 to E14 follow the unit rows that E1 already decides.

| # | Node / pin | Proposed | Recommended | Finding | Decision |
|---|---|---|---|---|---|
| E1 | Blur / Max Radius | DIP (Qt's `blurMax` is in item pixels already); help and `protocol.h:416` say so | as proposed | SREF-02 | as recommended |
| E2 | Blur / Padding | DIP | after A5 | SREF-01 | follows from A5: DIP |
| E3 | DropShadow / Offset | `Vector2` in DIP | after A5 | SREF-01 | follows from A5: DIP |
| E4 | DropShadow / Offset default | `0.05, 0.05` units → `5, 5` DIP | after A5 | SREF-01 | follows from A5: DIP |
| E5 | DropShadow / Padding | DIP | after A5 | SREF-01 | follows from A5: DIP |
| E6 | ColorAdjust / Saturation | 0..2, 1 = unchanged (HSCB), the node sends `value - 1` | as proposed | SREF-04 | as recommended |
| E7 | ColorAdjust / Saturation default | `-1` → `0` (grey in the new scale) | as proposed | SREF-04 | as recommended |
| E8 | DirectionalBlur / Length default | `0.1` units → `10` DIP | as proposed | SREF-01 | as recommended |
| E9 | RadialBlur / Padding default | `0.3` units → `30` DIP | as proposed | SREF-01 | as recommended |
| E10 | ZoomBlur / Length default | `0.4` units → `40` DIP | as proposed | SREF-01 | as recommended |
| E11 | Glow / Radius default | `0.2` units → `20` DIP | as proposed | SREF-01 | as recommended |
| E12 | InnerShadow / Radius default | `0.1` units → `10` DIP | as proposed | SREF-01 | as recommended |
| E13 | InnerShadow / Offset default | `0.05, 0.05` units → `5, 5` DIP | as proposed | SREF-01 | as recommended |
| E14 | Displace / Displacement default | `0.1` units → `10` DIP | as proposed | SREF-01 | as recommended |
| E15 | Blend / Mode | `Blend Mode` (VL.Skia), explicit `Normal` | as proposed (A7) | SREF-05 | follows from A4 |
| E16 | every effect node / Apply | `Apply`, bool, default true, optional, last input pin; off passes the chain on | as proposed | SREF-06 | as recommended |
| E17 | shader files / `@optional` | the pin is optional | as proposed | SREF-07 | as recommended |
| E18 | shader files / `@tags`, `@remarks` | node browser tags and node help | as proposed | SREF-08 | as recommended |
| E19 | shader files / `@category` | a path without `Qt.` is appended to the default category of the node kind | as proposed | SREF-09 | as recommended |
| E20 | shader files / enum pins | not now: `int` plus `@summary` | as proposed | SREF-13 | as recommended |
| E21 | shader files / pin names from uniform names | also split before a digit (`pass1` → `Pass 1`) | as proposed | SREF-12 | as recommended |
| E22 | shader files / texel size and output size | `texelSize`, `dpr`, `viewSize` (DIP) on the element node too, if declared | as proposed | SREF-10 | as recommended |
| E23 | RGBSplit.frag (template) / amount | a distance in DIP (default `10`), `@padding distance` | as proposed, with E22 | SREF-11 | as recommended |
| E24 | shader files / reserved uniform names | one list, docs match | with D8 | NFX-14 | as recommended |

## After the round

1. Copy every decision into its source (Decision column, `Decided:` line, DECISIONS.md for A1 to A5 and the rules of block B).
2. Correct TARGET.md where a decision renames something it lists (B8 to B11, C13, C16, C17).
3. Cut the issues (milestone `0.10.0`, P13), with the machine per lane (P16) and the open issues of P21.
