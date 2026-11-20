using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using P = VL.Qt.Wire.Protocol.Prop;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Auto Padding / Padding of Blur and DropShadow: fold into the one effect area and the reconciler SETs.</summary>
    public class EffectPaddingTests
    {
        private static BlurEffect Blur(Effect? previous = null, bool auto = true, RectangleF padding = default)
            => new(previous, 0.5f, 32, 0, auto, padding);

        private static DropShadowEffect Shadow(Effect? previous = null, bool auto = true, RectangleF padding = default)
            => new(previous, Color4.Black, new Vector2(0.1f, 0.2f), 1, 1, auto, padding);

        [Test]
        public void Fold_Default_IsAutoPadding()
        {
            var desc = Effect.Fold(Shadow(Blur()), out _)!.Value;
            Assert.That(desc.AutoPadding, Is.True);
            Assert.That(desc.PaddingRect, Is.EqualTo(default(RectangleF)));
        }

        [Test]
        public void Fold_OneManualLink_UsesItsPadding()
        {
            var desc = Effect.Fold(Shadow(Blur(null, false, new RectangleF(0.1f, 0.2f, 0.3f, 0.4f))), out _)!.Value;
            Assert.That(desc.AutoPadding, Is.False);
            Assert.That(desc.PaddingRect, Is.EqualTo(new RectangleF(0.1f, 0.2f, 0.3f, 0.4f)));
        }

        [Test]
        public void Fold_BothManual_SideWiseMaximum()
        {
            var a = Blur(null, false, new RectangleF(0.1f, 0.5f, 0.3f, 0.1f));
            var b = Shadow(a, false, new RectangleF(0.4f, 0.2f, 0.1f, 0.6f));
            var desc = Effect.Fold(b, out _)!.Value;
            Assert.That(desc.AutoPadding, Is.False);
            Assert.That(desc.PaddingRect, Is.EqualTo(new RectangleF(0.4f, 0.5f, 0.3f, 0.6f)));
        }

        [Test]
        public void Nodes_PaddingPins_FlowIntoEffect()
        {
            var pad = new RectangleF(1, 2, 3, 4);
            var blur = (BlurEffect)new BlurNode().Core.Describe(null, new BlurNode.Props(0.5f, 32, 0, false, pad))!;
            var shadow = (DropShadowEffect)new DropShadowNode().Core.Describe(blur, new DropShadowNode.Props(Color4.Black, Vector2.Zero, 1, 1, true, default))!;
            Assert.That(blur.AutoPadding, Is.False);
            Assert.That(blur.Padding, Is.EqualTo(pad));
            Assert.That(Effect.Fold(shadow, out _)!.Value.PaddingRect, Is.EqualTo(pad));
        }

        [Test]
        public void Reconciler_SendsAutoPaddingAndPaddingRect()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new SceneRegistry());
            rec.Update(new Rectangle { Effect = Blur(null, false, new RectangleF(1, 2, 3, 4)) }, Space.DIPTopLeft);
            var chain = sink.OfOp("CHAIN").Single();
            Assert.That(chain, Does.Contain($" {nameof(P.EffectAutoPadding)}=bool(0)"));
            Assert.That(chain, Does.Contain($" {nameof(P.EffectPaddingRect)}=rect(100,200,300,400)"), "units to DIP");
            Assert.That(sink.OfOp("SET").Count(), Is.EqualTo(0));
        }
    }
}
