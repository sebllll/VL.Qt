# Review 18 – QtNative: elements

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/elements.cpp` (1915 lines, not 1748), `elements.h`, `pathelement.cpp`, `pathelement.h`, `qml/deploy.qml` (the only file in `qml/`). Skimmed by targeted excerpt: `commands.cpp` (Scene create/destroy/reset/~Scene, setStyle), `debug.cpp:160-280`, `perf.cpp:1040-1056`, `events.cpp` (poll calls), `vlqt.cpp:1058-1066`, `CMakeLists.txt:18-32,121-137`, `src/VL.Qt/Reconciler/PropertyDiff.cs:25-120`, `src/VL.Qt/Elements/ElementType.cs`, the SmokeTest file list. Not read: `effectchain.*`, `qmlitem.*`, `shadereffectitem.*`, `qtimageitem.*`, `style/`.

## 1 Summary

Elements are table-driven: one `TypeDef` per type (id, name, QML one-liner or C++ factory, `PropDef` list) and one setter function per property, found by id at SET time. That core is sound and must stay. The problems are around it:

- `elements.cpp` is four subsystems in one file and one class (`Elements`): the type table, geometry (anchor/transform/drag), effect-source and Group-proxy bookkeeping, and the effect-chain/mask frames. `Node` (the Scene's per-element record) lives in `elements.h` too.
- Facts about a type are not in its table entry but in `if (type == …)` chains in seven places across four files (`takesAutoSize`, `pollsState`, `create`, `applySize`, `debug.cpp`, `perf.cpp`, `Scene::setStyle`, `Scene` video release). Adding an element means finding all of them; none fails when one is missed.
- The QML of ten types is C string literals in C++, which is why `qml/deploy.qml` has to repeat their imports by hand.
- Defects: the frame of a Group ignores the frame origin of a nested Group (`must`); the `inPositioner` cache is keyed on a meta-object pointer that QML creates per instance; non-finite floats reach Qt unchecked, and one reaches `qRound` (undefined behaviour).

The changes that matter most: NELM-01 (nested Group frame), NELM-04 (type traits in the table), NELM-05 (cut `elements.cpp` into files), NELM-06 (QML snippets into `.qml` resources), NELM-07 (typed setters for Rectangle/Text, which removes the grouped-property machinery).

## 2 What works and must survive

- **Table + resolve once.** `PropDef {id, valueType, setter, qmlName}`; QML properties are resolved to a `QMetaProperty` once per type (`Elements::resolve`, `elements.cpp:1304`), never looked up by name per frame. A new property needs no new export.
- **Typed setters with Qt's compare-first** (`setQmlColor` for Rectangle/Text, all Shape setters, `setMatrixValue`): an unchanged value is a no-op without a QVariant. Keep, and extend (NELM-07).
- **Translation fast path** (`placeItem`, `setMatrix`, `elements.cpp:1447,1537`): a pure x/y TRANSFORM goes into x/y; the `Matrix4x4` is attached only when needed. Hot path for 10,000 items in the perf test.
- **`TransformState` as `shared_ptr`** shared with the width/height connection, so a retired item's signal never touches an erased `Node`.
- **`GroupItem` recomputes the frame only when dirty** (`updatePolish`, `elements.cpp:564`), and `pokeGroup` uses `typeid` on a `final` class instead of `dynamic_cast` (hot path).
- **Deferred deletion by one frame** (`layerOff`, `frameDelete` as `QPointer` lists, `flushLayerOff`): an effect may still sample a former source, mask frame or proxy in the current frame. The use-after-free this prevents must stay explained in the comment (today only "#123").
- **Group proxy refcount with its own connection handle** (`proxyRetire`, `releaseGroupProxy`, `elements.cpp:1846,1861`): disconnecting only this connection keeps Qt's own `sourceItemDestroyed`.
- **Value inputs without echo**: QML counts user changes (`vlqtUserCount`/`vlqtUserValue`) in the control's user-only signal; C# SETs never fire it.
- **Shape built in C++** with the `QQmlParserStatus` order (`makeShape`, `elements.cpp:932`) and unaligned buffer reads through `memcpy` (`pathelement.cpp:11`, `setShapeDash`).

**Hot paths** (per changed property per frame, must not allocate): `Elements::set` → setter; `setTransform` → `setMatrix`/`placeItem`/`inPositioner`/`pokeGroup`; the width/height lambda of `initGeometry`; `GroupItem::updatePolish` (dirty only); the poll readers `readError`, `takeClicks`, `takeUserValue`, `readMedia` (per polled node per poll). Broken today: `modifyFont` reads and writes a `QFont` through `QVariant` for every font property change (NELM-07).

## 3 Findings

### NELM-01 Group frame: use the child Group's frame, not (0, 0, w, h)
- **Kind:** correctness
- **Severity:** must
- **Breaking:** yes: a Group that contains an auto-sized Group whose children reach left of / above its origin (or a Group with Auto Size off and an anchor other than Left/Top) gets a different, now correct frame: hit area, clip, SCENE_BOUNDS, effect padding and debug overlay move.
- **Where:** `src/VL.Qt.Native/elements.cpp:575-581`, `elements.cpp:593-598`
- **Finding:** `updatePolish` maps every child as `QRectF(0, 0, c->width(), c->height())`. For a child `GroupItem` the drawn area is `itemFrame(*child->xf, child)`, which is `(ox, oy, w, h)` with `ox < 0` (auto size, content left of the origin) or `(-fx*w, -fy*h, w, h)` (`sizedFrame`). The outer frame is then shifted by `-ox`/`-oy`. In addition, a change of only the inner origin (`moved` at `elements.cpp:593`) emits no x/y/size signal when the anchor is Left/Top, so the outer Group is not invalidated at all.
- **Proposal:** In `GroupItem::updatePolish` take `isGroup(c) && static_cast<GroupItem*>(c)->xf ? itemFrame(*xf, c) : QRectF(0, 0, w, h)` as the child's local rect. When `moved` is true, also call `pokeGroup(this)` so the parent Group recomputes.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp` (after NELM-05: `groupitem.cpp`), `src/VL.Qt.SmokeTest/GroupFrameTest.cs`
- **Depends on:** none
- **Verification:** new case in SmokeTest `GroupFrameTest`: outer Group > inner Group > Rectangle at x = -50; outer SCENE_BOUNDS must start at -50. Same with inner Auto Size off and anchor Center.

### NELM-02 `inPositioner`: a flag set at REPARENT instead of a meta-object cache
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/elements.cpp:1417-1430`
- **Finding:** The cache compares `parent->metaObject()` with the last one seen and says "runs once per meta object". Row and Column are QML one-liners that declare `property int childAlignment` and a function, so Qt gives every instance its own `QQmlVMEMetaObject` (a QML object with own properties gets a per-instance meta object). Two Columns therefore never share a cache entry, so the cache misses when siblings in different Row/Columns alternate, and `indexOfSignal` (a string search) runs again. The `thread_local` raw pointer can also be reused after a Row is deleted (ABA): today all per-instance meta objects that can be parents are positioners, so the answer happens to stay right, but a future container type with QML properties would make it wrong. Not measured: confirm by logging `parent->metaObject()` of two Column parents.
- **Proposal:** Add `bool inLayout` to `TransformState`. `Scene::reparent` sets it from the parent node's type (Row or Column, via the trait of NELM-04); `placeItem`, `setMatrix` and `placeAfterReparent` read `s.inLayout`. Delete `inPositioner`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `elements.h` (`TransformState`), `commands.cpp` (`Scene::reparent`)
- **Depends on:** NELM-04 (trait `layout`), can also be done with a plain type check first
- **Verification:** SmokeTest `RowColumnTest`, `AnchorTest`, `PerfLimits`

### NELM-03 Reject non-finite floats in one place
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no (a NaN/inf value now gives ApplyBadValue instead of an undefined result)
- **Where:** `src/VL.Qt.Native/elements.cpp:255-259`, `elements.cpp:132`, `186-189`, `277-280`, `711-728`, `965-971`
- **Finding:** `setFontPixelSize` checks `!(v.f[0] > 0)`, which lets `+inf` and values above `INT_MAX` through to `qRound`, whose float→int conversion is undefined behaviour. The other F32 setters (`setOpacity`, `setQmlReal` for radius, border width, spacing, padding, line height, slider bounds; `setLetterSpacing`) write NaN/inf to Qt unchecked, while the Shape setters call `finiteF32` and Video checks `isfinite` itself. `commands.cpp` does not check finiteness while decoding (no `isfinite` there). Three validation styles for the same rule.
- **Proposal:** In `Elements::set`, before calling the setter: for `VLQT_VT_F32`, `VEC2`, `COLOR`, `RECTF`, `MAT4` reject any non-finite component with `ApplyBadValue` ("value must be finite"). Delete `finiteF32` and the per-setter checks. In `setFontPixelSize` clamp before rounding (`qRound(std::min(v.f[0], 1e6f))`).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`
- **Depends on:** none
- **Verification:** SmokeTest `ApplyTest`: SET OPACITY NaN and FONT_SIZE inf return -7; `ShapeTest` unchanged

### NELM-04 All facts about a type in its table entry
- **Kind:** architecture
- **Severity:** should
- **Breaking:** no
- **Where:** `elements.cpp:73-86` (`applySize`), `1236-1243` (`create`, VIDEO), `1280-1289` (`pollsState`), `1471-1478` (`takesAutoSize`), `commands.cpp:221,499-501` (`vlqtRelease` by name, twice), `commands.cpp:798` (`setStyle`: `type < BUTTON || type > TEXT_FIELD`), `debug.cpp:166,232-243,269-271` (`isContainer`, padding), `perf.cpp:1044-1052` (`typeName`, misses 8 of 16 types)
- **Finding:** To add an element a newcomer has to touch, in QtNative: `protocol.h` (type id, property ids), the snippet/factory and setters, the property-table function and its line in `typeTable()`, `takesAutoSize` if it auto-sizes (duplicating the presence of `VLQT_PROP_AUTO_SIZE` in its table), `applySize` if its size is special, `pollsState`/`create`/`readMedia` if it has state the poll reads, `Scene::setStyle`'s id range if it is a control (a control with an id outside 10..15 is silently unstylable), `debug.cpp` if it is a container, `perf.cpp` for its name, `deploy.qml` if it imports a new module, and for a control `style/VLQt/<Control>.qml` + `qmldir` + `CMakeLists.txt` FILES. In C#: `Protocol.cs` (`Type`, `Prop`), `ElementType.cs`, `Elements/<X>.cs`, `PropertyDiff.cs` (`DefaultOf` static instance, the switch case, a `Diff<X>` method), `Nodes/<X>Node.cs`, a help patch and `help/Help.xml`. A property: `protocol.h`, `Protocol.cs` (by hand, `--drift` checks), `PropDef` line + setter, the C# element property, the `Diff<X>` line, the node pin, the help patch. None of the native places fails when forgotten; only `--drift` and the QML name lookup (a log warning) do.
- **Proposal:** Extend `TypeDef` so one entry says everything QtNative needs:
  ```cpp
  enum class AutoSize : quint8 { None, Always, WithInput };   // WithInput: ShaderEffect
  struct TypeDef {
      quint16 id; const char* name;           // name = perf/debug name ("group", "rect", ...)
      const char* qmlUrl;                     // qrc:/vlqt/elements/....qml, or nullptr (NELM-06)
      QQuickItem* (*make)();                  // C++ item, or nullptr
      std::vector<PropDef> props;
      AutoSize autoSize = AutoSize::None;     // replaces takesAutoSize
      bool layout = false;                    // Row/Column: owns x/y of children (NELM-02, debug.cpp)
      bool container = false;                 // debug.cpp isContainer
      bool styled = false;                    // replaces the id range in Scene::setStyle
      void (*beforeDestroy)(QQuickItem*) = nullptr; // Video: vlqtRelease + audio tap, called by Scene in one place
  };
  ```
  Each element file (NELM-05) defines `const TypeDef& rectangleType()` etc.; `typeTable()` only lists them. `pollsState` keeps resolving `vlqtError`/`vlqtClicks`/`vlqtUser*` from the meta object but reads media through the same mechanism (`vlqtPosition`/`vlqtDuration` in `TypeCache`) instead of the VIDEO special case in `create`. `applySize`'s Group/QML cases become a per-type `void (*applyAutoSize)(QQuickItem*, bool)`. Optionally store the resolved props in a per-type array indexed by property id offset instead of the four hash/linear lookups per SET (`findType`, `findProp`, `types[...]`, `cache.props.find`).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `elements.h`, `commands.cpp`, `debug.cpp`, `perf.cpp`
- **Depends on:** NELM-05 (can be done before it inside `elements.cpp`)
- **Verification:** `tools/build.ps1 -Test` (all element SmokeTests: `ElementsTest`, `ButtonTest`, `SpinBoxTest`, `ValueInputsTest`, `VideoTest`, `VideoStopTest`, `StyleTest`, `DebugOverlayTest`, `PerfStatsTest`)

### NELM-05 Cut `elements.cpp` and the `Elements` class by responsibility
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/elements.cpp` (whole file), `elements.h:113-136` (`Node`), `elements.h:176-301` (`Elements`)
- **Finding:** One 1915-line file holds: the font-family warning used by `vlstyle.cpp` (`elements.cpp:45`), common properties, 16 element types with their QML and setters, `GroupItem`, the type table, `Elements` core (create/resolve/set/poll readers), geometry (anchor, transform, drag offset, fast path), effect-source bookkeeping, Group texture proxies, and the effect chain's mask frames. The class `Elements` has 30 members from these four areas. `Node` is the Scene's record (owned by `Scene::nodes`, `commands.h:91`) but is declared in `elements.h` and holds input-handler, poll and effect fields. Effect-chain code is split between `effectchain.cpp` and `elements.cpp:1634-1804`. A reader looking for "how does Slider work" scrolls past Group proxies.
- **Proposal:** See section 4 for the file list. In short: `node.h` (Node), `element_types.h/.cpp` (Value, PropDef, TypeDef, the table, generic QML setters), one file per element family (`type_*.cpp`), `groupitem.h/.cpp`, `geometry.h/.cpp` (TransformState, frame/anchor math, the geometry methods, the common props SIZE/TRANSFORM/ANCHOR), `effect_sources.cpp` (source refcount, Group proxies, texture groups, `flushLayerOff`), effect layer + mask frames into `effectchain.cpp` (or `effect_layer.cpp`), `fonts.h/.cpp` (`warnMissingFontFamily`, `modifyFont` if it survives NELM-07). `Elements` keeps create/resolve/set/poll readers; geometry and effect sources may stay methods of `Elements` defined in their own `.cpp` (cheapest), or become classes `Geometry`/`EffectSources` owned by the Scene. Recommend the first: same API, files by topic.
- **Effort:** L, mechanical: yes for the move, no for the class boundaries
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `elements.h`, new files of section 4, `effectchain.cpp/.h`, `vlstyle.cpp` (include), `CMakeLists.txt` (sources), every includer of `elements.h` (`commands.*`, `events.*`, `debug.cpp`, `perf.cpp`, `shadereffectitem.cpp`, `audio_tap.cpp`)
- **Depends on:** none; do it before NELM-04/06/07 or right after NELM-04
- **Verification:** `tools/build.ps1 -Test`, no behaviour change

### NELM-06 QML of the element types as `.qml` resources, not C strings
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `elements.cpp:205,222-223,309-310,432-458,668-708,754-760,787-801,821-839,855-864,884-892`, `elements.cpp:1220-1222`, `qml/deploy.qml:1-12`
- **Finding:** Ten types are QML written as escaped C string literals (Video is 40 lines with `\"` and a regex inside a `#define`). They cannot be read with highlighting, linted (`qmllint`) or opened in Qt Creator, and windeployqt cannot see their imports, which is the only reason `qml/deploy.qml` exists ("keep the list in sync with the imports used by the element snippets"). The project already embeds QML as resources (`CMakeLists.txt:27` `qt_add_resources(vlqt "vlqt_style" …)`).
- **Proposal:** Move each snippet to `src/VL.Qt.Native/qml/elements/<Name>Element.qml` (suffix needed: a file `Button.qml` containing `Button {}` would instantiate itself, because QML resolves types from the file's own folder first), embed with `qt_add_resources(vlqt "vlqt_elements" PREFIX "/vlqt/elements" …)`, and create with `QQmlComponent(engine, QUrl("qrc:/vlqt/elements/RowElement.qml"))`. Point windeployqt's `--qmldir` (and `deploy-linux.sh`) at `qml/`, so the element files declare their own imports; `deploy.qml` shrinks to the modules not imported by a file (QtQuick.Shapes, QtQuick.Effects if `effectchain.cpp` keeps strings, QtQuick.Controls.Basic) or goes away if those move too. Keep `make` for the C++ types. The `vlqtPath` macro becomes a function in `VideoElement.qml`.
- **Effort:** M, mechanical: yes
- **Touches:** `src/VL.Qt.Native/elements.cpp`, new `src/VL.Qt.Native/qml/elements/*.qml`, `qml/deploy.qml`, `CMakeLists.txt`, `tools/build-native.ps1` (deploy checks), `tools/deploy-linux.sh`
- **Depends on:** NELM-05 (optional), Question 1
- **Verification:** `tools/build.ps1 -Test`; `tools/check-licenses.ps1`; Linux `tools/deploy-linux.sh` + SmokeTest; `tools/test-nupkg-consumer.ps1`

### NELM-07 Typed setters for Rectangle and Text; drop grouped properties and `modifyFont`
- **Kind:** performance
- **Severity:** should
- **Breaking:** no
- **Where:** `elements.cpp:162-179` (`setQmlColor` with `qobject_cast` and `rp.sub` test), `207-215`, `243-303`, `elements.h:146-150` (`ResolvedProp::sub`), `elements.h:303-326` (`modifyFont`), `elements.cpp:1184-1197`, `1320-1334`
- **Finding:** Rectangle and Text are the most numerous elements and the motion-typography use case animates their properties every frame. Only `color`/`border.color` are typed (via a type test inside the generic setter that uses "is the property grouped" to mean "is it the border"); radius, border width, text, alignment, line height go through `QVariant`, and every font property (size, weight, family, letter spacing) does a `QFont` read + write through `QVariant` per change, the cost the #560 work removed for colors. The grouped-property machinery (`ResolvedProp::sub`, the `.` split in `resolve`, the group branch in `writeQml`) exists only for `border.color` and `border.width`.
- **Proposal:** Give Rectangle and Text their own setters on `QQuickRectangle`/`QQuickText` (private headers already included): `setRectColor`, `setRectRadius`, `setBorderColor` (`r->border()->setColor`), `setBorderWidth`, `setText`, `setTextColor`, `setFontPixelSize` (`QFont f = t->font(); f.setPixelSize(px); t->setFont(f)`: QFont copy-on-write, no QVariant) and so on. `qmlName` stays only for controls and Video. Delete `ResolvedProp::sub`, the dot handling in `resolve`, the group branch of `writeQml`, `modifyFont` and the `#include <QtGui/QFont>` at the end of `elements.h`. Rectangle and Text could then be created in C++ like Image (no component), with the one-liner defaults (`border.width: 0`, `textFormat: PlainText`, `minimumPixelSize: 1`, `lineHeightMode`) set in the factory.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp` (after NELM-05: `type_rectangle.cpp`, `type_text.cpp`), `elements.h`
- **Depends on:** Question 3
- **Verification:** SmokeTest `RectangleTest`, `TextTest`, `FontTest`, `PerfLimits` (compare apply time with animated font size before/after)

### NELM-08 Create `Matrix4x4` in C++, append with `QQuickTransform::appendToItem`
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `elements.cpp:1377-1395` (`createTransform`), `1561-1575`, `1739-1752`, `elements.h:288`
- **Finding:** A QML component (`"import QtQuick\nMatrix4x4 {}"`) is compiled per window only to create a `QQuickMatrix4x4`, with two error paths and a `qobject_cast` check, and it is attached by name through `QQmlListReference(item, "transform")`. The code already includes `qquicktranslate_p.h` and casts to `QQuickMatrix4x4` for writing.
- **Proposal:** `auto* t = new QQuickMatrix4x4(item); t->appendToItem(item);` (`appendToItem` is public API of `QQuickTransform`). Delete `matrixComponent`, the error branches of `writeTransform`/`createMaskFrame`, and `createTransform` itself (`std::string& err` parameters of `setMatrix`/`setDragOffset`/`setAnchor` may then become unnecessary). Type `Node::transform` and `TransformState::matrix` as `QQuickMatrix4x4*`, which removes the `static_cast` in `setMatrixValue`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `elements.h`
- **Depends on:** none
- **Verification:** SmokeTest `AnchorTest`, `EffectMaskTest`, `InteractionTest` (drag offset), `PerfLimits`

### NELM-09 Ownership made explicit: components, GroupItem state, debug counter
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `elements.cpp:1203-1209,1220-1221` (raw `new`/`delete` of `QQmlComponent`), `elements.cpp:489-497` (`GroupItem` public fields), `1822-1863` (`Elements` changes `proxyRefs`, `proxy`, `proxyRetire` of the Group from outside), `elements.h:261` + `elements.cpp:135-141` + `commands.cpp:529-530` (`debugCount`)
- **Finding:** Component lifetime is a manual destructor loop. `GroupItem` is a class whose invariants (refcount ≥ 0, proxy present iff refs > 0, connection valid iff proxy) are maintained by another class through public fields; `releaseGroupProxy` even resets a negative count to 0 (`elements.cpp:1855`). `debugCount` is a public field incremented by a property setter and decremented by the Scene in `destroySubtree`; nothing ties the two (the Scene must check `node.debug`).
- **Proposal:** `std::unique_ptr<QQmlComponent>` in `TypeCache` (no destructor body). `GroupItem` gets `QQuickItem* acquireProxy(QQuickItem* proxyParent, std::function<void(QQuickItem*)> retire)` / `void releaseProxy(retire)` / `QQuickItem* proxy() const`, with the fields private; `Elements` only routes. Move the debug count to the Scene: `Scene::set` compares `node.debug` before and after the DEBUG property (or a method `Elements::setDebug(Node&, bool)` that is also called with `false` on destroy), so increment and decrement live in one file.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `elements.h`, `commands.cpp`
- **Depends on:** NELM-05
- **Verification:** SmokeTest `GroupShaderSourceTest`, `GroupShaderWindowDestroyTest`, `DebugOverlayTest`

### NELM-10 Comments that are no longer true
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `elements.h:1-8` ("plan 05"), `elements.h:246-247` (Image has `vlqtError` – it has not since `QtImageItem`, errors are on the resource), `elements.cpp:321-322` ("No SOURCE any more (-6)"), `elements.cpp:748` vs `qml/deploy.qml:9` ("style Basic (set in vlqt.cpp)" – `vlqt.cpp:1064` sets `VLQt`, Basic is only its fallback), `elements.cpp:1128` ("Plan 05b-f add types and properties here"), `elements.cpp:1306` ("All instances of a QML component share one meta object": they share the property layout, not the object, see NELM-02), `qml/deploy.qml:8` (lists only Button, SpinBox)
- **Finding:** These describe earlier states. A reader trusts them and goes looking for an Image error property or a Basic style choice.
- **Proposal:** Rewrite each to the present state; the header of `elements.h` becomes three sentences: what a type entry is, how properties are resolved, that new properties need no export. The K1 removal needs replacement text where the number carries the reason: "#123" → "an effect may still sample it until it syncs in the next frame; deleting it now is a use-after-free"; "#469" → "Group frame, see protocol.h 'Group frame'"; "#560" → "typed setter: QVariant writes cost several percent CPU per frame"; "#20" → "Qt has no signal for this; queue a measurement".
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `elements.h`, `qml/deploy.qml`
- **Depends on:** none (do with NELM-05 if that comes first)
- **Verification:** review only

### NELM-11 Names: `ITEM` → `GROUP`, `VlPathElement` → `ShapePathElement`
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: protocol constant `VLQT_TYPE_ITEM`/`Protocol.Type.Item`/`ElementType.Item` renamed (value unchanged); no patch notices.
- **Where:** `protocol.h:299`, `elements.cpp:1134`, `src/VL.Qt/Protocol.cs:127`, `src/VL.Qt/Elements/ElementType.cs:5,9`, `pathelement.h:13`
- **Finding:** The user-facing element is Group, the C++ class is `GroupItem`, `isGroup()` tests it, but the type id is `ITEM` and `ElementType.cs` needs a sentence "Group maps to Item". `VlPathElement` is the only class with a `Vl` prefix (others: `QtImageItem`, `ShaderEffectItem`, `QmlItem`) and its file name `pathelement` does not say it belongs to Shape.
- **Proposal:** `VLQT_TYPE_GROUP`, `Protocol.Type.Group`, `ElementType.Group`; `ShapePathElement` in `shapepathelement.h/.cpp` (or inside `type_shape.cpp` if it stays small). `TypeDef::name` becomes the lower-case display name used by perf/debug (NELM-04).
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/protocol.h`, `elements.cpp`, `debug.cpp`, `perf.cpp`, `pathelement.*`, `CMakeLists.txt`, `src/VL.Qt/Protocol.cs`, `src/VL.Qt/Elements/ElementType.cs`, C# users of `ElementType.Item`
- **Depends on:** none
- **Verification:** `dotnet run --project src/VL.Qt.SmokeTest -c Release -- --drift`, `tools/build.ps1 -Test`

### NELM-12 A test that every table property resolves and accepts its default
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `elements.cpp:1335-1338`
- **Finding:** A typo in a `qmlName` or a renamed QML property only logs a warning at the first CREATE and makes every SET of that property fail with -6 at runtime. The per-element SmokeTests cover the properties they use, not the table as a whole. Refactorings NELM-04 to NELM-07 move every table line.
- **Proposal:** SmokeTest scenario `element-table`: for each type id CREATE one element, SET every property of the type with a valid value of its value type (from a small table in the test, mirrored from `protocol.h` comments), expect `ApplyOk`, and expect no `vlqt.elements` warning in the log. Optionally an exported `TEST ONLY` function is not needed: the SET stream is enough.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.SmokeTest/ElementsTest.cs` (or new `ElementTableTest.cs`), `src/VL.Qt.SmokeTest/Program.cs`
- **Depends on:** none (should land before NELM-04/05/06/07)
- **Verification:** the test itself; break one `qmlName` locally to see it fail

### NELM-13 Row/Column cross-axis alignment in C++ instead of QML JavaScript
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `elements.cpp:432-458`, `460-480`
- **Finding:** `vlqtAlign()` loops over all children in JavaScript after every positioning pass and on every width/height change. With animated child sizes that is a JS call per Row/Column per frame. The QML property `childAlignment` is also what gives Row/Column a per-instance meta object (NELM-02). Not measured.
- **Proposal:** Measure first (`PerfLimits` with 50 Columns of animated Rectangles). If it shows: create Row/Column as plain `Row {}`/`Column {}`, keep the alignment in `Node` (or a small C++ helper object), and connect `positioningComplete`, `widthChanged`/`heightChanged` to a C++ function that sets the cross-axis x/y of the tracked children.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`
- **Depends on:** NELM-06 (if the QML moves to files first)
- **Verification:** SmokeTest `RowColumnTest`, `PerfLimits`

### NELM-14 Suspected: layer effect item of a child counted in the parent Group's frame
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `elements.cpp:548-556`, `575-581`
- **Finding:** Not confirmed. Qt's item layer creates its `layer.effect` item as a child of the layered item's *parent* (`QQuickItemLayer::activateEffect`). For a child with an effect chain inside a Group, that effect item is then a child of the Group: `itemChange` connects it and `updatePolish` adds its rect (including the chain's padding) to the frame. Whether that is intended (blur bleed counts) or not, it is undocumented. Confirm with `GroupFrameTest`: Group > Rectangle with a Blur effect chain, compare SCENE_BOUNDS with and without the effect.
- **Proposal:** If confirmed and not wanted: count only tracked children (pass the Scene's id set, or mark untracked items with a dynamic property / skip children that are not elements). If wanted: say so in the Group frame comment.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `src/VL.Qt.SmokeTest/GroupFrameTest.cs`
- **Depends on:** NELM-01
- **Verification:** the new `GroupFrameTest` case

### NELM-15 Small cleanups
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `elements.cpp:123-126` (alignment as hex literals), `821-839` (CheckBox and Switch snippets identical but for the type), `617-621,1504,1590,1645,1699` (`dynamic_cast<GroupItem*>` where `isGroup`/`node.type` exists), `elements.h:156` (five-parameter `Setter`, most setters use two), `commands.cpp:480` (`Node{item, type, 0, 0, nullptr}` positional aggregate init)
- **Finding / Proposal:** Use `Qt::AlignLeft` … in `setAnchor`; one snippet for both toggles (or two files with NELM-06); one way to test for a Group (`node.type == VLQT_TYPE_GROUP` where a Node is at hand, `isGroup` otherwise); keep the `Setter` signature but name unused parameters out consistently; designated initializers for `Node` (`Node{.item = item, .type = type}`).
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/elements.cpp`, `elements.h`, `commands.cpp`
- **Depends on:** NELM-05
- **Verification:** `tools/build.ps1 -Test`

K6 (test hooks): none in this area.

## 4 Target structure

Flat `src/VL.Qt.Native/` gets a subfolder `elements/` (align with the native-structure review if it proposes another layout; the file cut is what matters).

| File | Responsibility | Change |
|---|---|---|
| `node.h` | `Node`: the Scene's record of one element (item, type, sink, geometry, effect, handler and poll fields) | split out of `elements.h` |
| `elements/element_types.h/.cpp` | `Value`, `PropDef`, `Setter`, `TypeDef` with traits (NELM-04), `findType`, `typeTable()` listing `xxxType()`; generic QML setters `setQmlBool/Int/Real/String/Enum`, `writeQml` | split out |
| `elements/elements.h/.cpp` | `class Elements`: per-engine component and property cache, `create`, `resolve`, `set` (finite check, NELM-03), poll readers (`readError`, `readMedia`, `takeClicks`, `takeUserValue`, `pollsState`) | kept, smaller |
| `elements/geometry.h/.cpp` | `TransformState` (+ `inLayout`), `itemFrame`, `anchorPoint`, `placeItem`, `pivoted`, `markGeometry` decl; `Elements::initGeometry/placeAfterReparent/setMatrix/setAnchor/setDragOffset/writeTransform`; common props SIZE, AUTO_SIZE, TRANSFORM, ANCHOR, OPACITY, CLIP, DEBUG | split out |
| `elements/groupitem.h/.cpp` | `GroupItem` (frame, proxy API private fields, NELM-09), `groupType()`, `isGroup`, `groupTextureSource`, `pokeGroup` | split out |
| `elements/effect_sources.cpp` | `Elements::setEffectSource/clearEffectSource/syncTextureGroups/releaseTextureGroups/acquireGroupProxy/releaseGroupProxy/flushLayerOff` | split out |
| `effectchain.cpp` (or `elements/effect_layer.cpp`) | `Elements::setEffectChain/releaseEffectLayer/detachEffectMask/effectMasks/createMaskFrame/retireMaskFrame/updateMaskFrame` | moved |
| `elements/type_rectangle.cpp` | Rectangle: factory + typed setters | split out (NELM-07) |
| `elements/type_text.cpp` | Text: factory + typed setters incl. font | split out (NELM-07) |
| `elements/type_image.cpp` | Image: `QtImageItem` factory, IMAGE/FILL_MODE/SMOOTH | split out |
| `elements/type_layout.cpp` | Row, Column (traits `layout`, `container`) | split out |
| `elements/type_video.cpp` | Video: props, `beforeDestroy` (vlqtRelease + audio tap) | split out |
| `elements/type_controls.cpp` | Button, SpinBox, CheckBox, Switch, Slider, TextField (trait `styled`) | split out |
| `elements/type_shadereffect.cpp` | ShaderEffect props, `AutoSize::WithInput` | split out |
| `elements/type_qml.cpp` | QmlElement props | split out |
| `elements/type_shape.cpp` | `ShapeItem`, `makeShape`, Shape setters | split out |
| `elements/shapepathelement.h/.cpp` | `ShapePathElement` (old `VlPathElement`, `pathelement.*`) | renamed |
| `fonts.h/.cpp` | `warnMissingFontFamily` (used by Text and `vlstyle.cpp`) | split out |
| `qml/elements/*Element.qml` | QML of Row, Column, Video, Button, SpinBox, CheckBox, Switch, Slider, TextField (Rectangle, Text only if NELM-07 is not done) | new, from C strings (NELM-06) |
| `qml/deploy.qml` | imports not covered by the element files | shrunk or deleted |

Nothing here is public API of the DLL; the exports stay in `vlqt.cpp`. Deleted: `createTransform`, `matrixComponent`, `ResolvedProp::sub`, `modifyFont`, `inPositioner`, `takesAutoSize` (→ trait), `finiteF32`, `VLQT_QML_PATH_FN`.

## 5 Questions for Sebastian

1. **QML of element types: files or strings?** (a) `.qml` files in a resource folder (NELM-06): readable, lintable, windeployqt sees the imports, `deploy.qml` mostly disappears; one more CMake list. (b) Keep C++ strings: one file per type with the C++ next to it, `deploy.qml` stays a hand-kept list. Recommend (a).
2. **Row/Column alignment** (NELM-13): keep the JavaScript in QML (simple, works) or move it to C++ after a measurement? Recommend: measure, move only if it shows.
3. **More Qt private API?** QtNative already uses private classes (`QQuickRectangle`, `QQuickText`, `QQuickMatrix4x4`, `QQuickShape`, `QQuickItemPrivate`, `QQuickShaderEffectSource`; "private" in Qt means: no compatibility promise between Qt versions, the headers may change in any minor release). NELM-07 and NELM-08 use more of the same classes, no new ones. Accept, with the rule "only classes already listed in `elements.h`'s head comment, checked on each Qt update"? Recommend yes.
4. **Rename the protocol type `ITEM` to `GROUP`** (NELM-11)? Breaking in the protocol only, invisible to patches. Recommend yes.

## 6 For other areas

- Scene (`commands.cpp:798`): `setStyle` decides "is a control" by the id range 10..15; replaced by the `styled` trait (NELM-04).
- Scene (`commands.cpp:219-223`, `commands.cpp:499-501`): Video release by method name duplicated in `~Scene` and `destroySubtree`; one `beforeDestroy` hook.
- Perf (`perf.cpp:1044-1052`): `typeName` returns "other" for Button, SpinBox, CheckBox, Switch, Slider, TextField, Qml, Shape.
- Debug overlay (`debug.cpp:166,239-243,269-271`): container and padding decisions by type id chains.
- Reconciler (`src/VL.Qt/Reconciler/PropertyDiff.cs:25-117`): one static default instance, one switch case and the repeated `DiffAutoSize` line per type; a virtual `Element.DiffProperties(sink, previous)` or a per-type table would make an element one file plus a node.
- C# docs (`src/VL.Qt/Elements/Button.cs:4`, `CheckBox.cs:4`, `Slider.cs:11`, `SpinBox.cs:4`, `Switch.cs:4`, `TextField.cs:4`, `Nodes/ButtonNode.cs:16`, `Nodes/InputIntNode.cs:15`) and `protocol.h:306-311` say "style Basic"; the style is VLQt with Basic as fallback.
- `events.h:11` still names Image as a `vlqtError` source.
