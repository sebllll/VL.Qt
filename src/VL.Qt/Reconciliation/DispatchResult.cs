namespace VL.Qt.Reconciliation
{
    /// <summary>Counts of one <see cref="FeedbackRouter.Dispatch"/> call.</summary>
    internal readonly record struct DispatchResult(int Applied, int Ignored, string? Error);
}
