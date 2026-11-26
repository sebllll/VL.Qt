namespace VL.Qt
{
    /// <summary>.NET garbage collector activity of the process over ~1 s.</summary>
    /// <param name="GenZeroPerSecond">Generation 0 collections per second.</param>
    /// <param name="GenOnePerSecond">Generation 1 collections per second.</param>
    /// <param name="GenTwoPerSecond">Generation 2 collections per second.</param>
    /// <param name="GcPauseMsPerSecond">GC pause in ms per second.</param>
    /// <param name="AllocatedBytesPerFrame">Bytes allocated per vvvv frame (process, average).</param>
    public readonly record struct GcPerfStats(
        float GenZeroPerSecond, float GenOnePerSecond, float GenTwoPerSecond,
        float GcPauseMsPerSecond, float AllocatedBytesPerFrame);
}
