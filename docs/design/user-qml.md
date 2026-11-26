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

- **Pins come from a hand-written C# parser** of the declarations of the root object (`QmlDeclarations.Parse`, `Qml/Parsing/`), because it works at node-browser time without a running Qt engine, runs no user code and is unit-tested (`QmlDeclarationsTests`). It is the same model as the annotations of the shader nodes. Supported: `property`, `readonly property`, `required property` and `default property` of `real`, `double`, `int`, `bool`, `string`, `color`, `vector2d`, `vector3d`, `vector4d`, `point`, `size`, `rect` (what the wire carries), and `signal`. `alias`, `var`, `list<…>`, object types and `url`, `font`, … get no pin and one info message at the node; the optional `Properties` input stays for them. A root type that is a `.qml` file of the same folder contributes its declarations first, with a cycle guard.
- **Defaults and sending.** A literal initializer is the default of the pin; a binding or no initializer gives the default of the type. An input is sent once it differs from the declared default and from then on on every change; a `required property` is always sent. Because of this a binding in the file stays alive until the patch really sets the value.
- **Inputs and outputs.** An input per non-readonly property; an output per property (the last value QML changed; visible for `readonly` and bound properties); a signal gives `bool` (no arguments), `Spread<T>` (one) or `Spread<QmlSignal>` (more). Input and output of a property share the pin name, because vvvv keeps the pin kind with the pin. The common pins come from one table (`QmlCommonPins`), in the order of `QmlElement`. Errors show at the node and in the log; there is no `Error` output.
- **Discovery** in `qml/` folders next to documents and in packages, recursive, by the `FolderNodeFactory` that the shader nodes use too. Qt projects keep their QML in `qml/`, and Qt's own deployed QML modules never sit at a document or package root. Only files whose name starts with an uppercase letter count (the QML rule for components). Node name, the `_` variant, category and the head tags `// @category`, `// @summary`, `// @tags` follow the shader nodes.
- **Hot reload by signature.** The node description is invalidated only when the declaration signature (`QmlDeclarations.Signature`) changes; any other edit only reloads the running instance, so editing the body of a component does not rebuild the patch. vvvv keeps the links on pins with the same name.
- **A file that does not parse keeps the last good pins and values.** `LastGood` (shared with the shader nodes) keeps the pins of the last version that worked, and the patch's values stay at the pins, so a typo does not drop the node and its links. The error shows at the node and in the log, with a note that the pins are the old ones. The instance in QtNative is rebuilt on every reload and stays empty until the file compiles. A node that never had a good version has the common pins and the messages.

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
