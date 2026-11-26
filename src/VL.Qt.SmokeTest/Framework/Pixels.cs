using System;
using System.Drawing;

/// <summary>
/// Pixel helpers of the scenarios. Grabs are in device pixels (HiDPI); test coordinates are logical, by default
/// in the 800x600 test window. Import with `using static Pixels;` so `Px`, `Near` and `IsBg` read as before.
/// </summary>
internal static class Pixels
{
    /// <summary>Logical size of the standard test window.</summary>
    public const int Width = 800, Height = 600;

    /// <summary>Per-channel tolerance of <see cref="Near"/> (exclusive).</summary>
    public const int Tolerance = 8;

    /// <summary>Clear color of most scenarios: #202020.</summary>
    public const int Bg = 0x20;

    /// <summary>Pixel at the logical position (x, y) of an 800x600 window.</summary>
    public static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / Width, y * a.Height / Height);

    /// <summary>Pixel at the logical position (x, y) of a window of logical size w x h.</summary>
    public static Color Px(Bitmap a, int x, int y, int w, int h) => a.GetPixel(x * a.Width / w, y * a.Height / h);

    /// <summary>Returns true if every channel of c is within <see cref="Tolerance"/> of (r, g, b).</summary>
    public static bool Near(Color c, int r, int g, int b) => Near(c, r, g, b, Tolerance);

    /// <summary>Returns true if every channel of c is within tolerance (exclusive) of (r, g, b).</summary>
    public static bool Near(Color c, int r, int g, int b, int tolerance) =>
        Math.Abs(c.R - r) < tolerance && Math.Abs(c.G - g) < tolerance && Math.Abs(c.B - b) < tolerance;

    /// <summary>Returns true if c is the #202020 background.</summary>
    public static bool IsBg(Color c) => Near(c, Bg, Bg, Bg);

    /// <summary>Returns true if c is the orange (255, 153, 0) of the test rectangles.</summary>
    public static bool IsOrange(Color c) => Near(c, 255, 153, 0);

    /// <summary>Returns true if both images have the same size and the same pixels (every second row and column).</summary>
    public static bool SameImage(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return false;
        for (int y = 0; y < a.Height; y += 2)
            for (int x = 0; x < a.Width; x += 2)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    return false;
        return true;
    }
}
