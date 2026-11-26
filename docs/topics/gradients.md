# Topic: Gradient fills

Handover from the brainstorm on lines, paths and splines ([../decisions/paths.md](../decisions/paths.md)), 2026-11-18. Gradients were out of scope in [../design/element-model.md](../design/element-model.md).

## Context
- Elements fill with a plain `Color4` today (`Rectangle`, `Circle`, `Text`).
- The new shape elements (`Polygon`, `Path`) run on Qt Quick Shapes. `ShapePath` offers `fillGradient` (`LinearGradient`, `RadialGradient`, `ConicalGradient`, each with `GradientStop`s and a spread mode), `fillItem` (an item as texture fill, Qt 6.8+) and `fillTransform`.
- `Rectangle` in Qt Quick only knows a vertical or horizontal linear `gradient`; other gradients there would need Shapes or a shader.

## Open questions
- A gradient value type (stops as `Spread<Color4>` + positions?) shared by all elements, or only on shape elements?
- Which elements get it: shapes only, or also `Rectangle`, `Circle`, `Text`?
- Brush concept (color | gradient | image) on one pin, or separate pins?
- Protocol value type for stops; strokes with gradients too?

## Relevant files
- `src/VL.Qt/Elements/Rectangle.cs`, `src/VL.Qt.Native/elements/type_rectangle.cpp`, `src/VL.Qt.Native/protocol.h`
- Qt: `C:\Qt\6.12.0\<arch>\qml\QtQuick\Shapes\plugins.qmltypes`
