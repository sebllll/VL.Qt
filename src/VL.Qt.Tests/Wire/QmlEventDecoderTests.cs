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
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests
{
    /// <summary>QmlEventDecoder: decoding of EVENT_QML_PROPERTY and EVENT_QML_SIGNAL, for every value type and for broken payloads.</summary>
    public class QmlEventDecoderTests
    {
        private static IEnumerable<TestCaseData> Values()
        {
            yield return new TestCaseData(Protocol.ValueType.F32, QmlWire.F(1.5f), 1.5f);
            yield return new TestCaseData(Protocol.ValueType.I32, QmlWire.I32(-42), -42);
            yield return new TestCaseData(Protocol.ValueType.Bool, QmlWire.Bool(true), true);
            yield return new TestCaseData(Protocol.ValueType.Bool, QmlWire.Bool(false), false);
            yield return new TestCaseData(Protocol.ValueType.String, QmlWire.Str("hä€"), "hä€");
            yield return new TestCaseData(Protocol.ValueType.String, QmlWire.Str(""), "");
            yield return new TestCaseData(Protocol.ValueType.Color, QmlWire.F(1, 0.5f, 0.25f, 0.75f), new Color4(1, 0.5f, 0.25f, 0.75f));
            yield return new TestCaseData(Protocol.ValueType.Vec2, QmlWire.F(1, 2), new Vector2(1, 2));
            yield return new TestCaseData(Protocol.ValueType.Vec3, QmlWire.F(1, 2, 3), new Vector3(1, 2, 3));
            yield return new TestCaseData(Protocol.ValueType.Vec4, QmlWire.F(1, 2, 3, 4), new Vector4(1, 2, 3, 4));
            yield return new TestCaseData(Protocol.ValueType.Rectf, QmlWire.F(10, 20, 30, 40), new RectangleF(10, 20, 30, 40));
        }

        [TestCaseSource(nameof(Values))]
        public void Property_EachValueType(ushort type, byte[] value, object expected)
        {
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("prop", type, value), out var name, out var v), Is.True);
            Assert.That(name, Is.EqualTo("prop"));
            Assert.That(v, Is.EqualTo(expected));
            Assert.That(v.GetType(), Is.EqualTo(expected.GetType()));
        }

        [Test]
        public void Property_NamePadding_AllLengths()
        {
            foreach (var name in new[] { "a", "ab", "abc", "abcd", "abcde", "größe", "x€" })
            {
                Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property(name, Protocol.ValueType.I32, QmlWire.I32(9)), out var n, out var v), Is.True, name);
                Assert.That((n, v), Is.EqualTo((name, (object)9)), name);
            }
        }

        [Test]
        public void Property_Malformed_IsRejected()
        {
            var good = QmlWire.Property("abc", Protocol.ValueType.F32, QmlWire.F(1));
            Assert.That(QmlEventDecoder.TryDecodeProperty(good, out _, out _), Is.True);
            Assert.That(QmlEventDecoder.TryDecodeProperty(good.AsSpan(0, 3), out _, out _), Is.False, "shorter than the header");
            Assert.That(QmlEventDecoder.TryDecodeProperty(good.AsSpan(0, good.Length - 1), out _, out _), Is.False, "truncated value");
            Assert.That(QmlEventDecoder.TryDecodeProperty(good.AsSpan(0, 6), out _, out _), Is.False, "name cut off");
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("", Protocol.ValueType.F32, QmlWire.F(1)), out _, out _), Is.False, "empty name");
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("a", Protocol.ValueType.Mat4, new byte[64]), out _, out _), Is.False, "unsupported value type");
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("a", 0, new byte[4]), out _, out _), Is.False, "value type 0");
            // string whose length field points beyond the payload
            var bad = QmlWire.Property("a", Protocol.ValueType.String, QmlWire.Str("hello"));
            Assert.That(QmlEventDecoder.TryDecodeProperty(bad.AsSpan(0, bad.Length - 3), out _, out _), Is.False, "string cut off");
        }

        [Test]
        public void Signal_NoArguments()
        {
            Assert.That(QmlEventDecoder.TryDecodeSignal(QmlWire.Signal("done"), out var s), Is.True);
            Assert.That(s.Name, Is.EqualTo("done"));
            Assert.That(s.Arguments.Count, Is.EqualTo(0));
        }

        [TestCaseSource(nameof(Values))]
        public void Signal_OneArgument_EachValueType(ushort type, byte[] value, object expected)
        {
            Assert.That(QmlEventDecoder.TryDecodeSignal(QmlWire.Signal("sig", (type, value)), out var s), Is.True);
            Assert.That(s.Arguments.Count, Is.EqualTo(1));
            Assert.That(s.Arguments[0], Is.EqualTo(expected));
            Assert.That(s.Arguments[0].GetType(), Is.EqualTo(expected.GetType()));
        }

        [Test]
        public void Signal_MixedArguments_AreAlignedIndividually()
        {
            var payload = QmlWire.Signal("finished",
                (Protocol.ValueType.String, QmlWire.Str("done")),
                (Protocol.ValueType.Bool, QmlWire.Bool(true)),
                (Protocol.ValueType.F32, QmlWire.F(1)),
                (Protocol.ValueType.Color, QmlWire.F(1, 0, 0, 1)),
                (Protocol.ValueType.I32, QmlWire.I32(7)));
            Assert.That(QmlEventDecoder.TryDecodeSignal(payload, out var s), Is.True);
            Assert.That(s.Name, Is.EqualTo("finished"));
            Assert.That(s.Arguments.ToArray(), Is.EqualTo(new object[] { "done", true, 1f, new Color4(1, 0, 0, 1), 7 }));
        }

        [Test]
        public void Signal_Malformed_IsRejected()
        {
            var good = QmlWire.Signal("clicked", (Protocol.ValueType.I32, QmlWire.I32(1)), (Protocol.ValueType.F32, QmlWire.F(2)));
            Assert.That(QmlEventDecoder.TryDecodeSignal(good, out _), Is.True);
            Assert.That(QmlEventDecoder.TryDecodeSignal(good.AsSpan(0, 3), out _), Is.False, "shorter than the header");
            Assert.That(QmlEventDecoder.TryDecodeSignal(good.AsSpan(0, good.Length - 4), out _), Is.False, "second argument missing");
            Assert.That(QmlEventDecoder.TryDecodeSignal(good.AsSpan(0, good.Length - 1), out _), Is.False, "second argument truncated");
            Assert.That(QmlEventDecoder.TryDecodeSignal(QmlWire.Signal("", (Protocol.ValueType.I32, QmlWire.I32(1))), out _), Is.False, "empty name");
            Assert.That(QmlEventDecoder.TryDecodeSignal(QmlWire.Signal("s", (Protocol.ValueType.Mat4, new byte[64])), out _), Is.False, "unknown argument type");
            var tooMany = QmlWire.Signal("s", Enumerable.Repeat((Protocol.ValueType.I32, QmlWire.I32(1)), 65).ToArray());
            Assert.That(QmlEventDecoder.TryDecodeSignal(tooMany, out _), Is.False, "more than 64 arguments");
            var many = QmlWire.Signal("s", Enumerable.Repeat((Protocol.ValueType.I32, QmlWire.I32(1)), 64).ToArray());
            Assert.That(QmlEventDecoder.TryDecodeSignal(many, out var s) && s.Arguments.Count == 64, Is.True);
        }

        // --- through the registry into the slot ---

        private static (FeedbackRouter Registry, ElementFeedback Feedback, uint FeedbackId) Bound()
        {
            var registry = new FeedbackRouter();
            var feedback = new ElementFeedback();
            return (registry, feedback, registry.Acquire(new FeedbackSlot(feedback, 0)));
        }

        [Test]
        public void Dispatch_Property_LastValuePerName()
        {
            var (reg, feedback, h) = Bound();
            var res = reg.Dispatch(new FeedbackRouterTests.Poll()
                .Add(Protocol.EventId.QmlProperty, h, QmlWire.Property("a", Protocol.ValueType.F32, QmlWire.F(1)))
                .Add(Protocol.EventId.QmlProperty, h, QmlWire.Property("b", Protocol.ValueType.String, QmlWire.Str("x")))
                .Add(Protocol.EventId.QmlProperty, h, QmlWire.Property("a", Protocol.ValueType.F32, QmlWire.F(2))).Bytes);
            Assert.That(res, Is.EqualTo(new DispatchResult(3, 0, null)));
            var values = feedback.QmlPropertyValues()!;
            Assert.That(values, Has.Count.EqualTo(2));
            Assert.That(values["a"], Is.EqualTo(2f));
            Assert.That(values["b"], Is.EqualTo("x"));
            Assert.That(feedback.QmlPropertyUpdates(), Is.EqualTo(3));
        }

        [Test]
        public void Dispatch_Signals_InOrder_TakenOnce()
        {
            var (reg, feedback, h) = Bound();
            reg.Dispatch(new FeedbackRouterTests.Poll()
                .Add(Protocol.EventId.QmlSignal, h, QmlWire.Signal("clicked", (Protocol.ValueType.I32, QmlWire.I32(1))))
                .Add(Protocol.EventId.QmlSignal, h, QmlWire.Signal("clicked", (Protocol.ValueType.I32, QmlWire.I32(2))))
                .Add(Protocol.EventId.QmlSignal, h, QmlWire.Signal("reset")).Bytes);
            var taken = feedback.TakeQmlSignals();
            Assert.That(taken.Select(s => s.Name), Is.EqualTo(new[] { "clicked", "clicked", "reset" }));
            Assert.That(taken[1].Arguments[0], Is.EqualTo(2));
            Assert.That(feedback.TakeQmlSignals().Count, Is.EqualTo(0));
        }

        [Test]
        public void Dispatch_MalformedPayload_IsIgnored_NotApplied()
        {
            var (reg, feedback, h) = Bound();
            var res = reg.Dispatch(new FeedbackRouterTests.Poll()
                .Add(Protocol.EventId.QmlProperty, h, QmlWire.Property("", Protocol.ValueType.F32, QmlWire.F(1)))
                .Add(Protocol.EventId.QmlSignal, h, new byte[] { 1, 0 }).Bytes);
            Assert.That(res.Applied, Is.EqualTo(0));
            Assert.That(res.Ignored, Is.EqualTo(2));
            Assert.That(feedback.QmlPropertyValues(), Is.Null);
            Assert.That(feedback.TakeQmlSignals().Count, Is.EqualTo(0));
        }

        [Test]
        public void ResetQml_ForgetsValuesAndSignals_AndCountsAsUpdate()
        {
            var (reg, feedback, h) = Bound();
            reg.Dispatch(new FeedbackRouterTests.Poll()
                .Add(Protocol.EventId.QmlProperty, h, QmlWire.Property("a", Protocol.ValueType.F32, QmlWire.F(1)))
                .Add(Protocol.EventId.QmlSignal, h, QmlWire.Signal("s")).Bytes);
            int before = feedback.QmlPropertyUpdates();
            feedback.ResetQml();
            Assert.That(feedback.QmlPropertyValues(), Is.Empty);
            Assert.That(feedback.TakeQmlSignals().Count, Is.EqualTo(0));
            Assert.That(feedback.QmlPropertyUpdates(), Is.Not.EqualTo(before));
        }

        [Test]
        public void Signals_BeyondTheCap_AreDropped()
        {
            var (reg, feedback, h) = Bound();
            var poll = new FeedbackRouterTests.Poll();
            for (int i = 0; i < ElementFeedback.MaxPendingQmlSignals + 10; i++)
                poll.Add(Protocol.EventId.QmlSignal, h, QmlWire.Signal("s"));
            reg.Dispatch(poll.Bytes);
            Assert.That(feedback.TakeQmlSignals().Count, Is.EqualTo(ElementFeedback.MaxPendingQmlSignals));
        }
    }
}
