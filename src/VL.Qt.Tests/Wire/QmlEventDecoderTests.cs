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
using VL.Qt.Tests.Reconciliation;
using VL.Qt.Tests.Support;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests.Wire
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
        public void Property_DateTime_IsLocalTime_AndNoDateIsMinValue()
        {
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("d", Protocol.ValueType.DateTime, QmlWire.I64(1500)), out _, out var v), Is.True);
            Assert.That(v, Is.EqualTo(new DateTime(1970, 1, 1, 0, 0, 1, 500, DateTimeKind.Utc).ToLocalTime()));
            Assert.That(((DateTime)v).Kind, Is.EqualTo(DateTimeKind.Local));
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("d", Protocol.ValueType.DateTime, QmlWire.I64(long.MinValue)), out _, out v), Is.True);
            Assert.That(v, Is.EqualTo(DateTime.MinValue));
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("d", Protocol.ValueType.DateTime, QmlWire.I32(1)), out _, out _), Is.False, "short");
        }

        [Test]
        public void Property_Font_SizeInUnitsOfTheMapping()
        {
            var payload = QmlWire.Property("f", Protocol.ValueType.Font, QmlWire.Font("Segoe UI", 24, 700, true));
            Assert.That(QmlEventDecoder.TryDecodeProperty(payload, out _, out var v), Is.True);
            Assert.That(v, Is.EqualTo(new Font { Family = "Segoe UI", Size = 0.24f, Weight = 700, Italic = true }), "DIPTopLeft: 100 DIP per unit");
            Assert.That(QmlEventDecoder.TryDecodeProperty(payload, new SpaceMapping(Space.Normalized, new Vector2(800, 600), 1f), out _, out v), Is.True);
            Assert.That(((Font)v).Size, Is.EqualTo(24f / 300f).Within(1e-6), "Normalized: half the scene height per unit");
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("f", Protocol.ValueType.Font, QmlWire.Font("", 12, 400, false).Take(14).ToArray()), out _, out _), Is.False, "short");
            var longFamily = QmlWire.Font("Arial", 12, 400, false);
            BinaryPrimitives.WriteUInt32LittleEndian(longFamily.AsSpan(12), 99);
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("f", Protocol.ValueType.Font, longFamily), out _, out _), Is.False, "family past the payload");
        }

        [Test]
        public void Signal_FontArgument_AndDateList()
        {
            var payload = QmlWire.Signal("picked", (Protocol.ValueType.Font, QmlWire.Font("Arial", 12, 400, false)),
                (Protocol.ValueType.List, QmlWire.List(Protocol.ValueType.DateTime, 2, QmlWire.I64(0), QmlWire.I64(long.MinValue))));
            Assert.That(QmlEventDecoder.TryDecodeSignal(payload, out var signal), Is.True);
            Assert.That(signal.Arguments[0], Is.EqualTo(new Font { Family = "Arial", Size = 0.12f }));
            Assert.That(signal.Arguments[1], Is.EqualTo(new[] { DateTime.UnixEpoch.ToLocalTime(), DateTime.MinValue }.ToSpread()));
        }

        // --- lists ---

        private static IEnumerable<TestCaseData> Lists()
        {
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.F32, 2, QmlWire.F(1, 2.5f)), new[] { 1f, 2.5f }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.I32, 3, QmlWire.I32(-1), QmlWire.I32(0), QmlWire.I32(7)), new[] { -1, 0, 7 }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.Bool, 3, QmlWire.Bool(true), QmlWire.Bool(false), QmlWire.Bool(true)), new[] { true, false, true }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.String, 3, QmlWire.Pad(QmlWire.Str("a")), QmlWire.Pad(QmlWire.Str("hä€")), QmlWire.Pad(QmlWire.Str(""))),
                new[] { "a", "hä€", "" }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.Color, 1, QmlWire.F(1, 0.5f, 0.25f, 0.75f)), new[] { new Color4(1, 0.5f, 0.25f, 0.75f) }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.Vec2, 2, QmlWire.F(1, 2), QmlWire.F(3, 4)), new[] { new Vector2(1, 2), new Vector2(3, 4) }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.Vec3, 1, QmlWire.F(1, 2, 3)), new[] { new Vector3(1, 2, 3) }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.Vec4, 1, QmlWire.F(1, 2, 3, 4)), new[] { new Vector4(1, 2, 3, 4) }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.Rectf, 1, QmlWire.F(10, 20, 30, 40)), new[] { new RectangleF(10, 20, 30, 40) }.ToSpread());
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.F32, 0), Spread<float>.Empty);
            yield return new TestCaseData(QmlWire.List(Protocol.ValueType.String, 0), Spread<string>.Empty);
        }

        [TestCaseSource(nameof(Lists))]
        public void Property_List_IsASpreadOfTheElementType(byte[] value, object expected)
        {
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("l", Protocol.ValueType.List, value), out var name, out var v), Is.True);
            Assert.That(name, Is.EqualTo("l"));
            Assert.That(v.GetType(), Is.EqualTo(expected.GetType()));
            Assert.That(v, Is.EqualTo(expected));
            // The decoded value is what QmlTypes coerces to a list pin: same type, equal content.
            Assert.That(QmlValue.TryConvert(v, out var a) && QmlValue.TryConvert(expected, out var b) && a == b, Is.True);
        }

        [TestCaseSource(nameof(Lists))]
        public void Signal_List_Argument_FollowedByAnother(byte[] value, object expected)
        {
            var payload = QmlWire.Signal("sig", (Protocol.ValueType.I32, QmlWire.I32(5)), (Protocol.ValueType.List, value), (Protocol.ValueType.Bool, QmlWire.Bool(true)));
            Assert.That(QmlEventDecoder.TryDecodeSignal(payload, out var s), Is.True);
            Assert.That(s.Arguments.Count, Is.EqualTo(3));
            Assert.That(s.Arguments[0], Is.EqualTo(5));
            Assert.That(s.Arguments[1], Is.EqualTo(expected));
            Assert.That(s.Arguments[1].GetType(), Is.EqualTo(expected.GetType()));
            Assert.That(s.Arguments[2], Is.EqualTo(true), "the argument after a list starts on its 4-byte boundary");
        }

        [Test]
        public void Property_List_MaxCount_IsAccepted_MoreIsRejected()
        {
            var max = QmlWire.List(Protocol.ValueType.F32, Protocol.ListValueMaxCount, new byte[Protocol.ListValueMaxCount * 4]);
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("l", Protocol.ValueType.List, max), out _, out var v), Is.True);
            Assert.That(((Spread<float>)v).Count, Is.EqualTo(Protocol.ListValueMaxCount));
            var more = QmlWire.List(Protocol.ValueType.F32, Protocol.ListValueMaxCount + 1, new byte[(Protocol.ListValueMaxCount + 1) * 4]);
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("l", Protocol.ValueType.List, more), out _, out _), Is.False);
            var huge = QmlWire.List(Protocol.ValueType.F32, int.MaxValue, QmlWire.F(1));
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("l", Protocol.ValueType.List, huge), out _, out _), Is.False, "a count far beyond the payload");
            var negative = QmlWire.List(Protocol.ValueType.I32, -1, QmlWire.I32(1));
            Assert.That(QmlEventDecoder.TryDecodeProperty(QmlWire.Property("l", Protocol.ValueType.List, negative), out _, out _), Is.False, "count with the high bit set");
        }

        [Test]
        public void Property_List_Malformed_IsRejected()
        {
            bool Decodes(byte[] value) => QmlEventDecoder.TryDecodeProperty(QmlWire.Property("l", Protocol.ValueType.List, value), out _, out _);
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.Vec2, 2, QmlWire.F(1, 2), QmlWire.F(3, 4))), Is.True);
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.Vec2, 2, QmlWire.F(1, 2), QmlWire.F(3))), Is.False, "last element truncated");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.Vec2, 3, QmlWire.F(1, 2), QmlWire.F(3, 4))), Is.False, "count larger than the elements");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.Bool, 4, QmlWire.Bool(true), QmlWire.Bool(true), QmlWire.Bool(true))), Is.False, "bool list too short");
            Assert.That(Decodes(new byte[] { 1, 0, 0, 0, 0, 0, 0 }), Is.False, "shorter than the list header");
            Assert.That(Decodes(Array.Empty<byte>()), Is.False, "no list header at all");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.List, 1, QmlWire.List(Protocol.ValueType.I32, 0))), Is.False, "a list of lists");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.Mat4, 0)), Is.False, "element type that is not a plain value");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.Ref, 1, QmlWire.I32(1))), Is.False, "element type Ref");
            Assert.That(Decodes(QmlWire.List(0, 0)), Is.False, "element type 0");
            Assert.That(Decodes(QmlWire.List(999, 0)), Is.False, "unknown element type");
        }

        [Test]
        public void Property_List_Strings_Malformed_IsRejected()
        {
            bool Decodes(byte[] value) => QmlEventDecoder.TryDecodeProperty(QmlWire.Property("l", Protocol.ValueType.List, value), out _, out _);
            var a = QmlWire.Pad(QmlWire.Str("abc"));
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.String, 2, a, a)), Is.True);
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.String, 3, a, a)), Is.False, "third string missing");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.String, 1, QmlWire.I32(1000), new byte[] { 65, 0, 0, 0 })), Is.False, "length beyond the payload");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.String, 1, QmlWire.I32(-1))), Is.False, "length with the high bit set");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.String, 1, new byte[] { 3, 0 })), Is.False, "length cut off");
            Assert.That(Decodes(QmlWire.List(Protocol.ValueType.String, 1, QmlWire.Str("abc"))), Is.False, "the padding of the last string is missing");
        }

        [Test]
        public void Dispatch_List_Property_And_Signal()
        {
            var registry = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = registry.Acquire(new FeedbackSlot(feedback, 0));
            var res = registry.Dispatch(new FeedbackRouterTests.Poll()
                .Add(Protocol.EventId.QmlProperty, h, QmlWire.Property("pts", Protocol.ValueType.List, QmlWire.List(Protocol.ValueType.Vec2, 1, QmlWire.F(1, 2))))
                .Add(Protocol.EventId.QmlSignal, h, QmlWire.Signal("done", (Protocol.ValueType.List, QmlWire.List(Protocol.ValueType.I32, 2, QmlWire.I32(1), QmlWire.I32(2)))))
                .Add(Protocol.EventId.QmlProperty, h, QmlWire.Property("bad", Protocol.ValueType.List, QmlWire.List(Protocol.ValueType.Vec2, 5, QmlWire.F(1, 2)))).Bytes);
            Assert.That(res.Applied, Is.EqualTo(2));
            Assert.That(res.Ignored, Is.EqualTo(1));
            Assert.That(feedback.QmlPropertyValues()!["pts"], Is.EqualTo(new[] { new Vector2(1, 2) }.ToSpread()));
            Assert.That(feedback.TakeQmlSignals().Single().Arguments[0], Is.EqualTo(new[] { 1, 2 }.ToSpread()));
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
