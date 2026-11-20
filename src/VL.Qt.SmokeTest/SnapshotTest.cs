using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using static Pixels;

// Snapshot: SnapshotNode → ResourceClient → QtNative (QQuickItem::grabToImage, asynchronous). Pixels against
// vlqt_window_grab, latency in pumps, error paths, hitch rate with periodic snapshots. PNGs go to <OutDir>/363_*.png.
[TestFixture, NonParallelizable]
public static class SnapshotTest
{
    private const int MaxLatency = 8; // frames from Grab to Is Ready (a grab needs the next render of its window)

    /// <summary>BGRA8 premultiplied pixel at (x, y) of the snapshot.</summary>
    internal static (int B, int G, int R, int A) At(IImage image, int x, int y, int sizeDip = 300)
    {
        // x/y are DIP of a snapshot of 300 DIP width at scale sizeDip/300; the image may be larger on a HiDPI window (see the size check).
        var f = image.Info.Width / (double)sizeDip;
        var bytes = image.GetData().Bytes.Span;
        var i = (int)(y * f) * image.Info.Width * 4 + (int)(x * f) * 4;
        return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
    }

    private static bool Near(int a, int b, int tolerance) => Math.Abs(a - b) <= tolerance;

    // Straight-alpha RGBA PNG (the smoke test has no image encoder on Linux, so a minimal one).
    internal static void WritePng(IImage image, string path)
    {
        int w = image.Info.Width, h = image.Info.Height;
        var src = image.GetData().Bytes.Span;
        var raw = new byte[(w * 4 + 1) * h];
        for (int y = 0; y < h; y++)
        {
            var row = y * (w * 4 + 1);
            for (int x = 0; x < w; x++)
            {
                var s = (y * w + x) * 4;
                int b = src[s], g = src[s + 1], r = src[s + 2], a = src[s + 3];
                int Un(int c) => a == 0 ? 0 : Math.Min(255, c * 255 / a);
                raw[row + 1 + x * 4] = (byte)Un(r);
                raw[row + 2 + x * 4] = (byte)Un(g);
                raw[row + 3 + x * 4] = (byte)Un(b);
                raw[row + 4 + x * 4] = (byte)a;
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true))
            zs.Write(raw);
        using var f = File.Create(path);
        f.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", z.ToArray());
        Chunk(f, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var n = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(n, data.Length);
        s.Write(n);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        s.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (var b in body)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(n, ~crc);
        s.Write(n);
    }

    /// <summary>Group snapshots: result, latency in pumps, error paths, hitch rate.</summary>
    [Test, Category("snapshot")]
    public static void Snapshot() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        // The client QtApp owns, without a QtApp (the smoke test pumps QtNative itself).
        var client = new ResourceClient(new NativeResourceApi(), Environment.CurrentManagedThreadId, static () => null);
        client.SetPumping(true);
        // One pump as QtApp.PumpFrame runs it: releases → processEvents (grabs finish) → status queue.
        void Pump(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
            }
        }

        var w = Native.vlqt_window_create(800, 600, "VL.Qt snapshot test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var scene = new SceneRegistry();
        var rec = new Reconciler(cmd, new IdAllocator(), scene) { Window = w };

        // Group at (100, 100) DIP, 300 x 200 (fixed size): orange rectangle 100 x 100 at its origin, white text right of it.
        var orange = new VL.Qt.Elements.Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Fill = new Color4(1, 0.6f, 0, 1) };
        var text = new Text { Size = new Vector2(1.8f, 0.6f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1.1f, 0.2f, 0), Content = "Snap", FontSize = 0.4f, Color = new Color4(1, 1, 1, 1) };
        var group = new Group { Size = new Vector2(3, 2), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 1, 0), AutoSize = false, Children = [orange, text] };
        var root = new Group { Anchor = RectangleAnchor.TopLeft, Children = [group] };

        void Frame(Element tree, bool clear = false)
        {
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(tree, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
        }

        Frame(root, clear: true);
        Pump(3);

        var node = new SnapshotNode(client, scene);
        var latencies = new List<int>();

        // Grab on the node, pump until Is Ready / Error; returns the image (or null) and the frames it took.
        IImage? Snap(SnapshotNode n, Element? element, float scale, out string error, out int frames)
        {
            var image = n.Update(element, out var ready, out error, scale, true);
            frames = 0;
            while (!ready && error.Length == 0 && frames < 120)
            {
                Pump(1);
                frames++;
                image = n.Update(element, out ready, out error, scale);
            }
            return ready ? image : null;
        }

        // 1) Group with Rectangle + Text: BGRA8 premultiplied, size = group size * Scale, pixels against vlqt_window_grab.
        var img1 = Snap(node, group, 1, out var err1, out var frames1);
        latencies.Add(frames1);
        s.Check(img1 is not null && err1.Length == 0, $"snapshot of the group is ready ('{err1}')");
        if (img1 is null)
        {
            Cleanup();
            return;
        }
        s.Check(img1.Info.Width == 300 && img1.Info.Height == 200 && img1.Info.Format == PixelFormat.B8G8R8A8 && img1.Info.IsPremultipliedAlpha && !img1.IsVolatile,
            $"snapshot is 300 x 200 BGRA8 premultiplied, not volatile ({img1.Info.Width} x {img1.Info.Height}, {img1.Info.Format}, premul {img1.Info.IsPremultipliedAlpha})");
        WritePng(img1, Path.Combine(s.OutDir, "363_snapshot.png"));
        {
            var (b, g, r, a) = At(img1, 50, 50);
            s.Check(a == 255 && Near(r, 255, 2) && Near(g, 153, 2) && Near(b, 0, 2), $"rectangle pixel is orange (B{b} G{g} R{r} A{a})");
            var (_, _, _, ca) = At(img1, 290, 190);
            s.NeedsRhi(ca == 0, $"empty group area is transparent (alpha {ca})", "the software backend renders grabToImage into an opaque pixmap (screen format RGB32), transparent areas come out black, alpha 255; vlqt.h");
            int ink = 0;
            for (int y = 20; y < 80; y++)
                for (int x = 110; x < 290; x++)
                    if (At(img1, x, y).A > 128)
                        ink++;
            s.Check(ink > 50, $"text area is not empty ({ink} opaque pixels)");
        }

        // Against the window grab (device pixels: the group sits at (100, 100) DIP). The snapshot is composited over the
        // background (0x20) first. Tolerance: Qt renders the text with another glyph cache/AA state per target (window
        // vs offscreen layer), so the mean over the group area may differ a little; fill and background areas must match.
        using (var a = s.Grab(w, "363_window.png"))
        {
            double diff = 0;
            int n = 0, rectOk = 0, rectAll = 0;
            for (int y = 0; y < 200; y += 2)
                for (int x = 0; x < 300; x += 2)
                {
                    var px = At(img1, x, y);
                    var g = Px(a, 100 + x, 100 + y);
                    int inv = 255 - px.A;
                    int sr = px.R + inv * 0x20 / 255, sg = px.G + inv * 0x20 / 255, sb = px.B + inv * 0x20 / 255;
                    diff += (Math.Abs(sr - g.R) + Math.Abs(sg - g.G) + Math.Abs(sb - g.B)) / 3.0;
                    n++;
                    if (x < 96 && y < 96)
                    {
                        rectAll++;
                        if (Near(sr, g.R, 6) && Near(sg, g.G, 6) && Near(sb, g.B, 6))
                            rectOk++;
                    }
                }
            var mean = diff / n;
            s.Info($"snapshot vs window grab: mean abs diff {mean:F2} (0..255), rectangle {rectOk}/{rectAll} within 6");
            s.Check(rectOk == rectAll, $"rectangle pixels equal the window grab ({rectOk}/{rectAll}, tolerance 6)");
            s.NeedsRhi(mean < 4, $"snapshot equals the window grab over the group area (mean diff {mean:F2} < 4)", "the software backend renders grabToImage into an opaque pixmap (screen format RGB32), transparent areas come out black, alpha 255; vlqt.h");
            int inkWindow = 0;
            for (int y = 20; y < 80; y++)
                for (int x = 110; x < 290; x++)
                {
                    var g = Px(a, 100 + x, 100 + y);
                    if (g.R > 160 && g.G > 160 && g.B > 160)
                        inkWindow++;
                }
            int inkSnap = 0;
            for (int y = 20; y < 80; y++)
                for (int x = 110; x < 290; x++)
                    if (At(img1, x, y) is { A: > 128, R: > 160 })
                        inkSnap++;
            s.Check(inkSnap > 0.6 * inkWindow && inkSnap < 1.6 * inkWindow, $"text ink equal to the window grab within 40 % ({inkSnap} vs {inkWindow} px)");
        }

        // Scale 2: double the pixels.
        var img2 = Snap(node, group, 2, out var err2, out var frames2);
        latencies.Add(frames2);
        s.Check(img2 is { Info: { Width: 600, Height: 400 } } && err2.Length == 0, $"Scale 2 = 600 x 400 ({img2?.Info.Width} x {img2?.Info.Height}, '{err2}')");
        if (img2 is not null)
        {
            WritePng(img2, Path.Combine(s.OutDir, "363_snapshot_x2.png"));
            var (b, g, r, a) = At(img2, 100, 100, 600);
            s.Check(a == 255 && Near(r, 255, 2) && Near(g, 153, 2) && Near(b, 0, 2), $"Scale 2 rectangle pixel is orange (B{b} G{g} R{r} A{a})");
        }
        s.Check(ReferenceEquals(node.Update(group, out _, out _, 2, false), img2), "Image stays the last finished snapshot");

        // 2) Latency (frames from Grab to Is Ready); repeat a few times for the range.
        for (int i = 0; i < 8; i++)
        {
            Snap(node, group, 1, out _, out var f);
            latencies.Add(f);
        }
        Console.WriteLine($"snapshot latency: {latencies.Max()} frames (min {latencies.Min()}, {latencies.Count} grabs: {string.Join(",", latencies)})");
        s.Check(latencies.Max() <= MaxLatency && latencies.Min() >= 1, $"latency {latencies.Min()}..{latencies.Max()} frames (1..{MaxLatency})");

        // 3) Element in no renderer: error at the node, no exception.
        var loose = new VL.Qt.Elements.Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0) };
        string looseError = "";
        IImage? looseImage = null;
        Exception? thrown = null;
        try
        {
            looseImage = Snap(node, loose, 1, out looseError, out _);
            Snap(node, null, 1, out var nullError, out _);
            s.Check(nullError == SnapshotNode.NotShownError, $"no Element = '{nullError}'");
        }
        catch (Exception e)
        {
            thrown = e;
        }
        s.Check(thrown is null, $"element in no renderer does not throw ({thrown?.Message})");
        s.Check(looseImage is null && looseError == SnapshotNode.NotShownError, $"element in no renderer: Error '{looseError}'");
        // The node recovers with a shown element.
        var again = Snap(node, group, 1, out var errAgain, out _);
        s.Check(again is not null && errAgain.Length == 0, $"grab of a shown element after the error works ('{errAgain}')");

        // 4) Error paths on the raw API (own poll, the client does not pump here).
        var events = new List<(ushort Ev, ulong Id, byte[] Payload)>();
        var buf = new byte[64 * 1024];
        void Poll()
        {
            var n = Native.ResPoll(buf);
            if (n < 0)
            {
                buf = new byte[-n * 2];
                n = Native.ResPoll(buf);
            }
            for (int at = 0; at < n;)
            {
                var ev = BitConverter.ToUInt16(buf, at);
                var len = BitConverter.ToUInt16(buf, at + 2);
                events.Add((ev, BitConverter.ToUInt64(buf, at + 4), buf.AsSpan(at + 12, len - 8).ToArray()));
                at += (4 + len + 3) & ~3;
            }
        }
        void RawFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                s.Pump(1);
                Poll();
            }
        }
        ulong RawCreate()
        {
            var rc = Native.vlqt_res_create(Protocol.ResKind.Snapshot, out var id);
            s.Check(rc == 0 && id != 0, $"raw create ({rc})");
            return id;
        }
        // Pumps until the id has Ready/Failed after `from` events (or 120 frames).
        byte Settle(ulong id, int from)
        {
            for (int i = 0; i < 120; i++)
            {
                RawFrames(1);
                var s = events.Skip(from).Where(e => e.Id == id && e.Ev == Protocol.ResEv.State).Select(e => e.Payload[0]).LastOrDefault();
                if (s is Protocol.ResState.Ready or Protocol.ResState.Failed)
                    return s;
            }
            return 0;
        }

        // 4a) Release while the grab is in flight: no crash, no record afterwards, id stale.
        RawFrames(1); // empty queue
        events.Clear();
        var rel = RawCreate();
        var rc4 = Native.vlqt_res_snapshot(rel, w, 0, 1);
        s.Check(rc4 == 0, $"raw snapshot of the window root ({rc4})");
        Native.vlqt_res_release(rel);
        events.Clear(); // what came before the release call is not interesting, only what comes after
        RawFrames(10);
        s.Check(!events.Any(e => e.Id == rel), $"release during the grab: no record afterwards ({events.Count(e => e.Id == rel)})");
        s.Check(Native.vlqt_res_wait(rel, 0) == -9, "released snapshot id is stale");
        var viaNode = new SnapshotNode(client, scene);
        viaNode.Update(group, out _, out _, 1, true);
        var liveBefore = client.LiveCount;
        viaNode.Dispose();
        Pump(6);
        s.Check(client.PendingReleases == 0 && client.LiveCount == liveBefore - 1, $"node disposed during the grab: released, nothing left (live {client.LiveCount})");

        // 4b) Window destroyed during the grab: Failed (or Ready if the grab was done first), never a hang; afterwards a
        // new grab on the same id works.
        var w2 = Native.vlqt_window_create(320, 200, "VL.Qt snapshot test 2");
        Native.vlqt_window_show(w2);
        cmd.Reset();
        cmd.ClearColor(0.2f, 0.2f, 0.2f, 1);
        cmd.Create(1, Protocol.Type.Rectangle);
        cmd.SetPlace(1, 10, 10, 100, 100);
        cmd.SetColor(1, Protocol.Prop.Color, 1, 0, 0, 1);
        cmd.Reparent(1, 0, 0);
        Native.Apply(w2, cmd.EndFrame());
        RawFrames(3);
        events.Clear();
        var id2 = RawCreate();
        var rcA = Native.vlqt_res_snapshot(id2, w2, 1, 1);
        Native.vlqt_window_destroy(w2);
        var from = events.Count;
        var state = Settle(id2, from);
        s.Check(rcA == 0 && state is Protocol.ResState.Failed or Protocol.ResState.Ready, $"window destroyed during the grab settles ({rcA}, state {state})");
        s.Info($"window destroyed during the grab: state {state} (4 = Ready, 5 = Failed)");
        if (state == Protocol.ResState.Failed)
            s.Check(events.Any(e => e.Id == id2 && e.Ev == Protocol.ResEv.Error && e.Payload.Length > 0), "Failed carries an error text");
        from = events.Count;
        var rcB = Native.vlqt_res_snapshot(id2, w, 0, 1);
        s.Check(rcB == 0, $"new grab on the id after the window died ({rcB})");
        var state2 = Settle(id2, from);
        s.Check(state2 == Protocol.ResState.Ready, $"new grab on a window that lives: Ready (state {state2})");
        // And after a Failed one: unknown element → Failed, the next one starts over.
        from = events.Count;
        Native.vlqt_res_snapshot(id2, w, 4_000_000, 1);
        var state3 = Settle(id2, from);
        from = events.Count;
        Native.vlqt_res_snapshot(id2, w, 0, 1);
        var state4 = Settle(id2, from);
        s.Check(state3 == Protocol.ResState.Failed && state4 == Protocol.ResState.Ready, $"unknown element fails, the grab after the Failed works (states {state3}, {state4})");
        Native.vlqt_res_release(id2);
        RawFrames(2);

        // 5) Hitch rate: paced to 60 Hz, a moving rectangle; 300 frames without snapshots, then 300 with a snapshot of
        // the group every 10th frame. Hitch = interval > 1.5 x median of the run without snapshots.
        if (PerfLimits.SkipHeavy)
            s.Skip("perf run snapshot hitches: heavy perf measurement skipped on shared CI runners");
        else
            RunPerf();

        void RunPerf()
        {
            const double Period = 1000.0 / 60;
            var perfNode = new SnapshotNode(client, scene);
            var intervals = new List<double>();
            var sw = Stopwatch.StartNew();
            double last = 0, next = sw.Elapsed.TotalMilliseconds;
            int done = 0;
            void PerfFrame(int i, bool snapshots)
            {
                next += Period;
                if (sw.Elapsed.TotalMilliseconds > next) next = sw.Elapsed.TotalMilliseconds; // behind: no catch-up burst
                while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50);
                var now = sw.Elapsed.TotalMilliseconds;
                intervals.Add(now - last);
                last = now;
                var m = new VL.Qt.Elements.Rectangle { Size = new Vector2(0.5f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(5 + i % 200 * 0.01f, 5, 0), Fill = new Color4(0, 1, 0, 1) };
                Frame(new Group { Anchor = RectangleAnchor.TopLeft, Children = [group, m] });
                if (snapshots)
                {
                    perfNode.Update(group, out var ready, out _, 1, i % 10 == 0);
                    if (ready) done++;
                }
                Pump(1);
            }
            for (int i = 0; i < 60; i++) PerfFrame(i, false); // warm-up
            intervals.Clear();
            for (int i = 0; i < 300; i++) PerfFrame(i, false);
            var background = intervals.Skip(1).ToList();
            var median = background.OrderBy(x => x).ElementAt(background.Count / 2);
            intervals.Clear();
            for (int i = 0; i < 300; i++) PerfFrame(i, true);
            Pump(10);
            var snapshots = intervals.Skip(1).ToList();
            var limit = median * 1.5;
            int Hitches(List<double> l) => l.Count(x => x > limit);
            double Rate(List<double> l) => l.Count == 0 ? 0 : 100.0 * Hitches(l) / l.Count;
            s.Info($"perf: without snapshots {Hitches(background)}/{background.Count} hitches ({Rate(background):F1} %), "
                + $"snapshot every 10th frame {Hitches(snapshots)}/{snapshots.Count} ({Rate(snapshots):F1} %), median {median:F2} ms, max {snapshots.Max():F1} ms, {done} snapshots delivered");
            s.Check(done >= 25, $"snapshots delivered while running ({done} of 30)");
            PerfLimits.Below(s, Rate(snapshots), Math.Max(Rate(background) * 3, 5), "hitch rate with a snapshot every 10th frame in the range of the background, %");
            perfNode.Dispose();
        }

        // 6) F5 Resources page shows the snapshot cost: a burst of snapshots gives a rate and a number for last / max,
        // a second without snapshots brings the rate back to 0/s.
        {
            string SnapLine() => PerfOverlayTest.PageText(w, PerfOverlayTest.ResourcesPage).Split('\n').FirstOrDefault(l => l.StartsWith("snapshots"))?.Trim() ?? "(missing)";
            s.Check(SnapLine().StartsWith("snapshots "), $"Resources page has a snapshots line ('{SnapLine()}')");
            for (int i = 0; i < 4; i++)
            {
                var sid = RawCreate();
                var from6 = events.Count;
                Native.vlqt_res_snapshot(sid, w, 0, 1);
                s.Check(Settle(sid, from6) == Protocol.ResState.Ready, $"burst snapshot {i} is Ready");
                Native.vlqt_res_release(sid);
            }
            var burst = SnapLine();
            var m = System.Text.RegularExpressions.Regex.Match(burst, @"^snapshots (\d+)/s last ([0-9.]+) ms max ([0-9.]+) ms$");
            s.Check(m.Success && int.Parse(m.Groups[1].Value) >= 4 && double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) > 0,
                $"after a burst the line shows a rate and numbers ('{burst}')");
            for (var t = Stopwatch.StartNew(); t.Elapsed.TotalSeconds < 1.3; ) { RawFrames(1); Thread.Sleep(15); }
            var idle = SnapLine();
            s.Check(System.Text.RegularExpressions.Regex.IsMatch(idle, @"^snapshots 0/s "), $"a second later the rate is 0/s ('{idle}')");
        }

        Cleanup();

        void Cleanup()
        {
            node.Dispose();
            Pump(3);
            s.Check(client.LiveCount == 0, $"all snapshot resources released (live {client.LiveCount})");
            cmd.Reset();
            rec.Clear();
            Native.Apply(w, cmd.EndFrame());
            Native.vlqt_window_destroy(w);
            client.SetPumping(false);
            s.Pump(2);
        }
    }
}
