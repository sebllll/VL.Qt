using System;

namespace VL.Qt.Tests
{
    /// <summary>Measures the managed allocations of a frame loop on the current thread.</summary>
    internal static class Allocations
    {
        /// <summary>
        /// Returns the fewest bytes the current thread allocated while calling <paramref name="frame"/>
        /// <paramref name="frames"/> times, in up to five attempts (stops at the first attempt without allocation).
        /// </summary>
        /// <remarks>
        /// A real per-frame allocation shows in every attempt; a one-off by the runtime on this thread (tiered JIT,
        /// lazy init in the framework, seen as ~7 KB once in a few dozen full runs) only in one. Create the delegate
        /// before calling, so a closure is not counted.
        /// </remarks>
        public static long Fewest(Action frame, int frames = 1000)
        {
            long fewest = long.MaxValue;
            for (int attempt = 0; attempt < 5 && fewest != 0; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < frames; i++)
                    frame();
                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return fewest;
        }
    }
}
