using System;
using Stride.Core.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// Target of the reconciler: the item commands of the protocol (see protocol.h), one SET method per value type.
    /// Implemented by <see cref="CommandWriter"/> (byte buffer for vlqt_window_apply) and by a recording fake in the tests.
    /// Implementations must not allocate per call in the steady state.
    /// </summary>
    internal interface ICommandSink
    {
        void Create(uint id, ushort type);
        void Destroy(uint id);
        void Reparent(uint id, uint parent, uint index);

        /// <summary>BIND: sinkHandle 0 = unbind; flags = <see cref="Protocol.Bind"/> bit mask.</summary>
        void Bind(uint id, uint sinkHandle, ushort flags);

        void SetF32(uint id, ushort prop, float value);
        void SetI32(uint id, ushort prop, int value);
        void SetBool(uint id, ushort prop, bool value);

        /// <summary>Color with straight (non-premultiplied) alpha.</summary>
        void SetColor(uint id, ushort prop, float r, float g, float b, float a);

        void SetRect(uint id, ushort prop, float x, float y, float width, float height);

        /// <summary>Stride matrix, sent as M11, M12, ..., M44 (row vectors, translation in M41..M43).</summary>
        void SetMat4(uint id, ushort prop, in Matrix m);

        void SetString(uint id, ushort prop, ReadOnlySpan<char> value);

        /// <summary>ref: id of another element (e.g. ShaderEffect source), 0 = none/root.</summary>
        void SetRef(uint id, ushort prop, uint refId);

        /// <summary>u64, e.g. an image resource id (<see cref="Protocol.Prop.Image"/>, #362), 0 = none.</summary>
        void SetU64(uint id, ushort prop, ulong value);

        /// <summary>vec2 (2 x f32), e.g. <see cref="Protocol.Prop.Size"/> (#469).</summary>
        void SetVec2(uint id, ushort prop, float x, float y);

        /// <summary>ivec2 (2 x i32), e.g. <see cref="Protocol.Prop.MeshResolution"/> (#437).</summary>
        void SetIvec2(uint id, ushort prop, int x, int y);

        /// <summary>
        /// SET_STYLE (#292): starts the command for element <paramref name="id"/>, flags = <see cref="Protocol.StyleSet"/> bit mask.
        /// Followed by Style* entries and <see cref="EndStyle"/>; no other command in between.
        /// </summary>
        void BeginStyle(uint id, ushort flags);

        /// <summary>Style entry: color with straight alpha. field = <see cref="Protocol.StyleField"/>, state = <see cref="Protocol.StyleState"/>.</summary>
        void StyleColor(ushort field, byte state, float r, float g, float b, float a);

        void StyleF32(ushort field, byte state, float value);
        void StyleI32(ushort field, byte state, int value);
        void StyleString(ushort field, byte state, ReadOnlySpan<char> value);

        /// <summary>Closes the SET_STYLE started by <see cref="BeginStyle"/> (entry count, payload length).</summary>
        void EndStyle();

        /// <summary>
        /// SET_UNIFORMS (#437): starts the command for the ShaderEffect <paramref name="id"/>, flags = <see cref="Protocol.UniformSet"/>
        /// bit mask. Followed by Uniform* entries (name 1..<see cref="Protocol.UniformNameMax"/> UTF-8 bytes) and
        /// <see cref="EndUniforms"/>; no other command in between.
        /// </summary>
        void BeginUniforms(uint id, ushort flags);

        void UniformF32(ReadOnlySpan<char> name, float value);
        void UniformI32(ReadOnlySpan<char> name, int value);
        void UniformBool(ReadOnlySpan<char> name, bool value);
        void UniformVec2(ReadOnlySpan<char> name, float x, float y);
        void UniformVec3(ReadOnlySpan<char> name, float x, float y, float z);
        void UniformVec4(ReadOnlySpan<char> name, float x, float y, float z, float w);
        void UniformIvec2(ReadOnlySpan<char> name, int x, int y);

        /// <summary>Color with straight alpha (QtNative premultiplies).</summary>
        void UniformColor(ReadOnlySpan<char> name, float r, float g, float b, float a);

        /// <summary>Stride matrix, sent as M11, M12, ..., M44 (QtNative transposes for GLSL).</summary>
        void UniformMat4(ReadOnlySpan<char> name, in Matrix m);

        /// <summary>sampler2D from an element: id of a child of the ShaderEffect (0 = none).</summary>
        void UniformRef(ReadOnlySpan<char> name, uint refId);

        /// <summary>sampler2D from an image resource id (0 = none).</summary>
        void UniformU64(ReadOnlySpan<char> name, ulong value);

        /// <summary>Sampler state of the sampler2D <paramref name="name"/> (#492, <see cref="Elements.Sampler.Pack"/>).</summary>
        void UniformSampler(ReadOnlySpan<char> name, ulong packed);

        /// <summary>Closes the SET_UNIFORMS started by <see cref="BeginUniforms"/> (entry count, payload length).</summary>
        void EndUniforms();

        /// <summary>
        /// SET_EFFECT_CHAIN (#489): starts the whole Effect chain of element <paramref name="id"/>. Followed by the links in
        /// application order (first = applied first), each <see cref="BeginMultiLink"/> + Effect* entries or
        /// <see cref="BeginShaderLink"/> + Uniform* entries, then <see cref="EndLink"/>; closed by <see cref="EndEffectChain"/>.
        /// No links = no chain. No other command in between.
        /// </summary>
        void BeginEffectChain(uint id);

        /// <summary>Starts a MULTI link: one MultiEffect run, entries keyed by <see cref="Protocol.Prop"/> EffectBlurEnabled..EffectMaskInverted.</summary>
        void BeginMultiLink();

        void EffectBool(ushort prop, bool value);
        void EffectF32(ushort prop, float value);
        void EffectI32(ushort prop, int value);

        /// <summary>Color with straight alpha.</summary>
        void EffectColor(ushort prop, float r, float g, float b, float a);

        /// <summary>x = left, y = top, width = right, height = bottom (EffectPaddingRect).</summary>
        void EffectRect(ushort prop, float x, float y, float width, float height);

        /// <summary>EffectMaskSource: id of the mask element object (0 = none).</summary>
        void EffectRef(ushort prop, uint refId);

        /// <summary>
        /// Starts a SHADER link: <paramref name="template"/> = <see cref="Protocol.EffectTemplate"/>, shader resource ids by
        /// role (Single/TwoPass: fragment; Pyramid: prefilter, composite), padding in DIP, strength (TwoPass: effect size,
        /// Pyramid: radius in DIP) and for TwoPass the uniform that takes it (else empty). Uniform* entries follow.
        /// </summary>
        void BeginShaderLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop,
            float paddingRight, float paddingBottom, float strength, ReadOnlySpan<char> strengthUniform);

        /// <summary>Closes the link started by <see cref="BeginMultiLink"/> or <see cref="BeginShaderLink"/>.</summary>
        void EndLink();

        /// <summary>Closes the SET_EFFECT_CHAIN started by <see cref="BeginEffectChain"/> (link count, payload length).</summary>
        void EndEffectChain();
    }

    /// <summary>Stride-typed convenience overloads for any <see cref="ICommandSink"/>.</summary>
    internal static class CommandSinkExtensions
    {
        public static void SetColor(this ICommandSink sink, uint id, ushort prop, in Color4 c) => sink.SetColor(id, prop, c.R, c.G, c.B, c.A);

        public static void SetRect(this ICommandSink sink, uint id, ushort prop, in RectangleF r) => sink.SetRect(id, prop, r.X, r.Y, r.Width, r.Height);

        public static void SetString(this ICommandSink sink, uint id, ushort prop, string? value) => sink.SetString(id, prop, value.AsSpan());
    }
}
