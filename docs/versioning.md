# Versioning

VL.Qt follows [Semantic Versioning](https://semver.org). The guarantees below hold from version 1.0.0 on. Before 1.0.0 a minor release may break: nodes, pins and categories may be renamed or removed without an `Obsolete` phase, and the help patches change with the break.

## What counts as the API

The API is what a saved vvvv patch uses: the nodes and types VL.Qt shows in the node browser, their names and categories, their pins (name, type, default) and what they draw. A version number tells you whether your patches still open and look the same after an update.

Not part of the API:

- QtNative (`vlqt.dll`, `libvlqt.so`) and its exports, the protocol between C# and QtNative (`protocol.h`, `Protocol.cs`).
- Internal C# types and symbols with the aspect `Hidden` or `Internal`.
- Nodes with the aspect `Experimental` (see below).

## Major, minor, patch

| Part | Changes when | Examples |
|---|---|---|
| Major (`2.0.0`) | A saved patch breaks or looks different. | A node, pin or category is renamed or removed; a pin changes its type; a default changes with a visible effect. |
| Minor (`1.1.0`) | Something new is added and nothing breaks. | New nodes; new pins, preferably optional ones; a Qt update without visible change. |
| Patch (`1.0.1`) | Bugs are fixed. | A crash, a leak, a wrong value that did not match the documented behavior. |

## Obsolete nodes

From 1.0.0 on, a node or pin is not renamed or removed in one step. First it gets the aspect `Obsolete`: existing patches keep working, the node help names the replacement, and new patches should use that. It is removed in the next major version at the earliest.

## Experimental nodes

Nodes can be marked with the aspect `Experimental` when they are new or still taking shape. They are usable, but their pins and behavior may change in a minor version. Their node help says that they are experimental. When a node becomes stable, the aspect is removed in a minor version.

## Setting the aspects (contributors)

vvvv reads the aspects from the `[Smell]` attribute of `VL.Core.Import` (the `SymbolSmell` flags of `VL.Core`), which can be put on a node class, a type or a member:

```csharp
using VL.Core;
using VL.Core.Import;

[ProcessNode(Name = "MyNode", Category = Categories.Elements)]
[Smell(SymbolSmell.Experimental)]
public sealed class MyNode : IDisposable { /* ... */ }
```

- Use `SymbolSmell.Experimental` for experimental nodes and `SymbolSmell.Obsolete` (from 1.0.0 on) for nodes on their way out. Mention it in the `/// <summary>` of the node, so it appears in the node help.
