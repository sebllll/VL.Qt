# User QML

How a user's own `.qml` file becomes an element (`QmlElement`) and, through a folder node factory, a node with named pins. Code: `src/VL.Qt/Nodes/Qml/QmlElementNode.cs`, `src/VL.Qt/Qml/` (parser, layout, factory, instance), `src/VL.Qt/NodeFactories/` (what it shares with the shader nodes) and `src/VL.Qt.Native/user_qml/` (`qml_item.cpp`, `qml_watcher.cpp`, `qml_wire.cpp`); the commands are described in the head comment of `protocol.h`. The behaviour for users is on [QML elements](../user-qml.md).

## Context

QML is used inside VL.Qt for the control style and the effects, one `QQmlEngine` per window. Users may also have QML components of their own, or want something that VL.Qt has no element for. The patch must stay the owner of the element tree, so user QML is an escape hatch: one leaf (or one subtree) in the tree, not a second UI model where QML builds the UI and C# is a backend (that is the model of Qt's own bridge, see [qt-bridge.md](qt-bridge.md)).

## Design

### Element

- **One element, `QmlElement`**, category `Qt.Elements.Qml`, experimental. Pins: `File`, `Properties` (`Dictionary<string, object>`) in; `Properties` and `Signals` out. It sits in the tree like any other element. A root that is not an `Item` is an error at the node.
- **A hand-written path in QtNative**, small and narrow: `ELEMENT_TYPE_QML`, `SET_QML_PROPERTIES`, `QML_SOURCE`, `QML_RELOAD`, `EVENT_QML_PROPERTY` and `EVENT_QML_SIGNAL`. `QmlItem` compiles a `QQmlComponent` on the window's engine and creates the root with the properties of the frame as initial properties (`beginCreate`, `setInitialProperties`, `completeCreate`). So `required property` is supported: Qt refuses a required property without a value at creation, and the values arrive in the same frame as the source.
- **Only what the root declares comes back.** A `QmlWatcher` reports each `property` and `signal` that the root of the file declares, not what `Item` already has. It is connected before the bindings of the file run, so what QML derives from the initial values is reported.
- **No echo of the value just set.** Only the value the patch has set is suppressed; a later change by QML (a `Behavior`, an animation, a binding) or a property that depends on it comes back. Because of this the patch can set a property and read it back without a feedback loop.
- **Auto Size** takes `width` and `height` of the root, which are `implicitWidth` and `implicitHeight` unless the file sets an explicit size on its root.
- **Hot reload watches the folder of the file** (the file, `*.qml`, `qmldir`), not subfolders, `.js` files or images. On a change `QML_RELOAD` clears the component cache and rebuilds the instance. A new element of a file that changed on disk since the last compile reads the file again too (QtNative keeps size and modification time per engine), because a node rebuilt after a declaration change must not show the old version.

### Node factory

- **Pins come from a hand-written C# parser** of the declarations of the root object (`QmlDeclarations.Parse`, `Qml/Parsing/`), because it works at node-browser time without a running Qt engine, runs no user code and is unit-tested (`QmlDeclarationsTests`). It is the same model as the annotations of the shader nodes. Supported: `property`, `readonly property`, `required property` and `default property` of `real`, `double`, `int`, `bool`, `string`, `color`, `vector2d`, `vector3d`, `vector4d`, `point`, `size`, `rect` (what the wire carries), a `list<…>` of these ([List values](#list-values)), and `signal`. `alias`, `var`, lists of other types, object types and `url`, `font`, … get no pin and one info message at the node; the optional `Properties` input stays for them. A root type that is a `.qml` file of the same folder contributes its declarations first, with a cycle guard.
- **Defaults and sending.** A literal initializer is the default of the pin; a binding or no initializer gives the default of the type. An input is sent once it differs from the declared default and from then on on every change; a `required property` is always sent. Because of this a binding in the file stays alive until the patch really sets the value.
- **Inputs and outputs.** An input per non-readonly property; an output per property (the last value QML changed; visible for `readonly` and bound properties); a signal gives `bool` (no arguments), `Spread<T>` (one) or `Spread<QmlSignal>` (more). Input and output of a property share the pin name, because vvvv keeps the pin kind with the pin. The common pins come from one table (`QmlCommonPins`), in the order of `QmlElement`. Errors show at the node and in the log; there is no `Error` output.
- **Discovery** in `qml/` folders next to documents and in packages, recursive, by the `FolderNodeFactory` that the shader nodes use too. Qt projects keep their QML in `qml/`, and Qt's own deployed QML modules never sit at a document or package root. Only files whose name starts with an uppercase letter count (the QML rule for components). Node name, the `_` variant, category and the head tags `// @category`, `// @summary`, `// @tags` follow the shader nodes.
- **Hot reload by signature.** The node description is invalidated only when the declaration signature (`QmlDeclarations.Signature`) changes; any other edit only reloads the running instance, so editing the body of a component does not rebuild the patch. vvvv keeps the links on pins with the same name.
- **A file that does not parse keeps the last good pins and values.** `LastGood` (shared with the shader nodes) keeps the pins of the last version that worked, and the patch's values stay at the pins, so a typo does not drop the node and its links. The error shows at the node and in the log, with a note that the pins are the old ones. The instance in QtNative is rebuilt on every reload and stays empty until the file compiles. A node that never had a good version has the common pins and the messages.

### List values

A `list<T>` property of a supported `T`, a `Spread` in the `Properties` dictionary and a list argument of a signal travel as one value of their own, `VLQT_VALUE_TYPE_LIST` (12). It is allowed in the entries of `SET_QML_PROPERTIES`, the value of `EVENT_QML_PROPERTY` and the arguments of `EVENT_QML_SIGNAL`, nowhere else (`SET`, `SET_UNIFORMS` and the effect chain refuse it with `-7`).

| Bytes | Field |
|---|---|
| u16 | `elemType`: `F32`, `I32`, `BOOL`, `STRING`, `COLOR`, `RECTF`, `VEC2`, `VEC3` or `VEC4` |
| u16 | 0 (reserved) |
| u32 | `count`, 0..`VLQT_LIST_VALUE_MAX_COUNT` (65536) |
| … | `count` elements of `elemType` |

- **Elements.** A fixed-size element is encoded like a value of its type, the elements back to back without padding: `F32`, `I32` 4 bytes, `BOOL` 1 byte, `VEC2` 8, `VEC3` 12, `COLOR` (straight alpha), `RECTF` (x, y, width, height) and `VEC4` 16. A `STRING` element is u32 length, UTF-8 and zero padding to 4, the last one too, so every length starts 4-byte aligned. The value is `VLQT_LIST_VALUE_HEADER_SIZE` (8) plus `count` × element size, or plus the padded string elements; the entry or the argument around it pads it to 4 like any value.
- **Decoding.** Both sides check the whole value before they use it: an `elemType` outside the list (also `LIST`), a reserved field that is not 0 or a `count` above the limit is `-7` (`BAD_ARGUMENT`), elements that leave the entry are `-2` (`BAD_LENGTH`). The C# decoder drops such an event. The SmokeTest `ApplyTest` has a case for each.
- **Into QML.** QtNative converts the list to the type the property declares: `list<real>` to `QList<qreal>`, `list<point>` and `list<size>` from `VEC2` to `QList<QPointF>` and `QList<QSizeF>`, `list<color>` to `QList<QColor>`, and so on; a `var` property gets a JavaScript array of the elements, each converted like a single value of its type.
- **Out of QML.** A list property or argument whose elements all have one wire type is sent as `LIST`; `int` and `real` mixed are `F32`. A list with an element of another type, or of mixed types, keeps the string form of today. An empty list carries the element type of the declaration, `F32` when there is none. A list longer than the limit is not sent; the element shows an error line naming the property.

The C# side models a list as a kind of its own, so every switch over `QmlValueKind` keeps one case per pin type:

| QML | `QmlValueKind` | Pin | `elemType` |
|---|---|---|---|
| `list<real>`, `list<double>` | `RealList` | `Spread<float>` | `F32` |
| `list<int>` | `IntList` | `Spread<int>` | `I32` |
| `list<bool>` | `BoolList` | `Spread<bool>` | `BOOL` |
| `list<string>` | `StringList` | `Spread<string>` | `STRING` |
| `list<color>` | `ColorList` | `Spread<Color4>` | `COLOR` |
| `list<vector2d>` | `Vector2DList` | `Spread<Vector2>` | `VEC2` |
| `list<point>` | `PointList` | `Spread<Vector2>` (x, y) | `VEC2` |
| `list<size>` | `SizeList` | `Spread<Vector2>` (width, height) | `VEC2` |
| `list<vector3d>` | `Vector3DList` | `Spread<Vector3>` | `VEC3` |
| `list<vector4d>` | `Vector4DList` | `Spread<Vector4>` | `VEC4` |
| `list<rect>` | `RectList` | `Spread<RectangleF>` | `RECTF` |

- **Kinds.** The list kinds follow the element kinds in the same order; `QmlTypes.IsList`, `ElementKind` and `ListKind` convert between them, `QmlPropertyDecl.IsList` tells a list property. `ClrType` gives the Spread, `TypeDefault` the empty spread; a literal array (`[1, 2, 3]`, `[Qt.point(0, 1), Qt.point(1, 2)]`) is the pin default.
- **Patch to wire.** `QmlValue.TryConvert` takes a `Spread` of `float`, `int`, `bool`, `string`, `Color4`, `Vector2`, `Vector3`, `Vector4` or `RectangleF` (no other element type, not more than the limit) without copying it: `ValueType` is `List`, `Integer` the element wire type, `Items` the spread. Two values are equal when their elements are, compared exactly like the single values; the same spread instance is equal at once, so an unchanged pin costs no walk.
- **Wire to patch.** `QmlEventDecoder` turns a `LIST` into a boxed `Spread<T>` of the CLR type of the element (`F32` → `Spread<float>`, `VEC2` → `Spread<Vector2>`, …). `QmlTypes.TryCoerce` takes it for a list kind: an empty spread fits every list kind, `Spread<int>` and `Spread<float>` convert like the single values. A signal with one list argument gives a `Spread<Spread<T>>` pin; in a `QmlSignal` the argument is the `Spread<T>`.

### Time

QML animations follow the animation driver, so they are deterministic and follow the clock of the patch ([timing.md](timing.md)). A QML `Timer` runs on wall time; the user page documents that it is not reproducible.

### Licensing

There is no import filter. QtNative deploys only LGPL modules; an `import` of anything else fails to load and the message shows at the node. The user page says so.

### JavaScript

The rule "no JavaScript for logic" applies to the QML of the built-in elements only: their logic is computed in QtNative or C#. User QML may use JavaScript, QtNative does not look into it.

## Consequences

- Factory nodes cannot carry the `Experimental` smell: `NodeBuilding.NewNodeDescription` and `IVLNodeDescription` have no smell, and `SymbolSmell` exists only as an attribute on symbols. The generated nodes are experimental all the same, as the user page says; the smell rule of [versioning.md](../versioning.md) cannot be met for factory nodes.
- A property or signal argument of an unsupported type has no pin; its value does not come back to the patch (the node says so once).
- The parser reads only the root object and a sibling file as root type, not imports from other folders or modules.
- `QmlDeclarationsTests`, `QmlNodeFactoryTests`, `QmlNodeInstanceTests` and `QmlElementNodeTests` protect the parser, the layout, the pins and the instance.

## Alternatives considered

- **Qt's own bridge for the wrappers:** a hand-written path (a few commands, two events) is smaller than build-time wrappers with a C++ toolchain ([qt-bridge.md](qt-bridge.md)).
- **A native probe for the pins:** needs a running engine at node-browser time and loads user code; the parser needs neither.
- **Animations that run free on wall time:** rejected; they follow the vvvv clock through the driver.
- **An import filter for non-LGPL modules:** rejected; not shipping the modules is the simplest guarantee.

## Open questions

- C# objects or models as context properties, and calling QML functions from the patch, are not offered; revisit when the dictionary form is used for it.

## Evidence

The behaviour statements were checked against the user page and the tests named above. The need for initial properties comes from Qt's rule that a `required property` must have a value at `completeCreate`.
