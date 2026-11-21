namespace VL.Qt
{
    /// <summary>
    /// A window (renderer) that takes part in the global pump. Called on the Qt thread, once per vvvv frame,
    /// after all node updates; only <see cref="PollInput"/> runs at the frame start, before them.
    /// </summary>
    internal interface IQtFrameParticipant
    {
        /// <summary>Hands the commands written in Update() to QtNative (vlqt_scene_apply).</summary>
        void ApplyCommands();

        /// <summary>
        /// QtNative window of this participant, zero = none. Only the windows of the pumping app may render in its
        /// pump, so with several vvvv apps a window still renders once per vvvv frame, not once per app.
        /// </summary>
        SceneHandle Window => SceneHandle.Null;

        /// <summary>After Qt processed its events (and rendered): poll the window, read perf, handle close requests.</summary>
        void AfterEvents();

        /// <summary>
        /// at the start of the vvvv frame, after Qt delivered the queued input (<see cref="Native.vlqt_app_deliver_input"/>):
        /// poll the window into the feedback, so the node updates of this frame see it. Nothing is rendered or applied.
        /// </summary>
        void PollInput() { }

        /// <summary>
        /// after <see cref="AfterEvents"/>, the host values of this pump for the window's native PerfMonitor. QtApp fills
        /// apply time and the shared values (host pump, interval, GC); the participant adds ops/reconcile and reports.
        /// </summary>
        void ReportHostFrame(Native.vlqt_host_pump frame) { }

        /// <summary>Last native PerfSample of the window (budget warning). false = none.</summary>
        bool TryGetLastSample(out Native.vlqt_perf_sample sample)
        {
            sample = default;
            return false;
        }
    }
}
