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

namespace VL.Qt.Tests.Elements
{
    /// <summary>QmlValue: conversion of patch values to wire values, exact equality, the lines for values that are refused.</summary>
    public class QmlValueTests
    {
        private static IEnumerable<TestCaseData> Supported()
        {
            yield return new TestCaseData(1.5f, Protocol.ValueType.F32);
            yield return new TestCaseData(2.25d, Protocol.ValueType.F32);
            yield return new TestCaseData(-7, Protocol.ValueType.I32);
            yield return new TestCaseData(true, Protocol.ValueType.Bool);
            yield return new TestCaseData("text", Protocol.ValueType.String);
            yield return new TestCaseData("", Protocol.ValueType.String);
            yield return new TestCaseData(new Color4(1, 0.5f, 0.25f, 0.75f), Protocol.ValueType.Color);
            yield return new TestCaseData(new Vector2(1, 2), Protocol.ValueType.Vec2);
            yield return new TestCaseData(new Vector3(1, 2, 3), Protocol.ValueType.Vec3);
            yield return new TestCaseData(new Vector4(1, 2, 3, 4), Protocol.ValueType.Vec4);
            yield return new TestCaseData(new RectangleF(1, 2, 3, 4), Protocol.ValueType.Rectf);
            yield return new TestCaseData(new Path("C:/a.png"), Protocol.ValueType.String);
        }

        [TestCaseSource(nameof(Supported))]
        public void TryConvert_EachSupportedType(object value, ushort wireType)
        {
            Assert.That(QmlValue.TryConvert(value, out var v), Is.True);
            Assert.That(v.ValueType, Is.EqualTo(wireType));
        }

        [Test]
        public void TryConvert_Fields()
        {
            QmlValue.TryConvert(new Color4(0.1f, 0.2f, 0.3f, 0.4f), out var c);
            Assert.That((c.A, c.B, c.C, c.D), Is.EqualTo((0.1f, 0.2f, 0.3f, 0.4f)));
            QmlValue.TryConvert(new RectangleF(10, 20, 30, 40), out var r);
            Assert.That((r.A, r.B, r.C, r.D), Is.EqualTo((10f, 20f, 30f, 40f)));
            QmlValue.TryConvert(new Vector3(1, 2, 3), out var v3);
            Assert.That((v3.A, v3.B, v3.C), Is.EqualTo((1f, 2f, 3f)));
            QmlValue.TryConvert(2.5d, out var d);
            Assert.That(d.A, Is.EqualTo(2.5f));
            QmlValue.TryConvert(true, out var b);
            Assert.That(b.Integer, Is.EqualTo(1));
            QmlValue.TryConvert(-5, out var i);
            Assert.That(i.Integer, Is.EqualTo(-5));
            QmlValue.TryConvert("hi", out var s);
            Assert.That(s.Text, Is.EqualTo("hi"));
            QmlValue.TryConvert(new Path("img/a b.png"), out var p);
            Assert.That(p.Text, Is.EqualTo("img/a b.png"), "the text of the path, as it is");
            Assert.That(p, Is.EqualTo(s with { Text = "img/a b.png" }), "the same wire value as the string");
        }

        private static IEnumerable<TestCaseData> Unsupported()
        {
            yield return new TestCaseData(null);
            yield return new TestCaseData(5L);
            yield return new TestCaseData((byte)1);
            yield return new TestCaseData(new object());
            yield return new TestCaseData(DateTime.Now);
            yield return new TestCaseData(new Int2(1, 2));
        }

        [TestCaseSource(nameof(Unsupported))]
        public void TryConvert_Unsupported(object? value) => Assert.That(QmlValue.TryConvert(value, out _), Is.False);

        [Test]
        public void TryConvert_LongString_IsSupported() =>
            Assert.That(QmlValue.TryConvert(new string('x', 200_000), out _), Is.True);

        [Test]
        public void Equality_IsExact_NotNearEqual()
        {
            QmlValue.TryConvert(new Vector2(1, 2), out var a);
            QmlValue.TryConvert(new Vector2(1, MathF.BitIncrement(2)), out var b);
            QmlValue.TryConvert(new Vector2(1, 2), out var c);
            Assert.That(a, Is.Not.EqualTo(b), "Stride's Vector2 equality is a near-equal compare; the wire value must not swallow tiny changes");
            Assert.That(a, Is.EqualTo(c));
        }

        [Test]
        public void Equality_SameNumber_DifferentWireType()
        {
            QmlValue.TryConvert(3f, out var f);
            QmlValue.TryConvert(3, out var i);
            Assert.That(f, Is.Not.EqualTo(i));
        }

        [Test]
        public void Describe_Null() =>
            Assert.That(QmlValue.Describe("x", null), Is.EqualTo("Property 'x': the value is not set, ignored"));

        [Test]
        public void Describe_UnsupportedType() =>
            Assert.That(QmlValue.Describe("t", 5L),
                Is.EqualTo("Property 't': type Int64 is not supported, ignored "
                    + "(use float, int, bool, string, Path, Color4, Vector2/3/4 or RectangleF, or a Spread of one of them)"));

        [Test]
        public void Describe_UnsupportedSpread_NamesTheElementType() =>
            Assert.That(QmlValue.Describe("t", new[] { Matrix.Identity }.ToSpread()), Does.StartWith("Property 't': type Spread<Matrix> is not supported"));

        [Test]
        public void Describe_TooLongList_DoesNotDependOnTheCount()
        {
            var a = QmlValue.Describe("t", new float[Protocol.ListValueMaxCount + 1].ToSpread());
            Assert.That(a, Is.EqualTo($"Property 't': a list can have at most {Protocol.ListValueMaxCount} elements, ignored"));
            Assert.That(QmlValue.Describe("t", new float[Protocol.ListValueMaxCount + 9].ToSpread()), Is.EqualTo(a), "QmlProperties keeps the text per name and type");
        }

        // --- lists ---

        private static IEnumerable<TestCaseData> Lists()
        {
            yield return new TestCaseData(new[] { 1f, 2f }.ToSpread(), Protocol.ValueType.F32);
            yield return new TestCaseData(new[] { 1, 2 }.ToSpread(), Protocol.ValueType.I32);
            yield return new TestCaseData(new[] { true, false }.ToSpread(), Protocol.ValueType.Bool);
            yield return new TestCaseData(new[] { "a", "b" }.ToSpread(), Protocol.ValueType.String);
            yield return new TestCaseData(new[] { new Color4(1, 0, 0, 1) }.ToSpread(), Protocol.ValueType.Color);
            yield return new TestCaseData(new[] { new Vector2(1, 2) }.ToSpread(), Protocol.ValueType.Vec2);
            yield return new TestCaseData(new[] { new Vector3(1, 2, 3) }.ToSpread(), Protocol.ValueType.Vec3);
            yield return new TestCaseData(new[] { new Vector4(1, 2, 3, 4) }.ToSpread(), Protocol.ValueType.Vec4);
            yield return new TestCaseData(new[] { new RectangleF(1, 2, 3, 4) }.ToSpread(), Protocol.ValueType.Rectf);
            yield return new TestCaseData(Spread<float>.Empty, Protocol.ValueType.F32);
            yield return new TestCaseData(Spread<string>.Empty, Protocol.ValueType.String);
            yield return new TestCaseData(new[] { new Path("a.png"), new Path("https://example.com/b.png") }.ToSpread(), Protocol.ValueType.String);
        }

        [TestCaseSource(nameof(Lists))]
        public void TryConvert_Spread_IsAListOfItsElementType(object spread, ushort elementType)
        {
            Assert.That(QmlValue.TryConvert(spread, out var v), Is.True);
            Assert.That(v.ValueType, Is.EqualTo(Protocol.ValueType.List));
            Assert.That(v.Integer, Is.EqualTo((int)elementType));
            Assert.That(v.Items, Is.SameAs(spread), "the spread is kept, not copied");
        }

        [Test]
        public void TryConvert_Spread_Unsupported()
        {
            Assert.That(QmlValue.TryConvert(new[] { 1L }.ToSpread(), out _), Is.False);
            Assert.That(QmlValue.TryConvert(new[] { Matrix.Identity }.ToSpread(), out _), Is.False);
            Assert.That(QmlValue.TryConvert(new object[] { 1f }.ToSpread(), out _), Is.False);
            Assert.That(QmlValue.TryConvert(new[] { new Int2(1, 2) }.ToSpread(), out _), Is.False);
        }

        [Test]
        public void TryConvert_ListLimit_IsTheProtocolMax()
        {
            Assert.That(QmlValue.TryConvert(new float[Protocol.ListValueMaxCount].ToSpread(), out _), Is.True);
            Assert.That(QmlValue.TryConvert(new float[Protocol.ListValueMaxCount + 1].ToSpread(), out _), Is.False);
            Assert.That(QmlValue.TryConvert(new Vector4[Protocol.ListValueMaxCount + 1].ToSpread(), out _), Is.False);
            Assert.That(QmlValue.TryConvert(new string[Protocol.ListValueMaxCount + 1].ToSpread(), out _), Is.False);
        }

        private static QmlValue Convert(object value)
        {
            Assert.That(QmlValue.TryConvert(value, out var v), Is.True);
            return v;
        }

        [TestCaseSource(nameof(Lists))]
        public void Equality_Lists_ByContent_NotByInstance(object spread, ushort _)
        {
            var copy = Rebuild(spread);
            Assert.That(Convert(copy), Is.EqualTo(Convert(spread)));
            Assert.That(Convert(copy).GetHashCode(), Is.EqualTo(Convert(spread).GetHashCode()));
        }

        // A second instance with the same elements.
        private static object Rebuild(object spread) => spread switch
        {
            Spread<float> s => s.ToArray().ToSpread(),
            Spread<int> s => s.ToArray().ToSpread(),
            Spread<bool> s => s.ToArray().ToSpread(),
            Spread<string> s => s.ToArray().Select(x => new string(x.AsSpan())).ToArray().ToSpread(),
            Spread<Color4> s => s.ToArray().ToSpread(),
            Spread<Vector2> s => s.ToArray().ToSpread(),
            Spread<Vector3> s => s.ToArray().ToSpread(),
            Spread<Vector4> s => s.ToArray().ToSpread(),
            Spread<RectangleF> s => s.ToArray().ToSpread(),
            Spread<Path> s => s.ToArray().Select(x => new Path(new string(x.Value.AsSpan()))).ToArray().ToSpread(),
            _ => throw new ArgumentException(spread.GetType().Name),
        };

        [Test]
        public void Equality_Lists_DifferentElementOrCount_IsNotEqual()
        {
            var a = Convert(new[] { 1f, 2f, 3f }.ToSpread());
            Assert.That(Convert(new[] { 1f, 2f, 4f }.ToSpread()), Is.Not.EqualTo(a));
            Assert.That(Convert(new[] { 1f, 2f }.ToSpread()), Is.Not.EqualTo(a));
            Assert.That(Convert(new[] { 1f, 2f, 3f, 3f }.ToSpread()), Is.Not.EqualTo(a));
            Assert.That(Convert(Spread<float>.Empty), Is.Not.EqualTo(a));
            Assert.That(Convert(Spread<float>.Empty), Is.EqualTo(Convert(Spread<float>.Empty)));
            Assert.That(Convert(new[] { "a", "b" }.ToSpread()), Is.Not.EqualTo(Convert(new[] { "a", "B" }.ToSpread())), "strings are compared ordinal");
            Assert.That(Convert(new[] { true, false }.ToSpread()), Is.Not.EqualTo(Convert(new[] { true, true }.ToSpread())));
        }

        [Test]
        public void Equality_Lists_OtherElementType_SameBits_IsNotEqual()
        {
            Assert.That(Convert(Spread<float>.Empty), Is.Not.EqualTo(Convert(Spread<int>.Empty)));
            Assert.That(Convert(new[] { new Vector2(1, 2) }.ToSpread()), Is.Not.EqualTo(Convert(new[] { new Vector4(1, 2, 0, 0) }.ToSpread())));
            Assert.That(Convert(new[] { 1f }.ToSpread()), Is.Not.EqualTo(Convert(1f)));
        }

        [Test]
        public void Equality_Lists_IsExact_NotNearEqual()
        {
            Assert.That(Convert(new[] { new Vector2(1, 2) }.ToSpread()),
                Is.Not.EqualTo(Convert(new[] { new Vector2(1, MathF.BitIncrement(2)) }.ToSpread())));
            Assert.That(Convert(new[] { new Color4(0, 0, 0, 1) }.ToSpread()),
                Is.Not.EqualTo(Convert(new[] { new Color4(0, 0, 0, MathF.BitIncrement(1)) }.ToSpread())));
            Assert.That(Convert(new[] { new RectangleF(1, 2, 3, 4) }.ToSpread()),
                Is.Not.EqualTo(Convert(new[] { new RectangleF(1, 2, 3, MathF.BitIncrement(4)) }.ToSpread())));
        }

        [Test]
        public void Equality_Lists_FloatsLikeTheScalars()
        {
            // float.Equals: NaN equals NaN (a NaN list does not resend every frame), 0 equals -0, like the scalar compare.
            Assert.That(Convert(new[] { float.NaN }.ToSpread()), Is.EqualTo(Convert(new[] { float.NaN }.ToSpread())));
            Assert.That(Convert(new[] { 0f }.ToSpread()), Is.EqualTo(Convert(new[] { -0f }.ToSpread())));
            Assert.That(Convert(new[] { 0f }.ToSpread()).GetHashCode(), Is.EqualTo(Convert(new[] { -0f }.ToSpread()).GetHashCode()));
            Assert.That(Convert(float.NaN), Is.EqualTo(Convert(float.NaN)));
        }

        [Test]
        public void Equality_Lists_AllocatesNothing()
        {
            var a = Convert(Enumerable.Range(0, 1000).Select(i => new Vector2(i, -i)).ToArray().ToSpread());
            var b = Convert(Enumerable.Range(0, 1000).Select(i => new Vector2(i, -i)).ToArray().ToSpread());
            var c = Convert(Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? "a" : "b").ToArray().ToSpread());
            var d = Convert(Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? "a" : "b").ToArray().ToSpread());
            bool eq = a == b && c == d && QmlValue.TryConvert(a.Items, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++)
                eq &= a == b && c == d && QmlValue.TryConvert(a.Items, out _);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
            Assert.That(eq, Is.True);
        }
    }
}
