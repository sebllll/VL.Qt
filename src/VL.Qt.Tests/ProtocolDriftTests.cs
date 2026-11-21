using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>
    /// protocol.h (single source of truth) and its hand-written mirror Protocol.cs agree in name and value, and so do
    /// vlqt_test.h (the constants of the test exports) and the mirror TestProtocol.cs of the test support. Needs no GPU and no
    /// QtNative: only the headers (copied to the output directory) and reflection.
    /// </summary>
    [TestFixture]
    public sealed class ProtocolDriftTests
    {
        private static readonly Regex s_define = new(@"^\s*#define\s+VLQT_(\w+)\s+(-?\d+)\s*(/\*.*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex s_enumMember = new(@"^\s*VLQT_(\w+)\s*=\s*(-?\d+)\s*,?\s*(/\*.*)?$", RegexOptions.CultureInvariant);

        [Test]
        public void HeaderAndMirrorAgree() => AssertAgree("protocol.h", "Protocol.cs", typeof(Protocol));

        [Test]
        public void TestHeaderAndMirrorAgree() => AssertAgree("vlqt_test.h", "TestProtocol.cs", typeof(TestProtocol));

        private static void AssertAgree(string headerName, string mirrorName, Type mirror)
        {
            var errors = new List<string>();
            var header = ParseHeader(headerName, File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, headerName)), errors);
            Assert.That(header, Is.Not.Empty, "no constants parsed from " + headerName);
            errors.AddRange(Compare(headerName, mirrorName, header, ReflectMirror(mirror)));
            Assert.That(errors, Is.Empty, headerName + " <-> " + mirrorName + ":" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        /// <summary>
        /// The <c>vlqt_result</c> enum of vlqt.h (the one table of return codes of every export) and its mirror
        /// <see cref="Native.Result"/> agree in name and value: <c>VLQT_OK</c> is <c>Ok</c>, <c>VLQT_ERR_UNKNOWN_ID</c> is <c>UnknownId</c>.
        /// </summary>
        [Test]
        public void ResultCodesAgree()
        {
            var header = ParseResultEnum(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vlqt.h")));
            Assert.That(header, Is.Not.Empty, "no vlqt_result members parsed from vlqt.h");
            var mirror = Enum.GetValues<Native.Result>().ToDictionary(r => r.ToString(), r => (long)(int)r);
            var errors = CompareResults(header, mirror);
            Assert.That(errors, Is.Empty, "vlqt.h vlqt_result <-> Native.Result:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        /// <summary>The result comparer finds a changed value, a code only in the header and a code only in the mirror.</summary>
        [Test]
        public void ResultComparerFindsEachKindOfMismatch()
        {
            var header = ParseResultEnum("typedef enum vlqt_result {\n    VLQT_OK = 0,\n    VLQT_ERR_NULL_HANDLE = -1, /* c */\n    VLQT_ERR_IO = -14\n} vlqt_result;\n");
            Assert.That(header, Is.EquivalentTo(new Dictionary<string, long> { ["Ok"] = 0, ["NullHandle"] = -1, ["Io"] = -14 }));
            var mirror = new Dictionary<string, long>(header);
            Assert.That(CompareResults(header, mirror), Is.Empty);

            var changed = new Dictionary<string, long>(mirror) { ["Io"] = -15 };
            Assert.That(CompareResults(header, changed), Has.Count.EqualTo(1).And.One.Contains("!="));

            var onlyInMirror = new Dictionary<string, long>(mirror) { ["Extra"] = -99 };
            Assert.That(CompareResults(header, onlyInMirror), Has.Count.EqualTo(1).And.One.Contains("missing in vlqt.h"));

            var onlyInHeader = new Dictionary<string, long>(mirror);
            onlyInHeader.Remove("NullHandle");
            Assert.That(CompareResults(header, onlyInHeader), Has.Count.EqualTo(1).And.One.Contains("missing in Native.Result"));
        }

        // The members of "typedef enum vlqt_result { ... } vlqt_result;" with the C# name (VLQT_OK -> Ok, VLQT_ERR_UNKNOWN_ID -> UnknownId).
        private static Dictionary<string, long> ParseResultEnum(string header)
        {
            var block = Regex.Match(header, @"typedef\s+enum\s+vlqt_result\s*\{(?<body>.*?)\}\s*vlqt_result\s*;", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            Assert.That(block.Success, "typedef enum vlqt_result not found in vlqt.h");
            var result = new Dictionary<string, long>();
            foreach (var line in block.Groups["body"].Value.Split('\n'))
            {
                var m = s_enumMember.Match(line);
                if (!m.Success)
                    continue;
                var name = m.Groups[1].Value;
                name = name.StartsWith("ERR_", StringComparison.Ordinal) ? name["ERR_".Length..] : name;
                var pascal = string.Concat(name.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
                Assert.That(result.TryAdd(pascal, long.Parse(m.Groups[2].Value)), $"duplicate vlqt_result member {pascal}");
            }
            return result;
        }

        private static List<string> CompareResults(Dictionary<string, long> header, Dictionary<string, long> mirror)
        {
            var errors = new List<string>();
            foreach (var (name, value) in header)
            {
                if (!mirror.TryGetValue(name, out var v))
                    errors.Add($"{name} missing in Native.Result");
                else if (v != value)
                    errors.Add($"{name}: vlqt.h {value} != Native.Result {v}");
            }
            foreach (var name in mirror.Keys.Where(n => !header.ContainsKey(n)))
                errors.Add($"Native.Result has {name}, missing in vlqt.h");
            return errors;
        }

        /// <summary>The comparer finds a changed value, a constant only in the header and a constant only in the mirror.</summary>
        [Test]
        public void ComparerFindsEachKindOfMismatch()
        {
            // Sample data, so the self-test does not depend on the state of the real files.
            var header = new Dictionary<string, long> { ["OP_CREATE"] = 1, ["HEADER_SIZE"] = 4 };
            var mirror = new Dictionary<string, long>(header);
            Assert.That(Compare("protocol.h", "Protocol.cs", header, mirror), Is.Empty);
            var first = "HEADER_SIZE";

            var changed = new Dictionary<string, long>(mirror) { [first] = mirror[first] + 1000 };
            Assert.That(Compare("protocol.h", "Protocol.cs", header, changed), Has.Count.EqualTo(1).And.One.Contains("!="));

            var onlyInMirror = new Dictionary<string, long>(mirror) { ["DRIFT_SELFTEST_EXTRA"] = 1 };
            Assert.That(Compare("protocol.h", "Protocol.cs", header, onlyInMirror), Has.Count.EqualTo(1).And.One.Contains("missing in protocol.h"));

            var onlyInHeader = new Dictionary<string, long>(mirror);
            onlyInHeader.Remove(first);
            Assert.That(Compare("protocol.h", "Protocol.cs", header, onlyInHeader), Has.Count.EqualTo(1).And.One.Contains("missing in Protocol.cs"));
        }

        private static Dictionary<string, long> ParseHeader(string headerName, string[] lines, List<string> errors)
        {
            var result = new Dictionary<string, long>();
            for (int i = 0; i < lines.Length; i++)
            {
                var m = s_define.Match(lines[i]);
                if (!m.Success)
                    m = s_enumMember.Match(lines[i]);
                if (!m.Success)
                    continue;
                if (!result.TryAdd(m.Groups[1].Value, long.Parse(m.Groups[2].Value)))
                    errors.Add($"{headerName}:{i + 1}: duplicate VLQT_{m.Groups[1].Value}");
            }
            return result;
        }

        // Top-level consts -> UPPER_SNAKE, nested class consts -> GROUP_UPPER_SNAKE.
        private static Dictionary<string, long> ReflectMirror(Type root)
        {
            var result = new Dictionary<string, long>();
            void Add(Type t, string prefix)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
                    result[prefix + ToUpperSnake(f.Name)] = Convert.ToInt64(f.GetRawConstantValue());
            }
            Add(root, "");
            foreach (var nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                Add(nested, ToUpperSnake(nested.Name) + "_"); // WindowMode -> WINDOW_MODE_
            return result;
        }

        private static List<string> Compare(string headerName, string mirrorName, Dictionary<string, long> header, Dictionary<string, long> mirror)
        {
            var errors = new List<string>();
            foreach (var (name, value) in header)
            {
                if (!mirror.TryGetValue(name, out var v))
                    errors.Add($"VLQT_{name} missing in {mirrorName}");
                else if (v != value)
                    errors.Add($"VLQT_{name}: {headerName} {value} != {mirrorName} {v}");
            }
            foreach (var name in mirror.Keys.Where(n => !header.ContainsKey(n)))
                errors.Add($"{mirrorName} has VLQT_{name}, missing in {headerName}");
            return errors;
        }

        private static string ToUpperSnake(string pascal)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pascal.Length; i++)
            {
                if (i > 0 && char.IsUpper(pascal[i]) && (char.IsLower(pascal[i - 1]) || char.IsDigit(pascal[i - 1])))
                    sb.Append('_');
                sb.Append(char.ToUpperInvariant(pascal[i]));
            }
            return sb.ToString();
        }
    }
}
