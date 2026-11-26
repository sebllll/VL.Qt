using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Wire;
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

        private static ShaderEffect Link(Effect? e) => (ShaderEffect)e!;

        private static void AssertPadding(RectangleF actual, float left, float top, float right, float bottom)
        {
            Assert.That(actual.X, Is.EqualTo(left).Within(1e-4), "left");
            Assert.That(actual.Y, Is.EqualTo(top).Within(1e-4), "top");
            Assert.That(actual.Width, Is.EqualTo(right).Within(1e-4), "right");
            Assert.That(actual.Height, Is.EqualTo(bottom).Within(1e-4), "bottom");
        }

        [Test]
        public void Glow_IsPyramidWithRadiusAndPaddingInDip()
        {
            using var node = new GlowNode(_client);
            var first = node.Update(null, new Color4(1, 0, 0, 1), 40, 0.2f, 1.5f, true, true);
            var link = Link(first);
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Pyramid));
            Assert.That(link.ShaderIds, Has.Length.EqualTo(2));
            Assert.That(link.ShaderIds.All(id => id != 0), Is.True);
            Assert.That(link.Strength, Is.EqualTo(40f).Within(1e-4));
            AssertPadding(link.Padding, 60, 60, 60, 60);
            Assert.That(link.Uniforms.Select(u => u.Name), Is.EquivalentTo(new[] { "color", "spread", "intensity", "additive" }));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(2), "two shaders, baked once");

            Assert.That(node.Update(null, new Color4(1, 0, 0, 1), 40, 0.2f, 1.5f, true, true), Is.SameAs(first), "unchanged inputs: same link");
            Assert.That(node.Update(null, new Color4(1, 0, 0, 1), 41, 0.2f, 1.5f, true, true), Is.Not.SameAs(first));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(2), "a pin change does not rebake");
        }

        [Test]
        public void Glow_NewLinkWhenTheChainChanges_AndKeepsPrevious()
        {
            using var node = new GlowNode(_client);
            var a = new BlurEffect(null, 0.5f, 32, 0);
            var b = new BlurEffect(null, 0.5f, 32, 0);
            var first = node.Update(a, default, 1, 0, 1, false, true);
            Assert.That(Link(first).Previous, Is.SameAs(a));
            Assert.That(node.Update(a, default, 1, 0, 1, false, true), Is.SameAs(first));
            Assert.That(Link(node.Update(b, default, 1, 0, 1, false, true)).Previous, Is.SameAs(b));
        }

        [Test]
        public void InnerShadow_OffsetIsALengthUniform()
        {
            using var node = new InnerShadowNode(_client);
            var link = Link(node.Update(null, Color4.Black, 10, new Vector2(8, 4), 0, 1, true));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Pyramid));
            Assert.That(link.Strength, Is.EqualTo(10f).Within(1e-4));
            AssertPadding(link.Padding, 15, 15, 15, 15);
            var offset = link.Uniforms.Single(u => u.Name == "offsetDip");
            Assert.That(offset.Numbers.M11, Is.EqualTo(8f).Within(1e-4));
            Assert.That(offset.Numbers.M12, Is.EqualTo(4f).Within(1e-4));
        }

        [Test]
        public void DirectionalBlur_TwoPassWithLengthAndPaddingAlongTheAngle()
        {
            using var node = new DirectionalBlurNode(_client);
            var link = Link(node.Update(null, 0f, 64, true));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.TwoPass));
            Assert.That(link.ShaderIds, Has.Length.EqualTo(1));
            Assert.That(link.Uniforms.Select(u => u.Name), Is.EqualTo(new[] { "angle", "blurLength" }));
            Assert.That(link.Uniforms.Single(u => u.Name == "blurLength").Numbers.M11, Is.EqualTo(64f).Within(1e-4));
            AssertPadding(link.Padding, 33, 0, 33, 0); // L / 2 + L / 64, horizontal

            var vertical = Link(node.Update(null, 0.25f, 64, true));
            Assert.That(vertical.Padding.X, Is.LessThan(1f));
            Assert.That(vertical.Padding.Y, Is.EqualTo(33f).Within(1e-4));
        }

        [Test]
        public void ZoomAndRadialBlur_UniformsAndPadding()
        {
            using var zoom = new ZoomBlurNode(_client);
            var z = Link(zoom.Update(null, new Vector2(0.5f, 0.5f), 100, true));
            Assert.That(z.Uniforms.Select(u => u.Name), Is.EqualTo(new[] { "center", "blurLength" }));
            Assert.That(z.Uniforms.Single(u => u.Name == "blurLength").Numbers.M11, Is.EqualTo(100f).Within(1e-4));
            AssertPadding(z.Padding, 60, 60, 60, 60);

            using var radial = new RadialBlurNode(_client);
            var r = Link(radial.Update(null, new Vector2(0.5f, 0.5f), 0.03f, 30, true));
            Assert.That(r.Uniforms.Single(u => u.Name == "angle").Numbers.M11, Is.EqualTo(0.03f), "cycles are passed on as they are");
            AssertPadding(r.Padding, 30, 30, 30, 30);
        }

        [Test]
        public void Displace_NeedsAMap_AndSendsItAsTextureUniform()
        {
            using var node = new DisplaceNode(_client);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            Assert.That(node.Update(input, null, null, 0.1f, true), Is.SameAs(input), "no map: chain unchanged");

            var map = new Rectangle();
            var link = Link(node.Update(input, map, null, 7, true));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Single));
            AssertPadding(link.Padding, 7, 7, 7, 7);
            Assert.That(link.Uniforms.Single(u => u.Name == "displacement").Numbers.M11, Is.EqualTo(7f).Within(1e-4));
            Assert.That(link.Uniforms.Single(u => u.Name == "map").Texture, Is.SameAs(map));
        }

        [Test]
        public void Blend_NeedsALayer_AndSendsModeOpacityAndLayer()
        {
            using var node = new BlendNode(_client);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            Assert.That(node.Update(input, null, null, BlendMode.Multiply, 1f, true), Is.SameAs(input), "no layer: chain unchanged");

            var layer = new Rectangle();
            var link = Link(node.Update(input, layer, null, BlendMode.SoftLight, 0.5f, true));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Single));
            Assert.That(link.Padding, Is.EqualTo(default(RectangleF)));
            Assert.That(link.Uniforms.Single(u => u.Name == "mode").Integer, Is.EqualTo((int)BlendMode.SoftLight));
            Assert.That(link.Uniforms.Single(u => u.Name == "layerOpacity").Numbers.M11, Is.EqualTo(0.5f).Within(1e-3));
            Assert.That(link.Uniforms.Single(u => u.Name == "layer").Texture, Is.SameAs(layer));
        }

        // Apply off: the chain at Input passes on unchanged (E16), for every effect node.
        [Test]
        public void ApplyOff_EveryEffectNodePassesTheChainOn()
        {
            var input = new BlurEffect(null, 0.5f, 32, 0);
            var layer = new Rectangle();
            using var glow = new GlowNode(_client);
            using var innerShadow = new InnerShadowNode(_client);
            using var directional = new DirectionalBlurNode(_client);
            using var zoom = new ZoomBlurNode(_client);
            using var radial = new RadialBlurNode(_client);
            using var displace = new DisplaceNode(_client);
            using var blend = new BlendNode(_client);
            using var mask = new MaskNode();
            Assert.Multiple(() =>
            {
                Assert.That(new BlurNode().Update(input, 0.5f, 32, 0, true, default, false), Is.SameAs(input), "Blur");
                Assert.That(new DropShadowNode().Update(input, default, default, 1, 1, true, default, false), Is.SameAs(input), "DropShadow");
                Assert.That(new ColorAdjustNode().Update(input, 0, 0, 0, false), Is.SameAs(input), "ColorAdjust");
                Assert.That(new ColorizeNode().Update(input, default, 1, false), Is.SameAs(input), "Colorize");
                Assert.That(mask.Update(input, layer, 0, 0, 1, 0, false, false), Is.SameAs(input), "Mask");
                Assert.That(glow.Update(input, default, 20, 0, 1, false, false), Is.SameAs(input), "Glow");
                Assert.That(innerShadow.Update(input, default, 10, default, 0, 1, false), Is.SameAs(input), "InnerShadow");
                Assert.That(directional.Update(input, 0, 10, false), Is.SameAs(input), "DirectionalBlur");
                Assert.That(zoom.Update(input, default, 40, false), Is.SameAs(input), "ZoomBlur");
                Assert.That(radial.Update(input, default, 0.05f, 30, false), Is.SameAs(input), "RadialBlur");
                Assert.That(displace.Update(input, layer, null, 10, false), Is.SameAs(input), "Displace");
                Assert.That(blend.Update(input, layer, null, BlendMode.Multiply, 1, false), Is.SameAs(input), "Blend");
            });
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.Zero, "an effect that is off bakes no shader");
        }

        [Test]
        public void ApplyToggled_ReturnsTheLinkAgainWithoutRebuilding()
        {
            using var node = new GlowNode(_client);
            var on = node.Update(null, default, 20, 0, 1, false, true);
            Assert.That(node.Update(null, default, 20, 0, 1, false, false), Is.Null);
            Assert.That(node.Update(null, default, 20, 0, 1, false, true), Is.SameAs(on));
        }

        // ColorAdjust Saturation is the scale of HSCB (1 = unchanged, 0 = grey); Qt's MultiEffect gets value - 1 (E6, E7).
        [Test]
        public void ColorAdjust_SaturationIsOnTheScaleOfHscb()
        {
            var node = new ColorAdjustNode();
            Assert.That(((ColorAdjustEffect)node.Update(null, 0, 0, 1, true)!).Saturation, Is.EqualTo(0f), "1 = unchanged");
            Assert.That(((ColorAdjustEffect)node.Update(null, 0, 0, 0, true)!).Saturation, Is.EqualTo(-1f), "0 = grey");
            Assert.That(((ColorAdjustEffect)node.Update(null, 0, 0, 2, true)!).Saturation, Is.EqualTo(1f), "2 = twice as colorful");
        }

        [Test]
        public void WithoutResourceClient_TheChainPassesOn()
        {
            using var node = new GlowNode((ResourceClient?)null);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            Assert.That(node.Update(input, default, 1, 0, 1, false, true), Is.SameAs(input));
        }

        [Test]
        public void Dispose_ReleasesTheShaders()
        {
            var node = new GlowNode(_client);
            node.Update(null, default, 1, 0, 1, false, true);
            node.Dispose();
            Assert.That(_api.Released, Has.Count.EqualTo(2));
        }
    }
}
