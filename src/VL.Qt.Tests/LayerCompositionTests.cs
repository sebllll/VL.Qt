using NUnit.Framework;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Qt.Stride;

namespace VL.Qt.Tests
{
    /// <summary>LayerComposition of QtRenderer (#120), compiled in from VL.Qt.Stride: format decision and input mapping.</summary>
    public class LayerCompositionTests
    {
        [TestCase(PixelFormat.B8G8R8A8_UNorm, PixelFormat.B8G8R8A8_UNorm_SRgb, PixelFormat.B8G8R8A8_UNorm)]   // flip-model back buffer
        [TestCase(PixelFormat.B8G8R8A8_Typeless, PixelFormat.B8G8R8A8_UNorm_SRgb, PixelFormat.B8G8R8A8_UNorm)]
        [TestCase(PixelFormat.R8G8B8A8_Typeless, PixelFormat.R8G8B8A8_UNorm_SRgb, PixelFormat.R8G8B8A8_UNorm)] // typeless render texture
        [TestCase(PixelFormat.R8G8B8A8_UNorm, PixelFormat.R8G8B8A8_UNorm, PixelFormat.R8G8B8A8_UNorm)]
        public void UNormOrTypelessResourceIsDirect(PixelFormat resource, PixelFormat view, PixelFormat expected)
        {
            var c = LayerComposition.Choose(resource, view);
            Assert.That(c.Direct, Is.True);
            Assert.That(c.CoreFormat, Is.EqualTo(expected));
            Assert.That(c.ViewFormat, Is.EqualTo(expected));
            Assert.That(c.Hint(resource), Is.Empty);
        }

        [TestCase(PixelFormat.R8G8B8A8_UNorm_SRgb, PixelFormat.R8G8B8A8_UNorm_SRgb, PixelFormat.R8G8B8A8_UNorm_SRgb)]
        [TestCase(PixelFormat.B8G8R8A8_UNorm_SRgb, PixelFormat.B8G8R8A8_UNorm_SRgb, PixelFormat.B8G8R8A8_UNorm_SRgb)]
        [TestCase(PixelFormat.R16G16B16A16_Float, PixelFormat.R16G16B16A16_Float, PixelFormat.R16G16B16A16_Float)]
        [TestCase(PixelFormat.R10G10B10A2_UNorm, PixelFormat.R10G10B10A2_UNorm, PixelFormat.R16G16B16A16_Float)]
        [TestCase(PixelFormat.R32G32B32A32_Float, PixelFormat.R32G32B32A32_Float, PixelFormat.R16G16B16A16_Float)]
        public void OtherResourcesFallBackToTheViewFormat(PixelFormat resource, PixelFormat view, PixelFormat core)
        {
            var c = LayerComposition.Choose(resource, view);
            Assert.That(c.Direct, Is.False);
            Assert.That(c.CoreFormat, Is.EqualTo(core));
            Assert.That(c.ViewFormat, Is.EqualTo(view));
            Assert.That(TextureFormats.TryGetTextureFormat(c.CoreFormat, out _), Is.True);
            Assert.That(c.Hint(resource), Does.Contain(resource.ToString()));
        }

        [Test]
        public void FallbackKeepsSRgbForOtherSRgbViews()
        {
            var c = LayerComposition.Fallback(PixelFormat.BC1_UNorm_SRgb);
            Assert.That(c.CoreFormat, Is.EqualTo(PixelFormat.B8G8R8A8_UNorm_SRgb));
        }

        [TestCase(1280f, 720f, 1280, 720)]
        [TestCase(639.6f, 359.4f, 640, 359)]
        [TestCase(0f, 0f, 1, 1)]
        public void TextureSizeIsTheRoundedViewport(float w, float h, int x, int y)
            => Assert.That(LayerComposition.TextureSize(w, h), Is.EqualTo(new Int2(x, y)));

        [Test]
        public void PositionsAreRelativeToTheViewport()
        {
            Assert.That(LayerComposition.ToTexturePixels(new Vector2(110, 60), new Vector2(100, 50)), Is.EqualTo(new Vector2(10, 10)));
            Assert.That(LayerComposition.ToTexturePixels(new Vector2(10, 10), Vector2.Zero), Is.EqualTo(new Vector2(10, 10)));
        }
    }
}
