using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>OffscreenScene (QtTexture): the error texts need no native device, the rest needs a built vlqt.dll.</summary>
    public class OffscreenSceneTests
    {
        // The codes the offscreen exports return (vlqt_result: the shared ones and -20 ... -25).
        private static readonly Native.Result[] s_errorCodes =
        {
            Native.Result.NullHandle, Native.Result.BadArgument, Native.Result.Unsupported, Native.Result.Unbound, Native.Result.AdapterMismatch,
            Native.Result.OpenFailed, Native.Result.FormatUnsupported, Native.Result.FenceFailed, Native.Result.DeviceLost,
        };

        [Test]
        public void Describe_NoTextForSuccess()
            => Assert.That(OffscreenScene.Describe(Native.Result.Ok), Is.Empty);

        [Test]
        public void Describe_EveryCodeHasItsOwnText()
        {
            var texts = s_errorCodes.Select(OffscreenScene.Describe).ToArray();
            Assert.That(texts, Has.All.Not.Empty);
            Assert.That(texts, Is.Unique);
            // Only a code of a newer QtNative falls back to the number.
            Assert.That(texts, Has.None.StartWith("Offscreen scene error"));
        }

        [Test]
        public void Describe_UnknownCodeShowsNumber()
            => Assert.That(OffscreenScene.Describe((Native.Result)(-999)), Is.EqualTo("Offscreen scene error -999"));

        [Test]
        public void Lifecycle_WithoutOrWithNativeScene()
        {
            ShaderReflectNativeTests.LinkNative();
            var r = new OffscreenScene(new SceneRegistry(), 64, 32);
            Assert.That(r.IsBound, Is.False);
            if (r.Handle == SceneHandle.Null)
            {
                // No scene (stub, or QtNative not started): the error says so, nothing throws.
                Assert.That(r.Error, Is.Not.Empty);
                Assert.That(r.Bind(IntPtr.Zero, IntPtr.Zero, new Int2(64, 32), 28), Is.EqualTo(Native.Result.NullHandle));
            }
            else
            {
                var rc = r.Bind(IntPtr.Zero, IntPtr.Zero, new Int2(64, 32), 28);
                Assert.That(rc, Is.Not.EqualTo(Native.Result.Ok), "no device and no texture: no binding");
                Assert.That(r.Error, Is.EqualTo(OffscreenScene.Describe(rc)));
                Assert.That(r.IsBound, Is.False);
            }
            r.Unbind();
            r.Dispose();
            Assert.That(r.Handle, Is.EqualTo(SceneHandle.Null));
            Assert.That(r.IsBound, Is.False);
            Assert.That(r.Bind(IntPtr.Zero, IntPtr.Zero, new Int2(64, 32), 28), Is.EqualTo(Native.Result.NullHandle));
            r.Dispose(); // twice is fine
        }

        [Test]
        public void RenderBatch_NullContextIsAnError()
        {
            ShaderReflectNativeTests.LinkNative();
            var r = new OffscreenScene(new SceneRegistry(), 16, 16);
            try
            {
                Assert.That(OffscreenScene.RenderBatch(IntPtr.Zero, new[] { r.Handle }, 0), Is.LessThan(0));
                Assert.That(OffscreenScene.RenderBatch(IntPtr.Zero, ReadOnlySpan<SceneHandle>.Empty, 0), Is.LessThan(0));
            }
            finally
            {
                r.Dispose();
            }
        }
    }
}
