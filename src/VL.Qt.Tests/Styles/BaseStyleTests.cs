using System.Linq;
using NUnit.Framework;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Tests.Support;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Styles
{
    /// <summary>Base style: sent once per Controls element at CREATE, nothing on unchanged frames, again after a resync.</summary>
    public class BaseStyleTests
    {
        private RecordingSink _sink = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        }

        [Test]
        public void ButtonCreate_SendsOneSetStyle_RightAfterCreate_OnlyOnce()
        {
            _rec.Update(new Button(), SpaceMapping.Default);

            var styles = _sink.OfOp("STYLE").ToArray();
            Assert.That(styles, Has.Length.EqualTo(1));
            Assert.That(styles[0], Does.StartWith("STYLE 1 " + Protocol.StyleSet.Clear));
            Assert.That(_sink.Commands.IndexOf(styles[0]), Is.EqualTo(_sink.Commands.IndexOf("CREATE 1 Button") + 1));

            _sink.Clear();
            _rec.Update(new Button(), SpaceMapping.Default);
            Assert.That(_sink.OfOp("STYLE"), Is.Empty);

            _sink.Clear();
            _rec.Update(new Button { Text = "other" }, SpaceMapping.Default);
            Assert.That(_sink.OfOp("STYLE"), Is.Empty);
        }

        [Test]
        public void NonControls_GetNoStyle()
        {
            _rec.Update(new Rectangle(), SpaceMapping.Default);
            Assert.That(_sink.OfOp("STYLE"), Is.Empty);
        }

        [Test]
        public void AfterDiscard_StyleIsSentAgain()
        {
            _rec.Update(new Slider(), SpaceMapping.Default);
            _rec.Discard();
            _sink.Clear();
            _rec.Update(new Slider(), SpaceMapping.Default);
            Assert.That(_sink.OfOp("STYLE").Count(), Is.EqualTo(1));
        }
    }
}
