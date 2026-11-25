using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using K = VL.Qt.Wire.Protocol.MultiEffectKey;
using P = VL.Qt.Wire.Protocol.PropertyId;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Auto Padding / Padding of Blur and DropShadow: merged into the one effect area of a run and the reconciler SETs.</summary>
    public class EffectPaddingTests
    {
        private static BlurEffect Blur(Effect? previous = null, bool auto = true, RectangleF padding = default)
            => new(previous, 0.5f, 32, 0, auto, padding);

        private static DropShadowEffect Shadow(Effect? previous = null, bool auto = true, RectangleF padding = default)
            => new(previous, Color4.Black, new Vector2(0.1f, 0.2f), 1, 1, auto, padding);

        // A chain of merged effects only: its one MultiEffect run.
        private static EffectDesc SingleRun(Effect chain) => EffectSegments.Of(chain, out _)[0].Multi;

        [Test]
        public void Run_Default_IsAutoPadding()
        {
            var desc = SingleRun(Shadow(Blur()));
            Assert.That(desc.AutoPadding, Is.True);
            Assert.That(desc.PaddingRect, Is.EqualTo(default(RectangleF)));
        }

        [Test]
        public void Run_OneManualLink_UsesItsPadding()
        {
            var desc = SingleRun(Shadow(Blur(null, false, new RectangleF(0.1f, 0.2f, 0.3f, 0.4f))));
            Assert.That(desc.AutoPadding, Is.False);
            Assert.That(desc.PaddingRect, Is.EqualTo(new RectangleF(0.1f, 0.2f, 0.3f, 0.4f)));
        }

        [Test]
        public void Run_BothManual_SideWiseMaximum()
        {
            var a = Blur(null, false, new RectangleF(0.1f, 0.5f, 0.3f, 0.1f));
            var b = Shadow(a, false, new RectangleF(0.4f, 0.2f, 0.1f, 0.6f));
            var desc = SingleRun(b);
            Assert.That(desc.AutoPadding, Is.False);
            Assert.That(desc.PaddingRect, Is.EqualTo(new RectangleF(0.4f, 0.5f, 0.3f, 0.6f)));
        }

        [Test]
        public void Nodes_PaddingPins_FlowIntoEffect()
        {
            var pad = new RectangleF(1, 2, 3, 4);
            var blur = (BlurEffect)new BlurNode().Update(null, 0.5f, 32, 0, false, pad, true)!;
            var shadow = (DropShadowEffect)new DropShadowNode().Update(blur, Color4.Black, Vector2.Zero, 1, 1, true, default, true)!;
            Assert.That(blur.AutoPadding, Is.False);
            Assert.That(blur.Padding, Is.EqualTo(pad));
            Assert.That(SingleRun(shadow).PaddingRect, Is.EqualTo(pad));
        }

        [Test]
        public void Reconciler_SendsAutoPaddingAndPaddingRect()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(new Rectangle { Effect = Blur(null, false, new RectangleF(1, 2, 3, 4)) }, Space.DIPTopLeft);
            var chain = sink.OfOp("CHAIN").Single();
            Assert.That(chain, Does.Contain($" {nameof(K.AutoPadding)}=bool(0)"));
            Assert.That(chain, Does.Contain($" {nameof(K.PaddingRect)}=rect(1,2,3,4)"), "DIP as given");
            Assert.That(sink.OfOp("SET").Count(), Is.EqualTo(0));
        }

        [Test]
        public void Reconciler_PaddingIsDipInEverySpace()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(new Rectangle { Effect = Blur(null, false, new RectangleF(1, 2, 3, 4)) }, Space.Normalized);
            Assert.That(sink.OfOp("CHAIN").Single(), Does.Contain($" {nameof(K.PaddingRect)}=rect(1,2,3,4)"), "the renderer's space does not scale effect lengths");
        }
    }
}
