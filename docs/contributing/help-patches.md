# Writing help patches

The rules for the help patches of all three packages. The list of the patches by topic, for users, is [help-patches.md](../help-patches.md); the checks are in [Development](development.md#scripts) (`vl-patch-check.py`, `vvvvc.ps1`).

## Help Browser

- `help/Help.xml` is the manifest of the Help Browser: one `<Topic title="…">` per group, one `<VLDocument link="Qt\HowTo ….vl" tags="…" />` per patch (link relative to `help/`, `tags` are search words). The order in the file is the order in the Browser.
- **Every patch under `help/` belongs in `Help.xml`.** vvvv does list patches that are missing from an existing manifest (below the listed ones, ordered alphabetically; found in the Help Browser code of vvvv 8.0), but without a topic and in an order nobody chose. That is why the export patches (`HelloWorld*`, `HelloConsole`) have a topic of their own and nothing is left out by accident.
- Name patches `HowTo <Task in title case>`; the prefix is the role in the Browser (`Example`, `Explanation`, … for the other roles). Rename or delete a patch together with its line in `Help.xml`, in the docs and in the patches that refer to it.
- Check after a change: every `link` exists and every `.vl` below `help/` is listed once.

## Rules

- `.vl` files (vvvv's XML format) may be written in vvvv or directly, by an agent or a script. A direct edit keeps the format as vvvv saves it: a new unique `Id` for every new element, CRLF, UTF-8 with BOM, and the elements, `Id`s and positions it does not change left as they are. Afterwards `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File` must pass. A help patch written or rearranged directly gets the `manual-check` label until someone has looked at it in vvvv.
- Put an IOBox on a pin only if its value differs from the default and says something in the help patch. An IOBox at a pin is the same to the node as an attached value or channel. Example: an IOBox at `Interaction` makes the element opaque to input, so never put one there.
- Animate (LFO) only where it serves the element (motion demos); otherwise use fixed values.
- Layout: compact regions, content stacked top to bottom in data-flow order, IOBoxes as a staircase above (inputs) and below (outputs) their node. Choose defaults so the effect is visible.
- `help/*.props` (export settings) are tracked and written by vvvv; do not edit them by hand.
- Background: the [vvvv gray book](https://thegraybook.vvvv.org) for the patching conventions.

## Text scheme

Every help patch has the same four parts, top to bottom:

1. **Title** – `How To …`, font size 25.
2. **Features** – ONE comment (font size 9) directly below the title: a short headline with the node(s) shown, then 3 to 5 terse bullets (pin names, no prose, no sentences that repeat the title).
3. **Content** – the patch itself: IOBoxes and nodes as described under [Rules](#rules), with short labels where a picture needs them.
4. **Notes** – one comment at the very bottom, below all content: `Notes:` and the longer explanations, one bullet per point, nothing that is already in the features.

Example, `HowTo Draw a Rectangle`:

```text
How To Draw a Rectangle                                  (title, size 25)

Rectangle [Qt]                                           (features, size 9)
- Size, Radius, Color
- Stroke Color and Stroke Width
- Anchor: Center or TopLeft
- Position and rotation via Transform

… the Rectangle nodes, IOBoxes, Renderer …               (content)

Notes:                                                   (notes, size 9, bottom)
- Size is width and height in units (in the default space `DIPTopLeft`, 1 unit = 100 DIP). A Rectangle with width or height 0 is not drawn.
- Position, rotation and scale come only from Transform. The Anchor is the point of the rectangle that sits at the
  origin and the pivot of Transform: TopLeft (orange) puts the top left corner on the Translation, Center (default, blue)
  centers the rectangle on it.
- Connect it to Renderer [Qt] or a Group [Qt].
```

## Renames

A change that renames a node or a pin or moves a category does not edit the patches by hand. It adds rows to [`tools/vl-patch-mapping.txt`](../../tools/vl-patch-mapping.txt) and runs `python tools/vl-patch-migrate.py`, which rewrites every patch of the repository (`help/`, `VL.Qt.vl`, `VL.Qt.HDE.vl`, `VL.Qt.Stride/`, `VL.Qt.Audio/`, `src/`). A change that adds a pin or rebuilds a patch still edits the `.vl` file directly.

- One row per rename: `step | kind | scope | old | new`, kinds `node`, `pin` and `category`. The header of the file explains the columns. Pin names are written as vvvv shows them (`Fill Color`) or as the C# parameter (`fillColor`), which the rule of `tools/vl_node_api.py` turns into the same name.
- The rows of one step apply together: `Radius → Set Radius` and `Get Radius → Radius` in one step never turn a setter into a getter. Steps apply in the order of the file.
- The script touches only VL.Qt references: the node name, its `Pin` and `PinReference` names, the category (`LastCategoryFullName`, the `CategoryReference` chain) and, for a category move, the type references. CRLF, the BOM, Ids, positions and every other byte stay as they are.
- It records nothing in the patches. Each name a row gives lives over a range of steps (an old name until its step, a new name from its step on), and the names of a node tell which steps it has seen; the script applies only the steps after that. So a second run changes nothing, and a patch whose nodes stand at different steps after a git merge is still migrated correctly. A node whose names do not tell the steps apart (a swap where the node shows only `Radius`), or that mixes old and new names, is reported instead of guessed.
- On a merge conflict in a `.vl` file: merge the mapping file (keep the rows of both sides, the side merged first comes first), take either side of the patch and run the script again. Never change or remove a merged row, since the script reads the steps from it.
- `--check` lists what would change and exits with 1 if anything would. Exit code 2: a bad or conflicting row, a new name that is not in `src/VL.Qt.Tests/Api/node-api.txt` after all steps (`--no-api-check` skips that lookup), or a node the script cannot place; nothing is written then.
- Afterwards `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File` run as after any patch change. `tools/test-vl-patch-migrate.py` is the regression test.
