using NUnit.Framework;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Qt.Stride;

namespace VL.Qt.Tests.Stride
{
    /// <summary>TextureState of SharedTextureScene, compiled in from VL.Qt.Stride: what a change of the inputs requires.</summary>
    public class TextureStateTests
    {
        private static readonly Int2 s_size = new(512, 256);
        private static readonly object s_device = new();
        private const PixelFormat format = PixelFormat.R8G8B8A8_UNorm;

        private static TextureState Created()
        {
            var state = new TextureState();
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.None, false), Is.EqualTo(TextureState.Change.Recreate));
            return state;
        }

        [Test]
        public void FirstUpdateRecreatesAndRemembersTheInputs()
        {
            var state = new TextureState();
            Assert.That(state.Update(s_size, PixelFormat.B8G8R8A8_UNorm, s_device, MultisampleCount.X4, false), Is.EqualTo(TextureState.Change.Recreate));
            Assert.That(state.Size, Is.EqualTo(s_size));
            Assert.That(state.Format, Is.EqualTo(PixelFormat.B8G8R8A8_UNorm));
            Assert.That(state.Samples, Is.EqualTo(MultisampleCount.X4));
        }

        [Test]
        public void SameInputsWithTextureOnlyRebindIfUnbound()
        {
            var state = Created();
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.None, true), Is.EqualTo(TextureState.Change.RebindIfUnbound));
        }

        [Test]
        public void SameInputsWithoutTextureDoNothing()
        {
            // The creation failed (error text): the state stays and nothing is retried until an input changes.
            var state = Created();
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.None, false), Is.EqualTo(TextureState.Change.None));
        }

        [Test]
        public void NewSampleCountRebindsTheSameTexture()
        {
            var state = Created();
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.X8, true), Is.EqualTo(TextureState.Change.Rebind));
            Assert.That(state.Samples, Is.EqualTo(MultisampleCount.X8));
            // Remembered: the same count again does not rebind.
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.X8, true), Is.EqualTo(TextureState.Change.RebindIfUnbound));
        }

        [Test]
        public void NewSampleCountWithoutTextureIsOnlyRemembered()
        {
            var state = Created();
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.X2, false), Is.EqualTo(TextureState.Change.None));
            Assert.That(state.Samples, Is.EqualTo(MultisampleCount.X2));
        }

        [Test]
        public void ChangedSizeFormatOrDeviceRecreates()
        {
            var state = Created();
            var size = new Int2(512, 257);
            Assert.That(state.Update(size, format, s_device, MultisampleCount.None, true), Is.EqualTo(TextureState.Change.Recreate));
            Assert.That(state.Update(size, PixelFormat.R16G16B16A16_Float, s_device, MultisampleCount.None, true), Is.EqualTo(TextureState.Change.Recreate));
            Assert.That(state.Update(size, PixelFormat.R16G16B16A16_Float, new object(), MultisampleCount.None, true), Is.EqualTo(TextureState.Change.Recreate));
        }

        [Test]
        public void ChangeOfSizeAndSamplesTogetherRecreatesWithTheNewCount()
        {
            var state = Created();
            Assert.That(state.Update(new Int2(64, 64), format, s_device, MultisampleCount.X4, true), Is.EqualTo(TextureState.Change.Recreate));
            Assert.That(state.Samples, Is.EqualTo(MultisampleCount.X4));
        }

        [Test]
        public void DeviceGoneRecreatesEvenWithTheSameInputs()
        {
            var state = Created();
            state.DeviceGone();
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.None, false), Is.EqualTo(TextureState.Change.Recreate));
            // The device is remembered again: nothing more to do.
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.None, true), Is.EqualTo(TextureState.Change.RebindIfUnbound));
        }

        [Test]
        public void NoDeviceYetIsAStateOfItsOwn()
        {
            var state = new TextureState();
            Assert.That(state.Update(s_size, format, null, MultisampleCount.None, false), Is.EqualTo(TextureState.Change.Recreate));
            Assert.That(state.Update(s_size, format, null, MultisampleCount.None, false), Is.EqualTo(TextureState.Change.None));
            Assert.That(state.Update(s_size, format, s_device, MultisampleCount.None, false), Is.EqualTo(TextureState.Change.Recreate));
        }

        [TestCase(0, 0, 1000, 1, 1)]
        [TestCase(-5, 3, 1000, 1, 3)]
        [TestCase(2000, 2000, 1000, 1000, 1000)]
        [TestCase(1920, 1080, 16384, 1920, 1080)]
        [TestCase(5, 5, int.MaxValue, 5, 5)]
        public void SizeIsClampedToOneAndTheDeviceLimit(int x, int y, int max, int expectedX, int expectedY)
            => Assert.That(TextureState.ClampSize(new Int2(x, y), max), Is.EqualTo(new Int2(expectedX, expectedY)));

        [TestCase("", true, true, false, true)]
        [TestCase("", true, false, true, false)]   // enabled: not bound means no texture, even with content
        [TestCase("", false, true, false, false)]
        [TestCase("", false, false, true, true)]   // disabled: keeps the last content, also when QtNative unbound the scene
        [TestCase("error", true, true, true, false)]
        [TestCase("error", false, false, true, false)]
        public void VisibleTextureFollowsEnabledBoundAndContent(string error, bool enabled, bool bound, bool content, bool shown)
        {
            var texture = new object();
            Assert.That(TextureState.Visible(texture, error, enabled, bound, content), shown ? Is.SameAs(texture) : Is.Null);
        }

        [Test]
        public void NoTextureIsNeverVisible()
            => Assert.That(TextureState.Visible<object>(null, "", true, true, true), Is.Null);

        [Test]
        public void FirstErrorPrefersTextureThenSceneThenBatch()
        {
            Assert.That(TextureState.FirstError("t", "s", "b"), Is.EqualTo("t"));
            Assert.That(TextureState.FirstError("", "s", "b"), Is.EqualTo("s"));
            Assert.That(TextureState.FirstError("", "", "b"), Is.EqualTo("b"));
            Assert.That(TextureState.FirstError("", "", ""), Is.Empty);
        }
    }
}
