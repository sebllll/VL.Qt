#version 440
// @category Qt.Effects.Examples
// @chainonly
// @passes 2
// @padding angle 600
// @summary Radial (rotational) blur: arcs around Center, a box blur over a rotation of Angle cycles (centred, the
//   picture does not turn).
//   Template: copy into a qtshaders/ folder next to your patch, rename, change.

// Template for an effect of your own: the same shader as the built-in RadialBlur node, as a shader file.
// The file name is the node name (ExampleRadialBlur.frag -> node "ExampleRadialBlur" in Qt.Effects). Chain only: the file makes only that
// effect chain node, no element node. It is kept equal to the built-in one by a unit test, so change a copy.
// Padding: the built-in node has its own Padding pin; a shader file cannot add a pin, so the padding grows with Angle here:
// 600 DIP per cycle = 30 DIP (the built-in default) at the default 0.05 cycles. The arcs need about half the angle in
// radians times the distance of the farthest point from Center, so 30 DIP fit a distance of about 430 DIP; raise the
// factor for a larger element.
//
// Two passes (the passes tag above): one pass has at most maxSamples (64) samples, so a blur wider than 64 pixels would
// show as copies. Pass 2 blurs the result of pass 1 again, with the length divided by 64, which fills the gaps exactly
// (64 x 64 effective samples). VL.Qt runs the same shader twice and sets passIndex (0, then 1).
// Where pass 1 needs no more than 64 samples, pass 2 blurs by less than a pixel, so it changes nothing.
//
// qt_Matrix and qt_Opacity are Qt's, the shader only has to declare them.
//
// Qt textures hold PREMULTIPLIED colors (rgb already times alpha), and so must the output: averaging premultiplied
// samples is right (a transparent sample adds nothing and dims the result), no division by alpha needed.

layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;

    // @summary Center of the rotation, 0..1 of the effect area (0.5, 0.5 = middle).
    // @default 0.5, 0.5
    vec2 center;

    // @summary Total rotation of the blur in cycles (1 = full turn), clockwise on screen. 0 = no blur.
    // @default 0.05
    // @min 0
    float angle;

    // Set by VL.Qt, no pin. 1 / width, 1 / height of the source in pixels (DIP size x dpr, as the layer is created):
    // turns a distance in pixels into UV.
    vec2 texelSize;

    // Set by VL.Qt, no pin. 0 in pass 1, 1 in pass 2: pass 2 blurs the result of pass 1 by 1/64 of the length.
    float passIndex;
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
// textureLod, not texture: the source has no mipmaps, so both read the same, but with texture the shader compiler copies the
// loop maxSamples times, and that large shader crashes the Windows ARM64 software renderer (WARP).
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
