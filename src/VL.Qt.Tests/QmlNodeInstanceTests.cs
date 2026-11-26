using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Qml;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>QmlNodeInstance: typed pins to dictionary (send rule), property and signal outputs, passthrough.</summary>
    public class QmlNodeInstanceTests
    {
        private string _dir = null!;
        private string _file = null!;
        private readonly Subject<FileSystemEventArgs> _events = new();
        private QmlElementNode _element = null!;
        private FeedbackRouter _registry = null!;
        private uint _handle;
        private QmlNodeInstance? _instance;

        [SetUp]
        public void SetUp()
        {
            _dir = Directory.CreateTempSubdirectory("vlqt-qmlinst").FullName;
            _file = System.IO.Path.Combine(_dir, "Gauge.qml");
            _element = new QmlElementNode(new RecordingMessages().Errors, _ => _events);
            _registry = new FeedbackRouter();
            _handle = _registry.Acquire(new FeedbackSlot(_element.Core.Feedback, 0));
        }

        [TearDown]
        public void TearDown()
        {
            if (_instance is null)
                _element.Dispose();
            else
                _instance.Dispose();
            Directory.Delete(_dir, true);
        }

        // --- declarations by hand (the parser is T2's) ---

        private static QmlPropertyDecl Prop(string name, QmlValueKind kind, object? literal = null, QmlPropertyFlags flags = QmlPropertyFlags.None)
            => new(name, kind.ToString().ToLowerInvariant(), kind, flags,
                literal is null ? QmlInitializer.None : QmlInitializer.Literal, literal ?? QmlTypes.TypeDefault(kind), null, 1);

        private static QmlSignalDecl Signal(string name, params QmlSignalArg[] args)
            => new(name, args.ToImmutableArray(), null, 1);

        private QmlNodeInstance Create(IEnumerable<QmlPropertyDecl>? properties = null, IEnumerable<QmlSignalDecl>? signals = null)
        {
            var declarations = QmlDeclarations.Empty with
            {
                Properties = (properties ?? Enumerable.Empty<QmlPropertyDecl>()).ToImmutableArray(),
                Signals = (signals ?? Enumerable.Empty<QmlSignalDecl>()).ToImmutableArray(),
            };
            _instance = new QmlNodeInstance(_element, QmlNodeLayout.Build(_file, declarations));
            return _instance;
        }

        // Pins by name, as the layout orders them.
        private static IVLPin In(QmlNodeInstance instance, int index) => instance.Inputs.ElementAt(index);

        private static IVLPin Out(QmlNodeInstance instance, int index) => instance.Outputs.ElementAt(index);

        // Input order: the leading common pins, the declared properties, Properties, the trailing common pins. Set counts from the first property.
        private static void Set(QmlNodeInstance instance, int index, object? value) => In(instance, QmlCommonPins.LeadingInputs.Length + index).Value = value;

        private static void SetCommon(QmlNodeInstance instance, string name, object? value)
        {
            var leading = QmlCommonPins.LeadingInputs.Select(p => p.Name).ToList().IndexOf(name);
            var trailing = QmlCommonPins.TrailingInputs.Select(p => p.Name).ToList().IndexOf(name);
            In(instance, leading >= 0 ? leading : instance.Inputs.Count() - QmlCommonPins.TrailingInputs.Length + trailing).Value = value;
        }

        private QmlElement Frame(QmlNodeInstance instance)
        {
            instance.Update();
            return (QmlElement)Out(instance, 0).Value!;
        }

        private static Dictionary<string, object> Sent(QmlElement e)
        {
            var d = new Dictionary<string, object>();
            foreach (var (name, _) in e.Properties)
                d[name] = name;
            return d;
        }

        private static string[] Names(QmlElement e) => Sent(e).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        private FeedbackRouterTests.Poll NewPoll() => new();

        private void Dispatch(FeedbackRouterTests.Poll poll) => _registry.Dispatch(poll.Bytes);

        // --- pins ---

        [Test]
        public void Pins_MatchTheDescriptions()
        {
            var instance = Create(
                new[] { Prop("progress", QmlValueKind.Real, 0.25f), Prop("title", QmlValueKind.String, "hi"), Prop("accentColor", QmlValueKind.Color) },
                new[] { Signal("fired"), Signal("picked", new QmlSignalArg("n", "int", QmlValueKind.Int)), Signal("moved", new QmlSignalArg("x", "real", QmlValueKind.Real), new QmlSignalArg("y", "real", QmlValueKind.Real)) });
            var layout = QmlNodeLayout.Build(_file, QmlDeclarations.Empty with
            {
                Properties = ImmutableArray.Create(Prop("progress", QmlValueKind.Real, 0.25f), Prop("title", QmlValueKind.String, "hi"), Prop("accentColor", QmlValueKind.Color)),
                Signals = ImmutableArray.Create(Signal("fired"), Signal("picked", new QmlSignalArg("n", "int", QmlValueKind.Int)), Signal("moved", new QmlSignalArg("x", "real", QmlValueKind.Real), new QmlSignalArg("y", "real", QmlValueKind.Real))),
            });

            var inputs = layout.InputDescriptions();
            var outputs = layout.OutputDescriptions();
            Assert.That(instance.Inputs.Count(), Is.EqualTo(inputs.Count));
            Assert.That(instance.Outputs.Count(), Is.EqualTo(outputs.Count));
            for (var i = 0; i < inputs.Count; i++)
            {
                var pin = In(instance, i);
                Assert.That(pin.GetType().GetGenericArguments()[0], Is.EqualTo(inputs[i].Type), $"input {inputs[i].Name}");
                if (inputs[i].DefaultValue is { } d && inputs[i].Type.IsValueType)
                    Assert.That(pin.Value, Is.EqualTo(d), $"default of input {inputs[i].Name}");
            }
            for (var i = 0; i < outputs.Count; i++)
            {
                var pin = Out(instance, i);
                Assert.That(pin.GetType().GetGenericArguments()[0], Is.EqualTo(outputs[i].Type), $"output {outputs[i].Name}");
            }
            Assert.That(outputs.Select(o => o.Name), Is.EqualTo(new[]
            {
                "Output", "Progress", "Title", "Accent Color", "Fired", "Picked", "Moved", "Measured Size", "Scene Bounds",
            }));
        }

        // --- the send rule ---

        [Test]
        public void Send_NotWhileDefault_ThenOnEveryChange_AlsoBackToDefault()
        {
            var instance = Create(new[] { Prop("progress", QmlValueKind.Real, 0.25f) });
            Assert.That(Names(Frame(instance)), Is.Empty, "nothing at the default");
            Set(instance, 0, 0.25f);
            Assert.That(Names(Frame(instance)), Is.Empty, "set to the default: still nothing");

            Set(instance, 0, 0.5f);
            var e = Frame(instance);
            Assert.That(Names(e), Is.EqualTo(new[] { "progress" }));
            Assert.That(e.Properties.TryGetValue("progress", out var v), Is.True);
            Assert.That(v.A, Is.EqualTo(0.5f));

            Set(instance, 0, 0.25f);
            e = Frame(instance);
            Assert.That(e.Properties.TryGetValue("progress", out v), Is.True, "back to the default: sent, it was different once");
            Assert.That(v.A, Is.EqualTo(0.25f));
        }

        [Test]
        public void Send_KeyIsTheQmlName_NotThePinName()
        {
            var instance = Create(new[] { Prop("accentColor", QmlValueKind.Color) });
            Set(instance, 0, new Color4(1, 0, 0, 1));
            Assert.That(Names(Frame(instance)), Is.EqualTo(new[] { "accentColor" }));
        }

        [Test]
        public void Send_Required_FromTheFirstFrame()
        {
            var instance = Create(new[] { Prop("steps", QmlValueKind.Int, flags: QmlPropertyFlags.Required), Prop("other", QmlValueKind.Int) });
            var e = Frame(instance);
            Assert.That(Names(e), Is.EqualTo(new[] { "steps" }));
            Assert.That(e.Properties.TryGetValue("steps", out var v), Is.True);
            Assert.That(v.Integer, Is.EqualTo(0));
        }

        [Test]
        public void Send_NullString_IsTheEmptyString()
        {
            var instance = Create(new[] { Prop("title", QmlValueKind.String, "hi") });
            Set(instance, 0, null);
            var e = Frame(instance);
            Assert.That(Names(e), Is.EqualTo(new[] { "title" }));
            e.Properties.TryGetValue("title", out var v);
            Assert.That(v.Text, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Send_ReadOnlyProperty_HasNoInput()
        {
            var instance = Create(new[] { Prop("value", QmlValueKind.Real, flags: QmlPropertyFlags.ReadOnly), Prop("steps", QmlValueKind.Int) });
            // readonly: no input, so Properties is at index 1 (after "steps").
            Assert.That(In(instance, QmlCommonPins.LeadingInputs.Length + 1).GetType().GetGenericArguments()[0], Is.EqualTo(typeof(Dictionary<string, object>)));
            Assert.That(instance.Outputs.Count(), Is.EqualTo(1 + 2 + 2));
        }

        // --- Properties (plan B) ---

        [Test]
        public void PlanB_EntriesGoThrough_PinWinsOnAClash()
        {
            var instance = Create(new[] { Prop("progress", QmlValueKind.Real, 0.25f) });
            // index 0 = Progress, 1 = Properties
            Set(instance, 1, new Dictionary<string, object> { ["extra"] = 3, ["progress"] = 9f });
            var e = Frame(instance);
            Assert.That(Names(e), Is.EqualTo(new[] { "extra", "progress" }), "pin still at default: the entry stays");
            e.Properties.TryGetValue("progress", out var v);
            Assert.That(v.A, Is.EqualTo(9f));

            Set(instance, 0, 0.5f);
            e = Frame(instance);
            e.Properties.TryGetValue("progress", out v);
            Assert.That(v.A, Is.EqualTo(0.5f), "pin on top");
            Assert.That(Names(e), Is.EqualTo(new[] { "extra", "progress" }));
        }

        [Test]
        public void PlanB_ChangedInPlace_IsSent_RemovedKeyDisappears()
        {
            var instance = Create();
            var d = new Dictionary<string, object> { ["a"] = 1f };
            Set(instance, 0, d);
            var first = Frame(instance);
            Assert.That(Names(first), Is.EqualTo(new[] { "a" }));
            d["a"] = 2f;
            var second = Frame(instance);
            Assert.That(second, Is.Not.SameAs(first));
            second.Properties.TryGetValue("a", out var v);
            Assert.That(v.A, Is.EqualTo(2f));
            d.Remove("a");
            Assert.That(Names(Frame(instance)), Is.Empty);
            Set(instance, 0, null);
            Assert.That(Names(Frame(instance)), Is.Empty);
        }

        // --- nothing per frame ---

        [Test]
        public void Update_NothingChanged_SameDescription_AndNoAllocation()
        {
            var instance = Create(
                new[] { Prop("progress", QmlValueKind.Real, 0.25f), Prop("title", QmlValueKind.String, "hi") },
                new[] { Signal("fired"), Signal("picked", new QmlSignalArg("n", "int", QmlValueKind.Int)) });
            Set(instance, 0, 0.5f);
            Set(instance, 1, "x");
            Set(instance, 2, new Dictionary<string, object> { ["extra"] = 3 });
            var first = Frame(instance);
            for (var i = 0; i < 5; i++)
                Assert.That(Frame(instance), Is.SameAs(first));

            var allocated = Allocations.Fewest(() => instance.Update(), frames: 100);
            Assert.That(allocated, Is.EqualTo(0), "bytes allocated by 100 idle frames");
        }

        // --- property outputs ---

        [Test]
        public void PropertyOutput_DefaultUntilChange_ThenValue_NotTheInput()
        {
            var instance = Create(new[] { Prop("progress", QmlValueKind.Real, 0.25f), Prop("steps", QmlValueKind.Int, 4) });
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(0.25f));
            Assert.That(Out(instance, 2).Value, Is.EqualTo(4));

            Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("progress", Protocol.ValueType.F32, QmlWire.F(0.75f))));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(0.75f));
            Assert.That(Out(instance, 2).Value, Is.EqualTo(4), "untouched: still the default");
        }

        [Test]
        public void PropertyOutput_IsCoerced_AndAMismatchGivesTheDefault()
        {
            var instance = Create(new[] { Prop("progress", QmlValueKind.Real, 0.25f), Prop("title", QmlValueKind.String, "hi") });
            Frame(instance);
            Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("progress", Protocol.ValueType.I32, QmlWire.I32(3))));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(3f), "int from QML for a real pin");

            Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("title", Protocol.ValueType.I32, QmlWire.I32(3))));
            Frame(instance);
            Assert.That(Out(instance, 2).Value, Is.EqualTo("hi"), "an int does not fit a string: the default");
        }

        [Test]
        public void PropertyOutput_AfterReload_BackToTheDefault()
        {
            var instance = Create(new[] { Prop("progress", QmlValueKind.Real, 0.25f) });
            Frame(instance);
            Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("progress", Protocol.ValueType.F32, QmlWire.F(0.75f))));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(0.75f));

            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "Gauge.qml"));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(0.25f), "the value belonged to the old instance");
        }

        [Test]
        public void PropertyOutput_ReadOnly_Works()
        {
            var instance = Create(new[] { Prop("value", QmlValueKind.Vector2D, flags: QmlPropertyFlags.ReadOnly) });
            Frame(instance);
            Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("value", Protocol.ValueType.Vec2, QmlWire.F(1, 2))));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(new Vector2(1, 2)));
        }

        [Test]
        public void PropertyOutput_Point_ArrivesAsVector2() => AssertVector2Output(QmlValueKind.Point);

        [Test]
        public void PropertyOutput_Size_ArrivesAsVector2() => AssertVector2Output(QmlValueKind.Size);

        private void AssertVector2Output(QmlValueKind kind)
        {
            var instance = Create(new[] { Prop("value", kind) });
            Frame(instance);
            Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("value", Protocol.ValueType.Vec2, QmlWire.F(3, -4))));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(new Vector2(3, -4)));
        }

        // --- signal outputs ---

        [Test]
        public void SignalOutput_Bang_TrueInTheFrame_OnlyForItsName()
        {
            var instance = Create(signals: new[] { Signal("fired"), Signal("other") });
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(false));
            Dispatch(NewPoll().Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("fired")).Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("fired")));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(true));
            Assert.That(Out(instance, 2).Value, Is.EqualTo(false));
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.EqualTo(false), "one frame only");
        }

        [Test]
        public void SignalOutput_Values_SpreadOfTheArgument_OneFrame()
        {
            var instance = Create(signals: new[] { Signal("picked", new QmlSignalArg("n", "int", QmlValueKind.Int)), Signal("scaled", new QmlSignalArg("f", "real", QmlValueKind.Real)) });
            Frame(instance);
            Assert.That(Out(instance, 1).Value, Is.InstanceOf<Spread<int>>());
            Assert.That(Out(instance, 2).Value, Is.InstanceOf<Spread<float>>());
            Dispatch(NewPoll()
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("picked", (Protocol.ValueType.I32, QmlWire.I32(1))))
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("scaled", (Protocol.ValueType.I32, QmlWire.I32(2))))
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("picked", (Protocol.ValueType.I32, QmlWire.I32(3)))));
            Frame(instance);
            Assert.That(((Spread<int>)Out(instance, 1).Value!).ToArray(), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(((Spread<float>)Out(instance, 2).Value!).ToArray(), Is.EqualTo(new[] { 2f }), "int coerced to the real pin");
            Frame(instance);
            Assert.That(((Spread<int>)Out(instance, 1).Value!).Count, Is.EqualTo(0));
            Assert.That(((Spread<float>)Out(instance, 2).Value!).Count, Is.EqualTo(0));
        }

        [Test]
        public void SignalOutput_Values_AnArgumentThatDoesNotFit_IsLeftOut()
        {
            var instance = Create(signals: new[] { Signal("picked", new QmlSignalArg("n", "int", QmlValueKind.Int)) });
            Frame(instance);
            Dispatch(NewPoll()
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("picked", (Protocol.ValueType.String, QmlWire.Str("x"))))
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("picked", (Protocol.ValueType.I32, QmlWire.I32(5)))));
            Frame(instance);
            Assert.That(((Spread<int>)Out(instance, 1).Value!).ToArray(), Is.EqualTo(new[] { 5 }));
        }

        [Test]
        public void SignalOutput_UntypedArgument_Passes()
        {
            var instance = Create(signals: new[] { Signal("changed", new QmlSignalArg("v", "var", null)) });
            Frame(instance);
            Dispatch(NewPoll().Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("changed", (Protocol.ValueType.String, QmlWire.Str("x")))));
            Frame(instance);
            Assert.That(((Spread<object>)Out(instance, 1).Value!).ToArray(), Is.EqualTo(new object[] { "x" }));
        }

        [Test]
        public void SignalOutput_Signals_WithArguments_ByName()
        {
            var instance = Create(signals: new[]
            {
                Signal("moved", new QmlSignalArg("x", "real", QmlValueKind.Real), new QmlSignalArg("y", "real", QmlValueKind.Real)),
                Signal("rest", new QmlSignalArg("a", "real", QmlValueKind.Real), new QmlSignalArg("b", "real", QmlValueKind.Real)),
            });
            Frame(instance);
            Dispatch(NewPoll().Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("moved", (Protocol.ValueType.F32, QmlWire.F(1)), (Protocol.ValueType.F32, QmlWire.F(2)))));
            Frame(instance);
            var moved = (Spread<QmlSignal>)Out(instance, 1).Value!;
            Assert.That(moved.Count, Is.EqualTo(1));
            Assert.That(moved[0].Name, Is.EqualTo("moved"));
            Assert.That(moved[0].Arguments.ToArray(), Is.EqualTo(new object[] { 1f, 2f }));
            Assert.That(((Spread<QmlSignal>)Out(instance, 2).Value!).Count, Is.EqualTo(0));
            Frame(instance);
            Assert.That(((Spread<QmlSignal>)Out(instance, 1).Value!).Count, Is.EqualTo(0));
        }

        // --- passthrough ---

        [Test]
        public void Passthrough_MeasuredSizeAndOutput()
        {
            var instance = Create();
            var e = Frame(instance);
            Assert.That(e.Type, Is.EqualTo(ElementType.Qml));
            Assert.That(e.Source, Is.EqualTo(_file));
            var last = instance.Outputs.Count();
            Assert.That(Out(instance, last - 2).Value, Is.EqualTo(Vector2.Zero));

            Dispatch(NewPoll().Measured(_handle, 100, 50));
            Frame(instance);
            Assert.That((Vector2)Out(instance, last - 2).Value!, Is.Not.EqualTo(Vector2.Zero));
        }

        [Test]
        public void CommonPins_GoToTheElement()
        {
            var instance = Create(new[] { Prop("progress", QmlValueKind.Real) });
            SetCommon(instance, "Auto Size", false);
            SetCommon(instance, "Size", new Vector2(3, 4));
            SetCommon(instance, "Opacity", 0.5f);
            var e = Frame(instance);
            Assert.That(e.AutoSize, Is.False);
            Assert.That(e.Size, Is.EqualTo(new Vector2(3, 4)));
            Assert.That(e.Opacity, Is.EqualTo(0.5f));
        }
    }
}
