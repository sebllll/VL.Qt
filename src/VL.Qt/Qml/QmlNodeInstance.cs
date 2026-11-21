using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.NodeFactories;
using VL.Qt.Nodes;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Instance of a generated QML node: a <see cref="QmlElementNode"/> fed with the file of the layout and a
    /// Dictionary built from the typed pins, its outputs split into typed pins. Per frame:
    /// <list type="number">
    /// <item>Properties dictionary: the entries of the Properties input, then the typed property inputs on top (a pin wins on a
    /// name clash). A typed input is in it once it differed from its declared default (<see cref="QmlInputPin"/>), a required one
    /// always. The dictionary instance is reused and changed only when an entry changes (QmlProperties compares content, no
    /// allocation per frame).</item>
    /// <item>QmlElementNode.Update(file, dictionary, common pins…).</item>
    /// <item>Property outputs: from its Properties output, coerced with <see cref="QmlTypes.TryCoerce"/>; until the first change and
    /// after a reload (the dictionary starts empty again) the declared default.</item>
    /// <item>Signal outputs: its Signals output this frame, by name: bool (any emission), Spread&lt;T&gt; of the only argument, or
    /// Spread&lt;QmlSignal&gt;; empty/false in a frame without emission.</item>
    /// <item>Output, Measured Size, Scene Bounds, Error passed on.</item>
    /// </list>
    /// Pins come in the order of <see cref="QmlNodeLayout.InputDescriptions"/> / <see cref="QmlNodeLayout.OutputDescriptions"/>.
    /// Hot reload of the content is QmlElementNode's (its watcher of the folder); new pins come with a new description.
    /// The instance owns its QmlElementNode and disposes it.
    /// </summary>
    internal sealed class QmlNodeInstance : IDisposable
    {
        private readonly QmlElementNode _element;
        private readonly Path _file;
        private readonly InputSlot[] _slots;
        private readonly PropertyOutput[] _propertyOutputs;
        private readonly SignalOutput[] _signalOutputs;

        private readonly ValuePin<Dictionary<string, object>?> _propertiesPin = new(null);
        private readonly ValuePin<Vector2> _size = new(new Vector2(1f, 1f));
        private readonly ValuePin<bool> _autoSize = new(true);
        private readonly ValuePin<Matrix> _transform = new(Matrix.Identity);
        private readonly ValuePin<RectangleAnchor> _anchor = new(RectangleAnchor.TopLeft);
        private readonly ValuePin<float> _opacity = new(1f);
        private readonly ValuePin<bool> _clip = new(false);
        private readonly ValuePin<bool> _debug = new(false);
        private readonly ValuePin<Interaction?> _interaction = new(null);
        private readonly ValuePin<Effect?> _effect = new(null);

        private readonly ValuePin<Element?> _output = new(null);
        private readonly ValuePin<Vector2> _measuredSize = new(Vector2.Zero);
        private readonly ValuePin<RectangleF> _sceneBounds = new(RectangleF.Empty);
        private readonly ValuePin<string> _error = new(string.Empty);

        private readonly IVLPin[] _inputs;
        private readonly IVLPin[] _outputs;

        // The dictionary handed to the element (reused), and the copy of the Properties input it was built from.
        private readonly Dictionary<string, object> _properties = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _planB = new(StringComparer.Ordinal);
        private Dictionary<string, object>? _lastChanged;
        private bool _signalsEmpty = true;

        public QmlNodeInstance(NodeContext nodeContext, QmlNodeLayout layout)
            : this(new QmlElementNode(nodeContext), layout)
        {
        }

        /// <summary>For tests: with a QmlElementNode built on its test constructor.</summary>
        internal QmlNodeInstance(QmlElementNode element, QmlNodeLayout layout)
        {
            _element = element;
            _file = new Path(layout.FilePath);

            _slots = new InputSlot[layout.PropertyInputs.Length];
            for (var i = 0; i < _slots.Length; i++)
                _slots[i] = InputSlot.Create(layout.PropertyInputs[i]);
            _propertyOutputs = new PropertyOutput[layout.PropertyOutputs.Length];
            for (var i = 0; i < _propertyOutputs.Length; i++)
                _propertyOutputs[i] = PropertyOutput.Create(layout.PropertyOutputs[i]);
            _signalOutputs = new SignalOutput[layout.SignalOutputs.Length];
            for (var i = 0; i < _signalOutputs.Length; i++)
                _signalOutputs[i] = SignalOutput.Create(layout.SignalOutputs[i]);

            var inputs = new List<IVLPin>(_slots.Length + 10);
            foreach (var slot in _slots)
                inputs.Add(slot.Pin);
            inputs.Add(_propertiesPin);
            inputs.AddRange(new IVLPin[] { _size, _autoSize, _transform, _anchor, _opacity, _clip, _debug, _interaction, _effect });
            _inputs = inputs.ToArray();

            var outputs = new List<IVLPin>(_propertyOutputs.Length + _signalOutputs.Length + 4) { _output };
            foreach (var property in _propertyOutputs)
                outputs.Add(property.Pin);
            foreach (var signal in _signalOutputs)
                outputs.Add(signal.Pin);
            outputs.AddRange(new IVLPin[] { _measuredSize, _sceneBounds, _error });
            _outputs = outputs.ToArray();
        }

        /// <summary>Input pins in the order of the description's inputs.</summary>
        internal IEnumerable<IVLPin> Inputs => _inputs;

        /// <summary>Output pins in the order of the description's outputs.</summary>
        internal IEnumerable<IVLPin> Outputs => _outputs;

        public IVLNode CreateNode(NodeBuilding.NodeInstanceBuildContext context) =>
            context.Node(Inputs, Outputs, update: Update, dispose: Dispose);

        public void Update()
        {
            var dirty = UpdatePlanB();
            foreach (var slot in _slots)
                dirty |= slot.Poll();
            if (dirty)
                RebuildProperties();

            var element = _element.Update(_file, _properties, _size.Value, _autoSize.Value, _transform.Value, _anchor.Value, _opacity.Value,
                _clip.Value, _debug.Value, _interaction.Value, _effect.Value,
                out var changed, out var signals, out var measuredSize, out var sceneBounds, out var error);

            _output.Value = element;
            _measuredSize.Value = measuredSize;
            _sceneBounds.Value = sceneBounds;
            _error.Value = error;

            // The element hands out a new dictionary whenever something changed (also on reload: empty).
            if (!ReferenceEquals(changed, _lastChanged))
            {
                _lastChanged = changed;
                foreach (var output in _propertyOutputs)
                    output.Apply(changed);
            }

            UpdateSignals(signals);
        }

        /// <summary>Copies the Properties input when its content differs from the last copy; true if it did.</summary>
        private bool UpdatePlanB()
        {
            var source = _propertiesPin.Value;
            var count = source?.Count ?? 0;
            var same = count == _planB.Count;
            if (same && count > 0)
            {
                foreach (var (name, value) in source!)
                {
                    if (!_planB.TryGetValue(name, out var mine) || !Equals(mine, value))
                    {
                        same = false;
                        break;
                    }
                }
            }
            if (same)
                return false;
            _planB.Clear();
            if (source != null)
            {
                foreach (var (name, value) in source)
                    _planB[name] = value;
            }
            return true;
        }

        private void RebuildProperties()
        {
            _properties.Clear();
            foreach (var (name, value) in _planB)
                _properties[name] = value;
            // A typed pin wins over a Properties entry of the same name.
            foreach (var slot in _slots)
            {
                if (slot.IsSent)
                    _properties[slot.PropertyName] = slot.Boxed!;
            }
        }

        private void UpdateSignals(Spread<QmlSignal> signals)
        {
            if (signals.Count == 0)
            {
                if (_signalsEmpty)
                    return;
                _signalsEmpty = true;
                foreach (var output in _signalOutputs)
                    output.Clear();
                return;
            }
            _signalsEmpty = false;
            foreach (var output in _signalOutputs)
                output.Apply(signals);
        }

        public void Dispose() => _element.Dispose();

        /// <summary>Typed input pin of a property: the send rule of <see cref="QmlInputPin"/>.</summary>
        private abstract class InputSlot
        {
            public string PropertyName { get; protected init; } = string.Empty;
            public bool IsSent { get; protected set; }
            public object? Boxed { get; protected set; }
            public abstract IVLPin Pin { get; }

            /// <summary>Reads the pin; true if the entry of the dictionary changed (appeared or got a new value).</summary>
            public abstract bool Poll();

            public static InputSlot Create(QmlInputPin pin)
                => (InputSlot)Activator.CreateInstance(typeof(InputSlot<>).MakeGenericType(pin.Property.ClrType), pin)!;
        }

        private sealed class InputSlot<T> : InputSlot
        {
            private readonly ValuePin<T> _pin;
            private readonly bool _alwaysSend;
            private readonly T _default;
            private T _last;

            public InputSlot(QmlInputPin pin)
            {
                PropertyName = pin.Property.Name;
                _alwaysSend = pin.AlwaysSend;
                _default = QmlTypes.TryCoerce(pin.Property.Kind, pin.Property.Default, out var d) ? (T)d : default!;
                _last = _default;
                _pin = new ValuePin<T>(_default);
            }

            public override IVLPin Pin => _pin;

            public override bool Poll()
            {
                var value = _pin.Value;
                // A pin that gets no value (null string) counts as the empty string, which QML knows.
                if (value is null && typeof(T) == typeof(string))
                    value = (T)(object)string.Empty;
                if (!IsSent)
                {
                    if (!_alwaysSend && EqualityComparer<T>.Default.Equals(value, _default))
                        return false;
                    IsSent = true;
                }
                else if (EqualityComparer<T>.Default.Equals(value, _last))
                    return false;
                _last = value;
                Boxed = value;
                return true;
            }
        }

        /// <summary>Output pin of a property: the last value QML changed, else the declared default.</summary>
        private abstract class PropertyOutput
        {
            public abstract IVLPin Pin { get; }

            /// <summary>Takes the value of the property from <paramref name="changed"/> (QML names), the default if it is not in it or does not fit.</summary>
            public abstract void Apply(Dictionary<string, object> changed);

            public static PropertyOutput Create(QmlOutputPin pin)
                => (PropertyOutput)Activator.CreateInstance(typeof(PropertyOutput<>).MakeGenericType(pin.Property.ClrType), pin)!;
        }

        private sealed class PropertyOutput<T> : PropertyOutput
        {
            private readonly ValuePin<T> _pin;
            private readonly QmlPropertyDecl _property;
            private readonly T _default;

            public PropertyOutput(QmlOutputPin pin)
            {
                _property = pin.Property;
                _default = QmlTypes.TryCoerce(_property.Kind, _property.Default, out var d) ? (T)d : default!;
                _pin = new ValuePin<T>(_default);
            }

            public override IVLPin Pin => _pin;

            public override void Apply(Dictionary<string, object> changed)
                => _pin.Value = changed.TryGetValue(_property.Name, out var value) && QmlTypes.TryCoerce(_property.Kind, value, out var coerced) ? (T)coerced : _default;
        }

        /// <summary>Output pin of a signal, see <see cref="QmlTypes.SignalPinType"/>.</summary>
        private abstract class SignalOutput
        {
            public abstract IVLPin Pin { get; }

            /// <summary>Back to false/empty.</summary>
            public abstract void Clear();

            /// <summary>Takes the emissions of this signal from <paramref name="signals"/> (all emissions of the frame).</summary>
            public abstract void Apply(Spread<QmlSignal> signals);

            public static SignalOutput Create(QmlSignalPin pin) => pin.Signal.Shape switch
            {
                QmlSignalShape.Bang => new BangOutput(pin),
                QmlSignalShape.Values => pin.ArgumentKind is { } kind
                    ? (SignalOutput)Activator.CreateInstance(typeof(ValuesOutput<>).MakeGenericType(QmlTypes.ClrType(kind)), pin, kind)!
                    : new ValuesOutput<object>(pin, null),
                _ => new SignalsOutput(pin),
            };
        }

        private sealed class BangOutput : SignalOutput
        {
            private readonly ValuePin<bool> _pin = new(false);
            private readonly string _name;

            public BangOutput(QmlSignalPin pin) => _name = pin.Signal.Name;

            public override IVLPin Pin => _pin;

            public override void Clear() => _pin.Value = false;

            public override void Apply(Spread<QmlSignal> signals)
            {
                var fired = false;
                foreach (var signal in signals)
                {
                    if (signal.Name == _name)
                    {
                        fired = true;
                        break;
                    }
                }
                _pin.Value = fired;
            }
        }

        private sealed class ValuesOutput<T> : SignalOutput
        {
            private readonly ValuePin<Spread<T>> _pin = new(Spread<T>.Empty);
            private readonly string _name;
            private readonly QmlValueKind? _kind;

            public ValuesOutput(QmlSignalPin pin, QmlValueKind? kind)
            {
                _name = pin.Signal.Name;
                _kind = kind;
            }

            public override IVLPin Pin => _pin;

            public override void Clear() => _pin.Value = Spread<T>.Empty;

            public override void Apply(Spread<QmlSignal> signals)
            {
                SpreadBuilder<T>? builder = null;
                foreach (var signal in signals)
                {
                    if (signal.Name != _name || signal.Arguments.Count == 0)
                        continue;
                    var argument = signal.Arguments[0];
                    object? value = argument;
                    // An argument that does not fit its declared type is left out, like a property value at the output.
                    if (_kind is { } kind)
                    {
                        if (!QmlTypes.TryCoerce(kind, argument, out var coerced))
                            continue;
                        value = coerced;
                    }
                    (builder ??= new SpreadBuilder<T>()).Add((T)value!);
                }
                _pin.Value = builder is null ? Spread<T>.Empty : builder.ToSpread();
            }
        }

        private sealed class SignalsOutput : SignalOutput
        {
            private readonly ValuePin<Spread<QmlSignal>> _pin = new(Spread<QmlSignal>.Empty);
            private readonly string _name;

            public SignalsOutput(QmlSignalPin pin) => _name = pin.Signal.Name;

            public override IVLPin Pin => _pin;

            public override void Clear() => _pin.Value = Spread<QmlSignal>.Empty;

            public override void Apply(Spread<QmlSignal> signals)
            {
                SpreadBuilder<QmlSignal>? builder = null;
                foreach (var signal in signals)
                {
                    if (signal.Name == _name)
                        (builder ??= new SpreadBuilder<QmlSignal>()).Add(signal);
                }
                _pin.Value = builder is null ? Spread<QmlSignal>.Empty : builder.ToSpread();
            }
        }
    }
}
