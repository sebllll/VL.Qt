using System;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// The memo of every chain node (effects, interactions, paths): remembers the last link and returns it while the input
    /// link (by reference) and the node specific <typeparamref name="TProps"/> are equal, so a chain stays reference-stable between
    /// frames and the reconciler can skip unchanged parts. A <c>static</c> create delegate keeps <c>Get</c> free of allocations.
    /// A mutable struct: keep it in a non-readonly field of the node. Main thread only.
    /// </summary>
    /// <typeparam name="TEffect">The link type of the chain (<see cref="Elements.Effect"/>, <see cref="Elements.Interaction"/>,
    /// <see cref="Paths.QtPath"/>).</typeparam>
    /// <typeparam name="TProps">Node specific inputs; a readonly record struct (value equality, no allocation).</typeparam>
    internal struct ChainMemo<TEffect, TProps>
        where TEffect : class
        where TProps : struct, IEquatable<TProps>
    {
        private TEffect? _input;
        private TProps _props;
        private TEffect? _output;

        /// <summary>Number of links created so far (for tests).</summary>
        public int Created { get; private set; }

        /// <summary>Forgets the last link, so the next <c>Get</c> builds a new one (e.g. while the node passes its input on, or on dispose).</summary>
        public void Reset()
        {
            _input = null;
            _props = default;
            _output = null;
        }

        /// <summary>Returns the link for the inputs: the last instance if nothing changed, else a new one from <paramref name="create"/>.
        /// Call once per Update.</summary>
        public TEffect Get(TEffect? input, in TProps props, Func<TEffect?, TProps, TEffect> create)
        {
            if (_output is null || !ReferenceEquals(_input, input) || !_props.Equals(props))
            {
                _output = create(input, props);
                _input = input;
                _props = props;
                Created++;
            }
            return _output;
        }

        /// <summary>Same with a state that <paramref name="create"/> needs and that is no part of the comparison (e.g. the feedback slot).</summary>
        public TEffect Get<TState>(TEffect? input, in TProps props, TState state, Func<TEffect?, TProps, TState, TEffect> create)
        {
            if (_output is null || !ReferenceEquals(_input, input) || !_props.Equals(props))
            {
                _output = create(input, props, state);
                _input = input;
                _props = props;
                Created++;
            }
            return _output;
        }
    }
}
