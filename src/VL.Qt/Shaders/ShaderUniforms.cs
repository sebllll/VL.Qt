using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Qt.Elements;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Input pin of one uniform (or image) of a generated shader node. Remembers the value it last turned into a
    /// <see cref="Uniform"/>, so <see cref="ShaderUniforms.Update"/> builds a new spread only when a pin changed.
    /// </summary>
    internal abstract class UniformPin : IVLPin
    {
        /// <summary>The uniform of the current value (valid after construction and every <see cref="Update"/>).</summary>
        public Uniform Current { get; protected set; } = null!;

        /// <summary>Rebuilds <see cref="Current"/> if the value changed since the last call; true if it did.</summary>
        public abstract bool Update();

        public abstract object? Value { get; set; }

        /// <summary>Pin of the CLR type of <paramref name="pin"/>, starting at its default.</summary>
        public static UniformPin Create(ShaderPin pin) => pin.Type switch
        {
            ShaderPinType.Float => new UniformPin<float>(pin, s_float),
            ShaderPinType.Vector2 => new UniformPin<Vector2>(pin, s_vector2),
            ShaderPinType.Vector3 => new UniformPin<Vector3>(pin, s_vector3),
            ShaderPinType.Vector4 => new UniformPin<Vector4>(pin, s_vector4),
            ShaderPinType.Color => new UniformPin<Color4>(pin, s_color),
            ShaderPinType.Int => new UniformPin<int>(pin, s_int),
            ShaderPinType.Bool => new UniformPin<bool>(pin, s_bool),
            ShaderPinType.Matrix => new UniformPin<Matrix>(pin, s_matrix),
            ShaderPinType.Image => new UniformPin<QtImage?>(pin, s_image),
            ShaderPinType.Sampler => new UniformPin<Sampler?>(pin, s_sampler),
            _ => throw new ArgumentOutOfRangeException(nameof(pin), pin.Type, null),
        };

        /// <summary>CLR type of the pin of a <see cref="ShaderPinType"/>.</summary>
        public static Type ClrType(ShaderPinType type) => type switch
        {
            ShaderPinType.Float => typeof(float),
            ShaderPinType.Vector2 => typeof(Vector2),
            ShaderPinType.Vector3 => typeof(Vector3),
            ShaderPinType.Vector4 => typeof(Vector4),
            ShaderPinType.Color => typeof(Color4),
            ShaderPinType.Int => typeof(int),
            ShaderPinType.Bool => typeof(bool),
            ShaderPinType.Matrix => typeof(Matrix),
            ShaderPinType.Image => typeof(QtImage),
            ShaderPinType.Sampler => typeof(Sampler),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        static readonly Func<string, float, Uniform> s_float = Uniform.Float;
        static readonly Func<string, Vector2, Uniform> s_vector2 = Uniform.Vector2;
        static readonly Func<string, Vector3, Uniform> s_vector3 = Uniform.Vector3;
        static readonly Func<string, Vector4, Uniform> s_vector4 = Uniform.Vector4;
        static readonly Func<string, Color4, Uniform> s_color = Uniform.Color;
        static readonly Func<string, int, Uniform> s_int = Uniform.Int;
        static readonly Func<string, bool, Uniform> s_bool = Uniform.Bool;
        static readonly Func<string, Matrix, Uniform> s_matrix = Uniform.Matrix;
        static readonly Func<string, QtImage?, Uniform> s_image = static (n, i) => Uniform.FromImage(n, i);
        static readonly Func<string, Sampler?, Uniform> s_sampler = Uniform.FromSampler;
    }

    internal sealed class UniformPin<T> : UniformPin, IVLPin<T>
    {
        readonly string _uniformName;
        readonly Func<string, T, Uniform> _create;
        T _value, _built;

        public UniformPin(ShaderPin pin, Func<string, T, Uniform> create)
        {
            _uniformName = pin.UniformName;
            _create = create;
            _value = _built = pin.Default is T d ? d : default!;
            Current = create(_uniformName, _built);
        }

        public override object? Value
        {
            get => _value;
            set => _value = value is T t ? t : default!;
        }

        T? IVLPin<T>.Value
        {
            get => _value;
            set => _value = value!;
        }

        public override bool Update()
        {
            if (EqualityComparer<T>.Default.Equals(_value, _built))
                return false;
            _built = _value;
            Current = _create(_uniformName, _built);
            return true;
        }
    }

    /// <summary>The uniform pins of a generated shader node and the Spread&lt;Uniform&gt; handed to the ShaderEffect (rebuilt only on a pin change, nothing allocated otherwise).</summary>
    internal sealed class ShaderUniforms
    {
        readonly UniformPin[] _pins;
        Spread<Uniform> _spread;

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

        Spread<Uniform> Build()
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
