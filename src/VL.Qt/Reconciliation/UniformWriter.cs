using System.Collections.Generic;
using VL.Qt.Elements;
using VL.Qt.Shaders;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Writes SET_UNIFORMS for a Shader as a diff by name against what was sent last: changed or new
    /// entries only, nothing at all if the table is equal. The protocol cannot remove a single name, so a removed
    /// uniform sends the whole table with <see cref="Protocol.UniformSet.Clear"/>. Entries with an invalid name (empty,
    /// more than <see cref="Protocol.UniformNameMax"/> UTF-8 bytes) are skipped; the node reports them.
    /// </summary>
    internal static class UniformWriter
    {
        /// <summary>The state of <paramref name="sampler"/> as the wire carries it (protocol.h "Sampler"): bytes filter, mip filter, address U, address V.</summary>
        public static ulong PackSampler(Sampler sampler)
            => (ulong)sampler.Filter
                | (ulong)sampler.MipFilter << 8
                | (ulong)sampler.AddressU << 16
                | (ulong)sampler.AddressV << 24;

        /// <summary>
        /// Sends the difference between the uniforms of <paramref name="effect"/> and what <paramref name="mirror"/> says was
        /// sent. <paramref name="children"/> are the mirrors of the effect's children (same order as <see cref="Element.ChildItems"/>);
        /// a texture whose mirror is a placeholder (a video shown elsewhere) is sent as 0 = none. The new table is built in
        /// <see cref="ShaderMirror.Scratch"/> and replaces <see cref="ShaderMirror.Sent"/> only when something was sent, so a visit allocates nothing.
        /// </summary>
        public static void Write(ICommandSink sink, uint id, ShaderMirror mirror, Shader effect, IReadOnlyList<ItemMirror> children)
        {
            var sent = mirror.Sent;
            var next = mirror.Scratch;
            next.Clear();
            foreach (var u in effect.Uniforms)
            {
                // Reserved names are an error at the node (ShaderNode); QtNative sets the automatic ones itself.
                if (u is null || !Protocol.IsValidName(u.Name) || ReservedUniforms.ItemAutomatic.Contains(u.Name))
                    continue;
                if (u.ValueType != Protocol.ValueType.Sampler)
                    next[UniformKey.Of(u)] = Resolve(u, effect, children);
                if (u.Sampler is { } sampler)
                    next[new UniformKey(u.Name, true)] = new UniformValue(Protocol.ValueType.Sampler, default, 0, PackSampler(sampler));
            }
            if (effect.InputSampler is { } inputSampler)
                next[new UniformKey(ReservedUniforms.InputSampler, true)] = new UniformValue(Protocol.ValueType.Sampler, default, 0, PackSampler(inputSampler));

            var clear = false;
            foreach (var key in sent.Keys)
            {
                if (!next.ContainsKey(key))
                {
                    clear = true;
                    break;
                }
            }

            if (!clear && Equal(sent, next))
                return;

            Send(sink, id, clear ? Protocol.UniformSet.Clear : (ushort)0, next, clear ? null : sent);
            mirror.Commit();
        }

        private static bool Equal(Dictionary<UniformKey, UniformValue> a, Dictionary<UniformKey, UniformValue> b)
        {
            if (a.Count != b.Count)
                return false;
            foreach (var (key, value) in b)
                if (!a.TryGetValue(key, out var other) || other != value)
                    return false;
            return true;
        }

        private static void Send(ICommandSink sink, uint id, ushort flags, Dictionary<UniformKey, UniformValue> next, Dictionary<UniformKey, UniformValue>? skipEqualTo)
        {
            sink.BeginUniforms(id, flags);
            foreach (var (key, value) in next)
            {
                if (skipEqualTo is not null && skipEqualTo.TryGetValue(key, out var old) && old == value)
                    continue;
                WriteEntry(sink, key.Name, value);
            }
            sink.EndUniforms();
        }

        private static UniformValue Resolve(Uniform u, Shader effect, IReadOnlyList<ItemMirror> children)
        {
            ulong handle = 0;
            if (u.Texture is { } texture)
            {
                var kids = effect.ChildItems;
                for (int i = 0; i < kids.Length && i < children.Count; i++)
                {
                    if (ReferenceEquals(kids[i], texture))
                    {
                        handle = children[i].Id; // 0 for a placeholder
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
                case Protocol.ValueType.F32: sink.UniformF32(name, m.M11); break;
                case Protocol.ValueType.I32: sink.UniformI32(name, v.Integer); break;
                case Protocol.ValueType.Bool: sink.UniformBool(name, v.Integer != 0); break;
                case Protocol.ValueType.Vec2: sink.UniformVec2(name, m.M11, m.M12); break;
                case Protocol.ValueType.Vec3: sink.UniformVec3(name, m.M11, m.M12, m.M13); break;
                case Protocol.ValueType.Vec4: sink.UniformVec4(name, m.M11, m.M12, m.M13, m.M14); break;
                case Protocol.ValueType.Color: sink.UniformColor(name, m.M11, m.M12, m.M13, m.M14); break;
                case Protocol.ValueType.Mat4: sink.UniformMat4(name, in m); break;
                case Protocol.ValueType.Ref: sink.UniformRef(name, (uint)v.Handle); break;
                case Protocol.ValueType.U64: sink.UniformU64(name, v.Handle); break;
                case Protocol.ValueType.Sampler: sink.UniformSampler(name, v.Handle); break;
            }
        }
    }
}
