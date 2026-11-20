﻿using System;
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
    /// <summary>Input (Int) node (#210): description, property diff, the value channel (pull, user wins, clamp) and EV_VALUE.</summary>
    public class InputIntNodeTests
    {
        static readonly Vector2 DefaultSize = new(1.4f, 0.4f);
        static readonly IDisposable NoWarning = new Nop();
        sealed class Nop : IDisposable { public void Dispose() { } }

        InputIntNode _node = null!;
        SinkRegistry _registry = null!;
        uint _handle;
        RecordingSink _sink = null!;
        Reconciler _reconciler = null!;

        [SetUp]
        public void SetUp()
        {
            _node = new InputIntNode(_ => NoWarning);
            _registry = new SinkRegistry();
            _handle = _registry.Acquire(new SinkSlot(_node.Core.Sink, 0));
            _sink = new RecordingSink();
            _reconciler = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
        }

        [TearDown]
        public void TearDown() => _node.Dispose();

        // Pin defaults as declared on Update.
        SpinBox Update(out int value, out bool bang, IChannel<int>? channel = null, Optional<int> min = default, Optional<int> max = default,
            int step = 1, bool editable = true, bool enabled = true, bool autoSize = true)
            => (SpinBox)_node.Update(DefaultSize, autoSize, default, RectangleAnchor.Center, channel, min, max, step, editable, enabled,
                1, false, false, null, null, null, out value, out bang, out _, out _);

        SpinBox Update(IChannel<int>? channel = null, Optional<int> min = default, Optional<int> max = default)
            => Update(out _, out _, channel, min, max);

        /// <summary>One frame: update the node, reconcile, return the commands it produced.</summary>
        string[] Frame(out int value, out bool bang, IChannel<int>? channel = null, Optional<int> min = default, Optional<int> max = default)
        {
            _sink.Clear();
            _reconciler.Update(Update(out value, out bang, channel, min, max), Space.DIPTopLeft);
            return _sink.Commands.ToArray();
        }

        string[] Frame(IChannel<int>? channel = null, Optional<int> min = default, Optional<int> max = default)
            => Frame(out _, out _, channel, min, max);

        void UserChanges(int value) => _registry.Dispatch(new SinkRegistryTests.Poll().ValueI32(_handle, value).Bytes);

        static string SetValue(int v) => $"SET 1 Value i32({v})";

        [Test]
        public void Defaults_FullIntRange()
        {
            var s = Update(out int value, out bool bang);
            Assert.That(s.Type, Is.EqualTo(ElementType.SpinBox));
            Assert.That((ushort)ElementType.SpinBox, Is.EqualTo(Protocol.Type.SpinBox));
            Assert.That((s.Value, s.Minimum, s.Maximum, s.Step, s.Editable, s.Enabled, s.AutoSize),
                Is.EqualTo((0, int.MinValue, int.MaxValue, 1, true, true, true)));
            Assert.That((value, bang), Is.EqualTo((0, false)));
            Assert.That(s.Sink.Sink, Is.SameAs(_node.Core.Sink));
        }

        [Test]
        public void Reconciler_SendsOnlyNonDefaults_ChangeDetection()
        {
            var cmds = Frame();
            Assert.That(cmds, Does.Contain($"CREATE 1 SpinBox"));
            Assert.That(cmds.Where(c => c.StartsWith("SET 1 Debug", StringComparison.Ordinal) || c.StartsWith($"SET 1 AutoSize ", StringComparison.Ordinal)),
                Is.Empty, "QtNative defaults are not sent");

            var a = Update();
            Assert.That(Update(), Is.SameAs(a));

            _sink.Clear();
            _reconciler.Update(Update(out _, out _, step: 5, editable: false, enabled: false, autoSize: false), Space.DIPTopLeft);
            Assert.That(_sink.Commands, Is.EquivalentTo(new[]
            {
                $"SET 1 AutoSize bool(0)", $"SET 1 Step i32(5)", $"SET 1 Editable bool(0)", $"SET 1 Enabled bool(0)",
            }));
        }

        [Test]
        public void StartValueFromPin_ThenSetFromPatch()
        {
            var ch = Channel.Create(42); // an IOBox on the pin: vvvv lifts it into a private channel
            Assert.That(Frame(out int value, out bool bang, ch), Does.Contain(SetValue(42)));
            Assert.That((value, bang), Is.EqualTo((42, false)));

            Assert.That(Frame(ch).Where(c => c.StartsWith("SET", StringComparison.Ordinal)), Is.Empty, "unchanged: nothing");

            ch.Value = -3;
            Assert.That(Frame(out value, out bang, ch), Is.EqualTo(new[] { SetValue(-3) }));
            Assert.That((value, bang), Is.EqualTo((-3, false)), "a patch change is no Bang");
        }

        [Test]
        public void UserChange_WritesChannel_Bang_NoEchoAfterwards()
        {
            var ch = Channel.Create(1);
            Frame(ch);

            UserChanges(5);
            var cmds = Frame(out int value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo((5, true, 5, "Qt")));
            // Diffed against the last sent value (1): one SET with the user's value, a no-op in Qt (no EV_VALUE), that
            // repairs a stale SET applied after the change (PR #354).
            Assert.That(cmds, Is.EqualTo(new[] { SetValue(5) }));

            cmds = Frame(out value, out bang, ch);
            Assert.That((value, bang), Is.EqualTo((5, false)), "Bang for one frame");
            Assert.That(cmds, Is.Empty, "the channel write of Qt is not seen as a patch change");
        }

        [Test]
        public void UserChangeToTheLastSentValue_NothingSent()
        {
            var ch = Channel.Create(1);
            Frame(ch);
            UserChanges(1); // e.g. + then - within one frame
            var cmds = Frame(out int value, out bool bang, ch);
            Assert.That((value, bang), Is.EqualTo((1, true)));
            Assert.That(cmds, Is.Empty);
        }

        [Test]
        public void ConflictInTheSameFrame_UserWins()
        {
            var ch = Channel.Create(1);
            Frame(ch);

            ch.Value = 10; // patch
            UserChanges(7); // user, same frame
            var cmds = Frame(out int value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo((7, true, 7, "Qt")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue(7) }));

            Assert.That(Frame(ch), Is.Empty, "stable afterwards");
        }

        [Test]
        public void WithoutChannel_ValueFollowsTheUser()
        {
            Frame();
            UserChanges(9);
            Frame(out int value, out bool bang);
            Assert.That((value, bang), Is.EqualTo((9, true)));
            Frame(out value, out bang);
            Assert.That((value, bang), Is.EqualTo((9, false)));
        }

        [Test]
        public void Clamp_OwnCopyOnly_BoundsBeforeValue()
        {
            var ch = Channel.Create(50);
            Frame(ch);

            var cmds = Frame(out int value, out _, ch, 0, 10);
            Assert.That(value, Is.EqualTo(10));
            Assert.That(ch.Value, Is.EqualTo(50), "the channel keeps the patch's value");
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 Minimum i32(0)", $"SET 1 Maximum i32(10)", SetValue(10) }),
                "Minimum/Maximum before Value");

            ch.Value = -5;
            Frame(out value, out _, ch, 0, 10);
            Assert.That(value, Is.EqualTo(0));

            // Bounds in any order (Qt allows from > to).
            Frame(out value, out _, ch, 10, 0);
            Assert.That(value, Is.EqualTo(0));

            // A bound change resends Value even if it stayed the same (Qt may have clamped with an intermediate range).
            cmds = Frame(out value, out _, ch, 0, 20);
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 Minimum i32(0)", $"SET 1 Maximum i32(20)", SetValue(0) }));
        }

        [Test]
        public void MinMax_FallBackToChannelAttributes()
        {
            var ch = Channel.Create(500);
            ch.Attributes().Value = new Attribute[] { new MinAttribute(-2), new MaxAttribute(100) }.ToSpread();
            var s = Update(out int value, out _, ch);
            Assert.That((s.Minimum, s.Maximum, value), Is.EqualTo((-2, 100, 100)));

            s = Update(out value, out _, ch, max: 300);
            Assert.That((s.Minimum, s.Maximum, value), Is.EqualTo((-2, 300, 100)), "the pin wins; the clamped copy stays clamped like in Qt");

            ch.Attributes().Value = Spread<Attribute>.Empty;
            s = Update(out _, out _, ch);
            Assert.That((s.Minimum, s.Maximum), Is.EqualTo((int.MinValue, int.MaxValue)), "attributes are pulled");
        }

        [Test]
        public void OtherChannelConnected_TakesItsValue()
        {
            var a = Channel.Create(1);
            var b = Channel.Create(1);
            Frame(a);
            UserChanges(4);
            Frame(a);
            Frame(out int value, out _, b);
            Assert.That(value, Is.EqualTo(1), "a new channel is a patch change, even with the same value as before");
            Assert.That(b.Value, Is.EqualTo(1));
        }

        [Test]
        public void UnchangedInputs_NoAllocation()
        {
            var ch = Channel.Create(3);
            for (int i = 0; i < 100; i++) { Update(ch); Update(); }
            Assert.That(Allocations.Fewest(() => Update(ch)), Is.EqualTo(0), "connected");

            var unconnected = new InputIntNode(_ => NoWarning);
            for (int i = 0; i < 100; i++) unconnected.Update(DefaultSize, true, default, RectangleAnchor.Center, null, default, default, 1, true, true, 1, false, false, null, null, null, out _, out _, out _, out _);
            Assert.That(Allocations.Fewest(() => unconnected.Update(DefaultSize, true, default, RectangleAnchor.Center, null, default, default, 1, true, true, 1, false, false, null, null, null, out _, out _, out _, out _)), Is.EqualTo(0), "unconnected (null)");
            unconnected.Dispose();
        }
    }
}
