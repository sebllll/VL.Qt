#version 440
// Radial (rotational) blur of the input element (builtin:radial_blur): arcs around center. Needs an Input. Two passes (see sampleCount).
// Box blur over a rotation of +-angle/2 cycles around center (centred on the pixel, the picture does not shift). texelSize in pixels.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, center 72, angle 80, texelSize 88, passIndex 96.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    vec2 center;     // rotation center, 0..1 of the effect area (0.5, 0.5 = middle)
    float angle;     // total rotation of the blur in cycles (1 = full turn, clockwise on screen); 0 = off
    vec2 texelSize;  // 1 / width, 1 / height of the source in pixels (DIP size x dpr, as the layer is created)
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
// textureLod, not texture: the source has no mipmaps, so both read level 0, but texture needs screen-space gradients, and in
// a loop with a per-pixel count the HLSL compiler then unrolls the loop to maxSamples copies. That unrolled shader needs so
// much stack that WARP's ARM64 shader JIT (d3d10warp 10.0.26100) addresses the spill wrongly and crashes; with textureLod the
// loop stays a loop. Keep it so even once WARP is fixed: the unrolled form is also far larger on every backend.
vec4 fetch(vec2 uv)
{
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return vec4(0.0);
    vec2 p = uv / texelSize - 0.5;
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 c = (i + 0.5) * texelSize;
    return mix(mix(textureLod(source, c, 0.0), textureLod(source, c + vec2(texelSize.x, 0.0), 0.0), f.x),
               mix(textureLod(source, c + vec2(0.0, texelSize.y), 0.0), textureLod(source, c + texelSize, 0.0), f.x), f.y);
}

void main()
{
    vec2 uv = qt_TexCoord0;
    vec2 qd = (uv - center) / texelSize;  // offset from center in pixels: rotate there, so the arcs stay round on any aspect
    float rad = 6.28318530718 * abs(angle) / (passIndex >= 0.5 ? float(maxSamples) : 1.0);
    float arc = sqrt(dot(qd, qd)) * rad;  // arc length at this pixel in pixels
    if (arc < 0.1) {
        fragColor = fetch(uv) * qt_Opacity;
        return;
    }
    int n = sampleCount(arc);
    vec4 sum = vec4(0.0);
    for (int i = 0; i < n; ++i) {
        float t = (float(i) + 0.5) / float(n) - 0.5;
        float s = sin(rad * t), c = cos(rad * t);
        vec2 r = vec2(c * qd.x - s * qd.y, s * qd.x + c * qd.y);
        sum += fetch(center + r * texelSize);
    }
    fragColor = sum / float(n) * qt_Opacity;
}
