#version 440
// Directional (linear motion) blur of the input element (builtin:directional_blur). Needs an Input. Two passes (see sampleCount).
// Box blur along a line through each pixel, centred on it (the picture does not shift). Lengths in DIP, texelSize in pixels.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, angle 68, blurLength 72, texelSize 80, dpr 88, passIndex 92.
// Not named `length`: the GLSL translation renames a member called like a GLSL builtin, the GL backend would not set it.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float angle;     // direction of the blur in cycles (1 = full turn), 0 = horizontal, increases clockwise on screen
    float blurLength; // length of the blur line in DIP (total, centred on the pixel), 0 = off
    vec2 texelSize;  // 1 / width, 1 / height of the source in pixels (DIP size x dpr, as the layer is created)
    float dpr;       // device pixels per DIP (1 if unset)
    float passIndex; // set by VL.Qt: 0 in pass 1, 1 in pass 2 (TWO_PASS template)
};
layout(binding = 1) uniform sampler2D source;

const int maxSamples = 64;

// Sample count for a blur line of `len` pixels: one sample per pixel, 2..maxSamples, equal weights, taken at the middle of
// their slots, so the result does not step when the count changes while the length is animated.
// Beyond maxSamples pixels the samples are further than one pixel apart and show as copies: that is why the effect is always
// two passes of this shader. Pass 2 (passIndex 1) reads the output of pass 1 with the length divided by maxSamples (= the sample spacing
// of pass 1 once the cap is reached): a box of one spacing fills the gaps exactly, up to maxSamples^2 effective samples.
// Where pass 1 does not hit the cap, pass 2 blurs by less than one pixel, so it is practically the identity.
int sampleCount(float len)
{
    return clamp(int(ceil(len)), 2, maxSamples);
}

// Bilinear sample of the premultiplied source, done by hand: the layers are sampled nearest (not smooth), and a nearest
// sample every pixel or less would hit some texels twice and skip others, which shows as hatching. Outside the item it is
// transparent.
vec4 fetch(vec2 uv)
{
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return vec4(0.0);
    vec2 p = uv / texelSize - 0.5;
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 c = (i + 0.5) * texelSize;
    return mix(mix(texture(source, c), texture(source, c + vec2(texelSize.x, 0.0)), f.x),
               mix(texture(source, c + vec2(0.0, texelSize.y)), texture(source, c + texelSize), f.x), f.y);
}

void main()
{
    vec2 uv = qt_TexCoord0;
    float len = abs(blurLength) * max(dpr, 1.0) / (passIndex >= 0.5 ? float(maxSamples) : 1.0);  // pixels
    if (len < 0.1) {
        fragColor = fetch(uv) * qt_Opacity;
        return;
    }
    int n = sampleCount(len);
    float a = 6.28318530718 * angle;
    vec2 step = vec2(cos(a), sin(a)) * len * texelSize;
    vec4 sum = vec4(0.0);
    for (int i = 0; i < n; ++i) {
        float t = (float(i) + 0.5) / float(n) - 0.5;
        sum += fetch(uv + step * t);
    }
    fragColor = sum / float(n) * qt_Opacity;
}
