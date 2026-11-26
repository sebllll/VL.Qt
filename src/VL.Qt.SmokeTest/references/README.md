# Reference images of the SmokeTest

Grabbed windows are compared with these PNGs (`ImageCompare`): mean absolute difference per channel and share of
differing pixels, limits as constants in `ImageCompare.cs`. The compare always runs on WARP (the software D3D11 adapter,
`QSG_RHI_PREFER_SOFTWARE_RENDERER=1`) at device pixel ratio 1 (`QT_ENABLE_HIGHDPI_SCALING=0`, whatever the monitor scale) in a
helper process of its own, in a borderless window (`NativeCalls.CreateCompareWindow`: Windows limits a framed window to the
screen size, 1024 x 768 on the CI runner), so a local run and the CI see the same pixels. The
references are made on the win-x64 workstation. The README pictures in `docs/images/` are not references: only the explicit `readme-docs` test writes them (normal renderer, default font, never the references).

```powershell
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=image-compare   # compare (readme-images, readme-gallery, reference-scenes); also the check on another machine, e.g. the arm64 notebook
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=update-refs     # explicit: rewrite all references
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=readme-docs      # explicit: regenerate docs/images
```

A FAIL names the grab and writes `<name>_diff.png` next to it in `build/smoke/grabs/` (difference x4, differing pixels red).
Change a reference only on purpose: commit the PNGs on their own and put the diff images of the failing compare into the pull
request description.

`trace-<name>.png` are the last frames of the command traces in `../traces/` (`TraceReplay`, category `replay-traces`): each trace recorded from a help patch by `tools/record-traces.ps1` is replayed against QtNative (shaders settled) and compared like the other references. The resources the patch had loaded are listed next to the trace (`<name>.images`, `<name>.shaders`); the replay creates them and rewrites the trace's ids to them, so a trace does not depend on the resources other scenes of the same process created. Re-record the traces after a protocol change, then `update-refs`.
