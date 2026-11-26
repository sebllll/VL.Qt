using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Qml;

namespace VL.Qt.Tests.Qml
{
    /// <summary>The parser of the root declarations of a .qml file.</summary>
    [TestFixture]
    internal sealed class QmlDeclarationsTests
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

        private static void AssertColor(Color4 expected, object actual)
        {
            var c = (Color4)actual;
            Assert.That(c.R, Is.EqualTo(expected.R).Within(1e-4));
            Assert.That(c.G, Is.EqualTo(expected.G).Within(1e-4));
            Assert.That(c.B, Is.EqualTo(expected.B).Within(1e-4));
            Assert.That(c.A, Is.EqualTo(expected.A).Within(1e-4));
        }

        // ---- the shipped example files ----------------------------------------------------------------------------------------

        [Test]
        public void ExampleFile_HasThePropertiesAndTheSignalOfTheGauge()
        {
            var d = Parse(File.ReadAllText(Path.Combine(RepoRoot(), "help", "Qt", "QmlElement Example.qml")));

            Assert.That(d.HasErrors, Is.False);
            Assert.That(d.Messages, Is.Empty);
            Assert.That(d.RootType, Is.EqualTo("Item"));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "value", "accent", "label", "percent" }));

            var value = Prop(d, "value");
            Assert.That(value.Kind, Is.EqualTo(QmlValueKind.Real));
            Assert.That(value.Initializer, Is.EqualTo(QmlInitializer.Literal));
            Assert.That(value.Default, Is.EqualTo(0.5f));
            Assert.That(value.Summary, Is.EqualTo("In: set from the patch. 0..1, values outside are clamped."));
            Assert.That(value.DeclaredIn, Is.Null);

            var accent = Prop(d, "accent");
            Assert.That(accent.Kind, Is.EqualTo(QmlValueKind.Color));
            Assert.That(accent.Initializer, Is.EqualTo(QmlInitializer.Literal));
            AssertColor(new Color4(1f, 0x7a / 255f, 0x3d / 255f, 1f), accent.Default);

            var label = Prop(d, "label");
            Assert.That(label.Default, Is.EqualTo("Load"));

            // A binding keeps the type default and spans two comment lines.
            var percent = Prop(d, "percent");
            Assert.That(percent.Kind, Is.EqualTo(QmlValueKind.Int));
            Assert.That(percent.Initializer, Is.EqualTo(QmlInitializer.Binding));
            Assert.That(percent.Default, Is.EqualTo(0));
            Assert.That(percent.Summary, Does.StartWith("Out: the percent the bar shows right now.").And.EndWith("QML-side change."));
            Assert.That(percent.Summary, Does.Contain("(an animation), so"));

            var milestone = d.Signals.Single();
            Assert.That(milestone.Name, Is.EqualTo("milestone"));
            Assert.That(milestone.Args, Is.EqualTo(new[] { new QmlSignalArg("percent", "int", QmlValueKind.Int) }));
            Assert.That(milestone.Shape, Is.EqualTo(QmlSignalShape.Values));
            Assert.That(milestone.Summary, Does.StartWith("Out: emitted whenever the bar passes"));

            // Members of the children (Text, Rectangle with its own `property real shown`) are not root members.
            Assert.That(d.Properties.Any(p => p.Name == "shown"), Is.False);
        }

        [Test]
        public void SmokeTestTypesFile_HasEveryValueKindWithLiteralAndBinding()
        {
            var d = Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "VL.Qt.SmokeTest", "assets", "QmlTypes.qml")));

            Assert.That(d.Messages, Is.Empty);
            Assert.That(d.Properties, Has.Length.EqualTo(22));
            var kinds = new Dictionary<string, QmlValueKind>
            {
                ["r"] = QmlValueKind.Real, ["i"] = QmlValueKind.Int, ["b"] = QmlValueKind.Bool, ["s"] = QmlValueKind.String,
                ["c"] = QmlValueKind.Color, ["v2"] = QmlValueKind.Vector2D, ["v3"] = QmlValueKind.Vector3D, ["v4"] = QmlValueKind.Vector4D,
                ["box"] = QmlValueKind.Rect, ["pt"] = QmlValueKind.Point, ["sz"] = QmlValueKind.Size,
            };
            foreach (var (name, kind) in kinds)
            {
                var input = Prop(d, name);
                Assert.That(input.Kind, Is.EqualTo(kind), name);
                Assert.That(input.Initializer, Is.EqualTo(QmlInitializer.Literal), name);
                var output = Prop(d, name + "Out");
                Assert.That(output.Kind, Is.EqualTo(kind), name + "Out");
                Assert.That(output.Initializer, Is.EqualTo(QmlInitializer.Binding), name + "Out");
            }

            AssertColor(new Color4(0f, 0f, 0f, 1f), Prop(d, "c").Default);
            Assert.That(Prop(d, "v2").Default, Is.EqualTo(Vector2.Zero));
            Assert.That(Prop(d, "v3").Default, Is.EqualTo(Vector3.Zero));
            Assert.That(Prop(d, "v4").Default, Is.EqualTo(Vector4.Zero));
            Assert.That(Prop(d, "box").Default, Is.EqualTo(new RectangleF(0, 0, 0, 0)));
            Assert.That(Prop(d, "pt").Default, Is.EqualTo(Vector2.Zero));
            Assert.That(Prop(d, "sz").Default, Is.EqualTo(Vector2.Zero));
            Assert.That(Prop(d, "b").Default, Is.EqualTo(false));
            Assert.That(Prop(d, "s").Default, Is.EqualTo(string.Empty));
            Assert.That(d.Signals.Select(x => x.Name), Is.EqualTo(new[] { "located" }));
            Assert.That(d.Signals[0].Args, Is.EqualTo(new[] { new QmlSignalArg("p", "point", QmlValueKind.Point) }));
        }

        // ---- literals ---------------------------------------------------------------------------------------------------------

        [Test]
        public void Literals_AreRecognisedPerKind()
        {
            var d = Parse("""
                import QtQuick
                Item {
                    property real a: -2.5
                    property double b: 1e3
                    property int c: 0x10
                    property int d: +7
                    property bool e: true
                    property string f: 'it\'s "quoted"\n'
                    property color g: "#8040ff80"
                    property color h: "#f00"
                    property color i: "steelblue"
                    property color j: Qt.rgba(0.5, 0.25, 1, 0.5)
                    property color k: Qt.rgb(1, 0, 0)
                    property vector2d l: Qt.vector2d(1, -2)
                    property vector3d m: Qt.vector3d(1, 2, 3)
                    property vector4d n: Qt.vector4d(1, 2, 3, 4)
                    property rect o: Qt.rect(1, 2, 30, 40)
                    property point pt: Qt.point(1.5, -2)
                    property size sz: Qt.size(30, 40)
                    property point wrong: Qt.size(1, 2)
                    property color p: "Transparent"
                    property color q: "darkgrey"
                    property real r: 5;
                }
                """);

            Assert.That(d.Messages, Is.Empty);
            Assert.That(d.Properties.Where(p => p.Initializer != QmlInitializer.Literal).Select(p => p.Name), Is.EqualTo(new[] { "wrong" }));
            Assert.That(Prop(d, "a").Default, Is.EqualTo(-2.5f));
            Assert.That(Prop(d, "b").Default, Is.EqualTo(1000f));
            Assert.That(Prop(d, "c").Default, Is.EqualTo(16));
            Assert.That(Prop(d, "d").Default, Is.EqualTo(7));
            Assert.That(Prop(d, "e").Default, Is.EqualTo(true));
            Assert.That(Prop(d, "f").Default, Is.EqualTo("it's \"quoted\"\n"));
            // #AARRGGBB: alpha 0x80, red 0x40, green 0xff, blue 0x80
            AssertColor(new Color4(0x40 / 255f, 1f, 0x80 / 255f, 0x80 / 255f), Prop(d, "g").Default);
            AssertColor(new Color4(1f, 0f, 0f, 1f), Prop(d, "h").Default);
            AssertColor(new Color4(70 / 255f, 130 / 255f, 180 / 255f, 1f), Prop(d, "i").Default);
            AssertColor(new Color4(0.5f, 0.25f, 1f, 0.5f), Prop(d, "j").Default);
            AssertColor(new Color4(1f, 0f, 0f, 1f), Prop(d, "k").Default);
            Assert.That(Prop(d, "l").Default, Is.EqualTo(new Vector2(1, -2)));
            Assert.That(Prop(d, "m").Default, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(Prop(d, "n").Default, Is.EqualTo(new Vector4(1, 2, 3, 4)));
            Assert.That(Prop(d, "o").Default, Is.EqualTo(new RectangleF(1, 2, 30, 40)));
            Assert.That(Prop(d, "pt").Default, Is.EqualTo(new Vector2(1.5f, -2)));
            Assert.That(Prop(d, "sz").Default, Is.EqualTo(new Vector2(30, 40)));
            Assert.That(Prop(d, "wrong").Default, Is.EqualTo(Vector2.Zero)); // a size is no point: a binding
            AssertColor(new Color4(0f, 0f, 0f, 0f), Prop(d, "p").Default);
            Assert.That(((Color4)Prop(d, "q").Default).R, Is.EqualTo(169 / 255f).Within(1e-4));
            Assert.That(Prop(d, "r").Default, Is.EqualTo(5f));
        }

        [Test]
        public void SvgColourNames_MatchTheCssTable()
        {
            var d = Parse("""
                Item {
                    property color green: "green"
                    property color lime: "lime"
                    property color grey: "grey"
                    property color purple: "rebeccapurple"
                    property color unknown: "notacolour"
                }
                """);

            AssertColor(new Color4(0f, 128 / 255f, 0f, 1f), Prop(d, "green").Default);
            AssertColor(new Color4(0f, 1f, 0f, 1f), Prop(d, "lime").Default);
            AssertColor(new Color4(128 / 255f, 128 / 255f, 128 / 255f, 1f), Prop(d, "grey").Default);
            AssertColor(new Color4(0x66 / 255f, 0x33 / 255f, 0x99 / 255f, 1f), Prop(d, "purple").Default);
            Assert.That(Prop(d, "unknown").Initializer, Is.EqualTo(QmlInitializer.Binding));
            AssertColor(new Color4(0f, 0f, 0f, 0f), Prop(d, "unknown").Default);
        }

        [Test]
        public void BindingsAndMissingInitializers_GetTheTypeDefault()
        {
            var d = Parse("""
                Item {
                    property real none
                    property real sum: 1 + 2
                    property real ref: other.width
                    property real call: Math.max(1, 2)
                    property int fraction: 2.5
                    property bool cond: width > 100
                    property string tr: qsTr("Hello")
                    property color computed: Qt.rgba(r, 0, 0, 1)
                    property color outOfRange: Qt.rgba(2, 0, 0, 1)
                    property vector2d wrong: Qt.vector3d(1, 2, 3)
                    property rect expr: Qt.rect(0, 0, width, 10)
                }
                """);

            Assert.That(Prop(d, "none").Initializer, Is.EqualTo(QmlInitializer.None));
            Assert.That(Prop(d, "none").Default, Is.EqualTo(0f));
            foreach (var name in new[] { "sum", "ref", "call", "fraction", "cond", "tr", "computed", "outOfRange", "wrong", "expr" })
            {
                var p = Prop(d, name);
                Assert.That(p.Initializer, Is.EqualTo(QmlInitializer.Binding), name);
                Assert.That(p.Default, Is.EqualTo(QmlTypes.TypeDefault(p.Kind)), name);
            }
        }

        // ---- structure --------------------------------------------------------------------------------------------------------

        [Test]
        public void BracesInCommentsAndStrings_DoNotConfuseTheScan()
        {
            var d = Parse("""
                import QtQuick
                // { an open brace in the head comment
                Item {
                    /* } block comment with a brace
                       over two lines { */
                    property string text: "}{ not a block // not a comment"
                    property real after: 3 // trailing { brace
                    // } above the signal
                    signal done(string why) // trailing }
                    property string single: '{'
                    Text { text: "}" }
                    property int last: 9
                }
                """);

            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "text", "after", "single", "last" }));
            Assert.That(Prop(d, "text").Default, Is.EqualTo("}{ not a block // not a comment"));
            Assert.That(Prop(d, "after").Default, Is.EqualTo(3f));
            Assert.That(Prop(d, "single").Default, Is.EqualTo("{"));
            Assert.That(d.Signals.Single().Summary, Is.EqualTo("} above the signal"));
            // The trailing comment belongs to its own line: it is no summary of the next declaration.
            Assert.That(Prop(d, "single").Summary, Is.Null);
        }

        [Test]
        public void MultiLineBindings_AndNestedObjects_AreSkippedAsOneMember()
        {
            var d = Parse("""
                Item {
                    property real a: 1 +
                        2
                    property real b: cond
                        ? 1
                        : 2
                    property real c: Math.max(
                        1,
                        2)
                    property var obj: ({
                        x: 1,
                        y: 2
                    })
                    property real afterObject: 4
                    onValueChanged: {
                        property real inner: 5
                        signal innerSignal()
                    }
                    function helper(x)
                    {
                        return x * 2
                    }
                    Rectangle
                    {
                        property real nested: 6
                    }
                    states: [
                        State { name: "a" },
                        State { name: "b" }
                    ]
                    readonly property real z: a
                        + b
                    property real last: 7
                }
                """);

            Assert.That(d.HasErrors, Is.False, string.Join("; ", d.Messages));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "a", "b", "c", "afterObject", "z", "last" }));
            Assert.That(Prop(d, "a").Initializer, Is.EqualTo(QmlInitializer.Binding));
            Assert.That(Prop(d, "afterObject").Default, Is.EqualTo(4f));
            Assert.That(Prop(d, "z").IsReadOnly, Is.True);
            Assert.That(Prop(d, "last").Default, Is.EqualTo(7f));
            Assert.That(d.Signals, Is.Empty);
            // `var` is the only declaration without a pin.
            Assert.That(d.Messages.Select(m => m.Severity), Is.EqualTo(new[] { QmlMessageSeverity.Info }));
        }

        [Test]
        public void Signals_WithZeroOneAndTwoArguments()
        {
            var d = Parse("""
                Item {
                    // Fired once.
                    signal bang
                    signal bang2()
                    signal one(int count)
                    signal two(real x, string label)
                    signal typed(a: int, b: color)
                    signal generic(var payload, list<int> items)
                    signal Event(int a)
                }
                """);

            Assert.That(d.Signals.Select(s => s.Name), Is.EqualTo(new[] { "bang", "bang2", "one", "two", "typed", "generic", "Event" }));
            var bang = d.Signals[0];
            Assert.That(bang.Args, Is.Empty);
            Assert.That(bang.Shape, Is.EqualTo(QmlSignalShape.Bang));
            Assert.That(bang.Summary, Is.EqualTo("Fired once."));
            Assert.That(d.Signals[1].Shape, Is.EqualTo(QmlSignalShape.Bang));
            Assert.That(d.Signals[1].Summary, Is.Null);
            Assert.That(d.Signals[2].Shape, Is.EqualTo(QmlSignalShape.Values));
            Assert.That(d.Signals[3].Shape, Is.EqualTo(QmlSignalShape.Signals));
            Assert.That(d.Signals[3].Args, Is.EqualTo(new[] { new QmlSignalArg("x", "real", QmlValueKind.Real), new QmlSignalArg("label", "string", QmlValueKind.String) }));
            Assert.That(d.Signals[4].Args, Is.EqualTo(new[] { new QmlSignalArg("a", "int", QmlValueKind.Int), new QmlSignalArg("b", "color", QmlValueKind.Color) }));
            Assert.That(d.Signals[5].Args, Is.EqualTo(new[] { new QmlSignalArg("payload", "var", null), new QmlSignalArg("items", "list<int>", null) }));
        }

        [Test]
        public void Modifiers_ReadonlyRequiredDefault()
        {
            var d = Parse("""
                Item {
                    readonly property real ro: 1
                    required property string req
                    default property int def: 2
                    required default property bool both: true
                    property real plain: 3
                }
                """);

            Assert.That(Prop(d, "ro").Flags, Is.EqualTo(QmlPropertyFlags.ReadOnly));
            Assert.That(Prop(d, "req").Flags, Is.EqualTo(QmlPropertyFlags.Required));
            Assert.That(Prop(d, "req").IsRequired, Is.True);
            Assert.That(Prop(d, "req").Initializer, Is.EqualTo(QmlInitializer.None));
            Assert.That(Prop(d, "def").Flags, Is.EqualTo(QmlPropertyFlags.Default));
            Assert.That(Prop(d, "both").Flags, Is.EqualTo(QmlPropertyFlags.Required | QmlPropertyFlags.Default));
            Assert.That(Prop(d, "plain").Flags, Is.EqualTo(QmlPropertyFlags.None));
        }

        [Test]
        public void SummaryIsTheBlockDirectlyAboveTheDeclaration()
        {
            var d = Parse("""
                Item {
                    // not for a (blank line below)

                    // Line one,
                    // line two.
                    property real a: 1
                    //
                    // After an empty comment line.
                    property real b: 2
                    property real c: 3 // trailing, no summary of its own
                    property real d: 4
                    /// Triple slash.
                    readonly property real e: 5
                }
                """);

            Assert.That(Prop(d, "a").Summary, Is.EqualTo("Line one, line two."));
            Assert.That(Prop(d, "b").Summary, Is.EqualTo("After an empty comment line."));
            Assert.That(Prop(d, "c").Summary, Is.Null);
            Assert.That(Prop(d, "d").Summary, Is.Null);
            Assert.That(Prop(d, "e").Summary, Is.EqualTo("Triple slash."));
            Assert.That(Prop(d, "a").Line, Is.EqualTo(6));
        }

        [Test]
        public void NoPinTypes_GiveOneInfoEach()
        {
            var d = Parse("""
                Item {
                    property alias inner: child.text
                    property var data
                    property list<Item> kids
                    property Item target
                    property url source
                    property font f
                    default property alias content: row.children
                    property real fine: 1
                    Text { id: child }
                }
                """);

            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "fine" }));
            Assert.That(d.Messages, Has.Length.EqualTo(7));
            Assert.That(d.Messages.All(m => m.Severity == QmlMessageSeverity.Info), Is.True);
            Assert.That(d.Messages[0].Line, Is.EqualTo(2));
            Assert.That(d.Messages[0].Text, Does.Contain("inner").And.Contain("alias"));
            Assert.That(d.Messages[2].Text, Does.Contain("kids").And.Contain("list<Item>"));
            Assert.That(d.Messages[3].Text, Does.Contain("target").And.Contain("Item"));
            // An info lists the name only: after lines move above it, a line number would point to the wrong place.
            Assert.That(d.Messages[0].ToString(), Does.Not.Contain("line"));
        }

        // ---- head -------------------------------------------------------------------------------------------------------------

        [Test]
        public void HeadTags_AreReadAroundImportsAndPragmas()
        {
            const string source = """
                // Copyright someone, no tag.
                // @category Qt.Widgets
                import QtQuick
                pragma ComponentBehavior: Bound
                // @summary A gauge with
                // a second line.
                // @tags gauge, meter,
                // dial ,, bar
                import QtQuick.Controls as C

                // @unknown nothing
                Item {
                    // @category Ignored.Inside
                    property real value: 1
                }
                """;

            var head = QmlDeclarations.ParseHead(source);
            Assert.That(head.Category, Is.EqualTo("Qt.Widgets"));
            Assert.That(head.Summary, Is.EqualTo("A gauge with a second line."));
            Assert.That(head.Tags, Is.EqualTo(new[] { "gauge", "meter", "dial", "bar" }));

            var d = Parse(source);
            Assert.That(d.Head, Is.EqualTo(head).Using<QmlFileHead>((a, b) => a.Category == b.Category && a.Summary == b.Summary && a.Tags.SequenceEqual(b.Tags)));
            Assert.That(d.Messages.Single().Severity, Is.EqualTo(QmlMessageSeverity.Warning));
            Assert.That(d.Messages.Single().Text, Does.Contain("@unknown"));
        }

        [Test]
        public void HeadWithoutTags_IsEmpty_AndParseHeadNeverThrowsOnBrokenFiles()
        {
            var head = QmlDeclarations.ParseHead("import QtQuick\nItem { property string s: \"unterminated\n");
            Assert.That(head.Category, Is.Null);
            Assert.That(head.Summary, Is.Null);
            Assert.That(head.Tags, Is.Empty);

            Assert.That(QmlDeclarations.ParseHead(string.Empty).Tags, Is.Empty);
            Assert.That(QmlDeclarations.ParseHead("// @summary only a head, no root\n").Summary, Is.EqualTo("only a head, no root"));
        }

        // ---- sibling root types -----------------------------------------------------------------------------------------------

        [Test]
        public void SiblingRootType_ContributesItsDeclarationsFirst()
        {
            var siblings = new Dictionary<string, string>
            {
                ["Gauge"] = """
                    import QtQuick
                    // @category Ignored.From.Sibling
                    Item {
                        // Base value.
                        property real value: 0.25
                        property color accent: "red"
                        signal changed(real v)
                    }
                    """,
            };

            var d = Parse("""
                import QtQuick
                Gauge {
                    // Own label.
                    property string label: "x"
                    property real value: 0.75
                    accent: "#00ff00"
                    signal extra
                    width: 10
                }
                """, siblings);

            Assert.That(d.RootType, Is.EqualTo("Gauge"));
            Assert.That(d.Head.Category, Is.Null);
            // The redeclared `value` keeps its place.
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "value", "accent", "label" }));
            Assert.That(d.Signals.Select(s => s.Name), Is.EqualTo(new[] { "changed", "extra" }));
            Assert.That(d.Signals[0].DeclaredIn, Is.EqualTo("Gauge.qml"));
            Assert.That(d.Signals[1].DeclaredIn, Is.Null);

            var value = Prop(d, "value");
            Assert.That(value.Default, Is.EqualTo(0.75f));
            Assert.That(value.DeclaredIn, Is.Null);

            var accent = Prop(d, "accent");
            Assert.That(accent.DeclaredIn, Is.EqualTo("Gauge.qml"));
            AssertColor(new Color4(0f, 1f, 0f, 1f), accent.Default); // assignment in the root of the derived file
            Assert.That(accent.Initializer, Is.EqualTo(QmlInitializer.Literal));
            Assert.That(accent.Line, Is.EqualTo(6)); // line in Gauge.qml
            Assert.That(d.Messages, Is.Empty);
        }

        [Test]
        public void SiblingChain_IsFollowed_AndAReassignedBindingBecomesBinding()
        {
            var siblings = new Dictionary<string, string>
            {
                ["Middle"] = "Base { property int steps: 4 }",
                ["Base"] = "Item { property real size: 10\n property var junk }",
            };

            var d = Parse("Middle { size: Math.max(1, 2) }", siblings);

            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "size", "steps" }));
            Assert.That(Prop(d, "size").DeclaredIn, Is.EqualTo("Base.qml"));
            Assert.That(Prop(d, "size").Initializer, Is.EqualTo(QmlInitializer.Binding));
            Assert.That(Prop(d, "size").Default, Is.EqualTo(0f));
            Assert.That(Prop(d, "steps").DeclaredIn, Is.EqualTo("Middle.qml"));
            var info = d.Messages.Single();
            Assert.That(info.Severity, Is.EqualTo(QmlMessageSeverity.Info));
            Assert.That(info.File, Is.EqualTo("Base.qml"));
            Assert.That(info.ToString(), Is.EqualTo("Base.qml: property 'junk' (var)"));
        }

        [Test]
        public void SiblingCycle_GivesAWarningAndStops()
        {
            var siblings = new Dictionary<string, string>
            {
                ["A"] = "B { property real fromA: 1 }",
                ["B"] = "A { property real fromB: 2 }",
            };

            var d = Parse("A { property real own: 3 }", siblings);

            Assert.That(d.HasErrors, Is.False);
            Assert.That(d.Messages.Any(m => m.Severity == QmlMessageSeverity.Warning && m.Text.Contains("cycle")), Is.True, string.Join("; ", d.Messages));
            Assert.That(d.Properties.Select(p => p.Name), Is.SupersetOf(new[] { "own", "fromA", "fromB" }));
            Assert.That(d.Properties.Last().Name, Is.EqualTo("own"));
        }

        [Test]
        public void SelfReference_RootTypeOfTheFileItself_IsAnError()
        {
            var asked = new List<string>();
            var d = QmlDeclarations.Parse("Gauge { property real a: 1 }", "Gauge", name => { asked.Add(name); return "Gauge { }"; });

            var error = d.Messages.Single();
            Assert.That(error.Severity, Is.EqualTo(QmlMessageSeverity.Error));
            Assert.That(error.Text, Is.EqualTo("'Gauge.qml' cannot have its own type 'Gauge' as root type"));
            Assert.That(d.Properties, Is.Empty);
            Assert.That(asked, Is.Empty);
        }

        [Test]
        public void Redeclaration_WithATypeWithoutPin_RemovesTheInheritedPin()
        {
            var siblings = new Dictionary<string, string> { ["Base"] = "Item { property real level: 1; property real other: 2 }" };

            var d = Parse("Base { property var level }", siblings);

            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "other" }));
            Assert.That(d.Messages.Single().Text, Is.EqualTo("property 'level' (var)"));
        }

        [Test]
        public void Signature_HasTheLineOfAnErrorOrWarning_ButNotOfAnInfo()
        {
            const string info = "Item {\n    property var model\n}";
            Assert.That(Parse("\n" + info).Signature, Is.EqualTo(Parse(info).Signature));

            const string warning = "Item {\n    property real\n}";
            Assert.That(Parse("\n" + warning).Signature, Is.Not.EqualTo(Parse(warning).Signature));
        }

        [Test]
        public void SelfReference_AndQualifiedOrUnknownRootTypes_ReadNoSibling()
        {
            var asked = new List<string>();
            var d = QmlDeclarations.Parse("Controls.Slider { property real a: 1 }", "Own", name => { asked.Add(name); return null; });
            Assert.That(asked, Is.Empty);
            Assert.That(d.RootType, Is.EqualTo("Controls.Slider"));

            d = QmlDeclarations.Parse("Rectangle { property real a: 1 }", "Own", name => { asked.Add(name); return null; });
            Assert.That(asked, Is.EqualTo(new[] { "Rectangle" }));
            Assert.That(d.Messages, Is.Empty);

            d = Parse("Self { property real a: 1 }", new Dictionary<string, string> { ["Self"] = "Self { property real b: 2 }" });
            Assert.That(d.Messages.Single().Text, Does.Contain("cycle"));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "b", "a" }));
        }

        [Test]
        public void SiblingThatDoesNotParse_OnlyGivesAWarning()
        {
            var d = Parse("Broken { property real a: 1 }", new Dictionary<string, string> { ["Broken"] = "Item { property real b: 2" });

            Assert.That(d.HasErrors, Is.False);
            var warning = d.Messages.Single();
            Assert.That(warning.Severity, Is.EqualTo(QmlMessageSeverity.Warning));
            Assert.That(warning.File, Is.EqualTo("Broken.qml"));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void SiblingThatThrowsOnRead_GivesAWarning()
        {
            var d = QmlDeclarations.Parse("Gauge { property real a: 1 }", "Own", _ => throw new IOException("locked"));

            Assert.That(d.HasErrors, Is.False);
            Assert.That(d.Messages.Single().Severity, Is.EqualTo(QmlMessageSeverity.Warning));
            Assert.That(d.Messages.Single().Text, Does.Contain("locked"));
            Assert.That(d.Properties.Select(p => p.Name), Is.EqualTo(new[] { "a" }));
        }

        // ---- files that do not parse ------------------------------------------------------------------------------------------

        [TestCase("", "no root object")]
        [TestCase("import QtQuick\n", "no root object")]
        [TestCase("import QtQuick\nItem\n", "no root object")]
        [TestCase("import QtQuick\n42 { }\n", "no root object")]
        [TestCase("Item {\n property real a: 1\n", "never closed")]
        [TestCase("Item {\n Rectangle {\n property real a: 1\n}\n", "missing '}'")]
        [TestCase("Item {\n property real a: foo(1\n}\n", "does not match")]
        [TestCase("Item {\n property real a: 1\n}\n}\n", "unexpected content")]
        [TestCase("Item { }\nItem { }\n", "unexpected content")]
        [TestCase("Item {\n property string s: \"open\n}\n", "unterminated string")]
        [TestCase("Item {\n /* never closed\n}\n", "unterminated comment")]
        [TestCase("Item {\n ) \n}\n", "unexpected ')'")]
        public void FileThatDoesNotParse_KeepsOnlyAnErrorMessage(string source, string expected)
        {
            var d = Parse(source);

            Assert.That(d.HasErrors, Is.True);
            Assert.That(d.Properties, Is.Empty);
            Assert.That(d.Signals, Is.Empty);
            var error = d.Messages.Single();
            Assert.That(error.Severity, Is.EqualTo(QmlMessageSeverity.Error));
            Assert.That(error.Text, Does.Contain(expected));
        }

        [Test]
        public void FileThatDoesNotParse_StillKeepsItsHeadTags()
        {
            var d = Parse("// @summary Still here\n// @category A.B\nItem {\n");

            Assert.That(d.HasErrors, Is.True);
            Assert.That(d.Head.Summary, Is.EqualTo("Still here"));
            Assert.That(d.Head.Category, Is.EqualTo("A.B"));
        }

        [Test]
        public void ErrorLine_PointsAtTheProblem()
        {
            var d = Parse("Item {\n  property real a: 1\n  property string s: \"open\n}\n");

            Assert.That(d.Messages.Single().Line, Is.EqualTo(3));
            Assert.That(d.Messages.Single().ToString(), Does.StartWith("line 3: "));
        }

        [Test]
        public void Parse_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => QmlDeclarations.Parse(null!, "Own", _ => null));
            Assert.Throws<ArgumentNullException>(() => QmlDeclarations.Parse("Item { }", "Own", null!));
            Assert.Throws<ArgumentNullException>(() => QmlDeclarations.ParseHead(null!));
        }

        [Test]
        public void EmptyRoot_HasNoDeclarations()
        {
            var d = Parse("import QtQuick\nItem { }");

            Assert.That(d.HasErrors, Is.False);
            Assert.That(d.RootType, Is.EqualTo("Item"));
            Assert.That(d.Properties, Is.Empty);
            Assert.That(d.Signals, Is.Empty);
            Assert.That(d.Messages, Is.Empty);
        }

        [Test]
        public void CrLfAndByteOrderMark_AreHandled()
        {
            var d = Parse("﻿import QtQuick\r\n// Doc line.\r\nItem {\r\n    // Summary.\r\n    property real a: 1 // t\r\n    signal s()\r\n}\r\n");

            Assert.That(d.HasErrors, Is.False);
            Assert.That(Prop(d, "a").Summary, Is.EqualTo("Summary."));
            Assert.That(d.Signals.Single().Name, Is.EqualTo("s"));
        }

        // ---- signature --------------------------------------------------------------------------------------------------------

        private const string SignatureBase = """
            import QtQuick
            // @category Qt.Widgets
            Item {
                // Value.
                property real value: 0.5
                signal done(int code)
                Text { text: "a" }
            }
            """;

        [Test]
        public void Signature_IsStable_AndIgnoresLinesAndBodies()
        {
            var a = Parse(SignatureBase).Signature;
            Assert.That(Parse(SignatureBase).Signature, Is.EqualTo(a));

            // Moved lines, a changed body, another import, a changed binding of an unrelated member.
            var moved = """
                import QtQuick
                import QtQuick.Controls

                // @category Qt.Widgets


                Item {
                    // Value.
                    property real value: 0.5

                    signal done(int code)
                    Text { text: "changed"; color: "red" }
                    Behavior on x { NumberAnimation { duration: 10 } }
                }
                """;
            Assert.That(Parse(moved).Signature, Is.EqualTo(a));
        }

        [TestCase("property real value: 0.6")]
        [TestCase("property real valu: 0.5")]
        [TestCase("property int value: 1")]
        [TestCase("readonly property real value: 0.5")]
        [TestCase("property real value: width")]
        [TestCase("property real value")]
        [TestCase("// Other.\n    property real value: 0.5")]
        public void Signature_ChangesWithWhatReachesTheDescription(string replacement)
        {
            var original = Parse(SignatureBase).Signature;
            var changed = SignatureBase.Replace("// Value.\n    property real value: 0.5", replacement);
            Assert.That(changed, Is.Not.EqualTo(SignatureBase));
            Assert.That(Parse(changed).Signature, Is.Not.EqualTo(original));
        }

        [TestCase("signal done(string code)")]
        [TestCase("signal done(int code, int more)")]
        [TestCase("signal done()")]
        [TestCase("signal finished(int code)")]
        public void Signature_ChangesWithTheSignals(string replacement)
        {
            var changed = SignatureBase.Replace("signal done(int code)", replacement);
            Assert.That(Parse(changed).Signature, Is.Not.EqualTo(Parse(SignatureBase).Signature));
        }

        [Test]
        public void Signature_ChangesWithHeadTagsAndMessages()
        {
            var original = Parse(SignatureBase).Signature;
            Assert.That(Parse(SignatureBase.Replace("Qt.Widgets", "Qt.Other")).Signature, Is.Not.EqualTo(original));
            Assert.That(Parse(SignatureBase.Replace("// @category Qt.Widgets", "// @category Qt.Widgets\n// @tags a, b")).Signature, Is.Not.EqualTo(original));
            Assert.That(Parse(SignatureBase.Replace("signal done", "property var extra\n    signal done")).Signature, Is.Not.EqualTo(original));
            Assert.That(Parse(SignatureBase.Replace("Item {", "Item {\n")).Signature, Is.EqualTo(original));
        }

        [Test]
        public void Signature_OfAnErrorFile_DiffersFromTheValidFile()
        {
            Assert.That(Parse(SignatureBase + "\n}").Signature, Is.Not.EqualTo(Parse(SignatureBase).Signature));
            Assert.That(QmlDeclarations.Empty.Signature, Is.Not.Null);
        }
    }
}
