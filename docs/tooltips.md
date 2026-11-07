# Tooltips in the vvvv editor

Hover a link that carries an `Element` or a `QtImage` and the tooltip shows a preview image. It comes from `VL.Qt.HDE.vl`, which only the editor loads, so exports are not affected.

- **Element:** only elements that a renderer shows get a preview (otherwise just text). It is live, at most about 5 images per second and only when the scene changed; the longest edge is at most 256 px. Elements containing a `Video` get text only, no preview (it would cost too much frame time).
- **QtImage:** a preview of at most 256 px, made when the image is loaded; editor only.
- **F5 overlay:** on the Resources page the line `snapshots` shows rate and duration of the preview snapshots (n/s, last, max in ms); without an open tooltip it reads 0/s. See [Diagnostics](diagnostics.md).
