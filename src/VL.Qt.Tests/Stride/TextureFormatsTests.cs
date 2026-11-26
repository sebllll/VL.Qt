using NUnit.Framework;
using Stride.Graphics;
using VL.Qt.Stride;

namespace VL.Qt.Tests.Stride
{
    /// <summary>TextureFormats of QtTexture, compiled in from VL.Qt.Stride: which formats are allowed and what the resource uses.</summary>
    public class TextureFormatsTests
    {
        [TestCase(PixelFormat.R8G8B8A8_UNorm, PixelFormat.R8G8B8A8_UNorm)]
        [TestCase(PixelFormat.B8G8R8A8_UNorm, PixelFormat.B8G8R8A8_UNorm)]
        [TestCase(PixelFormat.R16G16B16A16_Float, PixelFormat.R16G16B16A16_Float)]
        // SRgb 8-bit textures are typeless resources: QtNative writes them through an SRgb view.
        [TestCase(PixelFormat.R8G8B8A8_UNorm_SRgb, PixelFormat.R8G8B8A8_Typeless)]
        [TestCase(PixelFormat.B8G8R8A8_UNorm_SRgb, PixelFormat.B8G8R8A8_Typeless)]
        public void AllowedFormatsGiveTheResourceFormat(PixelFormat format, PixelFormat resource)
        {
            Assert.That(TextureFormats.TryGetTextureFormat(format, out var actual), Is.True);
            Assert.That(actual, Is.EqualTo(resource));
        }

        [TestCase(PixelFormat.None)]
        [TestCase(PixelFormat.R8G8B8A8_Typeless)]
        [TestCase(PixelFormat.R32G32B32A32_Float)]
        [TestCase(PixelFormat.R10G10B10A2_UNorm)]
        [TestCase(PixelFormat.BC1_UNorm)]
        public void OtherFormatsAreRefused(PixelFormat format)
        {
            Assert.That(TextureFormats.TryGetTextureFormat(format, out var actual), Is.False);
            Assert.That(actual, Is.EqualTo(PixelFormat.None));
        }

        [Test]
        public void ErrorTextNamesTheListAndTheFormatGiven()
        {
            var text = TextureFormats.FormatErrorFor(PixelFormat.R32G32B32A32_Float);
            Assert.That(text, Does.StartWith(TextureFormats.FormatError));
            Assert.That(text, Does.EndWith("(got R32G32B32A32_Float)"));
        }
    }
}
