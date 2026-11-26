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
    internal sealed class UniformPin<T> : UniformPin, IVLPin<T>
    {
        private readonly string _uniformName;
        private readonly Func<string, T, Uniform> _create;
        private T _value, _built;

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
}
