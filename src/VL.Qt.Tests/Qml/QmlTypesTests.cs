using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Qml;

namespace VL.Qt.Tests.Qml
{
    /// <summary>The kinds of the QML node factory: list kinds and their element kinds, pin types and defaults.</summary>
    [TestFixture]
    internal sealed class QmlTypesTests
    {
        private static readonly QmlValueKind[] s_elementKinds = Enum.GetValues<QmlValueKind>().Where(k => !QmlTypes.IsList(k)).ToArray();

        [Test]
        public void EveryElementKindHasOneListKind()
        {
            var lists = Enum.GetValues<QmlValueKind>().Where(QmlTypes.IsList).ToArray();
            Assert.That(lists, Has.Length.EqualTo(s_elementKinds.Length));
            foreach (var element in s_elementKinds)
            {
                var list = QmlTypes.ListKind(element);
                Assert.That(QmlTypes.IsList(list), $"{element} -> {list}");
                Assert.That(list.ToString(), Is.EqualTo(element + "List"));
                Assert.That(QmlTypes.ElementKind(list), Is.EqualTo(element));
            }
        }

        [Test]
        public void ListPinIsSpreadOfTheElementPin()
        {
            foreach (var element in s_elementKinds)
            {
                var list = QmlTypes.ListKind(element);
                Assert.That(QmlTypes.ClrType(list), Is.EqualTo(typeof(Spread<>).MakeGenericType(QmlTypes.ClrType(element))), list.ToString());
                Assert.That(QmlTypes.TypeDefault(list), Is.InstanceOf(QmlTypes.ClrType(list)).And.Empty, list.ToString());
            }
            Assert.That(QmlTypes.ClrType(QmlValueKind.PointList), Is.EqualTo(typeof(Spread<Vector2>)));
        }

        [Test]
        public void ElementAndListKindRefuseTheWrongSide()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => QmlTypes.ElementKind(QmlValueKind.Real));
            Assert.Throws<ArgumentOutOfRangeException>(() => QmlTypes.ListKind(QmlValueKind.RealList));
        }
    }
}
