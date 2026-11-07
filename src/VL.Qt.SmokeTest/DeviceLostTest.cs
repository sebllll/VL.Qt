#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using VL.Qt;

/// <summary>
/// #367 (images package 10): the shared D3D11 device is removed for real (D3D11On12 + RemoveDevice, needs
/// VLQT_TEST_D3D11ON12=1 before the first window, set by Program for "--only device-lost"). QtNative rebuilds device,
/// upload QRhi, windows and resources: both windows end up on the new device, same as the upload QRhi, and draw their
/// rect and 20 images again (10 PNG files + 10 via set_pixels, both windows show all of them). Twice in a row.
/// After each loss: vlqt_res_list has the same resources, all Ready, same bytes (no leaks), the Ready snapshot has
/// unchanged pixels. INFO lines carry the time until everything is visible again (for the docs).
/// </summary>
static class DeviceLostTest
{
    const uint Rect = 1;
    const int TimeoutMs = 5000;
    const int Images = 20;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump)
    {
        if (!OperatingSystem.IsWindows() || Program.QuickSoftware)
        {
            Console.WriteLine("SKIP device-lost: D3D11 only");
            return;
        }
        var cmd = new CommandWriter();
        var dir = Path.Combine(outDir, "device-lost");
        Directory.CreateDirectory(dir);

        // 20 distinct colors; tile i sits in a 5 x 4 grid from y = 100 of a 400 x 400 window.
        var colors = Enumerable.Range(0, Images).Select(i => Color.FromArgb(60 + i * 9, 220 - i * 8, (i * 53) % 200 + 40)).ToArray();
        var ids = new List<ulong>();
        for (int i = 0; i < Images; i++)
        {
            if (Native.vlqt_res_create(Protocol.ResKind.Image, out var id) != 0)
            {
                check(false, $"367: res_create {i}");
                return;
            }
            ids.Add(id);
            if (i < 10)
            {
                var path = Path.Combine(dir, $"tile{i}.png");
                using (var bmp = new Bitmap(64, 48))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.Clear(colors[i]);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Native.vlqt_res_load_image(id, path, 0);
            }
            else
            {
                var d = new byte[64 * 48 * 4];
                for (int p = 0; p < d.Length; p += 4)
                {
                    d[p] = colors[i].R;
                    d[p + 1] = colors[i].G;
                    d[p + 2] = colors[i].B;
                    d[p + 3] = 255;
                }
                Native.ResSetPixels(id, new Native.vlqt_pixel_desc { width = 64, height = 48, format = 7, premultiplied = 1 }, d);
            }
        }
        foreach (var id in ids)
            check(Native.vlqt_res_wait(id, 10000) == Protocol.ResState.Ready, $"367: image {id} ready before the loss");

        IntPtr Open(string title, float x)
        {
            var w = Native.vlqt_window_create_at(x, 100, 400, 400, title, 0);
            check(w != IntPtr.Zero, $"367: create {title} " + (w == IntPtr.Zero ? Native.LastError() : ""));
            cmd.Reset();
            cmd.ClearColor(0.1f, 0.1f, 0.1f, 1);
            cmd.Create(Rect, Protocol.Type.Rectangle);
            cmd.SetPlace(Rect, 20, 20, 100, 60);
            cmd.SetColor(Rect, Protocol.Prop.Color, 1, 0.6f, 0, 1);
            cmd.Reparent(Rect, 0, 0);
            for (int i = 0; i < Images; i++)
            {
                var el = (uint)(10 + i);
                cmd.Create(el, Protocol.Type.Image);
                cmd.SetPlace(el, 10 + i % 5 * 76, 100 + i / 5 * 72, 72, 68);
                cmd.SetU64(el, Protocol.Prop.Image, ids[i]);
                cmd.Reparent(el, 0, (uint)(1000 + i));
            }
            check(Native.Apply(w, cmd.EndFrame()) == 0, $"367: apply {title}");
            Native.vlqt_window_show(w);
            return w;
        }

        IntPtr Device(IntPtr w) => Native.vlqt_test_device_info(w, out var dev, out _) == 0 ? dev : IntPtr.Zero;

        // Grab succeeds and shows the orange rect (logical 20,20,100,60) on the dark background and every image tile
        // in its color.
        int badTiles = 0;
        bool Shows(IntPtr w, string path)
        {
            badTiles = -1;
            if (Native.vlqt_window_grab(w, path) != 0)
                return false;
            using var b = new Bitmap(path);
            var c = b.GetPixel(70 * b.Width / 400, 50 * b.Height / 400);
            var bg = b.GetPixel(300 * b.Width / 400, 50 * b.Height / 400);
            badTiles = 0;
            for (int i = 0; i < Images; i++)
            {
                var t = b.GetPixel((10 + i % 5 * 76 + 36) * b.Width / 400, (100 + i / 5 * 72 + 34) * b.Height / 400);
                var e = colors[i];
                if (Math.Abs(t.R - e.R) > 10 || Math.Abs(t.G - e.G) > 10 || Math.Abs(t.B - e.B) > 10)
                    badTiles++;
            }
            return Math.Abs(c.R - 255) < 8 && Math.Abs(c.G - 153) < 8 && c.B < 8 && bg.R < 40 && bg.G < 40 && badTiles == 0;
        }

        // Status queue: after a loss every image resource must announce its replacement (INFO record) - without the
        // re-upload the old texture may still be shown (Adreno / D3D11On12), so the pixels alone prove nothing.
        var infos = new Dictionary<ulong, int>();
        var resBuf = new byte[64 * 1024];
        void Poll()
        {
            var n = Native.ResPoll(resBuf);
            if (n < 0)
            {
                resBuf = new byte[-n * 2];
                n = Native.ResPoll(resBuf);
            }
            for (int at = 0; at < n;)
            {
                var len = BitConverter.ToUInt16(resBuf, at + 2);
                if (BitConverter.ToUInt16(resBuf, at) == Protocol.ResEv.Info)
                {
                    var id = BitConverter.ToUInt64(resBuf, at + 4);
                    infos[id] = infos.GetValueOrDefault(id) + 1;
                }
                at += (4 + len + 3) & ~3;
            }
        }
        void Pump()
        {
            pump(1);
            Poll();
        }

        bool Settled(IntPtr w, string path)
        {
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < TimeoutMs)
            {
                if (Shows(w, path))
                    return true;
                pump(1);
            }
            return false;
        }

        var a = Open("VL.Qt device lost A", 50);
        var b = Open("VL.Qt device lost B", 500);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < TimeoutMs && (Device(a) == IntPtr.Zero || Device(b) == IntPtr.Zero))
            pump(1);
        var dev = Device(a);
        check(dev != IntPtr.Zero && dev == Device(b), $"367: both windows on the shared device ({dev:X})");
        check(Settled(a, Path.Combine(dir, "before_a.png")), $"367: rect + 20 images visible before the loss in A (bad tiles {badTiles})");
        check(Settled(b, Path.Combine(dir, "before_b.png")), $"367: rect + 20 images visible before the loss in B (bad tiles {badTiles})");

        // Ready snapshot of A and the resource table before the loss.
        Native.vlqt_res_create(Protocol.ResKind.Snapshot, out var snap);
        check(Native.vlqt_res_snapshot(snap, a, 0, 1) == 0, "367: snapshot started");
        var snapSw = Stopwatch.StartNew();
        while (snapSw.ElapsedMilliseconds < TimeoutMs && SnapState() != Protocol.ResState.Ready)
            pump(1);
        check(SnapState() == Protocol.ResState.Ready, "367: snapshot Ready before the loss");
        int SnapState()
        {
            var l = new Native.vlqt_res_info[128];
            var n = Native.ResList(l);
            return l.Take(n).Where(r => r.id == snap).Select(r => r.state).FirstOrDefault();
        }
        byte[] ReadSnap(out Native.vlqt_pixel_desc desc)
        {
            var n = Native.ResReadSnapshot(snap, Span<byte>.Empty, out desc);
            if (n >= 0)
                return [];
            var px = new byte[-n];
            return Native.ResReadSnapshot(snap, px, out desc) == px.Length ? px : [];
        }
        (int count, int notReady, long bytes) Table()
        {
            var list = new Native.vlqt_res_info[128];
            var n = Native.ResList(list);
            var ours = list.Take(n).Where(r => r.id == snap || ids.Contains(r.id)).ToArray();
            return (n, ours.Count(r => r.state != Protocol.ResState.Ready) + (Images + 1 - ours.Length), ours.Sum(r => r.bytes));
        }
        var snapBefore = ReadSnap(out var snapDesc);
        check(snapBefore.Length > 0 && snapDesc.width > 0, $"367: snapshot pixels before the loss ({snapBefore.Length} bytes)");
        var before = Table();
        check(before.count == Images + 1 && before.notReady == 0, $"367: {Images + 1} resources, all Ready before the loss ({before.count}, not ready {before.notReady})");
        Console.WriteLine($"INFO 367 before: {before.count} resources, {before.bytes} bytes");

        for (int round = 1; round <= 2; round++)
        {
            var rc = Native.vlqt_test_device_lost(Protocol.DeviceLost.Remove);
            check(rc == 0, $"367: round {round}: vlqt_test_device_lost(REMOVE) = {rc} (needs VLQT_TEST_D3D11ON12=1)");
            if (rc != 0)
                return;
            sw.Restart();
            Poll();
            infos.Clear();
            IntPtr upload = IntPtr.Zero, da = IntPtr.Zero, db = IntPtr.Zero;
            int pumps = 0;
            // The new device may get the old one's address: without ID3D12DeviceFactory (CI, Windows Server 2022) the
            // old device is released before the new one is created. Then the INFO records of the rebuild tell instead.
            bool Moved() => upload != IntPtr.Zero && (upload != dev || infos.Count > 0) && da == upload && db == upload;
            while (sw.ElapsedMilliseconds < TimeoutMs)
            {
                Pump();
                pumps++;
                Native.vlqt_test_device_info(IntPtr.Zero, out upload, out _);
                da = Device(a);
                db = Device(b);
                if (Moved())
                    break;
            }
            var moved = sw.Elapsed.TotalMilliseconds;
            check(Moved(), $"367: round {round}: new device {upload:X} (old {dev:X}{(upload == dev ? ", address reused" : "")}) in upload QRhi and both windows ({da:X}, {db:X})");
            bool visible = false;
            while (sw.ElapsedMilliseconds < TimeoutMs && !visible)
            {
                visible = Shows(a, Path.Combine(dir, $"after{round}_a.png")) && Shows(b, Path.Combine(dir, $"after{round}_b.png"));
                if (!visible)
                    Pump();
            }
            var visibleMs = sw.Elapsed.TotalMilliseconds;
            check(visible, $"367: round {round}: rect + 20 images visible again in both windows (bad tiles {badTiles}) " + (visible ? "" : Native.LastError()));
            Native.vlqt_window_grab(a, Path.Combine(dir, $"after{round}_a.png"));
            Native.vlqt_window_grab(b, Path.Combine(dir, $"after{round}_b.png"));

            // Resources: same count, all Ready, same bytes (no leak of old textures), snapshot pixels unchanged.
            var after = Table();
            while (sw.ElapsedMilliseconds < TimeoutMs && (after.notReady != 0 || after.count != before.count))
            {
                Pump();
                after = Table();
            }
            for (int i = 0; i < 5 && ids.Any(id => infos.GetValueOrDefault(id) == 0); i++)
                Pump();
            var replaced = ids.Count(id => infos.GetValueOrDefault(id) >= 1);
            check(replaced == Images, $"367: round {round}: every image resource got a replacement INFO record ({replaced} of {Images})");
            check(after.count == before.count, $"367: round {round}: vlqt_res_list count {after.count} (before {before.count})");
            check(after.notReady == 0, $"367: round {round}: all resources Ready again (not ready/missing {after.notReady})");
            check(after.bytes == before.bytes, $"367: round {round}: resource bytes {after.bytes} (before {before.bytes})");
            var snapAfter = ReadSnap(out var desc2);
            check(snapAfter.AsSpan().SequenceEqual(snapBefore) && desc2.width == snapDesc.width && desc2.height == snapDesc.height,
                $"367: round {round}: snapshot pixels unchanged ({snapAfter.Length} bytes)");
            Console.WriteLine($"INFO 367 round {round}: windows on the new device after {moved:F1} ms ({pumps} pumps), rect + images visible after {visibleMs:F1} ms, resources Ready after {sw.Elapsed.TotalMilliseconds:F1} ms");
            dev = upload;
        }
        Native.vlqt_res_release(snap);
        foreach (var id in ids)
            Native.vlqt_res_release(id);
        Native.vlqt_window_destroy(a);
        Native.vlqt_window_destroy(b);
        pump(2);
    }
}
#endif
