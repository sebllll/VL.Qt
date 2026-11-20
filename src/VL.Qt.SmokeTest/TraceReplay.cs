using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using VL.Qt;

// Traces recorded from help patches (TEST-04, tools/record-traces.ps1): each trace in src/VL.Qt.SmokeTest/traces/<name>.bin
// is replayed against QtNative and its last frame compared with references/trace-<name>.png (ImageCompare: WARP, own process,
// like the other compare scenes). A trace is bound to the wire format: after a protocol change run tools/record-traces.ps1
// and the explicit test `update-refs`.
[TestFixture, NonParallelizable]
public static class TraceReplay
{
    const int Width = 800, Height = 600;

    /// <summary>Names of the recorded traces (files traces/&lt;name&gt;.bin except the hand-written testrect), sorted.</summary>
    internal static string[] TraceNames()
    {
        var dir = ImageCompare.FindUp("src", "VL.Qt.SmokeTest", "traces");
        if (dir == null)
            return Array.Empty<string>();
        var names = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "*.bin"))
        {
            var n = Path.GetFileNameWithoutExtension(f);
            if (n != "testrect")
                names.Add(n);
        }
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    /// <summary>Compare scene names of all recorded traces ("trace-&lt;name&gt;", see OwnProcess).</summary>
    internal static string[] TraceScenes() => Array.ConvertAll(TraceNames(), n => "trace-" + n);

    /// <summary>One recorded trace replayed against QtNative, last frame against references/trace-&lt;name&gt;.png (WARP, own process).</summary>
    [Test, TestCaseSource(nameof(TraceNames)), Category("replay-traces"), Category("image-compare"), Category("own-process")]
    public static void ReplayTraces(string name) => Scenario.RunLocal(s => ImageCompare.Run(s, new[] { "trace-" + name }));

    /// <summary>Body of the compare scene "trace-&lt;name&gt;" (runs in the helper process).</summary>
    internal static void Run(Scenario s, string name)
    {
        var traces = ImageCompare.FindUp("src", "VL.Qt.SmokeTest", "traces");
        if (traces == null)
        {
            s.Check(false, $"trace-{name}: traces folder not found");
            return;
        }
        var repo = Path.GetFullPath(Path.Combine(traces, "..", "..", ".."));
        List<byte[]> frames;
        try { frames = ReplayTest.ReadFrames(Path.Combine(traces, name + ".bin")); }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            s.Check(false, $"trace-{name}: unreadable: {e.Message}");
            return;
        }
        s.Check(frames.Count > 0, $"trace-{name}: {frames.Count} frames");
        if (frames.Count == 0)
            return;

        // Image resources the patch had loaded: the trace holds only their ids, the files are listed in <name>.images.
        var images = new List<ulong>();
        var imagesFile = Path.Combine(traces, name + ".images");
        if (File.Exists(imagesFile))
            foreach (var line in File.ReadAllLines(imagesFile))
            {
                var rel = line.Trim();
                if (rel.Length == 0)
                    continue;
                var path = Path.GetFullPath(Path.Combine(repo, rel));
                s.Check(File.Exists(path), $"trace-{name}: image file {rel} exists");
                images.Add(ImageTest.Load(path, (ok, what) => s.Check(ok, $"trace-{name}: {what}"), rel));
            }
        frames = Rewrite(frames, Path.Combine(repo, "help", "Qt"), images, out var unmapped);
        s.Check(unmapped == 0, $"trace-{name}: every image id of the trace has a file in {name}.images ({unmapped} without)");

        var w = Native.vlqt_window_create(Width, Height, "VL.Qt trace " + name);
        s.Check(w != IntPtr.Zero, $"trace-{name}: window_create");
        if (w == IntPtr.Zero)
            return;
        Native.vlqt_window_show(w);
        s.Pump(4);
        var msgs = new List<string>();
        var errors = ReplayTest.Apply(w, frames, s.Pump, msgs.Add);
        s.Check(errors == 0, $"trace-{name}: all {frames.Count} frames apply without errors" + (msgs.Count > 0 ? " (" + string.Join("; ", msgs.GetRange(0, Math.Min(3, msgs.Count))) + ")" : ""));
        // Shaders bake asynchronously: pump until two grabs 10 frames apart are identical (at most 60 rounds), so the picture is the settled one.
        var grabPath = Path.Combine(s.OutDir, $"trace-{name}.png");
        var probePath = Path.Combine(s.OutDir, $"trace-{name}-probe.png");
        byte[]? previous = null;
        var settled = false;
        for (var round = 0; round < 60 && !settled; round++)
        {
            s.Pump(10);
            if (Native.vlqt_window_grab(w, probePath) != 0)
                break;
            var bytes = File.ReadAllBytes(probePath);
            settled = previous != null && bytes.AsSpan().SequenceEqual(previous);
            previous = bytes;
        }
        File.Delete(probePath);
        s.Check(settled, $"trace-{name}: picture settled (two identical grabs)");
        using (var a = s.Grab(w, grabPath))
        {
            var ink = ImageCompare.Ink(a);
            s.Check(ink > 0.005, $"trace-{name}: picture not empty (ink {ink:P1})");
        }
        ImageCompare.Check(s, "trace-" + name, grabPath, Width);
        Native.vlqt_window_destroy(w);
        s.Pump(1);
    }

    /// <summary>
    /// Makes a trace portable: image ids (Prop.Image) become, in order of first appearance, the ids of the resources created
    /// here; strings that point into the help/Qt folder of the recording machine (QmlElement source) point into
    /// <paramref name="helpQtDir"/>. Walks the commands (u16 op, u16 payload length, payload, padded to 4).
    /// </summary>
    internal static List<byte[]> Rewrite(List<byte[]> frames, string helpQtDir, IReadOnlyList<ulong> images, out int unmapped)
    {
        var map = new Dictionary<ulong, ulong>();
        var missing = 0;
        var result = new List<byte[]>(frames.Count);
        foreach (var frame in frames)
        {
            var o = new List<byte>(frame.Length);
            for (var pos = 0; pos + Protocol.HeaderSize <= frame.Length;)
            {
                var op = BitConverter.ToUInt16(frame, pos);
                var payload = BitConverter.ToUInt16(frame, pos + 2);
                var total = (Protocol.HeaderSize + payload + 3) & ~3;
                if (pos + total > frame.Length)
                    throw new InvalidDataException($"command at {pos} exceeds the frame");
                var cmd = frame.AsSpan(pos, total);
                pos += total;
                if (op == Protocol.Op.Set && payload >= 12)
                {
                    var prop = BitConverter.ToUInt16(cmd.Slice(8));
                    var vt = BitConverter.ToUInt16(cmd.Slice(10));
                    if (vt == Protocol.Vt.U64 && prop == Protocol.Prop.Image)
                    {
                        var id = BitConverter.ToUInt64(cmd.Slice(12));
                        var copy = cmd.ToArray();
                        if (id != 0)
                        {
                            if (!map.ContainsKey(id))
                                map[id] = map.Count < images.Count ? images[map.Count] : 0;
                            if (map[id] == 0)
                                missing++;
                            BitConverter.TryWriteBytes(copy.AsSpan(12), map[id]);
                        }
                        o.AddRange(copy);
                        continue;
                    }
                    if (vt == Protocol.Vt.String)
                    {
                        var text = System.Text.Encoding.UTF8.GetString(cmd.Slice(16, (int)BitConverter.ToUInt32(cmd.Slice(12))));
                        var marker = "help" + (text.Contains('\\') ? "\\Qt\\" : "/Qt/");
                        var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                        if (at > 0 && Path.IsPathRooted(text))
                        {
                            var rest = text[(at + marker.Length)..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                            var bytes = System.Text.Encoding.UTF8.GetBytes(Path.Combine(helpQtDir, rest));
                            var newPayload = 12 + bytes.Length;
                            var buf = new byte[(Protocol.HeaderSize + newPayload + 3) & ~3];
                            cmd.Slice(0, 16).CopyTo(buf);
                            BitConverter.TryWriteBytes(buf.AsSpan(2), (ushort)newPayload);
                            BitConverter.TryWriteBytes(buf.AsSpan(12), (uint)bytes.Length);
                            bytes.CopyTo(buf, 16);
                            o.AddRange(buf);
                            continue;
                        }
                    }
                }
                o.AddRange(cmd.ToArray());
            }
            result.Add(o.ToArray());
        }
        unmapped = missing;
        return result;
    }
}
