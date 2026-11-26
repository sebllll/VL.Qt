# QtTexture manual checks

Three patches for `QtTexture` (VL.Qt.Stride) that a person looks at in vvvv. No test runs them: the SmokeTest covers the native texture path (`src/VL.Qt.SmokeTest`), `src/VL.Qt.Tests/Stride/` the pure parts of the Stride layer (pixel mapping, formats, texture bookkeeping), these patches cover what only shows with Stride and a window.

Open a patch in vvvv with the repository as a source package (`tools/start-vvvv.ps1`), and read the text field at the top of the patch as well.

| Patch | What to look at | What it checks |
|---|---|---|
| `QtTexture01_Basic.vl` | A `TextureWindow` shows the texture of a `QtTexture` (Size 512 × 512, clear color transparent): a semi-transparent blue `Rectangle` that moves (`TransformSRT`, `LFO`) and a `Text`. | The basic path: a Qt scene is rendered into a Stride texture, alpha is kept (the blue is see-through over the window background), `Size`, `Format`, `Clear Color` and `Space` take effect. |
| `QtTexture02_FrameOrder.vl` | A frame number in the Qt texture (top half, yellow-red) and the same `FrameCounter` drawn directly by Stride (bottom half, black). Take a screenshot while it runs: both numbers must be equal in every frame. | Frame order: the texture is not one frame late (a lag shows the previous number, off by 1), also while the window is hidden or moved. The `QtTexture` node shows no error. |
| `QtTexture05_Stress.vl` | Long run (several minutes): `Size` follows an `LFO` (period 120 s, 1 × 1 up to 1600 × 900), a toggle switches `Format` between `B8G8R8A8_UNorm_SRgb` and `R16G16B16A16_Float`. Watch GPU memory and the handle count of vvvv in Task Manager. | Resize and format change leak nothing (no staircase growth), no exception. Also delete the `QtTexture` node and undo, and close the document while it runs: no exception, no hang. |

`vl-patch-check.py` checks these patches with the others (pins against the C# sources). They are not in `help/Help.xml`: they are checks, not help for patchers.
