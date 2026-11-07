#version 440
// Tints the input element: HowTo Write a ShaderEffect. Needs an Input.
// Bake: qsb --glsl "100es,120,150" --hlsl 50 --msl 12 -o tint.frag.qsb tint.frag
// Uniform block (std140): qt_Matrix and qt_Opacity first, then your own members.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    vec4 tintColor;   // Color uniform: arrives premultiplied (rgb * a)
    float amount;     // 0 = input unchanged, 1 = fully tinted
};
layout(binding = 1) uniform sampler2D source; // the Input element, premultiplied

void main()
{
    vec4 c = texture(source, qt_TexCoord0);
    vec3 tint = tintColor.a > 0.0 ? tintColor.rgb / tintColor.a : vec3(0.0); // back to straight alpha
    vec3 rgb = mix(c.rgb, tint * c.a, amount);
    fragColor = vec4(rgb, c.a) * qt_Opacity; // output premultiplied
}
