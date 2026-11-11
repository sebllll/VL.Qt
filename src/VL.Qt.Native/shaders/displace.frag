#version 440
// Displace (builtin:displace): shifts the pixels of the input by a displacement map (one pass).
// Same convention as Qt5Compat Displace: map red -> x, green -> y, 0.5 = no shift, alpha of the map scales the shift.
// The map is stretched over the ELEMENT (elementRect, set by the effect chain: the element's rectangle in UV of this pass, bigger
// than the element after a Blur or with this effect's own padding); outside it the edge of the map continues. elementRect 0
// (not in a chain): the map covers the whole item. The output samples the source at uv + shift, so the picture moves
// against the map value (red above 0.5 pulls content from the right: it appears shifted to the left).
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, texelSize 72 (vec2, aligned to 8), displacement 80, dpr 84, elementRect 96 (vec4).
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    vec2 texelSize;      // 1 / width, 1 / height of the source in pixels (DIP size x dpr, as the layer is created)
    float displacement;  // maximum shift in DIP, negative allowed
    float dpr;           // device pixels per DIP (1 if unset)
    vec4 elementRect;    // x, y, w, h of the element in UV of this pass (set by VL.Qt in a chain)
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D map;

void main()
{
    vec2 muv = elementRect.z > 0.0 && elementRect.w > 0.0 ? clamp((qt_TexCoord0 - elementRect.xy) / elementRect.zw, 0.0, 1.0) : qt_TexCoord0;
    vec4 mapColor = texture(map, muv);
    // the map is premultiplied: recover the straight channels, alpha scales the shift
    vec2 dir = mapColor.a > 0.0 ? 2.0 * (mapColor.rg / mapColor.a - 0.5) : vec2(0.0);
    vec2 uv = qt_TexCoord0 + dir * mapColor.a * displacement * (dpr > 0.0 ? dpr : 1.0) * texelSize; // DIP -> pixels -> UV
    // premultiplied source; samples outside the item are transparent
    vec4 c = (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) ? vec4(0.0) : texture(source, uv);
    fragColor = c * qt_Opacity;
}
