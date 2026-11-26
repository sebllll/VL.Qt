using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;

namespace VL.Qt.Tests.Scene
{
    public class GeometryTests
    {
        private const float Eps = 1e-3f;

        // 2 x 1 units → local item 200 x 100 DIP.
        private static readonly Vector2 Size = new Vector2(2f, 1f);

        private static Vector2 Apply(in Matrix m, float x, float y)
        {
            var r = Vector3.TransformCoordinate(new Vector3(x, y, 0), m);
            return new Vector2(r.X, r.Y);
        }

        private static void AssertPoint(Vector2 actual, float x, float y)
        {
            Assert.That(actual.X, Is.EqualTo(x).Within(Eps), "x");
            Assert.That(actual.Y, Is.EqualTo(y).Within(Eps), "y");
        }

        private static void AssertMatrix(in Matrix actual, in Matrix expected)
        {
            var a = actual.ToArray();
            var e = expected.ToArray();
            for (int i = 0; i < 16; i++)
                Assert.That(a[i], Is.EqualTo(e[i]).Within(Eps), $"element {i}");
        }

        [TestCase(RectangleAnchor.TopLeft, 0f, 0f)]
        [TestCase(RectangleAnchor.TopCenter, 100f, 0f)]
        [TestCase(RectangleAnchor.TopRight, 200f, 0f)]
        [TestCase(RectangleAnchor.MiddleLeft, 0f, 50f)]
        [TestCase(RectangleAnchor.Center, 100f, 50f)]
        [TestCase(RectangleAnchor.MiddleRight, 200f, 50f)]
        [TestCase(RectangleAnchor.BottomLeft, 0f, 100f)]
        [TestCase(RectangleAnchor.BottomCenter, 100f, 100f)]
        [TestCase(RectangleAnchor.BottomRight, 200f, 100f)]
        public void AnchorOffset_AllAnchors(RectangleAnchor anchor, float px, float py)
            => AssertPoint(Geometry.AnchorOffset(Size, anchor) * SpaceMapping.DipPerUnit, px, py);

        [TestCase(RectangleAnchor.TopLeft, 0f, 0f)]
        [TestCase(RectangleAnchor.TopCenter, 100f, 0f)]
        [TestCase(RectangleAnchor.TopRight, 200f, 0f)]
        [TestCase(RectangleAnchor.MiddleLeft, 0f, 50f)]
        [TestCase(RectangleAnchor.Center, 100f, 50f)]
        [TestCase(RectangleAnchor.MiddleRight, 200f, 50f)]
        [TestCase(RectangleAnchor.BottomLeft, 0f, 100f)]
        [TestCase(RectangleAnchor.BottomCenter, 100f, 100f)]
        [TestCase(RectangleAnchor.BottomRight, 200f, 100f)]
        public void Identity_AnchorPointLandsAtParentOrigin(RectangleAnchor anchor, float px, float py)
        {
            // Position comes from the transform: with Identity the anchor point of the element sits at the origin.
            var q = SpaceMapping.Default.ToQtTransform(Matrix.Identity, Size, anchor);
            AssertPoint(Apply(q, px, py), 0, 0);
            AssertPoint(Apply(q, 0, 0), -px, -py);
        }

        [Test]
        public void Identity_TopLeft_IsIdentityMatrix()
            => AssertMatrix(SpaceMapping.Default.ToQtTransform(Matrix.Identity, Size, RectangleAnchor.TopLeft), Matrix.Identity);

        [Test]
        public void Identity_Center_ElementCenterAtOrigin()
        {
            var q = SpaceMapping.Default.ToQtTransform(Matrix.Identity, Size, RectangleAnchor.Center);
            AssertPoint(Apply(q, 100, 50), 0, 0);
            AssertPoint(Apply(q, 0, 0), -100, -50);
            AssertPoint(Apply(q, 200, 100), 100, 50);
            Assert.That(q.M41, Is.EqualTo(-100f).Within(Eps));
            Assert.That(q.M42, Is.EqualTo(-50f).Within(Eps));
        }

        [TestCase(RectangleAnchor.TopLeft, 0f, 0f)]
        [TestCase(RectangleAnchor.Center, 100f, 50f)]
        [TestCase(RectangleAnchor.BottomRight, 200f, 100f)]
        public void Rotation_AroundAnchor_AnchorStaysAtOrigin(RectangleAnchor anchor, float px, float py)
        {
            // Skia order: (v - anchor) · m. The anchor point itself ends at the origin for any rotation/scale.
            var m = Matrix.Scaling(2f, 3f, 1f) * Matrix.RotationZ(MathF.PI / 2);
            var q = SpaceMapping.Default.ToQtTransform(m, Size, anchor);
            AssertPoint(Apply(q, px, py), 0, 0);
            // 10 DIP right of the anchor: (10, 0) · S(2,3) = (20, 0); · Rz(90°) = (0, 20).
            AssertPoint(Apply(q, px + 10, py), 0, 20);
        }

        [Test]
        public void Rotation90_AroundCenter_ThenTranslated()
        {
            // Stride row vectors: (1,0) · Rz(90°) = (0,1), i.e. clockwise on screen (y down).
            var m = Matrix.RotationZ(MathF.PI / 2) * Matrix.Translation(0.5f, 0.25f, 0f); // position 50, 25 DIP
            var q = SpaceMapping.Default.ToQtTransform(m, Size, RectangleAnchor.Center);
            AssertPoint(Apply(q, 100, 50), 50, 25);    // center lands at the translation
            AssertPoint(Apply(q, 0, 0), 100, -75);     // (-100,-50) · Rz(90°) = (50,-100), + (50,25)
            AssertPoint(Apply(q, 200, 100), 0, 125);   // (100, 50) · Rz(90°) = (-50, 100), + (50,25)
        }

        [Test]
        public void Scaling_AroundTopLeft_And_BottomRight()
        {
            var s = Matrix.Scaling(0.5f, 2f, 1f);
            var tl = SpaceMapping.Default.ToQtTransform(s, Size, RectangleAnchor.TopLeft);
            AssertMatrix(tl, s); // anchor at local origin → unchanged, no translation
            AssertPoint(Apply(tl, 200, 100), 100, 200);

            var br = SpaceMapping.Default.ToQtTransform(s, Size, RectangleAnchor.BottomRight);
            AssertPoint(Apply(br, 200, 100), 0, 0);
            AssertPoint(Apply(br, 0, 0), -100, -200);
        }

        [Test]
        public void AnchorOffset_ScalesWithSize()
        {
            Assert.That(Geometry.AnchorOffset(new Vector2(4f, 2f), RectangleAnchor.Center), Is.EqualTo(new Vector2(2f, 1f)));
            Assert.That(Geometry.AnchorOffset(new Vector2(4f, 2f), RectangleAnchor.BottomRight), Is.EqualTo(new Vector2(4f, 2f)));
            Assert.That(Geometry.AnchorOffset(Vector2.Zero, RectangleAnchor.Center), Is.EqualTo(Vector2.Zero));
        }

        [Test]
        public void Translation_IsScaledTo_Dip_RotationAndScaleUnchanged()
        {
            var m = Matrix.Scaling(1.5f) * Matrix.RotationZ(0.3f) * Matrix.Translation(0.1f, -0.2f, 0f);
            var q = SpaceMapping.Default.ToQt(m);
            Assert.That(q.M41, Is.EqualTo(10f).Within(Eps));
            Assert.That(q.M42, Is.EqualTo(-20f).Within(Eps));
            Assert.That(q.M11, Is.EqualTo(m.M11));
            Assert.That(q.M12, Is.EqualTo(m.M12));
            Assert.That(q.M21, Is.EqualTo(m.M21));
            Assert.That(q.M22, Is.EqualTo(m.M22));

            // Translation in the element transform is applied after the anchor shift.
            var t = SpaceMapping.Default.ToQtTransform(Matrix.Translation(0.1f, 0.2f, 0f), Size, RectangleAnchor.TopLeft);
            AssertPoint(Apply(t, 0, 0), 10, 20);
        }

        [Test]
        public void Space_DipTopLeft_OneUnitIs100Dip()
        {
            Assert.That(SpaceMapping.Default.ToQt(0.01f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(SpaceMapping.Default.ToQt(new RectangleF(0.5f, 0.25f, 2f, 1f)), Is.EqualTo(new RectangleF(50, 25, 200, 100)));
            Assert.That(SpaceMapping.Default.FromQt(SpaceMapping.Default.ToQt(new Vector2(1.25f, -3f))), Is.EqualTo(new Vector2(1.25f, -3f)));
        }

        [Test]
        public void FontPixels_RoundedToWholeDip_AtLeastOne()
        {
            Assert.That(SpaceMapping.Default.ToFontPixels(0.12f), Is.EqualTo(12f));
            Assert.That(SpaceMapping.Default.ToFontPixels(0.1249f), Is.EqualTo(12f));
            Assert.That(SpaceMapping.Default.ToFontPixels(0.126f), Is.EqualTo(13f));
            Assert.That(SpaceMapping.Default.ToFontPixels(0f), Is.EqualTo(1f));
            Assert.That(SpaceMapping.Default.ToFontPixels(-1f), Is.EqualTo(1f));
        }
    }
}
