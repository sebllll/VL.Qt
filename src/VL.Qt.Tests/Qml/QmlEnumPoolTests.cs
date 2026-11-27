using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;
using VL.Lib.Collections;
using VL.Qt.Qml;

namespace VL.Qt.Tests.Qml
{
    /// <summary>
    /// The QML enum pool: mapping file, stability over restarts (a new pool on the same folders), full pool, collisions between
    /// folders, entries and the conversion between pin values and ints. Also the generator of QmlEnumSlotTypes.cs (see
    /// <see cref="SlotTypes_MatchSlotCount"/>).
    /// </summary>
    [TestFixture]
    public sealed class QmlEnumPoolTests
    {
        private const string updateVariable = "VLQT_UPDATE_SNAPSHOT";

        private string _dir = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vlqt-enumpool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }

        // --- generated pool types ---

        /// <summary>
        /// QmlEnumSlotTypes.cs is generated from <see cref="QmlEnumPool.SlotCount"/>; VLQT_UPDATE_SNAPSHOT=1 rewrites it (then build
        /// again, see the constant).
        /// </summary>
        [Test]
        public void SlotTypes_MatchSlotCount()
        {
            var path = Path.GetFullPath(typeof(QmlEnumPoolTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "QmlEnumSlotTypes").Value!);
            var expected = SlotTypesSource(QmlEnumPool.SlotCount);
            if (Environment.GetEnvironmentVariable(updateVariable) == "1")
            {
                // C# files of the repository: UTF-8 with BOM, LF.
                File.WriteAllText(path, expected, new UTF8Encoding(true));
                TestContext.Out.WriteLine($"Rewrote {path}.");
                return;
            }
            Assert.That(File.ReadAllText(path).Replace("\r\n", "\n"), Is.EqualTo(expected),
                $"{path} does not match QmlEnumPool.SlotCount: run the tests with {updateVariable}=1, then build again.");
            Assert.That(QmlEnumSlotTypes.All, Has.Length.EqualTo(QmlEnumPool.SlotCount));
        }

        [Test]
        public void SlotTypes_AreInSlotOrderAndCreateTheirValues()
        {
            for (var i = 0; i < QmlEnumSlotTypes.All.Length; i++)
            {
                var slot = QmlEnumSlotTypes.All[i];
                Assert.That(slot.Type.Name, Is.EqualTo(TypeName(i + 1)));
                var value = slot.Create("A");
                Assert.That((value.GetType(), value.Value), Is.EqualTo((slot.Type, "A")));
            }
        }

        /// <summary>
        /// The definition of each pool type reads the entries and the change stream of its slot of the shared pool, unsorted. Its
        /// constructor needs vvvv's AppHost, so the test calls the overrides on an instance made without it.
        /// </summary>
        [Test]
        public void Definitions_ReadTheirSlotOfTheSharedPool()
        {
            var claim = QmlEnumPool.Shared.Claim(QmlFile("Doc"), Decl("Shared", "Mode", ("Small", 0), ("Large", 5)));
            Assert.That(claim.Slot, Is.Not.Null, claim.Message);
            for (var n = 1; n <= QmlEnumSlotTypes.All.Length; n++)
            {
                var definitionType = QmlEnumSlotTypes.All[n - 1].Type.BaseType!.GetGenericArguments()[1];
                var definition = RuntimeHelpers.GetUninitializedObject(definitionType);
                Assert.That(Call(definition, "GetEntries"), Is.SameAs(QmlEnumPool.Shared.Entries(n)), definitionType.Name);
                Assert.That(Call(definition, "GetEntriesChangedObservable"), Is.SameAs(QmlEnumPool.Shared.EntriesChanged(n)), definitionType.Name);
                Assert.That(Call(definition, "get_AutoSortAlphabetically"), Is.False, definitionType.Name);
            }
            Assert.That(QmlEnumPool.Shared.Entries(claim.Slot!.Number).Keys, Is.EqualTo(new[] { "Small", "Large" }));
        }

        // --- mapping file ---

        [Test]
        public void Claim_WritesTheFileAndGivesTheSameSlotAgain()
        {
            var pool = Pool(4);
            var first = pool.Claim(QmlFile("Doc"), Gauge());
            var again = pool.Claim(QmlFile("Doc", "sub/Other.qml"), Gauge());

            Assert.That(first.Slot, Is.Not.Null, first.Message);
            Assert.That(first.Message, Is.Null);
            Assert.That(again.Slot, Is.SameAs(first.Slot), "a file in a subfolder uses the same qml folder");
            Assert.That(Lines("Doc"), Is.EqualTo(new[] { $"Gauge.Mode = {first.Slot!.Number}" }));
            Assert.That(File.ReadAllText(SlotFile("Doc")), Does.StartWith("# ").And.Not.Contain("\r"));
        }

        [Test]
        public void Claim_KeepsSlotsOverRestarts()
        {
            var before = Pool(4);
            var mode = before.Claim(QmlFile("Doc"), Gauge()).Slot!.Number;
            var status = before.Claim(QmlFile("Doc"), Decl("Card", "Status", ("Online", 0))).Slot!.Number;

            // A restart: claims in another order, nothing in memory.
            var after = Pool(4);
            Assert.That(after.Claim(QmlFile("Doc"), Decl("Card", "Status", ("Online", 0))).Slot!.Number, Is.EqualTo(status));
            Assert.That(after.Claim(QmlFile("Doc"), Gauge()).Slot!.Number, Is.EqualTo(mode));
        }

        [Test]
        public void Claim_UsesTheSlotOfTheFile()
        {
            WriteSlotFile("Doc", "# comment", "Gauge.Mode = 3", "broken line", "Card.Status = 99", "Other.Mode = 3");
            var pool = Pool(4);
            Assert.That(pool.Claim(QmlFile("Doc"), Gauge()).Slot!.Number, Is.EqualTo(3));
            var other = pool.Claim(QmlFile("Doc"), Decl("Other", "Mode", ("A", 0))).Slot!.Number;
            Assert.That(other, Is.Not.EqualTo(3), "a number a line above already has is no reservation");
        }

        [Test]
        public void Claim_FullPool_GivesNoSlot()
        {
            var pool = Pool(2);
            Assert.That(pool.Claim(QmlFile("Doc"), Decl("A", "E", ("X", 0))).Slot, Is.Not.Null);
            Assert.That(pool.Claim(QmlFile("Doc"), Decl("B", "E", ("X", 0))).Slot, Is.Not.Null);
            var full = pool.Claim(QmlFile("Doc"), Decl("C", "E", ("X", 0)));
            Assert.That(full.Slot, Is.Null);
            Assert.That(full.Message, Does.Contain("All 2 pool types").And.Contain("C.E"));
            Assert.That(Lines("Doc"), Has.Length.EqualTo(2), "nothing written for C.E");
        }

        [Test]
        public void Claim_InstalledPackage_UsesItsFileAndNeverWrites()
        {
            MarkAsPackage("Pkg");
            WriteSlotFile("Pkg", "Gauge.Mode = 2");
            var before = File.ReadAllText(SlotFile("Pkg"));
            var pool = Pool(4);

            Assert.That(pool.Claim(QmlFile("Pkg"), Gauge()).Slot!.Number, Is.EqualTo(2));
            var missing = pool.Claim(QmlFile("Pkg"), Decl("Card", "Status", ("Online", 0)));
            Assert.That(missing.Slot, Is.Null, "a type for one session would not match the saved patches next time");
            Assert.That(missing.Message, Does.Contain("installed package"));
            Assert.That(File.ReadAllText(SlotFile("Pkg")), Is.EqualTo(before));
        }

        [Test]
        public void Claim_FileThatCannotBeWritten_GivesNoSlot()
        {
            WriteSlotFile("Doc", "Gauge.Mode = 1");
            File.SetAttributes(SlotFile("Doc"), FileAttributes.ReadOnly);
            var pool = Pool(4);

            Assert.That(pool.Claim(QmlFile("Doc"), Gauge()).Slot!.Number, Is.EqualTo(1), "its lines still count");
            var claim = pool.Claim(QmlFile("Doc"), Decl("Card", "Status", ("Online", 0)));
            Assert.That(claim.Slot, Is.Null);
            Assert.That(claim.Message, Does.Contain("cannot be written"));
        }

        // --- collisions ---

        [Test]
        public void Collision_WritableNewcomerMovesAndRewritesItsFile()
        {
            WriteSlotFile("A", "Gauge.Mode = 1");
            WriteSlotFile("B", "Card.Status = 1");
            var pool = Pool(4);
            Assert.That(pool.Claim(QmlFile("A"), Gauge()).Slot!.Number, Is.EqualTo(1));

            var moved = pool.Claim(QmlFile("B"), Status());
            Assert.That(moved.Slot!.Number, Is.Not.EqualTo(1));
            Assert.That(moved.Message, Does.Contain("moved from QmlEnum01").And.Contain("recreate the IOBoxes"));
            Assert.That(Lines("B"), Is.EqualTo(new[] { $"Card.Status = {moved.Slot.Number}" }));
            Assert.That(pool.Claim(QmlFile("B"), Status()).Message, Is.Null, "the move is reported once");

            // Next start, B first: no collision any more.
            var restart = Pool(4);
            Assert.That(restart.Claim(QmlFile("B"), Status()).Slot!.Number, Is.EqualTo(moved.Slot.Number));
            Assert.That(restart.Claim(QmlFile("A"), Gauge()).Slot!.Number, Is.EqualTo(1));
        }

        [Test]
        public void Collision_PackageWinsOverAWritableReservationNotHandedOut()
        {
            WriteSlotFile("Doc", "Gauge.Mode = 1", "Card.Status = 2");
            MarkAsPackage("Pkg");
            WriteSlotFile("Pkg", "Knob.Style = 1");
            var pool = Pool(4);
            // Loads Doc (reserves 1 and 2) without handing out Gauge.Mode.
            Assert.That(pool.Claim(QmlFile("Doc"), Status()).Slot!.Number, Is.EqualTo(2));

            Assert.That(pool.Claim(QmlFile("Pkg"), Decl("Knob", "Style", ("Flat", 0))).Slot!.Number, Is.EqualTo(1));
            var moved = pool.Claim(QmlFile("Doc"), Gauge());
            Assert.That(moved.Slot!.Number, Is.Not.EqualTo(1).And.Not.EqualTo(2));
            Assert.That(moved.Message, Does.Contain("moved from QmlEnum01"));
            Assert.That(Lines("Doc"), Is.EquivalentTo(new[] { "Card.Status = 2", $"Gauge.Mode = {moved.Slot.Number}" }));
        }

        [Test]
        public void Collision_PackageWaitsForARestartWhenTheWritableOwnerIsHandedOut()
        {
            WriteSlotFile("Doc", "Gauge.Mode = 1");
            MarkAsPackage("Pkg");
            WriteSlotFile("Pkg", "Knob.Style = 1");
            var pool = Pool(4);
            var held = pool.Claim(QmlFile("Doc"), Gauge()).Slot!;

            var blocked = pool.Claim(QmlFile("Pkg"), Decl("Knob", "Style", ("Flat", 0)));
            Assert.That(blocked.Slot, Is.Null);
            Assert.That(blocked.Message, Does.Contain("until vvvv restarts"));
            Assert.That(pool.Claim(QmlFile("Doc"), Gauge()).Slot, Is.SameAs(held), "the handed-out type stays in this session");
            var next = Lines("Doc").Single();
            Assert.That(next, Is.Not.EqualTo("Gauge.Mode = 1"), "Doc moves on disk for the next start");

            var restart = Pool(4);
            Assert.That(restart.Claim(QmlFile("Doc"), Gauge()).Slot!.Number.ToString(CultureInfo.InvariantCulture), Is.EqualTo(next.Split(" = ")[1]));
            Assert.That(restart.Claim(QmlFile("Pkg"), Decl("Knob", "Style", ("Flat", 0))).Slot!.Number, Is.EqualTo(1));
        }

        [Test]
        public void Collision_TwoPackages_SecondGetsNoSlot()
        {
            MarkAsPackage("P1");
            MarkAsPackage("P2");
            WriteSlotFile("P1", "Gauge.Mode = 1");
            WriteSlotFile("P2", "Card.Status = 1");
            var pool = Pool(4);
            Assert.That(pool.Claim(QmlFile("P1"), Gauge()).Slot!.Number, Is.EqualTo(1));

            var second = pool.Claim(QmlFile("P2"), Status());
            Assert.That(second.Slot, Is.Null);
            Assert.That(second.Message, Does.Contain("both have QmlEnum01").And.Contain("Change the number"));
        }

        [Test]
        public void SameKeyInTwoFolders_GetsTwoSlots()
        {
            var pool = Pool(4);
            var a = pool.Claim(QmlFile("A"), Decl("Gauge", "Mode", ("Small", 0)));
            var b = pool.Claim(QmlFile("B"), Decl("Gauge", "Mode", ("Tiny", 0), ("Huge", 9)));
            Assert.That(b.Slot!.Number, Is.Not.EqualTo(a.Slot!.Number));
            Assert.That(a.Slot.Entries.Keys, Is.EqualTo(new[] { "Small" }));
            Assert.That(b.Slot.Entries.Keys, Is.EqualTo(new[] { "Tiny", "Huge" }));
        }

        // --- entries and values ---

        [Test]
        public void Claim_UpdatesEntriesAndFiresOnlyOnChange()
        {
            var pool = Pool(4);
            var slot = pool.Claim(QmlFile("Doc"), Gauge()).Slot!;
            var fired = 0;
            using var subscription = slot.Changed.Subscribe(_ => fired++);

            pool.Claim(QmlFile("Doc"), Gauge());
            Assert.That(fired, Is.Zero, "same values");
            pool.Claim(QmlFile("Doc"), Decl("Gauge", "Mode", ("Small", 0), ("Large", 6)));
            Assert.That(fired, Is.EqualTo(1));
            Assert.That(slot.Entries, Is.EqualTo(new Dictionary<string, object> { ["Small"] = 0, ["Large"] = 6 }));
        }

        [Test]
        public void Values_RoundTripBetweenIntAndEnum()
        {
            var pool = Pool(4);
            var slot = pool.Claim(QmlFile("Doc"), Decl("Gauge", "Mode", ("Small", 0), ("Large", 5), ("Big", 5))).Slot!;

            var large = slot.FromInt(5);
            Assert.That((large.GetType(), large.Value), Is.EqualTo((slot.Type, "Large")), "first entry of the value");
            Assert.That(slot.TryToInt(large, out var five) && five == 5);
            Assert.That(slot.TryToInt(slot.FromInt(0), out var zero) && zero == 0);

            var unknown = slot.FromInt(-7);
            Assert.That(unknown.Value, Is.EqualTo("-7"), "a value without entry keeps its number");
            Assert.That(slot.TryToInt(unknown, out var seven) && seven == -7);
        }

        [Test]
        public void TryToInt_RejectsOtherValues()
        {
            var pool = Pool(4);
            var slot = pool.Claim(QmlFile("Doc"), Gauge()).Slot!;
            var other = pool.Claim(QmlFile("Doc"), Status()).Slot!;
            var small = slot.FromInt(0);

            Assert.That(slot.TryToInt(null, out _), Is.False);
            Assert.That(slot.TryToInt(0, out _), Is.False);
            Assert.That(slot.TryToInt(other.FromInt(0), out _), Is.False, "value of another pool type");

            pool.Claim(QmlFile("Doc"), Decl("Gauge", "Mode", ("Tiny", 0), ("Large", 5)));
            Assert.That(slot.TryToInt(small, out _), Is.False, "an entry the enum no longer has");
        }

        // --- file helpers ---

        [Test]
        public void RootOf_IsTheNearestQmlFolder()
        {
            var root = Path.Combine(_dir, "Doc", "qml");
            Assert.That(QmlEnumSlotFile.RootOf(Path.Combine(root, "Gauge.qml")), Is.EqualTo(root));
            Assert.That(QmlEnumSlotFile.RootOf(Path.Combine(root, "a", "b", "Gauge.qml")), Is.EqualTo(root));
            Assert.That(QmlEnumSlotFile.RootOf(Path.Combine(_dir, "loose", "Gauge.qml")), Is.EqualTo(Path.Combine(_dir, "loose")), "no qml folder: the file's folder");
        }

        [Test]
        public void IsInstalledPackage_LooksAtAllFoldersAbove()
        {
            var package = Path.Combine(_dir, "vl.thing", "1.0.0");
            var help = Path.Combine(package, "help", "Thing", "qml");
            var installed = Path.Combine(_dir, "nugets", "VL.Other.2.0.0");
            Directory.CreateDirectory(help);
            Directory.CreateDirectory(Path.Combine(installed, "qml"));
            Assert.That(QmlEnumSlotFile.IsInstalledPackage(help), Is.False, "a source folder");

            File.WriteAllText(Path.Combine(package, ".nupkg.metadata"), "{}");
            Assert.That(QmlEnumSlotFile.IsInstalledPackage(help), Is.True, "the qml folder of a help patch of a package");

            File.WriteAllText(Path.Combine(installed, "vl.other.2.0.0.nupkg"), "");
            Assert.That(QmlEnumSlotFile.IsInstalledPackage(Path.Combine(installed, "qml")), Is.False, "a .nupkg alone, as in a download folder");
            File.WriteAllText(Path.Combine(installed, "VL.Other.nuspec"), "");
            Assert.That(QmlEnumSlotFile.IsInstalledPackage(Path.Combine(installed, "qml")), Is.True);
        }

        [Test]
        public void Parse_SkipsInvalidAndRepeatedLines()
        {
            var lines = QmlEnumSlotFile.Parse(new[] { "# x", "", "A.E = 2", "B.E=3", "A.E = 4", "C.E = 2", "D.E = 0", "E.E = x", "= 1", "F.E = 33" }, 32);
            Assert.That(lines, Is.EqualTo(new[] { ("A.E", 2), ("B.E", 3) }));
        }

        [Test]
        public void Format_IsSortedAndReadsBack()
        {
            var map = new Dictionary<string, int> { ["b.E"] = 2, ["B.E"] = 1, ["A.E"] = 3 };
            var text = QmlEnumSlotFile.Format(map);
            Assert.That(text.Split('\n').Where(l => l.Length > 0 && l[0] != '#'), Is.EqualTo(new[] { "A.E = 3", "B.E = 1", "b.E = 2" }));
            Assert.That(QmlEnumSlotFile.Parse(text.Split('\n'), 32), Is.EquivalentTo(map.Select(p => (p.Key, p.Value))));
        }

        // --- helpers ---

        private static QmlEnumPool Pool(int count) => new(QmlEnumSlotTypes.All.Take(count).ToArray());

        private static QmlEnumDecl Decl(string type, string name, params (string Name, int Value)[] values)
            => new(type, name, values.Select(v => new QmlEnumValue(v.Name, v.Value)).ToImmutableArray());

        private static QmlEnumDecl Gauge() => Decl("Gauge", "Mode", ("Small", 0), ("Large", 5));

        private static QmlEnumDecl Status() => Decl("Card", "Status", ("Online", 0), ("Away", 1));

        private string QmlRoot(string owner)
        {
            var root = Path.Combine(_dir, owner, "qml");
            Directory.CreateDirectory(root);
            return root;
        }

        private string QmlFile(string owner, string file = "Gauge.qml") => Path.Combine(QmlRoot(owner), file);

        private string SlotFile(string owner) => Path.Combine(QmlRoot(owner), QmlEnumSlotFile.FileName);

        private void WriteSlotFile(string owner, params string[] lines) => File.WriteAllLines(SlotFile(owner), lines);

        private string[] Lines(string owner) => File.ReadAllLines(SlotFile(owner)).Where(l => l.Length > 0 && l[0] != '#').ToArray();

        // The NuGet cache keeps .nupkg.metadata next to the content of a package.
        private void MarkAsPackage(string owner) => File.WriteAllText(Path.Combine(Path.GetDirectoryName(QmlRoot(owner))!, ".nupkg.metadata"), "{}");

        private static object? Call(object target, string method)
            => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

        private static string TypeName(int number) => "QmlEnum" + number.ToString("00", CultureInfo.InvariantCulture);

        // --- generator of QmlEnumSlotTypes.cs ---

        /// <summary>The source of QmlEnumSlotTypes.cs for <paramref name="count"/> pool types, in the form of vvvv's dynamic enum template.</summary>
        internal static string SlotTypesSource(int count)
        {
            var s = new StringBuilder();
            void Line(string text = "") => s.Append(text).Append('\n');

            Line("// Generated by QmlEnumPoolTests.SlotTypes_MatchSlotCount (src/VL.Qt.Tests) from QmlEnumPool.SlotCount: do not edit.");
            Line("// Change the constant, build, and run the tests with VLQT_UPDATE_SNAPSHOT=1. Form of vvvv's template \"DynamicEnum\".");
            Line("using System;");
            Line("using System.Collections.Generic;");
            Line("using VL.Core;");
            Line("using VL.Core.CompilerServices;");
            Line("using VL.Core.Import;");
            Line("using VL.Lib.Collections;");
            Line("using VL.Qt.Elements.Qml;");
            Line();
            Line("namespace VL.Qt.Elements.Qml");
            Line("{");
            for (var n = 1; n <= count; n++)
            {
                var type = TypeName(n);
                var number = n.ToString(CultureInfo.InvariantCulture);
                if (n > 1)
                    Line();
                Line($"    /// <summary>Values of a QML enum: the type of its dropdown pins (pool type {number}, see vlqt-enum-slots.txt in the qml folder).</summary>");
                Line("    [Serializable, Smell(SymbolSmell.Advanced)]");
                Line($"    public sealed class {type} : DynamicEnumBase<{type}, {type}Definition>");
                Line("    {");
                Line($"        public {type}(string value)");
                Line("            : base(value)");
                Line("        {");
                Line("        }");
                Line();
                Line("        [CreateDefault]");
                Line($"        public static {type} CreateDefault() => CreateDefaultBase();");
                Line("    }");
                Line();
                Line($"    /// <summary>Entries of <see cref=\"{type}\"/>: the values of the QML enum that has pool type {number}.</summary>");
                Line("    [Smell(SymbolSmell.Hidden)]");
                Line($"    public sealed class {type}Definition : DynamicEnumDefinitionBase<{type}Definition>");
                Line("    {");
                Line($"        protected override IReadOnlyDictionary<string, object> GetEntries() => VL.Qt.Qml.QmlEnumPool.Shared.Entries({number});");
                Line();
                Line($"        protected override IObservable<object> GetEntriesChangedObservable() => VL.Qt.Qml.QmlEnumPool.Shared.EntriesChanged({number});");
                Line();
                Line("        protected override bool AutoSortAlphabetically => false;");
                Line("    }");
            }
            Line("}");
            Line();
            Line("namespace VL.Qt.Qml");
            Line("{");
            Line("    /// <summary>The pool types in slot order, for <see cref=\"QmlEnumPool\"/>.</summary>");
            Line("    internal static class QmlEnumSlotTypes");
            Line("    {");
            Line("        public static readonly QmlEnumSlotType[] All =");
            Line("        {");
            for (var n = 1; n <= count; n++)
                Line($"            new(typeof({TypeName(n)}), static v => new {TypeName(n)}(v)),");
            Line("        };");
            Line("    }");
            Line("}");
            return s.ToString();
        }
    }
}
