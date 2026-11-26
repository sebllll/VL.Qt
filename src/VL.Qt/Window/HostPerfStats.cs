namespace VL.Qt
{
    /// <summary>The host side of the frame of a Qt window (ms, average and max over ~1 s).</summary>
    /// <param name="VvvvMs">vvvv frame (patch update, from the frame clock's tick to the Qt pump), average.</param>
    /// <param name="VvvvMsMax">Longest vvvv frame.</param>
    /// <param name="WaitMs">Waiting between the end of the previous Qt pump and the next vvvv frame start (frame timer, vsync), average.</param>
    /// <param name="WaitMsMax">Longest wait.</param>
    /// <param name="ApplyMs">Applying this window's commands in the pump, average.</param>
    /// <param name="ApplyMsMax">Longest apply.</param>
    /// <param name="ReconcileMs">Reconcile and command writing in the node update (ms per frame), average.</param>
    /// <param name="ReconcileMsMax">Longest reconcile.</param>
    /// <param name="Ops">Commands applied per frame, average.</param>
    public readonly record struct HostPerfStats(
        float VvvvMs, float VvvvMsMax,
        float WaitMs, float WaitMsMax,
        float ApplyMs, float ApplyMsMax,
        float ReconcileMs, float ReconcileMsMax,
        float Ops);
}
