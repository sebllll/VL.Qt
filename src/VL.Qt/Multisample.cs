using System;

namespace VL.Qt
{
    /// <summary>What the nodes with a Multisample Count pin (Renderer [Qt], QtTexture) share: the clamp warning (#118).</summary>
    internal static class Multisample
    {
        /// <summary>
        /// The warning for a count QtNative had to lower to what the device supports, null if the request was met.
        /// <paramref name="used"/> is the count QtNative reports (>= 1); 0 or less = unknown, no warning.
        /// </summary>
        public static string? ClampWarning(MultisampleCount requested, int used)
            => used >= 1 && used < (int)requested
                ? $"Multisample Count {(int)requested}x is not supported by this device, {used}x is used."
                : null;
    }
}
