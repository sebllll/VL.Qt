#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// (images package 10): the shared D3D11 device is removed for real (D3D11On12 + RemoveDevice, needs
/// VLQT_TEST_D3D11ON12=1 before the first window, so it runs in a helper process of its own). QtNative rebuilds device,
/// upload QRhi, windows and resources: both windows end up on the new device, same as the upload QRhi, and draw their
/// rect and 20 images again (10 PNG files + 10 via set_pixels, both windows show all of them). Twice in a row.
/// After each loss: vlqt_res_list has the same resources, all Ready, same bytes (no leaks), the Ready snapshot has
/// unchanged pixels. INFO lines carry the time until everything is visible again (for the docs).
/// </summary>
[TestFixture, NonParallelizable]
public static class DeviceLostTest
{
    private const uint Rect = 1;
    private const int TimeoutMs = 5000;
    private const int Images = 20;

    /// <summary>Real removal of the shared D3D11 device, twice: windows, images and snapshot come back (own process).</summary>
    [Test, Category("device-lost"), Category("own-process")]
    public static void DeviceLost() => Scenario.RunLocal(s =>
    {
        var env = new Dictionary<string, string> { ["VLQT_TEST_D3D11ON12"] = "1" };
        // CI (GitHub sets CI=true) has no GPU: the device is removed on the software adapter (WARP), only in this process.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            env["QSG_RHI_PREFER_SOFTWARE_RENDERER"] = "1";
        OwnProcess.Run(s, new[] { "device-lost" }, env);
    });

    // Body, run by the helper executable (OwnProcess).
    internal static void Run(Scenario s)
    {
        if (!OperatingSystem.IsWindows() || s.QuickSoftware)
        {
            s.Skip("device-lost: D3D11 only");
            return;
        }
        var cmd = new CommandWriter();
        var dir = Path.Combine(s.OutDir, "device-lost");
        Directory.CreateDirectory(dir);

        // 20 distinct colors; tile i sits in a 5 x 4 grid from y = 100 of a 400 x 400 window.
        var colors = Enumerable.Range(0, Images).Select(i => Color.FromArgb(60 + i * 9, 220 - i * 8, (i * 53) % 200 + 40)).ToArray();
        var ids = new List<ulong>();
        for (int i = 0; i < Images; i++)
        {
            if (Native.vlqt_res_create(Protocol.ResourceKind.Image, out var id) != 0)
            {
                s.Check(false, $"res_create {i}");
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
                Native.vlqt_image_load(id, path, 0);
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
                Native.ImageSetPixels(id, new Native.vlqt_pixel_desc { width = 64, height = 48, format = 7, premultiplied = 1 }, d);
            }
        }
        foreach (var id in ids)
            s.Check(Native.vlqt_res_wait(id, 10000) == Protocol.ResourceState.Ready, $"image {id} ready before the loss");

        SceneHandle Open(string title, float x)
        {
            var w = Native.vlqt_window_create(x, 100, 400, 400, title, 0);
            s.Check(w != SceneHandle.Null, $"create {title} " + (w == SceneHandle.Null ? Native.LastError() : ""));
            cmd.Reset();
            cmd.ClearColor(0.1f, 0.1f, 0.1f, 1);
            cmd.Create(Rect, Protocol.ElementType.Rectangle);
            cmd.SetPlace(Rect, 20, 20, 100, 60);
            cmd.SetColor(Rect, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
            cmd.Reparent(Rect, 0, 0);
            for (int i = 0; i < Images; i++)
            {
                var el = (uint)(10 + i);
                cmd.Create(el, Protocol.ElementType.Image);
                cmd.SetPlace(el, 10 + i % 5 * 76, 100 + i / 5 * 72, 72, 68);
                cmd.SetU64(el, Protocol.PropertyId.Image, ids[i]);
                cmd.Reparent(el, 0, (uint)(1000 + i));
            }
            s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"apply {title}");
            Native.vlqt_window_show(w);
            return w;
        }

        IntPtr Device(SceneHandle w) => NativeTest.vlqt_test_device_info(w, out var dev, out _) == 0 ? dev : IntPtr.Zero;

        // Grab succeeds and shows the orange rect (logical 20,20,100,60) on the dark background and every image tile
        // in its color.
        int badTiles = 0;
        bool Shows(SceneHandle w, string path)
        {
            badTiles = -1;
            if (NativeTest.vlqt_test_window_grab(w, path) != 0)
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
            var n = NativeCalls.ResPoll(resBuf, out var required);
            if (n == (int)Native.Result.BufferTooSmall)
            {
                resBuf = new byte[required * 2];
                n = NativeCalls.ResPoll(resBuf);
            }
            for (int at = 0; at < n;)
            {
                var len = (int)BitConverter.ToUInt32(resBuf, at + 4);
                if (BitConverter.ToUInt16(resBuf, at) == Protocol.ResourceEventId.Info)
                {
                    var id = BitConverter.ToUInt64(resBuf, at + 8);
                    infos[id] = infos.GetValueOrDefault(id) + 1;
                }
                at += (8 + len + 3) & ~3;
            }
        }
        void Pump()
        {
            s.Pump(1);
            Poll();
        }

        bool Settled(SceneHandle w, string path)
        {
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < TimeoutMs)
            {
                if (Shows(w, path))
                    return true;
                s.Pump(1);
            }
            return false;
        }

        var a = Open("VL.Qt device lost A", 50);
        var b = Open("VL.Qt device lost B", 500);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < TimeoutMs && (Device(a) == IntPtr.Zero || Device(b) == IntPtr.Zero))
            s.Pump(1);
        var dev = Device(a);
        s.Check(dev != IntPtr.Zero && dev == Device(b), $"both windows on the shared device ({dev:X})");
        s.Check(Settled(a, Path.Combine(dir, "before_a.png")), $"rect + 20 images visible before the loss in A (bad tiles {badTiles})");
        s.Check(Settled(b, Path.Combine(dir, "before_b.png")), $"rect + 20 images visible before the loss in B (bad tiles {badTiles})");

        // Ready snapshot of A and the resource table before the loss.
        Native.vlqt_res_create(Protocol.ResourceKind.Snapshot, out var snap);
        s.Check(Native.vlqt_snapshot_start(snap, a, 0, 1) == 0, "snapshot started");
        var snapSw = Stopwatch.StartNew();
        while (snapSw.ElapsedMilliseconds < TimeoutMs && SnapState() != Protocol.ResourceState.Ready)
            s.Pump(1);
        s.Check(SnapState() == Protocol.ResourceState.Ready, "snapshot Ready before the loss");
        int SnapState()
        {
            var l = new Native.vlqt_res_info[128];
            var n = Native.ResList(l);
            return l.Take(n).Where(r => r.id == snap).Select(r => r.state).FirstOrDefault();
        }
        byte[] ReadSnap(out Native.vlqt_pixel_desc desc)
        {
            var rc = NativeCalls.ReadSnapshot(snap, Span<byte>.Empty, out desc, out var required);
            if (rc != (int)Native.Result.BufferTooSmall)
                return [];
            var px = new byte[required];
            return NativeCalls.ReadSnapshot(snap, px, out desc) == px.Length ? px : [];
        }
        (int count, int notReady, long bytes) Table()
        {
            var list = new Native.vlqt_res_info[128];
            var n = Native.ResList(list);
            var ours = list.Take(n).Where(r => r.id == snap || ids.Contains(r.id)).ToArray();
            return (n, ours.Count(r => r.state != Protocol.ResourceState.Ready) + (Images + 1 - ours.Length), ours.Sum(r => r.bytes));
        }
        var snapBefore = ReadSnap(out var snapDesc);
        s.Check(snapBefore.Length > 0 && snapDesc.width > 0, $"snapshot pixels before the loss ({snapBefore.Length} bytes)");
        var before = Table();
        s.Check(before.count == Images + 1 && before.notReady == 0, $"{Images + 1} resources, all Ready before the loss ({before.count}, not ready {before.notReady})");
        s.Info($"before: {before.count} resources, {before.bytes} bytes");

        for (int round = 1; round <= 2; round++)
        {
            var rc = NativeTest.vlqt_test_device_lost(TestProtocol.DeviceLost.Remove);
            s.Check(rc == 0, $"round {round}: vlqt_test_device_lost(REMOVE) = {rc} (needs VLQT_TEST_D3D11ON12=1)");
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
                NativeTest.vlqt_test_device_info(SceneHandle.Null, out upload, out _);
                da = Device(a);
                db = Device(b);
                if (Moved())
                    break;
            }
            var moved = sw.Elapsed.TotalMilliseconds;
            s.Check(Moved(), $"round {round}: new device {upload:X} (old {dev:X}{(upload == dev ? ", address reused" : "")}) in upload QRhi and both windows ({da:X}, {db:X})");
            bool visible = false;
            while (sw.ElapsedMilliseconds < TimeoutMs && !visible)
            {
                visible = Shows(a, Path.Combine(dir, $"after{round}_a.png")) && Shows(b, Path.Combine(dir, $"after{round}_b.png"));
                if (!visible)
                    Pump();
            }
            var visibleMs = sw.Elapsed.TotalMilliseconds;
            s.Check(visible, $"round {round}: rect + 20 images visible again in both windows (bad tiles {badTiles}) " + (visible ? "" : Native.LastError()));
            NativeTest.vlqt_test_window_grab(a, Path.Combine(dir, $"after{round}_a.png"));
            NativeTest.vlqt_test_window_grab(b, Path.Combine(dir, $"after{round}_b.png"));

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
            s.Check(replaced == Images, $"round {round}: every image resource got a replacement INFO record ({replaced} of {Images})");
            s.Check(after.count == before.count, $"round {round}: vlqt_res_list count {after.count} (before {before.count})");
            s.Check(after.notReady == 0, $"round {round}: all resources Ready again (not ready/missing {after.notReady})");
            s.Check(after.bytes == before.bytes, $"round {round}: resource bytes {after.bytes} (before {before.bytes})");
            var snapAfter = ReadSnap(out var desc2);
            s.Check(snapAfter.AsSpan().SequenceEqual(snapBefore) && desc2.width == snapDesc.width && desc2.height == snapDesc.height,
                $"round {round}: snapshot pixels unchanged ({snapAfter.Length} bytes)");
            s.Info($"round {round}: windows on the new device after {moved:F1} ms ({pumps} pumps), rect + images visible after {visibleMs:F1} ms, resources Ready after {sw.Elapsed.TotalMilliseconds:F1} ms");
            dev = upload;
        }
        Native.vlqt_res_release(snap);
        foreach (var id in ids)
            Native.vlqt_res_release(id);
        Native.vlqt_scene_destroy(a);
        Native.vlqt_scene_destroy(b);
        s.Pump(2);
    }
}
#endif
