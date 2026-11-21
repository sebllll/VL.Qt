using System;
using NUnit.Framework;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using VlPath = VL.Lib.IO.Path;
using static Pixels;

// QtImage end to end: LoadImage / LoadImage (Async) / ToQtImage nodes → ResourceClient → QtNative, one QtImage in
// 100 Image nodes through the Reconciler, release queue. Grabs go to <OutDir>/362_*.png.
[TestFixture, NonParallelizable]
public static class QtImageTest
{

    private sealed class SolidImage : IImage, IImageData
    {
        private readonly byte[] _bytes;

        public SolidImage(int width, int height, byte b, byte g, byte r)
        {
            Info = new ImageInfo(width, height, PixelFormat.B8G8R8A8);
            _bytes = new byte[width * height * 4];
            for (int i = 0; i < _bytes.Length; i += 4)
            {
                _bytes[i] = b;
                _bytes[i + 1] = g;
                _bytes[i + 2] = r;
                _bytes[i + 3] = 255;
            }
        }

        public ImageInfo Info { get; }
        public bool IsVolatile => false;
        public IImageData GetData() => this;
        public ReadOnlyMemory<byte> Bytes => _bytes;
        public int ScanSize => Info.Width * 4;
        public void Dispose() { }
    }

    /// <summary>QtImage.</summary>
    [Test, Category("qtimage")]
    public static void QtImage() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var asset = ImageTest.FindAsset();
        s.Check(asset != null, "test asset found");
        if (asset == null)
            return;

        // The client QtApp owns, without a QtApp (the smoke test pumps QtNative itself).
        var client = new ResourceClient(new NativeResourceApi(), Environment.CurrentManagedThreadId, static () => null);
        client.SetPumping(true);
        // One pump as QtApp.PumpFrame runs it: releases → processEvents (decode results, uploads) → status queue.
        void Pump(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
            }
        }

        // 1) LoadImage: synchronous, Ready with its size after the first Update.
        var load = new LoadImageNode(client);
        var image = load.Update(new VlPath(asset), false, out var error);
        s.Check(image is { State: QtImageState.Ready, Width: 30, Height: 10 } && error.Length == 0,
            $"LoadImage ready at once ({image}, error '{error}')");
        var missing = new LoadImageNode(client);
        var failed = missing.Update(new VlPath(Path.Combine(s.OutDir, "missing_362.png")), false, out var missingError);
        s.Check(failed is { State: QtImageState.Failed } && missingError.Length > 0, $"LoadImage missing file fails ('{missingError}')");

        // 2) One QtImage in 100 Image nodes (10 x 10 cells of 0.6 x 0.2 units) through the Reconciler.
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt QtImage test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var nodes = Enumerable.Range(0, 100).Select(_ => new ImageNode(_ => throw new InvalidOperationException("unexpected warning"))).ToArray();
        var toQt = new ToQtImageNode(client);

        void Frame(QtImage? shared, QtImage? extra, bool clear = false)
        {
            var children = ImmutableArray.CreateBuilder<Element>(101);
            for (int i = 0; i < nodes.Length; i++)
                children.Add(nodes[i].Update(new Vector2(0.6f, 0.2f), Matrix.Translation(i % 10 * 0.7f, i / 10 * 0.3f, 0), RectangleAnchor.TopLeft,
                    shared, ImageFillMode.Stretch, false, 1, false, false, null, null, out _, out _));
            children.Add(new VL.Qt.Elements.Image { Size = new Vector2(1, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 3.2f, 0), Source = extra, Smooth = false });
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(new Group { Anchor = RectangleAnchor.TopLeft, Children = children.MoveToImmutable() }, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
        }

        // 3) ToQtImage: solid blue BGRA, upload within the budget of the next pump.
        var blue = toQt.Update(new SolidImage(16, 16, 255, 0, 0), out var toQtError);
        Frame(image, blue, clear: true);
        Pump(3);
        s.Check(blue is { State: QtImageState.Ready, Width: 16, Height: 16 } && toQtError.Length == 0, $"ToQtImage ready ({blue}, '{toQtError}')");
        using (var a = s.Grab(w, "362_qtimage.png"))
        {
            // Cell (i % 10, i / 10) at x = 70 * col, y = 30 * row DIP, 60 x 20: stripes red | green | blue (20 DIP each).
            var cells = 0;
            for (int i = 0; i < 100; i++)
            {
                int x = i % 10 * 70, y = i / 10 * 30;
                if (ImageTest.IsRed(Px(a, x + 10, y + 10)) && ImageTest.IsGreen(Px(a, x + 30, y + 10)) && ImageTest.IsBlue(Px(a, x + 50, y + 10)))
                    cells++;
            }
            s.Check(cells == 100, $"one QtImage drawn by 100 Image nodes ({cells}/100 cells show the stripes)");
            s.Check(ImageTest.IsBlue(Px(a, 50, 370)), $"ToQtImage pixels drawn ({Px(a, 50, 370)})");
        }

        // 4) ToQtImage with a new instance: new QtImage, same resource, content replaced (red).
        var red = toQt.Update(new SolidImage(16, 16, 0, 0, 255), out _);
        s.Check(red != blue && red?.Id == blue?.Id, "ToQtImage new input = new QtImage on the same resource");
        Frame(image, red);
        Pump(3);
        using (var a = s.Grab(w, "362_qtimage_replaced.png"))
            s.Check(ImageTest.IsRed(Px(a, 50, 370)), $"ToQtImage replaced content drawn ({Px(a, 50, 370)})");

        // 5) LoadImage (Async): Ready after a few pumps, Is Loaded, On Loaded fired once.
        var async = new LoadImageAsyncNode(client);
        QtImage? fired = null;
        var loadedCount = 0;
        var first = async.Update(new VlPath(asset), false, true, out var isLoaded, out _, out var onLoaded);
        using var subscription = onLoaded.Subscribe(i => { fired = i; loadedCount++; });
        // Wait by time, not by pump count: the decode runs in a worker, so how many pumps it takes depends on CPU load and
        // on the worker queue. Keep pumping so the result can be delivered; yield so the worker gets the CPU.
        var asyncWait = Stopwatch.StartNew();
        var asyncPumps = 0;
        while (!isLoaded && asyncWait.ElapsedMilliseconds < 10000)
        {
            Pump(1);
            asyncPumps++;
            async.Update(new VlPath(asset), false, true, out isLoaded, out _, out _);
            if (!isLoaded)
                Thread.Sleep(1);
        }
        s.Check(isLoaded && first is { State: QtImageState.Ready, Width: 30 } && fired == first && loadedCount == 1,
            $"LoadImage (Async) loaded ({first}, fired {loadedCount}, after {asyncWait.ElapsedMilliseconds} ms / {asyncPumps} pumps)");

        // 6) Dispose: exactly the released ids leave QtNative after the next pumps; the element keeps its own reference.
        var before = Live();
        load.Dispose();
        missing.Dispose();
        async.Dispose();
        s.Check(client.PendingReleases == 3, $"dispose queues the releases ({client.PendingReleases})");
        Frame(null, null);
        Pump(2);
        s.Check(client.PendingReleases == 0 && client.LiveCount == 1, $"releases flushed in the next pump (live {client.LiveCount})");
        s.Check(Live() == before - 3, $"QtNative frees the released images ({before} → {Live()})");
        toQt.Dispose();
        foreach (var n in nodes)
            n.Dispose();
        Pump(2);
        s.Check(client.LiveCount == 0, "all images released");
        Native.vlqt_scene_destroy(w);
        client.SetPumping(false);
        Pump(1);
    }

    private static int Live()
    {
        Span<Native.vlqt_res_info> list = stackalloc Native.vlqt_res_info[256];
        return Native.ResList(list);
    }
}
