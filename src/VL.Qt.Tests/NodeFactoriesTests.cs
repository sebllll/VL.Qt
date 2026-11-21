using NUnit.Framework;
using VL.Qt.NodeFactories;

namespace VL.Qt.Tests
{
    /// <summary>The layer shared by the shader and the QML node factory: node names and pin names. No QtNative, no vvvv.</summary>
    public class NodeFactoriesTests
    {
        [TestCase("Glitch", "Glitch", null)]
        [TestCase("Glitch_RGB", "Glitch", "RGB")]
        [TestCase("Gauge_Round", "Gauge", "Round")]
        [TestCase("Levels_Clamp_Both", "Levels", "Clamp Both")]
        [TestCase("Foo_", "Foo", null)]
        [TestCase("_Foo", "_Foo", null)]
        public void FileNodeNames_SplitsVariantLikeStride(string fileName, string name, string? version)
        {
            Assert.That(FileNodeNames.Split(fileName), Is.EqualTo((name, version)));
        }

        [Test]
        public void FileNodeNames_FullName_HasVariantInParentheses()
        {
            Assert.That(FileNodeNames.FullName("Glitch", "RGB"), Is.EqualTo("Glitch (RGB)"));
            Assert.That(FileNodeNames.FullName("Glitch", null), Is.EqualTo("Glitch"));
        }

        [Test]
        public void PinNames_SplitCamelCase()
        {
            Assert.That(PinNames.FromIdentifier("pixelSize"), Is.EqualTo("Pixel Size"));
            Assert.That(PinNames.FromIdentifier("amount"), Is.EqualTo("Amount"));
            Assert.That(PinNames.FromIdentifier("uvOffset2"), Is.EqualTo("Uv Offset2"));
            Assert.That(PinNames.FromIdentifier("RGBShift"), Is.EqualTo("RGB Shift"));
            Assert.That(PinNames.FromIdentifier("tint_color"), Is.EqualTo("Tint Color"));
        }
    }
}
