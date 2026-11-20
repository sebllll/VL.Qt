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
    /// protocol.h (single source of truth) and its hand-written mirror Protocol.cs agree in name and value. Needs no GPU
    /// and no QtNative: only the header (copied to the output directory) and reflection.
    /// </summary>
    [TestFixture]
    public sealed class ProtocolDriftTests
    {
        private static readonly Regex s_define = new(@"^\s*#define\s+VLQT_(\w+)\s+(-?\d+)\s*(/\*.*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex s_enumMember = new(@"^\s*VLQT_(\w+)\s*=\s*(-?\d+)\s*,?\s*(/\*.*)?$", RegexOptions.CultureInvariant);

        internal static string HeaderPath => Path.Combine(AppContext.BaseDirectory, "protocol.h");

        [Test]
        public void HeaderAndMirrorAgree()
        {
            var errors = new List<string>();
            var header = ParseHeader(File.ReadAllLines(HeaderPath), errors);
            Assert.That(header, Is.Not.Empty, "no constants parsed from protocol.h");
            errors.AddRange(Compare(header, ReflectMirror(typeof(Protocol))));
            Assert.That(errors, Is.Empty, "protocol.h <-> Protocol.cs:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        /// <summary>The comparer finds a changed value, a constant only in the header and a constant only in the mirror.</summary>
        [Test]
        public void ComparerFindsEachKindOfMismatch()
        {
            // Sample data, so the self-test does not depend on the state of the real files.
            var header = new Dictionary<string, long> { ["OP_CREATE"] = 1, ["HEADER_SIZE"] = 4 };
            var mirror = new Dictionary<string, long>(header);
            Assert.That(Compare(header, mirror), Is.Empty);
            var first = "HEADER_SIZE";

            var changed = new Dictionary<string, long>(mirror) { [first] = mirror[first] + 1000 };
            Assert.That(Compare(header, changed), Has.Count.EqualTo(1).And.One.Contains("!="));

            var onlyInMirror = new Dictionary<string, long>(mirror) { ["DRIFT_SELFTEST_EXTRA"] = 1 };
            Assert.That(Compare(header, onlyInMirror), Has.Count.EqualTo(1).And.One.Contains("missing in protocol.h"));

            var onlyInHeader = new Dictionary<string, long>(mirror);
            onlyInHeader.Remove(first);
            Assert.That(Compare(header, onlyInHeader), Has.Count.EqualTo(1).And.One.Contains("missing in Protocol.cs"));
        }

        private static Dictionary<string, long> ParseHeader(string[] lines, List<string> errors)
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
                    errors.Add($"protocol.h:{i + 1}: duplicate VLQT_{m.Groups[1].Value}");
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

        private static List<string> Compare(Dictionary<string, long> header, Dictionary<string, long> mirror)
        {
            var errors = new List<string>();
            foreach (var (name, value) in header)
            {
                if (!mirror.TryGetValue(name, out var v))
                    errors.Add($"VLQT_{name} missing in Protocol.cs");
                else if (v != value)
                    errors.Add($"VLQT_{name}: protocol.h {value} != Protocol.cs {v}");
            }
            foreach (var name in mirror.Keys.Where(n => !header.ContainsKey(n)))
                errors.Add($"Protocol.cs has VLQT_{name}, missing in protocol.h");
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
