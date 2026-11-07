#version 440
// Blur pyramid, one step up (builtin:pyramid_up): the item is twice the size of its Input (the step below), output
// mix(base, tent(source), weight). base = the pyramid_down step of this size, weight = how far the blur goes past this
// step (0 = base unchanged, 1 = only the coarser blur). Generic premultiplied RGBA. Pass templates: protocol.h.
// The output is dithered (8-bit layers would step in rings on flat gradients).
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, texelSize 72, weight 80.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    vec2 texelSize; // 1 / pixel size of the Input texture (the coarser step)
    float weight;   // 0..1
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D base;

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

// Interleaved gradient noise (Jimenez 2014): one value in [0, 1) per pixel, no visible pattern.
float ign(vec2 p)
{
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

// The layers are 8-bit: this adds noise of +-0.5 / 255 before the rounding, so a flat gradient does not step in rings
// but flickers between two neighbouring values (stochastic rounding, unbiased). Exact 8-bit input (source texels) stays
// as it is, empty stays empty (the noise never reaches a full step). Premultiplied: rgb never above a.
vec4 dither(vec4 c)
{
    float k = (ign(gl_FragCoord.xy) - 0.5) / 255.0;
    c = clamp(c + k, 0.0, 1.0);
    c.rgb = min(c.rgb, c.a);
    return c;
}

void main()
{
    vec4 c = mix(texture(base, qt_TexCoord0), tent(source, qt_TexCoord0, texelSize), weight);
    fragColor = dither(c * qt_Opacity);
}
