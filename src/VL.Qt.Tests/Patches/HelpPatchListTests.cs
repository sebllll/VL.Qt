using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace VL.Qt.Tests.Patches
{
    /// <summary>docs/help-patches.md lists every patch of the three Help.xml files.</summary>
    [TestFixture]
    internal sealed class HelpPatchListTests
    {
        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docs", "help-patches.md")))
                    return dir.FullName;
            }
            throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
        }

        [Test]
        public void EveryHelpXmlPatchIsInHelpPatchesPage()
        {
            var root = RepoRoot();
            var page = File.ReadAllText(Path.Combine(root, "docs", "help-patches.md"));
            var missing = new[] { "help", "VL.Qt.Stride/help", "VL.Qt.Audio/help" }
                .SelectMany(folder => XDocument.Load(Path.Combine(root, folder, "Help.xml")).Descendants("VLDocument"))
                .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("link")!).Replace('\\', '/')))
                .Where(name => !page.Contains(name, StringComparison.Ordinal))
                .ToList();

            Assert.That(missing, Is.Empty, "patches listed in a Help.xml but missing in docs/help-patches.md");
        }
    }
}
