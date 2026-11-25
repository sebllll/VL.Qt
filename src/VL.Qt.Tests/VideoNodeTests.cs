using System;
using System.Buffers.Binary;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using P = VL.Qt.Wire.Protocol.PropertyId;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests
{
    /// <summary>Video element + node: defaults, property diff, clamping, EVENT_MEDIA back channel.</summary>
    public class VideoNodeTests
    {
        private static readonly IDisposable NoWarning = new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }

        private VideoNode _node = null!;

        [SetUp]
        public void SetUp() => _node = new VideoNode(_ => NoWarning);

        [TearDown]
        public void TearDown() => _node.Dispose();

        // Pin defaults as declared on Update.
        private Element Update(string? path = null, bool play = true, bool loop = true, bool keepLast = true, float rate = 1, float volume = 1,
            FillMode fill = FillMode.Fit) => Update(out _, out _, path, play, loop, keepLast, rate, volume, fill);

        private Element Update(out float position, out float duration, string? path = null, bool play = true, bool loop = true, bool keepLast = true,
            float rate = 1, float volume = 1, FillMode fill = FillMode.Fit)
            => (Element)_node.Update(new Vector2(1.6f, 0.9f), default, RectangleAnchor.Center, path is null ? Path.Default : new Path(path),
                play, loop, keepLast, rate, volume, fill, 1, false, false, null, null, true, out position, out duration,
                out _, out _);

        private static byte[] ErrorRecord(uint handle, string text)
        {
            var t = System.Text.Encoding.UTF8.GetBytes(text);
            var b = new byte[(12 + t.Length + 3) & ~3];
            BinaryPrimitives.WriteUInt16LittleEndian(b, Protocol.EventId.Error);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(4 + t.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), handle);
            t.CopyTo(b, 12);
            return b;
        }

        private static byte[] MediaRecord(uint handle, float position, float duration)
        {
            var b = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(b, Protocol.EventId.Media);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 12);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), handle);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), position);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), duration);
            return b;
        }

        [Test]
        public void ElementDefaults_MatchQt()
        {
            var v = new Video();
            Assert.That(v.Type, Is.EqualTo(ElementType.Video));
            Assert.That((ushort)v.Type, Is.EqualTo(Protocol.ElementType.Video));
            Assert.That(v.Playing, Is.False);
            Assert.That(v.Loop, Is.False);
            Assert.That(v.KeepLastFrame, Is.False);
            Assert.That(v.Rate, Is.EqualTo(1f));
            Assert.That(v.Volume, Is.EqualTo(1f));
            Assert.That(v.FillMode, Is.EqualTo(FillMode.Fit));
            Assert.That((int)FillMode.Crop, Is.EqualTo(2));
        }

        [Test]
        public void Node_Describes_Video_WithFeedback()
        {
            var v = (Video)Update("c:/a.mp4", rate: 2, volume: 0.5f, fill: FillMode.Crop);
            Assert.That(v.Path.Value, Is.EqualTo("c:/a.mp4"));
            Assert.That(v.Playing && v.Loop && v.KeepLastFrame, Is.True);
            Assert.That(((Video)Update("c:/a.mp4", keepLast: false)).KeepLastFrame, Is.False);
            Assert.That(v.Rate, Is.EqualTo(2f));
            Assert.That(v.Volume, Is.EqualTo(0.5f));
            Assert.That(v.FillMode, Is.EqualTo(FillMode.Crop));
            Assert.That(v.Feedback.Feedback, Is.SameAs(_node.Core.Feedback));
        }

        [Test]
        public void Node_UnchangedInputs_SameInstance_NoAllocation()
        {
            var a = Update("c:/a.mp4");
            Assert.That(Update(new string("c:/a.mp4".AsSpan())), Is.SameAs(a));
            Assert.That(Update("c:/b.mp4"), Is.Not.SameAs(a));
            for (int i = 0; i < 100; i++) Update(out _, out _, "c:/b.mp4");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var path = new Path("c:/b.mp4");
            for (int i = 0; i < 1000; i++)
                _node.Update(new Vector2(1.6f, 0.9f), default, RectangleAnchor.Center, path, true, true, true, 1, 1, FillMode.Fit,
                    1, false, false, null, null, true, out _, out _, out _, out _);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(100));
        }

        [Test]
        public void Node_TileFillMode_SendsStretchAndWarnsUntilChanged()
        {
            var warnings = new System.Collections.Generic.List<string>();
            using var node = new VideoNode(text => { warnings.Add(text); return NoWarning; });
            var path = new Path("c:/a.mp4");
            Element Describe(FillMode fill) => (Element)node.Update(new Vector2(1.6f, 0.9f), default, RectangleAnchor.Center, path, true, true, true, 1, 1, fill,
                1, false, false, null, null, true, out _, out _, out _, out _);

            var tiled = (Video)Describe(FillMode.Tile);
            Assert.That(tiled.FillMode, Is.EqualTo(FillMode.Stretch));
            Assert.That(warnings, Has.Count.EqualTo(1).And.One.Contains("Tile"));
            Assert.That(Describe(FillMode.Tile), Is.SameAs(tiled));
            Assert.That(warnings, Has.Count.EqualTo(1), "one warning, not one per frame");

            Assert.That(((Video)Describe(FillMode.Crop)).FillMode, Is.EqualTo(FillMode.Crop));
        }

        [Test]
        public void Diff_Create_SendsNonDefaults_PlayingLast()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(new Video { Path = new Path("c:/a.mp4"), Playing = true, Loop = true, KeepLastFrame = true, Rate = 0.5f, Volume = 0.25f, FillMode = FillMode.Stretch }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[]
            {
                $"SET 1 Source string(c:/a.mp4)",
                $"SET 1 FillMode i32(0)",
                $"SET 1 Loop bool(1)",
                $"SET 1 KeepLastFrame bool(1)",
                $"SET 1 PlaybackRate f32(0.5)",
                $"SET 1 Volume f32(0.25)",
                $"SET 1 Playing bool(1)",
            }));
            Assert.That(sink.Commands[0], Is.EqualTo($"CREATE 1 Video"));

            sink.Clear();
            rec.Update(new Video(), Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[]
            {
                $"SET 1 Source string()",
                $"SET 1 FillMode i32(1)",
                $"SET 1 Loop bool(0)",
                $"SET 1 KeepLastFrame bool(0)",
                $"SET 1 PlaybackRate f32(1)",
                $"SET 1 Volume f32(1)",
                $"SET 1 Playing bool(0)",
            }));
        }

        [Test]
        public void Diff_AudioTap_OnlyWhenSet_BeforePlaying()
        {
            // Video [VL.Qt] (AudioTap 0) never sends it; Video (VL.Audio) sends it before Playing and switches ids directly.
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(new Video { Playing = true, AudioTap = 5 }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 1 AudioTap u64(5)", $"SET 1 Playing bool(1)" }));
            sink.Clear();
            rec.Update(new Video { Playing = true, AudioTap = 6 }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 1 AudioTap u64(6)" }));
            sink.Clear();
            rec.Update(new Video { Playing = true }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 1 AudioTap u64(0)" }));
        }

        [Test]
        public void Diff_ClampsRateAndVolume()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(new Video { Rate = -1, Volume = 3 }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 1 PlaybackRate f32(0.01)" }), "volume 3 clamps to the default 1");
            sink.Clear();
            rec.Update(new Video { Rate = 0, Volume = float.NaN }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 1 Volume f32(0)" }), "rate 0 = still MinRate");
            sink.Clear();
            rec.Update(new Video { Rate = float.PositiveInfinity, Volume = -1 }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 1 PlaybackRate f32(1)" }));
        }

        [Test]
        public void MediaEvent_ReachesNodeOutputs_DefaultZero()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(Update(out var pos, out var dur, "c:/a.mp4"), Space.DIPTopLeft);
            Assert.That(pos, Is.EqualTo(0f));
            Assert.That(dur, Is.EqualTo(0f), "defined default until loaded");

            var bind = sink.OfOp("BIND").Single().Split(' ');
            uint handle = uint.Parse(bind[2]);
            var res = rec.FeedbackRouter.Dispatch(MediaRecord(handle, 0.5f, 2f));
            Assert.That(res.Applied, Is.EqualTo(1));
            Update(out pos, out dur, "c:/a.mp4");
            Assert.That(pos, Is.EqualTo(0.5f), "seconds, not converted to units");
            Assert.That(dur, Is.EqualTo(2f));

            // Too short payload is ignored.
            var shortRec = MediaRecord(handle, 1, 1)[..16];
            BinaryPrimitives.WriteUInt32LittleEndian(shortRec.AsSpan(4), 8);
            Assert.That(rec.FeedbackRouter.Dispatch(shortRec).Ignored, Is.EqualTo(1));
        }

        [Test]
        public void ErrorEvent_ShownAtNode_UntilCleared()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(Update("c:/missing.mp4"), Space.DIPTopLeft);
            Assert.That(_node.Core.Error, Is.Null);

            uint handle = uint.Parse(sink.OfOp("BIND").Single().Split(' ')[2]);
            rec.FeedbackRouter.Dispatch(ErrorRecord(handle, "Could not open file (c:/missing.mp4)"));
            Update("c:/missing.mp4");
            Assert.That(_node.Core.Error, Is.EqualTo("Could not open file (c:/missing.mp4)"));

            rec.FeedbackRouter.Dispatch(ErrorRecord(handle, ""));
            Update("c:/missing.mp4");
            Assert.That(_node.Core.Error, Is.Null);
        }
    }
}
