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

    /// <summary>
    /// The effects trace replayed after readme-images in one process (W1-20): readme-images creates shader resources whose
    /// ids the trace used to refer to, so its effect chains ran the glow shaders instead of RGBSplit.
    /// </summary>
    [Test, Category("replay-traces"), Category("image-compare"), Category("own-process")]
    public static void ReplayTraceAfterReadmeImages() => Scenario.RunLocal(s => ImageCompare.Run(s, new[] { "readme-images", "trace-effects" }));

    /// <summary>Body of the compare scene "trace-&lt;name&gt;" (runs in the helper process).</summary>
    internal static void Run(Scenario s, string name)
    {
        var traces = ImageCompare.FindUp("src", "VL.Qt.SmokeTest", "traces");
        if (traces == null)
        {
            s.Check(false, $"trace-{name}: traces folder not found");
            return;
        }
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
        frames = Prepare(s, traces, name, frames, "trace-" + name, checkMapped: true, out _);

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
    /// Creates the resources the patch had loaded (the trace holds only their ids): the image files listed in
    /// <c>&lt;name&gt;.images</c> and the shader files listed in <c>&lt;name&gt;.shaders</c> (one path relative to the
    /// repository per line, in order of first appearance of the ids in the trace), then rewrites the trace to their ids.
    /// Without this a trace refers to ids that do not exist in the replaying process, or, after another scene in the same
    /// process created resources, to that scene's resources (W1-20: readme-images before trace-effects).
    /// </summary>
    /// <param name="resources">The created image and shader resources, for the caller to release.</param>
    internal static List<byte[]> Prepare(Scenario s, string traces, string name, List<byte[]> frames, string label, bool checkMapped,
        out List<ulong> resources)
    {
        var repo = Path.GetFullPath(Path.Combine(traces, "..", "..", ".."));
        var images = new List<ulong>();
        foreach (var rel in ListFile(Path.Combine(traces, name + ".images")))
        {
            var path = Path.GetFullPath(Path.Combine(repo, rel));
            s.Check(File.Exists(path), $"{label}: image file {rel} exists");
            images.Add(ImageTest.Load(path, (ok, what) => s.Check(ok, $"{label}: {what}"), rel));
        }
        var shaders = new List<ulong>();
        foreach (var rel in ListFile(Path.Combine(traces, name + ".shaders")))
        {
            var path = Path.GetFullPath(Path.Combine(repo, rel));
            var exists = File.Exists(path);
            s.Check(exists, $"{label}: shader file {rel} exists");
            if (!exists)
            {
                shaders.Add(0);
                continue;
            }
            var stage = path.EndsWith(".vert", StringComparison.OrdinalIgnoreCase) ? Protocol.ShaderStage.Vertex : Protocol.ShaderStage.Fragment;
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader_source(id, File.ReadAllText(path), stage);
            s.Check(Native.vlqt_res_wait(id, 20000) == Protocol.ResState.Ready, $"{label}: shader {rel} baked, Ready");
            shaders.Add(id);
        }
        resources = new List<ulong>(images);
        resources.AddRange(shaders.FindAll(id => id != 0));
        var result = Rewrite(frames, Path.Combine(repo, "help", "Qt"), images, shaders, out var unmappedImages, out var unmappedShaders);
        if (checkMapped)
        {
            s.Check(unmappedImages == 0, $"{label}: every image id of the trace has a file in {name}.images ({unmappedImages} without)");
            s.Check(unmappedShaders == 0, $"{label}: every shader id of the trace has a file in {name}.shaders ({unmappedShaders} without)");
        }
        return result;
    }

    static IEnumerable<string> ListFile(string file)
    {
        if (!File.Exists(file))
            yield break;
        foreach (var line in File.ReadAllLines(file))
            if (line.Trim() is { Length: > 0 } rel)
                yield return rel;
    }

    /// <summary>
    /// Makes a trace portable: image ids (Prop.Image) and shader ids (Prop.FragmentShader / VertexShader, shader links of
    /// SET_EFFECT_CHAIN) become, in order of first appearance, the ids of the resources created here; strings that point
    /// into the help/Qt folder of the recording machine (QmlElement source) point into <paramref name="helpQtDir"/>. Walks
    /// the commands (u16 op, u16 payload length, payload, padded to 4). An id without a resource becomes 0 (counted).
    /// </summary>
    internal static List<byte[]> Rewrite(List<byte[]> frames, string helpQtDir, IReadOnlyList<ulong> images, IReadOnlyList<ulong> shaders,
        out int unmappedImages, out int unmappedShaders)
    {
        var map = new Dictionary<ulong, ulong>();
        var shaderMap = new Dictionary<ulong, ulong>();
        var missing = 0;
        var missingShaders = 0;
        ulong MapShader(ulong id)
        {
            if (id == 0)
                return 0;
            if (!shaderMap.TryGetValue(id, out var mapped))
                shaderMap[id] = mapped = shaderMap.Count < shaders.Count ? shaders[shaderMap.Count] : 0;
            if (mapped == 0)
                missingShaders++;
            return mapped;
        }
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
                if (op == Protocol.Op.SetEffectChain && payload >= 8)
                {
                    // u32 id, u16 linkCount, u16 flags; per link u16 kind, u16 count, u32 body length, body. A shader link
                    // body starts with u16 template, u16 shaderCount, u64 shaderIds[shaderCount].
                    var copy = cmd.ToArray();
                    var links = BitConverter.ToUInt16(copy, Protocol.HeaderSize + 4);
                    var at = Protocol.HeaderSize + 8;
                    for (var i = 0; i < links && at + 8 <= Protocol.HeaderSize + payload; i++)
                    {
                        var kind = BitConverter.ToUInt16(copy, at);
                        var body = (int)BitConverter.ToUInt32(copy, at + 4);
                        if (kind == Protocol.EffectLink.Shader && body >= 4)
                        {
                            var count = BitConverter.ToUInt16(copy, at + 10);
                            for (var k = 0; k < count && 4 + (k + 1) * 8 <= body; k++)
                            {
                                var p = at + 12 + k * 8;
                                BitConverter.TryWriteBytes(copy.AsSpan(p), MapShader(BitConverter.ToUInt64(copy, p)));
                            }
                        }
                        at += 8 + body;
                    }
                    o.AddRange(copy);
                    continue;
                }
                if (op == Protocol.Op.Set && payload >= 12)
                {
                    var prop = BitConverter.ToUInt16(cmd.Slice(8));
                    var vt = BitConverter.ToUInt16(cmd.Slice(10));
                    if (vt == Protocol.Vt.U64 && (prop == Protocol.Prop.FragmentShader || prop == Protocol.Prop.VertexShader))
                    {
                        var copy = cmd.ToArray();
                        BitConverter.TryWriteBytes(copy.AsSpan(12), MapShader(BitConverter.ToUInt64(copy, 12)));
                        o.AddRange(copy);
                        continue;
                    }
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
        unmappedImages = missing;
        unmappedShaders = missingShaders;
        return result;
    }
}
