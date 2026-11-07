using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>SinkRegistry: handles, routing of poll records into sinks, pulse logic, rebind (06a).</summary>
    public class SinkRegistryTests
    {
        /// <summary>Builds a synthetic poll buffer like QtNative: u16 event, u16 len, u32 handle, u32 index, payload, 4-aligned.</summary>
        internal sealed class Poll
        {
            readonly List<byte> _b = new();

            public Poll Add(ushort ev, uint handle, byte[] payload, uint index = 0)
            {
                var rec = new byte[12 + payload.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(rec, ev);
                BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2), (ushort)(8 + payload.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), handle);
                BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), index);
                payload.CopyTo(rec, 12);
                _b.AddRange(rec);
                while (_b.Count % 4 != 0) _b.Add(0);
                return this;
            }

            public Poll Measured(uint h, float w, float hgt) => Add(Protocol.Ev.Measured, h, F(w, hgt));
            public Poll Bounds(uint h, float x, float y, float w, float hgt) => Add(Protocol.Ev.SceneBounds, h, F(x, y, w, hgt));
            public Poll Hover(uint h, bool v) => Add(Protocol.Ev.Hover, h, new[] { (byte)(v ? 1 : 0) });
            public Poll Press(uint h, bool v) => Add(Protocol.Ev.Press, h, new[] { (byte)(v ? 1 : 0) });
            public Poll Click(uint h, uint delta) { var p = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(p, delta); return Add(Protocol.Ev.Click, h, p); }
            public Poll TapCount(uint h, uint count) { var p = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(p, count); return Add(Protocol.Ev.TapCount, h, p); }
            public Poll LongPress(uint h, uint count) { var p = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(p, count); return Add(Protocol.Ev.LongPress, h, p); }
            public Poll Active(uint h, bool v) => Add(Protocol.Ev.Active, h, new[] { (byte)(v ? 1 : 0) });
            public Poll Wheel(uint h, float x, float y) => Add(Protocol.Ev.Wheel, h, F(x, y));
            public Poll Pointer(uint h, float x, float y) => Add(Protocol.Ev.Pointer, h, F(x, y));
            public Poll Drag(uint h, float x, float y) => Add(Protocol.Ev.Drag, h, F(x, y));
            public Poll Pinch(uint h, float scale, float degrees, float tx, float ty) => Add(Protocol.Ev.Pinch, h, F(scale, degrees, tx, ty));

            /// <summary>EV_TOUCH_POINTS with points (id, x, y, pressure, w, h).</summary>
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
                return Add(Protocol.Ev.TouchPoints, h, p);
            }

            /// <summary>EV_VALUE: u16 value type, u16 0, then <paramref name="value"/> encoded like SET.</summary>
            public Poll Value(uint h, ushort type, byte[] value)
            {
                var p = new byte[4 + value.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(p, type);
                value.CopyTo(p, 4);
                return Add(Protocol.Ev.Value, h, p);
            }

            public Poll ValueI32(uint h, int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return Value(h, Protocol.Vt.I32, b); }

            public Poll ValueString(uint h, string v)
            {
                var utf8 = System.Text.Encoding.UTF8.GetBytes(v);
                var b = new byte[4 + utf8.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)utf8.Length);
                utf8.CopyTo(b, 4);
                return Value(h, Protocol.Vt.String, b);
            }

            public Poll Error(uint h, string text) => Add(Protocol.Ev.Error, h, System.Text.Encoding.UTF8.GetBytes(text));

            public byte[] Bytes => _b.ToArray();

            static byte[] F(params float[] v)
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
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 2));
            var res = reg.Dispatch(new Poll()
                .Measured(h, 120, 40)
                .Bounds(h, 10, 20, 120, 40)
                .Hover(h, true)
                .Press(h, true)
                .Pointer(h, 50, 25)
                .Click(h, 1).Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(6, 0, null)));
            Assert.That(sink.MeasuredSize(2), Is.EqualTo(new Vector2(1.2f, 0.4f)));
            Assert.That(sink.SceneBounds(2), Is.EqualTo(new RectangleF(0.1f, 0.2f, 1.2f, 0.4f)));
            Assert.That(sink.IsHovered(2) && sink.IsPressed(2), Is.True);
            Assert.That(sink.PointerPosition(2), Is.EqualTo(new Vector2(0.5f, 0.25f)));
            Assert.That(sink.TakeClicks(2), Is.EqualTo(1));
            // Other slots untouched.
            Assert.That(sink.IsHovered(0) || sink.IsHovered(), Is.False);

            reg.Dispatch(new Poll().Hover(h, false).Press(h, false).Bytes);
            Assert.That(sink.IsHovered(2) || sink.IsPressed(2), Is.False);
        }

        [Test]
        public void Value_DecodesEachTypeAndKeepsTheLast()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 0));
            Assert.That(sink.TakeValue<int>(out _), Is.False);

            reg.Dispatch(new Poll().ValueI32(h, 3).ValueI32(h, -7).Bytes);
            Assert.That(sink.TakeValue<int>(out int i), Is.True);
            Assert.That(i, Is.EqualTo(-7), "last change wins");
            Assert.That(sink.TakeValue<int>(out _), Is.False, "taken");

            reg.Dispatch(new Poll().Value(h, Protocol.Vt.F32, BitConverter.GetBytes(0.25f)).Bytes);
            Assert.That(sink.TakeValue<float>(out float f) && f == 0.25f, Is.True);
            reg.Dispatch(new Poll().Value(h, Protocol.Vt.Bool, new byte[] { 1 }).Bytes);
            Assert.That(sink.TakeValue<bool>(out bool b) && b, Is.True);
            reg.Dispatch(new Poll().ValueString(h, "ä b").Bytes);
            Assert.That(sink.TakeValue<string>(out string? str), Is.True);
            Assert.That(str, Is.EqualTo("ä b"));

            reg.Dispatch(new Poll().ValueI32(h, 5).Bytes);
            Assert.That(sink.TakeValue<float>(out _), Is.False, "type mismatch is dropped");
            Assert.That(sink.TakeValue<int>(out _), Is.False);

            var res = reg.Dispatch(new Poll().Value(h, Protocol.Vt.I32, new byte[2]).Value(h, Protocol.Vt.Color, new byte[16])
                .Value(h, Protocol.Vt.String, new byte[] { 9, 0, 0, 0, 65 }).Bytes);
            Assert.That((res.Applied, res.Ignored), Is.EqualTo((0, 3)), "short payload, unknown type, string longer than the payload");
        }

        [Test]
        public void TakeClicks_ReturnsPendingOnce()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 0));

            Assert.That(sink.TakeClicks(0), Is.Zero);

            reg.Dispatch(new Poll().Click(h, 1).Click(h, 2).Bytes);
            Assert.That(sink.TakeClicks(0), Is.EqualTo(3));
            Assert.That(sink.TakeClicks(0), Is.Zero); // taken

            // Empty frame: nothing.
            reg.Dispatch(ReadOnlySpan<byte>.Empty);
            Assert.That(sink.TakeClicks(0), Is.Zero);

            // Pending clicks survive frames nobody takes them in.
            reg.Dispatch(new Poll().Click(h, 1).Bytes);
            reg.Dispatch(ReadOnlySpan<byte>.Empty);
            Assert.That(sink.TakeClicks(0), Is.EqualTo(1));

            // Delta 0 does nothing.
            reg.Dispatch(new Poll().Click(h, 0).Bytes);
            Assert.That(sink.TakeClicks(0), Is.Zero);
        }

        [Test]
        public void UnknownHandle_Index_Event_ShortPayload_Ignored()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 0));
            var res = reg.Dispatch(new Poll()
                .Hover(h + 1, true)                                     // unknown handle
                .Hover(0, true)                                         // handle 0
                .Add(Protocol.Ev.Hover, h, new byte[] { 1 }, index: SinkRegistry.MaxIndexOffset + 1) // index too large
                .Add(Protocol.Ev.Hover, h, new byte[] { 1 }, index: uint.MaxValue)
                .Add(99, h, new byte[] { 1 })                           // unknown event
                .Add(Protocol.Ev.Measured, h, new byte[4])              // payload too short
                .Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(0, 6, null)));
            Assert.That(sink.IsHovered(), Is.False);
            Assert.That(sink.MeasuredSize(), Is.EqualTo(Vector2.Zero));
            Assert.That(sink.Count, Is.Zero); // nothing allocated for ignored records
        }

        [Test]
        public void MalformedRecord_StopsWithError_EarlierRecordsApplied()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 0));
            var bytes = new Poll().Hover(h, true).Bytes;
            var buf = new byte[bytes.Length + 6];
            bytes.CopyTo(buf, 0); // + truncated header

            var res = reg.Dispatch(buf);
            Assert.That(res.Applied, Is.EqualTo(1));
            Assert.That(res.Error, Is.Not.Null);
            Assert.That(sink.IsHovered(), Is.True);
        }

        [Test]
        public void Error_SetClearedByEmpty_ClearedOnRelease()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 0));
            Assert.That(sink.Error, Is.Null);

            Assert.That(reg.Dispatch(new Poll().Error(h, "Cannot open C:/ä b.png").Bytes).Applied, Is.EqualTo(1));
            Assert.That(sink.Error, Is.EqualTo("Cannot open C:/ä b.png"), "UTF-8");

            reg.Dispatch(new Poll().Error(h, "").Bytes);
            Assert.That(sink.Error, Is.Null, "empty = cleared");

            reg.Dispatch(new Poll().Error(h, "x").Bytes);
            reg.Release(h);
            Assert.That(sink.Error, Is.Null, "the element is gone");
        }

        [Test]
        public void NotPrimary_DropsStateKeepsEvents_ReleaseLeavesSlot()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint p = reg.Acquire(new SinkSlot(sink, 0));
            uint o = reg.Acquire(new SinkSlot(sink, 0), primary: false);
            var res = reg.Dispatch(new Poll().Measured(o, 5, 5).Error(o, "o").Click(o, 1).ValueI32(o, 7).Error(p, "p").Bytes);
            Assert.That(res.Applied, Is.EqualTo(3));
            Assert.That(sink.Error, Is.EqualTo("p"));
            Assert.That(sink[0].MeasuredSize, Is.EqualTo(default(Vector2)));
            Assert.That(sink[0].PendingClicks, Is.EqualTo(1));
            Assert.That(sink[0].HasPendingValue, Is.True);

            reg.Release(o);
            Assert.That(sink.Error, Is.EqualTo("p"), "a further occurrence leaves the slot alone");

            reg.SetPrimary(p, false);
            Assert.That(reg.IsPrimary(p), Is.False);
            reg.Release(p);
            Assert.That(sink.Error, Is.EqualTo("p"));
        }

        [Test]
        public void ApplyError_TakesPrecedence_SurvivesRelease()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 0));
            reg.Dispatch(new Poll().Error(h, "Cannot open x.png").Bytes);
            sink[0].ApplyError = "Qt rejected Color: bad";
            Assert.That(sink.Error, Is.EqualTo("Qt rejected Color: bad"), "#275: apply error first");

            reg.Release(h); // resync: the mirror drops its bindings, the apply error stays until the renderer clears it
            Assert.That(sink.Error, Is.EqualTo("Qt rejected Color: bad"));
            sink[0].ApplyError = null;
            Assert.That(sink.Error, Is.Null);
        }

        [Test]
        public void Release_ResetsHoverPress_DropsLateEvents_HandlesNotReused()
        {
            var reg = new SinkRegistry();
            var a = new ElementSink();
            var b = new ElementSink();
            uint ha = reg.Acquire(new SinkSlot(a, 0));
            reg.Dispatch(new Poll().Hover(ha, true).Press(ha, true).Bytes);

            reg.Release(ha);
            Assert.That(a.IsHovered() || a.IsPressed(), Is.False);
            Assert.That(reg.Count, Is.Zero);

            uint hb = reg.Acquire(new SinkSlot(b, 0));
            Assert.That(hb, Is.Not.EqualTo(ha));
            // QtNative's late EV_HOVER/EV_PRESS for the old handle (and a stale click) reach nobody.
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
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint first = reg.Acquire(new SinkSlot(sink, 0)); // 1, stays live
            Assert.That(first, Is.EqualTo(1u));
            typeof(SinkRegistry).GetField("_last", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(reg, uint.MaxValue - 1);
            Assert.That(reg.Acquire(new SinkSlot(sink, 1)), Is.EqualTo(uint.MaxValue));
            Assert.That(reg.Acquire(new SinkSlot(sink, 2)), Is.EqualTo(2u)); // skips 0 and live 1
        }

        [Test]
        public void Warning_FirstSlotWithWarning()
        {
            var sink = new ElementSink();
            Assert.That(sink.Warning, Is.Null);
            sink[1].Warning = "dup";
            Assert.That(sink.Warning, Is.EqualTo("dup"));
            sink[1].Warning = null;
            Assert.That(sink.Warning, Is.Null);
        }

        [Test]
        public void Missing_Slot_ReturnsDefaults_WithoutAllocating()
        {
            var sink = new ElementSink();
            Assert.That(sink.IsHovered(3) || sink.IsPressed(3) || sink.TakeClicks(3) > 0, Is.False);
            Assert.That(sink.MeasuredSize(3), Is.EqualTo(Vector2.Zero));
            Assert.That(sink.SceneBounds(3), Is.EqualTo(default(RectangleF)));
            Assert.That(sink.PointerPosition(-1), Is.EqualTo(Vector2.Zero));
            Assert.That(sink.Count, Is.Zero);
        }

        // --- with the reconciler ---

        [Test]
        public void HandlerEvents_TapPointWheel_LandInTheSlot()
        {
            var sink = new ElementSink();
            var reg = new SinkRegistry();
            uint h = reg.Acquire(new SinkSlot(sink, 0));

            var res = reg.Dispatch(new Poll().Press(h, true).TapCount(h, 2).LongPress(h, 1).LongPress(h, 2).Active(h, true)
                .Pointer(h, 100, 200).Wheel(h, 0, 1).Wheel(h, 0.5f, 1).Bytes);
            Assert.That(res.Ignored, Is.Zero);
            Assert.That(sink.IsPressed() && sink.IsActive(), Is.True);
            Assert.That(sink.TapCount(), Is.EqualTo(2));
            Assert.That(sink.TakeLongPresses(), Is.EqualTo(3), "summed until taken");
            Assert.That(sink.TakeLongPresses(), Is.Zero);
            Assert.That(sink.PointerPosition(), Is.EqualTo(new Vector2(1, 2)), "pixels -> units");
            Assert.That(sink.TakeWheel(), Is.EqualTo(new Vector2(0.5f, 2)), "notches summed, not converted");
            Assert.That(sink.TakeWheel(), Is.EqualTo(Vector2.Zero));

            // Too short payloads are ignored.
            res = reg.Dispatch(new Poll().Add(Protocol.Ev.Wheel, h, new byte[4]).Add(Protocol.Ev.TapCount, h, new byte[2]).Add(Protocol.Ev.Active, h, new byte[0]).Bytes);
            Assert.That(res.Applied, Is.Zero);
            Assert.That(res.Ignored, Is.EqualTo(3));
        }

        [Test]
        public void DragEvents_LandInTheSlot_CountedPerEvent()
        {
            var sink = new ElementSink();
            var reg = new SinkRegistry();
            uint h = reg.Acquire(new SinkSlot(sink, 0));

            var res = reg.Dispatch(new Poll().Active(h, true).Drag(h, 100, -50).Drag(h, 100, -50).Bytes);
            Assert.That(res.Ignored, Is.Zero);
            Assert.That(sink.IsActive(), Is.True);
            Assert.That(sink.DragDelta(), Is.EqualTo(new Vector2(1, -0.5f)), "pixels -> units");
            Assert.That(sink.DragUpdates(), Is.EqualTo(2), "equal values count too");

            res = reg.Dispatch(new Poll().Add(Protocol.Ev.Drag, h, new byte[4]).Bytes);
            Assert.That(res.Ignored, Is.EqualTo(1), "too short");

            reg.Release(h);
            Assert.That(sink.IsActive(), Is.False);
            Assert.That(sink.DragDelta(), Is.EqualTo(new Vector2(1, -0.5f)), "the last drag stays until the next one");
        }

        [Test]
        public void TouchPointsEvent_BecomesSpread_InUnits_EmptyOnZero()
        {
            var sink = new ElementSink();
            var reg = new SinkRegistry();
            uint h = reg.Acquire(new SinkSlot(sink, 0));

            var res = reg.Dispatch(new Poll().TouchPoints(h, (3, 50, 25, 0.5f, 10, 20), (7, 100, 0, 1, 0, 0)).Bytes);
            Assert.That(res, Is.EqualTo(new DispatchResult(1, 0, null)));
            var points = sink.TouchPoints();
            Assert.That(points, Has.Count.EqualTo(2));
            Assert.That(points[0], Is.EqualTo(new Elements.TouchPoint(3, new Vector2(0.5f, 0.25f), 0.5f, new Vector2(0.1f, 0.2f))), "pixels -> units");
            Assert.That(points[1].Id, Is.EqualTo(7));
            Assert.That(sink.TouchUpdates(), Is.EqualTo(1));

            reg.Dispatch(new Poll().TouchPoints(h).Bytes);
            Assert.That(sink.TouchPoints(), Is.Empty);
            Assert.That(sink.TouchUpdates(), Is.EqualTo(2));
            reg.Dispatch(new Poll().TouchPoints(h).Bytes);
            Assert.That(sink.TouchUpdates(), Is.EqualTo(2), "empty again: no change");

            // Too short / too many points are ignored.
            res = reg.Dispatch(new Poll().Add(Protocol.Ev.TouchPoints, h, new byte[3])
                .Add(Protocol.Ev.TouchPoints, h, new byte[] { 2, 0, 0, 0, 1, 2, 3, 4 })
                .Add(Protocol.Ev.TouchPoints, h, new byte[] { 65, 0, 0, 0 }).Bytes);
            Assert.That(res.Applied, Is.Zero);
            Assert.That(res.Ignored, Is.EqualTo(3));

            // Release clears the points of a handle that still holds some.
            reg.Dispatch(new Poll().TouchPoints(h, (1, 0, 0, 0, 0, 0)).Bytes);
            reg.Release(h);
            Assert.That(sink.TouchPoints(), Is.Empty);
        }

        [Test]
        public void PinchEvent_DegreesToCycles_TranslationInUnits_KeptAfterRelease()
        {
            var sink = new ElementSink();
            var reg = new SinkRegistry();
            uint h = reg.Acquire(new SinkSlot(sink, 0));
            Assert.That(sink.PinchScale(), Is.EqualTo(1f), "no gesture yet");

            var res = reg.Dispatch(new Poll().Active(h, true).Pinch(h, 1.5f, 90, 20, -40).Bytes);
            Assert.That(res.Ignored, Is.Zero);
            Assert.That(sink.PinchScale(), Is.EqualTo(1.5f));
            Assert.That(sink.PinchRotation(), Is.EqualTo(0.25f).Within(1e-6f), "90 degrees = 0.25 cycles");
            Assert.That(sink.PinchTranslation(), Is.EqualTo(new Vector2(0.2f, -0.4f)));
            Assert.That(sink.PinchUpdates(), Is.EqualTo(1));

            reg.Dispatch(new Poll().Active(h, false).Bytes);
            Assert.That(sink.PinchScale(), Is.EqualTo(1.5f), "the last value stays");
            Assert.That(reg.Dispatch(new Poll().Add(Protocol.Ev.Pinch, h, new byte[12]).Bytes).Ignored, Is.EqualTo(1), "too short");
        }

        [Test]
        public void ReleaseHandle_ResetsHandlerState()
        {
            var sink = new ElementSink();
            var reg = new SinkRegistry();
            uint h = reg.Acquire(new SinkSlot(sink, 0));
            reg.Dispatch(new Poll().Press(h, true).Active(h, true).TapCount(h, 3).LongPress(h, 1).Wheel(h, 1, 1).Bytes);

            reg.Release(h);
            Assert.That(sink.IsPressed() || sink.IsActive(), Is.False);
            Assert.That(sink.TapCount(), Is.Zero);
            Assert.That(sink.TakeLongPresses(), Is.Zero);
            Assert.That(sink.TakeWheel(), Is.EqualTo(Vector2.Zero));
        }

        static Rectangle R(float w, SinkSlot sink)
            => new() { Size = new Vector2(w, 1), Sink = sink };

        [Test]
        public void Rebind_SinkChange_RoutesToNewSink_OldSinkReset()
        {
            var cmds = new RecordingSink();
            var rec = new Reconciler(cmds, new IdAllocator(), new SceneRegistry());
            var a = new ElementSink();
            var b = new ElementSink();

            rec.Update(R(1, new SinkSlot(a, 0)), Space.DIPTopLeft);
            uint ha = rec.Root!.BoundHandle;
            rec.Sinks.Dispatch(new Poll().Hover(ha, true).Press(ha, true).Bytes);
            Assert.That(a.IsHovered() && a.IsPressed(), Is.True);

            // Same object, other sink: BIND with a new handle (flags 0, CancelPress is reserved); old sink loses hover/press right away.
            cmds.Clear();
            rec.Update(R(1, new SinkSlot(b, 0)), Space.DIPTopLeft);
            uint hb = rec.Root!.BoundHandle;
            Assert.That(hb, Is.Not.EqualTo(ha));
            Assert.That(cmds.Commands, Does.Contain($"BIND {rec.Root.Id} {hb} 0"));
            Assert.That(a.IsHovered() || a.IsPressed(), Is.False);

            // Next poll: QtNative's reset for the old handle is dropped, new events go to b.
            rec.Sinks.Dispatch(new Poll().Hover(ha, false).Press(ha, false).Hover(hb, true).Click(hb, 1).Bytes);
            Assert.That(b.IsHovered(), Is.True);
            Assert.That(b.TakeClicks(0), Is.EqualTo(1));
            Assert.That(a.TakeClicks(0), Is.Zero);

            // Unbind and destroy release the handle.
            rec.Update(R(1, default), Space.DIPTopLeft);
            Assert.That(rec.Sinks.Count, Is.Zero);
            Assert.That(b.IsHovered(), Is.False);
            rec.Update(R(1, new SinkSlot(b, 0)), Space.DIPTopLeft);
            Assert.That(rec.Sinks.Count, Is.EqualTo(1));
            rec.Update(null, Space.DIPTopLeft);
            Assert.That(rec.Sinks.Count, Is.Zero);
        }

        [Test]
        public void RecordIndex_IsOffsetToSlotIndex()
        {
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 2));
            var res = reg.Dispatch(new Poll()
                .Add(Protocol.Ev.Hover, h, new byte[] { 1 }, index: 1)
                .Add(Protocol.Ev.Measured, h, new byte[8], index: SinkRegistry.MaxIndexOffset)
                .Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(2, 0, null)));
            Assert.That(sink.IsHovered(3), Is.True);
            Assert.That(sink.IsHovered(2), Is.False);
            Assert.That(sink.Count, Is.EqualTo(2 + SinkRegistry.MaxIndexOffset + 1));
        }

        [Test]
        public void MultiElementSink_EachElementRoutedToItsSlot()
        {
            // One sink for a spread of three elements (slot index = spread index), like a ForEach in vvvv.
            var rec = new Reconciler(new RecordingSink(), new IdAllocator(), new SceneRegistry());
            var sink = new ElementSink();
            var col = new Column { Children = ImmutableArray.Create<Element>(R(1, new SinkSlot(sink, 0)), R(2, new SinkSlot(sink, 1)), R(3, new SinkSlot(sink, 2))) };
            rec.Update(col, Space.DIPTopLeft);

            var kids = rec.Root!.Children;
            var poll = new Poll();
            for (int i = 0; i < 3; i++)
                poll.Measured(kids[i].BoundHandle, 100 * (i + 1), 50).Bounds(kids[i].BoundHandle, 0, 100 * i, 100 * (i + 1), 50)
                    .Hover(kids[i].BoundHandle, i == 1);
            var res = rec.Sinks.Dispatch(poll.Bytes);

            Assert.That(res, Is.EqualTo(new DispatchResult(9, 0, null)));
            Assert.That(rec.Sinks.Count, Is.EqualTo(3));
            for (int i = 0; i < 3; i++)
            {
                Assert.That(sink.MeasuredSize(i), Is.EqualTo(new Vector2(i + 1, 0.5f)));
                Assert.That(sink.SceneBounds(i), Is.EqualTo(new RectangleF(0, i, i + 1, 0.5f)));
                Assert.That(sink.IsHovered(i), Is.EqualTo(i == 1));
            }

            // Spread order changes: slots move with the elements, new handles, late records of old handles dropped.
            uint old0 = kids[0].BoundHandle;
            col = new Column { Children = ImmutableArray.Create<Element>(R(1, new SinkSlot(sink, 1)), R(2, new SinkSlot(sink, 0)), R(3, new SinkSlot(sink, 2))) };
            rec.Update(col, Space.DIPTopLeft);
            Assert.That(sink.IsHovered(1), Is.False); // released slot loses hover
            res = rec.Sinks.Dispatch(new Poll().Measured(old0, 9, 9).Measured(rec.Root!.Children[0].BoundHandle, 700, 50).Bytes);
            Assert.That(res, Is.EqualTo(new DispatchResult(1, 1, null)));
            Assert.That(sink.MeasuredSize(1), Is.EqualTo(new Vector2(7, 0.5f)));
            Assert.That(sink.MeasuredSize(0), Is.EqualTo(new Vector2(1, 0.5f))); // kept until QtNative resends
        }

        [Test]
        public void Dispatch_1000Records_NoAllocations()
        {
            const int n = 1000;
            var reg = new SinkRegistry();
            var sink = new ElementSink();
            var p = new Poll();
            for (int i = 0; i < n; i++)
            {
                uint h = reg.Acquire(new SinkSlot(sink, i));
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
