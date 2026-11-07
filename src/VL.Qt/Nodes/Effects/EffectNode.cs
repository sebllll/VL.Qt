using System;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Shared core of the effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), the counterpart of
    /// <see cref="InteractionNode{TProps}"/>: a new link only if the chain at Input (by reference) or the node specific
    /// <typeparamref name="TProps"/> changed, otherwise the last instance (reference equal → no QtNative ops). Effects have no
    /// sink: warnings (e.g. a duplicate kind) are shown at the element. No static state. Main thread only.
    /// </summary>
    /// <typeparam name="TProps">Node specific inputs; a readonly record struct (value equality, no allocation).</typeparam>
    internal sealed class EffectNode<TProps>
        where TProps : struct, IEquatable<TProps>
    {
        readonly Func<Effect?, TProps, Effect> _create;
        Effect? _last;
        Effect? _input;
        TProps _props;

        /// <param name="create">Builds a link; use a static lambda (no capture → no allocation).</param>
        public EffectNode(Func<Effect?, TProps, Effect> create) => _create = create;

        /// <summary>Number of links created so far (for tests).</summary>
        public int Created { get; private set; }

        /// <summary>Returns the link for the inputs: the last instance if nothing changed, else a new one. Call once per Update.</summary>
        public Effect Describe(Effect? input, in TProps props)
        {
            if (_last is null || !ReferenceEquals(_input, input) || !_props.Equals(props))
            {
                _last = _create(input, props);
                _input = input;
                _props = props;
                Created++;
            }
            return _last;
        }
    }
}
