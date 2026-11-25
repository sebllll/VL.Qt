using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests
{
    /// <summary>Toggle node: description for both looks, property diff, look change, value channel and EVENT_VALUE (bool).</summary>
    public class ToggleNodeTests
    {
        private static readonly Vector2 DefaultSize = new(0.68f, 0.4f);
        private static readonly IDisposable NoWarning = new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }

        private ToggleNode _node = null!;
        private FeedbackRouter _registry = null!;
        private uint _handle;
        private RecordingSink _sink = null!;
        private Reconciler _reconciler = null!;

        [SetUp]
        public void SetUp()
        {
            _node = new ToggleNode(_ => NoWarning);
            _registry = new FeedbackRouter();
            _handle = _registry.Acquire(new FeedbackSlot(_node.Core.Feedback, 0));
            _sink = new RecordingSink();
            _reconciler = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        }

        [TearDown]
        public void TearDown() => _node.Dispose();

        private Element Update(out bool value, out bool bang, IChannel<bool>? channel = null, ToggleLook look = ToggleLook.Checkbox,
            bool enabled = true, bool autoSize = true)
            => (Element)_node.Update(autoSize, DefaultSize, default, RectangleAnchor.Center, channel, look, enabled,
                1, false, false, null, null, null, out value, out bang, out _, out _);

        private Element Update(IChannel<bool>? channel = null, ToggleLook look = ToggleLook.Checkbox)
            => Update(out _, out _, channel, look);

        private string[] Frame(out bool value, out bool bang, IChannel<bool>? channel = null, ToggleLook look = ToggleLook.Checkbox)
        {
            _sink.Clear();
            _reconciler.Update(Update(out value, out bang, channel, look), Space.DIPTopLeft);
            return _sink.Commands.ToArray();
        }

        private string[] Frame(IChannel<bool>? channel = null, ToggleLook look = ToggleLook.Checkbox)
            => Frame(out _, out _, channel, look);

        private void UserChanges(bool value) => _registry.Dispatch(new FeedbackRouterTests.Poll().Value(_handle, Protocol.ValueType.Bool, new byte[] { (byte)(value ? 1 : 0) }).Bytes);

        private static string SetValue(int id, bool v) => $"SET {id} Value bool({(v ? 1 : 0)})";

        [Test]
        public void Defaults_Checkbox()
        {
            var e = Update(out bool value, out bool bang);
            var c = (CheckBox)e;
            Assert.That(c.Type, Is.EqualTo(ElementType.CheckBox));
            Assert.That((ushort)ElementType.CheckBox, Is.EqualTo(Protocol.ElementType.CheckBox));
            Assert.That((c.Value, c.Enabled, c.AutoSize), Is.EqualTo((false, true, true)));
            Assert.That((value, bang), Is.EqualTo((false, false)));
            Assert.That(c.Feedback.Feedback, Is.SameAs(_node.Core.Feedback));
        }

        [Test]
        public void SwitchLook_Describes_Switch()
        {
            var s = (Switch)Update(look: ToggleLook.Switch);
            Assert.That(s.Type, Is.EqualTo(ElementType.Switch));
            Assert.That((ushort)ElementType.Switch, Is.EqualTo(Protocol.ElementType.Switch));
            Assert.That((s.Value, s.Enabled, s.AutoSize), Is.EqualTo((false, true, true)));
            Assert.That(s.Feedback.Feedback, Is.SameAs(_node.Core.Feedback));
        }

        [Test]
        public void Reconciler_SendsOnlyNonDefaults_ChangeDetection()
        {
            var cmds = Frame();
            Assert.That(cmds, Does.Contain($"CREATE 1 CheckBox"));
            Assert.That(cmds.Where(c => c.StartsWith($"SET 1 Value ", StringComparison.Ordinal) || c.StartsWith($"SET 1 Enabled ", StringComparison.Ordinal)
                || c.StartsWith($"SET 1 AutoSize ", StringComparison.Ordinal)), Is.Empty, "QtNative defaults are not sent");

            var a = Update();
            Assert.That(Update(), Is.SameAs(a));

            _sink.Clear();
            _reconciler.Update(Update(out _, out _, enabled: false, autoSize: false), Space.DIPTopLeft);
            Assert.That(_sink.Commands, Is.EquivalentTo(new[] { $"SET 1 AutoSize bool(0)", $"SET 1 Enabled bool(0)" }));
        }

        [Test]
        public void LookChange_ReplacesTheElement_KeepsTheValue()
        {
            var ch = Channel.Create(true);
            Frame(ch);
            Assert.That(Frame(ch, ToggleLook.Checkbox), Is.Empty);

            var cmds = Frame(out bool value, out bool bang, ch, ToggleLook.Switch);
            Assert.That((value, bang), Is.EqualTo((true, false)));
            Assert.That(cmds, Does.Contain("DESTROY 1"));
            Assert.That(cmds, Does.Contain($"CREATE 1 Switch"), "ids are reused");
            Assert.That(cmds, Does.Contain(SetValue(1, true)), "the new element gets the value");
            Assert.That(cmds.Any(c => c.StartsWith("BIND 1 ", StringComparison.Ordinal)), Is.True, "the feedback is bound to the new element");

            Assert.That(Frame(ch, ToggleLook.Switch), Is.Empty, "stable afterwards");

            // user input reaches the node through the new element and writes the channel
            UserChanges(false);
            cmds = Frame(out value, out bang, ch, ToggleLook.Switch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo((false, true, false, "Qt")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue(1, false) }));

            // and back
            cmds = Frame(out value, out _, ch, ToggleLook.Checkbox);
            Assert.That(value, Is.False);
            Assert.That(cmds, Does.Contain($"CREATE 1 CheckBox"));
            Assert.That(cmds.Any(c => c.StartsWith($"SET 1 Value ", StringComparison.Ordinal)), Is.False, "false is QtNative default");
        }

        [Test]
        public void StartValueFromPin_ThenSetFromPatch()
        {
            var ch = Channel.Create(true);
            Assert.That(Frame(out bool value, out bool bang, ch), Does.Contain(SetValue(1, true)));
            Assert.That((value, bang), Is.EqualTo((true, false)));

            Assert.That(Frame(ch).Where(c => c.StartsWith("SET", StringComparison.Ordinal)), Is.Empty, "unchanged: nothing");

            ch.Value = false;
            Assert.That(Frame(out value, out bang, ch), Is.EqualTo(new[] { SetValue(1, false) }));
            Assert.That((value, bang), Is.EqualTo((false, false)), "a patch change is no Bang");
        }

        [Test]
        public void UserChange_WritesChannel_Bang_NoEchoAfterwards()
        {
            var ch = Channel.Create(false);
            Frame(ch);

            UserChanges(true);
            var cmds = Frame(out bool value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo((true, true, true, "Qt")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue(1, true) }), "no-op in Qt, repairs a stale SET (PR #354)");

            cmds = Frame(out value, out bang, ch);
            Assert.That((value, bang), Is.EqualTo((true, false)), "Bang for one frame");
            Assert.That(cmds, Is.Empty, "the channel write of Qt is not seen as a patch change");
        }

        [Test]
        public void ConflictInTheSameFrame_UserWins()
        {
            var ch = Channel.Create(false);
            Frame(ch);

            ch.Value = true; // patch
            UserChanges(false); // user, same frame: Qt shows false (was true before the patch change? no: false -> user sets false)
            var cmds = Frame(out bool value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo((false, true, false, "Qt")));
            Assert.That(cmds, Is.Empty, "user value equals the last sent one");
            Assert.That(Frame(ch), Is.Empty, "stable afterwards");
        }

        [Test]
        public void WithoutChannel_ValueFollowsTheUser()
        {
            Frame();
            UserChanges(true);
            Frame(out bool value, out bool bang);
            Assert.That((value, bang), Is.EqualTo((true, true)));
            Frame(out value, out bang);
            Assert.That((value, bang), Is.EqualTo((true, false)));
        }

        [Test]
        public void UnchangedInputs_NoAllocation()
        {
            var ch = Channel.Create(true);
            for (int i = 0; i < 100; i++) { Update(ch); Update(); }
            Assert.That(Allocations.Fewest(() => Update(ch)), Is.EqualTo(0), "connected");

            var unconnected = new ToggleNode(_ => NoWarning);
            for (int i = 0; i < 100; i++) unconnected.Update(true, DefaultSize, default, RectangleAnchor.Center, null, ToggleLook.Switch, true, 1, false, false, null, null, null, out _, out _, out _, out _);
            Assert.That(Allocations.Fewest(() => unconnected.Update(true, DefaultSize, default, RectangleAnchor.Center, null, ToggleLook.Switch, true, 1, false, false, null, null, null, out _, out _, out _, out _)), Is.EqualTo(0), "unconnected (null)");
            unconnected.Dispose();
        }
    }
}
