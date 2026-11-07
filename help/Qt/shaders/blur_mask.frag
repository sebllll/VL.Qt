#version 440
// Gaussian blur controlled by a grayscale mask (ShaderEffect, #487).
// Black in `mask` = sharp, white = `radius` pixels (in the pixels of the layer of Input).
//
// Separable on purpose: one shader for both passes, `direction` (1,0) = horizontal, (0,1) = vertical. Chain two
// ShaderEffects (pass 1: Input = the image element, pass 2: Input = pass 1, same uniforms, same mask). A Gaussian is
// separable, so the cost is 2 x (2*TAPS+1) texture fetches per pixel instead of (2*TAPS+1)^2 for a single 2D pass.
// With a mask the result is approximate (the radius is read at the destination pixel in both passes, "gather"
// variant), which is invisible for smooth masks.
//
// Fixed number of taps: the step between taps scales with the local radius (step = r / TAPS), so the cost does not
// grow with the radius; the kernel always covers +-3 sigma (sigma = r / 3), the weights are therefore constant and
// normalized. Large radii get a coarser sampling (bilinear filtering of the layer smooths it).
// Radius 0 (black mask or radius 0) returns the original pixel. Premultiplied in and out, times qt_Opacity.
//
// `size` = pixel size of the layer of Input (textureSize() is not available in ESSL 100 / GLSL 120, which qsb also bakes).
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, radius 68, size 72 (vec2), direction 80 (vec2).
// Bake: qsb --glsl "100 es,120,150" --hlsl 50 --msl 12 -o blur_mask.frag.qsb blur_mask.frag
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float radius;     // maximum radius in pixels (mask = 1)
    vec2 size;        // pixel size of the source layer
    vec2 direction;   // (1, 0) horizontal pass, (0, 1) vertical pass
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D mask;

const int TAPS = 12; // per side

void main()
{
    vec2 uv = qt_TexCoord0;
    vec4 center = texture(source, uv);
    float r = radius * texture(mask, uv).r;
    if (r < 0.5)
    {
        fragColor = center * qt_Opacity;
        return;
    }
    vec2 stepUv = direction * (r / float(TAPS)) / size;
    vec4 sum = center;
    float wsum = 1.0;
    for (int i = 1; i <= TAPS; i++)
    {
        float x = float(i) * (3.0 / float(TAPS)); // distance in sigmas
        float w = exp(-0.5 * x * x);
        vec2 o = stepUv * float(i);
        sum += w * (texture(source, clamp(uv + o, 0.0, 1.0)) + texture(source, clamp(uv - o, 0.0, 1.0)));
        wsum += 2.0 * w;
    }
    fragColor = sum / wsum * qt_Opacity;
}
