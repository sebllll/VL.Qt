# Exporting an app

Export a patch with vvvvc via the helper script (builds into `build/vvvvc` by default):

```powershell
tools/vvvvc.ps1 -File help/HelloWorld.vl -Out build/export
```

- The export bundles the Qt runtime next to the executable, only for the runtime identifier (RID) of the exported app.
- Export settings live in the `*.props` files next to the patches (`help/*.props`).
- A `qtshaders/` folder next to the exported patch is copied along (see [Effects and shaders](effects-and-shaders.md#shader-nodes-from-files)).
- For a Linux export (console app with a Qt-owned window) see [Linux](linux.md).
