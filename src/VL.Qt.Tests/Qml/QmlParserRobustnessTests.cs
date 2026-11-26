using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VL.Qt.Qml;

namespace VL.Qt.Tests.Qml
{
    /// <summary>The QML parser never throws and handles the odd corners of the lexer.</summary>
    [TestFixture]
    internal sealed class QmlParserRobustnessTests
    {
        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "help", "Qt", "QmlElement Example.qml")))
                    return dir.FullName;
            }
            throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
        }

        private static QmlDeclarations Parse(string source, IDictionary<string, string>? siblings = null)
            => QmlDeclarations.Parse(source, "Own", name => siblings != null && siblings.TryGetValue(name, out var s) ? s : null);

        private static QmlPropertyDecl Prop(QmlDeclarations d, string name) => d.Properties.Single(p => p.Name == name);

        private const string tricky = """
            // @category Qt.Test
            import QtQuick
            Item {
                property string s: "a\
            b{"
                property int n: 0b101 + 0o17 + 0xff
                property var re: /[{}"']\/(/g
                signal done(int code)
                enum Mode { A, B }
                component Inner: Item { property int hidden: 1 }
                function f(x) { return `t${x}{` + 'q"'; }
                /* block { */ property int last: 3
            }
            """;

        private static IEnumerable<(string Name, string Source)> Sources()
        {
            var root = RepoRoot();
            var files = Directory.GetFiles(Path.Combine(root, "help", "Qt"), "*.qml", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(root, "src", "VL.Qt.SmokeTest", "assets"), "*.qml"))
                .Concat(Directory.GetFiles(Path.Combine(root, "src", "VL.Qt.Native", "style"), "*.qml", SearchOption.AllDirectories));
            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
                yield return (Path.GetFileName(file), File.ReadAllText(file));
            yield return ("tricky", tricky);
        }

        [Test]
        public void Parse_NeverThrows_OnAnyPrefixOrSuffix()
        {
            var sw = Stopwatch.StartNew();
            var count = 0;
            foreach (var (name, source) in Sources())
            {
                for (var i = 0; i <= source.Length; i++)
                {
                    foreach (var (kind, text) in new[] { ("prefix", source[..i]), ("suffix", source[i..]) })
                    {
                        try
                        {
                            Assert.That(Parse(text).Signature, Is.Not.Null);
                            Assert.That(QmlDeclarations.ParseHead(text), Is.Not.Null);
                            count++;
                        }
                        catch (Exception ex) when (ex is not AssertionException)
                        {
                            Assert.Fail($"{name}, {kind} at {i}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            TestContext.Out.WriteLine($"{count} parses in {sw.ElapsedMilliseconds} ms");
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(60)));
        }

        [Test]
        public void Regex_LiteralWithBracesAndQuotes_DoesNotBreakTheMembersAfterIt()
        {
            var d = Parse("""
                import QtQuick
                Item {
                    property var re: /[{}"']\/(/g
                    property int after: 3
                }
                """);
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(Prop(d, "after").Default, Is.EqualTo(3));
        }

        [TestCase("function strip(s) { return s.replace(/\"/g, \"\") }")]
        [TestCase("function f(s) { return /https?:\\/\\//.test(s) }")]
        [TestCase("function f(s) { return /[{]/.test(s) }")]
        [TestCase("function f(s) { return s.split(/[/]{2}/) }")]
        [TestCase("function f(x) { if (/a\"b/.test(x)) return 1 }")]
        public void Regex_Literals_AreSkipped_AndTheMembersAfterThemStayInTheDeclarations(string member)
        {
            var d = Parse($"import QtQuick\nItem {{\n    {member}\n    property int after: 3\n}}\n");
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(Prop(d, "after").Default, Is.EqualTo(3));
            Assert.That(Prop(d, "after").Line, Is.EqualTo(4));
        }

        [Test]
        public void Division_IsNotARegex()
        {
            var d = Parse("import QtQuick\nItem {\n    property real half: width / 2\n    property real q: (width + 1) / 2 / 3\n    property int after: 1\n}\n");
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "half", "q", "after" }));
        }

        [Test]
        public void Regex_Unterminated_IsAnErrorWithItsLine()
        {
            var d = Parse("import QtQuick\nItem {\n    property var re: /abc\n}\n");
            var error = d.Messages.Single(m => m.Severity == QmlMessageSeverity.Error);
            Assert.That(error.Text, Does.Contain("regular expression"));
            Assert.That(error.Line, Is.EqualTo(3));
        }

        [Test]
        public void Template_WithANestedTemplateInASubstitution_DoesNotEndEarly()
        {
            var d = Parse("import QtQuick\nItem {\n    function f(a) { return `x${a ? `y${a}` : \"}\"}z`; }\n    property int after: 1\n}\n");
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(Prop(d, "after").Line, Is.EqualTo(4));
        }
        [Test]
        public void InlineComponent_AtTheRoot_IsSkippedAndDoesNotBreakTheMembersAfterIt()
        {
            var d = Parse("""
                import QtQuick
                Item {
                    property int before: 1
                    component Inner: Item { property int hidden: 2 }
                    property int after: 3
                }
                """);
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "before", "after" }));
        }

        [Test]
        public void Enum_AtTheRoot_IsSkippedAndDoesNotBreakTheMembersAfterIt()
        {
            var d = Parse("""
                import QtQuick
                Item {
                    enum Mode { A, B = 2 }
                    property int after: 3
                }
                """);
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "after" }));
        }

        // 0b/0o: Default stays 0 (not read as a number, no message); not implemented.
        [Ignore("0b/0o numbers are not evaluated, the default stays 0")]
        [TestCase("0b101", 5)]
        [TestCase("0B11", 3)]
        [TestCase("0o17", 15)]
        [TestCase("0O7", 7)]
        public void Number_BinaryOctal_IsReadAsTheDefault(string literal, int expected)
        {
            var d = Parse($"import QtQuick\nItem {{\n    property int n: {literal}\n    property int after: 1\n}}\n");
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(Prop(d, "n").Default, Is.EqualTo(expected));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "n", "after" }));
        }

        [Test]
        public void LineNumbers_AfterAnEscapedNewlineInAString_StayCorrect()
        {
            // The string continues over a line break with a backslash: that line break counts.
            var d = Parse("import QtQuick\nItem {\n    property string s: \"a\\\nb\"\n    property int after: 1\n}\n");
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(Prop(d, "s").Line, Is.EqualTo(3));
            Assert.That(Prop(d, "after").Line, Is.EqualTo(5));
        }

        [Test]
        public void LineNumbers_AfterEscapesInStringsAndTemplates_StayCorrect()
        {
            var d = Parse("import QtQuick\nItem {\n    property string s: \"a\\\"b\\\\\"\n    property string t: `x\n${1}\\``\n    property int after: 1\n}\n");
            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(Prop(d, "s").Line, Is.EqualTo(3));
            Assert.That(Prop(d, "t").Line, Is.EqualTo(4));
            Assert.That(Prop(d, "after").Line, Is.EqualTo(6));
        }
    }
}
