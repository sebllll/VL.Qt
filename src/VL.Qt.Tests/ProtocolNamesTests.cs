using System.Linq;
using NUnit.Framework;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>The names <see cref="RecordingSink"/> prints for element types and properties.</summary>
    [TestFixture]
    internal sealed class ProtocolNamesTests
    {
        [TestCase(typeof(Protocol.ElementType))]
        [TestCase(typeof(Protocol.PropertyId))]
        public void ProtocolNamesAreUnique(System.Type holder)
        {
            var shared = RecordingSink.AllNamesOf(holder).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}: {string.Join(", ", g)} (the sink prints {g.First()})").ToArray();
            Assert.That(shared, Is.Empty, $"{holder.Name}: names that share a value, the sink prints the first one");
        }
    }
}
