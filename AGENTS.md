# AGENTS.md

Conventions for coding agents and contributors working on VL.Qt. `CLAUDE.md` only imports this file for Claude Code; personal additions go in `CLAUDE.local.md`, which stays out of the repository.

## What this repo is

VL.Qt brings Qt 6 (Qt Quick) to vvvv gamma as a library, focused on 2D graphics content (motion, typography, layout). Three layers:

- `src/VL.Qt/` – C# library: element types, vvvv nodes, reconciler (element tree diffing), interop.
- `src/VL.Qt.Native/` – **QtNative**, the C++ layer that owns Qt and the render thread (`vlqt.dll`, `libvlqt.so`).
- `help/Qt/` – help patches (`HowTo …`), the test base for nodes and the user-facing examples.

Companion packages (Windows): `src/VL.Qt.Stride` (`QtTexture`, `QtRenderer`) and `src/VL.Qt.Audio` (`Video (VL.Audio)`), with the package folders `VL.Qt.Stride/` and `VL.Qt.Audio/`. Tests: `src/VL.Qt.Tests` (unit), `src/VL.Qt.Audio.Tests`, `src/VL.Qt.SmokeTest` (QtNative without vvvv); shared code in `src/VL.Qt.TestSupport`.

First build and scripts: [development.md](docs/contributing/development.md). How the pieces fit: [architecture.md](docs/contributing/architecture.md). Words: [glossary.md](docs/glossary.md).

## Before you finish

`tools/build.ps1 -Test` runs the gate; `-SkipNative` skips QtNative for C#-only changes. After a change in QtNative, rebuild it and restart vvvv (no hot reload). A change is done when:

- the unit tests and the SmokeTest pass; they include the drift tests of `protocol.h` and `vlqt.h` ([Change the protocol](docs/contributing/development.md#typical-workflows));
- the node-API snapshot `src/VL.Qt.Tests/Api/node-api.txt` is regenerated if a node, pin or type changed (`VLQT_UPDATE_SNAPSHOT=1` rewrites it; the author of the change does it);
- `python tools/check-source-rules.py` passes (its header lists the rules);
- the affected help patch passes `python tools/vl-patch-check.py "<patch>"` (no argument: all help patches of the three packages) and `tools/vvvvc.ps1 -File "<patch>"` (run `dotnet build src/VL.Qt -c Release` first);
- the docs page of the changed behavior under `docs/` is updated;
- encoding is right ([Encoding](#encoding)).

## C# rules

The reference is [VL.StandardLibs](https://github.com/vvvv/VL.StandardLibs). Correct deviations in files you touch, not in separate PRs.

- Allman braces, 4 spaces, block-scoped `namespace X { }`, no `#region`. `var` for locals, `=>` for one-line members.
- Private fields `_camelCase`, private statics `s_camelCase`, constants camelCase, the rest PascalCase. `<Nullable>enable</Nullable>`; `Optional<T>` for "not set", not `T?` on value types.
- `sealed`, `internal`, `readonly` by default; `public` only for node and API types. A public type that is not a node sits in the namespace of its category (`ImportAsIs` in `Properties/AssemblyInfo.cs`); the node browser is steered with `[Smell(SymbolSmell.Hidden)]`, `Advanced`, `Experimental` (never `Hidden` on a type a visible pin uses).
- A node is a `[ProcessNode]` class (name `…Node`, file name = type) with a constructor taking a `NodeContext`, one `Update(...)` method, and `IDisposable`. Outputs via `[return: Pin(Name = "Output")]` or `out` parameters (last), no tuples; optional pins with `Pin(Visibility = PinVisibility.Optional)`.
- Defaults: set one explicitly unless `default(T)` is meant, as `[DefaultValue(...)]` next to `Pin`, never as a C# parameter default and never for `Matrix` (vvvv's default is the identity). Enums `typeof(E), nameof(E.X)`; other Stride structs (`Vector2`, `Color4`, `RectangleF`) the string form `"1, 1, 1, 1"`; a form vvvv cannot read silently gives `default(T)`.
- Pin names come from the parameter names; `[Pin(Name = "…")]` only for wrong spelling or acronyms, and when renaming a parameter. Renaming or removing a node or pin: [versioning.md](docs/versioning.md).
- Change detection instead of rebuilding every frame; allocate nothing per frame in hot paths.
- `SerialDisposable` / `CompositeDisposable` / `Disposable.Create`; begin/end pairs through `using` or `try/finally`. Locks only on a private `syncRoot`, short bodies. Never trigger a collection (`GC.Collect`, `GC.WaitForPendingFinalizers`).
- Platform code behind `OperatingSystem.Is…()` / `[SupportedOSPlatform]`.
- Errors from Qt/QML go through the node's `NodeErrors` (shown at the node, logged once per distinct text); no `Error` output pins. Throw only for programming errors.
- Log through the vvvv logger (`nodeContext.GetLogger()`, `appHost.LoggerFactory`) with message templates, not interpolation. No `Console.Write*` or `Debug.Write*` for messages.
- `/// <summary>` (English, "Returns true if …") on every node and non-trivial pin, `<param>` for every `Update` pin, `<returns>` for the return pin. Shared pin texts live in `src/VL.Qt/Nodes/CommonPins.xml` (`<include>`). Write for the patcher: no Qt type names.
- Comments in English, short, above the line, explaining why; `// TODO: sentence.`

## QtNative rules

- One folder per area under `src/VL.Qt.Native/`, `snake_case` file names.
- Qt private classes are allowed; every use is listed in the head comment of `elements/elements.h` (the checklist for a Qt update) and in the head comment of the file that has it.
- QtNative runs Qt's `basic` render loop on purpose (`app/app.cpp`).
- No JavaScript for layout in the QML of built-in elements: the alignment of `Row` and `Column` is computed in QtNative. QML functions stay only as glue to Qt types.
- Environment variables only for what must be set before vvvv starts and for test switches; everything else is a pin.
- Exceptions never cross the C API; ownership of strings, buffers and handles across it is fixed in the head comment of [vlqt.h](src/VL.Qt.Native/vlqt.h).

## Help patches

Test base of the nodes; full rules: [help-patches.md](docs/contributing/help-patches.md).

- Every patch is listed in `help/Help.xml`; add, rename or delete the line with the patch.
- A `.vl` written directly keeps vvvv's format (new unique `Id`s, CRLF, BOM) and gets the label `manual-check` until someone has looked at it in vvvv. `help/*.props` are written by vvvv, never by hand.
- An IOBox on a pin only where its value differs from the default; never on `Interaction`.
- Renamed nodes and pins reach the patches only through `tools/vl-patch-mapping.txt` and `tools/vl-patch-migrate.py`, never by hand.

## Encoding

- LF in the repository (`.gitattributes`); `.vl` and `help/**/*.props` are checked out with CRLF. No hand conversion, no EOL-only commits.
- UTF-8 **with BOM** only for `.cs` and `.vl`, all other files without.
- A diff that shows whole files as changed means line endings or BOM: `git add --renormalize .` or `tools/fix-encoding.py`. The git hook ([setup](docs/contributing/development.md#encoding-and-git-hook)) repairs the BOM at every commit.

## Git and pull requests

Details, the fork and maintainer paths and the issue labels: [CONTRIBUTING.md](CONTRIBUTING.md).

- Work on a branch from `develop`; open the pull request as a draft against `develop`; `gh pr ready` only after `tools/build.ps1 -Test` passes locally.
- Never push to `develop` or `main`, never a bare `git push`: `git push origin HEAD:<kind>/<issue>-<topic>`.
- Every new issue gets a type, an area and a milestone from the labels in CONTRIBUTING.md; do not invent labels or milestones.

## Where to read on

[Contributing pages](docs/README.md#contributing), the walkthrough [add-an-element.md](docs/contributing/add-an-element.md), [debugging.md](docs/contributing/debugging.md) (hang, crash; the in-app tools are in [diagnostics.md](docs/diagnostics.md)), [docs/design/](docs/design/) for the reasons behind decisions.
