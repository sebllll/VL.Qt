using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace VL.Qt.Tests
{
    /// <summary>The shared pin texts of <c>src/VL.Qt/Nodes/CommonPins.xml</c>, for tests that check texts written elsewhere against them.</summary>
    internal static class CommonPinsXml
    {
        public static string Path()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var file = System.IO.Path.Combine(dir.FullName, "src", "VL.Qt", "Nodes", "CommonPins.xml");
                if (File.Exists(file))
                    return file;
            }
            throw new InvalidOperationException("src/VL.Qt/Nodes/CommonPins.xml not found above " + AppContext.BaseDirectory);
        }

        /// <summary>The text of the <c>param</c> with this name in the set, e.g. <c>Param("element", "debug")</c>.</summary>
        public static string Param(string set, string name) =>
            XDocument.Load(Path()).Root!.Elements("set").Single(s => (string?)s.Attribute("name") == set)
                .Elements("param").Single(p => (string?)p.Attribute("name") == name).Value;
    }
}
