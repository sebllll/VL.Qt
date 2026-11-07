using System;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// A link of the Effect chain driven by shaders shipped with VL.Qt (<c>builtin:</c>, #438): template, shader ids,
    /// padding, strength and uniforms as <see cref="ShaderLink"/> describes them; padding and strength in DIP.
    /// </summary>
    internal sealed class BuiltinShaderLinkEffect : ShaderLink
    {
        public BuiltinShaderLinkEffect(Effect? previous, ushort template, ImmutableArray<ulong> shaderIds, Spread<Uniform> uniforms,
            RectangleF padding, float strength, string? strengthUniform)
            : base(previous, template, shaderIds, uniforms, padding, strength, strengthUniform)
        {
        }
    }

    /// <summary>Padding sums up in float: values like 60.000004 must not grow by a whole DIP.</summary>
    internal static class EffectPadding
    {
        /// <summary>Rounds up to whole DIP, ignoring float noise below 0.001.</summary>
        public static float Ceil(float dip) => MathF.Ceiling(dip - 0.001f);
    }

    /// <summary>
    /// Shared core of the shader effect nodes (Glow, InnerShadow, DirectionalBlur, ZoomBlur, RadialBlur, Displace): owns the
    /// shader resources of one node, builds a new link only if the chain at Input, a shader id or the node specific
    /// <typeparamref name="TProps"/> changed (the reconciler compares by reference). Without shader ids (still creating, failed,
    /// no resource client) the chain passes on unchanged and the problem shows at the node. Main thread only.
    /// </summary>
    /// <typeparam name="TProps">Node specific inputs; a readonly record struct (value equality, no allocation).</typeparam>
    internal sealed class BuiltinShaderLink<TProps> : IDisposable
        where TProps : struct, IEquatable<TProps>
    {
        const string phase = "Shader";

        readonly ShaderSlot[] _slots;
        readonly string[] _paths;
        readonly Func<Effect?, ImmutableArray<ulong>, TProps, Effect> _create;
        readonly NodeErrors? _errors;
        Effect? _last;
        Effect? _lastInput;
        ulong _id0, _id1;
        TProps _props;

        /// <param name="client">Creates the shader resources, may be null (tests): the chain then passes on unchanged.</param>
        /// <param name="shaders">Names of the built-in shaders by role (see <see cref="ShaderLink.ShaderIds"/>).</param>
        /// <param name="create">Builds the link; use a static lambda (no capture → no allocation).</param>
        /// <param name="errors">Shows shader problems at the node, may be null.</param>
        public BuiltinShaderLink(ResourceClient? client, string[] shaders, Func<Effect?, ImmutableArray<ulong>, TProps, Effect> create,
            NodeErrors? errors = null)
        {
            _create = create;
            _errors = errors;
            _paths = new string[shaders.Length];
            _slots = new ShaderSlot[shaders.Length];
            for (var i = 0; i < shaders.Length; i++)
            {
                _paths[i] = "builtin:" + shaders[i];
                _slots[i] = new ShaderSlot(client, Protocol.ShaderStage.Fragment, shaders[i]);
            }
        }

        /// <summary>Number of links created so far (tests).</summary>
        public int Created { get; private set; }

        /// <summary>The link for the inputs: the last instance if nothing changed, else a new one; <paramref name="input"/> while a shader is missing. Call once per Update.</summary>
        public Effect? Describe(Effect? input, in TProps props)
        {
            var id0 = _slots[0].Update(_paths[0], null);
            var id1 = _slots.Length > 1 ? _slots[1].Update(_paths[1], null) : 1;
            ReportErrors();
            if (id0 == 0 || id1 == 0)
            {
                _last = null;
                return input;
            }
            if (_last is null || !ReferenceEquals(input, _lastInput) || id0 != _id0 || id1 != _id1 || !_props.Equals(props))
            {
                _last = _create(input, _slots.Length > 1 ? ImmutableArray.Create(id0, id1) : ImmutableArray.Create(id0), props);
                _lastInput = input;
                _id0 = id0;
                _id1 = id1;
                _props = props;
                Created++;
            }
            return _last;
        }

        void ReportErrors()
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
