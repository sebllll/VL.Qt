# Exporting an app

Export a patch with vvvvc via the helper script (builds into `build/vvvvc` by default):

```powershell
tools/vvvvc.ps1 -File "help/Qt/Demo Typo Motion.vl" -Out build/export
```

- The export bundles the Qt runtime next to the executable, only for the runtime identifier (RID) of the exported app.
- Export settings live in the `*.props` files next to the patches (`help/*.props`).
- A `qtshaders/` folder next to the exported patch is copied along (see [Effects and shaders](effects-and-shaders.md#shader-nodes-from-files)).
- A `fonts/` folder next to the exported patch is copied along as well (see [Fonts](#fonts)).
- vvvvc exports self-contained by default (the .NET runtime is part of the export folder); `--self-contained false` makes a smaller framework-dependent export that needs the .NET 10 runtime on the target.
- A `.props` next to the patch wins over the command-line arguments `--rid` and `--output-type`. Patches without one (all of `help/Qt/`) get them from the command line, e.g. for Linux arm64: `tools/vvvvc.ps1 -File "help/Qt/HowTo Draw a Rectangle.vl" -Out build/export-linux-arm64 --rid linux-arm64 --output-type Exe --asset-behavior RelativeToOutput`.
- For a Linux export (console app with a Qt-owned window, needs a `KeepAppAlive` in the patch) see [Linux](linux.md).

## Fonts

`Text` and `Style` name a font only by its family. Qt looks it up among the installed fonts, and a font that is not installed on the target computer is replaced silently (on a Linux without fonts by empty boxes). Fonts that are not installed everywhere are shipped with the app and loaded from the file:

1. Put the font files (TTF, OTF, TTC) in a `fonts/` folder next to the patch that you export, and put their license files there as well.
2. In the patch, give the file to a `LoadFont` node with a `Path` IOBox that points into that folder (`fonts/MyFont.ttf`); its `Family` output goes to the `FontFamily` pin of `Text` or `Style`.
3. Export. The export copies `fonts/` next to the exe, and with `VLAssetBehavior` `RelativeToOutput` (as in the `help/*.props` export settings) vvvv writes the `Path` as `fonts/MyFont.ttf`, relative to the exe, so the loaded font is found there.

vvvv does not copy files that a `Path` pin refers to by itself: with `RelativeToOutput` it only writes the path relative to the exe and expects the file to be placed there. VL.Qt does that for `fonts/` (and `qtshaders/`) of the exported document only. A font file kept elsewhere (another folder, another document, a package) has to be copied beside the exe by hand, to the same relative path it has to the exported (root) document during development. With `RelativeToDocument` the exe refers to the file where it is, which only works on the computer that has it.

If `LoadFont` cannot read the file it shows a warning at the node and `Family` stays empty. A family name that neither the system nor a loaded font provides is logged once as a warning (category `vlqt.font`: `Font family '…' not found, Qt uses '…' instead`).

> [!WARNING]
> Many font licenses (most desktop fonts) do not allow embedding the font in an app or passing it on. Ship only fonts whose license permits that (for example the SIL Open Font License) and keep the license text beside the font file.
