#version 440
// @category Qt.Effects.Examples
// @chainonly
// @padding displacement
// @summary Shifts the pixels of the Input by a displacement map (an element): red moves in x, green in y, 0.5 = no shift,
//   the alpha of the map scales the shift. Same convention as Qt5Compat Displace.
//   Template: copy into a qtshaders/ folder next to your patch, rename, change.

// Template for an effect of your own: the same shader as the built-in Displace node, as a shader file.
// The file name is the node name (ExampleDisplace.frag -> node "ExampleDisplace" in Qt.Effects). Chain only: the file
// makes only that effect chain node, no element node. It is kept equal to the built-in one by a unit test, so change a copy.
// Padding: the shifted picture reaches up to "displacement" DIP beyond the element, so that is the space around it
// (abs(displacement) times 1 on all four sides). The built-in node rounds up to whole DIP, this one does not.
//
// qt_Matrix and qt_Opacity are Qt's, the shader only has to declare them.
//
// Qt textures hold PREMULTIPLIED colors (rgb already times alpha), and so must the output.
// The output samples the source at uv + shift, so the picture moves against the map value
// (red above 0.5 pulls content from the right: it appears shifted to the left).

layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;

    // Set by VL.Qt, no pin. 1 / width, 1 / height of the source in pixels (DIP size x dpr, as the layer is created):
    // turns a distance in pixels into UV.
    vec2 texelSize;

    // @summary Largest shift in DIP (1 unit in a patch = 100 DIP), negative allowed. A map value of 0 or 1 moves the pixels by this.
    // @default 10
    float displacement;

    // Set by VL.Qt, no pin. Device pixels per DIP (1 if unset); a DIP is not a pixel on a high-DPI screen.
    float dpr;

    // Set by VL.Qt, no pin. The rectangle of the element in the UV space of this pass (x, y, width, height). The effect
    // chain makes the area bigger than the element (padding), the map must stay over the element only.
    // 0 outside a chain: the map then covers the whole area.
    vec4 elementRect;
};
layout(binding = 1) uniform sampler2D source;

// The map is an element of its own (e.g. a ShaderEffect with noise), stretched over the element.
// @summary Element used as displacement map. Outside the element its edge continues. Unconnected: no effect.
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
