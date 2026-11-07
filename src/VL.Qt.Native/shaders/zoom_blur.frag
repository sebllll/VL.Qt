#version 440
// Zoom blur of the input element (builtin:zoom_blur): streaks along rays from center. Needs an Input. Two passes (see sampleCount).
// Box blur between uv scaled by 1 - k/2 and 1 + k/2 around center (centred on the pixel, the picture does not shift).
// Lengths in DIP, texelSize in pixels.
// Uniform block (std140): qt_Matrix 0, qt_Opacity 64, center 72, length 80, texelSize 88, dpr 96.
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    vec2 center;     // zoom center, 0..1 of the effect area (0.5, 0.5 = middle)
    float length;    // streak length in DIP at the farthest corner of the area; grows linearly from 0 at center. 0 = off
    vec2 texelSize;  // 1 / width, 1 / height of the source in pixels (DIP size x dpr, as the layer is created)
    float dpr;       // device pixels per DIP (1 if unset)
};
layout(binding = 1) uniform sampler2D source;

const int maxSamples = 64;

// Sample count for a blur line of `len` pixels: one sample per pixel, 2..maxSamples, equal weights, taken at the middle of
// their slots, so the result does not step when the count changes while the length is animated.
// Beyond maxSamples pixels the samples are further than one pixel apart and show as copies: that is why the effect is always
// two passes of this shader. Pass 2 reads the output of pass 1 with the length divided by maxSamples (= the sample spacing
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

float dist(vec2 v) { return sqrt(dot(v, v)); }

void main()
{
    vec2 uv = qt_TexCoord0;
    vec2 q = uv - center;
    vec2 qd = q / texelSize;  // offset from center in pixels
    // distance of the farthest corner in pixels: there the streak is exactly `length` DIP
    float rMax = max(max(dist(center / texelSize), dist((vec2(1.0, 0.0) - center) / texelSize)),
                     max(dist((vec2(0.0, 1.0) - center) / texelSize), dist((vec2(1.0) - center) / texelSize)));
    float k = abs(length) * max(dpr, 1.0) / max(rMax, 1.0);   // relative zoom range of the streak
    float local = dist(qd) * k;                               // streak length at this pixel in pixels
    if (local < 0.1) {
        fragColor = fetch(uv) * qt_Opacity;
        return;
    }
    int n = sampleCount(local);
    vec4 sum = vec4(0.0);
    for (int i = 0; i < n; ++i) {
        float t = (float(i) + 0.5) / float(n) - 0.5;
        sum += fetch(center + q * (1.0 + k * t));
    }
    fragColor = sum / float(n) * qt_Opacity;
}
