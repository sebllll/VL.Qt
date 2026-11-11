#version 440
// @category Qt.Effects.Examples
// @chainonly
// @padding 0
// @summary Blends a layer element over the Input with a blend mode (Multiply, Screen, Overlay, Add, ...).
//   Where the layer is transparent the Input stays unchanged.
//   Template: copy into a qtshaders/ folder next to your patch, rename, change.

// Template for an effect of your own: the same shader as the built-in Blend node, as a shader file.
// The file name is the node name (ExampleBlend.frag -> node "ExampleBlend" in Qt.Effects). Chain only: the file makes
// only that effect chain node, no element node. It is kept equal to the built-in one by a unit test, so change a copy.
// Padding 0: the result never reaches beyond the area it works on, so no extra space is needed around the element.
//
// qt_Matrix and qt_Opacity are Qt's, the shader only has to declare them.
//
// Qt textures hold PREMULTIPLIED colors (rgb already times alpha), and so must the output. The blend formulas
// work on straight colors, so main() divides by alpha first and puts the alpha back into the result.
// W3C Compositing and Blending Level 1, separable modes:
//   Co = (1 - ab) * Cs + (1 - as) * Cb + as * ab * B(cb, cs),  ao = as + ab - as * ab
//   (as = layer alpha * layerOpacity, ab = base alpha, B = blend function of the straight colors)

layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;

    // @summary How the layer is combined with the Input: 0 Normal, 1 Multiply, 2 Screen, 3 Overlay, 4 Add, 5 Subtract,
    //   6 Difference, 7 Darken, 8 Lighten, 9 SoftLight, 10 HardLight, 11 ColorDodge, 12 ColorBurn.
    // @default 0
    // @min 0
    // @max 12
    int mode;

    // Shows as pin "Layer Opacity" (the name "Opacity" belongs to the fixed pins of an effect node, so a uniform called opacity would be numbered).
    // @summary Strength of the layer, 0 = Input only, 1 = full blend.
    // @default 1
    // @min 0
    // @max 1
    float layerOpacity;

    // Set by VL.Qt, no pin. The rectangle of the element in the UV space of this pass (x, y, width, height). The effect chain makes the area
    // bigger than the element (padding of this or an earlier effect), the layer must stay over the element only.
    // 0 outside a chain: the layer then covers the whole area.
    vec4 elementRect;
};
layout(binding = 1) uniform sampler2D source;

// The layer is an element of its own (e.g. a gradient, an image), stretched over the element.
// @summary Element blended over the Input, stretched over the size of the element. Unconnected: no effect.
layout(binding = 2) uniform sampler2D layer;

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

// Add your own mode here: take a new case number, and list it in the summary of "mode".
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
    // The layer is premultiplied: scaling all four channels by the opacity fades it out as a whole.
    vec4 layerColor = inside ? texture(layer, luv) * clamp(layerOpacity, 0.0, 1.0) : vec4(0.0);
    float ab = base.a;
    float as = layerColor.a;
    // Back to straight colors for the blend function; 0 where there is nothing (no division by 0).
    vec3 cb = ab > 0.0 ? base.rgb / ab : vec3(0.0);
    vec3 cs = as > 0.0 ? layerColor.rgb / as : vec3(0.0);
    vec3 rgb = (1.0 - ab) * layerColor.rgb + (1.0 - as) * base.rgb + as * ab * blendFunction(cb, cs);
    float a = as + ab - as * ab;
    fragColor = vec4(rgb, a) * qt_Opacity;
}
