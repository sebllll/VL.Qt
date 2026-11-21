# Images

Images are GPU resources (`QtImage`), separate from the `Image [Qt]` element that draws them. A loaded image is uploaded once and can be shown by any number of Image elements in any window; switching between loaded images costs nothing.

<img src="images/images.png" width="820" alt="One image drawn in the four fill modes Stretch, PreserveAspectFit, PreserveAspectCrop and Tile">

## Nodes

| Node | Use |
|---|---|
| `LoadImage` | Loads a file (PNG, JPG, BMP, … everything Qt reads, EXIF rotation applied). Blocks the frame: for startup or Create. |
| `LoadImage (Async)` | Same without blocking: decoding runs in a worker, the GPU upload is spread over frames. The previous image stays until the new one is ready (`Keep Previous`); `Is Loaded` and `On Loaded` tell when it is. |
| `ToQtImage` | Uploads an `IImage` of vvvv (Skia, OpenCV, camera, …). Uploads only when a new image arrives; 16 bit and float are reduced to 8 bit with a warning. |
| `VideoSourceToQtImage` | Shows a VL.Video source (VideoPlayer, camera, …) in the Image element. Each new frame replaces the content of the same `QtImage`; frames are copied from CPU memory. |
| `Snapshot` | The other way: grabs an element or Group (with children) as an `IImage` on `Grab`, e.g. for thumbnails, export or processing in Skia/OpenCV. Asynchronous, `Is Ready` comes one frame later. `Scale` sets pixels per DIP independent of the screen scaling. |

## Mipmaps, frame budget, errors, device loss

- **Mipmaps** (pin on the loaders) make images smoother when drawn much smaller, at +33 % GPU memory.
- **Frame budget:** uploads are spread so that loading does not stall the frame. Load a spread of files ahead with `LoadImage (Async)` (see `HowTo Preload Images`).
- **Errors** go to the log. A failed load or upload (`LoadImage`, `LoadImage (Async)`, `ToQtImage`, `VideoSourceToQtImage`) also marks the node until an input changes, and the failed image draws nothing; a failed `Snapshot` or `FileWriter` is only logged (`FileWriter` has `Success` next to `Is Done`).
- **F5 → Resources** lists all live images with state, size, references and age, failed or stuck ones first, plus upload queue and budget.
- **Device loss:** after a GPU reset all windows and images are rebuilt; the output comes back by itself (see [GPU device loss](diagnostics.md#gpu-device-loss)).

## Help patches

`HowTo Draw an Image`, `HowTo Preload Images`, `HowTo Use an IImage`, `HowTo Take a Snapshot`, `HowTo Show a VL.Video player in Qt`. See [Help patches](help-patches.md).
