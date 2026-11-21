using VL.Core;

namespace VL.Qt.NodeFactories
{
    /// <summary>A plain input/output pin of a generated node instance.</summary>
    internal sealed class ValuePin<T> : IVLPin<T>
    {
        public ValuePin(T value) => Value = value;

        public T Value { get; set; }

        T? IVLPin<T>.Value
        {
            get => Value;
            set => Value = value!;
        }

        object? IVLPin.Value
        {
            get => Value;
            set => Value = value is T t ? t : default!;
        }
    }
}
