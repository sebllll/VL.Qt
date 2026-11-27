using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// Decoder robustness: the recorded traces (src/VL.Qt.SmokeTest/traces) are damaged in two ways and applied to QtNative:
// every command buffer truncated at every 4th byte, and single bytes flipped (fixed seed). Whatever arrives, vlqt_scene_apply must
// return 0 or a negative ApplyResult, never crash, and vlqt_scene_get_apply_error must tell the same story as the return code.
// The damaged frame k is applied to the scene that frames 0..k-1 built; afterwards the scene is rebuilt (RESET + replay), so the
// next frame starts from a clean state.
[TestFixture, NonParallelizable]
public static class ApplyFuzzTest
{
    private const int Seed = 20260101;
    private const int TruncateStep = 4;
    // Flipped buffers per frame: one per 2 bytes, at least 64.
    private const int FlipsPerBytes = 2, MinFlips = 64;

    /// <summary>One recorded trace, damaged: truncated at every 4th byte and byte flips; apply returns 0 or an ApplyResult and agrees with
    /// apply_error.</summary>
    [Test, TestCaseSource(typeof(TraceReplay), "TraceNames"), Category("apply-fuzz")]
    public static void Fuzz(string name) => Scenario.Run(s => Run(s, name));

    /// <summary>Applies a buffer; null if the result is as the contract says (0 or a negative ApplyResult, apply_error telling the same
    /// story), else what is wrong.</summary>
    private static string? Judge(SceneHandle w, byte[] buf, out Native.Result rc)
    {
        rc = Native.Apply(w, buf);
        var got = Native.GetApplyError(w, out var ae);
        if (rc > Native.Result.Ok || rc < Native.Result.Invalid || rc == Native.Result.NullHandle)
            return $"return code {rc} is not 0 or a negative ApplyResult";
        if (got != Native.Result.Ok)
            return $"apply_error returned {got}";
        if (ae.Rc != (int)rc)
            return $"apply_error.rc {ae.Rc} differs from the return code {rc}";
        if (rc == 0 && (ae.Reason.Length != 0 || ae.Id != 0 || ae.Command != 0))
            return $"success, but apply_error holds \"{ae.Reason}\" (id {ae.Id}, command {ae.Command})";
        if (rc < 0 && (ae.Reason.Length == 0 || ae.Command < 0 || !Native.LastError().Contains("apply: command #" + ae.Command)))
            return $"failure {rc}, but apply_error has reason \"{ae.Reason}\", command {ae.Command}, last error \"{Native.LastError()}\"";
        return null;
    }

    /// <summary>LIST values in SET_QML_PROPERTIES of every element type, damaged: truncated at every byte, every byte flipped, and
    /// every 4 / 2 byte field replaced by values that are hostile for a count or an element type. Whatever arrives, apply returns 0 or an
    /// ApplyResult and agrees with apply_error; the undamaged command still applies afterwards, and the QML element loads it.</summary>
    [Test, Category("apply-fuzz")]
    public static void FuzzLists() => Scenario.Run(RunLists);

    private static void RunLists(Scenario s)
    {
        var qmlTypes = ImageCompare.FindUp("src", "VL.Qt.SmokeTest", "assets", "QmlTypes.qml");
        s.Check(qmlTypes != null, "fuzz-lists: QmlTypes.qml found");
        if (qmlTypes == null)
            return;
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt apply fuzz lists");
        s.Check(w != SceneHandle.Null, "fuzz-lists: window_create");
        if (w == SceneHandle.Null)
            return;
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        cmd.Create(1, Protocol.ElementType.Qml);
        cmd.SetString(1, Protocol.PropertyId.QmlSource, qmlTypes);
        cmd.Reparent(1, 0, 0);
        s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "fuzz-lists: create the QmlElement");
        s.Pump(3);

        // One of every list type (ints and strings twice: one empty), as the patch would send them.
        cmd.Clear();
        cmd.BeginQmlProperties(1, 0);
        cmd.QmlPropertyListF32("rl", Protocol.ValueType.F32, new[] { 0.25f, -1.5f, 1000f });
        cmd.QmlPropertyListF32("dl", Protocol.ValueType.F32, ReadOnlySpan<float>.Empty);
        cmd.QmlPropertyListI32("il", new[] { 7, -3, int.MaxValue, 0 });
        cmd.QmlPropertyListBool("bl", new[] { true, false, true, true, false });
        cmd.QmlPropertyListString("sl", new[] { "", "a", "ab", "abc", "abcd", "hä€😀" });
        cmd.QmlPropertyListF32("cl", Protocol.ValueType.Color, new[] { 1f, 0, 0, 1, 0.2f, 0.4f, 0.6f, 0.8f });
        cmd.QmlPropertyListF32("ptl", Protocol.ValueType.Vec2, new[] { 1f, 2, -3.5f, 4 });
        cmd.QmlPropertyListF32("szl", Protocol.ValueType.Vec2, new[] { 640f, 480 });
        cmd.QmlPropertyListF32("v2l", Protocol.ValueType.Vec2, new[] { 0f, 1 });
        cmd.QmlPropertyListF32("v3l", Protocol.ValueType.Vec3, new[] { 1f, 2, 3, 4, 5, 6 });
        cmd.QmlPropertyListF32("v4l", Protocol.ValueType.Vec4, new[] { 4f, 3, 2, 1 });
        cmd.QmlPropertyListF32("boxl", Protocol.ValueType.Rectf, new[] { 10f, 20, 30, 40, 1, 2, 3, 4 });
        cmd.QmlPropertyListString("sl", Array.Empty<string>());
        cmd.EndQmlProperties();
        var frame = cmd.EndFrame().ToArray();
        cmd.Clear();
        s.Check(Native.Apply(w, frame) == 0, $"fuzz-lists: the undamaged command applies ({Native.LastError()})");
        s.Pump(3);

        var rng = new Random(Seed);
        var clock = Stopwatch.StartNew();
        int applies = 0, accepted = 0, violations = 0;
        var firstViolations = new List<string>();
        void Probe(string what, byte[] buf)
        {
            var problem = Judge(w, buf, out var rc);
            applies++;
            if (rc == 0)
                accepted++;
            if (problem != null)
            {
                violations++;
                if (firstViolations.Count < 5)
                    firstViolations.Add($"{what}: {problem}");
            }
            if (applies % 256 == 0)
                s.Pump(1); // QML converts what was accepted
        }

        for (int cut = 0; cut < frame.Length; cut++)
            Probe($"cut at {cut}/{frame.Length}", frame.AsSpan(0, cut).ToArray());
        for (int pos = 0; pos < frame.Length; pos++)
        {
            foreach (var bits in new byte[] { 0x01, 0x80, 0xFF, (byte)rng.Next(1, 256) })
            {
                var copy = (byte[])frame.Clone();
                copy[pos] ^= bits;
                Probe($"byte {pos} ^ 0x{bits:X2}", copy);
            }
        }
        // Counts and sizes: every aligned u32 replaced by the numbers around the limit and the signs.
        foreach (var poison in new uint[] { 0, 1, 3, 65535, 65536, 65537, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF })
        {
            for (int pos = 0; pos + 4 <= frame.Length; pos += 4)
            {
                var copy = (byte[])frame.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(pos), poison);
                Probe($"u32 at {pos} = {poison}", copy);
            }
        }
        // Element types and value types: every aligned u16 replaced by each value type, the neighbours of the list ones, and nonsense.
        foreach (var value in new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 20, 21, 999, 0xFFFF })
        {
            for (int pos = 0; pos + 2 <= frame.Length; pos += 2)
            {
                var copy = (byte[])frame.Clone();
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(pos), value);
                Probe($"u16 at {pos} = {value}", copy);
            }
        }
        s.Info($"apply fuzz lists: {applies} damaged buffers ({accepted} accepted, {applies - accepted} rejected) in {clock.Elapsed.TotalSeconds:F1} s");
        s.Check(violations == 0, $"fuzz-lists: {violations} violations of {applies} applies" + (violations > 0 ? " (" + string.Join("; ", firstViolations) + ")" : ""));
        s.Pump(3);
        // The element is still alive and takes the undamaged command.
        var rc2 = Native.Apply(w, frame);
        s.Check(rc2 == 0, $"fuzz-lists: the undamaged command still applies after the damaged ones ({rc2}) {(rc2 != 0 ? Native.LastError() : "")}");
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }

    private static void Run(Scenario s, string name)
    {
        var traces = ImageCompare.FindUp("src", "VL.Qt.SmokeTest", "traces");
        if (traces == null)
        {
            s.Check(false, $"fuzz-{name}: traces folder not found");
            return;
        }
        var frames = ReplayTest.ReadFrames(Path.Combine(traces, name + ".bin"));
        s.Check(frames.Count > 0, $"fuzz-{name}: {frames.Count} frames");
        if (frames.Count == 0)
            return;

        // Image and shader resources the trace refers to (same preparation as TraceReplay).
        frames = TraceReplay.Prepare(s, traces, name, frames, "fuzz-" + name, checkMapped: false, out var resources);

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt apply fuzz " + name);
        s.Check(w != SceneHandle.Null, $"fuzz-{name}: window_create");
        if (w == SceneHandle.Null)
            return;
        Native.vlqt_window_show(w);
        s.Pump(2);

        var reset = new byte[Protocol.HeaderSize];
        reset[0] = (byte)Protocol.Op.Reset; // u16 op, u16 flags 0, u32 payload length 0
        var rng = new Random(Seed);
        var clock = Stopwatch.StartNew();
        int applies = 0, accepted = 0, violations = 0;
        var firstViolations = new List<string>();

        void Probe(string what, byte[] buf)
        {
            var problem = Judge(w, buf, out var rc);
            applies++;
            if (rc == 0)
                accepted++;
            if (problem != null)
            {
                violations++;
                if (firstViolations.Count < 5)
                    firstViolations.Add($"{what}: {problem}");
            }
        }

        // Rebuilds the scene of frames 0..k-1 after damaged commands may have changed it.
        void Rebuild(int k)
        {
            Native.Apply(w, reset);
            for (int i = 0; i < k; i++)
                Native.Apply(w, frames[i]);
            s.Pump(1);
        }

        for (int k = 0; k < frames.Count; k++)
        {
            var frame = frames[k];
            for (int cut = 0; cut < frame.Length; cut += TruncateStep)
                Probe($"frame {k} cut at {cut}/{frame.Length}", frame.AsSpan(0, cut).ToArray());
            s.Pump(1);
            var flips = Math.Max(MinFlips, frame.Length / FlipsPerBytes);
            for (int f = 0; f < flips && frame.Length > 0; f++)
            {
                var copy = (byte[])frame.Clone();
                var pos = rng.Next(copy.Length);
                var bits = (byte)rng.Next(1, 256);
                copy[pos] ^= bits;
                Probe($"frame {k} byte {pos} ^ 0x{bits:X2}", copy);
                if (f % 32 == 31)
                    s.Pump(1);
            }
            s.Pump(1);
            Rebuild(k);
            // The undamaged frame still applies to the rebuilt scene.
            var rc = Native.Apply(w, frame);
            if (rc != 0)
            {
                violations++;
                firstViolations.Add($"frame {k}: undamaged frame returns {rc} after the damaged ones: {Native.LastError()}");
            }
            s.Pump(1);
        }
        s.Info($"apply fuzz {name}: {applies} damaged buffers ({accepted} accepted, {applies - accepted} rejected) in {clock.Elapsed.TotalSeconds:F1} s");
        s.Check(violations == 0, $"fuzz-{name}: {violations} violations of {applies} applies" + (violations > 0 ? " (" + string.Join("; ", firstViolations) + ")" : ""));
        s.Pump(2);
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
        // Later in-process scenarios (image-resource, image-share, resoverlay) expect no resources left.
        foreach (var id in resources)
            Native.vlqt_res_release(id);
        s.Pump(2);
    }
}
