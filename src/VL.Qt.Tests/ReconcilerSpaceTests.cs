using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Paths;
using P = VL.Qt.Protocol.Prop;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Reconciler and Space (#143): a change of the scale rewrites every length, a change of the Space alone writes nothing.</summary>
    public class ReconcilerSpaceTests
    {
        RecordingSink _sink = null!;
        Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
        }

        void Frame(Element? root, SpaceMapping mapping)
        {
            _sink.Clear();
            _rec.Update(root, mapping);
        }

        static SpaceMapping Normalized(float height) => new(Space.Normalized, new Vector2(800, height), 1f);

        // Ids: Column 1, Rectangle 2, Text 3. Every length of the tree is non-zero.
        static readonly Element Tree = new Column
        {
            Spacing = 0.1f, Padding = 0.05f,
            Children =
            [
                new Rectangle
                {
                    Size = new Vector2(1, 0.5f), Radius = 0.1f, StrokeWidth = 0.02f, Opacity = 0.5f, Fill = new Color4(1, 0, 0, 1),
                    Transform = Matrix.Translation(0.2f, 0.3f, 0f),
                },
                new Text { Content = "Hi", FontSize = 0.2f },
            ],
        };

        [Test]
        public void Shape_Coordinates_FollowTheMapping()
        {
            var shape = new Shape
            {
                Points = Spread.Create(new Vector2(0, 0), new Vector2(1, 0.5f)), StrokeWidth = 0.02f,
                Dash = Spread.Create(0.1f, 0.05f), DashOffset = 0.02f,
            };
            Frame(shape, Normalized(600));   // 300 per unit
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapePoints f32array(0,0,300,150)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapeStrokeWidth f32(6)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapeDash f32array(5,2.5)"));

            // Scale change: points and stroke width again, the dash (a ratio) not.
            Frame(shape, Normalized(300));   // 150 per unit
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapePoints f32array(0,0,150,75)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapeStrokeWidth f32(3)"));
            Assert.That(_sink.Commands.Any(c => c.Contains($" {nameof(P.ShapeDash)} ")), Is.False);
            Assert.That(_sink.Commands.Any(c => c.Contains($" {nameof(P.ShapeDashOffset)} ")), Is.False);
        }

        [Test]
        public void Shape_Path_FollowsTheMappingOnRescale()
        {
            var shape = new Shape { Path = QtPath.Empty.MoveTo(0, 0).LineTo(1, 1) };
            Frame(shape, Normalized(600));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapePath path(0,1;0,0,300,300)"));
            Frame(shape, Normalized(300));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 ShapePath path(0,1;0,0,150,150)"));
        }

        [Test]
        public void Normalized_LengthsFollowTheSceneHeight()
        {
            Frame(Tree, Normalized(600));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Spacing f32(30)"));   // 0.1 * 300
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Padding f32(15)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 Size vec2(300,150)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 Radius f32(30)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 3 FontSize f32(60)"));
        }

        [Test]
        public void ScaleChange_Normalized_Resize_RewritesEveryLength_Only()
        {
            Frame(Tree, Normalized(600));

            // Same tree object, the window got half as high: every length again, nothing structural, no other property.
            Frame(Tree, Normalized(300));

            Assert.That(_sink.OfOp("SET").Count(), Is.EqualTo(_sink.Count), "only SET commands");
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Spacing f32(15)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Padding f32(7.5)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 Size vec2(150,75)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 Radius f32(15)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 BorderWidth f32(3)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 3 FontSize f32(30)"));
            Assert.That(_sink.Commands.Any(c => c.StartsWith($"SET 2 Transform mat4(") && c.Contains(",30,45,0,")), "transform translation (0.2, 0.3) * 150: " + string.Join("\n", _sink.Commands));
            Assert.That(_sink.Commands.Any(c => c.Split(' ')[2] is var prop && (prop == nameof(P.Opacity) || prop == nameof(P.Color))), Is.False,
                "opacity and color unchanged: " + string.Join("\n", _sink.Commands));
        }

        [Test]
        public void ScaleChange_ThenSameScale_WritesNothing()
        {
            Frame(Tree, Normalized(600));
            Frame(Tree, Normalized(300));
            Frame(Tree, Normalized(300));
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void ScaleChange_PixelTopLeft_DprChange_RewritesEveryLength()
        {
            Frame(Tree, new SpaceMapping(Space.PixelTopLeft, new Vector2(800, 600), 1f));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 Size vec2(100,50)"), "100 device pixels per unit at dpr 1");

            // The window moves to a screen with 200 %: 1 unit = 100 device pixels = 50 DIP.
            Frame(Tree, new SpaceMapping(Space.PixelTopLeft, new Vector2(800, 600), 2f));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 Size vec2(50,25)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 2 Radius f32(5)"));
            Assert.That(_sink.Commands, Does.Contain($"SET 3 FontSize f32(10)"));
            Assert.That(_sink.OfOp("SET").Count(), Is.EqualTo(_sink.Count));
        }

        [Test]
        public void ScaleChange_ChangedProperty_IsWrittenOnce()
        {
            Frame(Tree, Normalized(600));
            Frame(new Column { Spacing = 0.2f, Padding = 0.05f, Children = ((Column)Tree).Children }, Normalized(300));
            Assert.That(_sink.OfOp("SET").Count(c => c.StartsWith($"SET 1 Spacing ")), Is.EqualTo(1));
            Assert.That(_sink.Commands, Does.Contain($"SET 1 Spacing f32(30)"), "0.2 * 150");
        }

        [TestCase(Space.DIPTopLeft, Space.DIP)]
        [TestCase(Space.DIP, Space.DIPTopLeft)]
        public void SpaceChange_WithTheSameScale_RewritesNothing(Space from, Space to)
        {
            // Only the origin of the root moves (SET_SPACE in QtNative, written by SceneCore): no property of the tree.
            Frame(Tree, new SpaceMapping(from, new Vector2(800, 600), 1f));
            Frame(Tree, new SpaceMapping(to, new Vector2(800, 600), 1f));
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void SpaceChange_NormalizedToDip_WithEqualScale_RewritesNothing()
        {
            // Normalized in a 200 DIP high scene is 100 DIP per unit, like DIP.
            Frame(Tree, Normalized(200));
            Frame(Tree, new SpaceMapping(Space.DIP, new Vector2(800, 200), 1f));
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void SceneSizeChange_InDip_RewritesNothing()
        {
            // The scale of DIP does not depend on the scene: a resize or a dpr change leaves the lengths alone.
            Frame(Tree, new SpaceMapping(Space.DIP, new Vector2(800, 600), 1f));
            Frame(Tree, new SpaceMapping(Space.DIP, new Vector2(400, 300), 1.5f));
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void ScaleChange_UpdatesMappingOfTheReconciler()
        {
            Frame(Tree, Normalized(600));
            Assert.That(_rec.Mapping.UnitScale, Is.EqualTo(300f));
            Frame(Tree, Normalized(300));
            Assert.That(_rec.Mapping.UnitScale, Is.EqualTo(150f));
        }
    }
}
