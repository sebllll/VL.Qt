namespace VL.Qt.Nodes
{
    /// <summary>The room that the blur pyramid of Glow and InnerShadow needs around the element.</summary>
    internal static class PyramidPadding
    {
        // The pyramid approximates a Gaussian of sigma = radius / 2 (the native pyramidLevel in effect_chain.cpp takes
        // sigma = radius x dpr / 2 pixels), and a Gaussian is practically zero beyond 3 sigma = 1.5 x radius.
        public const float RadiusFactor = 1.5f;
    }
}
