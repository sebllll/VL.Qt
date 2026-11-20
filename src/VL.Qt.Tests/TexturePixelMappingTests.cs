using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Stride;

namespace VL.Qt.Tests
{
    /// <summary>TexturePixelMapping of ToTexturePixels, compiled in from VL.Qt.Stride: window position to texture pixel.</summary>
    public class TexturePixelMappingTests
    {
        private static readonly Vector2 s_texture = new(100, 50);
        private static readonly Vector2 s_client = new(200, 100);

        private static void AssertPixel(Vector2 actual, float x, float y)
        {
            Assert.That(actual.X, Is.EqualTo(x).Within(1e-3f), "x of " + actual);
            Assert.That(actual.Y, Is.EqualTo(y).Within(1e-3f), "y of " + actual);
        }

        // World = Scaling(2, 2, 1) turns the unit quad (-0.5 ... 0.5) into the whole clip space (-1 ... 1).
        private static TexturePixelMapping Fullscreen() => new(Matrix.Scaling(2, 2, 1), Matrix.Identity, Matrix.Identity, s_texture);

        [Test]
        public void FullscreenMapsWindowCornersToTextureCorners()
        {
            var mapping = Fullscreen();
            AssertPixel(mapping.Map(new Vector2(0, 0), s_client), 0, 0);
            AssertPixel(mapping.Map(new Vector2(200, 0), s_client), 100, 0);
            AssertPixel(mapping.Map(new Vector2(0, 100), s_client), 0, 50);
            AssertPixel(mapping.Map(new Vector2(200, 100), s_client), 100, 50);
            AssertPixel(mapping.Map(new Vector2(100, 50), s_client), 50, 25);
        }

        [Test]
        public void PerspectiveCameraWithRotatedQuadHitsTheCentre()
        {
            var world = Matrix.RotationY(0.6f);
            var view = Matrix.LookAtRH(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY);
            var projection = Matrix.PerspectiveFovRH(MathUtil.Pi / 3, 2, 0.1f, 100f);
            var mapping = new TexturePixelMapping(world, view, projection, s_texture);

            // The ray through the window centre passes the quad's origin whatever its rotation.
            AssertPixel(mapping.Map(new Vector2(100, 50), s_client), 50, 25);

            // Right of the centre lands right of the texture centre, above lands above (top-left origin).
            var right = mapping.Map(new Vector2(150, 50), s_client);
            var above = mapping.Map(new Vector2(100, 25), s_client);
            Assert.That(right.X, Is.GreaterThan(50));
            Assert.That(right.Y, Is.EqualTo(25).Within(1e-3f));
            Assert.That(above.Y, Is.LessThan(25));
            Assert.That(above.X, Is.EqualTo(50).Within(1e-3f));
        }

        [Test]
        public void RayParallelToTheQuadGivesMiss()
        {
            // Swaps local X and Z: the quad plane becomes world x = 0, edge-on to the rays of the orthographic camera.
            var world = Matrix.Identity;
            world.M11 = 0;
            world.M13 = 1;
            world.M31 = 1;
            world.M33 = 0;
            var mapping = new TexturePixelMapping(world, Matrix.Identity, Matrix.Identity, s_texture);
            Assert.That(mapping.Map(new Vector2(60, 40), s_client), Is.EqualTo(TexturePixelMapping.Miss));
        }

        [Test]
        public void SingularMatrixGivesMiss()
        {
            var mapping = new TexturePixelMapping(Matrix.Scaling(1, 1, 0), Matrix.Identity, Matrix.Identity, s_texture);
            Assert.That(mapping.Map(new Vector2(100, 50), s_client), Is.EqualTo(TexturePixelMapping.Miss));
        }

        [Test]
        public void EmptyClientAreaGivesMiss()
        {
            Assert.That(Fullscreen().Map(new Vector2(10, 10), new Vector2(0, 100)), Is.EqualTo(TexturePixelMapping.Miss));
            Assert.That(Fullscreen().Map(new Vector2(10, 10), new Vector2(200, 0)), Is.EqualTo(TexturePixelMapping.Miss));
        }

        [Test]
        public void ZeroMatrixCountsAsIdentity()
        {
            var zero = new TexturePixelMapping(Matrix.Zero, Matrix.Zero, Matrix.Zero, s_texture);
            var identity = new TexturePixelMapping(Matrix.Identity, Matrix.Identity, Matrix.Identity, s_texture);
            foreach (var p in new[] { new Vector2(0, 0), new Vector2(50, 20), new Vector2(150, 90) })
            {
                var expected = identity.Map(p, s_client);
                Assert.That(expected, Is.Not.EqualTo(TexturePixelMapping.Miss));
                Assert.That(zero.Map(p, s_client), Is.EqualTo(expected));
            }

            // Identity: the window is clip space and the unit quad covers its middle half.
            AssertPixel(identity.Map(new Vector2(100, 50), s_client), 50, 25);
        }
    }
}
