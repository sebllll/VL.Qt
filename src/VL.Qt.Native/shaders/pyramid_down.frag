#version 440
// Blur pyramid, one step down (builtin:pyramid_down): the item is half the size of its Input, 4 x 4 tent
// ([1 3 3 1] / 8 per axis at 2:1). Generic premultiplied RGBA (Glow, InnerShadow). Pass templates: protocol.h.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, texelSize 72.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    vec2 texelSize; // 1 / pixel size of the Input texture (DIP size * device pixel ratio)
};
layout(binding = 1) uniform sampler2D source;

// Tent [1 2 1] / 4 (offsets -1, 0, +1 texel) over the bilinear interpolation of t, by hand from the 4 x 4 texel
// centres around uv: the layers of the passes are not smooth (nearest), texel centres read the same with either filter.
vec4 row(sampler2D t, vec2 c, float dx, vec4 w)
{
    return w.x * texture(t, c - vec2(dx, 0.0)) + w.y * texture(t, c) + w.z * texture(t, c + vec2(dx, 0.0))
        + w.w * texture(t, c + vec2(2.0 * dx, 0.0));
}

vec4 tent(sampler2D t, vec2 uv, vec2 texel)
{
    vec2 p = uv / texel - 0.5;
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 w0 = (1.0 - f) * 0.25, w1 = f * 0.25 + (1.0 - f) * 0.5, w2 = f * 0.5 + (1.0 - f) * 0.25, w3 = f * 0.25;
    vec4 wx = vec4(w0.x, w1.x, w2.x, w3.x);
    vec2 c = (i + 0.5) * texel; // centre of texel i
    return w0.y * row(t, c - vec2(0.0, texel.y), texel.x, wx) + w1.y * row(t, c, texel.x, wx)
        + w2.y * row(t, c + vec2(0.0, texel.y), texel.x, wx) + w3.y * row(t, c + vec2(0.0, 2.0 * texel.y), texel.x, wx);
}

void main()
{
    fragColor = tent(source, qt_TexCoord0, texelSize) * qt_Opacity;
}
