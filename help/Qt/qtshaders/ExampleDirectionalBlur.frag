#version 440
// @category Qt.Effects.Examples
// @chainonly
// @passes 2
// @padding blurLength 0.52
// @summary Linear motion blur: smears the Input along a line, centred on each pixel (the picture does not shift).
//   Template: copy into a qtshaders/ folder next to your patch, rename, change.

// Template for an effect of your own: the same shader as the built-in DirectionalBlur node, as a shader file.
// The file name is the node name (ExampleDirectionalBlur.frag -> node "ExampleDirectionalBlur" in Qt.Effects). Chain only: the file makes only that
// effect chain node, no element node. It is kept equal to the built-in one by a unit test, so change a copy.
// Padding: the blur reaches half its length beyond the element. The built-in node pads x and y separately by the angle
// (half the length plus 1/64, times |cos| and |sin|); the padding tag has one number for all four sides, so this is
// 0.52 times the length on every side, the worst case (angle 0 or 90).
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

    // @summary Direction of the blur in cycles (1 = full turn): 0 = horizontal, increases clockwise on screen.
    // @default 0
    float angle;

    // Not named "length": the GLSL translation renames a member that is called like a GLSL builtin, and the GL backend
    // would then not set it (#77). Avoid builtin names (length, distance, dot, step, mix, ...) for uniforms.
    // @summary Length of the blur line in DIP, total and centred on the pixel. 0 = no blur.
    // @default 10
    // @min 0
    float blurLength;

    // Set by VL.Qt, no pin. 1 / width, 1 / height of the source in pixels (DIP size x dpr, as the layer is created):
    // turns a distance in pixels into UV.
    vec2 texelSize;

    // Set by VL.Qt, no pin. Device pixels per DIP (1 if unset); a DIP is not a pixel on a high-DPI screen.
    float dpr;

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
