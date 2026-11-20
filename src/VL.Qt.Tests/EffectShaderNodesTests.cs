using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>The shader effect nodes (phase B): template, shaders, uniforms, padding and strength in DIP, change detection.</summary>
    public class EffectShaderNodesTests
    {
        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;

        [SetUp]
        public void CreateClient()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
        }

        private static ShaderLink Link(Effect? e) => (ShaderLink)e!;

        // The nodes describe the effect in units; the reconciler converts (EffectDiff). The expectations are written in DIP.
        private static float Unit(float dip) => SpaceMapping.Default.FromQt(dip);

        private static void AssertPadding(RectangleF actual, float left, float top, float right, float bottom)
        {
            Assert.That(actual.X, Is.EqualTo(Unit(left)).Within(1e-4), "left");
            Assert.That(actual.Y, Is.EqualTo(Unit(top)).Within(1e-4), "top");
            Assert.That(actual.Width, Is.EqualTo(Unit(right)).Within(1e-4), "right");
            Assert.That(actual.Height, Is.EqualTo(Unit(bottom)).Within(1e-4), "bottom");
        }

        [Test]
        public void Glow_IsPyramidWithRadiusAndPaddingInUnits()
        {
            using var node = new GlowNode(_client);
            var first = node.Update(null, new Color4(1, 0, 0, 1), Unit(40), 0.2f, 1.5f, true);
            var link = Link(first);
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Pyramid));
            Assert.That(link.ShaderIds, Has.Length.EqualTo(2));
            Assert.That(link.ShaderIds.All(id => id != 0), Is.True);
            Assert.That(link.Strength, Is.EqualTo(Unit(40f)).Within(1e-4));
            AssertPadding(link.Padding, 60, 60, 60, 60);
            Assert.That(link.Uniforms.Select(u => u.Name), Is.EquivalentTo(new[] { "color", "spread", "intensity", "additive" }));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(2), "two shaders, baked once");

            Assert.That(node.Update(null, new Color4(1, 0, 0, 1), Unit(40), 0.2f, 1.5f, true), Is.SameAs(first), "unchanged inputs: same link");
            Assert.That(node.Core.Created, Is.EqualTo(1));
            Assert.That(node.Update(null, new Color4(1, 0, 0, 1), Unit(41), 0.2f, 1.5f, true), Is.Not.SameAs(first));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(2), "a pin change does not rebake");
        }

        [Test]
        public void Glow_NewLinkWhenTheChainChanges_AndKeepsPrevious()
        {
            using var node = new GlowNode(_client);
            var a = new BlurEffect(null, 0.5f, 32, 0);
            var b = new BlurEffect(null, 0.5f, 32, 0);
            var first = node.Update(a, default, 1, 0, 1, false);
            Assert.That(Link(first).Previous, Is.SameAs(a));
            Assert.That(node.Update(a, default, 1, 0, 1, false), Is.SameAs(first));
            Assert.That(Link(node.Update(b, default, 1, 0, 1, false)).Previous, Is.SameAs(b));
        }

        [Test]
        public void InnerShadow_OffsetIsALengthUniform()
        {
            using var node = new InnerShadowNode(_client);
            var link = Link(node.Update(null, Color4.Black, Unit(10), new Vector2(Unit(8), Unit(4)), 0, 1));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Pyramid));
            Assert.That(link.Strength, Is.EqualTo(Unit(10f)).Within(1e-4));
            AssertPadding(link.Padding, 15, 15, 15, 15);
            var offset = link.Uniforms.Single(u => u.Name == "offsetDip");
            Assert.That(offset.Numbers.M11, Is.EqualTo(Unit(8f)).Within(1e-4));
            Assert.That(offset.Numbers.M12, Is.EqualTo(Unit(4f)).Within(1e-4));
        }

        [Test]
        public void DirectionalBlur_TwoPassWithLengthAndPaddingAlongTheAngle()
        {
            using var node = new DirectionalBlurNode(_client);
            var link = Link(node.Update(null, 0f, Unit(64)));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.TwoPass));
            Assert.That(link.ShaderIds, Has.Length.EqualTo(1));
            Assert.That(link.Uniforms.Select(u => u.Name), Is.EqualTo(new[] { "angle", "blurLength" }));
            Assert.That(link.Uniforms.Single(u => u.Name == "blurLength").Numbers.M11, Is.EqualTo(Unit(64f)).Within(1e-4));
            AssertPadding(link.Padding, 33, 0, 33, 0); // L / 2 + L / 64, horizontal

            var vertical = Link(node.Update(null, 90f, Unit(64)));
            Assert.That(vertical.Padding.X, Is.LessThan(Unit(1f)));
            Assert.That(vertical.Padding.Y, Is.EqualTo(Unit(33f)).Within(1e-4));
        }

        [Test]
        public void ZoomAndRadialBlur_UniformsAndPadding()
        {
            using var zoom = new ZoomBlurNode(_client);
            var z = Link(zoom.Update(null, new Vector2(0.5f, 0.5f), Unit(100)));
            Assert.That(z.Uniforms.Select(u => u.Name), Is.EqualTo(new[] { "center", "blurLength" }));
            Assert.That(z.Uniforms.Single(u => u.Name == "blurLength").Numbers.M11, Is.EqualTo(Unit(100f)).Within(1e-4));
            AssertPadding(z.Padding, 60, 60, 60, 60);

            using var radial = new RadialBlurNode(_client);
            var r = Link(radial.Update(null, new Vector2(0.5f, 0.5f), 12f, Unit(30)));
            Assert.That(r.Uniforms.Single(u => u.Name == "angle").Numbers.M11, Is.EqualTo(12f), "degrees are not converted");
            AssertPadding(r.Padding, 30, 30, 30, 30);
        }

        [Test]
        public void Displace_NeedsAMap_AndSendsItAsTextureUniform()
        {
            using var node = new DisplaceNode(_client);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            Assert.That(node.Update(input, null, null, 0.1f), Is.SameAs(input), "no map: chain unchanged");

            var map = new Rectangle();
            var link = Link(node.Update(input, map, null, Unit(7)));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Single));
            AssertPadding(link.Padding, 7, 7, 7, 7);
            Assert.That(link.Uniforms.Single(u => u.Name == "displacement").Numbers.M11, Is.EqualTo(Unit(7f)).Within(1e-4));
            Assert.That(link.Uniforms.Single(u => u.Name == "map").Texture, Is.SameAs(map));
        }

        [Test]
        public void Blend_NeedsALayer_AndSendsModeOpacityAndLayer()
        {
            using var node = new BlendNode(_client);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            Assert.That(node.Update(input, null, null, BlendMode.Multiply, 1f), Is.SameAs(input), "no layer: chain unchanged");

            var layer = new Rectangle();
            var link = Link(node.Update(input, layer, null, BlendMode.SoftLight, 0.5f));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Single));
            Assert.That(link.Padding, Is.EqualTo(default(RectangleF)));
            Assert.That(link.Uniforms.Single(u => u.Name == "mode").Integer, Is.EqualTo((int)BlendMode.SoftLight));
            Assert.That(link.Uniforms.Single(u => u.Name == "layerOpacity").Numbers.M11, Is.EqualTo(0.5f).Within(1e-3));
            Assert.That(link.Uniforms.Single(u => u.Name == "layer").Texture, Is.SameAs(layer));
        }

        [Test]
        public void WithoutResourceClient_TheChainPassesOn()
        {
            using var node = new GlowNode((ResourceClient?)null);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            Assert.That(node.Update(input, default, 1, 0, 1, false), Is.SameAs(input));
        }

        [Test]
        public void Dispose_ReleasesTheShaders()
        {
            var node = new GlowNode(_client);
            node.Update(null, default, 1, 0, 1, false);
            node.Dispose();
            Assert.That(_api.Released, Has.Count.EqualTo(2));
        }
    }
}
