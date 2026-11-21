using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Nodes;
using Image = VL.Qt.Elements.Image;

namespace VL.Qt.Tests
{
    /// <summary>Text and Image nodes (07c): defaults, change detection, outputs. The shared core is covered by RectangleNodeTests.</summary>
    public class TextImageNodeTests
    {
        private static readonly Vector2 DefaultSize = new(1, 1);
        private static readonly IDisposable NoWarning = new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }

        private TextNode _text = null!;
        private ImageNode _image = null!;

        [SetUp]
        public void SetUp()
        {
            _text = new TextNode(_ => NoWarning);
            _image = new ImageNode(_ => NoWarning);
        }

        [TearDown]
        public void TearDown() { _text.Dispose(); _image.Dispose(); }

        // Pin defaults as declared on Update.
        private Element UpdateText(string text = "Text", string family = "", float fontSize = 0.12f, FontWeight weight = FontWeight.Normal,
            bool italic = false, Color4? color = null, TextHorizontalAlignment h = TextHorizontalAlignment.Left,
            VerticalAlignment v = VerticalAlignment.Top, TextWrap wrap = TextWrap.NoWrap, TextElide elide = TextElide.None,
            float lineHeight = 1, float letterSpacing = 0, TextAutofit autofit = TextAutofit.Fixed, Vector2? size = null,
            Interaction? interaction = null)
            => _text.Update(size ?? DefaultSize, default, RectangleAnchor.Center, text, family, fontSize, weight, italic,
                color ?? Color4.White, h, v, wrap, elide, lineHeight, letterSpacing, autofit, 1, false, false,
                interaction, null, out _, out _);

        private Element UpdateImage(QtImage? image = null, ImageFillMode fill = ImageFillMode.Stretch, bool smooth = true)
            => _image.Update(DefaultSize, default, RectangleAnchor.Center, image, fill, smooth, 1, false, false, null, null, out _, out _);

        private static QtImage NewImage(ulong id) => new(new ImageResource(id));

        [Test]
        public void Text_Defaults_WithFeedback()
        {
            var t = (Text)UpdateText();
            Assert.That(t.Size, Is.EqualTo(DefaultSize));
            Assert.That(t.Content, Is.EqualTo("Text"));
            Assert.That(t.FontFamily, Is.EqualTo(""));
            Assert.That(t.FontSize, Is.EqualTo(Text.DefaultFontSize));
            Assert.That(t.Color, Is.EqualTo(Color4.White));
            Assert.That(t.Transform, Is.EqualTo(Matrix.Identity));
            Assert.That(t.Feedback.Feedback, Is.SameAs(_text.Core.Feedback));
        }

        [Test]
        public void Text_NullStrings_BecomeEmpty()
        {
            var e = (Text)_text.Update(DefaultSize, default, RectangleAnchor.Center, null!, null!, 0.12f, FontWeight.Normal, false,
                Color4.White, TextHorizontalAlignment.Left, VerticalAlignment.Top, TextWrap.NoWrap, TextElide.None, 1, 0,
                TextAutofit.Fixed, 1, false, false, null, null, out _, out _);
            Assert.That(e.Content, Is.EqualTo(""));
            Assert.That(e.FontFamily, Is.EqualTo(""));
        }

        [Test]
        public void Text_UnchangedInputs_SameInstance_NoAllocation()
        {
            var a = UpdateText("Hello", "Arial");
            Assert.That(UpdateText("Hello", "Arial"), Is.SameAs(a));
            Assert.That(_text.Core.Created, Is.EqualTo(1));
            // equal but not reference-identical strings are unchanged too
            Assert.That(UpdateText(new string("Hello".AsSpan()), new string("Arial".AsSpan())), Is.SameAs(a));

            for (int i = 0; i < 100; i++) UpdateText("Hello", "Arial");
            Assert.That(Allocations.Fewest(() => UpdateText("Hello", "Arial")), Is.EqualTo(0));
        }

        [Test]
        public void Text_EachChangedInput_NewDescription()
        {
            var last = UpdateText();
            void Changed(Element e, Func<Text, bool> check, string what)
            {
                Assert.That(e, Is.Not.SameAs(last), what);
                Assert.That(check((Text)e), Is.True, what);
                last = e;
            }

            Changed(UpdateText(text: "Other"), t => t.Content == "Other", "text");
            Changed(UpdateText(family: "Arial"), t => t.FontFamily == "Arial", "family");
            Changed(UpdateText(fontSize: 0.2f), t => t.FontSize == 0.2f, "fontSize");
            Changed(UpdateText(weight: FontWeight.Bold), t => t.Weight == FontWeight.Bold, "weight");
            Changed(UpdateText(italic: true), t => t.Italic, "italic");
            Changed(UpdateText(color: Color4.Black), t => t.Color == Color4.Black, "color");
            Changed(UpdateText(h: TextHorizontalAlignment.Center), t => t.HAlign == TextHorizontalAlignment.Center, "h");
            Changed(UpdateText(v: VerticalAlignment.Bottom), t => t.VAlign == VerticalAlignment.Bottom, "v");
            Changed(UpdateText(wrap: TextWrap.WordWrap), t => t.Wrap == TextWrap.WordWrap, "wrap");
            Changed(UpdateText(elide: TextElide.Right), t => t.Elide == TextElide.Right, "elide");
            Changed(UpdateText(lineHeight: 1.5f), t => t.LineHeight == 1.5f, "lineHeight");
            Changed(UpdateText(letterSpacing: 0.01f), t => t.LetterSpacing == 0.01f, "letterSpacing");
            Changed(UpdateText(autofit: TextAutofit.Fit), t => t.Autofit == TextAutofit.Fit, "autofit");
            Changed(UpdateText(size: new Vector2(3, 4)), t => t.Size == new Vector2(3, 4), "size");
            var chain = new HoverNode(_ => NoWarning).Update(null, null);
            Changed(UpdateText(interaction: chain), t => ReferenceEquals(t.Interaction, chain), "interaction");
        }

        [Test]
        public void Text_MeasuredSize_FromFeedback()
        {
            UpdateText();
            _text.Core.Feedback[0].MeasuredSize = new Vector2(0.5f, 0.15f);
            _text.Update(DefaultSize, default, RectangleAnchor.Center, "Text", "", 0.12f, FontWeight.Normal, false, Color4.White,
                TextHorizontalAlignment.Left, VerticalAlignment.Top, TextWrap.NoWrap, TextElide.None, 1, 0, TextAutofit.Fixed, 1,
                false, false, null, null, out var measured, out _);
            Assert.That(measured, Is.EqualTo(new Vector2(0.5f, 0.15f)));
        }

        [Test]
        public void Image_Defaults_WithFeedback()
        {
            var i = (Image)UpdateImage();
            Assert.That(i.Size, Is.EqualTo(DefaultSize));
            Assert.That(i.Source, Is.Null);
            Assert.That(i.FillMode, Is.EqualTo(ImageFillMode.Stretch));
            Assert.That(i.Smooth, Is.True);
            Assert.That(i.Feedback.Feedback, Is.SameAs(_image.Core.Feedback));
        }

        [Test]
        public void Image_UnchangedInputs_SameInstance_NoAllocation_ImageByReference()
        {
            var image = NewImage(1);
            var a = UpdateImage(image);
            Assert.That(UpdateImage(image), Is.SameAs(a), "same QtImage = unchanged");
            Assert.That(_image.Core.Created, Is.EqualTo(1));
            Assert.That(UpdateImage(NewImage(1)), Is.Not.SameAs(a), "another QtImage (same resource) = new image");

            void Call() => _image.Update(DefaultSize, default, RectangleAnchor.Center, image, ImageFillMode.Stretch, true, 1,
                false, false, null, null, out _, out _);
            for (int k = 0; k < 100; k++) Call();
            Assert.That(Allocations.Fewest(Call), Is.EqualTo(0));
        }

        [Test]
        public void Image_EachChangedInput_NewDescription()
        {
            var last = UpdateImage();
            void Changed(Element e, Func<Image, bool> check, string what)
            {
                Assert.That(e, Is.Not.SameAs(last), what);
                Assert.That(check((Image)e), Is.True, what);
                last = e;
            }

            var x = NewImage(7);
            Changed(UpdateImage(x), i => i.Source == x, "image");
            Changed(UpdateImage(x, ImageFillMode.Crop), i => i.FillMode == ImageFillMode.Crop, "fillMode");
            Changed(UpdateImage(x, ImageFillMode.Crop, false), i => !i.Smooth, "smooth");
            Changed(UpdateImage(), i => i.Source is null, "image cleared");
        }
    }
}
