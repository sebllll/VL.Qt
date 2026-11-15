using NUnit.Framework;

namespace VL.Qt.Tests
{
    /// <summary>MultisampleCount and the clamp warning of the Multisample Count pins (#118).</summary>
    public class MultisampleTests
    {
        [Test]
        public void Enum_ValueIsTheSampleCount()
        {
            Assert.That((int)MultisampleCount.None, Is.EqualTo(1));
            Assert.That((int)MultisampleCount.X2, Is.EqualTo(2));
            Assert.That((int)MultisampleCount.X4, Is.EqualTo(4));
            Assert.That((int)MultisampleCount.X8, Is.EqualTo(8));
        }

        [TestCase(MultisampleCount.None, 1)]
        [TestCase(MultisampleCount.X4, 4)]
        [TestCase(MultisampleCount.X8, 8)]
        public void ClampWarning_MetRequestHasNone(MultisampleCount requested, int used)
            => Assert.That(Multisample.ClampWarning(requested, used), Is.Null);

        [Test]
        public void ClampWarning_LoweredCountNamesBoth()
        {
            var text = Multisample.ClampWarning(MultisampleCount.X8, 4);
            Assert.That(text, Does.Contain("8x").And.Contain("4x"));
            Assert.That(Multisample.ClampWarning(MultisampleCount.X2, 1), Is.Not.Null);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void ClampWarning_UnknownCountHasNone(int used)
            => Assert.That(Multisample.ClampWarning(MultisampleCount.X8, used), Is.Null);
    }
}
