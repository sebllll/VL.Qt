namespace VL.Qt
{
    /// <summary>The time of the animation driver: it never runs backwards, whoever advances it (a pump or an offscreen render).</summary>
    internal struct DriverTime
    {
        /// <summary>Time in effect, NaN before the first advance.</summary>
        public double Seconds { get; private set; }

        public static DriverTime Start => new() { Seconds = double.NaN };

        /// <summary>Moves the time to <paramref name="seconds"/> if that is later; true if it moved.</summary>
        public bool TryAdvance(double seconds)
        {
            if (!double.IsNaN(Seconds) && seconds <= Seconds)
                return false;
            Seconds = seconds;
            return true;
        }
    }
}
