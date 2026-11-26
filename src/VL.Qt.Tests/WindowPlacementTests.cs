using System;
using System.Buffers.Binary;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    // Bounds pin → vlqt_window_create arguments, and parsing of EVENT_WINDOW_GEOMETRY.
    // The screen rules (rect on no screen → centered) live in QtNative (QScreen) and are checked by the SmokeTest.
    public class WindowPlacementTests
    {
        [Test]
        public void Bounds_PassedAsDip()
        {
            var a = WindowPlacement.FromBounds(new RectangleF(100.4f, -50, 800.4f, 599.6f));
            Assert.That(a, Is.EqualTo(new WindowPlacement.CreateArgs(100.4f, -50, 800, 600)));
        }

        [TestCase(0, 0)]
        [TestCase(0.5f, 300)]
        [TestCase(300, -1)]
        [TestCase(float.NaN, 300)]
        public void EmptyBounds_CenteredSentinelAndQtNativeFallbackSize(float w, float h)
        {
            var a = WindowPlacement.FromBounds(new RectangleF(10, 20, w, h));
            Assert.That(float.IsNaN(a.X) && float.IsNaN(a.Y), Is.True);
            Assert.That((a.Width, a.Height), Is.EqualTo((0, 0)));
        }

        // a Renderer created by vvvv's hotswap (added live, Undo/Redo) gets the VL type default for Bounds,
        // (-0.5, -0.5, 1, 1) in vvvv 8.0-0156; it must open at the pin default, not as a 1x1 window.
        [Test]
        public void CreateBounds_TypeDefaultMeansPinDefault()
        {
            var vlDefault = new RectangleF(-0.5f, -0.5f, 1, 1);
            Assert.That(WindowPlacement.CreateBounds(vlDefault, vlDefault), Is.EqualTo(WindowPlacement.DefaultBounds));
            var set = new RectangleF(10, 20, 300, 200);
            Assert.That(WindowPlacement.CreateBounds(set, vlDefault), Is.EqualTo(set));
            Assert.That(WindowPlacement.CreateBounds(default, vlDefault), Is.EqualTo(default(RectangleF))); // empty = centered sentinel
        }

        [Test]
        public void DefaultBounds_MatchesPinDefaultString()
        {
            var parts = Array.ConvertAll(WindowPlacement.DefaultBoundsString.Split(','), s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
            Assert.That(new RectangleF(parts[0], parts[1], parts[2], parts[3]), Is.EqualTo(WindowPlacement.DefaultBounds));
        }

        private static byte[] Record(ushort ev, uint handle, float x, float y, float w, float h, uint flags, float cx = 0, float cy = 0)
        {
            var b = new byte[Protocol.HeaderSize + Protocol.EventFieldsSize + 28];
            BinaryPrimitives.WriteUInt16LittleEndian(b, ev);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(Protocol.EventFieldsSize + 28));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), handle);
            int p = Protocol.HeaderSize + Protocol.EventFieldsSize;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p), x);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p + 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p + 8), w);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p + 12), h);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 16), flags);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p + 20), cx);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p + 24), cy);
            return b;
        }

        [Test]
        public void Geometry_LastWindowRecordWins()
        {
            var buf = new byte[0]
                .Concat(Record(Protocol.EventId.WindowGeometry, 0, 1, 2, 3, 4, Protocol.WindowState.Visible))
                .Concat(Record(Protocol.EventId.SceneBounds, 7, 9, 9, 9, 9, 0)) // sink record: not the window
                .Concat(Record(Protocol.EventId.WindowGeometry, 0, -32000, -32000, 800, 600, Protocol.WindowState.Minimized | Protocol.WindowState.Visible, -31992, -31969));
            Assert.That(WindowPlacement.TryReadGeometry(buf, out var g), Is.True);
            Assert.That(g.Bounds, Is.EqualTo(new RectangleF(-32000, -32000, 800, 600)));
            Assert.That(g.ClientBounds, Is.EqualTo(new RectangleF(-31992, -31969, 800, 600)));
            Assert.That(g.IsMinimized && g.IsVisible && !g.IsFullscreen, Is.True);
        }

        [Test]
        public void Geometry_NoneInBuffer()
        {
            Assert.That(WindowPlacement.TryReadGeometry(Record(Protocol.EventId.WindowGeometry, 3, 1, 2, 3, 4, 0), out _), Is.False);
            Assert.That(WindowPlacement.TryReadGeometry(ReadOnlySpan<byte>.Empty, out _), Is.False);
        }

        // Save Bounds: debounce 0.5 s, only normal geometry, whole DIP, nothing while equal to the pin value.
        private static readonly RectangleF Pin = new(1000, 40, 786, 432);
        private static WindowGeometry Normal(float x, float y, float w, float h) => new(new RectangleF(x, y, w, h), WindowStates.Visible);
        private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

        [Test]
        public void SaveBounds_InitialReportEqualToPin_WritesNothing()
        {
            var s = new BoundsSaver(Pin);
            s.OnGeometry(Normal(1000.2f, 39.8f, 786, 432), S(0));
            Assert.That(s.TryTake(S(5), out _), Is.False);
        }

        [Test]
        public void SaveBounds_WritesAfterWindowIsStill()
        {
            var s = new BoundsSaver(Pin);
            s.OnGeometry(Normal(10, 20, 640, 480), S(1));
            s.OnGeometry(Normal(110.6f, 20, 640, 480), S(1.3)); // still moving: restarts the delay
            Assert.That(s.TryTake(S(1.7), out _), Is.False);
            Assert.That(s.TryTake(S(1.8), out var r), Is.True);
            Assert.That(r, Is.EqualTo(new RectangleF(111, 20, 640, 480)));
            Assert.That(s.TryTake(S(9), out _), Is.False, "taken once");
            s.OnGeometry(Normal(111, 20, 640, 480), S(10));
            Assert.That(s.TryTake(S(11), out _), Is.False, "equal to the written value");
        }

        [Test]
        public void SaveBounds_MovedBackBeforeDelay_WritesNothing()
        {
            var s = new BoundsSaver(Pin);
            s.OnGeometry(Normal(10, 20, 640, 480), S(0));
            s.OnGeometry(Normal(1000, 40, 786, 432), S(0.2));
            Assert.That(s.TryTake(S(5), out _), Is.False);
        }

        [TestCase(Protocol.WindowState.Minimized | Protocol.WindowState.Visible)]
        [TestCase(Protocol.WindowState.Maximized | Protocol.WindowState.Visible)]
        [TestCase(Protocol.WindowState.Fullscreen | Protocol.WindowState.Visible)]
        [TestCase(0u)] // hidden
        public void SaveBounds_SkipsNonNormalGeometry_KeepsPendingValue(uint flags)
        {
            var s = new BoundsSaver(Pin);
            s.OnGeometry(Normal(10, 20, 640, 480), S(0));
            s.OnGeometry(new WindowGeometry(new RectangleF(-32000, -32000, 1920, 1080), (WindowStates)flags), S(0.1));
            Assert.That(s.TryTake(S(1), out var r), Is.True);
            Assert.That(r, Is.EqualTo(new RectangleF(10, 20, 640, 480)));
            s.OnGeometry(new WindowGeometry(new RectangleF(-32000, -32000, 1920, 1080), (WindowStates)flags), S(2));
            Assert.That(s.TryTake(S(3), out _), Is.False);
        }

        [Test]
        public void SaveBounds_FullscreenRoundTrip_WritesNothing() // on → screen rect, off → geometry from before
        {
            var s = new BoundsSaver(Pin);
            s.OnGeometry(Normal(1000, 40, 786, 432), S(0));
            s.OnGeometry(new WindowGeometry(new RectangleF(0, 0, 1464, 976), WindowStates.Fullscreen | WindowStates.Visible), S(1));
            Assert.That(s.TryTake(S(5), out _), Is.False, "nothing while fullscreen");
            s.OnGeometry(Normal(1000, 40, 786, 432), S(6));
            Assert.That(s.TryTake(S(9), out _), Is.False, "restored geometry = pin value");
        }
    }
}
