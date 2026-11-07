#version 440
// Generative animated gradient (builtin:gradient), no Input needed.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, time 68, color 80 (vec4 aligned to 16), size 96.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float time;   // seconds
    vec4 color;   // RGBA tint, already premultiplied by QtNative (Color uniforms)
};

void main()
{
    vec2 uv = qt_TexCoord0;
    float t = 0.5 + 0.5 * sin((uv.x + uv.y) * 4.0 + time);
    // color is premultiplied, so scaling all channels keeps the output premultiplied
    fragColor = color * mix(0.2, 1.0, t) * qt_Opacity;
}
