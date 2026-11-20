using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using P = VL.Qt.Protocol.Prop;

namespace VL.Qt.Tests
{
    /// <summary>TextInput node (#301): description, property diff, value channel (string), null/empty handling, EV_VALUE.</summary>
    public class TextInputNodeTests
    {
        static readonly Vector2 DefaultSize = new(2, 0.4f);
        static readonly IDisposable NoWarning = new Nop();
        sealed class Nop : IDisposable { public void Dispose() { } }

        TextInputNode _node = null!;
        SinkRegistry _registry = null!;
        uint _handle;
        RecordingSink _sink = null!;
        Reconciler _reconciler = null!;

        [SetUp]
        public void SetUp()
        {
            _node = new TextInputNode(_ => NoWarning);
            _registry = new SinkRegistry();
            _handle = _registry.Acquire(new SinkSlot(_node.Core.Sink, 0));
            _sink = new RecordingSink();
            _reconciler = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
        }

        [TearDown]
        public void TearDown() => _node.Dispose();

        TextField Update(out string value, out bool bang, IChannel<string>? channel = null, string? placeholder = "",
            bool editable = true, bool enabled = true, bool autoSize = true)
            => (TextField)_node.Update(DefaultSize, autoSize, default, RectangleAnchor.Center, channel, placeholder, editable, enabled,
                1, false, false, null, null, null, out value, out bang, out _, out _);

        TextField Update(IChannel<string>? channel = null) => Update(out _, out _, channel);

        string[] Frame(out string value, out bool bang, IChannel<string>? channel = null, string? placeholder = "")
        {
            _sink.Clear();
            _reconciler.Update(Update(out value, out bang, channel, placeholder), Space.DIPTopLeft);
            return _sink.Commands.ToArray();
        }

        string[] Frame(IChannel<string>? channel = null) => Frame(out _, out _, channel);

        void UserChanges(string value) => _registry.Dispatch(new SinkRegistryTests.Poll().ValueString(_handle, value).Bytes);

        static string SetValue(string v) => $"SET 1 Value string({v})";

        [Test]
        public void Defaults_EmptyEditable()
        {
            var t = Update(out string value, out bool bang);
            Assert.That(t.Type, Is.EqualTo(ElementType.TextField));
            Assert.That((ushort)ElementType.TextField, Is.EqualTo(Protocol.Type.TextField));
            Assert.That((t.Value, t.Placeholder, t.Editable, t.Enabled, t.AutoSize), Is.EqualTo(("", "", true, true, true)));
            Assert.That((value, bang), Is.EqualTo(("", false)));
            Assert.That(t.Sink.Sink, Is.SameAs(_node.Core.Sink));
        }

        [Test]
        public void Reconciler_SendsOnlyNonDefaults_ChangeDetection()
        {
            var cmds = Frame();
            Assert.That(cmds, Does.Contain($"CREATE 1 TextField"));
            Assert.That(cmds.Where(c => c.StartsWith($"SET 1 Value ", StringComparison.Ordinal) || c.StartsWith($"SET 1 Placeholder ", StringComparison.Ordinal)
                || c.StartsWith($"SET 1 AutoSize ", StringComparison.Ordinal)), Is.Empty, "QtNative defaults are not sent");

            var a = Update();
            Assert.That(Update(), Is.SameAs(a));

            _sink.Clear();
            _reconciler.Update(Update(out _, out _, placeholder: "Name", editable: false, enabled: false, autoSize: false), Space.DIPTopLeft);
            Assert.That(_sink.Commands, Is.EquivalentTo(new[]
            {
                $"SET 1 AutoSize bool(0)", $"SET 1 Placeholder string(Name)", $"SET 1 Editable bool(0)", $"SET 1 Enabled bool(0)",
            }));
        }

        [Test]
        public void StartValueFromPin_ThenSetFromPatch()
        {
            var ch = Channel.Create("abc");
            Assert.That(Frame(out string value, out bool bang, ch), Does.Contain(SetValue("abc")));
            Assert.That((value, bang), Is.EqualTo(("abc", false)));

            Assert.That(Frame(ch).Where(c => c.StartsWith("SET", StringComparison.Ordinal)), Is.Empty, "unchanged: nothing");

            ch.Value = "äö";
            Assert.That(Frame(out value, out bang, ch), Is.EqualTo(new[] { SetValue("äö") }));
            Assert.That((value, bang), Is.EqualTo(("äö", false)), "a patch change is no Bang");
        }

        [Test]
        public void UserChange_WritesChannel_Bang_NoEchoAfterwards()
        {
            var ch = Channel.Create("a");
            Frame(ch);

            UserChanges("typed");
            var cmds = Frame(out string value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo(("typed", true, "typed", "Qt")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue("typed") }), "no-op in Qt, repairs a stale SET (PR #354)");

            cmds = Frame(out value, out bang, ch);
            Assert.That((value, bang), Is.EqualTo(("typed", false)), "Bang for one frame");
            Assert.That(cmds, Is.Empty, "the channel write of Qt is not seen as a patch change");
        }

        [Test]
        public void UserClearsTheText_EmptyStringIsAValue()
        {
            var ch = Channel.Create("abc");
            Frame(ch);
            UserChanges("");
            var cmds = Frame(out string value, out bool bang, ch);
            Assert.That((value, bang, ch.Value), Is.EqualTo(("", true, "")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue("") }));
        }

        [Test]
        public void ConflictInTheSameFrame_UserWins()
        {
            var ch = Channel.Create("a");
            Frame(ch);

            ch.Value = "patch";
            UserChanges("user");
            var cmds = Frame(out string value, out bool bang, ch);
            Assert.That((value, bang, ch.Value, ch.LatestAuthor), Is.EqualTo(("user", true, "user", "Qt")));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue("user") }));
            Assert.That(Frame(ch), Is.Empty, "stable afterwards");
        }

        [Test]
        public void NullStrings_AreEmpty()
        {
            var ch = Channel.Create<string>("x");
            Frame(ch);
            ch.Value = null!;
            var cmds = Frame(out string value, out _, ch, placeholder: null);
            Assert.That(value, Is.EqualTo(""));
            Assert.That(cmds, Is.EqualTo(new[] { SetValue("") }));

            ch.Value = "";
            Assert.That(Frame(ch), Is.Empty, "null and empty are the same description");

            var t = Update(out value, out _, null, placeholder: null);
            Assert.That((t.Placeholder, t.Value), Is.EqualTo(("", "")));
        }

        [Test]
        public void WithoutChannel_ValueFollowsTheUser()
        {
            Frame();
            UserChanges("hi");
            Frame(out string value, out bool bang);
            Assert.That((value, bang), Is.EqualTo(("hi", true)));
            Frame(out value, out bang);
            Assert.That((value, bang), Is.EqualTo(("hi", false)));
        }

        [Test]
        public void UnchangedInputs_NoAllocation()
        {
            var ch = Channel.Create("abc");
            for (int i = 0; i < 100; i++) { Update(ch); Update(); }
            Assert.That(Allocations.Fewest(() => Update(ch)), Is.EqualTo(0), "connected, equal strings");

            // a patch value with equal content but a new string instance must not allocate or describe again either
            var ch2 = Channel.Create("abc");
            var node = new TextInputNode(_ => NoWarning);
            for (int i = 0; i < 100; i++) node.Update(DefaultSize, true, default, RectangleAnchor.Center, ch2, "ph", true, true, 1, false, false, null, null, null, out _, out _, out _, out _);
            Assert.That(Allocations.Fewest(() => node.Update(DefaultSize, true, default, RectangleAnchor.Center, ch2, "ph", true, true, 1, false, false, null, null, null, out _, out _, out _, out _)), Is.EqualTo(0), "with placeholder");
            node.Dispose();

            var unconnected = new TextInputNode(_ => NoWarning);
            for (int i = 0; i < 100; i++) unconnected.Update(DefaultSize, true, default, RectangleAnchor.Center, null, null, true, true, 1, false, false, null, null, null, out _, out _, out _, out _);
            Assert.That(Allocations.Fewest(() => unconnected.Update(DefaultSize, true, default, RectangleAnchor.Center, null, null, true, true, 1, false, false, null, null, null, out _, out _, out _, out _)), Is.EqualTo(0), "unconnected (null)");
            unconnected.Dispose();
        }
    }
}
