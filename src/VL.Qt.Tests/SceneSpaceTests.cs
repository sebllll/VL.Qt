using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Wire;
using Rectangle = VL.Qt.Elements.Rectangle;
using VL.Qt.Interop;

namespace VL.Qt.Tests
{
    /// <summary>SET_SPACE of Scene.Update: written when the Space changes; a Space change alone rewrites no lengths.</summary>
    public class SceneSpaceTests
    {
        private static readonly Color4 c = new Color4(0.1f, 0.2f, 0.3f, 1f);
        private static readonly int setSpaceBytes = Protocol.HeaderSize + 4;

        // Same tree object every frame, like a patch at rest.
        private static readonly Element tree = new Group
        {
            Children = [new Rectangle { Size = new Vector2(1, 2), Radius = 0.1f, Fill = new Color4(1, 0, 0, 1) }],
        };

        // Written commands of the next Update, without a native handle (Apply does not run; the scene is 0 x 0 DIP, dpr 1).
        private static int Written(Scene core, Space space)
        {
            core.Commands.Reset();
            core.Update(tree, space, c, true);
            return core.Commands.Length;
        }

        [Test]
        public void SpaceChange_WritesSetSpaceOnce()
        {
            var core = new Scene(new SceneRegistry(), SceneHandle.Null);
            Written(core, Space.DIPTopLeft);
            Assert.That(Written(core, Space.DIPTopLeft), Is.EqualTo(0), "unchanged");
            Assert.That(Written(core, Space.DIP), Is.EqualTo(setSpaceBytes), "changed: only SET_SPACE");
            Assert.That(Written(core, Space.DIP), Is.EqualTo(0), "written once");
            Assert.That(Written(core, Space.DIPTopLeft), Is.EqualTo(setSpaceBytes), "and back");
        }

        [TestCase(Space.DIPTopLeft, Space.DIP)]
        [TestCase(Space.DIP, Space.PixelTopLeft)] // dpr 1 without a handle: same scale
        [TestCase(Space.PixelTopLeft, Space.DIPTopLeft)]
        public void SpaceChange_WithTheSameScale_RewritesNoProperties(Space from, Space to)
        {
            var core = new Scene(new SceneRegistry(), SceneHandle.Null);
            Written(core, from);
            Assert.That(Written(core, to), Is.EqualTo(setSpaceBytes));
        }
    }
}
