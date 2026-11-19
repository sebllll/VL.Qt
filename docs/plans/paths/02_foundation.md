# Paths 02: Foundation – protocol value types, SHAPE element in QtNative, `Shape` element and `QtPath` core in C#

Milestone 1.0 · Labels: `enhancement`, `area:native`, `area:csharp` · Depends on: Paths 01 · Plan: `docs/plans/paths/02_foundation.md`, overview `docs/plans/paths/00_overview.md`

## Goal
End-to-end path from a C# `Shape` element to a drawn `QQuickShape`, without nodes: protocol value types for point lists and paths, the SHAPE element in QtNative with `VlPathElement`, the immutable `Shape` element and the `QtPath` core type in C#, reconciler diff, tests and a SmokeTest scenario.

## Context
- Read `docs/research/02_shapes-spike.md` first and reuse the prototype from the spike branch.
- How an element travels (Rectangle as template): `src/VL.Qt/Elements/Rectangle.cs`, `ElementType.cs:97-114`, `Reconciler/PropertyDiff.cs` (`s_rectangle` :22-36, `DefaultOf` :38-55, `switch` in `DiffProperties` :57-117, `DiffRectangle` :166-176), `ICommandSink.cs:38-44`, `CommandWriter.cs` (`SetString` :171-177 is the template for variable length, `BeginSet` :561-568, `MaxPayload` :557), `src/VL.Qt.Native/commands.cpp` (`valueSize` :66-85, `Scene::set` :644-720), `elements.cpp` (`typeTable()` :921-941, `rectangleProps()` :203-212, typed setter `setQmlColor` ~:123-141, `Elements::set` :1132-1165), `elements.h` (`Value` :55, `TypeDef` :156).
- A freshly created element is diffed against `DefaultOf(type)`: C# element defaults must equal the QtNative defaults exactly.
- Coordinates and lengths are converted by the reconciler only (`_mapping.ToQt`, `SpaceMapping.cs`); when `_rescale` is true, every length-carrying property is resent (`Reconciler.cs:86`, `:141-142`).

## Specification

### Protocol (`protocol.h` + `Protocol.cs`, by hand)
- `VLQT_PROTOCOL_VERSION` 13 → 14.
- Type `VLQT_TYPE_SHAPE = 26` ↔ `Protocol.Type.Shape`.
- Value types: `VLQT_VT_F32_ARRAY = 10` (u32 count, count × f32) ↔ `Vt.F32Array`; `VLQT_VT_PATH = 11` (u32 verbCount, u32 pointCount, verbCount × u8 then zero padding to 4 bytes, pointCount × 2 × f32) ↔ `Vt.Path`. Verbs: 0 Move, 1 Line, 2 Quad (2 points), 3 Cubic (3 points), 4 Close (0 points). Document both in the head comment "Values".
- Properties (block 960, one constant per line, explicit values):

  | Constant | Id | Value type | Meaning |
  |---|---|---|---|
  | `VLQT_PROP_SHAPE_POINTS` | 960 | F32_ARRAY | x,y pairs, logical pixels |
  | `VLQT_PROP_SHAPE_CLOSED` | 961 | BOOL | close the point list |
  | `VLQT_PROP_SHAPE_PATH` | 962 | PATH | verbs + points, logical pixels; replaces POINTS |
  | `VLQT_PROP_SHAPE_FILL` | 963 | COLOR | fill color |
  | `VLQT_PROP_SHAPE_STROKE_COLOR` | 964 | COLOR | |
  | `VLQT_PROP_SHAPE_STROKE_WIDTH` | 965 | F32 | < 0 = no stroke |
  | `VLQT_PROP_SHAPE_CAP` | 966 | I32 | 0 Flat, 1 Square, 2 Round |
  | `VLQT_PROP_SHAPE_JOIN` | 967 | I32 | 0 Miter, 1 Bevel, 2 Round |
  | `VLQT_PROP_SHAPE_MITER_LIMIT` | 968 | F32 | |
  | `VLQT_PROP_SHAPE_DASH` | 969 | F32_ARRAY | empty = solid; stroke-width units |
  | `VLQT_PROP_SHAPE_DASH_OFFSET` | 970 | F32 | stroke-width units |
  | `VLQT_PROP_SHAPE_TRIM_START` | 971 | F32 | 0..1 |
  | `VLQT_PROP_SHAPE_TRIM_END` | 972 | F32 | 0..1 |
  | `VLQT_PROP_SHAPE_TRIM_OFFSET` | 973 | F32 | |
  | `VLQT_PROP_SHAPE_FILL_RULE` | 974 | I32 | 0 NonZero, 1 EvenOdd |

- QtNative defaults (set in the factory, mirrored as C# element defaults): no geometry, closed false, fill transparent, stroke color white (1,1,1,1), stroke width −1, cap Flat, join Miter, miter limit 4, dash empty, dash offset 0, trim 0/1/0, fill rule NonZero, `preferredRendererType` CurveRenderer.

### QtNative
- `commands.cpp`: `valueSize` and the length check in `Scene::set` handle both new types like the STRING special case (`expected = 4 + 4·count` resp. `8 + pad4(verbs) + 8·points`); validate verb values and that the point count matches the verbs (error code, no crash). Reject the new types explicitly in `setUniforms` (:773), `setQmlProperties` (:861) and `readEffectValue` (:115), which use `valueSize` too.
- `elements.h` `Value`: fields for the array (pointer + count, valid only during the setter call, like `utf8`).
- `pathelement.h/.cpp` (new): `VlPathElement : QQuickCurve`, holds either points + closed or verbs + points (copied, owned), `addToPath` appends to the `QPainterPath`, emits `changed()` after new data (per spike Q2).
- `elements.cpp`: `makeShape()` factory (C++ instead of a QML snippet, so the setters reach `QQuickShapePath` and `VlPathElement` directly), `shapeProps()` with typed setters (no QVariant writes, see #560), entry in `typeTable()`. Include private headers as `<QtQuickShapes/private/qquickshape_p.h>`. Correct the outdated "no private Qt headers" note in the head comment of `elements.h`.
- `CMakeLists.txt`: `QuickShapes QuickShapesPrivate` in `find_package` (:17) and the link line (:63) with a comment; `qml/deploy.qml`: `import QtQuick.Shapes`; `tools/build-native.ps1`: add `'QtQuick\Shapes'` to the deploy checks (:40 and the loop at :54).

### C#
- `ICommandSink`: `SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values)` and `SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points)`; implement in `CommandWriter` (style of `SetString`) and in the test sinks `Tests/RecordingSink.cs`, `CountingSink` (`ReconcilerPropertyTests.cs:274`, `ReconcilerStructureTests.cs:251`), `RecordingSinkNull` (`ReconcilerSceneTests.cs:542`).
- `Protocol.cs`: constants `MaxF32ArrayCount = 16380` (8190 points) and a helper `PathFits(int verbs, int points)`; the writer throws `ArgumentOutOfRangeException` beyond them (programming error); callers check first.
- `Paths/QtPath.cs` (new, `namespace VL.Qt.Paths`, public, sealed, immutable): a chain node (previous `QtPath?`, own segment: verb + up to 3 points) plus `static QtPath Empty`; `Flatten()` iterative (no recursion; 10 000+ segments), result cached in the instance (`ImmutableArray<byte> Verbs`, `ImmutableArray<float> Points`, bounds); `ContentEquals(QtPath other)` on the flattened arrays. Internal factory methods `MoveTo/LineTo/QuadTo/CubicTo/Close/Append` used by package 04. Arc-to-cubic conversion is not here (package 04).
- `Elements/Shape.cs` (new, sealed, init-only): `Points` (`Spread<Vector2>`, empty), `Closed`, `Path` (`QtPath?`, null), `Fill` (0,0,0,0), `StrokeColor` (1,1,1,1), `StrokeWidth` (0 = no stroke), `Cap` (`ShapeCap.Flat`), `Join` (`ShapeJoin.Miter`), `MiterLimit` (4), `Dash` (`Spread<float>`, empty, units), `DashOffset` (0), `TrimStart` (0), `TrimEnd` (1), `TrimOffset` (0), `FillRule` (`ShapeFillRule.NonZero`). Public enums `ShapeCap`, `ShapeJoin`, `ShapeFillRule` in their own files.
- `ElementType.Shape = 26`; `PropertyDiff.cs`: `s_shape`, `DefaultOf` arm, `case`, `DiffShape`:
  - Points/path: resend when the reference differs and the content differs, or on `_rescale`; convert every coordinate with `_mapping.ToQt`; `Path` set → send PATH, else POINTS.
  - Stroke width: `> 0` → `ToQt(width)`, else −1. Dash: each length / stroke width (both in units), dash offset the same; resend dash when the stroke width changes.
  - Everything else only on change.

## Steps
- [ ] T1 Protocol in `protocol.h` and `Protocol.cs`, `--drift` green.
- [ ] T2 `ICommandSink` + `CommandWriter` + test sinks; byte tests in `CommandWriterTests.cs` for both types incl. padding and the limit.
- [ ] T3 QtNative decode in `commands.cpp` + `elements.h` incl. explicit rejects.
- [ ] T4 QtNative SHAPE element: CMake, deploy, `pathelement.*`, `makeShape`, `shapeProps`, `typeTable`.
- [ ] T5 C# `QtPath` core with unit tests (flatten order, long chains without stack overflow, cache, `ContentEquals`).
- [ ] T6 C# `Shape` element, enums, `ElementType`, `DiffShape`; tests in `ElementTypeTests` (`AllTypes`, `TypeIds`, defaults), `ReconcilerPropertyTests` (only changes are sent, rescale resends points, dash divided by stroke width), `ReconcilerSpaceTests`.
- [ ] T7 SmokeTest scenario `shapes` (`src/VL.Qt.SmokeTest/ShapeTest.cs`, registered in `Program.cs` Scenarios like `rectangle`): open polyline, closed filled polygon with thick stroke, cubic path, trimmed polyline, dashed line; PNG `build/smoke/…/shapes.png`; pixel checks for stroke, fill, closed join and trim.

## Acceptance criteria
- `dotnet run --project src/VL.Qt.SmokeTest -c Release -- --drift` passes.
- `tools/build.ps1 -Test` passes; the `shapes` PNG shows the five shapes correctly.
- An invalid PATH payload (wrong count) is rejected with an error, not a crash (unit or SmokeTest check).

## Verification
`tools/build.ps1 -Test`, `tools/build.ps1 -Test -Only shapes`, look at the PNG.

## Task cut (for the orchestrator)
| Task | Layer | Files | Model |
|---|---|---|---|
| T1 | protocol | `protocol.h`, `Protocol.cs` | Sonnet |
| T2 | C# writer | `ICommandSink.cs`, `CommandWriter.cs`, test sinks, `CommandWriterTests.cs` | Sonnet |
| T3 | native decode | `commands.cpp`, `elements.h` | Opus |
| T4 | native element | `pathelement.*`, `elements.cpp`, `CMakeLists.txt`, `qml/deploy.qml`, `tools/build-native.ps1` | Opus |
| T5 | C# path core | `Paths/QtPath.cs`, `Tests/QtPathTests.cs` | Sonnet |
| T6 | C# element + diff | `Elements/Shape.cs`, enums, `ElementType.cs`, `PropertyDiff.cs`, tests | Sonnet |
| T7 | SmokeTest | `ShapeTest.cs`, `Program.cs` | Sonnet |

T1 first (interface); then T2, T3, T5 in parallel; T4 after T3; T6 after T2 and T5; T7 last.
