using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Qt.NodeFactories;
using VL.Qt.Qml;
using VL.Qt.Tests.Support;

namespace VL.Qt.Tests.Qml
{
    /// <summary>QML node factory: names, scan of qml/, event filter, descriptions, pins of hand-made declarations. No QtNative, no vvvv.</summary>
    public class QmlNodeFactoryTests
    {
        private string _dir = null!;

        [SetUp]
        public void CreateDir()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vlqt-qmlnodes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void DeleteDir() => Directory.Delete(_dir, recursive: true);

        private string Write(string relativePath, string content = "import QtQuick\nItem { }\n")
        {
            var path = Path.Combine(_dir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        // Stands in for QmlDeclarations.ParseHead (T2): "// @category X", "// @summary X", "// @tags a, b".
        private static QmlFileHead FakeHead(string source)
        {
            string? category = null, summary = null;
            var tags = ImmutableArray<string>.Empty;
            foreach (var line in source.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("// @category ")) category = l["// @category ".Length..];
                else if (l.StartsWith("// @summary ")) summary = l["// @summary ".Length..];
                else if (l.StartsWith("// @tags ")) tags = l["// @tags ".Length..].Split(',').Select(t => t.Trim()).ToImmutableArray();
            }
            return new QmlFileHead(category, summary, tags);
        }

        // --- names ---

        [Test]
        public void FullName_HasVariantInParentheses()
        {
            var f = new QmlFile("x", "Gauge", "Round", QmlNodeFactory.DefaultCategory, null, "", null);
            Assert.That(f.FullName, Is.EqualTo("Gauge (Round)"));
            Assert.That((f with { Version = null }).FullName, Is.EqualTo("Gauge"));
        }

        [TestCase("Gauge.qml", true)]
        [TestCase("A.qml", true)]
        [TestCase("Gauge_Round.QML", true)]
        [TestCase("gauge.qml", false)]
        [TestCase("main.qml", false)]
        [TestCase("_Gauge.qml", false)]
        [TestCase("1Gauge.qml", false)]
        [TestCase(".qml", false)]
        [TestCase("Gauge.js", false)]
        [TestCase("Gauge.qml~", false)]
        [TestCase("qmldir", false)]
        [TestCase("sub/Gauge.qml", true)]
        [TestCase(null, false)]
        public void ComponentFileName_NeedsUppercaseFirstLetter(string? fileName, bool expected)
        {
            Assert.That(QmlNodeFactory.IsComponentFileName(fileName), Is.EqualTo(expected));
        }

        // --- scan ---

        [Test]
        public void Scan_UppercaseRuleVariantAndHead()
        {
            Write("Gauge_Round.qml", "// @category Qt.Test\n// @summary A round gauge.\n// @tags dial, meter\nItem { }\n");
            Write("Plain.qml");
            Write("main.qml");
            Write("helper.qml");
            Write("_Private.qml");
            Write("Gauge.js", "x");
            Write("qmldir", "module X");
            Write("notes.txt", "x");

            var files = QmlNodeFactory.Scan(_dir, FakeHead);

            Assert.That(files.Select(f => f.FullName), Is.EqualTo(new[] { "Gauge (Round)", "Plain" }));
            var gauge = files[0];
            Assert.That(gauge.Name, Is.EqualTo("Gauge"));
            Assert.That(gauge.Version, Is.EqualTo("Round"));
            Assert.That(gauge.FilePath, Is.EqualTo(Path.Combine(_dir, "Gauge_Round.qml")));
            Assert.That(gauge.Category, Is.EqualTo("Qt.Test"));
            Assert.That(gauge.Summary, Is.EqualTo("A round gauge."));
            Assert.That(gauge.Tags, Is.EqualTo("dial,meter"));
            Assert.That(gauge.DuplicateError, Is.Null);

            var plain = files[1];
            Assert.That(plain.Version, Is.Null);
            Assert.That(plain.Category, Is.EqualTo("Qt.Elements.Qml"));
            Assert.That(plain.Summary, Is.Null);
            Assert.That(plain.Tags, Is.Empty);
        }

        [Test]
        public void Scan_NestedFolders_AreRecursive()
        {
            Write("Top.qml");
            var deep = Write(Path.Combine("sub", "deeper", "Deep.qml"));
            Write(Path.Combine("sub", "lower.qml"));

            var files = QmlNodeFactory.Scan(_dir, FakeHead);

            Assert.That(files.Select(f => f.Name), Is.EquivalentTo(new[] { "Top", "Deep" }));
            Assert.That(files.Single(f => f.Name == "Deep").FilePath, Is.EqualTo(deep));
            Assert.That(files.All(f => f.DuplicateError == null));
        }

        [Test]
        public void Scan_DuplicateFileNames_BothGetTheError()
        {
            var a = Write("Twice.qml");
            var b = Write(Path.Combine("sub", "deeper", "Twice.qml"));
            Write("Once.qml");

            var files = QmlNodeFactory.Scan(_dir, FakeHead);

            Assert.That(files.Select(f => f.Name), Is.EquivalentTo(new[] { "Once", "Twice", "Twice" }));
            foreach (var twice in files.Where(f => f.Name == "Twice"))
            {
                Assert.That(twice.DuplicateError, Does.Contain(a));
                Assert.That(twice.DuplicateError, Does.Contain(b));
            }
            Assert.That(files.Single(f => f.Name == "Once").DuplicateError, Is.Null);
        }

        [Test]
        public void Scan_BlankCategory_FallsBackToDefault()
        {
            Write("Blank.qml", "// @category   \nItem { }\n");
            Write("Trimmed.qml", "// @category  Qt.Test  \nItem { }\n");

            var files = QmlNodeFactory.Scan(_dir, FakeHead);

            Assert.That(files.Single(f => f.Name == "Blank").Category, Is.EqualTo(QmlNodeFactory.DefaultCategory));
            Assert.That(files.Single(f => f.Name == "Trimmed").Category, Is.EqualTo("Qt.Test"));
        }

        [Test]
        public void Scan_EmptyFolder_IsEmpty()
        {
            Assert.That(QmlNodeFactory.Scan(_dir, FakeHead), Is.Empty);
        }

        [Test]
        public void ForPath_WithoutQmlFolder_HasNoNodes()
        {
            var impl = QmlNodeFactory.ForPath(_dir, new FakeFactory(), NullLogger.Instance);
            Assert.That(impl.Nodes.IsDefaultOrEmpty);
        }

        // --- watcher filter ---

        [Test]
        public void Watcher_CountsComponentFilesAndFolders()
        {
            static FileSystemEventArgs E(WatcherChangeTypes t, string name) => new(t, "c:\\x", name);
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Created, "Gauge.qml")));
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Deleted, Path.Combine("sub", "Gauge.qml"))));
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Created, "sub")), "a folder may hold files");
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Deleted, Path.Combine("sub", "deeper"))));
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Created, "helper.qml")), Is.False, "lowercase is no node");
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Created, "Gauge.qml~")), Is.False, "editor temp file");
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Created, "Gauge.js")), Is.False);
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(E(WatcherChangeTypes.Changed, "Gauge.qml")), Is.False, "content goes to the node");
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(new RenamedEventArgs(WatcherChangeTypes.Renamed, "c:\\x", "Gauge.qml", "New.txt")));
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(new RenamedEventArgs(WatcherChangeTypes.Renamed, "c:\\x", "Gauge.qml", "gauge.qml")), "an uppercase name was lost");
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(new RenamedEventArgs(WatcherChangeTypes.Renamed, "c:\\x", "gauge.qml", "Gauge.qml")), "an uppercase name appeared");
            Assert.That(QmlNodeFactory.IsNewOrDeletedQmlFile(new RenamedEventArgs(WatcherChangeTypes.Renamed, "c:\\x", "a.qml~", "a.tmp")), Is.False);
        }

        // --- descriptions ---

        private static QmlFile File_(string path, string name, string? version = null, string category = QmlNodeFactory.DefaultCategory, string tags = "") =>
            new(path, name, version, category, null, tags, null);

        [Test]
        public void CreateDescriptions_OnePerFile_NameAndCategoryFromFile()
        {
            var files = new[]
            {
                File_(Path.Combine(_dir, "Gauge_Round.qml"), "Gauge", "Round", "Qt.Test", "dial,meter"),
                File_(Path.Combine(_dir, "Plain.qml"), "Plain"),
            };

            var descriptions = QmlNodeFactory.CreateDescriptions(files, new FakeFactory(), NullLogger.Instance);

            Assert.That(descriptions.Select(d => d.Name), Is.EqualTo(new[] { "Gauge (Round)", "Plain" }));
            Assert.That(descriptions.Select(d => d.Category), Is.EqualTo(new[] { "Qt.Test", "Qt.Elements.Qml" }));
        }

        [Test]
        public void Description_IsNotFragmented_SoVvvvCallsUpdate()
        {
            // vvvv calls IVLNode.Update only for non-fragmented descriptions; fragmented ones never update and Output stays null.
            var description = QmlNodeDescription.New(new FakeFactory(), File_(Write("Gauge.qml"), "Gauge"), NullLogger.Instance);
            Assert.That(description.Fragmented, Is.False);
        }

        [Test]
        public void Description_WithoutTags_IsStillCreated()
        {
            // The tags argument of NewNodeDescription gets "" when the head has none.
            var description = QmlNodeDescription.New(new FakeFactory(), File_(Write("Gauge.qml"), "Gauge", tags: ""), NullLogger.Instance);
            Assert.That(description.Name, Is.EqualTo("Gauge"));
        }

        [Test]
        public void Signature_NothingKnownYet_NothingFires()
        {
            // WatchSignature compares with the signature of the last init; before the first init (null) no event may invalidate.
            var path = Write("Gauge.qml");
            var fired = 0;
            using var _ = QmlNodeFactory.WatchSignature(File_(path, "Gauge"), () => null).Subscribe(_ => fired++);

            // A probe with a stale signature fires on every real change: waiting for it (positive wait, with a timeout) proves that
            // the watcher saw the writes, instead of asserting "nothing happened" after a fixed sleep. Two writes, so the first
            // one has certainly been delivered to the silent subscription when the second probe event arrives.
            using var probed = new System.Threading.SemaphoreSlim(0);
            using var probe = QmlNodeFactory.WatchSignature(File_(path, "Gauge"), () => "stale").Subscribe(_ => probed.Release());
            File.WriteAllText(path, "Item { property real a }\n");
            Assert.That(probed.Wait(TimeSpan.FromSeconds(10)), "the watcher reports the first write");
            File.WriteAllText(path, "Item { property real a; property real b }\n");
            Assert.That(probed.Wait(TimeSpan.FromSeconds(10)), "the watcher reports the second write");
            Assert.That(fired, Is.Zero);
        }

        // --- pins from hand-made declarations ---

        private static QmlPropertyDecl Prop(string name, QmlValueKind kind, QmlPropertyFlags flags = QmlPropertyFlags.None, QmlInitializer init = QmlInitializer.None, object? value = null, string? summary = null) =>
            new(name, kind.ToString().ToLowerInvariant(), kind, flags, init, value ?? QmlTypes.TypeDefault(kind), summary, 1);

        private static QmlDeclarations Declare(QmlPropertyDecl[] properties, QmlSignalDecl[]? signals = null) =>
            new("Item", QmlFileHead.Empty, properties.ToImmutableArray(), (signals ?? Array.Empty<QmlSignalDecl>()).ToImmutableArray(), ImmutableArray<QmlParseMessage>.Empty);

        // The pin texts that QML nodes share with the element nodes come from CommonPins.xml (written once).
        [Test]
        public void Layout_CommonPinHelp_IsTheSharedText()
        {
            var layout = QmlNodeLayout.Build("Gauge.qml", Declare(Array.Empty<QmlPropertyDecl>()));
            var inputs = layout.InputDescriptions().ToDictionary(i => i.Name, i => ((IInfo)i).Summary);
            foreach (var (pin, name) in new[] { ("Transform", "transform"), ("Anchor", "anchor"), ("Opacity", "opacity"), ("Clip", "clip"), ("Debug", "debug"),
                ("Interaction", "interaction"), ("Effect", "effect"), ("Enabled", "enabled") })
                Assert.That(inputs[pin], Is.EqualTo(CommonPinsXml.Param("element", name)), pin);
            var outputs = layout.OutputDescriptions().ToDictionary(o => o.Name, o => ((IInfo)o).Summary);
            Assert.That(outputs["Scene Bounds"], Is.EqualTo(CommonPinsXml.Param("element", "sceneBounds")));
        }

        [Test]
        public void Layout_DuplicateAndReadError_AreBothShown()
        {
            var missing = Path.Combine(_dir, "Missing.qml");
            var file = new QmlFile(missing, "Missing", null, QmlNodeFactory.DefaultCategory, null, "", "duplicate of Other.qml");

            var layout = QmlNodeLayout.Load(file, NullLogger.Instance);

            // A missing file is a FileNotFoundException, so the read error comes with the duplicate error.
            var error = layout.Messages.Single(m => m.Type == VL.Core.Diagnostics.MessageType.Error).Text;
            Assert.That(error, Does.Contain("duplicate of Other.qml").And.Contain("cannot be read").And.Contain(missing));
        }

        [Test]
        public void Layout_ParseErrorAfterGoodVersion_KeepsLastGoodPinsAndShowsTheError()
        {
            var path = Write("Gauge.qml", "Item { property real level: 1; property color accentColor }\n");
            var file = File_(path, "Gauge");
            var lastGood = new LastGood<QmlNodeLayout>();

            var good = lastGood.Resolve(QmlNodeLayout.Load(file, NullLogger.Instance), failed: false, QmlNodeLayout.KeepPins);
            // A root type that is the file itself is a parse error: alone it leaves only the common pins.
            File.WriteAllText(path, "Gauge { property real level: 1 }\n");
            var loaded = QmlNodeLayout.Load(file, NullLogger.Instance);
            Assert.That(loaded.HasErrors);
            Assert.That(loaded.InputDescriptions().Select(i => i.Name), Does.Not.Contain("Level"));

            var kept = lastGood.Resolve(loaded, loaded.HasErrors, QmlNodeLayout.KeepPins);

            Assert.That(kept.InputDescriptions().Select(i => i.Name), Is.EqualTo(good.InputDescriptions().Select(i => i.Name)));
            Assert.That(kept.InputDescriptions().Select(i => i.Name), Does.Contain("Level").And.Contain("Accent Color"));
            Assert.That(kept.Messages.Count(m => m.Type == VL.Core.Diagnostics.MessageType.Error), Is.EqualTo(1));
            Assert.That(kept.Messages.Single(m => m.Type == VL.Core.Diagnostics.MessageType.Error).Text, Does.Contain("own type"));
            Assert.That(kept.Messages.Select(m => m.Text), Does.Contain(LastGood<QmlNodeLayout>.KeptNote));
        }

        [Test]
        public void Layout_UnreadableFileAfterGoodVersion_KeepsLastGoodPins()
        {
            var path = Write("Gauge.qml", "Item { property real level: 1 }\n");
            var file = File_(path, "Gauge");
            var lastGood = new LastGood<QmlNodeLayout>();
            lastGood.Resolve(QmlNodeLayout.Load(file, NullLogger.Instance), failed: false, QmlNodeLayout.KeepPins);

            // Locked by the editor that saves it.
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var loaded = QmlNodeLayout.Load(file, NullLogger.Instance);
            var kept = lastGood.Resolve(loaded, loaded.HasErrors, QmlNodeLayout.KeepPins);

            Assert.That(kept.InputDescriptions().Select(i => i.Name), Does.Contain("Level"));
            Assert.That(kept.Messages.Single(m => m.Type == VL.Core.Diagnostics.MessageType.Error).Text, Does.Contain("cannot be read"));
        }

        [Test]
        public void Layout_ParseErrorWithoutGoodVersion_ShowsWhatTheFailedVersionGives()
        {
            var path = Write("Gauge.qml", "Gauge { property real level: 1 }\n");
            var loaded = QmlNodeLayout.Load(File_(path, "Gauge"), NullLogger.Instance);
            var kept = new LastGood<QmlNodeLayout>().Resolve(loaded, loaded.HasErrors, QmlNodeLayout.KeepPins);
            Assert.That(kept, Is.SameAs(loaded));
        }

        [Test]
        public void Layout_PinsFollowDeclarations_CommonPinsAround()
        {
            var layout = QmlNodeLayout.Build("Gauge.qml", Declare(
                new[]
                {
                    Prop("accentColor", QmlValueKind.Color, init: QmlInitializer.Literal, summary: "Needle color."),
                    Prop("level", QmlValueKind.Real, QmlPropertyFlags.ReadOnly),
                    Prop("size", QmlValueKind.Real),
                },
                new[] { new QmlSignalDecl("clicked", ImmutableArray<QmlSignalArg>.Empty, null, 5) }));

            var inputs = layout.InputDescriptions();
            Assert.That(inputs.Select(i => i.Name), Is.EqualTo(new[]
            {
                "Auto Size", "Size", "Transform", "Anchor", "Accent Color", "Size 2", "Properties",
                "Opacity", "Clip", "Debug", "Interaction", "Effect", "Enabled",
            }));
            Assert.That(inputs[4].Type, Is.EqualTo(QmlTypes.ClrType(QmlValueKind.Color)));

            var outputs = layout.OutputDescriptions();
            Assert.That(outputs.Select(o => o.Name), Is.EqualTo(new[] { "Output", "Accent Color", "Level", "Size 2", "Clicked", "Measured Size", "Scene Bounds" }));
            Assert.That(outputs.Single(o => o.Name == "Clicked").Type, Is.EqualTo(typeof(bool)));

            var warning = layout.Messages.Single();
            Assert.That(warning.Type, Is.EqualTo(VL.Core.Diagnostics.MessageType.Warning));
            Assert.That(warning.Text, Does.Contain("'size'").And.Contain("Size 2"));
        }

        [Test]
        public void Layout_ListProperties_AreSpreadPins_ReadonlyOnlyAnOutput()
        {
            var layout = QmlNodeLayout.Build("Plot.qml", Declare(
                new[]
                {
                    Prop("values", QmlValueKind.RealList, init: QmlInitializer.Literal, value: (new[] { 1f, 2f }).ToSpread()),
                    Prop("points", QmlValueKind.PointList),
                    Prop("names", QmlValueKind.StringList, QmlPropertyFlags.ReadOnly),
                },
                new[] { new QmlSignalDecl("picked", ImmutableArray.Create(new QmlSignalArg("items", "list<int>", QmlValueKind.IntList)), null, 5) }));

            var inputs = layout.InputDescriptions();
            Assert.That(inputs.Select(i => i.Name), Is.EqualTo(new[]
            {
                "Auto Size", "Size", "Transform", "Anchor", "Values", "Points", "Properties",
                "Opacity", "Clip", "Debug", "Interaction", "Effect", "Enabled",
            }));
            Assert.That(inputs[4].Type, Is.EqualTo(typeof(Spread<float>)));
            Assert.That(inputs[4].DefaultValue, Is.EqualTo((new[] { 1f, 2f }).ToSpread()));
            Assert.That(inputs[5].Type, Is.EqualTo(typeof(Spread<Vector2>)));
            Assert.That(inputs[5].DefaultValue, Is.InstanceOf<Spread<Vector2>>().And.Empty);

            var outputs = layout.OutputDescriptions();
            Assert.That(outputs.Select(o => o.Name), Is.EqualTo(new[] { "Output", "Values", "Points", "Names", "Picked", "Measured Size", "Scene Bounds" }));
            Assert.That(outputs.Single(o => o.Name == "Names").Type, Is.EqualTo(typeof(Spread<string>)));
            Assert.That(outputs.Single(o => o.Name == "Picked").Type, Is.EqualTo(typeof(Spread<Spread<int>>)));
            Assert.That(layout.Messages, Is.Empty);
        }

        [Test]
        public void ParsedListFile_GivesSpreadPinsWithTheLiteralAsDefault()
        {
            var declarations = QmlDeclarations.Parse(
                "Item {\n property list<real> values: [1, 2]\n readonly property list<point> points\n signal picked(list<string> names)\n}", "Plot", _ => null);
            var layout = QmlNodeLayout.Build("Plot.qml", declarations);

            Assert.That(declarations.Messages, Is.Empty);
            var input = layout.InputDescriptions().Single(i => i.Name == "Values");
            Assert.That(input.Type, Is.EqualTo(typeof(Spread<float>)));
            Assert.That(input.DefaultValue, Is.EqualTo((new[] { 1f, 2f }).ToSpread()));
            Assert.That(layout.InputDescriptions().Any(i => i.Name == "Points"), Is.False);
            Assert.That(layout.OutputDescriptions().Single(o => o.Name == "Points").Type, Is.EqualTo(typeof(Spread<Vector2>)));
            Assert.That(layout.OutputDescriptions().Single(o => o.Name == "Picked").Type, Is.EqualTo(typeof(Spread<Spread<string>>)));
        }

        [Test]
        public void ParsedFile_WithInternalDeclarations_GivesNoPinsAndNoMessageForThem()
        {
            var declarations = QmlDeclarations.Parse("""
                Item {
                    property real value: 1
                    // @internal
                    property real shownAmount
                    // @internal
                    readonly property real chartTop: 2
                    // @internal
                    readonly property var points
                    readonly property real _labelY
                    // @internal
                    signal tick(real t)
                    signal _quiet
                    signal done
                }
                """, "Card", _ => null);
            var layout = QmlNodeLayout.Build("Card.qml", declarations);

            Assert.That(layout.Messages, Is.Empty);
            var names = layout.InputDescriptions().Select(i => i.Name).Concat(layout.OutputDescriptions().Select(o => o.Name)).ToList();
            Assert.That(names, Does.Contain("Value"));
            foreach (var hidden in new[] { "Shown Amount", "Chart Top", "Points", "Label Y", "Tick", "Quiet" })
                Assert.That(names, Does.Not.Contain(hidden), hidden);
            Assert.That(layout.OutputDescriptions().Select(o => o.Name), Does.Contain("Value").And.Contain("Done"));
        }

        private sealed class FakeFactory : IVLNodeDescriptionFactory
        {
            public string Identifier => "Test";
            public ImmutableArray<IVLNodeDescription> NodeDescriptions => default;
            public IObservable<object> Invalidated => System.Reactive.Linq.Observable.Never<object>();
            public IVLNodeDescriptionFactory? ForPath(string path) => null;
            public void Export(ExportContext exportContext) { }
        }
    }
}
