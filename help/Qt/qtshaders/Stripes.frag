#version 440
// @summary Moving diagonal stripes in two colors. A generator: it needs no Input, the Size pin sets its size.

// Example of a generator: there is no sampler2D "source", so the node has no Input pin.
// Without @category the node lands in Qt.Elements.Shaders.

layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;

layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;

    // @summary Shifts the stripes, one full cycle moves them by one stripe pair. Drive it with an LFO.
    // @default 0
    float phase;

    // @summary Thickness of the stripes, as a fraction of the element size.
    // @default 1
    float thickness;

    // @summary Number of stripe pairs across the element.
    // @default 8
    // @min 1
    // @max 64
    int count;

    // @summary First stripe color.
    // @color
    // @default 1, 0.35, 0.1, 1
    vec4 colorA;

    // @summary Second stripe color.
    // @color
    // @default 0.08, 0.1, 0.4, 1
    vec4 colorB;
};

void main()
{
    vec2 uv = qt_TexCoord0;

    // Diagonal coordinate along the stripes; fract() makes it repeat, step() cuts hard edges.
    float t = fract((uv.x - 0.5 * uv.y) * float(count) + phase);
    float s = step(0.5, t * thickness);

    // Both colors arrive premultiplied, so mixing them is already premultiplied.
    fragColor = mix(colorA, colorB, s) * qt_Opacity;
}
