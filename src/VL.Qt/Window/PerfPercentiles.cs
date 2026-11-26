namespace VL.Qt
{
    /// <summary>Frame budget and the frame and pump percentiles of a Qt window over the last up to 240 pumps (ms, nearest rank).</summary>
    /// <param name="BudgetMs">Frame budget in ms: vvvv's frame interval, which Qt renders in step with (60 Hz before the first frame).</param>
    /// <param name="FrameMsP50">Median interval between consecutive frames (only intervals between consecutive frames, not after idle pumps).</param>
    /// <param name="FrameMsP95">95th percentile of the frame interval.</param>
    /// <param name="FrameMsP99">99th percentile of the frame interval.</param>
    /// <param name="PumpMsP50">Median pump time.</param>
    /// <param name="PumpMsP95">95th percentile of the pump time.</param>
    /// <param name="PumpMsP99">99th percentile of the pump time.</param>
    public readonly record struct PerfPercentiles(
        float BudgetMs,
        float FrameMsP50, float FrameMsP95, float FrameMsP99,
        float PumpMsP50, float PumpMsP95, float PumpMsP99);
}
