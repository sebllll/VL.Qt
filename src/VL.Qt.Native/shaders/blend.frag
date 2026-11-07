#version 440
// Blend (builtin:blend): blends the layer over the input with a blend mode (one pass). W3C Compositing and Blending Level 1,
// separable modes, on straight colors computed from the premultiplied samples; the result is premultiplied again.
// Co = (1 - ab) * Cs + (1 - as) * Cb + as * ab * B(cb, cs), ao = as + ab - as * ab (as = layer alpha * opacity, ab = base alpha).
// Where the layer is transparent the base stays unchanged. The layer is stretched over the ELEMENT (elementRect, set by the
// effect chain: the element's rectangle in UV of this pass, which may be bigger than the element after a Blur); outside the
// element the layer is transparent. elementRect 0 (not in a chain): the layer covers the whole item.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, mode 68 (int), opacity 72, elementRect 80 (vec4).
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    int mode;       // 0 Normal 1 Multiply 2 Screen 3 Overlay 4 Add 5 Subtract 6 Difference 7 Darken 8 Lighten
                    // 9 SoftLight 10 HardLight 11 ColorDodge 12 ColorBurn
    float opacity;  // strength of the layer, 0..1
    vec4 elementRect; // x, y, w, h of the element in UV of this pass (set by VL.Qt in a chain)
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D layerSource;

vec3 hardLight(vec3 cb, vec3 cs)
{
    return mix(2.0 * cb * cs, 1.0 - 2.0 * (1.0 - cb) * (1.0 - cs), step(0.5, cs));
}

vec3 softLight(vec3 cb, vec3 cs)
{
    vec3 d = mix(((16.0 * cb - 12.0) * cb + 4.0) * cb, sqrt(cb), step(0.25, cb));
    return mix(cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb), cb + (2.0 * cs - 1.0) * (d - cb), step(0.5, cs));
}

float colorDodge(float cb, float cs)
{
    if (cb == 0.0) return 0.0;
    if (cs >= 1.0) return 1.0;
    return min(1.0, cb / (1.0 - cs));
}

float colorBurn(float cb, float cs)
{
    if (cb >= 1.0) return 1.0;
    if (cs <= 0.0) return 0.0;
    return 1.0 - min(1.0, (1.0 - cb) / cs);
}

vec3 blendFunction(vec3 cb, vec3 cs)
{
    switch (mode) {
    case 1: return cb * cs;
    case 2: return cb + cs - cb * cs;
    case 3: return hardLight(cs, cb);
    case 4: return min(cb + cs, vec3(1.0));
    case 5: return max(cb - cs, vec3(0.0));
    case 6: return abs(cb - cs);
    case 7: return min(cb, cs);
    case 8: return max(cb, cs);
    case 9: return softLight(cb, cs);
    case 10: return hardLight(cb, cs);
    case 11: return vec3(colorDodge(cb.r, cs.r), colorDodge(cb.g, cs.g), colorDodge(cb.b, cs.b));
    case 12: return vec3(colorBurn(cb.r, cs.r), colorBurn(cb.g, cs.g), colorBurn(cb.b, cs.b));
    default: return cs;
    }
}

void main()
{
    vec4 base = texture(source, qt_TexCoord0);
    vec2 luv = elementRect.z > 0.0 && elementRect.w > 0.0 ? (qt_TexCoord0 - elementRect.xy) / elementRect.zw : qt_TexCoord0;
    bool inside = luv.x >= 0.0 && luv.x <= 1.0 && luv.y >= 0.0 && luv.y <= 1.0;
    vec4 layer = inside ? texture(layerSource, luv) * clamp(opacity, 0.0, 1.0) : vec4(0.0); // premultiplied, scaled as a whole
    float ab = base.a;
    float as = layer.a;
    vec3 cb = ab > 0.0 ? base.rgb / ab : vec3(0.0);
    vec3 cs = as > 0.0 ? layer.rgb / as : vec3(0.0);
    vec3 rgb = (1.0 - ab) * layer.rgb + (1.0 - as) * base.rgb + as * ab * blendFunction(cb, cs);
    float a = as + ab - as * ab;
    fragColor = vec4(rgb, a) * qt_Opacity;
}
