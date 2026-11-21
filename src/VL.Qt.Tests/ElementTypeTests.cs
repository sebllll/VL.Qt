using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.IO;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    public class ElementTypeTests
    {
        private static readonly Type[] AllTypes =
            { typeof(Group), typeof(Elements.Rectangle), typeof(Text), typeof(Image), typeof(Row), typeof(Column), typeof(Video), typeof(Button), typeof(SpinBox), typeof(CheckBox), typeof(Elements.Switch), typeof(Elements.Slider), typeof(TextField), typeof(ShaderEffect), typeof(Shape) };

        [TestCaseSource(nameof(AllTypes))]
        public void SealedAndInitOnly(Type t)
        {
            Assert.That(t.IsSealed, Is.True);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var set = p.SetMethod;
                if (set is null) continue;
                var isInit = set.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                Assert.That(isInit, Is.True, $"{t.Name}.{p.Name} must be init-only");
            }
        }

        [TestCaseSource(nameof(AllTypes))]
        public void CommonDefaults(Type t)
        {
            var e = (Element)Activator.CreateInstance(t)!;
            // All elements: TopLeft, like QtNative default.
            Assert.That(e.Anchor, Is.EqualTo(RectangleAnchor.TopLeft));
            Assert.That(e.Opacity, Is.EqualTo(1f));
            Assert.That(e.Transform, Is.EqualTo(Matrix.Identity));
            Assert.That(e.Children.IsDefault, Is.False);
            Assert.That(e.Children, Is.Empty);
        }

        [Test]
        public void TypeIds()
        {
            Assert.That(new Group().Type, Is.EqualTo(ElementType.Item));
            Assert.That(new Elements.Rectangle().Type, Is.EqualTo(ElementType.Rectangle));
            Assert.That(new Text().Type, Is.EqualTo(ElementType.Text));
            Assert.That(new Image().Type, Is.EqualTo(ElementType.Image));
            Assert.That(new Row().Type, Is.EqualTo(ElementType.Row));
            Assert.That(new Column().Type, Is.EqualTo(ElementType.Column));
            Assert.That(new Shape().Type, Is.EqualTo(ElementType.Shape));
            Assert.That((ushort)ElementType.Shape, Is.EqualTo(26));
            Assert.That((ushort)ElementType.Rectangle, Is.EqualTo(Protocol.ElementType.Rectangle));
            Assert.That((ushort)ElementType.ShaderEffect, Is.EqualTo(Protocol.ElementType.Shader));
        }

        [Test]
        public void ShapeDefaults()
        {
            var s = new Shape();
            Assert.That(s.Points, Is.Empty);
            Assert.That(s.Closed, Is.False);
            Assert.That(s.Path, Is.Null);
            Assert.That(s.Fill, Is.EqualTo(new Color4(0, 0, 0, 0)));
            Assert.That(s.StrokeColor, Is.EqualTo(Color4.White));
            Assert.That(s.StrokeWidth, Is.EqualTo(0f));
            Assert.That(s.Cap, Is.EqualTo(ShapeCap.Flat));
            Assert.That(s.Join, Is.EqualTo(ShapeJoin.Miter));
            Assert.That(s.MiterLimit, Is.EqualTo(4f));
            Assert.That(s.Dash, Is.Empty);
            Assert.That(s.DashOffset, Is.EqualTo(0f));
            Assert.That(s.TrimStart, Is.EqualTo(0f));
            Assert.That(s.TrimEnd, Is.EqualTo(1f));
            Assert.That(s.TrimOffset, Is.EqualTo(0f));
            Assert.That(s.FillRule, Is.EqualTo(ShapeFillRule.NonZero));
        }

        [Test]
        public void ShapeEnums_AreProtocolValues()
        {
            Assert.That((int)ShapeCap.Square, Is.EqualTo(1));
            Assert.That((int)ShapeCap.Round, Is.EqualTo(2));
            Assert.That((int)ShapeJoin.Bevel, Is.EqualTo(1));
            Assert.That((int)ShapeJoin.Round, Is.EqualTo(2));
            Assert.That((int)ShapeFillRule.EvenOdd, Is.EqualTo(1));
        }

        [Test]
        public void ShapeExtent_IsMaxOfTheGeometryAndNotNegative()
        {
            Assert.That(Shape.ExtentOf(VL.Lib.Collections.Spread.Create(new Vector2(1, -2), new Vector2(-3, 4))), Is.EqualTo(new Vector2(1, 4)));
            Assert.That(Shape.ExtentOf(VL.Lib.Collections.Spread<Vector2>.Empty), Is.EqualTo(Vector2.Zero));
            Assert.That(Shape.ExtentOf(VL.Qt.Paths.QtPath.Empty), Is.EqualTo(Vector2.Zero));
            Assert.That(Shape.ExtentOf(VL.Qt.Paths.QtPath.Empty.MoveTo(-1, 2).CubicTo(0, 0, 5, 1, 3, 6)), Is.EqualTo(new Vector2(5, 6)));
        }

        [Test]
        public void RectangleDefaults()
        {
            var r = new Elements.Rectangle();
            Assert.That(r.Fill, Is.EqualTo(Color4.White));
            Assert.That(r.Radius, Is.EqualTo(0f));
            Assert.That(r.StrokeColor, Is.EqualTo(Color4.Black));
            Assert.That(r.StrokeWidth, Is.EqualTo(0f));
        }

        [Test]
        public void TextDefaults()
        {
            var t = new Text();
            Assert.That(t.Content, Is.EqualTo(string.Empty));
            Assert.That(t.FontFamily, Is.EqualTo(string.Empty));
            Assert.That(t.FontSize, Is.EqualTo(0.12f));
            Assert.That(t.Weight, Is.EqualTo(FontWeight.Normal));
            Assert.That(t.Italic, Is.False);
            Assert.That(t.Color, Is.EqualTo(Color4.Black));
            Assert.That(t.HAlign, Is.EqualTo(TextHorizontalAlignment.Left));
            Assert.That(t.VAlign, Is.EqualTo(VerticalAlignment.Top));
            Assert.That(t.Wrap, Is.EqualTo(TextWrap.NoWrap));
            Assert.That(t.Elide, Is.EqualTo(TextElide.None));
            Assert.That(t.LineHeight, Is.EqualTo(1f));
            Assert.That(t.LetterSpacing, Is.EqualTo(0f));
            Assert.That(t.Autofit, Is.EqualTo(TextAutofit.Fixed));
        }

        [Test]
        public void TextEnums_AreQtValues()
        {
            Assert.That((int)FontWeight.Thin, Is.EqualTo(100));
            Assert.That((int)FontWeight.Black, Is.EqualTo(900));
            Assert.That((int)TextWrap.Wrap, Is.EqualTo(4));
            Assert.That((int)TextElide.None, Is.EqualTo(3));
            Assert.That((int)TextAutofit.Fit, Is.EqualTo(3));
            Assert.That((int)TextHorizontalAlignment.Center, Is.EqualTo(0x4));
            Assert.That((int)VerticalAlignment.Center, Is.EqualTo(0x80));
        }

        [Test]
        public void ImageDefaults()
        {
            var i = new Image();
            Assert.That(i.Source, Is.Null);
            Assert.That(i.FillMode, Is.EqualTo(ImageFillMode.Stretch));
            Assert.That(i.Smooth, Is.True);
            Assert.That((int)ImageFillMode.Tile, Is.EqualTo(3));
        }

        [Test]
        public void RowColumnDefaults()
        {
            var r = new Row();
            Assert.That(r.Spacing, Is.EqualTo(0f));
            Assert.That(r.Padding, Is.EqualTo(0f));
            Assert.That(r.ChildAlignment, Is.EqualTo(VerticalAlignment.Top));
            var c = new Column();
            Assert.That(c.Spacing, Is.EqualTo(0f));
            Assert.That(c.Padding, Is.EqualTo(0f));
            Assert.That(c.ChildAlignment, Is.EqualTo(HorizontalAlignment.Left));
        }

        [Test]
        public void Containers_KeepChildren()
        {
            var a = new Elements.Rectangle();
            var b = new Text { Content = "x" };
            var kids = ImmutableArray.Create<Element>(a, b);
            Assert.That(new Group { Children = kids }.Children, Is.EqualTo(kids));
            Assert.That(new Row { Children = kids }.Children, Is.EqualTo(kids));
            Element col = new Column { Children = kids };
            Assert.That(col.Children, Is.EqualTo(kids));
            Assert.That(new Group { Children = default }.Children, Is.Empty);
        }

        [Test]
        public void Leaves_RejectChildren()
        {
            var kids = ImmutableArray.Create<Element>(new Group());
            Assert.Throws<NotSupportedException>(() => _ = new Elements.Rectangle { Children = kids });
            Assert.Throws<NotSupportedException>(() => _ = new Video { Children = kids });
            Assert.That(new Text { Children = ImmutableArray<Element>.Empty }.Children, Is.Empty);
        }
    }
}
