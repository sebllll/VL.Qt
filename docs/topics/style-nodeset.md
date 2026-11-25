# Style Node Set

Design of the nodes that build, merge and read styles for interactive elements (widgets such as Button, Slider, Checkbox, TextField).

## Background
- A style is an immutable C# record. Every field is `Optional<T>`; "not set" means inherit from the parent. It holds a shared set (colours per state, font, radius, padding, border) plus sub-objects per control type.
- Widgets and Group have a `Style` pin. A Group passes its style on, and a style on a child overrides only the fields it sets. Primitives (Rectangle, Text) have no style pin.
- Resolution (base style → groups → element) happens entirely in C# in the reconciler. QtNative only receives fully resolved values.
- Hand-patched widgets implement `IStylable` with `Build(Style resolved) → IElement`.

## Design
### Chain form
- Setter nodes form a chain: a `Style` input on top, a `Style` output below, as in Skia and ImGui. An empty input means an empty style; an unconnected pin means "not set". There is no style constructor, because a chain with an empty input does the same.
- `Merge (Style)` combines two finished styles, for example a preset and overrides coming from another branch.
- Presets `Dark` and `Light` are patched from setter nodes, so they can be opened and copied as templates. There is no `Default` preset.
- Getter ("split") nodes return resolved values: fields that are not set fall back to the base style, and there is no `Optional`.

### Generic nodes per component, not per control
- `Frame`, `Text`, `Indicator`, `Track` are generic setters. `Indicator` covers slider handle, checkbox tick and switch knob; `Track` covers slider groove and switch track.
- Colours live in a separate `Colors` setter (pins Frame, Border, Text, Indicator, Track, Track Fill); the component nodes set sizes and shape.
- For quick overrides directly on a widget there are single-field setters per component (for example `Radius`), in the Advanced category.
- Components of different controls are set separately by assigning the style on the widget or on a group of its own.

### States
- Every setter, including the single-field ones, has a `State` pin of a flags enum (Normal, Hovered, Pressed, Disabled, Checked, combinable with `|`, plus `All`). The default is `All`, so a value applies to every state unless refined, for example `Colors (State = Hovered)`.
- Inheritance is strictly per field and state. There are no automatically derived hover or pressed colours: no magic, and a value set with `All` behaves predictably.
- When several states are active, a fixed precedence applies: Disabled > Pressed > Hovered > Checked > Normal. Using the graph order as precedence (CSS-like "later wins") was rejected: the record would have to store ordered lists and QML would evaluate them at runtime, and the order would be hard to read across inheritance and `Merge`. Chain order only decides overriding of the same field in the same state.
- Every getter has a `State` input for exactly one state (no `|`). The helper `ResolveState (IsHovered, IsPressed, IsDisabled, IsChecked) → State` applies the fixed precedence, so a hand-patched widget that knows its state (for example from the interaction nodes) follows the same rules as the built-in widgets.

### Naming and categories
- A setter is named after the component (`Frame`, `Text`, `Indicator`, `Track`, `Colors`); the getter has `Get` in front (`GetFrame`, …).
- Categories per component: `Qt.Style.Frame.Frame` (setter), `Qt.Style.Frame.GetFrame` (getter), `Qt.Style.Frame.Radius` and `Qt.Style.Frame.GetRadius` (single setter/getter, Advanced). The same applies to `Text`, `Indicator`, `Track`.
- Single colour setters sit with their component (`Qt.Style.Frame.Color`, `Qt.Style.Frame.BorderColor`, `Qt.Style.Indicator.Color`, …). The collective `Colors` and `GetColors` sit directly in `Qt.Style`, as do `Merge`, `ResolveState`, `Dark` and `Light`.
- Rationale: everything about one component is found in one place in the node browser, and the category name disambiguates identically named fields (`Radius` on Frame and on Indicator). A component name never occurs twice.

## Open questions
- A `ForControl` node (a partial style for one control type only), if the generic component split proves insufficient.
