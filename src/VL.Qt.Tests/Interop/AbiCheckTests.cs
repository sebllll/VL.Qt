using NUnit.Framework;
using VL.Qt;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Interop
{
    [TestFixture]
    public sealed class AbiCheckTests
    {
        [Test]
        public void SameVersionPasses() => Assert.That(AbiCheck.Describe(Protocol.AbiVersion, Protocol.AbiVersion, "x"), Is.Null);

        [Test]
        public void OtherVersionNamesBothVersionsAndThePath()
        {
            var text = AbiCheck.Describe(14, 15, @"C:\pkg\vlqt.dll");
            Assert.That(text, Is.EqualTo(@"vlqt.dll C:\pkg\vlqt.dll has ABI 14, VL.Qt.dll expects 15: rebuild QtNative or reinstall the package"));
        }

        [Test]
        public void MissingExportIsAMismatch()
        {
            var text = AbiCheck.Describe(null, 15, "vlqt.dll");
            Assert.That(text, Does.Contain("has ABI none").And.Contain("expects 15"));
        }
    }
}
