using System;
using NUnit.Framework;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Wire
{
    /// <summary>QmlDateTime: DateTime to milliseconds since 1970 UTC and back.</summary>
    public class QmlDateTimeTests
    {
        [Test]
        public void ToWire_UtcAsItIs_LocalAndUnspecifiedAsLocalTime()
        {
            var utc = new DateTime(2024, 5, 1, 10, 0, 0, 250, DateTimeKind.Utc);
            var ms = (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;
            Assert.That(QmlDateTime.ToWire(utc), Is.EqualTo(ms));
            Assert.That(QmlDateTime.ToWire(utc.ToLocalTime()), Is.EqualTo(ms));
            Assert.That(QmlDateTime.ToWire(DateTime.SpecifyKind(utc.ToLocalTime(), DateTimeKind.Unspecified)), Is.EqualTo(ms));
        }

        [Test]
        public void ToWire_BeforeTheEpoch_RoundsDown()
        {
            Assert.That(QmlDateTime.ToWire(DateTime.UnixEpoch.AddTicks(-1)), Is.EqualTo(-1L));
            Assert.That(QmlDateTime.ToWire(DateTime.UnixEpoch.AddMilliseconds(-1)), Is.EqualTo(-1L));
        }

        [Test]
        public void MinValue_IsNoDate_BothWays()
        {
            Assert.That(QmlDateTime.ToWire(DateTime.MinValue), Is.EqualTo(long.MinValue));
            Assert.That(QmlDateTime.FromWire(long.MinValue), Is.EqualTo(DateTime.MinValue));
            Assert.That(QmlDateTime.FromWire(long.MaxValue), Is.EqualTo(DateTime.MinValue), "outside the range of DateTime");
        }

        [Test]
        public void FromWire_IsLocalTime_AndRoundTrips()
        {
            var local = new DateTime(2024, 12, 31, 23, 59, 59, 999, DateTimeKind.Local);
            var back = QmlDateTime.FromWire(QmlDateTime.ToWire(local));
            Assert.That(back, Is.EqualTo(local));
            Assert.That(back.Kind, Is.EqualTo(DateTimeKind.Local));
        }
    }
}
