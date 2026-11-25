namespace VL.Qt
{
    /// <summary>Pump counters of a Qt window, either since the window was created or over the last 10 s.</summary>
    /// <param name="Pumps">Pumps.</param>
    /// <param name="OverBudget">Pumps whose Qt work (events, sync, render; not the vsync wait and Present) took longer than the budget.</param>
    /// <param name="LongFrames">Frame intervals longer than 1.5 x budget between consecutive frames (missed frames; not after idle pumps of on-demand rendering).</param>
    /// <param name="IdlePumps">Pumps without a frame (nothing changed).</param>
    /// <param name="MultiSwaps">Pumps with more than one frame.</param>
    public readonly record struct PerfCounters(long Pumps, long OverBudget, long LongFrames, long IdlePumps, long MultiSwaps);
}
