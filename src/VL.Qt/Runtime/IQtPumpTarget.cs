namespace VL.Qt
{
    /// <summary>What a <see cref="QtPump"/> drives (the <see cref="QtApp"/>; a fake in unit tests, which have no Qt).</summary>
    internal interface IQtPumpTarget
    {
        /// <summary>Last time handed to the animation driver, NaN before the first pump.</summary>
        double DriverSeconds { get; }

        /// <summary>
        /// apply → advance(<paramref name="driverSeconds"/>) → processEvents → poll. <paramref name="vvvvStart"/>: Stopwatch
        /// timestamp of the vvvv frame start before this pump, 0 = unknown. <paramref name="meter"/>: host-pump
        /// values of the pumping app (interval and GC per app, not since another app's pump).
        /// </summary>
        void PumpFrame(IQtFrameParticipant[] participants, double driverSeconds, double budgetMs, long vvvvStart, HostPumpMeter meter);

        /// <summary>
        /// start of the vvvv frame: Qt delivers the input queued since the last pump (without rendering), then every
        /// participant polls it (<see cref="IQtFrameParticipant.PollInput"/>).
        /// </summary>
        void DeliverInput(IQtFrameParticipant[] participants) { }

        /// <summary>Bookkeeping for [vlqt.stats]: participants (+registered / -unregistered) and live pumps.</summary>
        void OnParticipantsChanged(int delta);
        void OnPumpChanged(int delta);
    }
}
