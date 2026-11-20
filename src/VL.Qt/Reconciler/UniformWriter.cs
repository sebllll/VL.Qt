using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// One uniform as QtNative holds it: wire value type, the value and, for samplers, the resolved id (element
    /// child id for a texture, image resource id for an image). Value equality decides what is sent again.
    /// </summary>
    internal readonly record struct UniformValue(ushort ValueType, Matrix Numbers, int Integer, ulong Handle);

    /// <summary>
    /// Writes SET_UNIFORMS for a ShaderEffect as a diff by name against what was sent last: changed or new
    /// entries only, nothing at all if the table is equal. The protocol cannot remove a single name, so a removed
    /// uniform sends the whole table with <see cref="Protocol.UniformSet.Clear"/>. Entries with an invalid name (empty,
    /// more than <see cref="Protocol.UniformNameMax"/> UTF-8 bytes) are skipped; the node reports them.
    /// </summary>
    internal static class UniformWriter
    {
        /// <summary>Suffix of the table key of a sampler entry: a texture and its sampler share a name, so they need two keys.</summary>
        private const char SamplerKey = '\u0001';

        /// <summary>Whether <paramref name="name"/> can be sent (1..<see cref="Protocol.UniformNameMax"/> UTF-8 bytes).</summary>
        public static bool IsValidName(string? name)
            => !string.IsNullOrEmpty(name) && Encoding.UTF8.GetByteCount(name) <= Protocol.UniformNameMax;

        /// <summary>
        /// Sends the difference between the uniforms of <paramref name="effect"/> and <paramref name="sent"/> (null = nothing
        /// sent yet) and returns the table QtNative holds afterwards. <paramref name="children"/> are the nodes of the
        /// effect's children (same order as <see cref="Element.Children"/>); a texture of an ignored (duplicate) element
        /// is sent as 0 = none.
        /// </summary>
        public static Dictionary<string, UniformValue>? Write(
            ICommandSink sink, uint id, ShaderEffect effect, IReadOnlyList<Node> children, Dictionary<string, UniformValue>? sent)
        {
            Dictionary<string, UniformValue>? next = null;
            foreach (var u in effect.Uniforms)
            {
                if (u is null || !IsValidName(u.Name))
                    continue;
                next ??= new Dictionary<string, UniformValue>(StringComparer.Ordinal);
                if (u.ValueType != Protocol.Vt.Sampler)
                    next[u.Name] = Resolve(u, effect, children);
                if (u.Sampler is { } sampler)
                    next[u.Name + SamplerKey] = new UniformValue(Protocol.Vt.Sampler, default, 0, sampler.Pack());
            }
            if (effect.InputSampler is { } inputSampler)
            {
                next ??= new Dictionary<string, UniformValue>(StringComparer.Ordinal);
                next["source" + SamplerKey] = new UniformValue(Protocol.Vt.Sampler, default, 0, inputSampler.Pack());
            }

            bool clear = false;
            if (sent is not null)
            {
                foreach (var name in sent.Keys)
                {
                    if (next is null || !next.ContainsKey(name))
                    {
                        clear = true;
                        break;
                    }
                }
            }

            if (next is null)
            {
                if (clear)
                    Send(sink, id, Protocol.UniformSet.Clear, null, null);
                return null;
            }

            if (!clear && sent is not null && Equal(sent, next))
                return sent;

            Send(sink, id, clear ? Protocol.UniformSet.Clear : (ushort)0, next, clear ? null : sent);
            return next;
        }

        private static bool Equal(Dictionary<string, UniformValue> a, Dictionary<string, UniformValue> b)
        {
            if (a.Count != b.Count)
                return false;
            foreach (var (name, value) in b)
                if (!a.TryGetValue(name, out var other) || other != value)
                    return false;
            return true;
        }

        private static void Send(ICommandSink sink, uint id, ushort flags, Dictionary<string, UniformValue>? next, Dictionary<string, UniformValue>? skipEqualTo)
        {
            sink.BeginUniforms(id, flags);
            if (next is not null)
            {
                foreach (var (name, value) in next)
                {
                    if (skipEqualTo is not null && skipEqualTo.TryGetValue(name, out var old) && old == value)
                        continue;
                    WriteEntry(sink, name, value);
                }
            }
            sink.EndUniforms();
        }

        private static UniformValue Resolve(Uniform u, ShaderEffect effect, IReadOnlyList<Node> children)
        {
            ulong handle = 0;
            if (u.Texture is { } texture)
            {
                var kids = effect.Children;
                for (int i = 0; i < kids.Length && i < children.Count; i++)
                {
                    if (ReferenceEquals(kids[i], texture))
                    {
                        handle = children[i].Id; // 0 for a placeholder of a duplicate
                        break;
                    }
                }
            }
            else if (u.Image is { } image)
            {
                handle = image.Id;
            }
            return new UniformValue(u.ValueType, u.Numbers, u.Integer, handle);
        }

        internal static void WriteEntry(ICommandSink sink, string name, in UniformValue v)
        {
            var m = v.Numbers;
            switch (v.ValueType)
            {
                case Protocol.Vt.F32: sink.UniformF32(name, m.M11); break;
                case Protocol.Vt.I32: sink.UniformI32(name, v.Integer); break;
                case Protocol.Vt.Bool: sink.UniformBool(name, v.Integer != 0); break;
                case Protocol.Vt.Vec2: sink.UniformVec2(name, m.M11, m.M12); break;
                case Protocol.Vt.Vec3: sink.UniformVec3(name, m.M11, m.M12, m.M13); break;
                case Protocol.Vt.Vec4: sink.UniformVec4(name, m.M11, m.M12, m.M13, m.M14); break;
                case Protocol.Vt.Color: sink.UniformColor(name, m.M11, m.M12, m.M13, m.M14); break;
                case Protocol.Vt.Mat4: sink.UniformMat4(name, in m); break;
                case Protocol.Vt.Ref: sink.UniformRef(name, (uint)v.Handle); break;
                case Protocol.Vt.U64: sink.UniformU64(name, v.Handle); break;
                case Protocol.Vt.Sampler: sink.UniformSampler(name.TrimEnd(SamplerKey), v.Handle); break;
            }
        }
    }
}
