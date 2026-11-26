# Shader reference – VL.Qt against VL.Stride TextureFX

Claude Opus 5.5, 2026-11-19. P12 of [PLAN-CHECK.md](../PLAN-CHECK.md), principle "Shaders follow VL.Stride and its TextureFX" (E1 in [QUESTIONS.md](../QUESTIONS.md), [DECISIONS.md](../DECISIONS.md)). Read-only; no code was built or run.

## 1 Summary

- VL.Qt and TextureFX agree on the shape of a shader node: a file per node, the name from the file, `Input` for the main texture, pins from the uniform names split at camel case, `Output`, a summary and a category from annotations.
- They disagree on units. TextureFX takes almost every length normalised (Blur `Strength`, DropShadow `Offset` in UV, Displace `Offset Scale`, BlurGlow `Radius`); VL.Qt's shader effects take lengths in renderer units and convert them to DIP for the shader. E1 asks for DIP: it overrides TextureFX on units and turns ten pins and their defaults from units to DIP (SREF-01). The rows that are shader effects follow from E1; DropShadow `Offset` and the two `Padding` pins of the merged effects are open (question 2).
- Blur `Max Radius` is already DIP: Qt's `blurMax` is measured in item coordinates, which are logical pixels. Only the word "pixels" in the pin help changes (SREF-02).
- Angles: the built-in shaders and the five templates still take degrees in their `angle` uniforms; C10 carries over to them (SREF-03).
- Names: where VL.Skia has the node (Blur `Amount`, DropShadow `Offset` as `Vector2`) this report keeps the Skia name (A7) instead of TextureFX's (`Strength`, `Offset` + `Angle`). Whether A7 or E1 wins for node and pin names is question 1.
- Five effects have no TextureFX counterpart (ZoomBlur, RadialBlur, InnerShadow, Colorize, and Glow, whose TextureFX namesake is a bloom); Mask, ColorAdjust, Blend, Displace, DirectionalBlur, Blur and DropShadow have one.
- Shader files: missing against TextureFX are `@optional`, `@tags`/`@remarks`, relative categories and a size uniform for element shaders (SREF-07 to SREF-10).

## 2 Method

Read in full: [DECISIONS.md](../DECISIONS.md), P12 of [PLAN-CHECK.md](../PLAN-CHECK.md), glossary 1.2 of [TARGET.md](../TARGET.md), block E and C10, C11, C14, C15 of [QUESTIONS.md](../QUESTIONS.md), section 7 of [23_api.md](23_api.md), [12_shaders.md](12_shaders.md); the effect findings of [15_nodes-effects-interaction-paths.md](15_nodes-effects-interaction-paths.md) (NODX-01, -07, -08, -12) and of [21_native-shaders-effects.md](21_native-shaders-effects.md) (NFX-14, NFX-16).

VL.Qt (branch `refactor`, a7d53bd): the twelve nodes in `src/VL.Qt/Nodes/Effects/`, `src/VL.Qt/Elements/Effect.cs`, `Uniform.cs` (the `Length` uniforms), `src/VL.Qt/Reconciler/EffectDiff.cs` (which values are converted with `ToQt`), `src/VL.Qt/Space.cs`, `src/VL.Qt/Shaders/ShaderPinModel.cs`, `ShaderNodeLayout.cs`, `ShaderNodeFactory.cs` (names, categories, reserved names), `src/VL.Qt/Nodes/ShaderEffectNode.cs` (pins), the eleven built-in shaders in `src/VL.Qt.Native/shaders/` (headers and uniform blocks; the pyramid shaders only in their headers), `shadereffectitem.cpp:765-800` and `effectchain.cpp:229` (reserved uniforms), `protocol.h:416, 430` (units of `BLUR_MAX` and `PADDING_RECT`), the seven files in `help/Qt/qtshaders/`, [docs/effects-and-shaders.md](../../../../docs/effects-and-shaders.md). `gradient.frag` and `wave.frag` are test shaders that move to the SmokeTest (NFX-16); they are not compared.

VL.Stride (`C:\vl\VL.StandardLibs`, `git log -1`: 61bd4ac, 2026-11-18, "Replaces experimental warning on node factory interfaces with proper docs"): the shaders in `VL.Stride.TextureFX/shaders/` (Blur, DirectionalBlur, Glow, BlurGlow, DropShadow, HSCB, Levels, TriColor, Grayscale, Displace, BlendMixer), `VL.Stride.Runtime/src/Effects/TextureFX/` (`TextureFX.sdsl`, `FilterBase.sdsl`, `MixerBase.sdsl`, `Utils/SetAlpha_TextureFX.sdsl`), the factory `VL.Stride.Runtime/src/Rendering/Effects/EffectShaderNodes.cs`, `EffectShaderNodes.TextureFX.cs`, `EffectShaderNodes.Utils.cs` (`GetNodeName`), `EffectUtils.cs` (`GetPinName`), `ShaderMetadata.cs` (attributes, `GetCategory`), the HDE templates `VL.Stride.HDE/Assets/Templates/ShaderTemplates/*_TextureFX.sdsl`, the patched wrappers in `VL.Stride.TextureFX/VL.Stride.TextureFX.vl` (Blur, DirectionalBlur, Glow, Blend: pin names and defaults), `BlendOperator` in `VL.Stride.Runtime/src/Shaders/ShaderFX/Operations/BlendOperation.cs`, the list of help patches in `VL.Stride.TextureFX/help/`. Searched, not read in full: `VL.Stride/help/Textures/HowTo Patch a TextureFX.vl` (only its comments), `VL.Skia/VL.Skia.vl` (pin names of Blur, DropShadow, SetBlendMode, for A7).

How TextureFX turns a shader into a node, as far as it matters here:

- A file `Name_Variant_TextureFX.sdsl` in a `shaders/` folder makes two nodes: `NameShader` in `Stride.Rendering.ImageShaders.Experimental.Advanced` and the TextureFX node `Name (Variant)`. `_Internal` is a variant like any other (`BlurPass1_Internal_TextureFX` → `BlurPass1 (Internal)`); the public Blur, DirectionalBlur, Glow and Blend are patched in `VL.Stride.TextureFX.vl` over such internal passes.
- Kind: `[TextureSource]` makes a source (pin `Enabled`, `Output Size` 512 × 512 visible); otherwise a filter or mixer (pin `Apply`, `Output Size` hidden and taken from the input). `FilterBase` adds `Control` (Float4, default 1, blends input and result per channel), `MixerBase` adds `Fader` (0.5).
- Textures of the `Texturing` shader become `Input`, `Input 2` …; their samplers `Sampler`, `Sampler 2` …, optional, placed after their texture. Other parameters: `GetPinName` splits at lower→upper case or digit (`blurLength` → `Blur Length`, `pass1` → `Pass 1`) and capitalises the first letter; on a clash it prefixes the shader name.
- Defaults from `[Default(…)]` on `compose` members or from the initializer (`float Blur = 0.5f;`). Attributes: `[Category]` (relative to `Stride.Textures` unless it starts with it or with `:`), `[Summary]`, `[Remarks]`, `[Tags]`, `[Color]`, `[EnumType]`, `[Optional]`, `[OutputFormat]`, `[OutputSize]`, `[RenderFormat]`, `[WantsMips]`, `[DontConvertToLinearOnRead]`, `[DontConvertToSRgbOnWrite]`. No min/max.
- In the shader: `Texture0`/`Sampler0`, `InTex0()`, `streams.TexCoord`, `ViewSize` (output size in pixels), `Texture0TexelSize`.

## 3 Rows

Columns as in [23_api.md](23_api.md) section 7, plus "TextureFX counterpart" and "Reason for a deviation". "Current" is taken from the code. Units: "units" means the renderer's space (`Space`; in the default `DIPTopLeft` 1 unit = 100 DIP, in `Normalized` half the scene height). Converting a default from units to DIP uses 1 unit = 100 DIP, which keeps the picture of the help patches in the DIP spaces. "Decision" is filled only where a decided principle leaves no choice; empty rows are open.

### 3.1 Blur

Counterpart: `Blur [Stride.Textures.Filter]`, patched in `VL.Stride.TextureFX.vl` over `BlurPass1..3_Internal_TextureFX`: `Input`, `Strength` (0.25, normalised: a fraction of log2 of the texture size), `Passes` (`BlurPasses`, `TwoPasses`), `Control`, `Apply`. VL.Skia: `Blur` with `Amount`.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| Blur / Amount | `Amount`, `float`, 0..1, default `0.5` | keep `Amount` | same meaning as `Strength`: 0..1 of the largest blur | `Strength`, default `0.25` | A7: VL.Skia's Blur says `Amount`, and the decided DropShadow `Blur Amount` is built on it; the default differs because the scale differs (Max Radius vs texture size) | SREF-14 || A4: keep (DECISION-ROUND.md) |
| Blur / Max Radius (was `Max`) | `int`, default `32`, help: "Maximum blur radius in pixels at Amount 1" | `int`, default `32`, in DIP; help "Largest blur radius in DIP at Amount 1" | Qt's `blurMax` is measured in the item's coordinates, which are logical pixels, so the value is DIP today; `protocol.h:416` says "pixels" too. Reading (a), device pixels: would need a conversion by dpr and would make the radius depend on the screen. Reading (b), DIP: no conversion, matches E1. Recommended: (b), only the help text and `protocol.h:416` change | Glow `Max Radius` (float, default `1`, normalised) | `int`, because Qt's property is `int` and a change switches shaders; DIP instead of normalised by E1 | SREF-02 (API-13) | yes (Sebastian, 2026-11-19, E1 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Blur / Radius Multiplier (was `Multiplier`) | `float`, no explicit default (0), unitless | keep, explicit `[DefaultValue(0f)]` | factor on Max Radius, no unit | none | Qt MultiEffect feature | NODX-07 | |
| Blur / Padding | `RectangleF`, units, x = left, y = top, width = right, height = bottom, default `0, 0, 0, 0` | DIP | a length of the effect, independent of the renderer's space | none (TextureFX keeps the input size) | merged effects: see question 2 | SREF-01 || follows from A5 (DECISION-ROUND.md) |
| Blur / Auto Padding | `bool`, default `true` | keep | | none | Qt MultiEffect feature | SREF-14 | |

### 3.2 DropShadow

Counterpart: `DropShadow_TextureFX`: `Blur` (0.5, normalised LOD), `Offset` (`float` 0.05, UV distance), `Angle` (`float` 0.9, cycles), `Color` (0, 0, 0, 1), `Alpha` (1), `Extension` (0), `Control`, `Apply`. VL.Skia's DropShadow: `Offset`, `Amount`, `Color`, `Mode`.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| DropShadow / Offset | `Vector2`, units ("Shadow offset (x, y) in units"), converted with `ToQt` (`EffectDiff.cs:205-208`) | `Vector2` in DIP | a shadow distance in pixel space does not change with the renderer's space | `Offset` (distance in UV) + `Angle` (cycles) | `Vector2` as VL.Skia's DropShadow (A7) and Qt's `shadowHorizontalOffset`/`shadowVerticalOffset`; DIP instead of UV by E1; merged effect, see question 2 | SREF-01, SREF-14 || follows from A5 (DIP) and A4 (`Vector2`, DECISION-ROUND.md) |
| DropShadow / Offset default | `0.05, 0.05` units | `5, 5` DIP | same picture in the DIP spaces | `0.05` UV at `0.9` cycles | | SREF-01 || follows from A5 (DECISION-ROUND.md) |
| DropShadow / Blur Amount (was `Blur`) | `float` 0..1, default `1`, a fraction of Blur's Max Radius | keep | decided in the catalog, parallel to Blur `Amount` | `Blur`, default `0.5`, normalised LOD | the default differs because the scale differs | SREF-14 | |
| DropShadow / Scale | `float`, default `1`, ratio | keep | | none (`Extension` is an alpha gain) | Qt MultiEffect feature | SREF-15 | |
| DropShadow / Padding | `RectangleF`, units, default `0, 0, 0, 0` | DIP | as Blur / Padding | none | merged effect, see question 2 | SREF-01 || follows from A5 (DECISION-ROUND.md) |
| DropShadow / pin order | `Color, Offset, Blur Amount, Scale, Auto Padding, Padding` | keep | | `Blur, Offset, Angle, Color, Alpha, Extension` | VL.Skia's DropShadow orders `Offset, Amount, Color`; VL.Qt puts the colour first as on its elements. No order follows from E1 | SREF-14 | |

### 3.3 ColorAdjust

Counterpart: `HSCB_TextureFX`: `Hue` (0, cycles), `Hue Cycles` (1), `Saturation` (1 = unchanged, multiplier), `Saturation Balance` (1), `Contrast` (0 = unchanged, exponent of 2), `Brightness` (0 = unchanged, exponent of 2), `Control`, `Apply`. `Levels_TextureFX` for black and white points.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| ColorAdjust (node name) | `ColorAdjust` | keep | it has no hue, so it is not HSCB | `HSCB` | different set of pins; Qt MultiEffect's word | SREF-14 | |
| ColorAdjust / Brightness, Contrast | `float`, -1..1, 0 = unchanged, no explicit default | keep, explicit `0f` | same names and the same neutral value as HSCB | `Brightness`, `Contrast`, 0 = unchanged | the scale is Qt's (-1..1, not an exponent); a shader need not compute like TextureFX | SREF-15, NODX-07 | |
| ColorAdjust / Saturation | `float`, -1..1, 0 = unchanged, -1 = grey | 0..2, 1 = unchanged, 0 = grey (the node sends `value - 1` to Qt) | a user who knows HSCB reads 1 as "unchanged" and 0 as "grey"; the mapping is one subtraction | `Saturation`, 1 = unchanged, multiplier | alternative: keep Qt's -1..1 and say it in the pin help | SREF-04 | yes (Sebastian, 2026-11-19, E6 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| ColorAdjust / Saturation default | `-1` (grey, "so the effect is visible") | `0` (grey in the new scale) | same picture; AGENTS.md wants a visible default | `1` (unchanged) | visible default per AGENTS.md | SREF-04 | yes (Sebastian, 2026-11-19, E7 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |

### 3.4 Colorize

Counterpart: none. Closest: `TriColor_TextureFX` (tag `colorize`: `Color A`, `Color B`, `Color C`, `Smooth`), `MapColor`, `Grayscale`.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| Colorize / Color, Amount | `Color4` default `1, 0, 0, 1`; `float` 0..1 default `1` | keep | | none | Qt MultiEffect `colorization`/`colorizationColor`; no unit involved | SREF-14 | |

### 3.5 Mask

Counterpart: `SetAlpha_TextureFX` (category `Utils`, tag `mask`: `Input`, `Alpha Mask`, `Alpha Channel`, `Opacity`); `BlendOperator.Mask` of Blend; the keying filters (`LumaKey`, `ChannelKeying`) for thresholds.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| Mask / Mask | `Element?` | keep | | `Alpha Mask` (texture) | the mask is an element in the parent's coordinates, not a texture | SREF-14 | |
| Mask / Threshold Min, Spread At Min, Threshold Max, Spread At Max | `float` 0..1 (alpha), defaults 0, 0, 1, 0 | keep, explicit defaults | alpha values, no length | none with these names | Qt MultiEffect names | SREF-15, NODX-07 | |
| Mask / Inverted | `bool` | keep | | none | | SREF-14 | |

### 3.6 DirectionalBlur

Counterpart: `DirectionalBlur [Stride.Textures.Filter]`, patched over `DirectionalBlur_Pass0/1_Internal_TextureFX`: `Strength` (0.5), `Direction` (`Vector2` `0.25, 0`, UV, direction and length in one), `Sample Center` (`0.5, 0.5`), `Aspect` (`true`), `Control`, `Apply`.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| DirectionalBlur / Angle | `float`, degrees, no explicit default (0) | cycles, explicit `0f` | | (inside `Direction`) | | API-06 | follows from C10 (decided in the catalog) |
| DirectionalBlur / Length | `float`, units, default `0.1`, sent as `Uniform.Length("blurLength")` (units → DIP) | `float` in DIP | a shader length in pixel space; the shader works in DIP already (`directional_blur.frag`) | `Strength` × `Direction` (UV) | polar form (Angle in cycles, Length in DIP) instead of one UV vector: the vector would mix a direction with a normalised length | SREF-01, SREF-14 | follows from E1 (unit) |
| DirectionalBlur / Length default | `0.1` units | `10` DIP | same picture in the DIP spaces; the template `ExampleDirectionalBlur.frag` has `@default 10` already | `Strength 0.5`, `Direction 0.25, 0` |  | SREF-01 | yes (Sebastian, 2026-11-19, E8 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |

### 3.7 RadialBlur

Counterpart: none (TextureFX has no radial or rotational blur; `CartesianToPolar` and `Kaleidoscope` take a centre in UV).

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| RadialBlur / Center | `Vector2`, 0..1 of the element area, default `0.5, 0.5` | keep | a position inside the element, not a length; E1 is about lengths | `Sample Center` of DirectionalBlur, centres of `CartesianToPolar`/`Kaleidoscope`: UV 0..1 | normalised like TextureFX positions | SREF-15 | |
| RadialBlur / Angle | `float`, degrees, default `8` | cycles, default `0.05` | | none | | API-06 | follows from C10; default decided in the catalog |
| RadialBlur / Padding | `float`, units, default `0.3` | `float` in DIP | a length of the effect area | none | | SREF-01 | follows from E1 (unit) |
| RadialBlur / Padding default | `0.3` units | `30` DIP | same picture in the DIP spaces | none |  | SREF-01 | yes (Sebastian, 2026-11-19, E9 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |

### 3.8 ZoomBlur

Counterpart: none.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| ZoomBlur / Center | `Vector2`, 0..1 of the element area, default `0.5, 0.5` | keep | as RadialBlur / Center | none | normalised like TextureFX positions | SREF-15 | |
| ZoomBlur / Length | `float`, units, default `0.4`, sent as `Uniform.Length("blurLength")` | `float` in DIP | the shader works in DIP already (`zoom_blur.frag`) | none | | SREF-01 | follows from E1 (unit) |
| ZoomBlur / Length default | `0.4` units | `40` DIP | same picture; the template has `@default 40` | none |  | SREF-01 | yes (Sebastian, 2026-11-19, E10 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |

### 3.9 Glow

Counterpart: none of the same meaning. TextureFX's `Glow` (patched over `GlowPre/Main/Mix_Internal_TextureFX`: `Amount`, `Shape`, `Highlight Boost`, `Exposure`, `Saturation`, `Auto Exposure Factor`, `Max Radius` 1, `Pre Blur Width` 8 pixels) and `BlurGlow` (`Shape`, `Brightness` 2, `Radius` 0.5 normalised, `Alpha`) are blooms of the bright parts; VL.Qt's Glow is an outer glow of the alpha (like Qt5Compat Glow).

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| Glow / Radius | `float`, units ("in units of the renderer's space"), default `0.2`, sent as the pyramid strength `ShaderLink.Strength` (converted with `ToQt`) | `float` in DIP | the node summary already says "up to several hundred DIP" | BlurGlow `Radius` (normalised) | DIP instead of normalised by E1 | SREF-01 | follows from E1 (unit) |
| Glow / Radius default | `0.2` units | `20` DIP | same picture in the DIP spaces | `0.5` (BlurGlow) |  | SREF-01 | yes (Sebastian, 2026-11-19, E11 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Glow / Color, Spread, Intensity, Additive | `Color4` `0.2, 0.7, 1, 1`; `float` 0..1 (no explicit default); `float` default `1`; `bool` | keep, explicit defaults | ratios and a colour, no length | `Brightness`, `Shape`, `Amount` (other effect) | a different effect; names from Qt5Compat Glow and CSS | SREF-14, NODX-07 | |

### 3.10 InnerShadow

Counterpart: none.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| InnerShadow / Radius | `float`, units, default `0.1` | `float` in DIP | as Glow / Radius | none | | SREF-01 | follows from E1 (unit) |
| InnerShadow / Radius default | `0.1` units | `10` DIP | same picture in the DIP spaces | none |  | SREF-01 | yes (Sebastian, 2026-11-19, E12 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| InnerShadow / Offset | `Vector2`, units, sent as `Uniform.LengthVector2("offsetDip")` | `Vector2` in DIP | a shader length | DropShadow `Offset` + `Angle` (UV, cycles) | `Vector2` as DropShadow in VL.Qt | SREF-01 | follows from E1 (unit) |
| InnerShadow / Offset default | `0.05, 0.05` units | `5, 5` DIP | same picture in the DIP spaces | none |  | SREF-01 | yes (Sebastian, 2026-11-19, E13 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| InnerShadow / Color, Spread, Intensity | `Color4` `0, 0, 0, 0.6`; `float` (no explicit default); `float` `1` | keep, explicit defaults | | none | | SREF-14, NODX-07 | |

### 3.11 Displace

Counterpart: `Displace_TextureFX`: `Input`, `Input 2` (the map), `Sampler`, `Sampler 2`, `Offset` (`Vector2` `0.5, 0.5`, the neutral map value), `Offset Scale` (`Vector2` `0.1, 0.1`, UV), `Control`, `Apply`.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| Displace / Map, Map Sampler | `Element?`, `Sampler?` (optional) | keep | | `Input 2`, `Sampler 2` | a named element pin says what it is; TextureFX numbers its textures because they come from `Texturing` slots | SREF-14 | |
| Displace / Displacement | `float`, units, default `0.1`, sent as `Uniform.Length("displacement")` | `float` in DIP | the shader works in DIP already (`displace.frag`) | `Offset Scale` (`Vector2`, UV) | one value for both axes as Qt5Compat Displace; DIP instead of UV by E1 | SREF-01, SREF-14 | follows from E1 (unit) |
| Displace / Displacement default | `0.1` units | `10` DIP | same picture; the template has `@default 10` | `0.1, 0.1` UV |  | SREF-01 | yes (Sebastian, 2026-11-19, E14 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| Displace / neutral map value | fixed 0.5, map alpha scales the shift | keep | | `Offset` pin, default `0.5, 0.5` | Qt5Compat convention; a pin for it has no known use in VL.Qt | SREF-14 | |

### 3.12 Blend

Counterpart: `Blend [Stride.Textures.Mixer]`, patched in `VL.Stride.TextureFX.vl`: `Input`, `Sampler`, `Input 2`, `Sampler 2`, `Blend Operation` (`BlendOperator`, default `Average`), `Fader` (0.5), `Apply`. VL.Skia: `SetBlendMode` with pin `Blend Mode`.

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| Blend / Layer, Layer Sampler | `Element?`, `Sampler?` (optional) | keep | catalog: `Layer` is a named exception (glossary 1.2) | `Input 2`, `Sampler 2` | as Displace / Map | SREF-14 | |
| Blend / Mode | `Mode`, `BlendMode`, no explicit default (`Normal`) | `Blend Mode`, explicit `Normal` | VL.Skia's pin name (A7); `Mode` alone says less next to `Opacity` | `Blend Operation` | A7: VL.Skia has the concept | SREF-05, NODX-07 || follows from A4 (DECISION-ROUND.md) |
| Blend / BlendMode values | `Normal, Multiply, Screen, Overlay, Add, Subtract, Difference, Darken, Lighten, SoftLight, HardLight, ColorDodge, ColorBurn` | keep | W3C Compositing and Blending names, shared with Skia | `BlendOperator`: 31 values, `Average` … `Over` (no `Normal`) | Qt draws W3C's separable modes; TextureFX's extra operators (Hue, Mask, In, Out …) are other operations | SREF-14 | |
| Blend / Opacity | `float` 0..1, default `1` | keep | strength of the layer | `Fader`, default `0.5` | a layer opacity, not a crossfade between two inputs | SREF-14 | |

### 3.13 Common to the twelve effect nodes

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| (every effect node) / Apply | missing; a node passes the chain on only when Layer/Map is unconnected (Blend, Displace) | `Apply`, `bool`, default `true`, last input pin, optional; off passes the chain on unchanged | switching an effect off without relinking is the most common patch need; TextureFX has it on every filter | `Apply` (filters, mixers), `Enabled` (sources) | name `Apply` as TextureFX, not `Enabled` (C2 gives `Enabled` to elements) | SREF-06 | yes (Sebastian, 2026-11-19, E16 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| (every effect node) / Control | missing | not proposed | | `Control` (Float4 per-channel blend of input and result) | no use case known in VL.Qt; Blend `Opacity` covers the common one | SREF-14 | |
| (every effect node) / Output | `Output`, `Effect` | keep | same name | `Output`, `Texture` | | — | |
| (every effect node) / category | `Qt.Effects` | keep | | `Stride.Textures.Filter`, `.Mixer`, `.Source` | one category, decided | — | follows from C11 |

### 3.14 Shader files (user shaders and built-in shaders)

| Node / pin | Current | Proposed | Reason | TextureFX counterpart | Reason for a deviation | Finding | Decision |
|---|---|---|---|---|---|---|---|
| folder | `qtshaders/` next to the document, recursive | keep | | `shaders/`, recursive | GLSL 440 for Qt, not SDSL; a folder of its own says which library reads it | SREF-16 | |
| file name and node kind | `Name_Variant.frag` (+ `.vert`) → `Name (Variant)`; filter if it declares `sampler2D source`, generator otherwise; `@chainonly` | keep | | `Name_Variant_TextureFX.sdsl`; kind by suffix (`_TextureFX`, `_ShaderFX`, `_DrawFX`, `_ComputeFX`) and `[TextureSource]` | one `.frag` gives an element node and a chain node; there is only one kind of shader file | SREF-16 | |
| annotation syntax | `// @tag value` in the file head and at a uniform | keep | | SDSL attributes `[Tag(…)]` | GLSL has no attributes | SREF-16 | |
| defaults | `// @default 0.5`; without it the type default (`Color` white, `mat4` identity) | keep | | `[Default(…)]` or the initializer | std140 block members cannot have initializers | SREF-16 | |
| `@min`, `@max` | limits of the IOBox only | keep | | none | VL.Qt extension, useful in the IOBox | SREF-16 | |
| `@optional` | missing: every uniform pin is visible | add `// @optional`: the pin is optional (hidden until shown) | many technical uniforms clutter the node | `[Optional]` |  | SREF-07 | yes (Sebastian, 2026-11-19, E17 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| `@tags`, `@remarks` | missing | add `// @tags a, b` (node browser search) and `// @remarks` (node help) in the file head | the node browser finds TextureFX nodes by tags (`HSCB`: "hue, saturation, contrast, brightness") | `[Tags]`, `[Remarks]` |  | SREF-08 | yes (Sebastian, 2026-11-19, E18 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| `@category` | absolute path; default `Qt.Elements.Shaders` (element node) and `Qt.Effects` (chain node) | absolute as today; a path that does not start with `Qt.` is appended to the default category of the node kind (`@category Examples` → `Qt.Elements.Shaders.Examples` / `Qt.Effects.Examples`) | one file makes two nodes in two categories; a relative path keeps them together | `[Category("Filter")]` → `Stride.Textures.Filter`; `:` prefix absolute |  | SREF-09 | yes (Sebastian, 2026-11-19, E19 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| enum pins | none (`int` pin) | not now; `int` plus `@summary` | needs a type lookup and a version rule | `[EnumType("Type, Assembly")]` | small gain for the effort | SREF-13 | yes (Sebastian, 2026-11-19, E20 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| pin names from uniform names | split at lower→upper case and at `_`, every word capitalised (`pixel_size` → `Pixel Size`); no split before a digit (`pass1` → `Pass1`) | also split before a digit (`pass1` → `Pass 1`) | same pin name as TextureFX for the same identifier | `GetPinName`: split at lower→upper or digit, first letter capitalised, `_` kept | the `_` split stays: GLSL code uses snake case more often than SDSL | SREF-12 | yes (Sebastian, 2026-11-19, E21 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| main input | `sampler2D source` → pin `Input` | keep | same pin name | `Texture0` → `Input` | Qt's ShaderEffect name `source`, one combined sampler | SREF-16 | |
| sampler of the main input | `Input Sampler` (optional) on the element node; none on the chain node | keep | | `Sampler` (optional, after `Input`) | VL.Qt names a sampler after its texture (`Map Sampler`, `Layer Sampler`), so one rule for all | SREF-14 | |
| further textures | `sampler2D map` → `Map` + `Map Sampler` | keep | | `Input 2` + `Sampler 2` | named pins say what they are | SREF-14 | |
| texture coordinate | `qt_TexCoord0`; `qt_Matrix`, `qt_Opacity` declared, set by Qt | keep | | `streams.TexCoord` | Qt's names | SREF-16 | |
| texel size and output size | `texelSize` (1 / pixel size of the sampled texture) and `dpr` only in a chain pass (`shadereffectitem.cpp:776-786`); the element node gets neither, nor its size | `texelSize`, `dpr` and `viewSize` (drawn size in DIP) set on the element node too, if declared | an element shader cannot work in DIP without them (E1); TextureFX gives every shader both | `Texture0TexelSize`, `ViewSize` (pixels) | `viewSize` in DIP plus `dpr`, since pixels depend on the screen | SREF-10 | yes (Sebastian, 2026-11-19, E22 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| units of user uniforms | sent as they are (`Uniform.IsLength` false); the templates for chain nodes use DIP (`blurLength 10`, `displacement 10`) | keep, and say in the docs: lengths in DIP, angles in cycles | E1 is about what an author should write; VL.Qt does not convert user values | normalised (UV) by convention | | SREF-01 | follows from E1 |
| RGBSplit.frag (template) / amount | "fraction of the element size", default `0.02`, with `@padding 40` (DIP) | a distance in DIP (default `10`), with `@padding distance` | the template teaches the convention; normalised and DIP padding do not fit | `ShiftRGB_TextureFX` (normalised) | needs SREF-10 (`texelSize`/`dpr` on the element node) | SREF-11 | yes (Sebastian, 2026-11-19, E23 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| `@padding` | documented and parsed as DIP, sent as units (100 × too large) | DIP | | none (TextureFX keeps the input size) | | SHD-01 | follows from E1 |
| angle uniforms | `angle` in degrees in `directional_blur.frag`, `radial_blur.frag`, `ExampleDirectionalBlur.frag`, `ExampleRadialBlur.frag` (`@default 8`, `@padding angle 3.75`) | cycles in the shaders and the templates; `ExampleRadialBlur`: `@default 0.05`, `@padding angle 1350` (3.75 × 360) | the template pin `Angle` must mean what the node pin `Angle` means | DropShadow `Angle`, `r2dlocal` in `GlowBase`: cycles | | SREF-03 | follows from C10 |
| template pin names vs node pin names | `ExampleDirectionalBlur` `Blur Length`, `ExampleBlend` `Layer Opacity` against the nodes' `Length`, `Opacity` | keep, as documented | a uniform cannot be named `length` (GLSL builtin, `docs/effects-and-shaders.md:164`); `opacity` would be close to `qt_Opacity` | — | | SREF-16 | |
| reserved uniform names | `qt_Matrix`, `qt_Opacity`, `source`; in a chain also `texelSize`, `dpr`, `weight`, `elementRect`, `passIndex`, `base`, `glow`, `shadow` | as NFX-14 (one list, docs match) |  | `Texture0`, `Sampler0`, `ViewSize`, `Texture0TexelSize` |  | NFX-14 | error at the node, one list (with D8) (Sebastian, 2026-11-19, E24 of [DECISION-ROUND.md](../DECISION-ROUND.md)) |
| alpha | premultiplied in and out; colour uniforms premultiplied by QtNative before they reach the shader | keep, say both in the templates (already) | | straight colours; `Premultiply_TextureFX` is a node of its own | Qt's scene graph is premultiplied throughout | SREF-16 | |
| colour space | 8-bit, no conversion | keep | | `[DontConvertToLinearOnRead]`, `[DontConvertToSRgbOnWrite]` (linear pipeline) | Qt Quick draws in the window's encoding | SREF-16 | |
| geometry pins of the Shader element | `Size` (`Vector2`, units, default `1, 1`), `Transform`, `Anchor` | keep units | they place an element like every other element; they are no shader parameter | `Output Size` (`Int2`, pixels) | | SREF-15 | |

## 4 Notes for the catalog round

- **E1 against TextureFX itself.** TextureFX takes lengths normalised (Blur `Strength`, DropShadow `Offset` in UV, Displace `Offset Scale`, DirectionalBlur `Direction`, BlurGlow `Radius`); only Glow `Pre Blur Width` is in pixels. "Follow TextureFX" and "pixel space (DIP)" therefore disagree on every unit. This report takes the decision's own words: DIP wins on units, TextureFX on names and file conventions. Every unit row records the deviation.
- **A7 against E1 on names.** Where VL.Skia has the node, the report keeps Skia's names (Blur `Amount`, DropShadow `Offset` as `Vector2`, Blend `Blend Mode`), not TextureFX's (`Strength`, `Offset` + `Angle`, `Blend Operation`). Question 1.
- **DropShadow `Blur Amount`** (decided, "parallel to `Amount` on Blur") stays consistent with SREF-14. If question 1 goes to TextureFX, Blur `Amount` → `Strength` and DropShadow `Blur Amount` → `Blur Strength`.
- **Blur `Max Radius`** (open note of the decided row): DIP, no conversion, only the help text and `protocol.h:416` change (SREF-02). The name also matches TextureFX Glow's `Max Radius`.
- **RadialBlur `Angle` default `0.05` cycles** (decided): consistent. The built-in shader and the template must follow (SREF-03), and the template's `@padding angle 3.75` becomes `1350`, or the padding rule is rewritten in cycles.
- **API-13** says "Glow and InnerShadow `Radius` stay (units of the space)"; **NODX-08** proposes "DIP" → "units" in the Glow summary. E1 reverses both: Radius in DIP, and the Glow summary keeps "DIP". The same applies to `docs/effects-and-shaders.md:33` ("Lengths are in units of the renderer's space, angles in degrees") → DIP and cycles.
- **NODX-07** (explicit defaults): the default rows of this report give the values in the new units (`10`, `20`, `30`, `40` DIP, `5, 5` DIP).
- **Help patches:** an IOBox at a length pin holds a value in units (`0.1`); after SREF-01 it must hold `10`. The rename script of P19 changes names and categories only, so these value edits are direct `.vl` edits in the issue of SREF-01 (P20), one patch after the other.
- **`Uniform.Length` / `IsLength`** and the `ToQt` conversion of `ShaderLink.Padding` and `Strength` (`EffectDiff.cs:257-279`) lose their reason once every shader length is DIP. SHD-01's proposal of an `EffectPadding` with a unit flag shrinks to "padding is always DIP".
- **`Normalized` space:** after SREF-01 a length of 10 DIP is the same on every space; before it, `0.1` in `Normalized` meant 5 % of the scene height. Patches in `Normalized` that relied on effects scaling with the window change; none of the six help patches with an effect node (`HowTo Add a Glow`, `Blend Elements`, `Blur An Element`, `Chain Effects`, `Displace an Element`, `Mask an Element`) contains `Normalized` (grep).

## 5 Questions for Sebastian

1. **A7 or E1 for the pin names of the built-in effect nodes?** (a) A7 first: where VL.Skia has the node or pin, its name (Blur `Amount`, DropShadow `Offset` `Vector2`, `Blend Mode`); TextureFX for the shader files. (b) E1 first: TextureFX's names (`Strength`, `Offset` + `Angle`, `Blend Operation`). Recommended: (a); it keeps the decided `Blur Amount` and matches what a Skia user patches next to it. Decision: (a) (Sebastian, 2026-11-19, A4 in [DECISION-ROUND.md](../DECISION-ROUND.md)).
2. **Do the merged effects count as shaders under E1?** DropShadow `Offset` and the `Padding` of Blur and DropShadow are values of Qt's MultiEffect, not of a VL.Qt shader. (a) Yes: DIP like the shader effects, one unit for every length in `Qt.Effects`. (b) No: they stay in units like the element's own geometry. Recommended: (a); otherwise one chain mixes units, e.g. DropShadow `Offset` in units next to InnerShadow `Offset` in DIP. Decision: (a) (Sebastian, 2026-11-19, A5 in [DECISION-ROUND.md](../DECISION-ROUND.md)).
