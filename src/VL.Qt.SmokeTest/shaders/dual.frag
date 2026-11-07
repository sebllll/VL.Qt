#version 440
// SmokeTest fixture (shader-effect, #437): two samplers. Output = mix(source, overlay, amount), premultiplied.
// Bake: qsb --glsl "100 es,120,150" --hlsl 50 --msl 12 -o dual.frag.qsb dual.frag
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, amount 68.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float amount;
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D overlay;

void main()
{
    fragColor = mix(texture(source, qt_TexCoord0), texture(overlay, qt_TexCoord0), amount) * qt_Opacity;
}
