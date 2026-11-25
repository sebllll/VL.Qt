using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>FeedbackRouter: feedback ids, routing of poll records into feedback, pulse logic, rebind (06a).</summary>
    public class FeedbackRouterTests
    {
        /// <summary>Builds a synthetic poll buffer like QtNative: u16 event, u16 flags, u32 len, u32 feedbackId, payload, 4-aligned.</summary>
        internal sealed class Poll
        {
            private readonly List<byte> _b = new();

            public Poll Add(ushort ev, uint handle, byte[] payload)
            {
                var rec = new byte[Protocol.HeaderSize + Protocol.EventFieldsSize + payload.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(rec, ev);
                BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)(Protocol.EventFieldsSize + payload.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), handle);
                payload.CopyTo(rec, 12);
                _b.AddRange(rec);
                while (_b.Count % 4 != 0) _b.Add(0);
                return this;
            }

            public Poll Measured(uint h, float w, float hgt) => Add(Protocol.EventId.Measured, h, F(w, hgt));
            public Poll Bounds(uint h, float x, float y, float w, float hgt) => Add(Protocol.EventId.SceneBounds, h, F(x, y, w, hgt));
            public Poll Hover(uint h, bool v) => Add(Protocol.EventId.Hover, h, new[] { (byte)(v ? 1 : 0) });
            public Poll Press(uint h, bool v) => Add(Protocol.EventId.Press, h, new[] { (byte)(v ? 1 : 0) });
            public Poll Click(uint h, uint delta) { var p = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(p, delta); return Add(Protocol.EventId.Click, h, p); }
            public Poll TapCount(uint h, uint count) { var p = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(p, count); return Add(Protocol.EventId.TapCount, h, p); }
            public Poll LongPress(uint h, uint count) { var p = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(p, count); return Add(Protocol.EventId.LongPress, h, p); }
            public Poll Active(uint h, bool v) => Add(Protocol.EventId.Active, h, new[] { (byte)(v ? 1 : 0) });
            public Poll Wheel(uint h, float x, float y) => Add(Protocol.EventId.Wheel, h, F(x, y));
            public Poll Pointer(uint h, float x, float y) => Add(Protocol.EventId.Pointer, h, F(x, y));
            public Poll Drag(uint h, float x, float y) => Add(Protocol.EventId.Drag, h, F(x, y));
            public Poll Pinch(uint h, float scale, float degrees, float tx, float ty, float px = 0, float py = 0) => Add(Protocol.EventId.Pinch, h, F(scale, degrees, tx, ty, px, py));

            /// <summary>EVENT_TOUCH_POINTS with points (id, x, y, pressure, w, h).</summary>
            public Poll TouchPoints(uint h, params (uint Id, float X, float Y, float Pressure, float W, float H)[] points)
            {
                var p = new byte[4 + 24 * points.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)points.Length);
                for (int i = 0; i < points.Length; i++)
                {
                    var s = p.AsSpan(4 + 24 * i);
                    BinaryPrimitives.WriteUInt32LittleEndian(s, points[i].Id);
                    F(points[i].X, points[i].Y, points[i].Pressure, points[i].W, points[i].H).CopyTo(s[4..]);
                }
                return Add(Protocol.EventId.TouchPoints, h, p);
            }

            /// <summary>EVENT_VALUE: u16 value type, u16 0, then <paramref name="value"/> encoded like SET.</summary>
            public Poll Value(uint h, ushort type, byte[] value)
            {
                var p = new byte[4 + value.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(p, type);
                value.CopyTo(p, 4);
                return Add(Protocol.EventId.Value, h, p);
            }

            public Poll ValueI32(uint h, int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return Value(h, Protocol.ValueType.I32, b); }

            public Poll ValueString(uint h, string v)
            {
                var utf8 = System.Text.Encoding.UTF8.GetBytes(v);
                var b = new byte[4 + utf8.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)utf8.Length);
                utf8.CopyTo(b, 4);
                return Value(h, Protocol.ValueType.String, b);
            }

            public Poll Error(uint h, string text) => Add(Protocol.EventId.Error, h, System.Text.Encoding.UTF8.GetBytes(text));

            public byte[] Bytes => _b.ToArray();

            private static byte[] F(params float[] v)
            {
                var p = new byte[v.Length * 4];
                for (int i = 0; i < v.Length; i++)
                    BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(i * 4), v[i]);
                return p;
            }
        }

        [Test]
        public void AllEventTypes_WrittenIntoSlot_InUnits()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 2));
            var res = reg.Dispatch(new Poll()
                .Measured(h, 120, 40)
                .Bounds(h, 10, 20, 120, 40)
                .Hover(h, true)
                .Press(h, true)
                .Pointer(h, 50, 25)
                .Click(h, 1).Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(6, 0, null)));
            Assert.That(feedback.MeasuredSize(2), Is.EqualTo(new Vector2(1.2f, 0.4f)));
            Assert.That(feedback.SceneBounds(2), Is.EqualTo(new RectangleF(0.1f, 0.2f, 1.2f, 0.4f)));
            Assert.That(feedback.IsHovered(2) && feedback.IsPressed(2), Is.True);
            Assert.That(feedback.PointerPosition(2), Is.EqualTo(new Vector2(0.5f, 0.25f)));
            Assert.That(feedback.TakeClicks(2), Is.EqualTo(1));
            // Other slots untouched.
            Assert.That(feedback.IsHovered(0) || feedback.IsHovered(), Is.False);

            reg.Dispatch(new Poll().Hover(h, false).Press(h, false).Bytes);
            Assert.That(feedback.IsHovered(2) || feedback.IsPressed(2), Is.False);
        }

        [Test]
        public void Value_DecodesEachTypeAndKeepsTheLast()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));
            Assert.That(feedback.TakeValue<int>(out _), Is.False);

            reg.Dispatch(new Poll().ValueI32(h, 3).ValueI32(h, -7).Bytes);
            Assert.That(feedback.TakeValue<int>(out int i), Is.True);
            Assert.That(i, Is.EqualTo(-7), "last change wins");
            Assert.That(feedback.TakeValue<int>(out _), Is.False, "taken");

            reg.Dispatch(new Poll().Value(h, Protocol.ValueType.F32, BitConverter.GetBytes(0.25f)).Bytes);
            Assert.That(feedback.TakeValue<float>(out float f) && f == 0.25f, Is.True);
            reg.Dispatch(new Poll().Value(h, Protocol.ValueType.Bool, new byte[] { 1 }).Bytes);
            Assert.That(feedback.TakeValue<bool>(out bool b) && b, Is.True);
            reg.Dispatch(new Poll().ValueString(h, "ä b").Bytes);
            Assert.That(feedback.TakeValue<string>(out string? str), Is.True);
            Assert.That(str, Is.EqualTo("ä b"));

            reg.Dispatch(new Poll().ValueI32(h, 5).Bytes);
            Assert.That(feedback.TakeValue<float>(out _), Is.False, "type mismatch is dropped");
            Assert.That(feedback.TakeValue<int>(out _), Is.False);

            var res = reg.Dispatch(new Poll().Value(h, Protocol.ValueType.I32, new byte[2]).Value(h, Protocol.ValueType.Color, new byte[16])
                .Value(h, Protocol.ValueType.String, new byte[] { 9, 0, 0, 0, 65 }).Bytes);
            Assert.That((res.Applied, res.Ignored), Is.EqualTo((0, 3)), "short payload, unknown type, string longer than the payload");
        }

        [Test]
        public void TakeClicks_ReturnsPendingOnce()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));

            Assert.That(feedback.TakeClicks(0), Is.Zero);

            reg.Dispatch(new Poll().Click(h, 1).Click(h, 2).Bytes);
            Assert.That(feedback.TakeClicks(0), Is.EqualTo(3));
            Assert.That(feedback.TakeClicks(0), Is.Zero); // taken

            // Empty frame: nothing.
            reg.Dispatch(ReadOnlySpan<byte>.Empty);
            Assert.That(feedback.TakeClicks(0), Is.Zero);

            // Pending clicks survive frames nobody takes them in.
            reg.Dispatch(new Poll().Click(h, 1).Bytes);
            reg.Dispatch(ReadOnlySpan<byte>.Empty);
            Assert.That(feedback.TakeClicks(0), Is.EqualTo(1));

            // Delta 0 does nothing.
            reg.Dispatch(new Poll().Click(h, 0).Bytes);
            Assert.That(feedback.TakeClicks(0), Is.Zero);
        }

        [Test]
        public void UnknownHandle_Event_ShortPayload_Ignored()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));
            var res = reg.Dispatch(new Poll()
                .Hover(h + 1, true)                                     // unknown handle
                .Hover(0, true)                                         // handle 0
                .Add(99, h, new byte[] { 1 })                           // unknown event
                .Add(Protocol.EventId.Measured, h, new byte[4])              // payload too short
                .Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(0, 4, null)));
            Assert.That(feedback.IsHovered(), Is.False);
            Assert.That(feedback.MeasuredSize(), Is.EqualTo(Vector2.Zero));
            Assert.That(feedback.Count, Is.Zero); // nothing allocated for ignored records
        }

        [Test]
        public void MalformedRecord_StopsWithError_EarlierRecordsApplied()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));
            var bytes = new Poll().Hover(h, true).Bytes;
            var buf = new byte[bytes.Length + 6];
            bytes.CopyTo(buf, 0); // + truncated header

            var res = reg.Dispatch(buf);
            Assert.That(res.Applied, Is.EqualTo(1));
            Assert.That(res.Error, Is.Not.Null);
            Assert.That(feedback.IsHovered(), Is.True);
        }

        [Test]
        public void Error_SetClearedByEmpty_ClearedOnRelease()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));
            Assert.That(feedback.Error, Is.Null);

            Assert.That(reg.Dispatch(new Poll().Error(h, "Cannot open C:/ä b.png").Bytes).Applied, Is.EqualTo(1));
            Assert.That(feedback.Error, Is.EqualTo("Cannot open C:/ä b.png"), "UTF-8");

            reg.Dispatch(new Poll().Error(h, "").Bytes);
            Assert.That(feedback.Error, Is.Null, "empty = cleared");

            reg.Dispatch(new Poll().Error(h, "x").Bytes);
            reg.Release(h);
            Assert.That(feedback.Error, Is.Null, "the element is gone");
        }

        [Test]
        public void NotPrimary_DropsStateKeepsEvents_ReleaseLeavesSlot_DemotionResetsIt()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint p = reg.Acquire(new FeedbackSlot(feedback, 0));
            uint o = reg.Acquire(new FeedbackSlot(feedback, 0), primary: false);
            var res = reg.Dispatch(new Poll().Measured(o, 5, 5).Error(o, "o").Click(o, 1).ValueI32(o, 7).Error(p, "p").Bytes);
            Assert.That(res.Applied, Is.EqualTo(3));
            Assert.That(feedback.Error, Is.EqualTo("p"));
            Assert.That(feedback[0].MeasuredSize, Is.EqualTo(default(Vector2)));
            Assert.That(feedback[0].PendingClicks, Is.EqualTo(1));
            Assert.That(feedback[0].HasPendingValue, Is.True);

            reg.Release(o);
            Assert.That(feedback.Error, Is.EqualTo("p"), "a further occurrence leaves the slot alone");

            // Demoted: another occurrence becomes primary with its own BIND, so the slot drops what p wrote.
            reg.SetPrimary(p, false);
            Assert.That(reg.IsPrimary(p), Is.False);
            Assert.That(feedback.Error, Is.Null);
            var q = reg.Acquire(new FeedbackSlot(feedback, 0));
            reg.Dispatch(new Poll().Error(q, "q").Bytes);
            reg.Release(p);
            Assert.That(feedback.Error, Is.EqualTo("q"), "the demoted id's release leaves the new primary's state");
        }

        [Test]
        public void Hover_SeveralIds_OrOverOccurrences_OrderDoesNotMatter()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint a = reg.Acquire(new FeedbackSlot(feedback, 0));
            uint b = reg.Acquire(new FeedbackSlot(feedback, 0), primary: false);

            // The pointer moves from A to B within one poll: "B enter, A leave" and "A leave, B enter" both end hovered.
            reg.Dispatch(new Poll().Hover(a, true).Bytes);
            reg.Dispatch(new Poll().Hover(b, true).Hover(a, false).Bytes);
            Assert.That(feedback.IsHovered(), Is.True, "B enter, A leave");
            reg.Dispatch(new Poll().Hover(b, false).Hover(a, true).Bytes);
            reg.Dispatch(new Poll().Hover(a, false).Hover(b, true).Bytes);
            Assert.That(feedback.IsHovered(), Is.True, "A leave, B enter");

            reg.Dispatch(new Poll().Hover(b, false).Bytes);
            Assert.That(feedback.IsHovered(), Is.False);
            Assert.That(feedback[0].HoverCount, Is.Zero);

            // Repeated records of one id count once.
            reg.Dispatch(new Poll().Hover(a, true).Hover(a, true).Hover(b, true).Bytes);
            reg.Dispatch(new Poll().Hover(a, false).Hover(a, false).Bytes);
            Assert.That(feedback.IsHovered(), Is.True, "B is still hovered");
            Assert.That(feedback[0].HoverCount, Is.EqualTo(1));
        }

        [Test]
        public void Press_SeveralIds_OrOverOccurrences()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint a = reg.Acquire(new FeedbackSlot(feedback, 0));
            uint b = reg.Acquire(new FeedbackSlot(feedback, 0), primary: false);

            reg.Dispatch(new Poll().Press(b, true).Press(a, true).Bytes);
            reg.Dispatch(new Poll().Press(a, false).Bytes);
            Assert.That(feedback.IsPressed(), Is.True, "B still pressed");
            reg.Dispatch(new Poll().Press(b, false).Bytes);
            Assert.That(feedback.IsPressed(), Is.False);
        }

        [Test]
        public void Release_OneOfTwoHoveredIds_SlotStaysHoveredUntilTheOtherLetsGo()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint a = reg.Acquire(new FeedbackSlot(feedback, 0)); // primary
            uint b = reg.Acquire(new FeedbackSlot(feedback, 0), primary: false);
            reg.Dispatch(new Poll().Hover(a, true).Press(a, true).Hover(b, true).Bytes);

            reg.Release(a);
            Assert.That(feedback.IsHovered(), Is.True, "B is hovered");
            Assert.That(feedback.IsPressed(), Is.False, "only A was pressed");

            reg.Release(b);
            Assert.That(feedback.IsHovered(), Is.False);
            Assert.That(feedback[0].HoverCount, Is.Zero);
            Assert.That(feedback[0].PressCount, Is.Zero);
        }

        [Test]
        public void Demotion_KeepsHoverOfTheDemotedId()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint a = reg.Acquire(new FeedbackSlot(feedback, 0));
            reg.Dispatch(new Poll().Hover(a, true).Bytes);

            reg.SetPrimary(a, false); // its Qt object is still hovered
            Assert.That(feedback.IsHovered(), Is.True);
            reg.Dispatch(new Poll().Hover(a, false).Bytes);
            Assert.That(feedback.IsHovered(), Is.False);
        }

        [Test]
        public void Clear_TakesBackHoverAndPress_AndDropsLateEvents()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint a = reg.Acquire(new FeedbackSlot(feedback, 0));
            uint b = reg.Acquire(new FeedbackSlot(feedback, 0), primary: false);
            reg.Dispatch(new Poll().Hover(a, true).Hover(b, true).Press(b, true).Bytes);

            reg.Clear();
            Assert.That(feedback.IsHovered() || feedback.IsPressed(), Is.False);
            Assert.That(reg.Count, Is.Zero);
            Assert.That(reg.Dispatch(new Poll().Hover(a, true).Bytes).Ignored, Is.EqualTo(1));
        }

        [Test]
        public void ApplyError_TakesPrecedence_SurvivesRelease()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));
            reg.Dispatch(new Poll().Error(h, "Cannot open x.png").Bytes);
            feedback[0].ApplyError = "Qt rejected Color: bad";
            Assert.That(feedback.Error, Is.EqualTo("Qt rejected Color: bad"), "#275: apply error first");

            reg.Release(h); // resync: the mirror drops its bindings, the apply error stays until the renderer clears it
            Assert.That(feedback.Error, Is.EqualTo("Qt rejected Color: bad"));
            feedback[0].ApplyError = null;
            Assert.That(feedback.Error, Is.Null);
        }

        [Test]
        public void Release_ResetsHoverPress_DropsLateEvents_HandlesNotReused()
        {
            var reg = new FeedbackRouter();
            var a = new ElementFeedback();
            var b = new ElementFeedback();
            uint ha = reg.Acquire(new FeedbackSlot(a, 0));
            reg.Dispatch(new Poll().Hover(ha, true).Press(ha, true).Bytes);

            reg.Release(ha);
            Assert.That(a.IsHovered() || a.IsPressed(), Is.False);
            Assert.That(reg.Count, Is.Zero);

            uint hb = reg.Acquire(new FeedbackSlot(b, 0));
            Assert.That(hb, Is.Not.EqualTo(ha));
            // QtNative's late EVENT_HOVER/EVENT_PRESS for the old handle (and a stale click) reach nobody.
            var res = reg.Dispatch(new Poll().Hover(ha, true).Click(ha, 1).Bytes);
            Assert.That(res.Ignored, Is.EqualTo(2));
            Assert.That(a.IsHovered() || b.IsHovered() || a.TakeClicks(0) > 0 || b.TakeClicks(0) > 0, Is.False);

            reg.Release(ha); // double release / unknown: no-op
            reg.Release(0);
            Assert.That(reg.Count, Is.EqualTo(1));
        }

        [Test]
        public void HandleWrapAround_SkipsZeroAndLiveHandles()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint first = reg.Acquire(new FeedbackSlot(feedback, 0)); // 1, stays live
            Assert.That(first, Is.EqualTo(1u));
            typeof(FeedbackRouter).GetField("_last", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(reg, uint.MaxValue - 1);
            Assert.That(reg.Acquire(new FeedbackSlot(feedback, 1)), Is.EqualTo(uint.MaxValue));
            Assert.That(reg.Acquire(new FeedbackSlot(feedback, 2)), Is.EqualTo(2u)); // skips 0 and live 1
        }

        [Test]
        public void Warning_FirstSlotWithWarning()
        {
            var feedback = new ElementFeedback();
            Assert.That(feedback.Warning, Is.Null);
            feedback[1].SetMessage(this, MessageKind.Warning, "dup");
            Assert.That(feedback.Warning, Is.EqualTo("dup"));
            feedback[1].SetMessage(this, MessageKind.Warning, null);
            Assert.That(feedback.Warning, Is.Null);
        }

        [Test]
        public void Messages_OnePerSourceAndKind_JoinedInOrder()
        {
            var state = new FeedbackState();
            var a = new object();
            var b = new object();
            state.SetMessage(a, MessageKind.Warning, "A");
            state.SetMessage(b, MessageKind.Warning, "B");
            state.SetMessage(a, MessageKind.BuildError, "E");
            Assert.That(state.Warning, Is.EqualTo("A B"));
            Assert.That(state.BuildError, Is.EqualTo("E"));

            state.SetMessage(a, MessageKind.Warning, "A2");
            Assert.That(state.Warning, Is.EqualTo("A2 B"), "a changed message replaces the old one");

            state.SetMessage(a, MessageKind.Warning, null);
            Assert.That(state.Warning, Is.EqualTo("B"), "removing one source keeps the other's message");
            state.SetMessage(b, MessageKind.Warning, null);
            state.SetMessage(a, MessageKind.BuildError, null);
            Assert.That(state.Warning, Is.Null);
            Assert.That(state.BuildError, Is.Null);
        }

        [Test]
        public void SlotSetMessage_DefaultIsNone_RemovingCreatesNoSlot()
        {
            default(FeedbackSlot).SetMessage(this, MessageKind.Warning, "x");

            var feedback = new ElementFeedback();
            new FeedbackSlot(feedback, 2).SetMessage(this, MessageKind.Warning, null);
            Assert.That(feedback.Count, Is.EqualTo(0));
            new FeedbackSlot(feedback, 2).SetMessage(this, MessageKind.Warning, "x");
            Assert.That(feedback.Warning, Is.EqualTo("x"));
        }

        [Test]
        public void Missing_Slot_ReturnsDefaults_WithoutAllocating()
        {
            var feedback = new ElementFeedback();
            Assert.That(feedback.IsHovered(3) || feedback.IsPressed(3) || feedback.TakeClicks(3) > 0, Is.False);
            Assert.That(feedback.MeasuredSize(3), Is.EqualTo(Vector2.Zero));
            Assert.That(feedback.SceneBounds(3), Is.EqualTo(default(RectangleF)));
            Assert.That(feedback.PointerPosition(-1), Is.EqualTo(Vector2.Zero));
            Assert.That(feedback.Count, Is.Zero);
        }

        // --- with the reconciler ---

        [Test]
        public void HandlerEvents_TapPointWheel_LandInTheSlot()
        {
            var feedback = new ElementFeedback();
            var reg = new FeedbackRouter();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));

            var res = reg.Dispatch(new Poll().Press(h, true).TapCount(h, 2).LongPress(h, 1).LongPress(h, 2).Active(h, true)
                .Pointer(h, 100, 200).Wheel(h, 0, 1).Wheel(h, 0.5f, 1).Bytes);
            Assert.That(res.Ignored, Is.Zero);
            Assert.That(feedback.IsPressed() && feedback.IsActive(), Is.True);
            Assert.That(feedback.TapCount(), Is.EqualTo(2));
            Assert.That(feedback.TakeLongPresses(), Is.EqualTo(3), "summed until taken");
            Assert.That(feedback.TakeLongPresses(), Is.Zero);
            Assert.That(feedback.PointerPosition(), Is.EqualTo(new Vector2(1, 2)), "pixels -> units");
            Assert.That(feedback.TakeWheel(), Is.EqualTo(new Vector2(0.5f, 2)), "notches summed, not converted");
            Assert.That(feedback.TakeWheel(), Is.EqualTo(Vector2.Zero));

            // Too short payloads are ignored.
            res = reg.Dispatch(new Poll().Add(Protocol.EventId.Wheel, h, new byte[4]).Add(Protocol.EventId.TapCount, h, new byte[2]).Add(Protocol.EventId.Active, h, new byte[0]).Bytes);
            Assert.That(res.Applied, Is.Zero);
            Assert.That(res.Ignored, Is.EqualTo(3));
        }

        [Test]
        public void DragEvents_LandInTheSlot_CountedPerEvent()
        {
            var feedback = new ElementFeedback();
            var reg = new FeedbackRouter();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));

            var res = reg.Dispatch(new Poll().Active(h, true).Drag(h, 100, -50).Drag(h, 100, -50).Bytes);
            Assert.That(res.Ignored, Is.Zero);
            Assert.That(feedback.IsActive(), Is.True);
            Assert.That(feedback.DragDelta(), Is.EqualTo(new Vector2(1, -0.5f)), "pixels -> units");
            Assert.That(feedback.DragUpdates(), Is.EqualTo(2), "equal values count too");

            res = reg.Dispatch(new Poll().Add(Protocol.EventId.Drag, h, new byte[4]).Bytes);
            Assert.That(res.Ignored, Is.EqualTo(1), "too short");

            reg.Release(h);
            Assert.That(feedback.IsActive(), Is.False);
            Assert.That(feedback.DragDelta(), Is.EqualTo(new Vector2(1, -0.5f)), "the last drag stays until the next one");
        }

        [Test]
        public void TouchPointsEvent_BecomesSpread_InUnits_EmptyOnZero()
        {
            var feedback = new ElementFeedback();
            var reg = new FeedbackRouter();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));

            var res = reg.Dispatch(new Poll().TouchPoints(h, (3, 50, 25, 0.5f, 10, 20), (7, 100, 0, 1, 0, 0)).Bytes);
            Assert.That(res, Is.EqualTo(new DispatchResult(1, 0, null)));
            var points = feedback.TouchPoints();
            Assert.That(points, Has.Count.EqualTo(2));
            Assert.That(points[0], Is.EqualTo(new Elements.TouchPoint(3, new Vector2(0.5f, 0.25f), 0.5f, new Vector2(0.1f, 0.2f))), "pixels -> units");
            Assert.That(points[1].Id, Is.EqualTo(7));
            Assert.That(feedback.TouchUpdates(), Is.EqualTo(1));

            reg.Dispatch(new Poll().TouchPoints(h).Bytes);
            Assert.That(feedback.TouchPoints(), Is.Empty);
            Assert.That(feedback.TouchUpdates(), Is.EqualTo(2));
            reg.Dispatch(new Poll().TouchPoints(h).Bytes);
            Assert.That(feedback.TouchUpdates(), Is.EqualTo(2), "empty again: no change");

            // Too short / too many points are ignored.
            res = reg.Dispatch(new Poll().Add(Protocol.EventId.TouchPoints, h, new byte[3])
                .Add(Protocol.EventId.TouchPoints, h, new byte[] { 2, 0, 0, 0, 1, 2, 3, 4 })
                .Add(Protocol.EventId.TouchPoints, h, new byte[] { 65, 0, 0, 0 }).Bytes);
            Assert.That(res.Applied, Is.Zero);
            Assert.That(res.Ignored, Is.EqualTo(3));

            // Release clears the points of a handle that still holds some.
            reg.Dispatch(new Poll().TouchPoints(h, (1, 0, 0, 0, 0, 0)).Bytes);
            reg.Release(h);
            Assert.That(feedback.TouchPoints(), Is.Empty);
        }

        [Test]
        public void PinchEvent_DegreesToCycles_TranslationInUnits_KeptAfterRelease()
        {
            var feedback = new ElementFeedback();
            var reg = new FeedbackRouter();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));
            Assert.That(feedback.PinchScale(), Is.EqualTo(1f), "no gesture yet");

            var res = reg.Dispatch(new Poll().Active(h, true).Pinch(h, 1.5f, 90, 20, -40, 100, 50).Bytes);
            Assert.That(res.Ignored, Is.Zero);
            Assert.That(feedback.PinchScale(), Is.EqualTo(1.5f));
            Assert.That(feedback.PinchRotation(), Is.EqualTo(0.25f).Within(1e-6f), "90 degrees = 0.25 cycles");
            Assert.That(feedback.PinchTranslation(), Is.EqualTo(new Vector2(0.2f, -0.4f)));
            Assert.That(feedback.PinchPivot(), Is.EqualTo(new Vector2(1f, 0.5f)));
            Assert.That(feedback.PinchUpdates(), Is.EqualTo(1));

            reg.Dispatch(new Poll().Active(h, false).Bytes);
            Assert.That(feedback.PinchScale(), Is.EqualTo(1.5f), "the last value stays");
            Assert.That(reg.Dispatch(new Poll().Add(Protocol.EventId.Pinch, h, new byte[16]).Bytes).Ignored, Is.EqualTo(1), "too short");
        }

        [Test]
        public void ReleaseHandle_ResetsHandlerState()
        {
            var feedback = new ElementFeedback();
            var reg = new FeedbackRouter();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 0));
            reg.Dispatch(new Poll().Press(h, true).Active(h, true).TapCount(h, 3).LongPress(h, 1).Wheel(h, 1, 1).Bytes);

            reg.Release(h);
            Assert.That(feedback.IsPressed() || feedback.IsActive(), Is.False);
            Assert.That(feedback.TapCount(), Is.Zero);
            Assert.That(feedback.TakeLongPresses(), Is.Zero);
            Assert.That(feedback.TakeWheel(), Is.EqualTo(Vector2.Zero));
        }

        private static Rectangle R(float w, FeedbackSlot feedback)
            => new() { Size = new Vector2(w, 1), Feedback = feedback };

        [Test]
        public void Rebind_FeedbackChange_RoutesToNewFeedback_OldFeedbackReset()
        {
            var cmds = new RecordingSink();
            var rec = new Reconciler(cmds, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            var a = new ElementFeedback();
            var b = new ElementFeedback();

            rec.Update(R(1, new FeedbackSlot(a, 0)), Space.DIPTopLeft);
            uint ha = rec.Root!.BoundFeedbackId;
            rec.FeedbackRouter.Dispatch(new Poll().Hover(ha, true).Press(ha, true).Bytes);
            Assert.That(a.IsHovered() && a.IsPressed(), Is.True);

            // Same object, other feedback: BIND with a new handle (no flags); old feedback loses hover/press right away.
            cmds.Clear();
            rec.Update(R(1, new FeedbackSlot(b, 0)), Space.DIPTopLeft);
            uint hb = rec.Root!.BoundFeedbackId;
            Assert.That(hb, Is.Not.EqualTo(ha));
            Assert.That(cmds.Commands, Does.Contain($"BIND {rec.Root.Id} {hb}"));
            Assert.That(a.IsHovered() || a.IsPressed(), Is.False);

            // Next poll: QtNative's reset for the old handle is dropped, new events go to b.
            rec.FeedbackRouter.Dispatch(new Poll().Hover(ha, false).Press(ha, false).Hover(hb, true).Click(hb, 1).Bytes);
            Assert.That(b.IsHovered(), Is.True);
            Assert.That(b.TakeClicks(0), Is.EqualTo(1));
            Assert.That(a.TakeClicks(0), Is.Zero);

            // Unbind and destroy release the handle.
            rec.Update(R(1, default), Space.DIPTopLeft);
            Assert.That(rec.FeedbackRouter.Count, Is.Zero);
            Assert.That(b.IsHovered(), Is.False);
            rec.Update(R(1, new FeedbackSlot(b, 0)), Space.DIPTopLeft);
            Assert.That(rec.FeedbackRouter.Count, Is.EqualTo(1));
            rec.Update(null, Space.DIPTopLeft);
            Assert.That(rec.FeedbackRouter.Count, Is.Zero);
        }

        [Test]
        public void Record_GoesToTheBoundSlot()
        {
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            uint h = reg.Acquire(new FeedbackSlot(feedback, 2));
            var res = reg.Dispatch(new Poll().Hover(h, true).Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(1, 0, null)));
            Assert.That(feedback.IsHovered(2), Is.True);
            Assert.That(feedback.IsHovered(3), Is.False);
        }

        [Test]
        public void MultiElementFeedback_EachElementRoutedToItsSlot()
        {
            // One feedback for a spread of three elements (slot index = spread index), like a ForEach in vvvv.
            var rec = new Reconciler(new RecordingSink(), new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            var feedback = new ElementFeedback();
            var col = new Column { Children = ImmutableArray.Create<Element>(R(1, new FeedbackSlot(feedback, 0)), R(2, new FeedbackSlot(feedback, 1)), R(3, new FeedbackSlot(feedback, 2))) };
            rec.Update(col, Space.DIPTopLeft);

            var kids = rec.Root!.Children;
            var poll = new Poll();
            for (int i = 0; i < 3; i++)
                poll.Measured(kids[i].BoundFeedbackId, 100 * (i + 1), 50).Bounds(kids[i].BoundFeedbackId, 0, 100 * i, 100 * (i + 1), 50)
                    .Hover(kids[i].BoundFeedbackId, i == 1);
            var res = rec.FeedbackRouter.Dispatch(poll.Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(9, 0, null)));
            Assert.That(rec.FeedbackRouter.Count, Is.EqualTo(3));
            for (int i = 0; i < 3; i++)
            {
                Assert.That(feedback.MeasuredSize(i), Is.EqualTo(new Vector2(i + 1, 0.5f)));
                Assert.That(feedback.SceneBounds(i), Is.EqualTo(new RectangleF(0, i, i + 1, 0.5f)));
                Assert.That(feedback.IsHovered(i), Is.EqualTo(i == 1));
            }

            // Spread order changes: slots move with the elements, new handles, late records of old handles dropped.
            uint old0 = kids[0].BoundFeedbackId;
            col = new Column { Children = ImmutableArray.Create<Element>(R(1, new FeedbackSlot(feedback, 1)), R(2, new FeedbackSlot(feedback, 0)), R(3, new FeedbackSlot(feedback, 2))) };
            rec.Update(col, Space.DIPTopLeft);
            Assert.That(feedback.IsHovered(1), Is.False); // released slot loses hover
            res = rec.FeedbackRouter.Dispatch(new Poll().Measured(old0, 9, 9).Measured(rec.Root!.Children[0].BoundFeedbackId, 700, 50).Bytes);
            Assert.That(res, Is.EqualTo(new DispatchResult(1, 1, null)));
            Assert.That(feedback.MeasuredSize(1), Is.EqualTo(new Vector2(7, 0.5f)));
            Assert.That(feedback.MeasuredSize(0), Is.EqualTo(new Vector2(1, 0.5f))); // kept until QtNative resends
        }

        [Test]
        public void Dispatch_1000Records_NoAllocations()
        {
            const int n = 1000;
            var reg = new FeedbackRouter();
            var feedback = new ElementFeedback();
            var p = new Poll();
            for (int i = 0; i < n; i++)
            {
                uint h = reg.Acquire(new FeedbackSlot(feedback, i));
                p.Bounds(h, i, 0, 10, 10).Hover(h, (i & 1) == 0);
            }
            var buf = p.Bytes;
            reg.Dispatch(buf); // warm-up (slots allocated)

            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int frames = 100;
            for (int f = 0; f < frames; f++)
                reg.Dispatch(buf);
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext.Progress.WriteLine($"Dispatch {2 * n} records: {sw.Elapsed.TotalMilliseconds / frames:F4} ms/frame, {allocated} bytes");
            Assert.That(allocated, Is.LessThan(1024));
        }
    }
}
