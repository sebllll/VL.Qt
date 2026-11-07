using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.EditorAttributes;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using P = VL.Qt.Protocol.Prop;

namespace VL.Qt.Tests
{
    /// <summary>Slider node (#301): description, property diff, value channel (f32), clamp, bound order and channel attributes.</summary>
    public class SliderNodeTests
    {
        static readonly Vector2 DefaultSize = new(2, 0.28f);
        static readonly IDisposable NoWarning = new Nop();
        sealed class Nop : IDisposable { public void Dispose() { } }

        SliderNode _node = null!;
        SinkRegistry _registry = null!;
        uint _handle;
        RecordingSink _sink = null!;
        Reconciler _reconciler = null!;

        [SetUp]
        public void SetUp()
        {
            _node = new SliderNode(_ => NoWarning);
            _registry = new SinkRegistry();
            _handle = _registry.Acquire(new SinkSlot(_node.Core.Sink, 0));
            _sink = new RecordingSink();
            _reconciler = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
        }

        [TearDown]
        public void TearDown() => _node.Dispose();

        Slider Update(out float value, out bool bang, IChannel<float>? channel = null, Optional<float> min = default, Optional<float> max = default,
            float step = 0, SliderOrientation orientation = SliderOrientation.Horizontal, bool enabled = true, bool autoSize = true)
            => (Slider)_node.Update(DefaultSize, autoSize, default, RectangleAnchor.Center, channel, min, max, step, orientation, enabled,
                1, false, false, null, null, null, out value, out bang, out _, out _);

        Slider Update(IChannel<float>? channel = null, Optional<float> min = default, Optional<float> max = default)
            => Update(out _, out _, channel, min, max);

        string[] Frame(out float value, out bool bang, IChannel<float>? channel = null, Optional<float> min = default, Optional<float> max = default)
        {
            _sink.Clear();
            _reconciler.Update(Update(out value, out bang, channel, min, max), Space.DIPTopLeft);
            return _sink.Commands.ToArray();
        }

        string[] Frame(IChannel<float>? channel = null, Optional<float> min = default, Optional<float> max = default)
            => Frame(out _, out _, channel, min, max);

        void UserChanges(float value) => _registry.Dispatch(new SinkRegistryTests.Poll().Value(_handle, Protocol.Vt.F32, BitConverter.GetBytes(value)).Bytes);

        static string SetValue(float v) => $"SET 1 {P.Value} f32({v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})";

        [Test]
        public void Defaults_ZeroToOne_Continuous()
        {
            var s = Update(out float value, out bool bang);
            Assert.That(s.Type, Is.EqualTo(ElementType.Slider));
            Assert.That((ushort)ElementType.Slider, Is.EqualTo(Protocol.Type.Slider));
            Assert.That((s.Value, s.Minimum, s.Maximum, s.Step, s.Orientation, s.Enabled, s.AutoSize),
                Is.EqualTo((0f, 0f, 1f, 0f, SliderOrientation.Horizontal, true, true)));
            Assert.That((value, bang), Is.EqualTo((0f, false)));
            Assert.That(s.Sink.Sink, Is.SameAs(_node.Core.Sink));
        }

        [Test]
        public void Reconciler_SendsOnlyNonDefaults_ChangeDetection()
        {
            var cmds = Frame();
            Assert.That(cmds, Does.Contain($"CREATE 1 {Protocol.Type.Slider}"));
            Assert.That(cmds.Where(c => c.StartsWith("SET 1 7", StringComparison.Ordinal) || c.StartsWith($"SET 1 {P.AutoSize} ", StringComparison.Ordinal)),
                Is.Empty, "QtNative defaults are not sent");

            var a = Update();
            Assert.That(Update(), Is.SameAs(a));

            _sink.Clear();
            _reconciler.Update(Update(out _, out _, step: 0.25f, orientation: SliderOrientation.Vertical, enabled: false, autoSize: false), Space.DIPTopLeft);
            Assert.That(_sink.Commands, Is.EquivalentTo(new[]
            {
                $"SET 1 {P.AutoSize} bool(0)", $"SET 1 {P.Step} f32(0.25)", $"SET 1 {P.Orientation} i32(2)", $"SET 1 {P.Enabled} bool(0)",
            }));
        }

        [Test]
        public void StartValueFromPin_ThenSetFromPatch()
        {
            var ch = Channel.Create(0.5f);
            Assert.That(Frame(out float value, out bool bang, ch), Does.Contain(SetValue(0.5f)));
            Assert.That((value, bang), Is.EqualTo((0.5f, false)));

            Assert.That(Frame(ch).Where(c => c.StartsWith("SET", StringComparison.Ordinal)), Is.Empty, "unchanged: nothing");

            ch.Value = 0.25f;
            Assert.That(Frame(out value, out bang, ch), Is.EqualTo(new[] { SetValue(0.25f) }));
            Assert.That((value, bang), Is.EqualTo((0.25f, false)), "a patch change is no Bang");
        }

        [Test]
        public void UserChange_WritesChannel_Bang_NoEchoAfterwards()
        {
            var ch = Channel.Create(0.5f);
            Frame(ch);

            UserChanges(0.75f);
            var cmds = Frame(out float value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo((0.75f, true, 0.75f, "Qt")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue(0.75f) }), "no-op in Qt, repairs a stale SET (PR #354)");

            cmds = Frame(out value, out bang, ch);
            Assert.That((value, bang), Is.EqualTo((0.75f, false)), "Bang for one frame");
            Assert.That(cmds, Is.Empty, "the channel write of Qt is not seen as a patch change");
        }

        [Test]
        public void ConflictInTheSameFrame_UserWins()
        {
            var ch = Channel.Create(0.5f);
            Frame(ch);

            ch.Value = 0.1f; // patch
            UserChanges(0.9f); // user, same frame
            var cmds = Frame(out float value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo((0.9f, true, 0.9f, "Qt")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue(0.9f) }));
            Assert.That(Frame(ch), Is.Empty, "stable afterwards");
        }

        [Test]
        public void WithoutChannel_ValueFollowsTheUser()
        {
            Frame();
            UserChanges(0.3f);
            Frame(out float value, out bool bang);
            Assert.That((value, bang), Is.EqualTo((0.3f, true)));
            Frame(out value, out bang);
            Assert.That((value, bang), Is.EqualTo((0.3f, false)));
        }

        [Test]
        public void Clamp_OwnCopyOnly_BoundsBeforeValue()
        {
            var ch = Channel.Create(5f);
            var cmds = Frame(out float value, out _, ch);
            Assert.That(value, Is.EqualTo(1f), "clamped to the default 0…1");
            Assert.That(ch.Value, Is.EqualTo(5f), "the channel keeps the patch's value");
            Assert.That(cmds, Does.Contain(SetValue(1f)));

            cmds = Frame(out value, out _, ch, 0f, 10f);
            Assert.That(value, Is.EqualTo(1f), "the clamped copy stays clamped like in Qt; the channel is not re-read without a change");
        }

        [Test]
        public void Clamp_BoundsInAnyOrder_AndBoundChangeResendsValue()
        {
            var ch = Channel.Create(50f);
            Frame(ch, 0f, 100f);

            var cmds = Frame(out float value, out _, ch, 0f, 10f);
            Assert.That(value, Is.EqualTo(10f));
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 {P.Maximum} f32(10)", SetValue(10f) }), "Maximum before Value");

            ch.Value = -5f;
            Frame(out value, out _, ch, 0f, 10f);
            Assert.That(value, Is.EqualTo(0f));

            Frame(out value, out _, ch, 10f, 0f);
            Assert.That(value, Is.EqualTo(0f), "bounds in any order");

            // a bound change resends Value even if it stayed the same
            cmds = Frame(out value, out _, ch, 0f, 20f);
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 {P.Minimum} f32(0)", $"SET 1 {P.Maximum} f32(20)", SetValue(0f) }));
        }

        [Test]
        public void BoundsAreSentBeforeValue_InTheFirstFrame()
        {
            var ch = Channel.Create(7f);
            var cmds = Frame(out float value, out _, ch, -10f, 10f);
            Assert.That(value, Is.EqualTo(7f));
            var sets = cmds.Where(c => c.StartsWith("SET 1 70", StringComparison.Ordinal) || c.StartsWith("SET 1 706", StringComparison.Ordinal)).ToArray();
            Assert.That(sets, Is.EqualTo(new[] { $"SET 1 {P.Minimum} f32(-10)", $"SET 1 {P.Maximum} f32(10)", SetValue(7f) }));
        }

        [Test]
        public void MinMax_FallBackToChannelAttributes()
        {
            var ch = Channel.Create(5f);
            ch.Attributes().Value = new Attribute[] { new MinAttribute(-2f), new MaxAttribute(3f) }.ToSpread();
            var s = Update(out float value, out _, ch);
            Assert.That((s.Minimum, s.Maximum, value), Is.EqualTo((-2f, 3f, 3f)));

            s = Update(out value, out _, ch, max: 100f);
            Assert.That((s.Minimum, s.Maximum, value), Is.EqualTo((-2f, 100f, 3f)), "the pin wins; the clamped copy stays clamped like in Qt");

            ch.Attributes().Value = Spread<Attribute>.Empty;
            s = Update(out _, out _, ch);
            Assert.That((s.Minimum, s.Maximum), Is.EqualTo((0f, 1f)), "attributes are pulled, fallback 0…1");
        }

        [Test]
        public void UnchangedInputs_NoAllocation()
        {
            var ch = Channel.Create(0.5f);
            for (int i = 0; i < 100; i++) { Update(ch); Update(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Update(ch);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0), "connected");

            var unconnected = new SliderNode(_ => NoWarning);
            for (int i = 0; i < 100; i++) unconnected.Update(DefaultSize, true, default, RectangleAnchor.Center, null, default, default, 0, SliderOrientation.Horizontal, true, 1, false, false, null, null, null, out _, out _, out _, out _);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) unconnected.Update(DefaultSize, true, default, RectangleAnchor.Center, null, default, default, 0, SliderOrientation.Horizontal, true, 1, false, false, null, null, null, out _, out _, out _, out _);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0), "unconnected (null)");
            unconnected.Dispose();
        }
    }
}
