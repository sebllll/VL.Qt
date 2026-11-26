using System;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>
    /// Base of the fakes that need only some commands: every member of <see cref="ICommandSink"/> does nothing and can be
    /// overridden.
    /// </summary>
    internal abstract class NullCommandSink : ICommandSink
    {
        public virtual void Create(uint id, ushort type) { }
        public virtual void Destroy(uint id) { }
        public virtual void Reparent(uint id, uint parent, uint index) { }
        public virtual void Bind(uint id, uint feedbackId) { }
        public virtual void SetF32(uint id, ushort prop, float value) { }
        public virtual void SetI32(uint id, ushort prop, int value) { }
        public virtual void SetBool(uint id, ushort prop, bool value) { }
        public virtual void SetColor(uint id, ushort prop, float r, float g, float b, float a) { }
        public virtual void SetRect(uint id, ushort prop, float x, float y, float width, float height) { }
        public virtual void SetMat4(uint id, ushort prop, in Matrix m) { }
        public virtual void SetString(uint id, ushort prop, ReadOnlySpan<char> value) { }
        public virtual void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values) { }
        public virtual void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points) { }
        public virtual void SetRef(uint id, ushort prop, uint refId) { }
        public virtual void SetU64(uint id, ushort prop, ulong value) { }
        public virtual void SetVec2(uint id, ushort prop, float x, float y) { }
        public virtual void SetIvec2(uint id, ushort prop, int x, int y) { }
        public virtual void BeginStyle(uint id, ushort flags) { }
        public virtual void StyleColor(ushort field, byte state, float r, float g, float b, float a) { }
        public virtual void StyleF32(ushort field, byte state, float value) { }
        public virtual void StyleI32(ushort field, byte state, int value) { }
        public virtual void StyleString(ushort field, byte state, ReadOnlySpan<char> value) { }
        public virtual void EndStyle() { }
        public virtual void BeginUniforms(uint id, ushort flags) { }
        public virtual void UniformF32(ReadOnlySpan<char> name, float value) { }
        public virtual void UniformI32(ReadOnlySpan<char> name, int value) { }
        public virtual void UniformBool(ReadOnlySpan<char> name, bool value) { }
        public virtual void UniformVec2(ReadOnlySpan<char> name, float x, float y) { }
        public virtual void UniformVec3(ReadOnlySpan<char> name, float x, float y, float z) { }
        public virtual void UniformVec4(ReadOnlySpan<char> name, float x, float y, float z, float w) { }
        public virtual void UniformIvec2(ReadOnlySpan<char> name, int x, int y) { }
        public virtual void UniformColor(ReadOnlySpan<char> name, float r, float g, float b, float a) { }
        public virtual void UniformMat4(ReadOnlySpan<char> name, in Matrix m) { }
        public virtual void UniformRef(ReadOnlySpan<char> name, uint refId) { }
        public virtual void UniformU64(ReadOnlySpan<char> name, ulong value) { }
        public virtual void UniformSampler(ReadOnlySpan<char> name, ulong packed) { }
        public virtual void EndUniforms() { }
        public virtual void BeginQmlProperties(uint id, ushort flags) { }
        public virtual void QmlPropertyF32(ReadOnlySpan<char> name, float value) { }
        public virtual void QmlPropertyI32(ReadOnlySpan<char> name, int value) { }
        public virtual void QmlPropertyBool(ReadOnlySpan<char> name, bool value) { }
        public virtual void QmlPropertyColor(ReadOnlySpan<char> name, float r, float g, float b, float a) { }
        public virtual void QmlPropertyVec2(ReadOnlySpan<char> name, float x, float y) { }
        public virtual void QmlPropertyVec3(ReadOnlySpan<char> name, float x, float y, float z) { }
        public virtual void QmlPropertyVec4(ReadOnlySpan<char> name, float x, float y, float z, float w) { }
        public virtual void QmlPropertyRect(ReadOnlySpan<char> name, float x, float y, float width, float height) { }
        public virtual void QmlPropertyString(ReadOnlySpan<char> name, ReadOnlySpan<char> value) { }
        public virtual void EndQmlProperties() { }
        public virtual void BeginEffectChain(uint id) { }
        public virtual void BeginMultiLink() { }
        public virtual void MultiEffectBool(ushort prop, bool value) { }
        public virtual void MultiEffectF32(ushort prop, float value) { }
        public virtual void MultiEffectI32(ushort prop, int value) { }
        public virtual void MultiEffectColor(ushort prop, float r, float g, float b, float a) { }
        public virtual void MultiEffectRect(ushort prop, float x, float y, float width, float height) { }
        public virtual void MultiEffectRef(ushort prop, uint refId) { }
        public virtual void BeginEffectChainLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop, float paddingRight, float paddingBottom, float strength) { }
        public virtual void EndLink() { }
        public virtual void EndEffectChain() { }
    }
}
