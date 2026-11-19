# Review 15 – Effect, interaction and path nodes

Reviewer: Claude Opus 5.5, 2026-11-19. State: develop at 0.9.10.

Files read in full: `src/VL.Qt/Nodes/Effects/*.cs` except `BuiltinShaderLink.cs` (13 files), `src/VL.Qt/Nodes/Interaction/*.cs` (8 files), `src/VL.Qt/Nodes/Paths/*.cs` (17 files).
Skimmed: `Effects/BuiltinShaderLink.cs:37-118` (the `BuiltinShaderLink<TProps>` memo, to compare the patterns), `src/VL.Qt/Categories.cs`, `src/VL.Qt/NodeErrors.cs:16-30`, `src/VL.Qt/Paths/QtPath.cs:138-148`, the names of the tests in `src/VL.Qt.Tests` that touch the area, `C:/vl/VL.StandardLibs/VL.Skia/VL.Skia.vl` (pin names of CubicTo, AddRect, AddOval, AddRoundRect).
Not read: `src/VL.Qt/Elements/Effect.cs` and `Interaction.cs` beyond a search for `MaskEffect`; the help patches.

## 1 Summary

The three families are small, regular and mostly well documented. Each node is a `[ProcessNode]` class that extends an immutable chain (`Effect`, `Interaction`, `QtPath`) and returns the same instance while its inputs do not change, so the reconciler sends nothing. Effects and interaction share one memo shape (`EffectNode<TProps>`, `InteractionNode<TProps>`, `BuiltinShaderLink<TProps>`: input by reference plus a `readonly record struct Props`); the 23 path node classes write the same memo out by hand, field by field. No node allocates per frame while its inputs are unchanged.

The changes that matter most:

1. **Angle units differ inside the library** (NODX-01): DirectionalBlur and RadialBlur take degrees, Pinch and every path node take cycles, which is the vvvv convention.
2. **Interaction pin names do not form one vocabulary** (NODX-02): `Delta` means "offset since press" on Drag and "motion since last frame" on Wheel; `Is Clicked` is an event; `Is Dragging` and `Is Active` name the same kind of state.
3. **One memo for all chain nodes** (NODX-05): a single `ChainMemo<TLink, TProps>` replaces the four copies of the change check, deletes `EffectNode<TProps>` and shrinks each path node to a few lines.
4. **One way to write sink values into channels** (NODX-03, NODX-04): four write variants exist; Drag overwrites a freshly connected channel with (0, 0), which Pinch and Touch Points deliberately avoid.
5. **Explicit defaults and `<returns>` docs** (NODX-07, NODX-08): about 40 pins have no explicit default and none of the 19 effect and interaction nodes documents its Output pin.

## 2 What works and must survive

- **Reference-stable output.** Every node returns the previous instance while the input chain (by reference) and its own inputs are equal (`EffectNode.cs:28-38`, `InteractionNode.cs:79-91`, `MoveToNode.cs:28-38`). Downstream nodes and the reconciler rely on reference equality to skip work; a refactoring must keep it and the tests that assert it (`Is.SameAs` in `EffectShaderNodesTests`, `InteractionTests`, `Paths/AddNodeTests`, `Paths/GeneratorNodeTests`).
- **Hot paths: the `Update` method of every node in this area runs every frame.** Allowed per frame: building a `Props` record struct, comparing it, comparing spreads element-wise (`AddPolyNode.cs:39`). Not allowed: closures (all `create` delegates are `static` lambdas in static fields), boxing, building uniforms or spreads when nothing changed (uniform arrays are built inside `s_create` only, `GlowNode.cs:26-39`; `TouchPointsNode.cs:56-77` builds the spread only on a sink update).
- **Composition, not inheritance.** The public node holds an internal core as a field. A public sealed node cannot derive from an internal base class, and `[assembly: ImportAsIs]` would otherwise expose the base. Keep the field pattern.
- **Pull model with one frame latency.** Interaction nodes read the sink state of the last pump in their `Update` and say "Values arrive one frame later" in every summary. Events are drained every frame even without a channel (`ClickNode.cs:70-71`, `WheelNode.cs:54`) so they do not pile up. Values whose repetition is itself an event (a drag with the same delta, a new pinch with the same scale, touch points) are written on a sink update counter, not on value change (`DragNode.cs:99-104`, `PinchNode.cs:111-125`). Every write carries author `"Qt"` (`InteractionNode.cs:29`), so a patch can tell Qt's writes from its own.
- **Coordinate spaces are chosen by purpose:** Drag delta and Pinch translation/pivot are in the parent's space (the element moves under them), Point position and Touch Points are local to the element. Keep and keep documenting.
- **Two effect backends with different ordering semantics:** MultiEffect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask) are merged into one Qt `MultiEffect` (Qt's single shader item for these effects) and take Qt's fixed order inside a run; shader effect nodes keep the patch order. The summaries say so; the split of `EffectNode` and `BuiltinShaderLink` follows from it (MultiEffect links need no resources, shader links own shader slots and are `IDisposable`).
- **Paths follow Skia.** Each summary names the Skia node it mirrors, relative variants are separate nodes with the version suffix `(Relative)`, angles are in cycles, `null` input means the empty path (`input ??= QtPath.Empty`).

## 3 Findings

### NODX-01 Use cycles for the angles of DirectionalBlur and RadialBlur
- **Kind:** api
- **Severity:** must
- **Breaking:** yes: the Angle pins of DirectionalBlur and RadialBlur change from degrees to cycles; patches that set them must divide by 360.
- **Where:** `src/VL.Qt/Nodes/Effects/DirectionalBlurNode.cs:30,52,57`, `src/VL.Qt/Nodes/Effects/RadialBlurNode.cs:14,30,49,55`; for comparison `src/VL.Qt/Nodes/Interaction/PinchNode.cs:71` (rotation in cycles), `src/VL.Qt/Nodes/Paths/ArcToNode.cs:11,63,104`, `src/VL.Qt/Nodes/Paths/AddArcNode.cs:21` (cycles).
- **Finding:** vvvv measures angles in cycles (Skia, Stride, VL.CoreLib). Inside this area Pinch and all path nodes follow that (`ArcToNode.cs:11` even states it as a rule), but the two shader blurs take degrees and pass them on as the uniform `angle`. A user who drives a rotation and a blur from the same LFO gets a 360-fold mismatch, and the node help is the only place that tells.
- **Proposal:** Pins stay named `Angle`, unit cycles, docs "in cycles (1 = full turn)". Convert in `s_create`: DirectionalBlur `var a = 2 * MathF.PI * p.Angle;` and pass `Uniform.Float("angle", p.Angle * 360f)` (or change the shader uniform to radians and pass `2π·angle`; the shader files are area 12). RadialBlur default `8f` degrees becomes `[DefaultValue(0.02f)]` (≈ 7.2°). Update `HowTo Blur An Element.vl` / the help patch that uses the blurs.
- **Decided:** C10 and catalog → cycles on DirectionalBlur and RadialBlur `Angle`; RadialBlur default `0.05` cycles (not `0.02`) so that the effect shows at once
- **Effort:** S, mechanical: no
- **Touches:** `Effects/DirectionalBlurNode.cs`, `Effects/RadialBlurNode.cs`, the built-in shaders `directional_blur`, `radial_blur` only if the uniform changes, help patches using DirectionalBlur/RadialBlur, `src/VL.Qt.Tests/EffectShaderNodesTests.cs` if it checks uniform values.
- **Depends on:** Q1
- **Verification:** `EffectShaderNodesTests`; SmokeTest grab of the blur scenario unchanged for an angle of 1/45 cycle vs 8° before; help patch compile-check.

### NODX-02 Give the interaction nodes one pin vocabulary
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: pin and node renames (links in patches to renamed pins break and are fixed in the help patches).
- **Where:** `src/VL.Qt/Nodes/Interaction/DragNode.cs:62,73`, `WheelNode.cs:41,47`, `PinchNode.cs:69,72,80`, `ClickNode.cs:45,47,54,56`, `PointNode.cs:45,52`, `HoverNode.cs:43-45`, `TouchPointsNode.cs:21`.
- **Finding:** What a user gets out today:

  | Node | state | values and events | settings |
  |---|---|---|---|
  | Hover | Is Hovered | – | – |
  | Click | Is Pressed | Is Clicked (event), Tap Count, Long Pressed (event) | Buttons, Devices |
  | Drag | Is Dragging | Delta (offset since the press, parent space) | Moves Element, Axis, Minimum, Maximum, Buttons, Devices |
  | Pinch | Is Active | Scale, Rotation, Translation (since start, parent space), Pivot | Minimum/Maximum Point Count, Devices |
  | Point | Is Active | Position (local) | Buttons, Devices |
  | Wheel | – | Delta (motion since the last frame), Value (accumulated) | Devices |
  | Touch Points | – | Points | Devices, Occlusion |

  Problems for a patcher: (a) `Delta` has two meanings: per-frame motion on Wheel (the usual vvvv meaning, as on the mouse), accumulated offset since the press on Drag; Pinch calls the same accumulated quantity `Translation`, as does Qt's DragHandler. (b) `Is Clicked` is a bang (`IChannel<Unit>`), but `Is …` reads as a state like `Is Pressed`; its sibling event `Long Pressed` has no `Is`. (c) The running gesture is `Is Dragging` on Drag but `Is Active` on Pinch. (d) `Touch Points` is the only node name with a space; every other node in the three families is PascalCase (`DropShadow`, `ColorAdjust`, `MoveTo`). (e) Hover is the only node without `Devices`, although Qt's HoverHandler filters devices like the others.
- **Proposal:** Drag `Delta` → `Translation` (doc: "movement since the press … in the parent's space", same words as Pinch). Click `Is Clicked` → `Clicked`; keep `Long Pressed`. Rule for the summaries and future nodes: states `Is <verb-ed/-ing>`, events past tense without `Is`, per-frame differences `Delta`, values since start `Translation`/`Scale`/`Rotation`. Pinch `Is Active` → `Is Pinching` (Point keeps `Is Active`, its state is "a point is valid", not a gesture; see Q4). Node `Touch Points` → `TouchPoints` (class name already is). Optionally add `Devices` to Hover (needs `HoverInteraction` to carry devices; skip if the native handler does not read it).
- **Decided:** C13 (a) → Pinch `Is Active` → `Is Pinching`, Drag keeps `Is Dragging`, Point keeps `Is Active`; catalog → Click `Is Clicked` → `On Click` and `Long Pressed` → `On Long Press` (not `Clicked`; `Long Pressed` is not kept), rule `Is …` for states and `On …` for events, `Touch Points` → `TouchPoints`, Drag `Minimum`/`Maximum` → `Min`/`Max`, Pinch `Min Point Count`/`Max Point Count`
- **Effort:** M, mechanical: partly (renames yes, Hover devices no)
- **Touches:** `Interaction/DragNode.cs`, `ClickNode.cs`, `PinchNode.cs`, `TouchPointsNode.cs`, optionally `HoverNode.cs` and `src/VL.Qt/Elements/Interaction.cs`; help patches `HowTo Drag an Element.vl`, `HowTo React to Hover and Click.vl`, `HowTo Pinch to Zoom and Rotate.vl`, `HowTo Use Touch Points.vl`, `help/Help.xml` if a title changes; `tools/vl_pin_catalog.py` reads the C# and needs no edit; `src/VL.Qt.Tests/InteractionTests.cs` only where it names pins.
- **Depends on:** Q4
- **Verification:** `python tools/vl-patch-check.py` on the four help patches (no `missing-pin`), `tools/vvvvc.ps1 -File` each; `InteractionTests`.

### NODX-03 Drag must not overwrite a freshly connected Delta channel before any drag
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes: a channel connected to Drag's Delta keeps its own value until the first drag instead of being set to (0, 0).
- **Where:** `src/VL.Qt/Nodes/Interaction/DragNode.cs:93-98`; the rule it breaks: `TouchPointsNode.cs:56-58,66-72`, `PinchNode.cs:95-96,118-124`.
- **Finding:** On a new or changed channel Drag calls `delta.EnsureValue(sink.DragDelta())` unconditionally. Before the first drag the sink holds (0, 0), so a channel that holds a value (a global channel, a value restored from a preset) is reset at node creation. Pinch and Touch Points write on connect only `if (updates != 0)`, with the comment "an untouched sink would overwrite the value of the channel for nothing". Same situation, opposite behaviour.
- **Proposal:** Use the same rule as Pinch: on connect write only when `updates != 0`, with `SetValueAndAuthor`. With NODX-04 this falls out of the shared helper.
- **Effort:** S, mechanical: no
- **Touches:** `Interaction/DragNode.cs`, `src/VL.Qt.Tests/InteractionTests.cs` (the delta test around line 585 expects the current behaviour on the first `Update`; adapt it).
- **Depends on:** none (simplest done together with NODX-04)
- **Verification:** new test in `InteractionTests`: channel with (0.3, 0), `DragNode.Update` without sink updates → channel still (0.3, 0); after a drag update → written with author "Qt".

### NODX-04 One set of channel writers in the interaction core
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/Interaction/InteractionNode.cs:97-117`; state writes with the same `if valid … else last = null` wrapper: `ClickNode.cs:65-68,76-79`, `DragNode.cs:85-88`, `HoverNode.cs:51-54`, `PinchNode.cs:98-101`, `PointNode.cs:61-69`; update-counter writes in three versions: `DragNode.cs:90-104`, `PinchNode.cs:111-125` (`Pull`), `TouchPointsNode.cs:59-77`; per-frame motion: `WheelNode.cs:55-74`.
- **Finding:** Four ways to write a sink value exist, three of them copied: the null check around `Write` is repeated six times, and "write on counter change and on reconnect" is written three times with a behavioural difference (NODX-03). A reader who adds a node cannot tell which variant is the rule. `Pull` in `PinchNode` is generic but private and references `InteractionNode<Props>.Author` of its own `Props`.
- **Proposal:** In the interaction core (renamed per NODX-05) keep three static helpers and use them everywhere:
  ```csharp
  // State: writes on change or when the channel is another one; forgets the channel when unconnected.
  public static void WriteState<T>(IChannel<T>? channel, ref IChannel<T>? last, ref T lastValue, T value) where T : struct, IEquatable<T>;
  // Events: one Unit per event; nothing without a channel.
  public static void WriteEvents(IChannel<Unit>? channel, int count);
  // Values whose repetition is an event: true when the caller has to write (sink updated, or channel newly connected after an update).
  public static bool TakeUpdate<T>(IChannel<T>? channel, ref IChannel<T>? last, ref int lastUpdates, int updates);
  ```
  Callers: `if (TakeUpdate(points, ref _points, ref _lastUpdates, sink.TouchUpdates())) points!.SetValueAndAuthor(sink.TouchPoints(), Author);` keeps the lazy spread. Wheel stays special (per-frame motion plus reset) but uses `Author` from the core. Delete `PinchNode.Pull`.
- **Decided:** A1 → `ElementSink` and `SinkSlot` are `ElementFeedback` and `FeedbackSlot` (`Element.Feedback`); the examples follow the new names
- **Effort:** S, mechanical: no
- **Touches:** `Interaction/InteractionNode.cs`, `ClickNode.cs`, `DragNode.cs`, `HoverNode.cs`, `PinchNode.cs`, `PointNode.cs`, `TouchPointsNode.cs`
- **Depends on:** NODX-03 (decides the reconnect rule)
- **Verification:** `InteractionTests` unchanged apart from NODX-03.

### NODX-05 One memo type for all chain nodes
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/Effects/EffectNode.cs:13-39`, `src/VL.Qt/Nodes/Interaction/InteractionNode.cs:79-91`, `src/VL.Qt/Nodes/Effects/BuiltinShaderLink.cs:73-83`, and the 23 hand-written memos in `src/VL.Qt/Nodes/Paths/*.cs` (e.g. `MoveToNode.cs:19-38`, `ArcToNode.cs:93-130`, `CubicToNode.cs:14-43`); the pattern is described only in a free comment `MoveToNode.cs:10-13`, referenced from `LineToNode.cs:10`.
- **Finding:** The same rule ("new link only if the input chain by reference or the node's own inputs changed") is implemented four times. Effects and interaction use a `Props` record struct and a generic helper; every path node instead declares one field per pin and a long `if` with one `!=` per pin (up to seven, `ArcToNode.cs:119`), so a pin added to `Update` but forgotten in the `if` silently stops updating. `EffectNode<TProps>` and `InteractionNode<TProps>` are named like nodes but are helpers; `EffectNode` adds nothing to the memo except the test counter `Created`.
- **Proposal:** Add `src/VL.Qt/Nodes/ChainMemo.cs`:
  ```csharp
  /// <summary>Remembers the last link of a chain node: returns it while the input link (by reference) and the props are equal.</summary>
  internal struct ChainMemo<TLink, TProps> where TLink : class where TProps : struct, IEquatable<TProps>
  {
      TLink? _input; TProps _props; TLink? _output;
      public TLink Get<TState>(TLink? input, in TProps props, TState state, Func<TLink?, TProps, TState, TLink> create);
      public void Reset();
  }
  ```
  (`TState` carries the `SinkSlot` for interaction and the shader ids for shader effects without a closure.) Then: delete `EffectNode.cs`, the five MultiEffect nodes hold `ChainMemo<Effect, Props>` directly; `InteractionNode<TProps>` → `InteractionNodeCore<TProps>` (same suffix as `ShapeNodeCore`) uses it for `Describe`; `BuiltinShaderLink` uses it with the ids as state (its id check moves into `Props` equality or stays as one extra comparison). Each path node gets `readonly record struct Props(...)` and a `static readonly Func<QtPath?, Props, QtPath>` (`input ?? QtPath.Empty` inside); for the four spread nodes `Props` implements `Equals(Props)` with content comparison (NODX-06). The template comment in `MoveToNode.cs:10-13` becomes the summary of `ChainMemo` plus one line in `docs/architecture.md` (nodes section); `LineToNode.cs:10` goes.
- **Decided:** O3 → `InteractionNodeCore<TProps>` and `EffectNode<>` goes, as proposed; O5/TARGET 2.2 → `BuiltinShaderLink` is `ShaderEffectBuilder` (`Nodes/Effects/ShaderEffectBuilder.cs`) and builds a `ShaderEffect : Effect`; A1 → the `SinkSlot` carried as `TState` is a `FeedbackSlot`; C17 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the type parameter is `TEffect`: `ChainMemo<TEffect, TProps>`
- **Effort:** M, mechanical: partly (paths yes once one is done)
- **Touches:** new `Nodes/ChainMemo.cs`; delete `Effects/EffectNode.cs`; `Effects/BlurNode.cs`, `ColorAdjustNode.cs`, `ColorizeNode.cs`, `DropShadowNode.cs`, `MaskNode.cs`, `BuiltinShaderLink.cs`; `Interaction/InteractionNode.cs` (renamed file `InteractionNodeCore.cs`) and the seven interaction nodes (type name only); all files in `Paths/`; tests that read `Core.Created` (`EffectTests.cs`, `EffectShaderNodesTests.cs`, `InteractionTests.cs`).
- **Depends on:** NODX-06; NODX-10 for the test side
- **Verification:** all `Is.SameAs` assertions in `Paths/*NodeTests.cs`, `EffectShaderNodesTests`, `InteractionTests` pass; add `Is.SameAs` checks for the builder nodes (NODX-10).

### NODX-06 One content comparison for spreads
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/Paths/AddPolyNode.cs:39-51`, `src/VL.Qt/Nodes/Paths/BezierHandlesNode.cs:62-74` (`SameModes`), `src/VL.Qt/Nodes/ShapeNodeCore.cs:217,226`, `src/VL.Qt/Reconciler/PropertyDiff.cs:306,318`; callers with null-forgiving `_anchors!`: `BezierHandlesNode.cs:46-48`, `SplineBSplineNode.cs:30`, `SplineCatmullRomNode.cs:40`.
- **Finding:** Six copies of the same loop for four element types. Two path nodes have their own private copy although `ShapeNodeCore.SameContent` exists; the shared one takes non-null arguments, so the spline and Bézier nodes pass `_points!` while `_points` is null on the first call (safe only because `_output == null` short-circuits first). The loops compare with `!=`, so a `NaN` in a spread or pin counts as a change every frame and the node allocates a new path per frame.
- **Proposal:** `src/VL.Qt/SpreadEquality.cs`: `internal static bool SameContent<T>(Spread<T>? a, Spread<T>? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b.AsSpan()));` (`MemoryExtensions.SequenceEqual` uses `EqualityComparer<T>.Default`, works for enums, no allocation, treats `NaN` as equal via `float.Equals`). Delete the five other copies (the reconciler ones belong to area 05, see section 6).
- **Decided:** TARGET 3.2 → NODX-06 also replaces the copies in `Reconciliation/PropertyDiff.cs` and `Nodes/ShapeNodeCore.cs` (they are not left to the reconciler issue); `SpreadEquality.cs` stays in the project root (namespace `VL.Qt`)
- **Effort:** S, mechanical: yes
- **Touches:** new `src/VL.Qt/SpreadEquality.cs`; `Paths/AddPolyNode.cs`, `BezierHandlesNode.cs`, `SplineBSplineNode.cs`, `SplineCatmullRomNode.cs`; `Nodes/ShapeNodeCore.cs` and its callers
- **Depends on:** none
- **Verification:** `Paths/AddNodeTests`, `Paths/GeneratorNodeTests` (same-content spreads give `Is.SameAs`); a new case with `NaN` in the points.

### NODX-07 Set every default explicitly, in one style, and choose visible ones
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: only where a default changes to a visible value (AddRect, AddOval, AddArc, AddRoundRect); explicit `0`/`false` defaults change nothing.
- **Where:** without explicit default: `Effects/BlendNode.cs:79` (mode), `BlurNode.cs:37,39`, `ColorAdjustNode.cs:31-32`, `DirectionalBlurNode.cs:57`, `DropShadowNode.cs:43`, `GlowNode.cs:66,68`, `InnerShadowNode.cs:68`, `MaskNode.cs:43-44,46-47`, `Paths/MoveToNode.cs:28,54`, `LineToNode.cs:25,51`, `QuadToNode.cs:25,54`, `CubicToNode.cs:28-31,64-67`, `ArcToNode.cs:32-34,70-73,112-116,156-160`, `AddPolyNode.cs:24`, `BezierHandlesNode.cs:33-35`, `SplineBSplineNode.cs:24`, `SplineCatmullRomNode.cs:31`. Three styles in Paths: C# default only (`AddArcNode.cs:25`), `[DefaultValue]` and C# default together (`SplineCatmullRomNode.cs:33-34`), none (`LineToNode.cs:25`). Empty defaults: `AddArcNode.cs:25`, `AddOvalNode.cs:23`, `AddRectNode.cs:23`, `AddRoundRectNode.cs:25` vs a visible one in `AddCircleNode.cs:25`. Pointer comments "Pin defaults: see RectangleNode": `ColorizeNode.cs:28`, `DropShadowNode.cs:34`, `GlowNode.cs:63`, `InnerShadowNode.cs:64`.
- **Finding:** AGENTS.md asks for an explicit default on every pin. About 40 pins rely on the type default, so a reader cannot tell "0 on purpose" from "forgotten" (e.g. `Blur.Multiplier`, `Mask.Threshold Min`). The path family mixes three styles, one of them stating the same default twice. AddRect, AddOval, AddArc and AddRoundRect create nothing visible when placed, AddCircle does (radius 0.5), against the rule "choose defaults so the effect is visible". The four pointer comments send the reader to another file for a rule AGENTS.md already states (and AGENTS.md shows the `typeof` form while these files use the string-only form; see section 6).
- **Proposal:** Effects and interaction: `[DefaultValue(...)]` on every value pin (`0f`, `false`, `typeof(BlendMode), nameof(BlendMode.Normal)`, `typeof(RectangleF), "0, 0, 0, 0"`). Paths: C# parameter defaults only (they are pure value pins, VL reads them), on every pin, struct pins via `[DefaultValue(typeof(Vector2), "…")]` where C# cannot express the value. Visible defaults: AddRect/AddRoundRect `rectangle` `"-0.5, -0.5, 1, 1"`, AddRoundRect radius `"0.1, 0.1"`, AddOval/AddArc `bounds` `"-0.5, -0.5, 1, 1"`, AddArc `sweepAngle = 0.25f`. Delete the four pointer comments.
- **Decided:** D3 (b) → `[DefaultValue]` is written only where the default differs from `default(T)` (never for `Matrix`, whose vvvv default is the identity, A2 of [DECISION-ROUND.md](../DECISION-ROUND.md)), so the explicit `0f`, `false`, `BlendMode.Normal` and `"0, 0, 0, 0"` of the proposal are not added; catalog → AddRect and AddRoundRect `Rectangle` default `0, 0, 1, 1`, AddOval and AddArc `Bounds` default `0, 0, 1, 1` (not `-0.5, -0.5, 1, 1`), AddArc `Sweep Angle` `0.25`, AddRoundRect `Radius` `0.1, 0.1`
- **Effort:** M, mechanical: yes
- **Touches:** every node file of the three folders except `ClickNode`, `DragNode`, `HoverNode`, `PinchNode`, `PointNode`, `TouchPointsNode`, `WheelNode`, `AddPathNode`, `CloseNode`; help patches whose IOBoxes now hold the default (`vl-patch-check` reports `default-iobox`).
- **Depends on:** none
- **Verification:** `python tools/vl-patch-check.py` (no new `hidden-non-default`), `tools/vvvvc.ps1 -File` for `HowTo Build a Path.vl`, `HowTo Draw a Path.vl`, `HowTo Chain Effects.vl`; `Paths/AddNodeTests`.

### NODX-08 Document the Output pin and say the chain rule once
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** no `<returns>` on `Update` in all 12 effect nodes (e.g. `BlendNode.cs:68-74`) and all 7 interaction nodes (e.g. `ClickNode.cs:42-50`); generic summary "Adds the effect to the effect chain." in `BlendNode.cs:68`, `DirectionalBlurNode.cs:50`, `DisplaceNode.cs:53`, `GlowNode.cs:53`, `InnerShadowNode.cs:54`, `RadialBlurNode.cs:46`, `ZoomBlurNode.cs:47`; the chain rule in two wordings ("data flow order: the node at the Effect pin acts last", `BlendNode.cs:36-37` and six more; "between runs the patch order applies", `BlurNode.cs:13-14` and four more); broken wraps in the middle of a sentence `BlurNode.cs:11-12`, `ColorAdjustNode.cs:9-10`, `ColorizeNode.cs:10-11`, `DropShadowNode.cs:11-12`, `MaskNode.cs:15-16`; "DIP" in `GlowNode.cs:15` where every other doc says "units".
- **Finding:** AGENTS.md asks for `<returns>` on the return pin; the path nodes have it, the other two families do not, so the Output pin shows no help in vvvv. The `Update` summary of the shader effects says nothing the node name does not. The same ordering rule is phrased two ways, which makes a user wonder whether the two families behave differently between runs (they do not).
- **Proposal:** `<returns>` for effects: "The effect chain with this effect added; connect it to the Effect pin of an element or to the next effect node." For interaction: "The interaction chain with this handler added; connect it to the Interaction pin of an element or to the next interaction node." `Update` summaries of shader effects name the effect ("Adds a glow to the effect chain."). One shared sentence pair, used verbatim in all 12 effect summaries: "Connect Output to the Effect pin of an element, or chain it with other effect nodes: the node at the Effect pin acts last. Neighbouring Blur, DropShadow, ColorAdjust, Colorize and Mask nodes are merged into one layer and take Qt's fixed order among themselves." Remove the shader-specific half sentence from the MultiEffect ones and vice versa. "DIP" → "units".
- **Decided:** E1 and "Shaders follow VL.Stride" → parameters of shader effects are in DIP, not in renderer units, so `DIP` in `GlowNode.cs:15` is not replaced by "units" as written; O4/TARGET 1.2 → the shared sentence calls Blur, DropShadow, ColorAdjust, Colorize and Mask the "merged effects" and the others "shader effects", and uses "layer" only in Qt's sense
- **Effort:** S, mechanical: yes
- **Touches:** all files in `Effects/` except `BuiltinShaderLink.cs` and `EffectNode.cs`; all node files in `Interaction/`
- **Depends on:** none
- **Verification:** build has no XML doc warnings; node help in vvvv (manual-check) shows Output help.

### NODX-09 Move the input error of Drag and Pinch into the interaction core
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/Interaction/DragNode.cs:31,36-49,57,119-126,130-132`, `PinchNode.cs:33,44-57,65,127-134,138-140`; message paths in the core: `InteractionNode.cs:45-49` (warning via `AddPersistentMessage`), `:129-141` (sink "error" shown as warning via `NodeErrors.Warn`).
- **Finding:** Drag and Pinch carry an identical copy of `_addError`, `_inputErrorMessage`, `InputError`, `SetInputError` and the disposal, and need a second constructor parameter for it. The core shows messages in three ways (persistent warning, `NodeErrors` warning for a property called `Error`, persistent error in the node), so a reader cannot tell which channel a new message should use.
- **Proposal:** `InteractionNodeCore<TProps>` gets `public void SetInputError(string? error)` and `InputError`, shown with severity Error through the same message delegate the core already holds (constructor takes `Func<string, MessageSeverity, IDisposable>` for tests). Rename the core's `Error` to `HandlerError` (it is shown as a warning). Drag and Pinch lose their fields and their second constructor body.
- **Effort:** S, mechanical: no
- **Touches:** `Interaction/InteractionNode.cs`, `DragNode.cs`, `PinchNode.cs`, and the test constructors of all seven interaction nodes; `src/VL.Qt.Tests/InteractionTests.cs` (6 uses of `InputError`, 3 of `Core.Error`)
- **Depends on:** NODX-05 (rename)
- **Verification:** `InteractionTests` (input error cases for Drag limits and Pinch counts).

### NODX-10 Replace the test hooks of the area (K6) and close the gap in builder-node tests
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `internal … Core` property on every effect and interaction node (e.g. `BlendNode.cs:66`, `ClickNode.cs:40`); `Created` counters `EffectNode.cs:24-25`, `InteractionNode.cs:72-73`, `BuiltinShaderLink.cs:70`; test constructors `ClickNode.cs:33-35`, `DragNode.cs:44-49`, `HoverNode.cs:30-32`, `PinchNode.cs:52-57`, `PointNode.cs:34-36`, `TouchPointsNode.cs:33-35`, `WheelNode.cs:30-32`, and `internal XNode(ResourceClient? client, NodeErrors? errors = null)` on the seven shader nodes; `InputError` on Drag and Pinch. Uses in tests: `Core.Sink` 35, `Core.Created` 9, `Core.Slot` 5, `Core.Warning` 4, `Core.Error` 3, `InputError` 6. Builder path tests: `src/VL.Qt.Tests/Paths/BuilderNodeTests.cs` has 2 `Is.SameAs` checks for 15 builder classes.
- **Finding:** Every node exposes its core only for tests. Most uses have a test that works on the public surface: `Created` measures exactly what `Is.SameAs` on two outputs measures; the sink can be reached from the link the node returns (the interaction link holds its `SinkSlot`). The reference stability of the 15 builder path nodes, the property a downstream Path element depends on, is checked for two of them.
- **Proposal:** Replace `Core.Created` assertions by `Is.SameAs`/`Is.Not.SameAs` on the outputs and delete `Created` (with NODX-05 it has no home). Replace `Core.Sink`/`Core.Slot` by a test helper `InteractionTestHelpers.SinkOf(Interaction link)` that reads the slot from the returned link; then delete `Core` on interaction nodes. Keep one internal constructor per node as the dependency seam (message delegate, `ResourceClient`), documented as such instead of "For tests". Add a parametrised test in `BuilderNodeTests` that calls each builder twice with equal inputs and asserts `Is.SameAs`, and once with a changed pin and asserts `Is.Not.SameAs` (catches a pin forgotten in the memo).
- **Decided:** P22 → NODX-10 is one of the K6 findings and is worked under TEST-07; F5 → the tests named are NUnit tests; A1 → the test helper reads the `FeedbackSlot` (`FeedbackOf`, was `SinkOf`)
- **Effort:** M, mechanical: partly
- **Touches:** all node files of `Effects/` and `Interaction/`; `src/VL.Qt.Tests/EffectTests.cs`, `EffectShaderNodesTests.cs`, `InteractionTests.cs`, `Paths/BuilderNodeTests.cs`, new `src/VL.Qt.Tests/InteractionTestHelpers.cs`
- **Depends on:** NODX-05, NODX-09
- **Verification:** `dotnet test src/VL.Qt.Tests` green with the hooks removed.

### NODX-11 Put Effects and Interaction nodes in the namespace of their folder; drop dead usings
- **Kind:** structure
- **Severity:** could
- **Breaking:** yes: C# users of the library see `VL.Qt.Nodes.Effects.BlendNode` etc.; vvvv categories are set explicitly and do not change.
- **Where:** `namespace VL.Qt.Nodes` in all files of `Effects/` and `Interaction/` (e.g. `BlendNode.cs:10`, `ClickNode.cs:10`) vs `namespace VL.Qt.Nodes.Paths` in `Paths/` (e.g. `AddArcNode.cs:6`); unused `using System;` and `using System.ComponentModel;` in `Paths/CloseNode.cs:1-2`, `LineToNode.cs:1-2`, `MoveToNode.cs:1-2`, `QuadToNode.cs:1-2`, `CubicToNode.cs:1-2`, `ArcToNode.cs:1-2` (System unused there; ComponentModel unused).
- **Finding:** One of three sibling folders follows the folder-namespace rule. The usings suggest attributes that the files do not use.
- **Proposal:** `VL.Qt.Nodes.Effects`, `VL.Qt.Nodes.Interaction`, `VL.Qt.Nodes.Paths`, unless the API review (01/10) decides that all nodes live in `VL.Qt.Nodes`; then move Paths there instead. Remove the unused usings.
- **Decided:** S1/TARGET 2.1 rule 3 → all node classes are in `VL.Qt.Nodes`: `Nodes/Paths/` moves from `VL.Qt.Nodes.Paths` to `VL.Qt.Nodes`, `Effects/` and `Interaction/` keep their namespace (no `VL.Qt.Nodes.Effects` or `.Interaction`); the unused usings go as proposed; the Breaking line names `VL.Qt.Nodes.Paths` only
- **Effort:** S, mechanical: yes
- **Touches:** all files of the three folders; test files' `using` lines
- **Depends on:** the namespace decision of reviews 01/10
- **Verification:** build; `tools/vvvvc.ps1` on the help patches of the area (node identity in `.vl` is by name and category, check that no patch breaks).

### NODX-12 Say why the padding factors of the shader effects are what they are
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `Effects/DirectionalBlurNode.cs:29` (`length / 2 + length / 64`), `GlowNode.cs:29` and `InnerShadowNode.cs:30` (`1.5f * radius`), `ZoomBlurNode.cs:29` (`0.6f * length`), `DisplaceNode.cs:31`.
- **Finding:** The effect area each shader effect requests is computed with unexplained factors. A person changing a shader cannot tell whether `1.5` is the reach of the blur pyramid or a safety margin, and whether it must follow the shader.
- **Proposal:** One comment line above each computation naming what the factor covers (e.g. "the pyramid blur reaches about 1.5 × radius", "1/64: the tap spacing of the two-pass blur"), taken from the shader source in area 12; as named constants where two nodes share one (`Glow`, `InnerShadow`).
- **Effort:** S, mechanical: no
- **Touches:** the five files listed
- **Depends on:** none
- **Verification:** `EffectPaddingTests` unchanged.

### NODX-13 Make the path pin names agree with Skia and with each other
- **Kind:** naming
- **Severity:** could
- **Breaking:** yes: renamed pins on QuadTo/CubicTo or AddCircle break links in patches.
- **Where:** `Paths/CubicToNode.cs:31,67` (`EndPoint`) vs `QuadToNode.cs:25,54` (`End Point`); `AddCircleNode.cs:25` (pin order Center, Dir, Radius); `Dir` in `AddCircleNode.cs:25`, `AddRoundRectNode.cs:25` vs `Direction` in `AddOvalNode.cs:23`, `AddRectNode.cs:23`.
- **Finding:** VL.Skia's CubicTo has the pins `Control Point 1`, `Control Point 2`, `EndPoint` (`C:/vl/VL.StandardLibs/VL.Skia/VL.Skia.vl:37887ff`); Qt's CubicTo copies that, Qt's QuadTo spells `End Point`. Skia itself mixes `Dir` (AddRoundRect, `VL.Skia.vl:40352ff`) and `Direction` (AddRect `:37631ff`, AddOval `:37496ff`), and Qt copies the mix. AddCircle puts `Dir` between `Center` and `Radius`; the SkiaSharp call inside VL.Skia's AddCircle takes X, Y, Radius, Dir. I did not verify the outer pin names of Skia's QuadTo and AddCircle.
- **Proposal:** Look up the outer pins of Skia's QuadTo and AddCircle, then: where Skia has the same node, use Skia's pin names and order exactly; otherwise use the spaced form (`End Point`, `Direction`). Recommended outcome if Skia's QuadTo uses `EndPoint`: both Qt nodes `EndPoint`; AddCircle order `Center, Radius, Dir`.
- **Decided:** C12 (b) and catalog → `Direction` on AddCircle and AddRoundRect (was `Dir`), `End Point` on CubicTo and CubicTo (Relative) (was `EndPoint`; QuadTo already has it); no lookup of Skia's outer pins is needed
- **Effort:** S, mechanical: yes
- **Touches:** `Paths/QuadToNode.cs` or `CubicToNode.cs`, `AddCircleNode.cs`; help patches `HowTo Build a Path.vl`, `HowTo Draw a Path.vl` if they link those pins
- **Depends on:** Q3
- **Verification:** `vl-patch-check.py` (no `missing-pin`) and `vvvvc.ps1` on the path help patches.

## 4 Target structure

```
src/VL.Qt/
  SpreadEquality.cs                      new: SameContent<T>(Spread<T>?, Spread<T>?) (NODX-06)
  Nodes/
    ChainMemo.cs                         new: ChainMemo<TLink, TProps>, the memo of every chain node (NODX-05)
    Effects/                             namespace VL.Qt.Nodes.Effects (NODX-11)
      EffectNode.cs                      deleted (NODX-05)
      BlendMode.cs                       split out of BlendNode.cs (K3): enum BlendMode
      BlendNode.cs                       Blend, shader effect
      BlurNode.cs, ColorAdjustNode.cs, ColorizeNode.cs, DropShadowNode.cs, MaskNode.cs
                                         MultiEffect nodes, hold ChainMemo<Effect, Props>
      DirectionalBlurNode.cs, DisplaceNode.cs, GlowNode.cs, InnerShadowNode.cs, RadialBlurNode.cs, ZoomBlurNode.cs
                                         shader effect nodes, hold BuiltinShaderLink<Props>
      BuiltinShaderLink.cs               (area of review 12/10; uses ChainMemo; its K3 split is theirs)
    Interaction/                         namespace VL.Qt.Nodes.Interaction (NODX-11)
      InteractionNode.cs → InteractionNodeCore.cs   InteractionNodeCore<TProps>: sink, ChainMemo, messages
                                         (warning, handler error, input error), WriteState/WriteEvents/TakeUpdate
      ClickNode.cs, DragNode.cs, HoverNode.cs, PinchNode.cs, PointNode.cs, WheelNode.cs
      TouchPointsNode.cs                 node name "Touch Points" → "TouchPoints" (NODX-02)
    Paths/                               namespace VL.Qt.Nodes.Paths
      MoveToNode.cs                      MoveToNode only; MoveToRelativeNode → MoveToRelativeNode.cs (K3)
      LineToNode.cs                      LineToNode; LineToRelativeNode → LineToRelativeNode.cs
      QuadToNode.cs                      QuadToNode; QuadToRelativeNode → QuadToRelativeNode.cs
      CubicToNode.cs                     CubicToNode; CubicToRelativeNode → CubicToRelativeNode.cs
      ArcToNode.cs                       split → ArcToTangentNode.cs, ArcToOvalNode.cs, ArcToSvgNode.cs, ArcToRelativeNode.cs;
                                         the header comment (lines 10-11) goes to the summaries of the four classes
      CloseNode.cs, AddArcNode.cs, AddCircleNode.cs, AddOvalNode.cs, AddPathNode.cs, AddPolyNode.cs,
      AddRectNode.cs, AddRoundRectNode.cs, BezierHandlesNode.cs, SplineBSplineNode.cs, SplineCatmullRomNode.cs
                                         unchanged files; each: Props record struct + static build + ChainMemo<QtPath, Props>
```

Public: the node classes and `BlendMode` (pin type). Internal: `ChainMemo`, `InteractionNodeCore`, `BuiltinShaderLink`, `SpreadEquality`, all `Props`. No `Core` property, no `Created` counter (NODX-10).

## 5 Questions for Sebastian

**Q1 Angle unit for effects.** (a) Cycles everywhere, as in vvvv, Pinch and the path nodes: DirectionalBlur and RadialBlur change unit. (b) Keep degrees for the two blurs because CSS and Qt use degrees. Consequence of (b): one library with two angle units, users must read the pin help. Recommendation: (a), see NODX-01.

**Q2 Effect categories.** Today `Qt.Effects` holds the shader effects and `Qt.Effects.MultiEffect` (`src/VL.Qt/Categories.cs:21,27`) holds Blur, DropShadow, ColorAdjust, Colorize and Mask, so Blur and DirectionalBlur sit in different categories. (a) Keep the split: the category tells that these five merge into one layer and reorder among themselves. (b) One category `Qt.Effects`; the merge is explained in the summaries (NODX-08) and in `HowTo Chain Effects`. Consequence of (b): a category change for five nodes (patches reference nodes by category and need the help patches updated). Recommendation: (b); the backend name "MultiEffect" means nothing to a patcher who does not know Qt, and the reordering rule is in every summary anyway.

**Q3 Mirror Skia's quirks in path pin names?** Skia uses `Dir` on AddRoundRect, `Direction` on AddRect/AddOval and `EndPoint` on CubicTo. (a) Copy Skia exactly where a Skia node of the same name exists, so a patch moves between libraries by swapping nodes. (b) Normalise to spaced names (`Direction`, `End Point`). Recommendation: (a), the path nodes are explicitly "modelled on Skia.Paths" (`Categories.cs:23`); see NODX-13.

**Q4 Name of the running-gesture state.** (a) Specific: `Is Hovered`, `Is Pressed`, `Is Dragging`, `Is Pinching`, Point `Is Active`. (b) Uniform `Is Active` on Drag, Pinch and Point (Qt's own term for handlers). Recommendation: (a), it reads without the help and matches Hover and Click; see NODX-02.

## 6 For other areas

- Reconciler (05): `PropertyDiff.SameContent` (`src/VL.Qt/Reconciler/PropertyDiff.cs:306,318`) duplicates the spread comparison; move to `SpreadEquality` (NODX-06).
- Element nodes (10): `ShapeNodeCore.SameContent` (`src/VL.Qt/Nodes/ShapeNodeCore.cs:217,226`) same; the pointer comments "Pin defaults: see RectangleNode" in `CircleNode.cs:46`, `TextNode.cs:86`; `RectangleNode.cs:66` says the string-only `[DefaultValue("…")]` works because VL deserializes it, while AGENTS.md prescribes `[DefaultValue(typeof(T), "…")]`: decide one form and update AGENTS.md or the code.
- Element nodes (10): `InteractionNode.cs:11` calls `ElementNode<TElement, TProps>` its counterpart; if `ElementNode` gets a `…Core` name, keep the three cores named alike.
- Shaders (12): `BuiltinShaderLink.cs:70` `Created` counter is a K6 hook; with NODX-05 its memo becomes `ChainMemo`.
