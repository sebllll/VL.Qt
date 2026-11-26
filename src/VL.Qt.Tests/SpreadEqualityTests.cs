using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;

namespace VL.Qt.Tests
{
    public class SpreadEqualityTests
    {
        [Test]
        public void SameInstanceOrContent_IsSame()
        {
            var a = Spread.Create(1f, 2f);
            Assert.That(SpreadEquality.SameContent(a, a), Is.True);
            Assert.That(SpreadEquality.SameContent(a, Spread.Create(1f, 2f)), Is.True);
            Assert.That(SpreadEquality.SameContent(a, Spread.Create(1f, 3f)), Is.False);
            Assert.That(SpreadEquality.SameContent(a, Spread.Create(1f)), Is.False);
        }

        [Test]
        public void Missing_DiffersFromAnySpread()
        {
            Assert.That(SpreadEquality.SameContent<float>(null, null), Is.True);
            Assert.That(SpreadEquality.SameContent(null, Spread<float>.Empty), Is.False);
            Assert.That(SpreadEquality.SameContent(Spread<float>.Empty, null), Is.False);
        }

        [Test]
        public void NaN_EqualsItself()
        {
            Assert.That(SpreadEquality.SameContent(Spread.Create(float.NaN), Spread.Create(float.NaN)), Is.True);
            Assert.That(SpreadEquality.SameContent(Spread.Create(new Vector2(float.NaN, 1)), Spread.Create(new Vector2(float.NaN, 1))), Is.True);
        }

        [Test]
        public void Enums_CompareByValue()
        {
            Assert.That(SpreadEquality.SameContent(Spread.Create(System.DayOfWeek.Monday), Spread.Create(System.DayOfWeek.Monday)), Is.True);
            Assert.That(SpreadEquality.SameContent(Spread.Create(System.DayOfWeek.Monday), Spread.Create(System.DayOfWeek.Friday)), Is.False);
        }
    }
}
