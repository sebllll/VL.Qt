using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Qml;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests.Qml
{
    /// <summary>The kinds of the QML node factory: list kinds and their element kinds, pin types and defaults.</summary>
    [TestFixture]
    internal sealed class QmlTypesTests
    {
        private static readonly QmlValueKind[] s_elementKinds = Enum.GetValues<QmlValueKind>().Where(QmlTypes.HasListKind).ToArray();

        [Test]
        public void EveryElementKindButFontHasOneListKind()
        {
            Assert.That(Enum.GetValues<QmlValueKind>().Where(k => !QmlTypes.IsList(k) && !QmlTypes.HasListKind(k)), Is.EqualTo(new[] { QmlValueKind.Font }));
            Assert.Throws<ArgumentOutOfRangeException>(() => QmlTypes.ListKind(QmlValueKind.Font));
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

        [TestCase("list<real>", QmlValueKind.RealList)]
        [TestCase("list<double>", QmlValueKind.RealList)]
        [TestCase("list<int>", QmlValueKind.IntList)]
        [TestCase("list<bool>", QmlValueKind.BoolList)]
        [TestCase("list<string>", QmlValueKind.StringList)]
        [TestCase("list<color>", QmlValueKind.ColorList)]
        [TestCase("list<vector2d>", QmlValueKind.Vector2DList)]
        [TestCase("list<vector3d>", QmlValueKind.Vector3DList)]
        [TestCase("list<vector4d>", QmlValueKind.Vector4DList)]
        [TestCase("list<rect>", QmlValueKind.RectList)]
        [TestCase("list<point>", QmlValueKind.PointList)]
        [TestCase("list<size>", QmlValueKind.SizeList)]
        [TestCase("list<url>", QmlValueKind.UrlList)]
        [TestCase("list<date>", QmlValueKind.DateList)]
        [TestCase("list< real >", QmlValueKind.RealList)]
        public void TryMap_ListOfASupportedType(string type, QmlValueKind expected)
        {
            Assert.That(QmlTypes.TryMap(type, out var kind), Is.True);
            Assert.That(kind, Is.EqualTo(expected));
        }

        [TestCase("list<Item>")]
        [TestCase("list<var>")]
        [TestCase("list<font>")]
        [TestCase("list<list<int>>")]
        [TestCase("list<>")]
        [TestCase("list<")]
        [TestCase("list<int")]
        [TestCase("list")]
        [TestCase("List<int>")]
        [TestCase("mylist<int>")]
        [TestCase(null)]
        public void TryMap_ListOfAnythingElse_HasNoPin(string? type)
        {
            Assert.That(QmlTypes.TryMap(type, out _), Is.False);
        }

        [Test]
        public void Url_IsAPathPin_WithTheEmptyPathAsDefault()
        {
            Assert.That(QmlTypes.TryMap("url", out var kind), Is.True);
            Assert.That(kind, Is.EqualTo(QmlValueKind.Url));
            Assert.That(QmlTypes.ClrType(kind), Is.EqualTo(typeof(Path)));
            Assert.That(QmlTypes.TypeDefault(kind), Is.EqualTo(new Path(string.Empty)));
        }

        [Test]
        public void DateAndFont_ArePinsOfTheirTypes_WithNoDateAndTheTextFontAsDefault()
        {
            Assert.That(QmlTypes.TryMap("date", out var date), Is.True);
            Assert.That(QmlTypes.TryMap("font", out var font), Is.True);
            Assert.That((QmlTypes.ClrType(date), QmlTypes.TypeDefault(date)), Is.EqualTo((typeof(DateTime), (object)DateTime.MinValue)));
            Assert.That((QmlTypes.ClrType(font), QmlTypes.TypeDefault(font)), Is.EqualTo((typeof(VL.Qt.Elements.Font), (object)new VL.Qt.Elements.Font())));
            Assert.That(new VL.Qt.Elements.Font(), Is.EqualTo(new VL.Qt.Elements.Font { Family = "", Size = 0.12f, Weight = 400, Italic = false }));
            var when = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Local);
            Assert.That(QmlTypes.TryCoerce(date, when, out var r) && r.Equals(when), Is.True);
            Assert.That(QmlTypes.TryCoerce(font, "Arial", out _), Is.False);
        }

        [Test]
        public void UrlToPath_FileUrlIsTheLocalPath_OtherSchemesStay_RelativeIsResolvedAgainstTheFolder()
        {
            var parent = System.IO.Path.GetTempPath();
            var folder = System.IO.Path.Combine(parent, "vlqt urls");
            var local = System.IO.Path.Combine(folder, "a b ä.png");
            Assert.That(QmlTypes.UrlToPath(new Uri(local).AbsoluteUri, folder).Value, Is.EqualTo(local), "file URL, encoded");
            Assert.That(QmlTypes.UrlToPath(local, folder).Value, Is.EqualTo(local), "a local path");
            Assert.That(QmlTypes.UrlToPath("https://example.com/a.png", folder).Value, Is.EqualTo("https://example.com/a.png"));
            Assert.That(QmlTypes.UrlToPath("qrc:/img/a.png", folder).Value, Is.EqualTo("qrc:/img/a.png"));
            Assert.That(QmlTypes.UrlToPath("img/a.png", folder).Value, Is.EqualTo(System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, "img/a.png"))));
            Assert.That(QmlTypes.UrlToPath("../a.png", folder).Value, Is.EqualTo(System.IO.Path.Combine(parent, "a.png")));
            Assert.That(QmlTypes.UrlToPath("img/a.png", null).Value, Is.EqualTo("img/a.png"), "no folder: stays relative");
            Assert.That(QmlTypes.UrlToPath(string.Empty, folder).Value, Is.Empty);
        }

        [Test]
        public void TryCoerce_Url_TakesTheTextFromQml_AndAPath()
        {
            var folder = System.IO.Path.GetTempPath();
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.Url, "img/a.png", out var r, folder), Is.True);
            Assert.That(r, Is.EqualTo(QmlTypes.UrlToPath("img/a.png", folder)));
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.Url, new Path("x.png"), out r), Is.True);
            Assert.That(r, Is.EqualTo(new Path("x.png")));
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.Url, 1f, out _), Is.False);
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.UrlList, (new[] { "https://example.com/a.png", "b.png" }).ToSpread(), out r, folder), Is.True);
            Assert.That(r, Is.EqualTo((new[] { new Path("https://example.com/a.png"), QmlTypes.UrlToPath("b.png", folder) }).ToSpread()));
        }

        [Test]
        public void TryCoerce_List_TakesItsOwnSpread_AndAnEmptyOne()
        {
            var points = (new[] { new Vector2(1, 2) }).ToSpread();
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.PointList, points, out var r), Is.True);
            Assert.That(r, Is.SameAs(points));
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.SizeList, points, out r), Is.True, "point and size share the spread");
            Assert.That(r, Is.SameAs(points));

            foreach (var kind in Enum.GetValues<QmlValueKind>().Where(QmlTypes.IsList))
            {
                Assert.That(QmlTypes.TryCoerce(kind, Spread<string>.Empty, out r), Is.True, kind.ToString());
                Assert.That(r, Is.InstanceOf(QmlTypes.ClrType(kind)).And.Empty, kind.ToString());
                Assert.That(QmlTypes.TryCoerce(kind, (Array.Empty<bool>()).ToSpread(), out r), Is.True, kind.ToString());
            }
        }

        [Test]
        public void TryCoerce_List_ConvertsIntAndFloatLikeTheScalars()
        {
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.RealList, (new[] { 1, -2 }).ToSpread(), out var r), Is.True);
            Assert.That(r, Is.EqualTo((new[] { 1f, -2f }).ToSpread()));
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.IntList, (new[] { 1f, 2.6f }).ToSpread(), out r), Is.True);
            Assert.That(r, Is.EqualTo((new[] { 1, 3 }).ToSpread()));
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.IntList, (new[] { 1f, float.NaN }).ToSpread(), out r), Is.False);
            Assert.That(r, Is.Empty);
        }

        [Test]
        public void TryCoerce_List_RefusesWhatDoesNotFit()
        {
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.StringList, (new[] { 1f }).ToSpread(), out var r), Is.False);
            Assert.That(r, Is.InstanceOf<Spread<string>>().And.Empty);
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.RealList, (new[] { Vector2.One }).ToSpread(), out _), Is.False);
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.RealList, 1f, out _), Is.False, "a scalar is no list");
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.RealList, null, out _), Is.False);
            Assert.That(QmlTypes.TryCoerce(QmlValueKind.Real, (new[] { 1f }).ToSpread(), out _), Is.False, "a list is no scalar");
        }

        [Test]
        public void SignalPinType_OfAListArgument_IsASpreadOfSpreads()
        {
            Assert.That(QmlTypes.SignalPinType(QmlSignalShape.Values, QmlValueKind.RealList), Is.EqualTo(typeof(Spread<Spread<float>>)));
            Assert.That(QmlTypes.SignalPinType(QmlSignalShape.Values, QmlValueKind.PointList), Is.EqualTo(typeof(Spread<Spread<Vector2>>)));
        }

        [Test]
        public void ElementAndListKindRefuseTheWrongSide()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => QmlTypes.ElementKind(QmlValueKind.Real));
            Assert.Throws<ArgumentOutOfRangeException>(() => QmlTypes.ListKind(QmlValueKind.RealList));
        }
    }
}
