using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Tests
{
    /// <summary>SET_EFFECT_CHAIN (#489): wire encoding of the writer and <see cref="Effect.Segment"/>.</summary>
    public class EffectChainTests
    {
        sealed class TestShaderLink : ShaderLink
        {
            public TestShaderLink(Effect? previous, ushort template = Protocol.EffectTemplate.Single, string? strengthUniform = null)
                : base(previous, template, ImmutableArray.Create(7UL), null, new RectangleF(1, 2, 3, 4), 10, strengthUniform)
            {
            }
        }

        static string Hex(byte[] b) => Convert.ToHexString(b);
        static string U16(int v) => $"{v & 0xFF:X2}{(v >> 8) & 0xFF:X2}";
        static string U32(uint v) => Hex(BitConverter.GetBytes(v));
        static string U64(ulong v) => Hex(BitConverter.GetBytes(v));
        static string F32(float v) => Hex(BitConverter.GetBytes(v));
        static string Ascii(string s) => Hex(Encoding.ASCII.GetBytes(s));

        static string Write(Action<CommandWriter> write)
        {
            var w = new CommandWriter();
            write(w);
            return Hex(w.Written.ToArray());
        }

        [Test]
        public void EmptyChain()
        {
            var hex = Write(w =>
            {
                w.BeginEffectChain(9);
                w.EndEffectChain();
            });
            Assert.That(hex, Is.EqualTo(U16(Protocol.Op.SetEffectChain) + U16(8) + U32(9) + U16(0) + U16(0)));
        }

        [Test]
        public void MultiAndTwoPassShaderLink()
        {
            var hex = Write(w =>
            {
                w.BeginEffectChain(3);
                w.BeginMultiLink();
                w.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
                w.EffectF32(Protocol.Prop.EffectBlur, 0.5f);
                w.EndLink();
                w.BeginShaderLink(Protocol.EffectTemplate.TwoPass, new ulong[] { 5 }, 1, 2, 3, 4, 100, "length");
                w.UniformF32("angle", 45);
                w.EndLink();
                w.EndEffectChain();
            });
            var multi = U16(Protocol.EffectLink.Multi) + U16(2) + U32(16)
                + U16(Protocol.Prop.EffectBlurEnabled) + U16(Protocol.Vt.Bool) + "01000000"
                + U16(Protocol.Prop.EffectBlur) + U16(Protocol.Vt.F32) + F32(0.5f);
            var shader = U16(Protocol.EffectLink.Shader) + U16(1) + U32(60)
                + U16(Protocol.EffectTemplate.TwoPass) + U16(1) + U64(5)
                + F32(1) + F32(2) + F32(3) + F32(4) + F32(100) + U16(6) + U16(0) + Ascii("length") + "0000"
                + U16(Protocol.Vt.F32) + U16(5) + Ascii("angle") + "000000" + F32(45);
            Assert.That(hex, Is.EqualTo(U16(Protocol.Op.SetEffectChain) + U16(100) + U32(3) + U16(2) + U16(0) + multi + shader));
        }

        [Test]
        public void PyramidLinkWithoutStrengthName()
        {
            var hex = Write(w =>
            {
                w.BeginEffectChain(1);
                w.BeginShaderLink(Protocol.EffectTemplate.Pyramid, new ulong[] { 11, 12 }, 0, 0, 0, 0, 32, default);
                w.EndLink();
                w.EndEffectChain();
            });
            var shader = U16(Protocol.EffectLink.Shader) + U16(0) + U32(44)
                + U16(Protocol.EffectTemplate.Pyramid) + U16(2) + U64(11) + U64(12)
                + F32(0) + F32(0) + F32(0) + F32(0) + F32(32) + U16(0) + U16(0);
            Assert.That(hex, Is.EqualTo(U16(Protocol.Op.SetEffectChain) + U16(60) + U32(1) + U16(1) + U16(0) + shader));
        }

        [Test]
        public void MisuseThrows()
        {
            var w = new CommandWriter();
            Assert.Throws<InvalidOperationException>(() => w.BeginMultiLink());
            w.BeginEffectChain(1);
            Assert.Throws<InvalidOperationException>(() => w.EffectF32(Protocol.Prop.EffectBlur, 1));
            Assert.Throws<InvalidOperationException>(() => w.UniformF32("x", 1));
            w.BeginMultiLink();
            Assert.Throws<InvalidOperationException>(() => w.UniformF32("x", 1));
            Assert.Throws<InvalidOperationException>(() => w.EndEffectChain());
            w.EndLink();
            w.EndEffectChain();
        }

        [Test]
        public void SegmentOfNoChainIsEmpty()
        {
            Assert.That(Effect.Segment(null, out var warnings), Is.Empty);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void SegmentWithoutShaderIsOneRunLikeFold()
        {
            var chain = new ColorAdjustEffect(new BlurEffect(null, 0.3f, 32, 0), 0.1f, 0, 0);
            var links = Effect.Segment(chain, out var warnings);
            Assert.That(links.Length, Is.EqualTo(1));
            Assert.That(links[0].IsShader, Is.False);
            Assert.That(links[0].Multi, Is.EqualTo(Effect.Fold(chain, out _)));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void SegmentKeepsApplicationOrderAndSplitsRuns()
        {
            // vvvv: Blur -> Shader -> DropShadow -> (Effect pin); the start of the chain applies first.
            var blur = new BlurEffect(null, 0.3f, 32, 0);
            var shader = new TestShaderLink(blur);
            var chain = new DropShadowEffect(shader, Color4.Black, new Vector2(2, 2), 1, 1);
            var links = Effect.Segment(chain, out var warnings);
            Assert.That(links.Select(l => l.IsShader), Is.EqualTo(new[] { false, true, false }));
            Assert.That(links[0].Multi.BlurEnabled && !links[0].Multi.ShadowEnabled, Is.True);
            Assert.That(links[1].Shader, Is.SameAs(shader));
            Assert.That(links[2].Multi.ShadowEnabled && !links[2].Multi.BlurEnabled, Is.True);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void SegmentWarnsOncePerRun()
        {
            // Run 1: Blur 0.1, Blur 0.2 (the one nearer to the pin wins), Shader, run 2: Blur 0.3, Blur 0.4.
            Effect chain = new BlurEffect(new BlurEffect(null, 0.1f, 32, 0), 0.2f, 32, 0);
            chain = new TestShaderLink(chain, Protocol.EffectTemplate.TwoPass, "length");
            chain = new BlurEffect(new BlurEffect(chain, 0.3f, 32, 0), 0.4f, 32, 0);
            var links = Effect.Segment(chain, out var warnings);
            Assert.That(links.Length, Is.EqualTo(3));
            Assert.That(links[0].Multi.Blur, Is.EqualTo(0.2f));
            Assert.That(links[1].Shader!.StrengthUniform, Is.EqualTo("length"));
            Assert.That(links[2].Multi.Blur, Is.EqualTo(0.4f));
            Assert.That(warnings.Length, Is.EqualTo(2));
        }

        [Test]
        public void AdjacentShadersHaveNoRunBetween()
        {
            var chain = new TestShaderLink(new TestShaderLink(null));
            var links = Effect.Segment(chain, out _);
            Assert.That(links.Select(l => l.IsShader), Is.EqualTo(new[] { true, true }));
            Assert.That(links[1].Shader, Is.SameAs(chain));
        }
    }
}
