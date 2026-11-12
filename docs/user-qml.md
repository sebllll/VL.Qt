# QML files as elements

`QmlElement` loads a `.qml` file of your own into the element tree. The patch sets properties of the QML root by name, and the properties QML changes and the signals it emits come back as outputs. It is an escape hatch for existing QML components and for things VL.Qt has no element for, not a second UI model: the patch still owns the tree, QML is one leaf (or one subtree) in it.

Experimental (`SymbolSmell.Experimental`): pins and behavior can still change, see [Versioning](versioning.md). Help patch: `HowTo Use a QML file` with the sample `help/Qt/QmlElement Example.qml`. Why it is built this way: [decisions/user-qml.md](decisions/user-qml.md).

## Pins

| Pin | Type | Meaning |
|---|---|---|
| `File` | `Path` | The `.qml` file. Its root must be an `Item` (Rectangle, Item, ... but not a `Window`), else the node shows an error. Empty = nothing loaded. |
| `Properties` | `Dictionary<string, object>` | Values for properties of the QML root, by name. Only entries that changed are sent. |
| `Size` | `Vector2` | Width and height of the element, in units (1 unit = 100 DIP), only with `Auto Size` off. |
| `Auto Size` | `bool` | On (default): the element takes the size of the QML root. Off: `Size` sets `width` and `height` of the root. |
| common element pins | | `Transform`, `Anchor`, `Opacity`, `Clip`, `Debug`, `Interaction`, `Effect`, like every element ([element catalog](decisions/element-catalog-v1.md)). |
| out `Output` | `Element` | The element, connect it to a `Group` or `Renderer`. |
| out `Properties` | `Dictionary<string, object>` | Last value of each root property that QML changed. |
| out `Signals` | `Spread<QmlSignal>` | Signals of the root emitted since the last frame, in order. `QmlSignal` has `Name` and `Arguments` (`Spread<object>`). |
| out `Error` | `string` | Compile, load and type errors, one line each (file and line); empty = OK. |
| out `Measured Size`, `Scene Bounds` | | Optional, like on the other elements. |

Property values are passed on unchanged in QML units (device-independent pixels), only `Size` is in units of the renderer's space.

## Value types

The same types go both ways:

| vvvv | QML |
|---|---|
| `float`, `double` | `real` |
| `int` | `int` |
| `bool` | `bool` |
| `string` | `string` |
| `Color4` | `color` |
| `Vector2`, `Vector3`, `Vector4` | `vector2d`, `vector3d`, `vector4d` |
| `RectangleF` | `rect` |

- A value of another type, or `null`, is not sent: the node shows an error line and ignores the value.
- Qt converts to the type of the QML property. A `float` set on an `int` property works, a string that is no number for a `real` property does not: error line, value ignored.
- A string can be up to 20000 characters.
- Back to the patch the same types arrive (integers outside the `int` range are clamped, a `url` arrives as a `string`, a `rect` as `RectangleF`). A signal argument of any other QML type (a list, an object) arrives as its string form. A property of such a type is not reported: `Error` shows one line for it.

## What comes back

Only what the **root of the file declares** comes back, not what `Item` already has (`width`, `x`, `opacity`, ...):

- every `property ...` and `readonly property ...` of the root: its change arrives on the `Properties` output (one entry per property, the last value of the frame);
- every `signal ...` of the root: each emission arrives on `Signals` with its arguments, in order. More than 256 signals in one frame are dropped and `Error` says so for that frame.

```qml
Item {
    property real value: 0.5          // set by the patch, comes back when QML changes it
    property int percent: ...         // changed by QML only: arrives on the Properties output
    signal milestone(int percent)     // arrives on the Signals output
}
```

The output `Properties` is a new dictionary whenever something changed, otherwise the same instance. It keeps the last value of every property that changed since the file was loaded.

### No echo

The patch's own `Properties` entry is not reported back: setting `value` from the patch does not make `value` appear on the output. That only covers the set itself. If QML changes the property afterwards (a `Behavior`, an animation, a binding) or if properties that depend on it change (`percent` follows `value`), those changes do come back.

## Size

- **Auto Size on:** the element takes `width` and `height` of the root. For most files that is `implicitWidth` and `implicitHeight`; if the file declares an explicit `width`/`height` on its root, that one wins.
- **Auto Size off:** `Size` sets `width` and `height` of the root, the file's own size is ignored.

`Measured Size` reports what Qt measured, and `Debug` shows it (orange frame).

## Hot reload

The node watches the **folder of the file** for changes of the file itself, of any `*.qml` next to it and of `qmldir` (components the file imports by relative path). On a change QtNative clears the component cache for the file, builds a new instance and sends all property values again. The outputs start empty again (`Properties`, `Signals`), `Error` is empty when the new version compiles. Saving the file while vvvv runs is all it takes.

Limits:

- Only that one folder: subfolders are not watched, nor are `.js` files or images the QML uses. After editing those, save a `.qml` in the folder (or set `File` again).
- If the folder does not exist when `File` is set, nothing is watched until `File` is set again.
- A property error stays on `Error` until the name is set again or the file is reloaded, even when the entry was removed from the dictionary in the meantime.

## Dictionary behavior

- Only entries that changed since the last frame are sent, the node compares by content (changing the dictionary in place works).
- A key that is **removed** is not reset in QML: the property keeps its last value. Set the value you want, e.g. the default, to reset it.
- Unknown property names are an error line, the other entries are applied.

## Time: animations and timers

QML animations (`Behavior`, `NumberAnimation`, `SpringAnimation`, states with transitions, ...) run on the frame clock of the renderer (the same clock as everything else, see [Timing](decisions/timing.md)): they are deterministic and follow the time of the patch.

A QML **`Timer`** runs on wall time, it does not follow the frame clock. Use animations or drive the time from the patch (a property) where timing must be reproducible.

## Licensing of imports

VL.Qt ships only the LGPL modules of Qt (see [QtNative deployment](decisions/qtnative-deployment.md)). There is no import filter: an `import` of anything that is not shipped, such as `QtQuick3D`, `QtCharts` or `QtDataVisualization`, fails to load and the message shows on the `Error` output. Qt Quick (`import QtQuick`) and its basic modules are available.

## Out of scope

- Pins generated from the QML file (like the shader nodes from `qtshaders/`): the dictionary is the interface for now; a node factory is tracked in [#107](https://github.com/sebllll/VL.Qt/issues/107).
- C# objects or models handed to QML as context properties (the idea of Qt Bridge for C#, see [qt-bridges.md](decisions/qt-bridges.md)).
- Calling QML functions from the patch.
- Pointer input inside the QML file (`MouseArea`, handlers) is not part of the design; use the `Interaction` pin on the element. Use signals and properties to carry results back.
