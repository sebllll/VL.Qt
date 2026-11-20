using NUnit.Framework;
using Stride.Core.Mathematics;

namespace VL.Qt.Tests
{
    /// <summary>Clear color and Clear flag of SceneCore.Update go to QtNative only when they change (#117).</summary>
    public class SceneCoreClearTests
    {
        private static readonly Color4 c = new Color4(0.1f, 0.2f, 0.3f, 1f);

        // Written commands of the next Update, without a native handle (Apply does not run).
        private static int Written(SceneCore core, bool clear, Color4 color)
        {
            core.Commands.Reset();
            core.Update(null, Space.DIPTopLeft, color, clear);
            return core.Commands.Length;
        }

        private static readonly int clearColorBytes = Protocol.HeaderSize + 16;
        private static readonly int setClearBytes = Protocol.HeaderSize + 4;
        private static readonly int setSpaceBytes = Protocol.HeaderSize + 4;

        [Test]
        public void FirstUpdate_SendsClearColorClearAndSpace()
        {
            var core = new SceneCore(new SceneRegistry());
            Assert.That(Written(core, true, c), Is.EqualTo(clearColorBytes + setClearBytes + setSpaceBytes));
        }

        [Test]
        public void UnchangedValues_SendNothing()
        {
            var core = new SceneCore(new SceneRegistry());
            Written(core, false, c);
            Assert.That(Written(core, false, c), Is.EqualTo(0));
        }

        [Test]
        public void ClearChange_SendsOnlySetClear()
        {
            var core = new SceneCore(new SceneRegistry());
            Written(core, true, c);
            Assert.That(Written(core, false, c), Is.EqualTo(setClearBytes), "off");
            Assert.That(Written(core, true, c), Is.EqualTo(setClearBytes), "on again");
        }

        [Test]
        public void ClearColorChange_DoesNotSendSetClear()
        {
            var core = new SceneCore(new SceneRegistry());
            Written(core, false, c);
            Assert.That(Written(core, false, new Color4(1, 0, 0, 1)), Is.EqualTo(clearColorBytes));
        }
    }
}
