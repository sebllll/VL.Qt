#version 440
// Wave distortion of the input element (builtin:wave). Needs an Input.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, time 68, amplitude 72, frequency 76, size 80.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float time;       // seconds
    float amplitude;  // shift in texture coordinates (0..1), e.g. 0.02
    float frequency;  // waves over the item, e.g. 8
};
layout(binding = 1) uniform sampler2D source;

void main()
{
    vec2 uv = qt_TexCoord0;
    uv.x += amplitude * sin(uv.y * frequency * 6.2831853 + time);
    uv.y += amplitude * 0.5 * sin(uv.x * frequency * 6.2831853 + time * 1.3);
    // premultiplied source; samples outside the item are transparent
    vec4 c = (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) ? vec4(0.0) : texture(source, uv);
    fragColor = c * qt_Opacity;
}
