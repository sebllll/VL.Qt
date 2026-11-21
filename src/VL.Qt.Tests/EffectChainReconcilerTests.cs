using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using P = VL.Qt.Wire.Protocol.PropertyId;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>SET_EFFECT_CHAIN (plan effects/08 task 5): what the reconciler sends for the Effect chain of an element.</summary>
    public class EffectChainReconcilerTests
    {
        private sealed class TestShaderEffect : ShaderEffect
        {
            public TestShaderEffect(Effect? previous, params Uniform[] uniforms)
                : base(previous, Protocol.EffectTemplate.Single, ImmutableArray.Create(7UL), uniforms.ToSpread(), new RectangleF(1, 2, 3, 4), 10)
            {
            }
        }

        private RecordingSink _sink = null!;
        private IdAllocator _ids = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _ids = new IdAllocator();
            _rec = new Reconciler(_sink, _ids, new OccurrenceRegistry());
        }

        private void Frame(Element? root)
        {
            _sink.Clear();
            _rec.Update(root, Space.DIPTopLeft);
        }

        private string[] Chains => _sink.OfOp("CHAIN").ToArray();

        /// <summary>The links of the (only) chain of the frame: "multi ...", "shader ...".</summary>
        private string[] Links()
        {
            Assert.That(Chains, Has.Length.EqualTo(1), _sink.ToString());
            return Chains[0].Split(" | ").Skip(1).ToArray();
        }

        private int IndexOf(string prefix) => _sink.Commands.FindIndex(c => c.StartsWith(prefix));

        private static BlurEffect Blur(Effect? previous = null) => new(previous, 0.5f, 32, 0);
        private static DropShadowEffect Shadow(Effect? previous = null) => new(previous, Color4.Black, new Vector2(0.1f, 0.2f), 1, 1);
        private static MaskEffect Mask(Effect? previous, Element source) => new(previous, source, 0, 0, 1, 0, false);

        [Test]
        public void Order_StartOfChainFirst()
        {
            // vvvv: Blur -> Shader -> DropShadow -> (Effect pin).
            var chain = Shadow(new TestShaderEffect(Blur()));
            Frame(new Rectangle { Effect = chain });
            var links = Links();
            Assert.That(links, Has.Length.EqualTo(3));
            Assert.That(links[0], Does.StartWith($"multi {nameof(Protocol.MultiEffectKey.BlurEnabled)}=bool(1)"));
            Assert.That(links[1], Does.StartWith("shader 1 [7] pad=(100,200,300,400) strength=1000"));
            Assert.That(links[2], Does.StartWith($"multi {nameof(Protocol.MultiEffectKey.ShadowEnabled)}=bool(1)"));
            Assert.That(links[2], Does.Not.Contain($"{nameof(Protocol.MultiEffectKey.BlurEnabled)}="));

            // Swapping the order swaps the links.
            Frame(new Rectangle { Effect = Blur(new TestShaderEffect(Shadow())) });
            links = Links();
            Assert.That(links[0], Does.StartWith($"multi {nameof(Protocol.MultiEffectKey.ShadowEnabled)}=bool(1)"));
            Assert.That(links[2], Does.StartWith($"multi {nameof(Protocol.MultiEffectKey.BlurEnabled)}=bool(1)"));
        }

        [Test]
        public void ShaderOnly_NoMultiLink()
        {
            Frame(new Rectangle { Effect = new TestShaderEffect(null, Uniform.Float("amount", 0.25f)) });
            var links = Links();
            Assert.That(links, Has.Length.EqualTo(1));
            Assert.That(links[0], Does.EndWith("strength=1000 amount=f32(0.25)"));
        }

        [Test]
        public void SendsOnlyOnChange()
        {
            var chain = new TestShaderEffect(Blur());
            Frame(new Rectangle { Effect = chain });
            Assert.That(Chains, Has.Length.EqualTo(1));

            // Same instance: nothing, whatever else changes.
            Frame(new Rectangle { Effect = chain, Opacity = 0.5f });
            Assert.That(_sink.Commands.Any(c => c.StartsWith("CHAIN ")), Is.False);

            // A new instance is a change: the whole chain once.
            Frame(new Rectangle { Effect = new TestShaderEffect(Blur()) });
            Assert.That(Chains, Has.Length.EqualTo(1));
            // The MULTI keys (833..855) are no properties of SET; Enabled is also a property of its own (704).
            var keyNames = typeof(Protocol.MultiEffectKey).GetFields().Select(f => f.Name).Except(typeof(Protocol.PropertyId).GetFields().Select(f => f.Name)).ToHashSet();
            Assert.That(_sink.OfOp("SET").Any(c => keyNames.Contains(c.Split(' ')[2])), Is.False, "no MULTI key SET");
        }

        [Test]
        public void TwoRuns_ShaderBetween_EachRunFoldedAlone()
        {
            // Run 1: Blur + Colorize; Shader; run 2: DropShadow.
            Effect chain = new ColorizeEffect(Blur(), Color4.White, 0.5f);
            chain = new TestShaderEffect(chain);
            chain = Shadow(chain);
            Frame(new Rectangle { Effect = chain });
            var links = Links();
            Assert.That(links, Has.Length.EqualTo(3));
            Assert.That(links[0], Does.Contain($"{nameof(Protocol.MultiEffectKey.BlurEnabled)}=").And.Contain($"{nameof(Protocol.MultiEffectKey.Colorization)}="));
            Assert.That(links[0], Does.Not.Contain($"{nameof(Protocol.MultiEffectKey.ShadowEnabled)}="));
            Assert.That(links[2], Does.Contain($"{nameof(Protocol.MultiEffectKey.ShadowEnabled)}="));
            Assert.That(links[2], Does.Not.Contain($"{nameof(Protocol.MultiEffectKey.BlurEnabled)}="));
        }

        [Test]
        public void Warnings_OnePerRun_WrittenIntoFeedback()
        {
            var feedback = new ElementFeedback();
            var slot = new FeedbackSlot(feedback, 0);
            Effect chain = Blur(Blur());
            chain = new TestShaderEffect(chain);
            chain = Blur(Blur(chain));
            Frame(new Rectangle { Feedback = slot, Effect = chain });
            Assert.That(feedback.Warning, Is.Not.Null);
            Assert.That(feedback.Warning!.Split("Blur is used more than once").Length - 1, Is.EqualTo(2), feedback.Warning);

            Frame(new Rectangle { Feedback = slot, Effect = new TestShaderEffect(Blur()) });
            Assert.That(feedback.Warning, Is.Null);
        }

        [Test]
        public void MaskPerRun_OwnObjectEach_NoReparent_DestroyedAfterChain()
        {
            var a = new Text { Content = "A" };
            var b = new Text { Content = "B" };
            var chain = Mask(new TestShaderEffect(Mask(null, a)), b);
            Frame(new Rectangle { Effect = chain });

            var links = Links();
            Assert.That(links[0], Does.Contain($"{nameof(Protocol.MultiEffectKey.MaskSource)}=ref(2)"));
            Assert.That(links[2], Does.Contain($"{nameof(Protocol.MultiEffectKey.MaskSource)}=ref(3)"));
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(3));
            Assert.That(_sink.OfOp("REPARENT").Count(), Is.EqualTo(1), "only the element itself");
            Assert.That(IndexOf("CREATE 3 "), Is.LessThan(IndexOf("CHAIN ")), "objects exist before the chain refers to them");

            // Same chain: nothing at all.
            Frame(new Rectangle { Effect = chain });
            Assert.That(_sink.Commands, Is.Empty, _sink.ToString());

            // Chain gone: it no longer refers to the objects, then they are destroyed.
            Frame(new Rectangle());
            Assert.That(_sink.Commands, Is.EqualTo(new[] { "CHAIN 1", "DESTROY 2", "DESTROY 3" }));
            Assert.That(_ids.IsLive(2) || _ids.IsLive(3), Is.False);
        }

        [Test]
        public void MaskKeptWhenChainChanges_SameElementSameObject()
        {
            var mask = new Text { Content = "A" };
            Frame(new Rectangle { Effect = Mask(null, mask) });
            Frame(new Rectangle { Effect = Blur(Mask(null, mask)) });
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(0));
            Assert.That(_sink.OfOp("DESTROY").Count(), Is.EqualTo(0));
            Assert.That(Links(), Has.Length.EqualTo(1));
            Assert.That(Links()[0], Does.Contain($"{nameof(Protocol.MultiEffectKey.MaskSource)}=ref(2)"));
        }

        [Test]
        public void ShaderTextureUniform_IsObjectOfItsOwn()
        {
            var map = new Rectangle();
            var chain = new TestShaderEffect(null, Uniform.Float("amount", 1), Uniform.FromTexture("map", map));
            Frame(new Rectangle { Effect = chain });
            var links = Links();
            Assert.That(links[0], Does.EndWith("amount=f32(1) map=ref(2)"));
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(2));
            Assert.That(_sink.OfOp("REPARENT").Count(), Is.EqualTo(1));
            Assert.That(IndexOf("CREATE 2 "), Is.LessThan(IndexOf("CHAIN ")));

            // A new link with the same texture element keeps the object.
            Frame(new Rectangle { Effect = new TestShaderEffect(null, Uniform.FromTexture("map", map)) });
            Assert.That(_sink.OfOp("CREATE").Count() + _sink.OfOp("DESTROY").Count(), Is.EqualTo(0));
            Assert.That(Links()[0], Does.EndWith("map=ref(2)"));

            // A different texture element of the same type is updated in place (same id).
            Frame(new Rectangle { Effect = new TestShaderEffect(null, Uniform.FromTexture("map", new Rectangle { Opacity = 0.5f })) });
            Assert.That(_sink.OfOp("CREATE").Count() + _sink.OfOp("DESTROY").Count(), Is.EqualTo(0));
            Assert.That(Links()[0], Does.EndWith("map=ref(2)"));
        }

        [Test]
        public void SameTextureTwice_OneObject()
        {
            var map = new Rectangle();
            var first = new TestShaderEffect(null, Uniform.FromTexture("map", map));
            var chain = new TestShaderEffect(first, Uniform.FromTexture("other", map));
            Frame(new Rectangle { Effect = chain });
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(2));
            var links = Links();
            Assert.That(links[0], Does.EndWith("map=ref(2)"));
            Assert.That(links[1], Does.EndWith("other=ref(2)"));
        }

        [Test]
        public void ImageUniform_SentAsResourceId()
        {
            var chain = new TestShaderEffect(null, Uniform.FromImage("img", null));
            Frame(new Rectangle { Effect = chain });
            Assert.That(Links()[0], Does.EndWith("img=u64(0)"));
        }

        [Test]
        public void Teardown_RemovingElementFreesObjects()
        {
            var chain = Mask(new TestShaderEffect(null, Uniform.FromTexture("map", new Rectangle())), new Text());
            Frame(new Group
            {
                Children = ImmutableArray.Create<Element>(new Rectangle { Effect = chain }),
            });
            Assert.That(_ids.IsLive(3) && _ids.IsLive(4), Is.True);

            Frame(new Group());
            Assert.That(_sink.OfOp("DESTROY").ToArray(), Is.EqualTo(new[] { "DESTROY 2" }), "QtNative removes the objects with the element");
            Assert.That(_ids.IsLive(2) || _ids.IsLive(3) || _ids.IsLive(4), Is.False);
        }

        [Test]
        public void ChainChangesAtRuntime_NoLeak()
        {
            for (int i = 0; i < 5; i++)
            {
                Frame(new Rectangle { Effect = Mask(new TestShaderEffect(null, Uniform.FromTexture("map", new Rectangle())), new Text()) });
                Frame(new Rectangle { Effect = Blur() });
                Frame(new Rectangle());
            }
            // Only the element itself is alive.
            Assert.That(Enumerable.Range(1, 40).Count(id => _ids.IsLive((uint)id)), Is.EqualTo(1));
        }
    }
}
