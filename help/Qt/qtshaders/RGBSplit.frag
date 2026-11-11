#version 440
// @category Qt.Elements.Shaders.Examples
// @padding 40
// @summary Shifts the red and blue channel of the Input in opposite directions, like a misaligned print.
//   Optional scanlines darken every second pixel row.

// Example of a shader file that becomes a node (folder qtshaders/ next to the patch).
// The file name is the node name: RGBSplit.frag -> node "RGBSplit".
// The head above (comments before the first code line) holds the node's annotations: category and summary.
// A summary goes on in the following comment lines until the next blank line.
// The padding line is the space around the element (DIP) that the shader may draw into when it is used as a node of an
// effect chain (Qt.Effects, see HowTo Chain Effects): the shifted colors reach beyond the element.
// More file head tags for effect chain nodes (docs/effects-and-shaders.md, Annotations):
//   @padding <uniform> [factor]   padding from a float uniform instead of a fixed number
//   @passes 2                     run the shader twice, the pass number is in the uniform passIndex (0, 1)
//   @chainonly                    only the effect chain node, no element node (templates: Example*.frag)
//
// Every uniform below becomes an input pin ("amount" -> "Amount"), in the order of this file.
// Annotations are // comments directly above the uniform or at the end of its line:
//   default / min / max   value as in the patch, vectors separated by commas; min and max only limit the pad
//   color                 a vec4 becomes a color pin (white without default, components 0..1)
//   summary               help text of the pin
// A sampler2D named "source" becomes the Input pin. Without it the shader is a generator (see Stripes.frag).

layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;

// All uniforms live in this one block. qt_Matrix and qt_Opacity come first and get no pin.
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;

    // @summary Distance of the shifted copies, as a fraction of the element size.
    // @default 0.02
    // @min 0
    // @max 0.2
    float amount;

    // @summary Direction of the shift (x to the right, y down).
    // @default 1, 0
    vec2 direction;

    // @summary Multiplied with the result.
    vec4 tint; // @color

    // @summary Darken every second pixel row.
    bool scanlines; // @default false
};

layout(binding = 1) uniform sampler2D source;

void main()
{
    vec2 uv = qt_TexCoord0;
    vec2 offset = direction * amount;

    // Three lookups of the premultiplied input: red from one side, blue from the other.
    vec4 a = texture(source, uv + offset);
    vec4 b = texture(source, uv);
    vec4 c = texture(source, uv - offset);
    vec4 result = vec4(a.r, b.g, c.b, max(a.a, max(b.a, c.a)));

    if (scanlines)
        result *= 0.6 + 0.4 * step(1.0, mod(gl_FragCoord.y, 2.0));

    // Premultiplied output: tint (also premultiplied) and opacity scale all four channels.
    fragColor = result * tint * qt_Opacity;
}
