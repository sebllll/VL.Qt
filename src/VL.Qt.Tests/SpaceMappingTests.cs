using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;

namespace VL.Qt.Tests
{
    /// <summary>SpaceMapping (#143): scale and origin mode per Space, conversions to Qt logical pixels and back.</summary>
    public class SpaceMappingTests
    {
        private const float Eps = 1e-3f;

        private static SpaceMapping Make(Space space, float height = 600, float dpr = 1f) => new(space, new Vector2(800, height), dpr);

        [TestCase(Space.Normalized, 600f, 1f, 300f)]
        [TestCase(Space.Normalized, 300f, 1.5f, 150f)]   // height -1 … 1 in DIP, independent of the dpr
        [TestCase(Space.Normalized, 0f, 1f, 0.5f)]       // no height yet: at least 1 DIP
        [TestCase(Space.DIP, 600f, 1f, 100f)]
        [TestCase(Space.DIP, 600f, 1.5f, 100f)]
        [TestCase(Space.DIPTopLeft, 600f, 1f, 100f)]
        [TestCase(Space.DIPTopLeft, 123f, 2f, 100f)]
        [TestCase(Space.PixelTopLeft, 600f, 1f, 100f)]
        [TestCase(Space.PixelTopLeft, 600f, 1.5f, 66.666664f)] // 100 device pixels
        [TestCase(Space.PixelTopLeft, 600f, 2f, 50f)]
        public void UnitScale_PerSpace(Space space, float height, float dpr, float expected)
            => Assert.That(Make(space, height, dpr).UnitScale, Is.EqualTo(expected).Within(Eps));

        [TestCase(0f)]
        [TestCase(-2f)]
        [TestCase(float.NaN)]
        public void DevicePixelRatio_NotPositive_IsOne(float dpr)
        {
            var m = Make(Space.PixelTopLeft, dpr: dpr);
            Assert.That(m.DevicePixelRatio, Is.EqualTo(1f));
            Assert.That(m.UnitScale, Is.EqualTo(100f));
        }

        [TestCase(Space.Normalized, true)]
        [TestCase(Space.DIP, true)]
        [TestCase(Space.DIPTopLeft, false)]
        [TestCase(Space.PixelTopLeft, false)]
        public void IsCentered_OriginAtCentreOrTopLeft(Space space, bool centered)
            => Assert.That(Make(space).IsCentered, Is.EqualTo(centered));

        [Test]
        public void Default_IsDipTopLeft()
        {
            Assert.That(SpaceMapping.Default.Space, Is.EqualTo(Space.DIPTopLeft));
            Assert.That(SpaceMapping.Default.UnitScale, Is.EqualTo(100f));
            Assert.That(SpaceMapping.Default.IsCentered, Is.False);
        }

        [TestCase(Space.Normalized, 300f)]
        [TestCase(Space.DIP, 100f)]
        [TestCase(Space.PixelTopLeft, 100f / 1.5f)]
        public void ToQtFromQt_Length_Vector_Rectangle_RoundTrip(Space space, float k)
        {
            var m = Make(space, 600, 1.5f);
            Assert.That(m.ToQt(2f), Is.EqualTo(2 * k).Within(Eps));
            Assert.That(m.ToQt(new Vector2(1, -0.5f)), Is.EqualTo(new Vector2(k, -0.5f * k)));
            Assert.That(m.ToQt(new RectangleF(0.5f, 0.25f, 2f, 1f)), Is.EqualTo(new RectangleF(0.5f * k, 0.25f * k, 2 * k, k)));
            Assert.That(m.FromQt(m.ToQt(1.25f)), Is.EqualTo(1.25f).Within(Eps));
            Assert.That(m.FromQt(m.ToQt(new Vector2(1.25f, -3f))), Is.EqualTo(new Vector2(1.25f, -3f)));
            var r = new RectangleF(1f, 2f, 3f, 4f);
            var back = m.FromQt(m.ToQt(r));
            Assert.That(back.X, Is.EqualTo(r.X).Within(Eps));
            Assert.That(back.Y, Is.EqualTo(r.Y).Within(Eps));
            Assert.That(back.Width, Is.EqualTo(r.Width).Within(Eps));
            Assert.That(back.Height, Is.EqualTo(r.Height).Within(Eps));
        }

        [Test]
        public void Normalized_FollowsSceneHeight()
        {
            // Window resize: the unit grows and shrinks with the height; 1 unit is half of it.
            Assert.That(Make(Space.Normalized, 600).ToQt(1f), Is.EqualTo(300f));
            Assert.That(Make(Space.Normalized, 300).ToQt(1f), Is.EqualTo(150f));
            Assert.That(Make(Space.Normalized, 300).FromQt(300f), Is.EqualTo(2f));
        }

        [TestCase(Space.Normalized, 300f)]
        [TestCase(Space.DIP, 100f)]
        [TestCase(Space.DIPTopLeft, 100f)]
        [TestCase(Space.PixelTopLeft, 50f)]
        public void Matrix_TranslationScaled_LinearPartKept_PerspectiveDivided(Space space, float k)
        {
            var m = Make(space, 600, 2f);
            var src = Matrix.RotationZ(0.4f) * Matrix.Scaling(2f, 3f, 1f) * Matrix.Translation(0.5f, -0.25f, 0.1f);
            src.M14 = 0.01f; src.M24 = 0.02f; src.M34 = 0.03f;

            var q = m.ToQt(src);

            Assert.That(q.M41, Is.EqualTo(src.M41 * k).Within(Eps));
            Assert.That(q.M42, Is.EqualTo(src.M42 * k).Within(Eps));
            Assert.That(q.M43, Is.EqualTo(src.M43 * k).Within(Eps));
            Assert.That(q.M14, Is.EqualTo(0.01f / k).Within(1e-6f));
            Assert.That(q.M24, Is.EqualTo(0.02f / k).Within(1e-6f));
            Assert.That(q.M34, Is.EqualTo(0.03f / k).Within(1e-6f));
            foreach (var (a, b) in new[] { (src.M11, q.M11), (src.M12, q.M12), (src.M21, q.M21), (src.M22, q.M22), (src.M33, q.M33), (src.M44, q.M44) })
                Assert.That(b, Is.EqualTo(a).Within(1e-6f));

            // The same as S⁻¹ · m · S with S = scale(k, k, k): a point in DIP goes through the transform in units.
            var expected = Matrix.Scaling(1f / k) * src * Matrix.Scaling(k);
            var qa = q.ToArray();
            var ea = expected.ToArray();
            for (int i = 0; i < 16; i++)
                Assert.That(qa[i], Is.EqualTo(ea[i]).Within(1e-4f), $"element {i}");
        }

        [Test]
        public void ToQtTransform_AnchorAndTranslation_InScaledUnits()
        {
            // Normalized, height 600 (300 DIP per unit), element 2 x 1 units, anchor Center = (300, 150) DIP, moved by (0.5, 0).
            var m = Make(Space.Normalized, 600);
            var q = m.ToQtTransform(Matrix.Translation(0.5f, 0f, 0f), new Vector2(2, 1), RectangleAnchor.Center);
            var anchorPoint = Vector3.TransformCoordinate(new Vector3(300, 150, 0), q);
            Assert.That(anchorPoint.X, Is.EqualTo(150f).Within(Eps));
            Assert.That(anchorPoint.Y, Is.EqualTo(0f).Within(Eps));
            var topLeft = Vector3.TransformCoordinate(Vector3.Zero, q);
            Assert.That(topLeft.X, Is.EqualTo(-150f).Within(Eps));
            Assert.That(topLeft.Y, Is.EqualTo(-150f).Within(Eps));
        }

        [TestCase(Space.Normalized, 0.1f, 30f)]       // 0.1 units = 30 DIP
        [TestCase(Space.Normalized, 0.0001f, 1f)]     // at least one pixel
        [TestCase(Space.DIP, 0.126f, 13f)]            // 12.6 DIP, whole pixels (QFont pixel sizes are integers)
        [TestCase(Space.DIP, 0.124f, 12f)]
        [TestCase(Space.DIPTopLeft, 0.12f, 12f)]
        [TestCase(Space.PixelTopLeft, 0.24f, 16f)]    // 24 device pixels at dpr 1.5 = 16 DIP
        [TestCase(Space.DIP, -1f, 1f)]
        public void ToFontPixels_WholeLogicalPixels(Space space, float size, float expected)
            => Assert.That(Make(space, 600, 1.5f).ToFontPixels(size), Is.EqualTo(expected));

        [Test]
        public void Equality_SpaceSizeAndDpr()
        {
            Assert.That(Make(Space.DIP), Is.EqualTo(Make(Space.DIP)));
            Assert.That(Make(Space.DIP) == Make(Space.DIP), Is.True);
            Assert.That(Make(Space.DIP), Is.Not.EqualTo(Make(Space.DIPTopLeft)));
            Assert.That(Make(Space.Normalized, 600), Is.Not.EqualTo(Make(Space.Normalized, 300)));
            Assert.That(Make(Space.DIP, 600, 1f), Is.Not.EqualTo(Make(Space.DIP, 600, 1.5f)));
            Assert.That(Make(Space.DIP).GetHashCode(), Is.EqualTo(Make(Space.DIP).GetHashCode()));
        }
    }
}
