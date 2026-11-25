using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.Core;
using VL.Qt.Qml;

namespace VL.Qt.Tests
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

        // The pin texts that QML nodes share with the element nodes come from CommonPins.xml (written once, NODE-03).
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
            Assert.That(inputs.Select(i => i.Name).Take(2), Is.EqualTo(new[] { "Accent Color", "Size 2" }));
            Assert.That(inputs.Select(i => i.Name).Skip(2).First(), Is.EqualTo("Properties"));
            Assert.That(inputs.Select(i => i.Name).Skip(3), Is.EqualTo(QmlNodeLayout.CommonInputNames));
            Assert.That(inputs[0].Type, Is.EqualTo(QmlTypes.ClrType(QmlValueKind.Color)));

            var outputs = layout.OutputDescriptions();
            Assert.That(outputs.Select(o => o.Name), Is.EqualTo(new[] { "Output", "Accent Color", "Level", "Size 2", "Clicked", "Measured Size", "Scene Bounds", "Error" }));
            Assert.That(outputs.Single(o => o.Name == "Clicked").Type, Is.EqualTo(typeof(bool)));

            var warning = layout.Messages.Single();
            Assert.That(warning.Type, Is.EqualTo(VL.Core.Diagnostics.MessageType.Warning));
            Assert.That(warning.Text, Does.Contain("'size'").And.Contain("Size 2"));
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
