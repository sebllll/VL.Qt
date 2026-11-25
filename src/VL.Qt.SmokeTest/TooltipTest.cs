using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Lib.Mathematics;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using VPath = VL.Lib.IO.Path;

// Tooltip previews (package 01): vlqt_scene_get_change_count (counts scene changes only: not every pump, not the
// render a grab triggers) and vlqt_snapshot_start_fit (scale so that the longest edge is <= maxEdgePx and <= 1 pixel per
// DIP). Window 1200 x 700 DIP: a 1000 x 500 DIP blue rectangle, a 100 x 50 red one, a Button, a Group whose orange child
// sits in negative coordinates (frame 250 x 150, see GroupFrameTest). Snapshots go to <OutDir>/544_*.png.
// A device pixel ratio other than 1 needs its own run: $env:QT_SCALE_FACTOR=1.5; tools/build.ps1 -Test -Only tooltip.
[TestFixture, NonParallelizable]
public static class TooltipTest
{
    private const string VideoAsset = "testsrc_160x90_2s.mp4";

    private static bool Near(int a, int b, int tolerance) => Math.Abs(a - b) <= tolerance;

    private static string? FindAsset(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "src", "VL.Qt.SmokeTest", "assets", name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    private sealed class Job
    {
        public ImageResource Resource = null!;
        public bool Done;
        public bool Started;
    }

    /// <summary>Tooltip previews: change count and snapshot fit.</summary>
    [Test, Category("tooltip")]
    public static void Tooltip() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var client = new ResourceClient(new NativeResourceApi(), Environment.CurrentManagedThreadId, static () => null);
        client.SetPumping(true);

        var w = NativeCalls.CreateWindow(1200, 700, "VL.Qt tooltip test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var scene = new OccurrenceRegistry();
        var interactions = new InteractionRegistry();
        var rec = new Reconciler(cmd, new IdAllocator(), scene, interactions) { Window = w };

        VL.Qt.Elements.Rectangle Big(Color4 fill) => new() { Size = new Vector2(10, 5), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Fill = fill };
        var blue = new Color4(0.2f, 0.4f, 1, 1);
        var big = Big(blue);
        var small = new VL.Qt.Elements.Rectangle { Size = new Vector2(1, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(10.2f, 0, 0), Fill = new Color4(1, 0, 0, 1) };
        var button = new Button { Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(10.2f, 1, 0), Text = "Hover" };
        var orange = new VL.Qt.Elements.Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.Center, Transform = Matrix.Translation(0, 0, 0), Fill = new Color4(1, 0.6f, 0, 1) };
        var tail = new VL.Qt.Elements.Rectangle { Size = new Vector2(1, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 0.5f, 0), Fill = new Color4(0.2f, 0.4f, 1, 1) };
        var group = new Group { Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(3, 2, 0), Children = [orange, tail] };
        var videoPath = FindAsset(VideoAsset);

        Element Tree(Element bigElement, bool withVideo)
        {
            var children = new List<Element> { bigElement, small, button, group };
            if (withVideo && videoPath is not null)
                children.Add(new Video { Size = new Vector2(1.6f, 0.9f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(10.2f, 3, 0), Path = new VPath(videoPath), Playing = true, Loop = true });
            return new Group { Anchor = RectangleAnchor.TopLeft, Children = [.. children] };
        }

        Element current = Tree(big, false);
        void Frame(bool clear = false)
        {
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(current, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
        }
        // One vvvv frame as QtApp runs it: reconciler apply, then releases → processEvents (grabs finish) → status queue.
        void Step(int frames, int sleepMs = 0)
        {
            for (int i = 0; i < frames; i++)
            {
                Frame();
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
                if (sleepMs > 0) Thread.Sleep(sleepMs);
            }
        }
        long Count() => Native.vlqt_scene_get_change_count(w);
        void Mouse(int type, float x, float y) => s.Check(NativeTest.vlqt_test_send_mouse(w, type, x, y) == 0, $"test_send_mouse {type}");

        Frame(clear: true);
        Mouse(TestProtocol.Mouse.Move, 1150, 650);
        Step(10);

        float dpr;
        using (var probe = s.Grab(w, "544_window.png"))
            dpr = probe.Width / 1200f;
        s.Info($"device pixel ratio {dpr:F2} (another dpr needs another run: $env:QT_SCALE_FACTOR=1.5; tools/build.ps1 -Test -Only tooltip)");

        // Snapshot helpers on the client the way a node uses them.
        Job Start(Element element, int fitEdge, float scale = 1)
        {
            var job = new Job { Resource = client.CreateSnapshot() };
            job.Resource.Settled = _ => job.Done = true;
            if (!scene.TryLocate(element, out var window, out var id))
            {
                s.Check(false, "element is shown (TryLocate)");
                return job;
            }
            job.Started = fitEdge > 0 ? client.SnapshotFit(job.Resource, window, id, fitEdge) : client.Snapshot(job.Resource, window, id, scale);
            return job;
        }
        void Finish(params Job[] jobs)
        {
            for (int frames = 0; frames < 120 && jobs.Any(j => j.Started && !j.Done); frames++)
                Step(1);
        }
        IImage? Read(Job job) => job.Resource.State == QtImageState.Ready ? client.ReadSnapshot(job.Resource) : null;

        // 1) Static scene: the count stays over 60 pumps, also while snapshots (scale and fit) run and finish.
        var c0 = Count();
        Step(60);
        var c1 = Count();
        s.Info($"static scene, 60 pumps: change count {c0} -> {c1}");
        s.Check(c0 >= 0 && c1 == c0, $"static scene: change count stays over 60 pumps ({c0} -> {c1})");

        var deltas = new List<string>();
        int snapshots = 0, readable = 0;
        long cStart = Count();
        for (int round = 0; round < 3; round++)
        {
            // All together (group with frame included), then one after the other.
            var jobs = new[] { Start(big, 256), Start(small, 256), Start(group, 100), Start(big, 0, 0.5f), Start(group, 0, 1) };
            var before = Count();
            Finish(jobs);
            snapshots += jobs.Count(j => j.Done);
            readable += jobs.Count(j => Read(j) is not null);
            deltas.Add($"round {round} together {Count() - before}");
            foreach (var j in jobs) client.Release(j.Resource);
            Step(2);
            foreach (var (element, edge) in new (Element, int)[] { (big, 256), (group, 256), (small, 64) })
            {
                var one = Start(element, edge);
                before = Count();
                Finish(one);
                snapshots += one.Done ? 1 : 0;
                readable += Read(one) is not null ? 1 : 0;
                deltas.Add($"{(element == big ? "big" : element == group ? "group" : "small")}@{edge} {Count() - before}");
                client.Release(one.Resource);
                Step(1);
            }
        }
        Step(10);
        var cEnd = Count();
        s.Info($"static scene with snapshots: change count {cStart} -> {cEnd} (delta per snapshot: {string.Join(", ", deltas)}), {snapshots} done, {readable} readable");
        s.Check(snapshots == 3 * 8 && readable == snapshots, $"snapshots finish while the scene is static ({snapshots} done, {readable} readable of 24)");
        s.Check(cEnd == cStart, $"static scene: change count stays while snapshots run and finish ({cStart} -> {cEnd})");

        // 2) snapshot_fit sizes and pixels: 1000 x 500 DIP at 256 -> 256 x 128, 100 x 50 at 256 -> 100 x 50, group frame (250 x 150) at 100 -> 100 x 60.
        var jBig = Start(big, 256);
        var jSmall = Start(small, 256);
        var jGroup = Start(group, 100);
        var jEdge = Start(big, 1);
        var jExact = Start(small, 100);
        Finish(jBig, jSmall, jGroup, jEdge, jExact);
        var bigImage = Read(jBig);
        var smallImage = Read(jSmall);
        var groupImage = Read(jGroup);
        var edgeImage = Read(jEdge);
        var exactImage = Read(jExact);
        {
            s.Check(bigImage is { Info: { Width: 256, Height: 128 } }, $"fit 1000 x 500 DIP at 256 = 256 x 128 ({bigImage?.Info.Width} x {bigImage?.Info.Height}, state {jBig.Resource.State})");
            s.Check(smallImage is { Info: { Width: 100, Height: 50 } }, $"fit 100 x 50 DIP at 256 = 100 x 50, never upscaled ({smallImage?.Info.Width} x {smallImage?.Info.Height})");
            s.Check(groupImage is { Info: { Width: 100, Height: 60 } }, $"fit Group frame 250 x 150 at 100 = 100 x 60 ({groupImage?.Info.Width} x {groupImage?.Info.Height})");
            s.Check(edgeImage is { Info: { Width: 1 } }, $"fit at 1 = 1 x 1 ({edgeImage?.Info.Width} x {edgeImage?.Info.Height})");
            s.Check(exactImage is { Info: { Width: 100, Height: 50 } }, $"fit 100 x 50 DIP at 100 = 100 x 50 ({exactImage?.Info.Width} x {exactImage?.Info.Height})");
            if (bigImage is not null)
            {
                SnapshotTest.WritePng(bigImage, Path.Combine(s.OutDir, "544_fit_big_256.png"));
                var (b, g, r, a) = SnapshotTest.At(bigImage, 128, 64, 256);
                s.Check(a == 255 && Near(r, 51, 3) && Near(g, 102, 3) && Near(b, 255, 3), $"fit big: center pixel is blue (B{b} G{g} R{r} A{a})");
            }
            if (smallImage is not null)
            {
                SnapshotTest.WritePng(smallImage, Path.Combine(s.OutDir, "544_fit_small_256.png"));
                var (b, g, r, a) = SnapshotTest.At(smallImage, 50, 25, 100);
                s.Check(a == 255 && Near(r, 255, 3) && Near(g, 0, 3) && Near(b, 0, 3), $"fit small: center pixel is red (B{b} G{g} R{r} A{a})");
            }
            if (groupImage is not null)
            {
                SnapshotTest.WritePng(groupImage, Path.Combine(s.OutDir, "544_fit_group_100.png"));
                var tl = SnapshotTest.At(groupImage, 5, 5, 100); // top left of the frame: the orange part in negative coordinates
                var br = SnapshotTest.At(groupImage, 95, 55, 100); // bottom right: the blue rectangle
                s.Check(tl.A == 255 && Near(tl.R, 255, 3) && Near(tl.G, 153, 3), $"fit group: top left of the frame is orange ({tl})");
                s.Check(br.A == 255 && Near(br.R, 51, 3) && Near(br.B, 255, 3), $"fit group: bottom right of the frame is blue ({br})");
            }
            if (edgeImage is not null)
                SnapshotTest.WritePng(edgeImage, Path.Combine(s.OutDir, "544_fit_big_1.png"));
        }
        foreach (var j in new[] { jBig, jSmall, jGroup, jEdge, jExact }) client.Release(j.Resource);
        Step(2);

        // 2b) ElementTooltipPreviewNode on the real QtNative: an image <= 256 px, no further grabs in a static scene,
        // a new one after the scene changed (hover), a text for an element no renderer shows, release on Dispose.
        {
            var tip = new ElementTooltipPreviewNode(client, scene, static window => Native.vlqt_scene_get_change_count(window),
                static () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            IImage? tipImage = null;
            string tipText = "";
            void TipSteps(int frames, int sleepMs)
            {
                for (int i = 0; i < frames; i++)
                {
                    Frame();
                    tipImage = tip.Update(big, out tipText);
                    client.BeforeEvents();
                    s.Pump(1);
                    client.AfterEvents();
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                }
            }

            for (int i = 0; i < 120 && tipImage is null; i++)
                TipSteps(1, 5);
            s.Check(tipImage is { Info: { Width: <= 256, Height: <= 256 } } && Math.Max(tipImage.Info.Width, tipImage.Info.Height) == 256,
                $"preview of the big rectangle has a longest edge of 256 px ({tipImage?.Info.Width} x {tipImage?.Info.Height})");
            s.Check(tipText.StartsWith("Rectangle"), $"preview text names the type ('{tipText}')");
            if (tipImage is not null)
            {
                SnapshotTest.WritePng(tipImage, Path.Combine(s.OutDir, "545_preview_big.png"));
                var (b, g, r, a) = SnapshotTest.At(tipImage, 128, 64, tipImage.Info.Width);
                s.Check(a == 255 && Near(r, 51, 3) && Near(g, 102, 3) && Near(b, 255, 3), $"preview: center pixel is blue (B{b} G{g} R{r} A{a})");
            }

            // Static scene: 40 calls over more than 0.2 s (the throttle is open) give no new image.
            TipSteps(20, 15); // let a pending grab (the first one's own render) settle
            var settledImage = tipImage;
            var cStatic = Count();
            TipSteps(40, 15);
            s.Check(ReferenceEquals(tipImage, settledImage), "static scene: no further grab (same image over 40 calls)");
            s.Check(Count() == cStatic, $"static scene: previews do not raise the change count ({cStatic} -> {Count()})");

            // Scene change: hover over the Button → the count rises → a new image.
            Mouse(TestProtocol.Mouse.Move, 1040, 115);
            for (int i = 0; i < 60 && ReferenceEquals(tipImage, settledImage); i++)
                TipSteps(1, 15);
            s.Check(!ReferenceEquals(tipImage, settledImage), "hover raises the change count: a new preview image arrives");
            Mouse(TestProtocol.Mouse.Move, 1150, 650);
            TipSteps(40, 15); // transition and the last grab done
            var afterHover = tipImage;
            TipSteps(40, 15);
            s.Check(ReferenceEquals(tipImage, afterHover), "static again: no further grab");

            // An element no renderer shows: text, no image, no grab.
            var hidden = new VL.Qt.Elements.Rectangle { Size = new Vector2(2, 1) };
            var hiddenImage = tip.Update(hidden, out var hiddenText);
            s.Check(hiddenImage is null && hiddenText.Contains("not shown in a renderer"), $"element in no renderer: text only ('{hiddenText}')");

            tip.Dispose();
            Step(3);
        }

        // 2c) ElementTooltipPreviewNode: a target that is a new instance every frame at the same place (an LFO on colour and size
        // in a patch). The place is the identity, so the first image arrives, stays, and gets replaced by newer ones, never null in between.
        {
            var tip = new ElementTooltipPreviewNode(client, scene, static window => Native.vlqt_scene_get_change_count(window),
                static () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            IImage? lastImage = null;
            int arrivals = 0, flickers = 0;
            for (int i = 0; i < 150; i++)
            {
                var target = new Group
                {
                    Anchor = RectangleAnchor.TopLeft,
                    Children =
                    [
                        Big(blue),
                        new VL.Qt.Elements.Rectangle { Size = new Vector2(4.8f, 2.7f + i % 10 * 0.1f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 1, 0), Fill = new Color4(1, 0.3f + i % 10 * 0.07f, 0, 1) }
                    ]
                };
                current = new Group { Anchor = RectangleAnchor.TopLeft, Children = [target, small] };
                Frame();
                var image = tip.Update(target, out _);
                if (lastImage is not null && image is null)
                    flickers++;
                if (image is not null && !ReferenceEquals(image, lastImage))
                {
                    arrivals++;
                    lastImage = image;
                }
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
                Thread.Sleep(15);
            }
            s.Check(lastImage is not null, "a target that is a new instance every frame gets a preview image");
            s.Check(flickers == 0, $"new instance every frame: the image never falls back to null ({flickers} times)");
            s.Check(arrivals >= 3, $"new instance every frame: the image is replaced by newer ones ({arrivals} images in about 2.5 s)");
            if (lastImage is not null)
                SnapshotTest.WritePng(lastImage, Path.Combine(s.OutDir, "548_preview_new_instance_every_frame.png"));
            tip.Dispose();
            current = Tree(big, false);
            Step(3);
        }

        // 2d) Order of a patch: Snapshot and TooltipPreview run BEFORE the renderer of the same frame, and the Group is a new
        // instance every frame (an animated input) with the feedback slot of its node. The registry still holds last frame's instance;
        // TryLocate falls back to the place of the slot, so both find the group and deliver an image.
        {
            var slot = new FeedbackSlot(new ElementFeedback(), 0);
            var tip = new ElementTooltipPreviewNode(client, scene, static window => Native.vlqt_scene_get_change_count(window),
                static () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            var snapLog = new NodeLog();
            var snap = new SnapshotNode(client, scene, snapLog.Errors);
            IImage? tipImage = null, snapImage = null;
            string tipText = "", snapError = "";
            int notShown = 0;
            Group Animated(int i) => new()
            {
                Anchor = RectangleAnchor.TopLeft,
                Transform = Matrix.Translation(3, 2, 0),
                Opacity = 0.9f + i % 10 * 0.01f,
                Feedback = slot,
                Children = [orange, tail],
            };
            current = new Group { Anchor = RectangleAnchor.TopLeft, Children = [Animated(0), small] };
            Step(2);
            for (int i = 1; i < 200 && (tipImage is null || snapImage is null); i++)
            {
                var target = Animated(i);
                tipImage = tip.Update(target, out tipText) ?? tipImage;
                // grab is an edge: once, in the first pass (the point of the test); every further grab would supersede the running one
                // (only the newest grab of an id counts), and with the software backend a grab needs more than one pump.
                snapImage = snap.Update(target, out _, 1, grab: i == 1) ?? snapImage;
                var logged = snapLog.Take();
                if (logged.Length > 0)
                    snapError = logged;
                if (tipText.Contains(ElementTooltipPreviewNode.NotShownSuffix) || logged == SnapshotNode.NotShownError)
                    notShown++;
                current = new Group { Anchor = RectangleAnchor.TopLeft, Children = [target, small] };
                Step(1, 15);
            }
            s.Check(notShown == 0, $"new instance before the renderer update: found by its place ({notShown} times 'not shown', last '{tipText}' / '{snapError}')");
            s.Check(tipImage is not null, "TooltipPreview before the renderer update delivers an image");
            s.Check(snapImage is not null, $"Snapshot before the renderer update delivers an image ('{snapError}')");
            if (snapImage is not null)
                SnapshotTest.WritePng(snapImage, Path.Combine(s.OutDir, "548_snapshot_before_renderer.png"));
            tip.Dispose();
            snap.Dispose();
            current = Tree(big, false);
            Step(3);
        }

        // 3) The count rises: changed property (colour), hover, running video.
        var cNow = Count();
        current = Tree(Big(new Color4(0.8f, 0.2f, 0.2f, 1)), false);
        Step(3);
        var cProp = Count();
        s.Info($"changed colour: change count {cNow} -> {cProp}");
        s.Check(cProp > cNow, $"changed property raises the change count ({cNow} -> {cProp})");
        cNow = Count();
        Step(30);
        s.Check(Count() == cNow, $"static again after the change ({cNow} -> {Count()})");

        cNow = Count();
        Mouse(TestProtocol.Mouse.Move, 1040, 115); // over the button
        Step(5);
        var cHover = Count();
        s.Info($"hover over the Button: change count {cNow} -> {cHover}");
        s.Check(cHover > cNow, $"hover raises the change count ({cNow} -> {cHover})");
        Step(20); // transition of the style done
        cNow = Count();
        Step(30);
        s.Check(Count() == cNow, $"static while hovering ({cNow} -> {Count()})");
        Mouse(TestProtocol.Mouse.Move, 1150, 650);
        Step(5);
        s.Info($"hover left: change count {cNow} -> {Count()}");
        s.Check(Count() > cNow, $"leaving the Button raises the change count ({cNow} -> {Count()})");
        Step(20);

        if (videoPath is null)
            s.Info($"video asset {VideoAsset} not found, running video not checked");
        else
        {
            current = Tree(Big(new Color4(0.8f, 0.2f, 0.2f, 1)), true);
            Step(10, 15); // load
            cNow = Count();
            Step(40, 15);
            var cVideo = Count();
            s.Info($"running video, 40 pumps (15 ms): change count {cNow} -> {cVideo}");
            s.NeedsRhi(cVideo - cNow >= 5, $"running video raises the change count ({cNow} -> {cVideo})", "VideoOutput draws nothing");
            current = Tree(Big(new Color4(0.8f, 0.2f, 0.2f, 1)), false);
            Step(10, 15);
            cNow = Count();
            Step(30);
            s.Check(Count() == cNow, $"static again after the video left ({cNow} -> {Count()})");
        }

        // F5 overlay (stats, pages with a graph): redraws every frame, but is no scene change; let it settle once after opening.
        foreach (var (page, name) in new[] { (1, "Timeline"), (0, "Summary") })
        {
            const int Stats = 2, PageShift = 4;
            s.Check(NativeTest.vlqt_test_set_overlay(w, Stats | ((page + 1) << PageShift)) == 0, $"F5 overlay {name} opens");
            Step(30);
            cNow = Count();
            Step(60);
            var cOverlay = Count();
            s.Info($"static scene, F5 overlay {name}, 60 pumps: change count {cNow} -> {cOverlay}");
            s.Check(cOverlay == cNow, $"static scene with the F5 overlay ({name}): change count stays over 60 pumps ({cNow} -> {cOverlay})");
        }
        NativeTest.vlqt_test_set_overlay(w, 0);
        Step(10);

        // 3b) ElementTooltipPreviewNode and video. (a) A tree with a Video gets only text and never a grab (a grab of a running
        // hardware-decoded video costs the frame 25-57 ms). (b) Hitch rate of the preview on an element without a video.
        {
            var videoGroup = new Group
            {
                Anchor = RectangleAnchor.TopLeft,
                Children =
                [
                    Big(blue),
                    new Group { Children = [new Video { Size = new Vector2(4.8f, 2.7f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 1, 0), Path = videoPath is null ? VPath.Default : new VPath(videoPath), Playing = true, Loop = true }] }
                ]
            };
            var videoTip = new ElementTooltipPreviewNode(client, scene, static window => Native.vlqt_scene_get_change_count(window),
                static () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            var liveBefore = client.LiveCount;
            current = videoGroup;
            Step(10, 15);
            IImage? videoImage = null;
            string videoText = "";
            for (int i = 0; i < 100; i++)
            {
                Frame();
                videoImage = videoTip.Update(videoGroup, out videoText);
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
                Thread.Sleep(5);
            }
            s.Check(videoImage is null, "tree with a Video: no preview image");
            s.Check(videoText.Contains("contains video, no preview"), $"tree with a Video: text says so ('{videoText}')");
            s.Check(client.LiveCount == liveBefore, $"tree with a Video: no snapshot resource created, so no grab (live {liveBefore} -> {client.LiveCount})");
            videoTip.Dispose();
            current = Tree(big, false);
            Step(5);
        }

        // Hitch rate of ElementTooltipPreviewNode: paced to 60 Hz, a moving rectangle next to the tooltip target (a group with a
        // big rectangle and a small one, 1000 x 500 DIP, scaled down to 256 px); the moving one changes the scene every frame.
        // The target is a new instance every frame in both arms (colour and size of the small rectangle move, like an LFO in a patch).
        // Arms A (without the node) and B (tip.Update every frame, throttled to <= 5 Hz inside) alternate A B A B so drift and
        // load hit both. Hitch = interval > 1.5 x median of all A intervals.
        if (PerfLimits.SkipHeavy)
            s.Skip("perf run tooltip preview hitches: heavy perf measurement skipped on shared CI runners");
        else
        {
            const double Period = 1000.0 / 60;
            const int BlockFrames = 200;
            Element targetGroup = null!;
            var perfTip = new ElementTooltipPreviewNode(client, scene, static window => Native.vlqt_scene_get_change_count(window),
                static () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            var intervalsA = new List<double>();
            var intervalsB = new List<double>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double lastAt = 0, nextAt = clock.Elapsed.TotalMilliseconds;
            IImage? lastPreview = null;
            int previews = 0, frameNo = 0;
            void PerfFrame(bool withTip, List<double>? into)
            {
                nextAt += Period;
                if (clock.Elapsed.TotalMilliseconds > nextAt) nextAt = clock.Elapsed.TotalMilliseconds; // behind: no catch-up burst
                while (clock.Elapsed.TotalMilliseconds < nextAt) Thread.SpinWait(50);
                var now = clock.Elapsed.TotalMilliseconds;
                into?.Add(now - lastAt);
                lastAt = now;
                var mover = new VL.Qt.Elements.Rectangle { Size = new Vector2(0.5f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(5 + frameNo++ % 200 * 0.01f, 6, 0), Fill = new Color4(0, 1, 0, 1) };
                targetGroup = new Group
                {
                    Anchor = RectangleAnchor.TopLeft,
                    Children =
                    [
                        Big(blue),
                        new VL.Qt.Elements.Rectangle { Size = new Vector2(4.8f, 2.7f + frameNo % 10 * 0.1f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 1, 0), Fill = new Color4(1, 0.3f + frameNo % 10 * 0.07f, 0, 1) }
                    ]
                };
                current = new Group { Anchor = RectangleAnchor.TopLeft, Children = [targetGroup, mover] };
                Frame();
                if (withTip)
                {
                    var image = perfTip.Update(targetGroup, out _);
                    if (into is not null && image is not null && !ReferenceEquals(image, lastPreview))
                        previews++;
                    lastPreview = image ?? lastPreview;
                }
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
            }
            for (int i = 0; i < 120; i++) PerfFrame(false, null); // warm-up
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < BlockFrames; i++) PerfFrame(false, i == 0 ? null : intervalsA);
                for (int i = 0; i < BlockFrames; i++) PerfFrame(true, i == 0 ? null : intervalsB);
            }
            var medianA = intervalsA.OrderBy(x => x).ElementAt(intervalsA.Count / 2);
            var hitchLimit = medianA * 1.5;
            int Hitches(List<double> l) => l.Count(x => x > hitchLimit);
            double HitchRate(List<double> l) => l.Count == 0 ? 0 : 100.0 * Hitches(l) / l.Count;
            s.Info($"perf: ohne Tooltip {Hitches(intervalsA)}/{intervalsA.Count} hitches ({HitchRate(intervalsA):F1} %), "
                + $"mit Tooltip {Hitches(intervalsB)}/{intervalsB.Count} ({HitchRate(intervalsB):F1} %), median {medianA:F2} ms, max {intervalsB.Max():F1} ms, {previews} previews");
            s.Check(previews >= 10, $"previews arrived while running ({previews} new images)");
            PerfLimits.Below(s, HitchRate(intervalsB), Math.Max(HitchRate(intervalsA) * 3, 5), "hitch rate with ElementTooltipPreviewNode on an animated element in the range of the background, %");

            // F5 Resources page after the B run: the snapshot line of package 01 shows the previews' rate and cost.
            s.Check(NativeTest.vlqt_test_set_overlay(w, 2 | ((PerfOverlayTest.ResourcesPage + 1) << 4)) == 0, "F5 overlay Resources opens");
            for (int i = 0; i < BlockFrames; i++) PerfFrame(true, null);
            s.Info($"F5 Resources after the run: {PerfOverlayTest.PageText(w, PerfOverlayTest.ResourcesPage).Split('\n').FirstOrDefault(l => l.StartsWith("snapshots"))?.Trim() ?? "(missing)"}");
            NativeTest.vlqt_test_set_overlay(w, 0);
            perfTip.Dispose();
            current = Tree(big, false);
            Step(10);
        }

        // 4) Error paths: null window, bad edge, unknown element.
        s.Check(Native.vlqt_scene_get_change_count(SceneHandle.Null) == (long)Native.Result.NullHandle, $"change_count of a null window = NullHandle ({Native.vlqt_scene_get_change_count(SceneHandle.Null)})");
        var bad = client.CreateSnapshot();
        s.Check(Native.vlqt_snapshot_start_fit(bad.Id, SceneHandle.Null, 0, 256) == Native.Result.NullHandle, "snapshot_fit with a null window = NullHandle");
        s.Check(Native.vlqt_snapshot_start_fit(bad.Id, w, 0, 0) == Native.Result.BadArgument, "snapshot_fit maxEdge 0 = BadArgument");
        s.Check(Native.vlqt_snapshot_start_fit(bad.Id, w, 0, -5) == Native.Result.BadArgument, "snapshot_fit maxEdge -5 = BadArgument");
        client.Release(bad);
        Step(3);
        s.Check(client.LiveCount == 0, $"all snapshot resources released (live {client.LiveCount})");

        // Raw: unknown element → Failed with an error text, same as vlqt_snapshot_start; the client does not poll here.
        var events = new List<(ushort Ev, ulong Id, byte[] Payload)>();
        var buf = new byte[64 * 1024];
        void Poll()
        {
            var n = NativeCalls.ResPoll(buf, out var required);
            if (n == (int)Native.Result.BufferTooSmall)
            {
                buf = new byte[required * 2];
                n = NativeCalls.ResPoll(buf);
            }
            for (int at = 0; at < n;)
            {
                var ev = BitConverter.ToUInt16(buf, at);
                var len = (int)BitConverter.ToUInt32(buf, at + 4);
                events.Add((ev, BitConverter.ToUInt64(buf, at + 8), buf.AsSpan(at + 16, len - 8).ToArray()));
                at += (8 + len + 3) & ~3;
            }
        }
        string RawFailure(bool fit, out byte state)
        {
            var rcCreate = Native.vlqt_res_create(Protocol.ResourceKind.Snapshot, out var id);
            s.Check(rcCreate == 0 && id != 0, $"raw create ({rcCreate})");
            events.Clear();
            var rc = fit ? Native.vlqt_snapshot_start_fit(id, w, 4_000_000, 256) : Native.vlqt_snapshot_start(id, w, 4_000_000, 1);
            s.Check(rc == 0, $"raw {(fit ? "snapshot_fit" : "snapshot")} of an unknown element is accepted ({rc})");
            state = 0;
            for (int i = 0; i < 120 && state is not (Protocol.ResourceState.Ready or Protocol.ResourceState.Failed); i++)
            {
                s.Pump(1);
                Poll();
                state = events.Where(e => e.Id == id && e.Ev == Protocol.ResourceEventId.State).Select(e => e.Payload[0]).LastOrDefault();
            }
            var text = events.Where(e => e.Id == id && e.Ev == Protocol.ResourceEventId.Error).Select(e => Encoding.UTF8.GetString(e.Payload)).LastOrDefault() ?? "";
            Native.vlqt_res_release(id);
            s.Pump(1);
            Poll();
            return text;
        }
        var fitError = RawFailure(true, out var fitState);
        var plainError = RawFailure(false, out var plainState);
        s.Info($"unknown element: snapshot_fit '{fitError.TrimEnd('\0')}' (state {fitState}), snapshot '{plainError.TrimEnd('\0')}' (state {plainState})");
        s.Check(fitState == Protocol.ResourceState.Failed && fitError.Length > 0, $"snapshot_fit of an unknown element: Failed with an error text (state {fitState}, '{fitError}')");
        s.Check(fitError == plainError, $"the error text equals the one of vlqt_snapshot_start ('{fitError}' vs '{plainError}')");

        // Cleanup: the window goes, the count of a destroyed window is not asked again.
        client.SetPumping(false);
        cmd.Reset();
        rec.Clear();
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
    }
}
