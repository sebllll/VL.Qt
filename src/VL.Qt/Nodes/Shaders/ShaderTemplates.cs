namespace VL.Qt.Nodes
{
    /// <summary>Default shader text of the ShaderEffect node's code pins.</summary>
    internal static class ShaderTemplates
    {
        public const string Fragment = """
            #version 440
            // Fragment shader for the ShaderEffect node: full Qt GLSL (Vulkan style, like Qt's qsb tool takes it).
            // Edit the text and the effect updates while you type; errors show at the node, the last
            // working version keeps drawing meanwhile.

            // Position inside the element, 0..1 (x to the right, y down).
            layout(location = 0) in vec2 qt_TexCoord0;
            layout(location = 0) out vec4 fragColor;

            // All uniforms live in this one block. It must start with qt_Matrix and qt_Opacity.
            // Below them add your own: a uniform gets its value from a Float or Color node of
            // Qt.Elements.Shaders.Uniform with the same name; without such a node the value is 0.
            layout(std140, binding = 0) uniform buf {
                mat4 qt_Matrix;
                float qt_Opacity;
                float time;   // seconds, drive it with a Float uniform named "time"
                vec4 color;   // tint, drive it with a Color uniform named "color"
            };

            // The element at the Input pin (without Input it is transparent).
            layout(binding = 1) uniform sampler2D source;

            void main()
            {
                vec2 uv = qt_TexCoord0;

                // Moving gradient, visible even without Input and without uniforms.
                float wave = 0.5 + 0.5 * sin((uv.x + uv.y) * 6.0 + time * 2.0);
                vec4 gradient = vec4(uv, wave, 1.0) * wave;

                // Sample the input (premultiplied alpha). Warp the lookup for a ripple.
                vec2 warped = uv + 0.02 * vec2(sin(uv.y * 20.0 + time), cos(uv.x * 20.0 + time));
                vec4 src = texture(source, warped);

                // Input on top of the gradient, tinted by color (premultiplied, so scale all channels; alpha 0 = no tint).
                vec4 result = src + gradient * (1.0 - src.a);
                fragColor = result * (color + step(color.a, 0.0)) * qt_Opacity;
            }
            """;
    }
}
