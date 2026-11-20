# Reference images of the SmokeTest

Grabbed windows are compared with these PNGs (`ImageCompare`, TEST-03): mean absolute difference per channel and share of
differing pixels, limits as constants in `ImageCompare.cs`. The compare always runs on WARP (the software D3D11 adapter,
`QSG_RHI_PREFER_SOFTWARE_RENDERER=1`) in a helper process of its own, so a local run and the CI see the same pixels. The
references are made on the win-x64 workstation. The README pictures in `docs/images/` are not touched by any test.

```powershell
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=image-compare   # compare (readme-images, readme-gallery, reference-scenes); also the check on another machine, e.g. the arm64 notebook
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=update-refs     # explicit: rewrite all references
```

A FAIL names the grab and writes `<name>_diff.png` next to it in `build/smoke/grabs/` (difference x4, differing pixels red).
Change a reference only on purpose: commit the PNGs on their own and put the diff images of the failing compare into the pull
request description.
