using NUnit.Framework;
using VL.Qt.NodeFactories;

namespace VL.Qt.Tests.NodeFactories
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
        public void LastGood_FailedVersion_GivesTheLastGoodOne_UntilAGoodOneComes()
        {
            var lastGood = new LastGood<string>();
            Assert.That(lastGood.Resolve("broken 0", failed: true, static (good, failed) => good + "+" + failed), Is.EqualTo("broken 0"), "no good version yet");
            Assert.That(lastGood.Resolve("good 1", failed: false, static (good, failed) => good + "+" + failed), Is.EqualTo("good 1"));
            Assert.That(lastGood.Resolve("broken 2", failed: true, static (good, failed) => good + "+" + failed), Is.EqualTo("good 1+broken 2"));
            Assert.That(lastGood.Resolve("broken 3", failed: true, static (good, failed) => good + "+" + failed), Is.EqualTo("good 1+broken 3"), "a failed version never replaces the good one");
            Assert.That(lastGood.Resolve("good 4", failed: false, static (good, failed) => good + "+" + failed), Is.EqualTo("good 4"));
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

        [Test]
        public void PinNames_SplitDigits_ForShaderFiles()
        {
            Assert.That(PinNames.FromIdentifier("pass1", splitDigits: true), Is.EqualTo("Pass 1"));
            Assert.That(PinNames.FromIdentifier("uvOffset2", splitDigits: true), Is.EqualTo("Uv Offset 2"));
            Assert.That(PinNames.FromIdentifier("RGB2", splitDigits: true), Is.EqualTo("RGB 2"));
            Assert.That(PinNames.FromIdentifier("tint_2", splitDigits: true), Is.EqualTo("Tint 2"));
            Assert.That(PinNames.FromIdentifier("amount", splitDigits: true), Is.EqualTo("Amount"));
        }
    }
}
