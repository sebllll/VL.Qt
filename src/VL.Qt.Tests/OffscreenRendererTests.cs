using System;
using System.Linq;
using NUnit.Framework;

namespace VL.Qt.Tests
{
    /// <summary>OffscreenRenderer (#116 QtTexture): the error texts need no native device, the rest needs a built vlqt.dll.</summary>
    public class OffscreenRendererTests
    {
        static int[] ErrorCodes => typeof(Protocol.Offscreen).GetFields()
            .Where(f => f.IsLiteral && f.GetRawConstantValue() is int v && v < 0)
            .Select(f => (int)f.GetRawConstantValue()!)
            .ToArray();

        [Test]
        public void Describe_NoTextForSuccessAndRenderedCount()
        {
            Assert.That(OffscreenRenderer.Describe(Protocol.Offscreen.Ok), Is.Empty);
            Assert.That(OffscreenRenderer.Describe(3), Is.Empty);
        }

        [Test]
        public void Describe_EveryCodeHasItsOwnText()
        {
            var codes = ErrorCodes;
            Assert.That(codes, Is.Not.Empty);
            var texts = codes.Select(OffscreenRenderer.Describe).ToArray();
            Assert.That(texts, Has.All.Not.Empty);
            Assert.That(texts, Is.Unique);
            // Only a code of a newer QtNative falls back to the number.
            Assert.That(texts, Has.None.StartWith("Offscreen scene error"));
        }

        [Test]
        public void Describe_UnknownCodeShowsNumber()
            => Assert.That(OffscreenRenderer.Describe(-999), Is.EqualTo("Offscreen scene error -999"));

        [Test]
        public void Lifecycle_WithoutOrWithNativeScene()
        {
            ShaderReflectNativeTests.LinkNative();
            var r = new OffscreenRenderer(new SceneRegistry(), 64, 32);
            Assert.That(r.IsBound, Is.False);
            if (r.Scene == IntPtr.Zero)
            {
                // No scene (stub, or QtNative not started): the error says so, nothing throws.
                Assert.That(r.Error, Is.Not.Empty);
                Assert.That(r.Bind(IntPtr.Zero, IntPtr.Zero, new Stride.Core.Mathematics.Int2(64, 32), 28), Is.EqualTo(Protocol.Offscreen.NullScene));
            }
            else
            {
                var rc = r.Bind(IntPtr.Zero, IntPtr.Zero, new Stride.Core.Mathematics.Int2(64, 32), 28);
                Assert.That(rc, Is.LessThan(0), "no device and no texture: no binding");
                Assert.That(r.Error, Is.EqualTo(OffscreenRenderer.Describe(rc)));
                Assert.That(r.IsBound, Is.False);
            }
            r.Unbind();
            r.Dispose();
            Assert.That(r.Scene, Is.EqualTo(IntPtr.Zero));
            Assert.That(r.IsBound, Is.False);
            Assert.That(r.Bind(IntPtr.Zero, IntPtr.Zero, new Stride.Core.Mathematics.Int2(64, 32), 28), Is.EqualTo(Protocol.Offscreen.NullScene));
            r.Dispose(); // twice is fine
        }

        [Test]
        public void RenderBatch_NullContextIsAnError()
        {
            ShaderReflectNativeTests.LinkNative();
            var r = new OffscreenRenderer(new SceneRegistry(), 16, 16);
            try
            {
                Assert.That(OffscreenRenderer.RenderBatch(IntPtr.Zero, new[] { r }, 0), Is.LessThan(0));
                Assert.That(OffscreenRenderer.RenderBatch(IntPtr.Zero, ReadOnlySpan<OffscreenRenderer>.Empty, 0), Is.LessThan(0));
            }
            finally
            {
                r.Dispose();
            }
        }
    }
}
