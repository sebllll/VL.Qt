using System;
using Stride.Core.Mathematics;

namespace VL.Qt.Tests
{
    /// <summary>
    /// Fault injection for reconciler tests: forwards every command to a <see cref="RecordingSink"/> and throws an
    /// <see cref="InjectedFault"/> when the <see cref="ThrowAt"/>-th command arrives (that command is not recorded, the
    /// next ones are again). A command is a Create, Destroy, Reparent, Bind, Set* or the Begin* of a multi-entry command;
    /// the entries in between do not count. <see cref="ThrowAt"/> 0 means never.
    /// </summary>
    internal sealed class ThrowingSink : ICommandSink
    {
        /// <summary>The exception the sink throws.</summary>
        internal sealed class InjectedFault : Exception
        {
            public InjectedFault(int command) : base($"injected fault at command {command}") { }
        }

        readonly RecordingSink _inner = new();

        /// <summary>What reached the inner sink.</summary>
        public RecordingSink Recorded => _inner;

        /// <summary>1-based number of the command that throws; 0 = never. Counting starts at <see cref="Count"/> 0.</summary>
        public int ThrowAt { get; set; }

        /// <summary>Commands seen so far, the throwing one included.</summary>
        public int Count { get; private set; }

        /// <summary>Counts the command-count from 0 again and forgets what was recorded; <see cref="ThrowAt"/> stays.</summary>
        public void Restart()
        {
            Count = 0;
            _inner.Clear();
        }

        void Tick()
        {
            if (++Count == ThrowAt)
                throw new InjectedFault(Count);
        }

        public void Create(uint id, ushort type) { Tick(); _inner.Create(id, type); }
        public void Destroy(uint id) { Tick(); _inner.Destroy(id); }
        public void Reparent(uint id, uint parent, uint index) { Tick(); _inner.Reparent(id, parent, index); }
        public void Bind(uint id, uint sinkHandle, ushort flags) { Tick(); _inner.Bind(id, sinkHandle, flags); }

        public void SetF32(uint id, ushort prop, float value) { Tick(); _inner.SetF32(id, prop, value); }
        public void SetI32(uint id, ushort prop, int value) { Tick(); _inner.SetI32(id, prop, value); }
        public void SetBool(uint id, ushort prop, bool value) { Tick(); _inner.SetBool(id, prop, value); }
        public void SetColor(uint id, ushort prop, float r, float g, float b, float a) { Tick(); _inner.SetColor(id, prop, r, g, b, a); }
        public void SetRect(uint id, ushort prop, float x, float y, float width, float height) { Tick(); _inner.SetRect(id, prop, x, y, width, height); }
        public void SetMat4(uint id, ushort prop, in Matrix m) { Tick(); _inner.SetMat4(id, prop, m); }
        public void SetString(uint id, ushort prop, ReadOnlySpan<char> value) { Tick(); _inner.SetString(id, prop, value); }
        public void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values) { Tick(); _inner.SetF32Array(id, prop, values); }
        public void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points) { Tick(); _inner.SetPath(id, prop, verbs, points); }
        public void SetRef(uint id, ushort prop, uint refId) { Tick(); _inner.SetRef(id, prop, refId); }
        public void SetU64(uint id, ushort prop, ulong value) { Tick(); _inner.SetU64(id, prop, value); }
        public void SetVec2(uint id, ushort prop, float x, float y) { Tick(); _inner.SetVec2(id, prop, x, y); }
        public void SetIvec2(uint id, ushort prop, int x, int y) { Tick(); _inner.SetIvec2(id, prop, x, y); }

        public void BeginStyle(uint id, ushort flags) { Tick(); _inner.BeginStyle(id, flags); }
        public void StyleColor(ushort field, byte state, float r, float g, float b, float a) => _inner.StyleColor(field, state, r, g, b, a);
        public void StyleF32(ushort field, byte state, float value) => _inner.StyleF32(field, state, value);
        public void StyleI32(ushort field, byte state, int value) => _inner.StyleI32(field, state, value);
        public void StyleString(ushort field, byte state, ReadOnlySpan<char> value) => _inner.StyleString(field, state, value);
        public void EndStyle() => _inner.EndStyle();

        public void BeginUniforms(uint id, ushort flags) { Tick(); _inner.BeginUniforms(id, flags); }
        public void UniformF32(ReadOnlySpan<char> name, float value) => _inner.UniformF32(name, value);
        public void UniformI32(ReadOnlySpan<char> name, int value) => _inner.UniformI32(name, value);
        public void UniformBool(ReadOnlySpan<char> name, bool value) => _inner.UniformBool(name, value);
        public void UniformVec2(ReadOnlySpan<char> name, float x, float y) => _inner.UniformVec2(name, x, y);
        public void UniformVec3(ReadOnlySpan<char> name, float x, float y, float z) => _inner.UniformVec3(name, x, y, z);
        public void UniformVec4(ReadOnlySpan<char> name, float x, float y, float z, float w) => _inner.UniformVec4(name, x, y, z, w);
        public void UniformIvec2(ReadOnlySpan<char> name, int x, int y) => _inner.UniformIvec2(name, x, y);
        public void UniformColor(ReadOnlySpan<char> name, float r, float g, float b, float a) => _inner.UniformColor(name, r, g, b, a);
        public void UniformMat4(ReadOnlySpan<char> name, in Matrix m) => _inner.UniformMat4(name, m);
        public void UniformRef(ReadOnlySpan<char> name, uint refId) => _inner.UniformRef(name, refId);
        public void UniformU64(ReadOnlySpan<char> name, ulong value) => _inner.UniformU64(name, value);
        public void UniformSampler(ReadOnlySpan<char> name, ulong packed) => _inner.UniformSampler(name, packed);
        public void EndUniforms() => _inner.EndUniforms();

        public void BeginQmlProperties(uint id, ushort flags) { Tick(); _inner.BeginQmlProperties(id, flags); }
        public void QmlRect(ReadOnlySpan<char> name, float x, float y, float width, float height) => _inner.QmlRect(name, x, y, width, height);
        public void QmlString(ReadOnlySpan<char> name, ReadOnlySpan<char> value) => _inner.QmlString(name, value);
        public void EndQmlProperties() => _inner.EndQmlProperties();

        public void BeginEffectChain(uint id) { Tick(); _inner.BeginEffectChain(id); }
        public void BeginMultiLink() => _inner.BeginMultiLink();
        public void EffectBool(ushort prop, bool value) => _inner.EffectBool(prop, value);
        public void EffectF32(ushort prop, float value) => _inner.EffectF32(prop, value);
        public void EffectI32(ushort prop, int value) => _inner.EffectI32(prop, value);
        public void EffectColor(ushort prop, float r, float g, float b, float a) => _inner.EffectColor(prop, r, g, b, a);
        public void EffectRect(ushort prop, float x, float y, float width, float height) => _inner.EffectRect(prop, x, y, width, height);
        public void EffectRef(ushort prop, uint refId) => _inner.EffectRef(prop, refId);
        public void BeginShaderLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop,
            float paddingRight, float paddingBottom, float strength)
            => _inner.BeginShaderLink(template, shaderIds, paddingLeft, paddingTop, paddingRight, paddingBottom, strength);
        public void EndLink() => _inner.EndLink();
        public void EndEffectChain() => _inner.EndEffectChain();
    }
}
