using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt.Shaders;

namespace VL.Qt.Tests
{
    /// <summary>
    /// The one native list of reserved uniform names (effects/shader_item.h) and its C# mirror <see cref="ReservedUniforms"/>
    /// agree. Needs no GPU and no QtNative: only the header (copied to the output directory).
    /// </summary>
    [TestFixture]
    public sealed class ReservedUniformsDriftTests
    {
        [Test]
        public void HeaderAndMirrorAgree()
        {
            var header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shader_item.h"));
            Assert.That(Names(header, "kItemReservedUniforms"), Is.EquivalentTo(ReservedUniforms.Item), "kItemReservedUniforms <-> ReservedUniforms.Item");
            Assert.That(Names(header, "kChainAutomaticUniforms"), Is.EquivalentTo(ReservedUniforms.ChainAutomatic), "kChainAutomaticUniforms <-> ReservedUniforms.ChainAutomatic");
            Assert.That(Names(header, "kChainReservedUniforms"), Is.EquivalentTo(ReservedUniforms.ChainReserved), "kChainReservedUniforms <-> ReservedUniforms.ChainReserved");
        }

        [Test]
        public void ChainReservedIsTheUnionOfTheThreeLists()
        {
            foreach (var name in ReservedUniforms.Item.Concat(ReservedUniforms.ChainAutomatic).Concat(ReservedUniforms.ChainReserved))
                Assert.That(ReservedUniforms.IsChainReserved(name), Is.True, name);
            Assert.That(ReservedUniforms.IsChainReserved("amount"), Is.False);
            foreach (var name in new[] { "texelSize", "dpr", "viewSize" })
                Assert.That(ReservedUniforms.IsItemReserved(name), Is.True, name);
            Assert.That(ReservedUniforms.IsItemReserved("weight"), Is.False, "weight is only reserved in an effect chain");
        }

        // The string literals of "inline constexpr std::string_view <name>[] = {...};".
        private static List<string> Names(string header, string array)
        {
            var m = Regex.Match(header, @"\b" + array + @"\[\]\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant);
            Assert.That(m.Success, $"{array} not found in shader_item.h");
            var names = Regex.Matches(m.Groups["body"].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
            Assert.That(names, Is.Not.Empty, array);
            return names;
        }
    }
}
