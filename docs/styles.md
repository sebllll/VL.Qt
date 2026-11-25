# Styles

All widgets (Button, Toggle, Slider (Float), Input (String), Input (Int)) are drawn in VL.Qt's own base style **VLQt**: flat, neutral, the same on Windows and Linux. Every widget and every `Group [Qt]` has a `Style` pin to change it:

- A style only holds what you set; everything else is inherited. It is resolved top-down: base style → each Group on the way → the widget's own Style pin.
- A Group passes its style to everything inside, also to Row/Column and nested Groups. A Style on a single widget overrides only its set fields.
- Each field has a value per state: Normal, Hovered, Pressed, Disabled, Checked. An unset state falls back to Normal.

<img src="images/styles.png" width="820" alt="The same widgets in the VLQt base style and in a light style">

## Style nodes

The nodes live in `Qt.Style`. Build a style by chaining them, each one adds to the style before it:

| Node | Part | Fields |
|---|---|---|
| `Colors` | Frame, Text | frame and text color in one node |
| `Frame` | background of the control | Color, Border Color, Radius, Border Width, Padding X/Y, Min Width/Height |
| `Text` | label | Color, Font Family, Font Size, Font Weight |
| `Indicator` | slider handle, check mark, switch knob, SpinBox −/+ | Color, Size, Radius, Border Width |
| `Track` | slider groove, switch track, check box | Color, Fill Color (value, on state, text selection), Thickness, Radius |

- **Presets:** `Dark` and `Light` are complete styles to start from.
- **Merge:** combines two styles, set fields of Input 2 win.
- **Single setters** (`Radius`, `FontSize`, `Color`, … in the Advanced aspect) set one field, for quick overrides.
- **Getters** (`GetColors`, `GetFrame`, `GetText`, …, `ResolveState`) return resolved values, also without a connected style (then the base style applies).

Sizes in a style are px. Rectangles and Text have no Style pin: they are primitives, their look comes from their own pins.

## Self-made widgets

A patched class that implements `IStylable` behaves like a built-in widget: connect it wherever an element goes (Group, Row, Column, Renderer, Labeled, an effect input), and its `Build` operation is called with the style resolved at that position; the returned element is shown. Build may return another stylable, which is built in turn; one that returns itself or a cycle shows nothing and warns. Inside Build, use the getters to read colors, radius and font for the current state and draw the widget from primitives. Build runs once per frame; element nodes return the same instance while nothing changes, so the scene stays untouched. The output pin of Build must be named `Result`.

## Help patches

`HowTo Style a Widget`, `HowTo Style Control Parts`, `HowTo Style Widgets with a Group`, `HowTo Patch a Stylable Widget`. See [Help patches](help-patches.md).
