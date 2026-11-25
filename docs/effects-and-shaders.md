# Effects and shaders

## Effects

Every element has an `Effect` pin. Chain the nodes of `Qt.Effects` to it, like the Interaction nodes on the `Interaction` pin:

| Node | Effect |
|---|---|
| `Blur` | blur the element |
| `DropShadow` | shadow behind the element |
| `ColorAdjust` | brightness, contrast, saturation |
| `Colorize` | tint the element |
| `Mask` | use another element as alpha mask, `Inverted` flips it |
| `Glow` | soft glow around the element (behind it, or `Additive` on top), `Radius` can be animated up to several hundred DIP |
| `InnerShadow` | shadow inside the element, like CSS `inset` |
| `DirectionalBlur` | motion blur along a line (`Angle`, `Length`) |
| `ZoomBlur` | streaks along rays from `Center` |
| `RadialBlur` | arcs around `Center` (`Angle` in degrees) |
| `Displace` | shifts the pixels by a displacement map, any element as `Map` |
| `Blend` | blends any element as `Layer` over the base with a `Mode` (Multiply, Screen, Overlay, Add, Subtract, Difference, Darken, Lighten, Soft Light, Hard Light, Color Dodge, Color Burn) |

<img src="images/effects-basic.png" width="820" alt="Blur, DropShadow, ColorAdjust, Colorize and Mask on the same element">

The chain runs in data flow order, like TextureFX in Stride: the top node acts first, the node at the `Effect` pin last. `Blur`, `DropShadow`, `ColorAdjust`, `Colorize` and `Mask` are the **merged effects**: neighbours in the chain merge into one offscreen layer (Qt's MultiEffect). Inside such a run Qt applies them in its own fixed order, so their order among each other does not matter, and each kind counts once: for a duplicate the node nearest to the `Effect` pin wins and the element shows a warning. A **shader effect** (the other nodes of `Qt.Effects`, and the shader nodes of [Shaders in the effect chain](#shaders-in-the-effect-chain)) ends the run and costs a layer of its own. Size, transform and layout stay with the element.

### Mask and padding

- **Mask:** any element works as mask, e.g. a Text. It lives in the coordinates of the parent, like a sibling: the mask covers exactly the place where it would be drawn on its own.
- **Optional pins:** Blur `Max`, `Multiplier`, `Auto Padding`, `Padding`; DropShadow `Scale`, `Auto Padding`, `Padding`. With Auto Padding on (default) the layer grows automatically by what blur and shadow need. Qt knows one effect area per layer: if Auto Padding is off on any Blur or DropShadow of a run, it is off for the whole layer of that run, and the area is the largest Padding per side of those nodes, so it suffices for all of them.

### Shader effects

`Glow`, `InnerShadow`, `DirectionalBlur`, `ZoomBlur`, `RadialBlur`, `Displace` and `Blend` are shader effects with their own passes; the pictures show the first six applied to the text `VL.Qt`. Lengths are in units of the renderer's space, angles in degrees. They end a run of merged effects and cost a layer of their own; Glow and InnerShadow use a blur pyramid, so their cost hardly depends on the radius.

<table>
<tr><td align="center"><img src="images/effect-glow.png" width="260" alt="The text VL.Qt with Glow"><br>Glow</td><td align="center"><img src="images/effect-directional-blur.png" width="260" alt="The text VL.Qt with Directional Blur"><br>Directional Blur</td><td align="center"><img src="images/effect-zoom-blur.png" width="260" alt="The text VL.Qt with Zoom Blur"><br>Zoom Blur</td></tr>
<tr><td align="center"><img src="images/effect-radial-blur.png" width="260" alt="The text VL.Qt with Radial Blur"><br>Radial Blur</td><td align="center"><img src="images/effect-innershadow.png" width="260" alt="The text VL.Qt with InnerShadow"><br>InnerShadow</td><td align="center"><img src="images/effect-displace.png" width="260" alt="The text VL.Qt with Displace"><br>Displace</td></tr>
</table>

### Shaders in the effect chain

Every filter shader from a `qtshaders/` folder (one with a `sampler2D source`, see [Shader nodes from files](#shader-nodes-from-files)) also appears as a node in `Qt.Effects`, with the same name and pins. Put it anywhere in the chain; here the order does matter, because each shader works on what comes out of the node before it:

<img src="images/effect-chain.png" width="820" alt="The text VL.Qt: Blur, RGBSplit, DropShadow on the left, DropShadow, RGBSplit, Blur on the right; the shadow is split and blurred in the second picture">

A shader that draws beyond the element (a blur, a shifted copy) declares its room with `// @padding` in the file head, see [Annotations](#annotations); without it the shader stays inside the element. The help patch `HowTo Chain Effects` builds the two chains above. Shaders that only make sense in the chain, and the templates of the built-in effects, are covered in [Own effects from templates](#own-effects-from-templates).

Help patches: `HowTo Chain Effects`, `HowTo Mask an Element`, `HowTo Add a Glow`, `HowTo Blur An Element`, `HowTo Displace an Element`, `HowTo Blend Elements`, `HowTo Draw Trails`. See [Help patches](help-patches.md).

## Shaders

Own fragment shaders (optionally with a vertex shader) are written in Qt's GLSL (version 440, the dialect Qt's `qsb` tool expects). There are two ways to use them; the first one is the normal way:

**Shader nodes from files (normal way):** every `.frag` file in a `qtshaders/` folder becomes a node with one pin per uniform (see [Shader nodes from files](#shader-nodes-from-files)).

**ShaderEffect node (runtime variant, `Qt.Elements.Shaders`):** for shader code that is built, generated or loaded at runtime, which a file cannot cover. The node is marked advanced, so it only shows in the node browser with the advanced filter. The shader code goes into the `Fragment Code` string pin, or `Fragment Shader` points to a pre-baked `.qsb` file. VL.Qt bakes it in a worker; while a new version is baking or has errors, the last working one keeps drawing. Compile errors show at the `Error` output with their line number. Uniforms come from the nodes in `Qt.Elements.Shaders.Uniform` (also advanced) and are matched by name, an element or Group on `Input` becomes the `source` texture.

### Sampler node

`Qt.Elements.Shaders` has a `Sampler` node that sets how a shader reads a texture. `Filter` (Linear, Point), `Mip Filter` (None, Point, Linear; also renders the sampled element's layer with mipmaps), `Address U` / `Address V` (Clamp, Wrap, Mirror). Connect it to `Input Sampler` of a shader node or a ShaderEffect, the `Sampler` pin of the `Texture` / `Image` uniform nodes, the sampler pin next to an image pin of a shader node, `Layer Sampler` of Blend or `Map Sampler` of Displace. Without a Sampler an element's layer is read Nearest, Clamp, without mipmaps. A float layer format and anisotropic filtering are not available (limits of Qt). Help patch: `HowTo Set a Sampler`.

### Shader nodes from files

Every `.frag` file in a folder named `qtshaders` next to the patch becomes a node, with one pin per uniform (the screenshot shows the help patch). Middle click on the node (or right click > Definition > Open) opens the `.frag`, and a `.vert` with the same name, in your default editor. Edit, save, and the node updates while vvvv runs.

- `RGBSplit.frag` gives the node `RGBSplit`, a `.vert` with the same name belongs to it. Subfolders count too.
- A `sampler2D source` becomes the `Input` pin: the shader is a filter. Without it, it is a generator without Input pin. Further `sampler2D` become image pins.
- Uniforms become pins in the order of the file, the name split into words (`pixelSize` → `Pixel Size`). Types: `float`, `vec2`, `vec3`, `vec4`, `int`, `bool`, `mat4`.
- Annotations as `//` comments, see [Annotations](#annotations): file head tags (`@category`, `@summary`, `@padding`, `@passes`, `@chainonly`) and uniform tags (`@default`, `@min`, `@max`, `@color`, `@summary`).
- Pins with the same name keep their links and values when the file changes. Compile errors, unknown annotations and unsupported uniform types (`uint`, `ivec`, `mat3`, arrays) show at the node.
- A filter shader is also a node of the effect chain, see [Shaders in the effect chain](#shaders-in-the-effect-chain).

<img src="images/shader-nodes-from-files.png" width="820" alt="Help patch 'HowTo Create Shader Nodes from Files': two shader nodes generated from .frag files, the Qt window and the shader source in VS Code">

#### Example: a filter with three pins

```glsl
#version 440
// @category Qt.Elements.Shaders.Examples
// @summary Shifts the red and blue channel of the Input in opposite directions.

layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;

layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;

    // @summary Distance of the shifted copies, as a fraction of the element size.
    // @default 0.02
    // @min 0
    // @max 0.2
    float amount;

    // @default 1, 0
    vec2 direction;

    vec4 tint; // @color
};

layout(binding = 1) uniform sampler2D source;

void main()
{
    vec2 offset = direction * amount;
    vec4 a = texture(source, qt_TexCoord0 + offset);
    vec4 b = texture(source, qt_TexCoord0);
    vec4 c = texture(source, qt_TexCoord0 - offset);
    fragColor = vec4(a.r, b.g, c.b, max(a.a, max(b.a, c.a))) * tint * qt_Opacity;
}
```

The full, commented templates are `help/Qt/qtshaders/RGBSplit.frag` (filter) and `Stripes.frag` (generator).

### Annotations

Annotations are `//` comments. Unknown tags show at the node.

**File head** (the comments before the first code line):

| Tag | Meaning |
|---|---|
| `@category <path>` | node category of the element node; with `@chainonly` the category of the effect chain node (default `Qt.Effects`) |
| `@summary <text>` | node help; goes on in the following comment lines until the next blank line |
| `@padding <left, top, right, bottom>` | room around the element in DIP that the shader may draw into as an effect chain node; one number for all four sides, or four numbers. Without it the shader stays inside the element |
| `@padding <uniform> [factor]` | all four sides = `abs(value of that float uniform pin) * factor` (factor default 1), recomputed when the pin value changes. A uniform that does not exist or is not a `float` is an error at the node |
| `@passes 1\|2` | `2`: the effect chain node runs the shader twice, the second pass on the result of the first (default 1, other numbers are an error at the node) |
| `@chainonly` | the file makes only the effect chain node, no element node |

**Uniform** (directly above it or at the end of its line): `@default`, `@min`, `@max` (limits of the pad only), `@color` (a `vec4` becomes a color), `@summary`.

**Names set by VL.Qt** (no pin, an own entry of that name is an error): `qt_Matrix`, `qt_Opacity`, and in the effect chain `source`, `texelSize` (1 / pixel size of the sampled texture), `dpr`, `weight`, `elementRect` and `passIndex`. `base`, `glow` and `shadow` are reserved there too: a shader file with a uniform of that name gets no pin and an error at its effect chain node. The same goes for an entry of such a name in the Uniforms of a ShaderEffect (`qt_Matrix`, `qt_Opacity`, `source`): an error at the node, the entry is ignored. The list lives once, in `effects/shader_item.h` (`kItemReservedUniforms`, `kChainAutomaticUniforms`, `kChainReservedUniforms`), and `ReservedUniforms` in C# mirrors it (a unit test compares them). `passIndex` is a `float` that VL.Qt writes into the uniform block if the shader declares it: 0 in a single pass and in pass 1, 1 in pass 2.

### Two-pass effects

A wide blur with a fixed number of samples (the built-in blurs have 64) shows the single samples as copies. With `@passes 2` the same shader runs twice: pass 2 blurs the result of pass 1 again, but only by `length / 64`, which fills the gaps exactly (64 x 64 effective samples). The shader tells the passes apart with `passIndex`:

```glsl
// @chainonly
// @passes 2
// @padding blurLength 0.52
...
float len = abs(blurLength) / (passIndex >= 0.5 ? 64.0 : 1.0);
```

Where pass 1 needs no more than 64 samples, pass 2 blurs by less than a pixel and changes nothing. `@padding` counts for the whole node (both passes together).

### Own effects from templates

The five built-in shader effects stay C# nodes (`Blend`, `Displace`, `DirectionalBlur`, `ZoomBlur`, `RadialBlur`). Their shader code is also in `help/Qt/qtshaders/` as annotated templates, chain-only files in the category `Qt.Effects.Examples`:

`ExampleBlend.frag`, `ExampleDisplace.frag`, `ExampleDirectionalBlur.frag`, `ExampleZoomBlur.frag`, `ExampleRadialBlur.frag`.

To make an effect of your own: copy a file into a `qtshaders/` folder next to your patch, rename it (the file name is the node name), change the code and the annotations. The templates carry the comments that explain `@passes`, `@padding` and the premultiplied alpha. They show up as nodes in `Qt.Effects` next to the built-in ones.

- The code of a template (without comments) is identical to the built-in shader; a unit test keeps them in sync, so change a copy, not the template.
- A tag cannot express everything the C# node does: `@padding` has one factor for all four sides, so the blur templates use the worst case (e.g. 0.52 times the length on every side) where the built-in node pads x and y separately by the angle. Such approximations are noted in the file.
- The pins of a template follow its uniform names (`Layer`, `Layer Opacity`, `Map`, ...); the C# nodes keep their own pin names.

### Notes

- The output is premultiplied alpha: multiply all four channels by `qt_Opacity`.
- The nodes appear once the `qtshaders` folder exists next to the saved patch.
- Do not name a uniform or sampler like a GLSL builtin (`length`, `distance`, `texture`, ...): the OpenGL translation (Linux) renames it and then never sets it. The node shows a warning on every backend.
- Two `.frag` files with the same name in one `qtshaders` tree give an error at both nodes. A `.vert` without a `.frag` makes no node.
- **Export:** the `qtshaders/` folder next to the exported patch is copied next to the exe automatically, and the exported app bakes the shaders at runtime like the editor does. Only that folder is copied: shader files that live next to other documents or in other packages have to be copied by hand.
- **Limitations:** exported apps bake shaders at runtime (no pre-baked `.qsb` shipped for `.frag` nodes), the editor keeps no pipeline cache, and uniform arrays are not supported.

Help patches: `HowTo Create Shader Nodes from Files`, `HowTo Write a ShaderEffect` (runtime variant), `HowTo Blur an Image with a Control Mask`, `HowTo Set a Sampler`. See [Help patches](help-patches.md).
