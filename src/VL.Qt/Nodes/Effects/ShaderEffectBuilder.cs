using System;
using System.Collections.Immutable;
using VL.Qt.Images;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// One shader of a <see cref="ShaderEffect"/>: either a path (<c>builtin:&lt;name&gt;</c>, shipped with VL.Qt) or GLSL
    /// source (a shader file), with the label that starts its error text.
    /// </summary>
    internal readonly record struct ShaderEffectSource(string Label, string? Path, string? Code)
    {
        /// <summary>A shader shipped with VL.Qt.</summary>
        public static ShaderEffectSource Builtin(string name) => new(name, ShaderSlot.builtinShaderPrefix + name, null);

        /// <summary>A fragment shader given as source text.</summary>
        public static ShaderEffectSource Fragment(string code) => new("Fragment shader", null, code);
    }

    /// <summary>
    /// Shared core of the shader effect nodes, built-in (Glow, InnerShadow, DirectionalBlur, ZoomBlur, RadialBlur, Displace, Blend)
    /// and generated from shader files: owns the shader resources of one node, builds a new link only if the chain at Input, a shader id or the node specific
    /// <typeparamref name="TProps"/> changed (the reconciler compares by reference). Without shader ids (still creating, failed,
    /// no resource client) the chain passes on unchanged and the problem shows at the node (one <see cref="NodeErrors"/> entry). Main thread only.
    /// </summary>
    /// <typeparam name="TProps">Node specific inputs; a readonly record struct (value equality, no allocation).</typeparam>
    internal sealed class ShaderEffectBuilder<TProps> : IDisposable
        where TProps : struct, IEquatable<TProps>
    {
        private const string phase = "Shader";

        private readonly ShaderSlot[] _slots;
        private readonly ShaderEffectSource[] _sources;
        private readonly Func<Effect?, ImmutableArray<ulong>, TProps, ShaderEffect> _create;
        private readonly NodeErrors? _errors;
        private ChainMemo<Effect, Key> _memo;

        // The shader ids belong to the comparison: a recreated shader gives the link a new id.
        private readonly record struct Key(ulong Id0, ulong Id1, TProps Props);

        /// <param name="client">Creates the shader resources, may be null (tests): the chain then passes on unchanged.</param>
        /// <param name="shaders">The shaders by role (see <see cref="ShaderEffect.ShaderIds"/>).</param>
        /// <param name="create">Builds the link; use a static lambda (no capture → no allocation).</param>
        /// <param name="errors">Shows shader problems at the node, may be null.</param>
        public ShaderEffectBuilder(ResourceClient? client, ShaderEffectSource[] shaders, Func<Effect?, ImmutableArray<ulong>, TProps, ShaderEffect> create,
            NodeErrors? errors = null)
        {
            _create = create;
            _errors = errors;
            _sources = shaders;
            _slots = new ShaderSlot[shaders.Length];
            for (var i = 0; i < shaders.Length; i++)
                _slots[i] = new ShaderSlot(client, Protocol.ShaderStage.Fragment, shaders[i].Label);
        }

        /// <summary>Number of links created so far (tests).</summary>
        public int Created => _memo.Created;

        /// <summary>The link for the inputs: the last instance if nothing changed, else a new one; <paramref name="input"/> while a shader
        /// is missing. Call once per Update.</summary>
        public Effect? Describe(Effect? input, in TProps props)
        {
            var id0 = _slots[0].Update(_sources[0].Path, _sources[0].Code);
            var id1 = _slots.Length > 1 ? _slots[1].Update(_sources[1].Path, _sources[1].Code) : 1;
            ReportErrors();
            if (id0 == 0 || id1 == 0)
            {
                _memo.Reset();
                return input;
            }
            return _memo.Get(input, new Key(id0, id1, props), this, static (previous, key, self) => self._create(
                previous, self._slots.Length > 1 ? ImmutableArray.Create(key.Id0, key.Id1) : ImmutableArray.Create(key.Id0), key.Props));
        }

        private void ReportErrors()
        {
            if (_errors is null)
                return;
            foreach (var slot in _slots)
            {
                var error = slot.Error;
                if (error.Length > 0)
                {
                    _errors.Warn(error, phase);
                    return;
                }
            }
            _errors.Clear(phase);
        }

        public void Dispose()
        {
            foreach (var slot in _slots)
                slot.Dispose();
            _errors?.Dispose();
        }
    }
}
