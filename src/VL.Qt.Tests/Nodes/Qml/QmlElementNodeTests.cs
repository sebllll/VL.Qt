using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Tests.Support;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests.Nodes.Qml
{
    /// <summary>QmlElement node: description, reconciler commands, reload, outputs, errors.</summary>
    public class QmlElementNodeTests
    {
        private string _dir = null!;
        private string _file = null!;
        private readonly Subject<FileSystemEventArgs> _events = new();
        private QmlElementNode _node = null!;
        private FeedbackRouter _registry = null!;
        private uint _handle;
        private RecordingSink _sink = null!;
        private Reconciler _reconciler = null!;
        private RecordingMessages _m = new();


        [SetUp]
        public void SetUp()
        {
            _dir = Directory.CreateTempSubdirectory("vlqt-qmlnode").FullName;
            _file = System.IO.Path.Combine(_dir, "a.qml");
            _m = new RecordingMessages();
            _node = new QmlElementNode(_m.Errors, _ => _events);
            _registry = new FeedbackRouter();
            _handle = _registry.Acquire(new FeedbackSlot(_node.Core.Feedback, 0));
            _sink = new RecordingSink();
            _reconciler = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        }

        [TearDown]
        public void TearDown()
        {
            _node.Dispose();
            Directory.Delete(_dir, true);
        }

        private QmlElement Update(Dictionary<string, object>? properties = null, string? file = "", bool autoSize = true)
            => Update(out _, out _, properties, file, autoSize);

        private QmlElement Update(out Dictionary<string, object> changed, out Spread<QmlSignal> signals,
            Dictionary<string, object>? properties = null, string? file = "", bool autoSize = true)
            => (QmlElement)_node.Update(autoSize, new Vector2(1, 1),
                Matrix.Identity, RectangleAnchor.TopLeft, file is null ? null : new Path(file == "" ? _file : file), properties, 1, false, false, null, null,
                true,
                out changed, out signals, out _, out _);

        private string[] Frame(Dictionary<string, object>? properties = null, string? file = "")
        {
            _sink.Clear();
            _reconciler.Update(Update(properties, file), SpaceMapping.Default);
            return _sink.Commands.ToArray();
        }

        private Reconciliation.FeedbackRouterTests.Poll NewPoll() => new();

        [Test]
        public void Defaults()
        {
            var e = Update(null, null);
            Assert.That(e.Type, Is.EqualTo(ElementType.Qml));
            Assert.That((ushort)ElementType.Qml, Is.EqualTo(Protocol.ElementType.Qml));
            Assert.That((e.AutoSize, e.Source, e.Reload), Is.EqualTo((true, string.Empty, 0)));
            Assert.That(e.Properties, Is.SameAs(QmlProperties.Empty));
            Assert.That(e.Feedback.Feedback, Is.SameAs(_node.Core.Feedback));
        }

        [Test]
        public void Source_IsTheFullPath()
        {
            Assert.That(Update(null, _file).Source, Is.EqualTo(_file));
            Assert.That(Update(null, System.IO.Path.Combine("rel", "x.qml")).Source, Is.EqualTo(System.IO.Path.GetFullPath(System.IO.Path.Combine("rel", "x.qml"))));
            Assert.That(Update(null, "  ").Source, Is.Empty);
        }

        [Test]
        public void SameInputs_SameDescription()
        {
            var props = new Dictionary<string, object> { ["a"] = 1f };
            var first = Update(props);
            Assert.That(Update(props), Is.SameAs(first));
            Assert.That(Update(new Dictionary<string, object> { ["a"] = 1f }), Is.SameAs(first), "a new dictionary with the same content");
            Assert.That(Update(new Dictionary<string, object> { ["a"] = 2f }), Is.Not.SameAs(first));
            Assert.That(Update(props, autoSize: false), Is.Not.SameAs(first));
        }

        [Test]
        public void Reconciler_FirstFrame_CreatesSetsSourceAndAllProperties()
        {
            var cmds = Frame(new() { ["fill"] = new Color4(1, 0, 0, 1), ["progress"] = 0.5f });
            Assert.That(cmds[0], Is.EqualTo($"CREATE 1 Qml"));
            Assert.That(cmds, Does.Contain($"SET 1 QmlSource string({_file})"));
            Assert.That(cmds, Does.Not.Contain($"SET 1 QmlReload i32(0)"), "reload counter 0 is the default");
            Assert.That(cmds, Does.Contain("QML 1 0 fill=color(1,0,0,1) progress=f32(0.5)"));
        }

        [Test]
        public void Reconciler_OnlyChangedProperties_AreSent()
        {
            Frame(new() { ["a"] = 1f, ["b"] = "x", ["c"] = true });
            Assert.That(Frame(new() { ["a"] = 1f, ["b"] = "x", ["c"] = true }), Is.Empty);
            Assert.That(Frame(new() { ["a"] = 2f, ["b"] = "x", ["c"] = true }), Is.EqualTo(new[] { "QML 1 0 a=f32(2)" }));
            Assert.That(Frame(new() { ["a"] = 2f, ["b"] = "y", ["c"] = false, ["d"] = 4 }), Is.EqualTo(new[] { "QML 1 0 b=string(y) c=bool(0) d=i32(4)" }));
        }

        [Test]
        public void Reconciler_RemovedKey_IsNotReset_AndNotSentAgainWhenItComesBack()
        {
            Frame(new() { ["a"] = 1f, ["b"] = 2f });
            Assert.That(Frame(new() { ["a"] = 1f }), Is.Empty, "removed: nothing is sent");
            Assert.That(Frame(new() { ["a"] = 1f, ["b"] = 2f }), Is.EqualTo(new[] { "QML 1 0 b=f32(2)" }),
                "back again: sent again, the diff is against the last description");
        }

        [Test]
        public void Reconciler_MutatedInPlace_IsSent()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f };
            Frame(d);
            d["a"] = 5f;
            Assert.That(Frame(d), Is.EqualTo(new[] { "QML 1 0 a=f32(5)" }));
        }

        [Test]
        public void Reconciler_NewFile_SendsSourceAndAllPropertiesAgain()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f };
            Frame(d);
            var other = System.IO.Path.Combine(_dir, "b.qml");
            var cmds = Frame(d, other);
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 QmlSource string({other})", "QML 1 0 a=f32(1)" }));
        }

        [Test]
        public void Reconciler_AutoSizeOff_IsSent()
        {
            Update();
            _sink.Clear();
            _reconciler.Update(Update(autoSize: false), SpaceMapping.Default);
            Assert.That(_sink.Commands, Does.Contain($"SET 1 AutoSize bool(0)"));
        }

        [Test]
        public void HotReload_ChangeOfAFileNextToIt_BumpsTheCounter_AndResendsEverything()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["b"] = "x" };
            Frame(d);
            Assert.That(Frame(d), Is.Empty);
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "Helper.qml"));
            var cmds = Frame(d);
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 QmlReload i32(1)", "QML 1 0 a=f32(1) b=string(x)" }));
            Assert.That(Frame(d), Is.Empty, "once per change");
        }

        [Test]
        public void HotReload_OtherFiles_DoNotReload()
        {
            Frame();
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "readme.txt"));
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "lib.js"));
            Assert.That(Frame(), Is.Empty);
        }

        [Test]
        public void HotReload_ResetsTheOutputs()
        {
            Frame();
            _registry.Dispatch(NewPoll()
                .Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("steps", Protocol.ValueType.I32, QmlWire.I32(3)))
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("clicked", (Protocol.ValueType.I32, QmlWire.I32(1)))).Bytes);
            Update(out var changed, out var signals);
            Assert.That(changed["steps"], Is.EqualTo(3));
            Assert.That(signals.Count, Is.EqualTo(1));

            _registry.Dispatch(NewPoll().Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("clicked")).Bytes);
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "a.qml"));
            Update(out changed, out signals);
            Assert.That(changed, Is.Empty, "the properties belonged to the old instance");
            Assert.That(signals.Count, Is.EqualTo(0), "so did the signal that was waiting");
        }

        [Test]
        public void NewFile_ResetsTheOutputs()
        {
            Update();
            _registry.Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("a", Protocol.ValueType.F32, QmlWire.F(1))).Bytes);
            Update(out var changed, out _);
            Assert.That(changed, Has.Count.EqualTo(1));
            Update(out changed, out _, file: System.IO.Path.Combine(_dir, "b.qml"));
            Assert.That(changed, Is.Empty);
        }

        [Test]
        public void Outputs_Properties_NewInstanceOnlyWhenSomethingChanged()
        {
            Update(out var first, out _);
            Assert.That(first, Is.Empty);
            _registry.Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("progress", Protocol.ValueType.F32, QmlWire.F(0.5f))).Bytes);
            Update(out var second, out _);
            Assert.That(second, Is.EqualTo(new Dictionary<string, object> { ["progress"] = 0.5f }));
            Update(out var third, out _);
            Assert.That(third, Is.SameAs(second), "nothing new: the same dictionary");
            _registry.Dispatch(NewPoll().Add(Protocol.EventId.QmlProperty, _handle, QmlWire.Property("progress", Protocol.ValueType.F32, QmlWire.F(0.75f))).Bytes);
            Update(out var fourth, out _);
            Assert.That(fourth, Is.Not.SameAs(second));
            Assert.That(fourth["progress"], Is.EqualTo(0.75f));
            Assert.That(second["progress"], Is.EqualTo(0.5f), "an earlier output is not changed afterwards");
        }

        [Test]
        public void Outputs_Signals_OnceThenEmpty()
        {
            Update();
            _registry.Dispatch(NewPoll()
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("clicked", (Protocol.ValueType.I32, QmlWire.I32(1))))
                .Add(Protocol.EventId.QmlSignal, _handle, QmlWire.Signal("clicked", (Protocol.ValueType.I32, QmlWire.I32(2)))).Bytes);
            Update(out _, out var signals);
            Assert.That(signals.Select(s => (s.Name, s.Arguments[0])), Is.EqualTo(new[] { ("clicked", (object)1), ("clicked", 2) }));
            Update(out _, out signals);
            Assert.That(signals.Count, Is.EqualTo(0));
        }

        [Test]
        public void Problems_UnsupportedValues_AreWarned_AndIgnored()
        {
            var d = new Dictionary<string, object> { ["good"] = 1f, ["bad"] = 5L };
            var e = Update(out _, out _, d);
            Assert.That(e.Properties.Count, Is.EqualTo(1));
            Assert.That(_m.Shown!, Does.Contain("Property 'bad': type Int64 is not supported"));
            Update(out _, out _, d);
            Assert.That(_m.Shows, Is.EqualTo(1), "the same problem is shown once");
        }

        [Test]
        public void Problems_FromQt_AreShownAtTheNode_AndClear()
        {
            Update();
            _registry.Dispatch(NewPoll().Error(_handle, "a.qml:3:5: Unexpected token").Bytes);
            Update();
            Assert.That(_node.Core.Error, Is.EqualTo("a.qml:3:5: Unexpected token"));
            _registry.Dispatch(NewPoll().Error(_handle, "").Bytes);
            Update();
            Assert.That(_node.Core.Error, Is.Null.Or.Empty);
        }
    }
}
