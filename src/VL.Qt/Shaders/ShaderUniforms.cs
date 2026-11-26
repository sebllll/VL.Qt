using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Images;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// The uniform pins of a generated shader node and the Spread&lt;Uniform&gt; handed to the Shader (rebuilt only on a pin
    /// change, nothing allocated otherwise).
    /// </summary>
    internal sealed class ShaderUniforms
    {
        private readonly UniformPin[] _pins;
        private Spread<Uniform> _spread;

        public ShaderUniforms(ImmutableArray<ShaderPin> pins)
        {
            _pins = new UniformPin[pins.Length];
            for (var i = 0; i < _pins.Length; i++)
                _pins[i] = UniformPin.Create(pins[i]);
            _spread = Build();
        }

        /// <summary>Input pins in pin order.</summary>
        public IReadOnlyList<UniformPin> Pins => _pins;

        /// <summary>Current uniforms; the same instance as long as no pin changed.</summary>
        public Spread<Uniform> Update()
        {
            var changed = false;
            foreach (var pin in _pins)
                changed |= pin.Update();
            if (changed)
                _spread = Build();
            return _spread;
        }

        private Spread<Uniform> Build()
        {
            if (_pins.Length == 0)
                return Spread<Uniform>.Empty;
            var builder = new SpreadBuilder<Uniform>(_pins.Length);
            foreach (var pin in _pins)
                builder.Add(pin.Current);
            return builder.ToSpread();
        }
    }
}
