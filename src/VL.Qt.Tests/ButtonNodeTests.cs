using System;
using System.Linq;
using System.Reactive;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using P = VL.Qt.Protocol.Prop;

namespace VL.Qt.Tests
{
    /// <summary>Button node (#213): description, property diff, Bang and the IsClicked channel without Interactive.</summary>
    public class ButtonNodeTests
    {
        static readonly Vector2 DefaultSize = new(1, 0.4f);
        static readonly IDisposable NoWarning = new Nop();
        sealed class Nop : IDisposable { public void Dispose() { } }

        ButtonNode _node = null!;

        [SetUp]
        public void SetUp() => _node = new ButtonNode(_ => NoWarning);

        [TearDown]
        public void TearDown() => _node.Dispose();

        // Pin defaults as declared on Update.
        Button Update(out bool bang, string text = "Button", bool enabled = true, bool autoRepeat = false, bool autoSize = true,
            IChannel<Unit>? isClicked = null, Interaction? interaction = null)
            => (Button)_node.Update(DefaultSize, autoSize, default, RectangleAnchor.Center, text, enabled, autoRepeat, isClicked,
                1, false, false, interaction, null, null, out bang, out _, out _);

        Button Update(string text = "Button", bool enabled = true, bool autoRepeat = false, bool autoSize = true)
            => Update(out _, text, enabled, autoRepeat, autoSize);

        [Test]
        public void Defaults_VisibleButton()
        {
            var b = Update();
            Assert.That(b.Type, Is.EqualTo(ElementType.Button));
            Assert.That((ushort)ElementType.Button, Is.EqualTo(Protocol.Type.Button));
            Assert.That(b.Text, Is.EqualTo("Button"));
            Assert.That(b.Enabled, Is.True);
            Assert.That(b.AutoRepeat, Is.False);
            Assert.That(b.AutoSize, Is.True);
            Assert.That(b.Interaction, Is.Null);
            Assert.That(b.Sink.Sink, Is.SameAs(_node.Core.Sink));
        }

        [Test]
        public void Props_ChangeDetection()
        {
            var a = Update();
            Assert.That(Update(), Is.SameAs(a));
            var b = Update(text: "OK", enabled: false, autoRepeat: true, autoSize: false);
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That((b.Text, b.Enabled, b.AutoRepeat, b.AutoSize), Is.EqualTo(("OK", false, true, false)));
            Assert.That(Update(text: null!).Text, Is.Empty, "null text → empty");
        }

        [Test]
        public void Reconciler_SendsTypeAndChangedProps()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new SceneRegistry());
            rec.Update(Update(), Space.DIPTopLeft);
            Assert.That(sink.Commands, Does.Contain($"CREATE 1 Button"));
            Assert.That(sink.Commands, Does.Contain($"SET 1 Text string(Button)"));
            Assert.That(sink.Commands.Any(c => c.StartsWith($"SET 1 Enabled ") || c.StartsWith($"SET 1 AutoRepeat ")
                || c.StartsWith($"SET 1 AutoSize ")), Is.False, "Qt defaults are not sent");
            Assert.That(sink.Commands.Single(c => c.StartsWith("BIND 1 ")), Does.EndWith(" 0"), "bound without flags");

            sink.Clear();
            rec.Update(Update(text: "OK", enabled: false, autoRepeat: true, autoSize: false), Space.DIPTopLeft);
            Assert.That(sink.Commands, Is.EquivalentTo(new[]
            {
                $"SET 1 AutoSize bool(0)", $"SET 1 Text string(OK)", $"SET 1 Enabled bool(0)", $"SET 1 AutoRepeat bool(1)",
            }));
        }

        [Test]
        public void Bang_OneUpdatePerClickBatch()
        {
            Update(out bool bang);
            Assert.That(bang, Is.False);
            var s = _node.Core.Sink[0];
            s.PendingClicks = 2;
            Update(out bang);
            Assert.That(bang, Is.True);
            Update(out bang);
            Assert.That(bang, Is.False, "true for exactly one Update");
        }

        [Test]
        public void IsClicked_OneValuePerClick()
        {
            var clicked = Channel.Create(Unit.Default);
            int clicks = 0;
            using var c = clicked.Subscribe(_ => clicks++);
            clicks = 0; // ignore the replay on subscribe

            var b = Update(out _, isClicked: clicked);
            Assert.That(b.Interaction, Is.Null, "clicks come from Qt clicked, no handler");

            _node.Core.Sink[0].PendingClicks = 3;
            Update(out bool bang, isClicked: clicked);
            Assert.That(clicks, Is.EqualTo(3));
            Assert.That(bang, Is.True);
            Assert.That(clicked.LatestAuthor, Is.EqualTo("Qt"));
        }

        [Test]
        public void InteractionPin_PassedToDescription_ChangeByReference()
        {
            var chain = new HoverNode(_ => NoWarning).Update(null, null);
            var a = Update(out _, interaction: chain);
            Assert.That(a.Interaction, Is.SameAs(chain));
            Assert.That(Update(out _, interaction: chain), Is.SameAs(a));
            Assert.That(Update(out _), Is.Not.SameAs(a));
        }

        [Test]
        public void UnchangedInputs_NoAllocation()
        {
            var clicked = Channel.Create(Unit.Default);
            for (int i = 0; i < 100; i++) Update(out _, isClicked: clicked);
            Assert.That(Allocations.Fewest(() => Update(out _, isClicked: clicked)), Is.EqualTo(0));
        }
    }
}
