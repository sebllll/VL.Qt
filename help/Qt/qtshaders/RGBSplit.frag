#version 440
// @category Qt.Elements.Shaders.Examples
// @padding amount
// @tags rgb, chromatic aberration, glitch, print
// @summary Shifts the red and blue channel of the Input in opposite directions, like a misaligned print.
//   Optional scanlines darken every second pixel row.

// Example of a shader file that becomes a node (folder qtshaders/ next to the patch).
// The file name is the node name: RGBSplit.frag -> node "RGBSplit".
// The head above (comments before the first code line) holds the node's annotations: category, summary and tags (search words of the node browser, separated by commas); @remarks adds longer help.
// A summary goes on in the following comment lines until the next blank line.
// The padding line is the space around the element (DIP) that the shader may draw into when it is used as a node of an
// effect chain (Qt.Effects, see HowTo Chain Effects): the shifted colors reach beyond the element. Here it follows the
// uniform "amount" (a distance in DIP), so the room grows with the shift.
// More file head tags for effect chain nodes (docs/effects-and-shaders.md, Annotations), written with a leading @:
//   padding <uniform> [factor]   padding from a float uniform instead of a fixed number (used here)
//   passes 2                     run the shader twice, the pass number is in the uniform passIndex (0, 1)
//   chainonly                    only the effect chain node, no element node (templates: Example*.frag)
//
// Every uniform below becomes an input pin ("amount" -> "Amount"), in the order of this file.
// Annotations are // comments directly above the uniform or at the end of its line:
//   default / min / max   value as in the patch, vectors separated by commas; min and max only limit the pad
//   color                 a vec4 becomes a color pin (white without default, components 0..1)
//   optional              the pin is hidden until shown
//   summary               help text of the pin
// A sampler2D named "source" becomes the Input pin. Without it the shader is a generator (see Stripes.frag).
//
// Lengths are in DIP, like the element's size, so the shift looks the same on every screen. VL.Qt sets three uniforms
// for you when a shader declares them (no pins): texelSize (1 / pixel size of source), dpr (device pixels per DIP)
// and viewSize (size of the element in DIP). DIP * dpr * texelSize is a length in texture coordinates (UV).

layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;

// All uniforms live in this one block. qt_Matrix and qt_Opacity come first and get no pin.
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;

    // Set by VL.Qt, no pin. 1 / pixel width, 1 / pixel height of source.
    vec2 texelSize;

    // Set by VL.Qt, no pin. Device pixels per DIP.
    float dpr;

    // @summary Distance of the shifted copies in DIP.
    // @default 10
    // @min 0
    // @max 200
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
    // DIP -> pixels -> UV
    vec2 offset = direction * amount * dpr * texelSize;

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
