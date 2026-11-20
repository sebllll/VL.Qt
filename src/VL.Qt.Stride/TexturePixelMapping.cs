using System;
using Stride.Core.Mathematics;

namespace VL.Qt.Stride
{
    /// <summary>
    /// The mapping of ToTexturePixels (#119): a window position through the inverse of World * View * Projection onto the
    /// plane z = 0 of Stride's unit quad (-0.5 ... 0.5 in local XY, +Y up), then to texture pixels with a top-left origin.
    /// Immutable: the matrices are inverted once per change, <see cref="Map"/> allocates nothing.
    /// </summary>
    internal sealed class TexturePixelMapping
    {
        // Where a position without a hit lands (ray parallel to the quad or a singular matrix): far outside every texture.
        public static readonly Vector2 Miss = new(-1e6f, -1e6f);

        private readonly Matrix _inverse;
        private readonly bool _valid;
        public readonly Vector2 TextureSize;

        /// <summary>A zero matrix counts as identity (an unconnected Matrix pin).</summary>
        public TexturePixelMapping(Matrix world, Matrix view, Matrix projection, Vector2 textureSize)
        {
            TextureSize = textureSize;
            var wvp = OrIdentity(world) * OrIdentity(view) * OrIdentity(projection);
            _valid = Math.Abs(wvp.Determinant()) > 1e-12f;
            _inverse = _valid ? Matrix.Invert(wvp) : Matrix.Identity;
        }

        private static Matrix OrIdentity(Matrix m) => m == Matrix.Zero ? Matrix.Identity : m;

        /// <summary>Returns the texture pixel under <paramref name="position"/> (pixels of a window of size <paramref name="clientArea"/>, top-left origin); outside 0 ... Texture Size when the quad is not hit there.</summary>
        public Vector2 Map(Vector2 position, Vector2 clientArea)
        {
            if (!_valid || clientArea.X <= 0 || clientArea.Y <= 0)
                return Miss;
            // The window point as a ray through the clip volume (near and far plane), back into the quad's local space.
            var x = 2 * position.X / clientArea.X - 1;
            var y = 1 - 2 * position.Y / clientArea.Y;
            var near = Vector3.TransformCoordinate(new Vector3(x, y, 0), _inverse);
            var far = Vector3.TransformCoordinate(new Vector3(x, y, 1), _inverse);
            var dz = far.Z - near.Z;
            if (Math.Abs(dz) < 1e-12f)
                return Miss;
            var t = -near.Z / dz;
            var lx = near.X + t * (far.X - near.X);
            var ly = near.Y + t * (far.Y - near.Y);
            if (!float.IsFinite(lx) || !float.IsFinite(ly))
                return Miss;
            return new Vector2((lx + 0.5f) * TextureSize.X, (0.5f - ly) * TextureSize.Y);
        }
    }
}
