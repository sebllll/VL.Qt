﻿using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Tests.Support;
using K = VL.Qt.Wire.Protocol.MultiEffectKey;
using P = VL.Qt.Wire.Protocol.PropertyId;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests.Elements
{
    /// <summary>Effect chain: the folded MultiEffect run and the reconciler's effect layer SETs.</summary>
    public class EffectTests
    {
        private RecordingSink _sink = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        }

        private void Frame(Element? root)
        {
            _sink.Clear();
            _rec.Update(root, SpaceMapping.Default);
        }

        private int Sets(ushort prop) => _sink.OfOp("SET").Count(c => c.Split(' ')[2] == RecordingSink.PropName(prop));

        private static BlurEffect Blur(Effect? previous = null, float amount = 0.5f) => new(previous, amount, 32, 0);
        private static DropShadowEffect Shadow(Effect? previous = null) => new(previous, Color4.Black, new Vector2(0.1f, 0.2f), 1, 1);
        private static ColorAdjustEffect Adjust(Effect? previous = null) => new(previous, 0.1f, 0.2f, 0.3f);

        // A chain of merged effects only: one MultiEffect run.
        private static EffectDesc SingleRun(Effect chain, out ImmutableArray<string> warnings)
        {
            var links = EffectSegments.Of(chain, out warnings);
            Assert.That(links.Length, Is.EqualTo(1));
            return links[0].Multi;
        }

        [Test]
        public void Run_IsOrderIndependent()
        {
            var a = SingleRun(Adjust(Shadow(Blur())), out var wa);
            var b = SingleRun(Blur(Adjust(Shadow())), out var wb);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(wa, Is.Empty);
            Assert.That(wb, Is.Empty);
            Assert.That(a.BlurEnabled && a.ShadowEnabled && a.Brightness == 0.1f);
        }

        [Test]
        public void Run_DuplicateKind_WarnsAndNearestToElementWins()
        {
            // Head of the chain is nearest to the Effect pin.
            var desc = SingleRun(Blur(Blur(null, 0.2f), 0.8f), out var warnings);
            Assert.That(warnings.Single(), Does.Contain("Blur"));
            Assert.That(desc.Blur, Is.EqualTo(0.8f));
        }

        [Test]
        public void Nodes_UnchangedInputs_KeepInstance()
        {
            var node = new BlurNode();
            var first = node.Update(null, 0.5f, 32, 0, true, default, true);
            Assert.That(node.Update(null, 0.5f, 32, 0, true, default, true), Is.SameAs(first));
            Assert.That(node.Update(null, 0.6f, 32, 0, true, default, true), Is.Not.SameAs(first));
        }

        private string[] Chains => _sink.OfOp("CHAIN").ToArray();

        [Test]
        public void Reconciler_SendsChainOnce_ThenNothing()
        {
            var chain = Shadow(Blur());
            Frame(new Rectangle { Effect = chain });
            // One MULTI run with the folded values (offset y 0.2 DIP, not converted), no EFFECT_* SET.
            Assert.That(Chains, Has.Length.EqualTo(1));
            Assert.That(Chains[0], Does.StartWith("CHAIN 1 | multi BlurEnabled=bool(1) Blur=f32(0.5) "));
            Assert.That(Chains[0], Does.Contain(" ShadowVerticalOffset=f32(0.2)"));
            Assert.That(Sets(K.BlurEnabled), Is.EqualTo(0));

            // New description with the same chain instance: nothing.
            Frame(new Rectangle { Effect = chain, Opacity = 1 });
            Assert.That(_sink.Commands, Is.Empty, _sink.ToString());

            // Changed chain: the whole chain again, no SET.
            Frame(new Rectangle { Effect = Shadow(Blur(null, 0.7f)) });
            Assert.That(Chains, Has.Length.EqualTo(1));
            Assert.That(Chains[0], Does.Contain("Blur=f32(0.7)"));
            Assert.That(_sink.OfOp("SET").Count(), Is.EqualTo(0));
        }

        [Test]
        public void Reconciler_RemovingEffect_SendsEmptyChain()
        {
            Frame(new Rectangle { Effect = Blur() });
            Frame(new Rectangle());
            Assert.That(_sink.Commands, Is.EqualTo(new[] { "CHAIN 1" }));

            // Nothing left to remove: nothing.
            Frame(new Rectangle());
            Assert.That(_sink.Commands, Is.Empty);

            // Back on: the whole chain.
            Frame(new Rectangle { Effect = Blur() });
            Assert.That(Chains, Has.Length.EqualTo(1));
            Assert.That(Chains[0], Does.StartWith("CHAIN 1 | multi BlurEnabled=bool(1)"));
        }

        [Test]
        public void Reconciler_NoEffect_SendsNoEffectCommands()
        {
            Frame(new Rectangle());
            Assert.That(Chains, Is.Empty);
            Assert.That(_sink.Commands.Any(c => c.StartsWith("SET ") && c.Split(' ')[2] == nameof(K.BlurEnabled)), Is.False);
        }

        [Test]
        public void Reconciler_Mask_AttachedWithoutReparent_AndRemoved()
        {
            var mask = new Text { Content = "Hi" };
            var chain = new MaskEffect(null, mask, 0, 0, 1, 0, false);
            Frame(new Rectangle { Effect = chain });
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(2));
            Assert.That(Chains, Has.Length.EqualTo(1));
            Assert.That(Chains[0], Does.Contain("MaskSource=ref(2)"));
            Assert.That(_sink.OfOp("REPARENT").Count(), Is.EqualTo(1), "only the element itself");
            Assert.That(_sink.Commands.Any(c => c.StartsWith("REPARENT 2 ")), Is.False);
            Assert.That(_sink.Commands.FindIndex(c => c.StartsWith("CREATE 2 ")), Is.LessThan(_sink.Commands.FindIndex(c => c.StartsWith("CHAIN "))),
                "the mask object exists before the chain refers to it");

            // Same chain: nothing.
            Frame(new Rectangle { Effect = chain });
            Assert.That(_sink.Commands, Is.Empty, _sink.ToString());

            // Mask gone: the chain no longer refers to it, then destroy.
            Frame(new Rectangle());
            Assert.That(_sink.Commands, Is.EqualTo(new[] { "CHAIN 1", "DESTROY 2" }));
        }

        [Test]
        public void Reconciler_ElementWithMaskRemoved_DestroysOnlyElement()
        {
            Frame(new Group
            {
                Children = ImmutableArray.Create<Element>(new Rectangle { Effect = new MaskEffect(null, new Text(), 0, 0, 1, 0, false) }),
            });
            Frame(new Group());
            Assert.That(_sink.OfOp("DESTROY").ToArray(), Is.EqualTo(new[] { "DESTROY 2" }));
        }

        [Test]
        public void Reconciler_DuplicateKind_WritesWarningIntoFeedback()
        {
            var feedback = new ElementFeedback();
            var slot = new FeedbackSlot(feedback, 0);
            Frame(new Rectangle { Feedback = slot, Effect = Blur(Blur()) });
            Assert.That(feedback.Warning, Does.Contain("Blur"));

            Frame(new Rectangle { Feedback = slot, Effect = Blur() });
            Assert.That(feedback.Warning, Is.Null);
        }
    }
}
