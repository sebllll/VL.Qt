using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>
    /// Fake <see cref="ICommandSink"/> for reconciler tests: records every command as a readable line, e.g.
    /// <c>CREATE 1 Rectangle</c>, <c>REPARENT 1 0 0</c>, <c>SET 1 Color color(1,0,0,1)</c>, <c>BIND 1 5 2</c>, <c>DESTROY 1</c>.
    /// Element types and properties print by their <see cref="Protocol.ElementType"/> / <see cref="Protocol.PropertyId"/> (<see cref="Protocol.MultiEffectKey"/>) name (the first
    /// name of a value, see <c>ProtocolNamesAreUnique</c>), so a renumbered protocol leaves the expectations valid.
    /// Numbers use the invariant culture; floats in round-trip format ("R").
    /// </summary>
    internal sealed class RecordingSink : ICommandSink
    {
        private static readonly Dictionary<ushort, string> s_typeNames = NamesOf(typeof(Protocol.ElementType));
        // The keys of the MULTI entries (832..855) are no properties of SET, but print by name too.
        private static readonly Dictionary<ushort, string> s_propNames = Merge(NamesOf(typeof(Protocol.PropertyId)), NamesOf(typeof(Protocol.MultiEffectKey)));

        private static Dictionary<ushort, string> Merge(Dictionary<ushort, string> first, Dictionary<ushort, string> second)
        {
            foreach (var (value, name) in second)
                first.TryAdd(value, name);
            return first;
        }

        /// <summary>Maps each value of the ushort constants of <paramref name="holder"/> to its first name (declaration order).</summary>
        internal static Dictionary<ushort, string> NamesOf(Type holder)
        {
            var map = new Dictionary<ushort, string>();
            foreach (var f in holder.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(ushort)).OrderBy(f => f.MetadataToken))
                map.TryAdd((ushort)f.GetRawConstantValue()!, f.Name);
            return map;
        }

        /// <summary>All names of the ushort constants of <paramref name="holder"/>, grouped by value.</summary>
        internal static ILookup<ushort, string> AllNamesOf(Type holder) =>
            holder.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(ushort))
                .ToLookup(f => (ushort)f.GetRawConstantValue()!, f => f.Name);

        private static string TypeName(ushort type) => s_typeNames.TryGetValue(type, out var n) ? n : type.ToString(CultureInfo.InvariantCulture);
        internal static string PropName(ushort prop) => s_propNames.TryGetValue(prop, out var n) ? n : prop.ToString(CultureInfo.InvariantCulture);

        public List<string> Commands { get; } = new();

        public int Count => Commands.Count;

        public void Clear() => Commands.Clear();

        /// <summary>Commands starting with the given op name ("CREATE", "SET", ...).</summary>
        public IEnumerable<string> OfOp(string op)
        {
            foreach (var c in Commands)
                if (c.StartsWith(op + " ", StringComparison.Ordinal))
                    yield return c;
        }

        public override string ToString() => string.Join("\n", Commands);

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private void Add(string s) => Commands.Add(s);

        private void AddSet(uint id, ushort prop, string value) => Add($"SET {id} {PropName(prop)} {value}");

        public void Create(uint id, ushort type) => Add($"CREATE {id} {TypeName(type)}");
        public void Destroy(uint id) => Add($"DESTROY {id}");
        public void Reparent(uint id, uint parent, uint index) => Add($"REPARENT {id} {parent} {index}");
        public void Bind(uint id, uint sinkHandle, ushort flags) => Add($"BIND {id} {sinkHandle} {flags}");

        public void SetF32(uint id, ushort prop, float value) => AddSet(id, prop, $"f32({F(value)})");
        public void SetI32(uint id, ushort prop, int value) => AddSet(id, prop, $"i32({value})");
        public void SetBool(uint id, ushort prop, bool value) => AddSet(id, prop, value ? "bool(1)" : "bool(0)");
        public void SetColor(uint id, ushort prop, float r, float g, float b, float a) => AddSet(id, prop, $"color({F(r)},{F(g)},{F(b)},{F(a)})");
        public void SetVec2(uint id, ushort prop, float x, float y) => AddSet(id, prop, $"vec2({F(x)},{F(y)})");
        public void SetRect(uint id, ushort prop, float x, float y, float width, float height) => AddSet(id, prop, $"rect({F(x)},{F(y)},{F(width)},{F(height)})");

        public void SetMat4(uint id, ushort prop, in Matrix m)
        {
            var a = m.ToArray(); // M11, M12, ..., M44 (same order as the protocol)
            AddSet(id, prop, "mat4(" + string.Join(",", Array.ConvertAll(a, F)) + ")");
        }

        public void SetString(uint id, ushort prop, ReadOnlySpan<char> value) => AddSet(id, prop, $"string({value.ToString()})");
        public void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values) => AddSet(id, prop, "f32array(" + string.Join(",", Array.ConvertAll(values.ToArray(), F)) + ")");
        public void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points) =>
            AddSet(id, prop, "path(" + string.Join(",", verbs.ToArray()) + ";" + string.Join(",", Array.ConvertAll(points.ToArray(), F)) + ")");
        public void SetRef(uint id, ushort prop, uint refId) => AddSet(id, prop, $"ref({refId})");
        public void SetU64(uint id, ushort prop, ulong value) => AddSet(id, prop, $"u64({value})");

        // SET_STYLE is recorded as one line: "STYLE id flags field/state=value ...".
        private readonly System.Text.StringBuilder _style = new();

        public void BeginStyle(uint id, ushort flags) => _style.Clear().Append($"STYLE {id} {flags}");
        public void StyleColor(ushort field, byte state, float r, float g, float b, float a) => _style.Append($" {field}/{state}=color({F(r)},{F(g)},{F(b)},{F(a)})");
        public void StyleF32(ushort field, byte state, float value) => _style.Append($" {field}/{state}=f32({F(value)})");
        public void StyleI32(ushort field, byte state, int value) => _style.Append($" {field}/{state}=i32({value})");
        public void StyleString(ushort field, byte state, ReadOnlySpan<char> value) => _style.Append($" {field}/{state}=string({value.ToString()})");
        public void EndStyle() => Add(_style.ToString());

        public void SetIvec2(uint id, ushort prop, int x, int y) => AddSet(id, prop, $"ivec2({x},{y})");

        // SET_UNIFORMS is recorded as one line: "UNIFORMS id flags name=value ...".
        private readonly System.Text.StringBuilder _uniforms = new();

        public void BeginUniforms(uint id, ushort flags) => _uniforms.Clear().Append($"UNIFORMS {id} {flags}");
        public void UniformF32(ReadOnlySpan<char> name, float value) => _uniforms.Append($" {name.ToString()}=f32({F(value)})");
        public void UniformI32(ReadOnlySpan<char> name, int value) => _uniforms.Append($" {name.ToString()}=i32({value})");
        public void UniformBool(ReadOnlySpan<char> name, bool value) => _uniforms.Append($" {name.ToString()}=bool({(value ? 1 : 0)})");
        public void UniformVec2(ReadOnlySpan<char> name, float x, float y) => _uniforms.Append($" {name.ToString()}=vec2({F(x)},{F(y)})");
        public void UniformVec3(ReadOnlySpan<char> name, float x, float y, float z) => _uniforms.Append($" {name.ToString()}=vec3({F(x)},{F(y)},{F(z)})");
        public void UniformVec4(ReadOnlySpan<char> name, float x, float y, float z, float w) => _uniforms.Append($" {name.ToString()}=vec4({F(x)},{F(y)},{F(z)},{F(w)})");
        public void UniformIvec2(ReadOnlySpan<char> name, int x, int y) => _uniforms.Append($" {name.ToString()}=ivec2({x},{y})");
        public void UniformColor(ReadOnlySpan<char> name, float r, float g, float b, float a) => _uniforms.Append($" {name.ToString()}=color({F(r)},{F(g)},{F(b)},{F(a)})");
        public void UniformMat4(ReadOnlySpan<char> name, in Matrix m) => _uniforms.Append($" {name.ToString()}=mat4({string.Join(",", Array.ConvertAll(m.ToArray(), F))})");
        public void UniformRef(ReadOnlySpan<char> name, uint refId) => _uniforms.Append($" {name.ToString()}=ref({refId})");
        public void UniformU64(ReadOnlySpan<char> name, ulong value) => _uniforms.Append($" {name.ToString()}=u64({value})");
        public void UniformSampler(ReadOnlySpan<char> name, ulong packed) => _uniforms.Append($" {name.ToString()}=sampler({packed:x})");
        public void EndUniforms() => Add(_uniforms.ToString());

        // SET_QML_PROPERTIES is recorded as one line: "QML id flags name=value ..."; the entries go through the Uniform*
        // methods (and QmlRect / QmlString) into the same builder.
        public void BeginQmlProperties(uint id, ushort flags) => _uniforms.Clear().Append($"QML {id} {flags}");
        public void QmlRect(ReadOnlySpan<char> name, float x, float y, float width, float height) => _uniforms.Append($" {name.ToString()}=rect({F(x)},{F(y)},{F(width)},{F(height)})");
        public void QmlString(ReadOnlySpan<char> name, ReadOnlySpan<char> value) => _uniforms.Append($" {name.ToString()}=string({value.ToString()})");
        public void EndQmlProperties() => Add(_uniforms.ToString());

        // SET_EFFECT_CHAIN is recorded as one line: "CHAIN id | multi prop=value ... | shader template ids pad=(l,t,r,b) strength=x name=value ...".
        // Uniform entries of a shader link go through the Uniform* methods into the same builder.
        public void BeginEffectChain(uint id) => _uniforms.Clear().Append($"CHAIN {id}");
        public void BeginMultiLink() => _uniforms.Append(" | multi");
        public void EffectBool(ushort prop, bool value) => _uniforms.Append($" {PropName(prop)}=bool({(value ? 1 : 0)})");
        public void EffectF32(ushort prop, float value) => _uniforms.Append($" {PropName(prop)}=f32({F(value)})");
        public void EffectI32(ushort prop, int value) => _uniforms.Append($" {PropName(prop)}=i32({value})");
        public void EffectColor(ushort prop, float r, float g, float b, float a) => _uniforms.Append($" {PropName(prop)}=color({F(r)},{F(g)},{F(b)},{F(a)})");
        public void EffectRect(ushort prop, float x, float y, float width, float height) => _uniforms.Append($" {PropName(prop)}=rect({F(x)},{F(y)},{F(width)},{F(height)})");
        public void EffectRef(ushort prop, uint refId) => _uniforms.Append($" {PropName(prop)}=ref({refId})");

        public void BeginEffectChainLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop,
            float paddingRight, float paddingBottom, float strength)
            => _uniforms.Append($" | shader {template} [{string.Join(",", shaderIds.ToArray())}] pad=({F(paddingLeft)},{F(paddingTop)},{F(paddingRight)},{F(paddingBottom)}) strength={F(strength)}");

        public void EndLink() { }
        public void EndEffectChain() => Add(_uniforms.ToString());
    }
}
