using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests
{
    /// <summary>QmlProperties: the converted snapshot of the Properties dictionary, change detection by content, problem lines.</summary>
    public class QmlPropertiesTests
    {
        [Test]
        public void NullAndEmpty_AreTheEmptyInstance()
        {
            Assert.That(QmlProperties.From(null), Is.SameAs(QmlProperties.Empty));
            Assert.That(QmlProperties.From(new Dictionary<string, object>()), Is.SameAs(QmlProperties.Empty));
            Assert.That(QmlProperties.Update(QmlProperties.Empty, null), Is.SameAs(QmlProperties.Empty));
            Assert.That(QmlProperties.Update(QmlProperties.From(new() { ["a"] = 1 }), new Dictionary<string, object>()), Is.SameAs(QmlProperties.Empty));
        }

        [Test]
        public void From_ConvertsEntries_AndListsProblems()
        {
            var p = QmlProperties.From(new()
            {
                ["ok"] = 1f,
                ["long"] = 5L,
                [""] = 1,
                ["null"] = null!,
            });
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(p.TryGetValue("ok", out var v) && v.ValueType == Protocol.ValueType.F32, Is.True);
            Assert.That(p.TryGetValue("long", out _), Is.False);
            var lines = p.Problems.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.That(lines, Has.Length.EqualTo(3));
            Assert.That(lines.Any(l => l.StartsWith("Property 'long': type Int64")), Is.True);
            Assert.That(lines.Any(l => l.StartsWith("Property name '' is empty")), Is.True);
            Assert.That(lines.Any(l => l == "Property 'null': the value is not set, ignored"), Is.True);
        }

        [Test]
        public void NameLimit_InUtf8Bytes()
        {
            Assert.That(Protocol.IsValidName(new string('a', Protocol.UniformNameMax)), Is.True);
            Assert.That(Protocol.IsValidName(new string('a', Protocol.UniformNameMax + 1)), Is.False);
            Assert.That(Protocol.IsValidName(new string('ä', Protocol.UniformNameMax / 2 + 1)), Is.False, "2 bytes per char");
            Assert.That(Protocol.IsValidName(null), Is.False);
            Assert.That(Protocol.IsValidName(""), Is.False);
            Assert.That(QmlProperties.From(new() { [new string('a', 300)] = 1 }).Problems, Does.Contain("longer than 255 bytes"));
        }

        [Test]
        public void Update_SameContent_KeepsTheInstance()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["b"] = "x" };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            Assert.That(QmlProperties.Update(p, d), Is.SameAs(p));
            Assert.That(QmlProperties.Update(p, new Dictionary<string, object> { ["b"] = "x", ["a"] = 1f }), Is.SameAs(p), "another dictionary, same content, other order");
        }

        [Test]
        public void Update_MutatedInPlace_IsSeen()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            d["a"] = 2f;
            var q = QmlProperties.Update(p, d);
            Assert.That(q, Is.Not.SameAs(p));
            Assert.That(q.TryGetValue("a", out var v) && v.A == 2f, Is.True);
            d["b"] = true;
            Assert.That(QmlProperties.Update(q, d), Is.Not.SameAs(q), "added key");
            d.Remove("b");
            Assert.That(QmlProperties.Update(QmlProperties.Update(q, new() { ["a"] = 2f, ["b"] = true }), d).Count, Is.EqualTo(1), "removed key");
        }

        [Test]
        public void Update_TypeChange_SameNumber_IsAChange()
        {
            var p = QmlProperties.Update(QmlProperties.Empty, new() { ["a"] = 3f });
            Assert.That(QmlProperties.Update(p, new() { ["a"] = 3 }), Is.Not.SameAs(p));
        }

        [Test]
        public void Update_InvalidEntries_DoNotCauseRebuilds()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["bad"] = 5L };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(QmlProperties.Update(p, d), Is.SameAs(p));
            d["bad2"] = 6L;
            Assert.That(QmlProperties.Update(p, d), Is.Not.SameAs(p), "one more invalid entry changes the problems");
            d.Remove("bad");
            d.Remove("bad2");
            d["a"] = 1f;
            Assert.That(QmlProperties.Update(p, d).Problems, Is.Empty);
        }

        [Test]
        public void Update_RejectedEntryChanges_GivesANewProblemText()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["bad"] = new double[] { 1 } };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            Assert.That(p.Problems, Does.Contain("Property 'bad': type Double[]"));

            d["bad"] = Matrix.Identity;
            var q = QmlProperties.Update(p, d);
            Assert.That(q, Is.Not.SameAs(p), "another unsupported type");
            Assert.That(q.Problems, Does.Contain("type Matrix"));

            d.Remove("bad");
            d["colr"] = Matrix.Identity;
            var r = QmlProperties.Update(q, d);
            Assert.That(r, Is.Not.SameAs(q), "renamed key");
            Assert.That(r.Problems, Does.Contain("Property 'colr'").And.Not.Contain("'bad'"));

            d["colr"] = null!;
            var s = QmlProperties.Update(r, d);
            Assert.That(s, Is.Not.SameAs(r), "unsupported value to null");
            Assert.That(s.Problems, Does.Contain("the value is not set"));

            var tooLong = new string('a', Protocol.UniformNameMax + 1);
            d.Remove("colr");
            d[tooLong] = 1f;
            var t = QmlProperties.Update(s, d);
            Assert.That(t.Problems, Does.Contain("longer than"));
            Assert.That(QmlProperties.Update(t, d), Is.SameAs(t), "the same refused entries keep the snapshot");
        }

        [Test]
        public void Update_RejectedEntriesUnchanged_AllocatesNothing()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["bad"] = Matrix.Identity, ["colr"] = null! };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            Action frame = () => QmlProperties.Update(p, d);
            Assert.That(Allocations.Fewest(frame), Is.EqualTo(0L));
        }
    }
}
