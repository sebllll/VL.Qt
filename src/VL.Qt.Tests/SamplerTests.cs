using System;
using System.Linq;
using NUnit.Framework;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Nodes;

namespace VL.Qt.Tests
{
    // Sampler state (#492): wire packing, SET_UNIFORMS entry, diff and removal by the UniformWriter.
    [TestFixture]
    public class SamplerTests
    {
        [Test]
        public void Pack_DefaultIsAllZero_AndFieldsLandInTheirBytes()
        {
            Assert.That(new Sampler().Pack(), Is.EqualTo(0UL), "Linear, None, Clamp, Clamp = Qt defaults = zero");
            var s = new Sampler
            {
                Filter = SamplerFilter.Point,
                MipFilter = SamplerMipFilter.Linear,
                AddressU = SamplerAddress.Wrap,
                AddressV = SamplerAddress.Mirror,
            };
            Assert.That(s.Pack(), Is.EqualTo(0x02_01_02_01UL));
        }

        [Test]
        public void SamplerNode_BuildsRecordFromPins()
        {
            var s = UniformNodes.Sampler(SamplerFilter.Point, SamplerMipFilter.Point, SamplerAddress.Mirror, SamplerAddress.Wrap);
            Assert.That(s, Is.EqualTo(new Sampler
            {
                Filter = SamplerFilter.Point,
                MipFilter = SamplerMipFilter.Point,
                AddressU = SamplerAddress.Mirror,
                AddressV = SamplerAddress.Wrap,
            }));
        }

        [Test]
        public void CommandWriter_SamplerEntry_Is8BytesLittleEndian()
        {
            var w = new CommandWriter();
            w.BeginUniforms(1, 0);
            w.UniformSampler("tex", 0x0201UL);
            w.EndUniforms();
            var hex = Convert.ToHexString(w.Written);
            Assert.That(hex, Does.EndWith("0102000000000000"));
            Assert.That(hex, Does.Contain(Convert.ToHexString(BitConverter.GetBytes(Protocol.Vt.Sampler))));
        }

        [Test]
        public void UniformWriter_SamplerNextToTexture_DiffAndRemove()
        {
            var sink = new RecordingSink();
            var effect = new ShaderEffect
            {
                InputSampler = new Sampler { Filter = SamplerFilter.Point },
                Uniforms = new[] { Uniform.FromImage("lut", null, new Sampler { AddressU = SamplerAddress.Wrap }) }.ToSpread(),
            };
            var sent = UniformWriter.Write(sink, 5, effect, Array.Empty<Node>(), null);
            var first = sink.Commands.Single(c => c.StartsWith("UNIFORMS", StringComparison.Ordinal));
            Assert.That(first, Does.Contain("lut=u64(0)"));
            Assert.That(first, Does.Contain("lut=sampler(10000)"));
            Assert.That(first, Does.Contain("source=sampler(1)"));

            sink.Clear();
            Assert.That(UniformWriter.Write(sink, 5, effect, Array.Empty<Node>(), sent), Is.SameAs(sent));
            Assert.That(sink.Count, Is.EqualTo(0), "unchanged: nothing sent");

            var changed = new ShaderEffect
            {
                InputSampler = new Sampler { Filter = SamplerFilter.Point },
                Uniforms = new[] { Uniform.FromImage("lut", null, new Sampler { AddressU = SamplerAddress.Mirror }) }.ToSpread(),
            };
            sent = UniformWriter.Write(sink, 5, changed, Array.Empty<Node>(), sent);
            var diff = sink.Commands.Single(c => c.StartsWith("UNIFORMS", StringComparison.Ordinal));
            Assert.That(diff, Does.Contain("lut=sampler(20000)"));
            Assert.That(diff, Does.Not.Contain("source="), "only the changed sampler");

            sink.Clear();
            var removed = new ShaderEffect { Uniforms = new[] { Uniform.FromImage("lut", null) }.ToSpread() };
            UniformWriter.Write(sink, 5, removed, Array.Empty<Node>(), sent);
            var clear = sink.Commands.Single(c => c.StartsWith("UNIFORMS", StringComparison.Ordinal));
            Assert.That(clear, Does.StartWith("UNIFORMS 5 1"), "a removed sampler needs the full table with CLEAR");
            Assert.That(clear, Does.Not.Contain("sampler("));
        }

        [Test]
        public void SamplerOnlyUniform_SendsNoTextureEntry()
        {
            var sink = new RecordingSink();
            var effect = new ShaderEffect { Uniforms = new[] { Uniform.FromSampler("lut", new Sampler()) }.ToSpread() };
            UniformWriter.Write(sink, 5, effect, Array.Empty<Node>(), null);
            var cmd = sink.Commands.Single(c => c.StartsWith("UNIFORMS", StringComparison.Ordinal));
            Assert.That(cmd, Does.Contain("lut=sampler(0)"));
            Assert.That(cmd, Does.Not.Contain("u64"));
            Assert.That(cmd, Does.Not.Contain("ref"));
        }
    }
}
