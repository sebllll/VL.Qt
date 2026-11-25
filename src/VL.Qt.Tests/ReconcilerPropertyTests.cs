using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Paths;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using P = VL.Qt.Wire.Protocol.PropertyId;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Property diff of the reconciler (04c): only changed values are sent, converted via Geometry.</summary>
    public class ReconcilerPropertyTests
    {
        private RecordingSink _sink = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        }

        private void Frame(Element? root)
        {
            _sink.Clear();
            _rec.Update(root, Space.DIPTopLeft);
        }

        private static Group G(params Element[] children) => new() { Children = ImmutableArray.Create(children) };
        private static Column C(params Element[] children) => new() { Children = ImmutableArray.Create(children) };

        /// <summary>A tree using every type with non-default values everywhere. Ids: Group 1, Rectangle 2,
        /// Column 3, Text 4, Image 5, Row 6, Shader 7, its input 8.</summary>
        private static Element BuildScene(float fill = 1, string text = "Hello", float spacing = 0.1f, bool blending = false,
            float width = 1, float opacity = 0.5f, float rotation = 0.3f) =>
            G(
                new Rectangle
                {
                    Size = new Vector2(width, 2), Fill = new Color4(fill, 0, 0, 1), Radius = 0.1f,
                    StrokeColor = new Color4(0, 0, 1, 1), StrokeWidth = 0.02f, Opacity = opacity, Clip = true,
                    Transform = Matrix.RotationZ(rotation), Anchor = RectangleAnchor.TopLeft,
                },
                new Column
                {
                    Spacing = spacing, Padding = 0.05f, ChildAlignment = HorizontalAlignment.Center,
                    Children = ImmutableArray.Create<Element>(
                        new Text
                        {
                            Content = text, FontFamily = "Arial", FontSize = 0.2f, Weight = 700, Italic = true,
                            Color = new Color4(0, 1, 0, 1), HAlign = TextHorizontalAlignment.Center,
                            VAlign = VerticalAlignment.Center, Wrap = TextWrap.Wrap, Elide = TextElide.Right,
                            LineHeight = 1.5f, LetterSpacing = 0.01f, Autofit = TextAutofit.Fit, Size = new Vector2(1, 0.5f),
                        },
                        new Image { Source = new QtImage(new ImageResource(0x1_0000_0005UL)), FillMode = FillMode.Crop, Smooth = false }),
                },
                new Row { Spacing = 0.2f, Padding = 0.1f, ChildAlignment = VerticalAlignment.Center },
                new Shader
                {
                    Input = new Rectangle { Size = new Vector2(1, 1) },
                    Blending = blending, MeshResolution = new Int2(2, 3),
                });
        [Test]
        public void FirstFrame_SendsOnlyNonDefaultValues_Converted()
        {
            Frame(new Rectangle { Size = new Vector2(1, 2), Fill = new Color4(1, 0, 0, 1), StrokeWidth = 0.02f });
            Assert.That(_sink.Commands, Is.EqualTo(new[]
            {
                "CREATE 1 Rectangle",
                $"SET 1 Size vec2(100,200)",
                $"SET 1 Color color(1,0,0,1)",
                $"SET 1 BorderWidth f32(2)",          // 0.02 * 100
                "REPARENT 1 0 0",
            }));
        }

        [Test]
        public void DefaultElements_SendNoSets_ExceptTextFontSize()
        {
            Frame(G(new Rectangle(), new Image(), new Row(), new Column(), new Shader(), new Text()));
            Assert.That(_sink.OfOp("SET"), Is.EqualTo(new[] { $"SET 7 FontSize f32(12)" }));
        }

        [Test]
        public void Debug_SentOnlyOnChange_PerElementNotRecursive()
        {
            Frame(G(new Rectangle()));
            Frame(G(new Rectangle { Debug = true }));
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 2 Debug bool(1)" }), "child only, not the Group");
            Frame(G(new Rectangle { Debug = true }));
            Assert.That(_sink.Count, Is.EqualTo(0), "unchanged: nothing sent");
            Frame(G(new Rectangle()));
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 2 Debug bool(0)" }));
        }

        [Test]
        public void Debug_OnCreatedElement_Sent()
        {
            Frame(new Rectangle { Debug = true });
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Debug bool(1)"));
        }

        [Test]
        public void Visible_SentOnlyOnChange_PerElementNotRecursive()
        {
            Frame(G(new Rectangle()));
            Frame(G(new Rectangle { Visible = false }));
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 2 Visible bool(0)" }), "child only, not the Group");
            Frame(G(new Rectangle { Visible = false }));
            Assert.That(_sink.Count, Is.EqualTo(0), "unchanged: nothing sent");
            Frame(G(new Rectangle()));
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 2 Visible bool(1)" }));
        }

        [Test]
        public void Visible_OnCreatedElement_SentOnlyWhenOff()
        {
            Frame(G(new Rectangle(), new Rectangle { Visible = false }));
            Assert.That(_sink.Commands.Where(c => c.Contains("Visible")), Is.EqualTo(new[] { $"SET 3 Visible bool(0)" }),
                "default true: nothing sent for the first child");
            var set = _sink.Commands.ToList().IndexOf("SET 3 Visible bool(0)");
            var reparent = _sink.Commands.ToList().FindIndex(c => c.StartsWith("REPARENT 3 "));
            Assert.That(set, Is.LessThan(reparent), "sent before REPARENT, like every property of a new element");
        }

        [Test]
        public void RebuiltWithSameValues_ZeroSets()
        {
            Frame(BuildScene());
            Assert.That(_sink.OfOp("SET").Count(), Is.GreaterThan(30));
            Frame(BuildScene());
            Assert.That(_sink.Count, Is.EqualTo(0), _sink.ToString());
        }

        private static IEnumerable<TestCaseData> SingleChanges()
        {
            yield return new TestCaseData(new Func<IElement>(() => BuildScene(fill: 0.5f)), $"SET 2 Color color(0.5,0,0,1)").SetName("Rectangle.Fill");
            yield return new TestCaseData(new Func<IElement>(() => BuildScene(opacity: 0.25f)), $"SET 2 Opacity f32(0.25)").SetName("Opacity");
            yield return new TestCaseData(new Func<IElement>(() => BuildScene(width: 2)), $"SET 2 Size vec2(200,200)").SetName("Size");
            yield return new TestCaseData(new Func<IElement>(() => BuildScene(text: "World")), $"SET 4 Text string(World)").SetName("Text.Content");
            yield return new TestCaseData(new Func<IElement>(() => BuildScene(spacing: 0.25f)), $"SET 3 Spacing f32(25)").SetName("Column.Spacing");
            yield return new TestCaseData(new Func<IElement>(() => BuildScene(blending: true)), $"SET 7 Blending bool(1)").SetName("Shader.Blending");
        }

        [TestCaseSource(nameof(SingleChanges))]
        public void OnePropertyChanged_ExactlyOneSet(Func<IElement> changed, string expected)
        {
            Frame(BuildScene());
            Frame((Element)changed());
            Assert.That(_sink.Commands, Is.EqualTo(new[] { expected }));
        }

        [Test]
        public void TransformChanged_OneSet_UnitsConverted_AnchorNotBakedIn()
        {
            Frame(BuildScene());
            Frame(BuildScene(rotation: 0.6f));
            var expected = new RecordingSink();
            expected.SetMat4(2, P.Transform, SpaceMapping.Default.ToQt(Matrix.RotationZ(0.6f)));
            Assert.That(_sink.Commands, Is.EqualTo(expected.Commands));
        }

        [Test]
        public void Anchor_SentSeparately_SizeChangeWithRotation_NoMatrixResend()
        {
            var b = new Vector2(1, 1);
            Frame(new Rectangle { Size = b, Anchor = RectangleAnchor.Center });
            Frame(new Rectangle { Size = b, Anchor = RectangleAnchor.TopLeft });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 Anchor i32(33)" }), "TopLeft = AlignLeft|AlignTop");

            var rot = Matrix.RotationZ(MathUtil.PiOverTwo);
            Frame(new Rectangle { Size = b, Transform = rot, Anchor = RectangleAnchor.TopLeft });
            Assert.That(_sink.Commands.Single(), Does.StartWith($"SET 1 Transform mat4("));

            Frame(new Rectangle { Size = new Vector2(2, 1), Transform = rot, Anchor = RectangleAnchor.TopLeft });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 Size vec2(200,100)" }),
                "QtNative resolves the anchor against the new size: no matrix resend");

            Frame(new Rectangle { Size = new Vector2(2, 1), Transform = rot, Anchor = RectangleAnchor.BottomRight });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 Anchor i32(66)" }), "BottomRight = AlignRight|AlignBottom");
        }

        [Test]
        public void Size_SentAsVec2OnChangeOnly_ConvertedToDip()
        {
            Frame(new Rectangle { Size = new Vector2(1, 0.5f) });
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Size vec2(100,50)"));

            Frame(new Rectangle { Size = new Vector2(1, 0.5f) });
            Assert.That(_sink.Count, Is.EqualTo(0), "same size: nothing sent");

            Frame(new Rectangle { Size = new Vector2(2, 0.5f) });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 Size vec2(200,50)" }));

            Frame(new Rectangle { Size = new Vector2(2, 0.25f) });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 Size vec2(200,25)" }));
        }

        [Test]
        public void Anchor_SentOnChangeOnly_NotOnEquality()
        {
            Frame(new Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.Center });
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Anchor i32(132)"), "Center = 0x84 (not the default)");

            Frame(new Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.Center });
            Assert.That(_sink.Count, Is.EqualTo(0), "same anchor: nothing sent");

            Frame(new Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.TopLeft });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 Anchor i32(33)" }), "back to TopLeft = 0x21");
        }

        [Test]
        public void SizeChange_NoAnchorNoTransformResend()
        {
            var rot = Matrix.RotationZ(0.5f);
            Frame(new Rectangle { Size = new Vector2(1, 1), Transform = rot, Anchor = RectangleAnchor.BottomRight });
            Frame(new Rectangle { Size = new Vector2(3, 1), Transform = rot, Anchor = RectangleAnchor.BottomRight });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 Size vec2(300,100)" }));
        }

        [Test]
        public void Created_DefaultAnchorTopLeft_NotSent_GroupAutoSize_SendsPlainMatrix()
        {
            var rot = Matrix.RotationZ(MathUtil.PiOverTwo);
            Frame(new Group { Size = new Vector2(0, 0), Transform = rot });
            Assert.That(_sink.Commands.Any(c => c.StartsWith($"SET 1 Anchor ")), Is.False, "TopLeft is QtNative default");
            var expected = new RecordingSink();
            expected.SetMat4(1, P.Transform, SpaceMapping.Default.ToQt(rot));
            Assert.That(_sink.Commands, Does.Contain(expected.Commands.Single()));
        }

        [TestCase(RectangleAnchor.TopLeft, 0x21)]
        [TestCase(RectangleAnchor.TopCenter, 0x24)]
        [TestCase(RectangleAnchor.TopRight, 0x22)]
        [TestCase(RectangleAnchor.MiddleLeft, 0x81)]
        [TestCase(RectangleAnchor.Center, 0x84)]
        [TestCase(RectangleAnchor.MiddleRight, 0x82)]
        [TestCase(RectangleAnchor.BottomLeft, 0x41)]
        [TestCase(RectangleAnchor.BottomCenter, 0x44)]
        [TestCase(RectangleAnchor.BottomRight, 0x42)]
        public void ToQtAlignment_AllAnchors(RectangleAnchor anchor, int expected)
            => Assert.That(Geometry.ToQtAlignment(anchor), Is.EqualTo(expected));
        [Test]
        public void LayoutChild_OnlySizeSent()
        {
            Frame(C(new Rectangle { Size = new Vector2(0.5f, 0.25f) }));
            Assert.That(_sink.OfOp("SET"), Is.EqualTo(new[] { $"SET 2 Size vec2(50,25)" }));

            Frame(C(new Rectangle { Size = new Vector2(0.5f, 0.25f) }));
            Assert.That(_sink.Count, Is.EqualTo(0), _sink.ToString());

            Frame(new Row { Children = ImmutableArray.Create<Element>(new Rectangle { Size = new Vector2(1, 0.25f) }) });
            Assert.That(_sink.OfOp("SET"), Is.EqualTo(new[] { $"SET 2 Size vec2(100,25)" }), "Column → Row recreates");

            Frame(new Row { Children = ImmutableArray.Create<Element>(new Rectangle { Size = new Vector2(1, 0.5f) }) });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 2 Size vec2(100,50)" }));
        }

        // ---- Shape: Frame converts with 100 per unit ----

        private static Spread<Vector2> Pts(params Vector2[] points) => Spread.Create(points);
        private static readonly Vector2[] s_triangle = { new(0, 0), new(1, 0), new(0, 1) };

        [Test]
        public void Shape_FirstFrame_SendsOnlyNonDefaultValues_Converted()
        {
            Frame(new Shape { Points = Pts(s_triangle), Closed = true, StrokeWidth = 0.02f, Dash = Spread.Create(0.04f, 0.02f), DashOffset = 0.01f, Cap = ShapeCap.Round });
            Assert.That(_sink.Commands, Is.EqualTo(new[]
            {
                "CREATE 1 Shape",
                $"SET 1 ShapePoints f32array(0,0,100,0,0,100)",
                $"SET 1 ShapeClosed bool(1)",
                $"SET 1 ShapeStrokeWidth f32(2)",
                $"SET 1 ShapeCap i32(2)",
                $"SET 1 ShapeDash f32array(2,1)",         // 0.04 / 0.02, 0.02 / 0.02
                $"SET 1 ShapeDashOffset f32(0.5)",       // 0.01 / 0.02
                "REPARENT 1 0 0",
            }));
        }

        [Test]
        public void Shape_Default_SendsNothing()
        {
            Frame(new Shape());
            Assert.That(_sink.OfOp("SET"), Is.Empty);
        }

        [Test]
        public void Shape_OtherProperties_SentOnlyOnChange()
        {
            var points = Pts(s_triangle);
            Frame(new Shape { Points = points });
            Frame(new Shape
            {
                Points = points, Fill = new Color4(1, 0, 0, 1), StrokeColor = new Color4(0, 1, 0, 1), Join = ShapeJoin.Round, MiterLimit = 2,
                TrimStart = 0.1f, TrimEnd = 0.9f, TrimOffset = 0.5f, FillRule = ShapeFillRule.EvenOdd,
            });
            Assert.That(_sink.Commands, Is.EqualTo(new[]
            {
                $"SET 1 ShapeFill color(1,0,0,1)",
                $"SET 1 ShapeStrokeColor color(0,1,0,1)",
                $"SET 1 ShapeJoin i32(2)",
                $"SET 1 ShapeMiterLimit f32(2)",
                $"SET 1 ShapeTrimStart f32(0.1)",
                $"SET 1 ShapeTrimEnd f32(0.9)",
                $"SET 1 ShapeTrimOffset f32(0.5)",
                $"SET 1 ShapeFillRule i32(1)",
            }));
        }

        [Test]
        public void Shape_NewReferenceWithTheSameContent_SendsNothing()
        {
            Frame(new Shape { Points = Pts(s_triangle), Dash = Spread.Create(0.1f, 0.1f), StrokeWidth = 0.02f });
            Frame(new Shape { Points = Pts(s_triangle), Dash = Spread.Create(0.1f, 0.1f), StrokeWidth = 0.02f });
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void Shape_ChangedPoints_AreSentAgain()
        {
            Frame(new Shape { Points = Pts(s_triangle) });
            Frame(new Shape { Points = Pts(new Vector2(0, 0), new Vector2(2, 0)) });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 ShapePoints f32array(0,0,200,0)" }));
        }

        [Test]
        public void Shape_PathInsteadOfPoints_SendsThePath()
        {
            var path = QtPath.Empty.MoveTo(0, 0).LineTo(1, 0).QuadTo(1, 1, 0, 1).Close();
            Frame(new Shape { Points = Pts(s_triangle), Path = path });
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapePath path(0,1,2,4;0,0,100,0,100,100,0,100)"));
            Assert.That(_sink.Commands.Any(c => c.Contains($" {nameof(P.ShapePoints)} ")), Is.False);

            // Same content in a new instance: nothing; another path: sent again.
            Frame(new Shape { Points = Pts(s_triangle), Path = QtPath.Empty.MoveTo(0, 0).LineTo(1, 0).QuadTo(1, 1, 0, 1).Close() });
            Assert.That(_sink.Count, Is.EqualTo(0));
            Frame(new Shape { Path = QtPath.Empty.MoveTo(0, 0).LineTo(2, 0) });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 ShapePath path(0,1;0,0,200,0)" }));
        }

        [Test]
        public void Shape_BackFromPathToPoints_SendsThePoints()
        {
            var points = Pts(s_triangle);
            Frame(new Shape { Path = QtPath.Empty.MoveTo(0, 0).LineTo(1, 0) });
            Frame(new Shape { Points = points });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 ShapePoints f32array(0,0,100,0,0,100)" }));
        }

        [Test]
        public void Shape_StrokeWidthZero_IsSentAsMinusOne()
        {
            Frame(new Shape { StrokeWidth = 0.02f });
            Frame(new Shape());
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 ShapeStrokeWidth f32(-1)", $"SET 1 ShapeDashOffset f32(0)" }));
        }

        [Test]
        public void Shape_StrokeWidthChange_SendsTheDashAgain()
        {
            var dash = Spread.Create(0.1f, 0.05f);
            Frame(new Shape { Dash = dash, DashOffset = 0.1f, StrokeWidth = 0.05f });
            Frame(new Shape { Dash = dash, DashOffset = 0.1f, StrokeWidth = 0.1f });
            Assert.That(_sink.Commands, Is.EqualTo(new[]
            {
                $"SET 1 ShapeStrokeWidth f32(10)",
                $"SET 1 ShapeDash f32array(1,0.5)",
                $"SET 1 ShapeDashOffset f32(1)",
            }));
        }

        [Test]
        public void Shape_DashChangeWithoutStrokeWidthChange_SendsOnlyTheDash()
        {
            Frame(new Shape { Dash = Spread.Create(0.1f, 0.05f), StrokeWidth = 0.05f });
            Frame(new Shape { Dash = Spread.Create(0.2f, 0.05f), StrokeWidth = 0.05f });
            Assert.That(_sink.Commands, Is.EqualTo(new[] { $"SET 1 ShapeDash f32array(4,1)" }));
        }

        [Test]
        public void Shape_ManyPoints_AreSentWhole()
        {
            Frame(new Shape { Points = Spread.Create(new Vector2[50_000]) });
            var set = _sink.Commands.Single(c => c.Contains($" {nameof(P.ShapePoints)} "));
            Assert.That(set.Split(',').Length, Is.EqualTo(100_000));
        }

        [Test]
        public void Shape_LongPath_IsSentWhole()
        {
            var path = QtPath.Empty.MoveTo(0, 0);
            for (int i = 0; i < 50_000; i++)
                path = path.LineTo(i, 1);
            Frame(new Shape { Path = path });
            var set = _sink.Commands.Single(c => c.Contains($" {nameof(P.ShapePath)} "));
            int verbs = set.Substring(set.IndexOf("path(") + 5, set.IndexOf(';') - set.IndexOf("path(") - 5).Split(',').Length;
            Assert.That(verbs, Is.EqualTo(50_001));
        }

        [Test]
        public void Shape_Steady_AllocatesNothing()
        {
            var shape = new Shape { Points = Pts(s_triangle), Dash = Spread.Create(0.1f, 0.1f), StrokeWidth = 0.02f };
            var pathShape = new Shape { Path = QtPath.Empty.MoveTo(0, 0).LineTo(1, 1) };
            var tree = G(shape, pathShape);
            Frame(tree);
            for (int i = 0; i < 10; i++)
                Frame(tree);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                _rec.Update(tree, Space.DIPTopLeft);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
        }

        private sealed class CountingSink : NullCommandSink
        {
            public int Sets;
            public override void SetF32(uint id, ushort prop, float value) => Sets++;
            public override void SetI32(uint id, ushort prop, int value) => Sets++;
            public override void SetBool(uint id, ushort prop, bool value) => Sets++;
            public override void SetColor(uint id, ushort prop, float r, float g, float b, float a) => Sets++;
            public override void SetVec2(uint id, ushort prop, float x, float y) => Sets++;
            public override void SetRect(uint id, ushort prop, float x, float y, float width, float height) => Sets++;
            public override void SetMat4(uint id, ushort prop, in Matrix m) => Sets++;
            public override void SetString(uint id, ushort prop, ReadOnlySpan<char> value) => Sets++;
            public override void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values) => Sets++;
            public override void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points) => Sets++;
            public override void SetRef(uint id, ushort prop, uint refId) => Sets++;
            public override void SetU64(uint id, ushort prop, ulong value) => Sets++;
            public override void SetIvec2(uint id, ushort prop, int x, int y) => Sets++;
            public override void BeginUniforms(uint id, ushort flags) => Sets++;
        }
    }
}
