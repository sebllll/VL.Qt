#version 440
// InnerShadow, first pass (builtin:innershadow_prefilter): inverted alpha of the Input, shifted by offset, times the
// shadow color, at half size like pyramid_down. Outside the Input counts as transparent (inverted alpha 1), so the
// edge of the element casts the shadow inwards. Pass templates: protocol.h.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, texelSize 72, offset 80, offsetDip 88, color 96, dpr 112.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    vec2 texelSize; // 1 / pixel size of the Input texture
    vec2 offset;    // shift of the shadow in Input UV units (offset in DIP / size of the effect area in DIP)
    vec2 offsetDip; // extra shift in DIP (the effect node uses this one; converted with dpr and texelSize)
    vec4 color;     // shadow color (premultiplied)
    float dpr;      // device pixels per DIP (set by the pass driver, 1 if unset)
};
layout(binding = 1) uniform sampler2D source;

// Inverted alpha of the Input at the texel centre c; 1 outside of it (the sampler clamps, so test the position).
float inv(vec2 c)
{
    bool inside = all(greaterThanEqual(c, vec2(0.0))) && all(lessThanEqual(c, vec2(1.0)));
    return inside ? 1.0 - texture(source, c).a : 1.0;
}

// Tent [1 2 1] / 4 (offsets -1, 0, +1 texel) over the bilinear interpolation of inv, by hand from the 4 x 4 texel
// centres around uv: the layers of the passes are not smooth (nearest), texel centres read the same with either filter.
float row(vec2 c, float dx, vec4 w)
{
    return w.x * inv(c - vec2(dx, 0.0)) + w.y * inv(c) + w.z * inv(c + vec2(dx, 0.0)) + w.w * inv(c + vec2(2.0 * dx, 0.0));
}

float tent(vec2 uv, vec2 texel)
{
    vec2 p = uv / texel - 0.5;
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 w0 = (1.0 - f) * 0.25, w1 = f * 0.25 + (1.0 - f) * 0.5, w2 = f * 0.5 + (1.0 - f) * 0.25, w3 = f * 0.25;
    vec4 wx = vec4(w0.x, w1.x, w2.x, w3.x);
    vec2 c = (i + 0.5) * texel; // centre of texel i
    return w0.y * row(c - vec2(0.0, texel.y), texel.x, wx) + w1.y * row(c, texel.x, wx)
        + w2.y * row(c + vec2(0.0, texel.y), texel.x, wx) + w3.y * row(c + vec2(0.0, 2.0 * texel.y), texel.x, wx);
}

void main()
{
    vec2 shift = offset + offsetDip * max(dpr, 1.0) * texelSize;
    fragColor = color * tent(qt_TexCoord0 - shift, texelSize) * qt_Opacity;
}
