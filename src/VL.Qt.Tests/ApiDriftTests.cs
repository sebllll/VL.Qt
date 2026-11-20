using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace VL.Qt.Tests
{
    /// <summary>
    /// The exports of QtNative (the <c>VLQT_API</c> declarations of vlqt.h, vlqt_test.h and the testkit's testkit.h) and the
    /// <c>[DllImport]</c> declarations in C# agree in name and parameter count: every C# entry point exists in its header, and
    /// every export is declared in C# (<c>Native</c> for vlqt.h, <c>NativeTest</c> for the other two). A missing or misspelled
    /// export would fail only when the call runs (<see cref="EntryPointNotFoundException"/>), a parameter added on one side only
    /// not at all. Needs no GPU and no QtNative.
    /// </summary>
    [TestFixture]
    public sealed class ApiDriftTests
    {
        private const string product = "vlqt";
        private const string testkit = "vlqt_testkit";

        private static readonly Regex s_declaration = new(@"\b(?:VLQT_API|VLQT_TESTKIT_API)\s+[^;()]*?\b(\w+)\s*\(([^;]*)\)\s*;", RegexOptions.CultureInvariant);
        private static readonly Regex s_comments = new(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex s_preprocessor = new(@"^[ \t]*#.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        [Test]
        public void ProductHeaderAndNativeAgree()
            => AssertAgree("vlqt.h <-> Native", ReadHeaders("vlqt.h"), ReflectImports(typeof(Native).Assembly.GetTypes(), product));

        [Test]
        public void TestHeaderAndNativeTestAgree()
            => AssertAgree("vlqt_test.h <-> NativeTest", ReadHeaders("vlqt_test.h"), ReflectImports(new[] { typeof(NativeTest) }, product));

        [Test]
        public void TestkitHeaderAndNativeTestAgree()
            => AssertAgree("testkit.h <-> NativeTest", ReadHeaders("testkit.h"), ReflectImports(new[] { typeof(NativeTest) }, testkit));

        private static void AssertAgree(string what, Dictionary<string, int> exports, Dictionary<string, HashSet<int>> imports)
        {
            Assert.That(exports, Is.Not.Empty, "no declarations parsed for " + what);
            Assert.That(imports, Is.Not.Empty, "no [DllImport] found for " + what);
            var errors = Compare(exports, imports);
            Assert.That(errors, Is.Empty, what + ":" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        /// <summary>The comparer finds a changed parameter count, an import without export and an export without import.</summary>
        [Test]
        public void ComparerFindsEachKindOfMismatch()
        {
            // Sample data, so the self-test does not depend on the state of the real files.
            var exports = new Dictionary<string, int> { ["vlqt_a"] = 2, ["vlqt_b"] = 0 };
            var imports = new Dictionary<string, HashSet<int>> { ["vlqt_a"] = new HashSet<int> { 2 }, ["vlqt_b"] = new HashSet<int> { 0 } };
            Assert.That(Compare(exports, imports), Is.Empty);
            var first = "vlqt_a";

            var otherCount = new Dictionary<string, HashSet<int>>(imports) { [first] = new HashSet<int> { imports[first].First() + 1 } };
            Assert.That(Compare(exports, otherCount), Has.Count.EqualTo(1).And.One.Contains("parameter"));

            var misspelled = new Dictionary<string, HashSet<int>>(imports) { ["vlqt_drift_selftest_extra"] = new HashSet<int> { 0 } };
            Assert.That(Compare(exports, misspelled), Has.Count.EqualTo(1).And.One.Contains("not declared in a header"));

            var notImported = new Dictionary<string, HashSet<int>>(imports);
            notImported.Remove(first);
            Assert.That(Compare(exports, notImported), Has.Count.EqualTo(1).And.One.Contains("no [DllImport]"));
        }

        [Test]
        public void ParserReadsMultiLineAndCommentedDeclarations()
        {
            var parsed = ParseHeaders(new[]
            {
                "#define VLQT_API __attribute__((visibility(\"default\")))\n" +
                "/* VLQT_API int vlqt_commented(void); */\n" +
                "VLQT_API int   vlqt_a(void);\n" +
                "VLQT_API void* vlqt_b(float x, // comment, with comma\n" +
                "                      const char* title, void* const* windows);\n" +
                "VLQT_API const char* vlqt_c(vlqt_log_fn log);\n" +
                "VLQT_TESTKIT_API int vlqt_d(void* a, int b);\n"
            });
            Assert.That(parsed, Is.EquivalentTo(new Dictionary<string, int> { ["vlqt_a"] = 0, ["vlqt_b"] = 3, ["vlqt_c"] = 1, ["vlqt_d"] = 2 }));
        }

        private static List<string> Compare(Dictionary<string, int> exports, Dictionary<string, HashSet<int>> imports)
        {
            var errors = new List<string>();
            foreach (var (name, counts) in imports.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!exports.TryGetValue(name, out var exported))
                    errors.Add($"{name} is imported in C# but not declared in a header");
                else
                    foreach (var count in counts.Where(c => c != exported).OrderBy(c => c))
                        errors.Add($"{name}: the header has {exported} parameter(s), a [DllImport] {count}");
            }
            foreach (var name in exports.Keys.Where(n => !imports.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal))
                errors.Add($"{name} is exported by a header but has no [DllImport] in C#");
            return errors;
        }

        // The header is copied to the output directory (VL.Qt.Tests.csproj).
        private static Dictionary<string, int> ReadHeaders(string fileName)
            => ParseHeaders(new[] { File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName)) });

        // Name -> parameter count of every VLQT_API / VLQT_TESTKIT_API declaration; a name declared twice is an error.
        private static Dictionary<string, int> ParseHeaders(IEnumerable<string> headers)
        {
            var result = new Dictionary<string, int>();
            foreach (var header in headers)
            {
                var text = s_preprocessor.Replace(s_comments.Replace(header, " "), "");
                foreach (Match m in s_declaration.Matches(text))
                {
                    if (!result.TryAdd(m.Groups[1].Value, CountParameters(m.Groups[2].Value)))
                        throw new InvalidOperationException("vlqt headers declare " + m.Groups[1].Value + " twice");
                }
            }
            return result;
        }

        // Number of parameters of a C or C# parameter list: commas at nesting depth 0, "" and "void" are none.
        private static int CountParameters(string list)
        {
            list = list.Trim();
            if (list.Length == 0 || list == "void")
                return 0;
            int count = 1, depth = 0;
            foreach (var c in list)
            {
                if (c is '(' or '[' or '{' or '<')
                    depth++;
                else if (c is ')' or ']' or '}' or '>')
                    depth--;
                else if (c == ',' && depth == 0)
                    count++;
            }
            return count;
        }

        private static void Add(Dictionary<string, HashSet<int>> imports, string name, int count)
        {
            if (!imports.TryGetValue(name, out var counts))
                imports[name] = counts = new HashSet<int>();
            counts.Add(count);
        }

        // The [DllImport(library)] methods of some types: entry point -> parameter counts (a library function may be imported
        // more than once, e.g. with different parameter types).
        private static Dictionary<string, HashSet<int>> ReflectImports(IEnumerable<Type> types, string library)
        {
            var result = new Dictionary<string, HashSet<int>>();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(all))
                {
                    var import = method.GetCustomAttribute<DllImportAttribute>();
                    if (import == null || import.Value != library)
                        continue;
                    var name = string.IsNullOrEmpty(import.EntryPoint) ? method.Name : import.EntryPoint;
                    Add(result, name, method.GetParameters().Length);
                }
            }
            return result;
        }
    }
}
