using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using VL.Qt.Interop;

namespace VL.Qt.Tests
{
    /// <summary>
    /// The structs of vlqt.h and their mirrors in `Interop/Native.*.cs` have the same size and the same offset of the last field.
    /// The numbers are the ones of the static_asserts at the top of vlqt.cpp (a C++ compiler checks the header, this test
    /// the mirror); change all three places together. They hold on every 64-bit target, so also on Windows arm64 and Linux.
    /// </summary>
    [TestFixture]
    public sealed class NativeLayoutTests
    {
        private static IEnumerable<TestCaseData> Layouts()
        {
            yield return Case<Native.vlqt_perf>(112, "work_ms", 104);
            yield return Case<Native.vlqt_perf_sample>(40, "work", 36);
            yield return Case<Native.vlqt_perf_stats>(160, "recent_multi_swap", 152);
            yield return Case<Native.vlqt_apply_error>(24, "reason", 16);
            yield return Case<Native.vlqt_host_pump>(48, "wait", 44);
            yield return Case<Native.vlqt_host_pump_stats>(128, "wait_ms_max", 120);
            yield return Case<Native.vlqt_res_info>(32, "element_refs", 28);
            yield return Case<Native.vlqt_pixel_desc>(24, "flags", 20);
            yield return Case<Native.vlqt_audio_tap_state>(96, "_reserved0", 88); // reserved[2] in vlqt.h
            yield return Case<Native.vlqt_scene_metrics>(12, "dpr", 8);
        }

        private static TestCaseData Case<T>(int size, string lastField, int lastOffset) where T : struct
            => new TestCaseData(typeof(T), size, lastField, lastOffset).SetName("Layout_" + typeof(T).Name);

        [TestCaseSource(nameof(Layouts))]
        public void SizeAndLastFieldMatchTheHeader(Type type, int size, string lastField, int lastOffset)
        {
            Assert.That(Marshal.SizeOf(type), Is.EqualTo(size), type.Name + " Marshal.SizeOf");
            Assert.That((int)Marshal.OffsetOf(type, lastField), Is.EqualTo(lastOffset), type.Name + "." + lastField + " offset");
        }

        // Unsafe.SizeOf is the managed size, Marshal.SizeOf the one the P/Invoke marshaller uses: both must be the C size
        // (the structs hold blittable fields only).
        [Test]
        public void ManagedSizesEqualMarshalledSizes()
        {
            Assert.That(Unsafe.SizeOf<Native.vlqt_perf>(), Is.EqualTo(112));
            Assert.That(Unsafe.SizeOf<Native.vlqt_perf_sample>(), Is.EqualTo(40));
            Assert.That(Unsafe.SizeOf<Native.vlqt_perf_stats>(), Is.EqualTo(160));
            Assert.That(Unsafe.SizeOf<Native.vlqt_apply_error>(), Is.EqualTo(24));
            Assert.That(Unsafe.SizeOf<Native.vlqt_host_pump>(), Is.EqualTo(48));
            Assert.That(Unsafe.SizeOf<Native.vlqt_host_pump_stats>(), Is.EqualTo(128));
            Assert.That(Unsafe.SizeOf<Native.vlqt_res_info>(), Is.EqualTo(32));
            Assert.That(Unsafe.SizeOf<Native.vlqt_pixel_desc>(), Is.EqualTo(24));
            Assert.That(Unsafe.SizeOf<Native.vlqt_audio_tap_state>(), Is.EqualTo(96));
            Assert.That(Unsafe.SizeOf<Native.vlqt_scene_metrics>(), Is.EqualTo(12));
        }

        /// <summary>The handle of a scene (vlqt_scene*) is blittable and as big as a pointer, so it passes and returns like one.</summary>
        [Test]
        public void SceneHandleIsAPointer()
        {
            Assert.That(RuntimeHelpers.IsReferenceOrContainsReferences<SceneHandle>(), Is.False);
            Assert.That(Unsafe.SizeOf<SceneHandle>(), Is.EqualTo(IntPtr.Size));
            Assert.That(Marshal.SizeOf<SceneHandle>(), Is.EqualTo(IntPtr.Size));
            Assert.That(default(SceneHandle).IsNull, Is.True);
            Assert.That(new SceneHandle(1).IsNull, Is.False);
        }
    }
}
