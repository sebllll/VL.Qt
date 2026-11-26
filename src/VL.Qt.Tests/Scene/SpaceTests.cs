using NUnit.Framework;

namespace VL.Qt.Tests.Scene
{
    public class SpaceTests
    {
        [Test]
        public void Space_MirrorsSkiaCommonSpace()
        {
            // Source: VL.Skia.CommonSpace (VL.Core.Skia/Enums.cs); order = numeric value.
            Assert.That(System.Enum.GetNames<Space>(),
                Is.EqualTo(new[] { "Normalized", "DIP", "DIPTopLeft", "PixelTopLeft" }));
        }
    }
}
