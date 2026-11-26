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
                Is.EqualTo("Property 't': type Int64 is not supported, ignored (use float, int, bool, string, Color4, Vector2/3/4 or RectangleF)"));
    }
}
