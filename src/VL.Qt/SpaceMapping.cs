using System;
using Stride.Core.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// Conversion between the VL units of one scene and Qt logical pixels (DIP), #143. Built per frame from the
    /// <see cref="Space"/> pin and the scene metrics (vlqt_window_get_scene_metrics: size in DIP, device pixel ratio).
    /// </summary>
    /// <remarks>
    /// Plan B of #143: C# scales, QtNative only moves the origin of the window root (SET_SPACE: top left, or the centre
    /// of the scene for Normalized and DIP). A scaled root would render effect layers and text blurry. y points down in
    /// every space. <see cref="UnitScale"/> (logical pixels per unit): Normalized = H / 2 (height -1 … 1, H = scene height
    /// in DIP), DIP and DIPTopLeft = 100, PixelTopLeft = 100 / dpr (100 device pixels). Offscreen scenes have dpr 1.
    /// Lengths (size, translation, font size, radius, ...) are scaled; angles, opacity and scale factors are not.
    /// Matrices use the Stride convention: row vectors, translation in M41..M43.
    /// </remarks>
    internal readonly struct SpaceMapping : IEquatable<SpaceMapping>
    {
        /// <summary>DIP per unit of DIP and DIPTopLeft (the logical pixels of Qt are DIP).</summary>
        public const float DipPerUnit = 100f;

        /// <summary>DIPTopLeft without scene metrics: 1 unit = 100 DIP, the mapping before the first frame.</summary>
        public static readonly SpaceMapping Default = new(Space.DIPTopLeft, default, 1f);

        /// <param name="space">The Space pin.</param>
        /// <param name="sceneSize">Size of the scene in DIP; only Normalized uses it (its height, at least 1 DIP).</param>
        /// <param name="devicePixelRatio">Device pixels per DIP; only PixelTopLeft uses it. Not positive or NaN = 1.</param>
        public SpaceMapping(Space space, Vector2 sceneSize, float devicePixelRatio)
        {
            Space = space;
            SceneSize = sceneSize;
            DevicePixelRatio = devicePixelRatio > 0f ? devicePixelRatio : 1f;
            UnitScale = space switch
            {
                Space.Normalized => MathF.Max(sceneSize.Y, 1f) / 2f,
                Space.PixelTopLeft => DipPerUnit / DevicePixelRatio,
                _ => DipPerUnit,
            };
        }

        public Space Space { get; }

        /// <summary>Size of the scene in DIP (window client size, or the texture size of an offscreen scene).</summary>
        public Vector2 SceneSize { get; }

        /// <summary>Device pixels per DIP (1 offscreen).</summary>
        public float DevicePixelRatio { get; }

        /// <summary>Logical pixels (DIP) per unit.</summary>
        public float UnitScale { get; }

        /// <summary>Returns true if the origin is the centre of the scene (Normalized, DIP), else the top left.</summary>
        public bool IsCentered => Space is Space.Normalized or Space.DIP;

        /// <summary>Length in units → logical pixels.</summary>
        public float ToQt(float length) => length * UnitScale;

        public Vector2 ToQt(Vector2 v) => v * UnitScale;

        public RectangleF ToQt(RectangleF r) =>
            new RectangleF(r.X * UnitScale, r.Y * UnitScale, r.Width * UnitScale, r.Height * UnitScale);

        /// <summary>
        /// Re-expresses a transform given in units in logical pixels: S⁻¹·m·S with S = scale(k, k, k), k = <see cref="UnitScale"/>.
        /// Translation is multiplied by k, the linear part stays unchanged, the perspective column is divided by k.
        /// </summary>
        public Matrix ToQt(in Matrix m)
        {
            var r = m;
            r.M41 *= UnitScale; r.M42 *= UnitScale; r.M43 *= UnitScale;
            r.M14 /= UnitScale; r.M24 /= UnitScale; r.M34 /= UnitScale;
            return r;
        }

        /// <summary>Font size in units → whole logical pixels (QFont pixel sizes are integers), at least 1.</summary>
        public float ToFontPixels(float size) => MathF.Max(1f, MathF.Round(size * UnitScale));

        /// <summary>Logical pixels → length in units (values coming back from Qt).</summary>
        public float FromQt(float length) => length / UnitScale;

        public Vector2 FromQt(Vector2 v) => v / UnitScale;

        public RectangleF FromQt(RectangleF r) =>
            new RectangleF(r.X / UnitScale, r.Y / UnitScale, r.Width / UnitScale, r.Height / UnitScale);

        public bool Equals(SpaceMapping other) =>
            Space == other.Space && SceneSize.X == other.SceneSize.X && SceneSize.Y == other.SceneSize.Y
            && DevicePixelRatio == other.DevicePixelRatio;

        public override bool Equals(object? obj) => obj is SpaceMapping other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Space, SceneSize.X, SceneSize.Y, DevicePixelRatio);

        public static bool operator ==(SpaceMapping a, SpaceMapping b) => a.Equals(b);

        public static bool operator !=(SpaceMapping a, SpaceMapping b) => !a.Equals(b);

        public override string ToString() => $"{Space}, {UnitScale} DIP/unit";
    }
}
