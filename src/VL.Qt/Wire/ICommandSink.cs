using System;
using Stride.Core.Mathematics;

namespace VL.Qt.Wire
{
    /// <summary>
    /// Target of the reconciler: the item commands of the protocol (see protocol.h), one SET method per value type.
    /// A command with entries (SET_STYLE, SET_UNIFORMS, SET_QML_PROPERTIES, SET_EFFECT_CHAIN) is a <c>Begin*</c>, its entries
    /// (<c>Style*</c>, <c>Uniform*</c>, <c>QmlProperty*</c>, <c>MultiEffect*</c> and the links) and its <c>End*</c>; no other command
    /// and no other <c>Begin*</c> in between. <see cref="CommandWriter"/> throws if a caller breaks this.
    /// Implemented by <see cref="CommandWriter"/> (byte buffer for vlqt_scene_apply) and by a recording fake in the tests.
    /// Implementations must not allocate per call in the steady state.
    /// </summary>
    internal interface ICommandSink
    {
        void Create(uint id, ushort type);
        void Destroy(uint id);
        void Reparent(uint id, uint parent, uint index);

        /// <summary>BIND: feedbackId 0 = unbind.</summary>
        void Bind(uint id, uint feedbackId);

        void SetF32(uint id, ushort prop, float value);
        void SetI32(uint id, ushort prop, int value);
        void SetBool(uint id, ushort prop, bool value);

        /// <summary>Color with straight (non-premultiplied) alpha.</summary>
        void SetColor(uint id, ushort prop, float r, float g, float b, float a);

        void SetRect(uint id, ushort prop, float x, float y, float width, float height);

        /// <summary>Stride matrix, sent as M11, M12, ..., M44 (row vectors, translation in M41..M43).</summary>
        void SetMat4(uint id, ushort prop, in Matrix m);

        void SetString(uint id, ushort prop, ReadOnlySpan<char> value);

        /// <summary>f32 array (<see cref="Protocol.ValueType.F32Array"/>): count and floats.</summary>
        void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values);

        /// <summary>path (<see cref="Protocol.ValueType.Path"/>): verbs (<see cref="Protocol.Verb"/>) and points as x,y pairs (2 floats per point).</summary>
        void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points);

        /// <summary>ref: id of another element (e.g. Shader source), 0 = none/root.</summary>
        void SetRef(uint id, ushort prop, uint refId);

        /// <summary>u64, e.g. an image resource id (<see cref="Protocol.PropertyId.Image"/>), 0 = none.</summary>
        void SetU64(uint id, ushort prop, ulong value);

        /// <summary>vec2 (2 x f32), e.g. <see cref="Protocol.PropertyId.Size"/>.</summary>
        void SetVec2(uint id, ushort prop, float x, float y);

        /// <summary>ivec2 (2 x i32), e.g. <see cref="Protocol.PropertyId.MeshResolution"/>.</summary>
        void SetIvec2(uint id, ushort prop, int x, int y);

        /// <summary>
        /// SET_STYLE: starts the command for element <paramref name="id"/>, flags = <see cref="Protocol.StyleSet"/> bit mask.
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
        /// SET_UNIFORMS: starts the command for the Shader <paramref name="id"/>, flags = <see cref="Protocol.UniformSet"/>
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

        /// <summary>sampler2D from an element: id of a child of the Shader (0 = none).</summary>
        void UniformRef(ReadOnlySpan<char> name, uint refId);

        /// <summary>sampler2D from an image resource id (0 = none).</summary>
        void UniformU64(ReadOnlySpan<char> name, ulong value);

        /// <summary>Sampler state of the sampler2D <paramref name="name"/> (<see cref="Reconciliation.UniformWriter.PackSampler"/>).</summary>
        void UniformSampler(ReadOnlySpan<char> name, ulong packed);

        /// <summary>Closes the SET_UNIFORMS started by <see cref="BeginUniforms"/> (entry count, payload length).</summary>
        void EndUniforms();

        /// <summary>
        /// SET_QML_PROPERTIES: starts the command for the QmlElement <paramref name="id"/> (flags 0). Followed by QmlProperty*
        /// entries (name 1..<see cref="Protocol.UniformNameMax"/> UTF-8 bytes) and <see cref="EndQmlProperties"/>; no other command in between.
        /// </summary>
        void BeginQmlProperties(uint id, ushort flags);

        void QmlPropertyF32(ReadOnlySpan<char> name, float value);
        void QmlPropertyI32(ReadOnlySpan<char> name, int value);
        void QmlPropertyBool(ReadOnlySpan<char> name, bool value);

        /// <summary>Color with straight alpha.</summary>
        void QmlPropertyColor(ReadOnlySpan<char> name, float r, float g, float b, float a);

        void QmlPropertyVec2(ReadOnlySpan<char> name, float x, float y);
        void QmlPropertyVec3(ReadOnlySpan<char> name, float x, float y, float z);
        void QmlPropertyVec4(ReadOnlySpan<char> name, float x, float y, float z, float w);

        /// <summary>rect (x, y, width, height).</summary>
        void QmlPropertyRect(ReadOnlySpan<char> name, float x, float y, float width, float height);

        /// <summary>string (u32 length + UTF-8).</summary>
        void QmlPropertyString(ReadOnlySpan<char> name, ReadOnlySpan<char> value);

        /// <summary>
        /// list of floats: <paramref name="elementType"/> is F32, Color, Rectf, Vec2, Vec3 or Vec4 and <paramref name="values"/> holds
        /// 1, 4, 4, 2, 3 or 4 floats per element (at most <see cref="Protocol.ListValueMaxCount"/> elements).
        /// </summary>
        void QmlPropertyListF32(ReadOnlySpan<char> name, ushort elementType, ReadOnlySpan<float> values);

        /// <summary>list of int (at most <see cref="Protocol.ListValueMaxCount"/> elements).</summary>
        void QmlPropertyListI32(ReadOnlySpan<char> name, ReadOnlySpan<int> values);

        /// <summary>list of bool, one byte each (at most <see cref="Protocol.ListValueMaxCount"/> elements).</summary>
        void QmlPropertyListBool(ReadOnlySpan<char> name, ReadOnlySpan<bool> values);

        /// <summary>list of string (at most <see cref="Protocol.ListValueMaxCount"/> elements; null counts as empty).</summary>
        void QmlPropertyListString(ReadOnlySpan<char> name, ReadOnlySpan<string> values);

        /// <summary>Closes the SET_QML_PROPERTIES started by <see cref="BeginQmlProperties"/> (entry count, payload length).</summary>
        void EndQmlProperties();

        /// <summary>
        /// SET_EFFECT_CHAIN: starts the whole Effect chain of element <paramref name="id"/>. Followed by the links in
        /// application order (first = applied first), each <see cref="BeginMultiLink"/> + MultiEffect* entries or
        /// <see cref="BeginEffectChainLink"/> + Uniform* entries, then <see cref="EndLink"/>; closed by <see cref="EndEffectChain"/>.
        /// No links = no chain. No other command in between.
        /// </summary>
        void BeginEffectChain(uint id);

        /// <summary>Starts a MULTI link: one MultiEffect run, entries keyed by <see cref="Protocol.MultiEffectKey"/> BlurEnabled..MaskInverted.</summary>
        void BeginMultiLink();

        void MultiEffectBool(ushort prop, bool value);
        void MultiEffectF32(ushort prop, float value);
        void MultiEffectI32(ushort prop, int value);

        /// <summary>Color with straight alpha.</summary>
        void MultiEffectColor(ushort prop, float r, float g, float b, float a);

        /// <summary>x = left, y = top, width = right, height = bottom (<see cref="Protocol.MultiEffectKey.PaddingRect"/>).</summary>
        void MultiEffectRect(ushort prop, float x, float y, float width, float height);

        /// <summary>MaskSource: id of the mask element object (0 = none).</summary>
        void MultiEffectRef(ushort prop, uint refId);

        /// <summary>
        /// Starts a SHADER link: <paramref name="template"/> = <see cref="Protocol.EffectTemplate"/>, shader resource ids by
        /// role (Single/TwoPass: fragment; Pyramid: prefilter, composite), padding in DIP and strength (Pyramid: radius in
        /// DIP, else unused). Uniform* entries follow.
        /// </summary>
        void BeginEffectChainLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop,
            float paddingRight, float paddingBottom, float strength);

        /// <summary>Closes the link started by <see cref="BeginMultiLink"/> or <see cref="BeginEffectChainLink"/>.</summary>
        void EndLink();

        /// <summary>Closes the SET_EFFECT_CHAIN started by <see cref="BeginEffectChain"/> (link count, payload length).</summary>
        void EndEffectChain();
    }
}
