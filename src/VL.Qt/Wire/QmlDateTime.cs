using System;

namespace VL.Qt.Wire
{
    /// <summary>
    /// The <see cref="Protocol.ValueType.DateTime"/> value: milliseconds since 1970-01-01 UTC. <see cref="DateTime.MinValue"/> (the
    /// default of a date pin) is long.MinValue, which QtNative takes as no date, the value of a QML <c>date</c> without one.
    /// </summary>
    internal static class QmlDateTime
    {
        private static readonly long s_minMilliseconds = -DateTime.UnixEpoch.Ticks / TimeSpan.TicksPerMillisecond;
        private static readonly long s_maxMilliseconds = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

        /// <summary>A local or unspecified time is local time, as the patch shows it; a UTC time is taken as it is.</summary>
        public static long ToWire(DateTime value)
        {
            if (value == DateTime.MinValue)
                return long.MinValue;
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return Math.DivRem(utc.Ticks - DateTime.UnixEpoch.Ticks, TimeSpan.TicksPerMillisecond, out var rest) - (rest < 0 ? 1 : 0);
        }

        /// <summary>A local time; no date and a time outside the range of DateTime give <see cref="DateTime.MinValue"/>.</summary>
        public static DateTime FromWire(long milliseconds)
        {
            if (milliseconds < s_minMilliseconds || milliseconds > s_maxMilliseconds)
                return DateTime.MinValue;
            return new DateTime(DateTime.UnixEpoch.Ticks + milliseconds * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc).ToLocalTime();
        }
    }
}
