using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace VL.Qt.Audio.Tests
{
    /// <summary>
    /// The version of VL.Audio is written in <c>Directory.Build.props</c> (<c>VlAudioVersion</c>, used by the NuGet
    /// dependency of VL.Qt.Audio) and in <c>VL.Qt.Audio/VL.Qt.Audio.vl</c> (<c>NugetDependency</c>, which vvvv reads): an
    /// update of one must not leave the other behind.
    /// </summary>
    [TestFixture]
    public sealed class VlAudioVersionTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Directory.Build.props not found above " + TestContext.CurrentContext.TestDirectory);
        }

        [Test]
        public void TheDocumentCarriesTheVersionOfTheBuild()
        {
            var root = RepoRoot();
            var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
            var build = Regex.Match(props, @"<VlAudioVersion>([^<]+)</VlAudioVersion>");
            Assert.That(build.Success, "no <VlAudioVersion> in Directory.Build.props");

            var document = File.ReadAllText(Path.Combine(root, "VL.Qt.Audio", "VL.Qt.Audio.vl"));
            var dependency = Regex.Match(document, @"<NugetDependency\b[^>]*\bLocation=""VL\.Audio""[^>]*\bVersion=""([^""]+)""");
            Assert.That(dependency.Success, "no NugetDependency on VL.Audio with a Version in VL.Qt.Audio.vl");

            Assert.That(dependency.Groups[1].Value, Is.EqualTo(build.Groups[1].Value.Trim()),
                "VL.Qt.Audio.vl and Directory.Build.props (VlAudioVersion) name different versions of VL.Audio");
        }
    }
}
