using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Rectangle = VL.Qt.Elements.Rectangle;

// Node level interaction (#319): Hover -> element Interaction pin -> Reconciler (handler
// objects) -> QtNative -> SinkRegistry -> channels. The raw QtNative side of the input handlers is HandlerTest (the #208 bind
// flags Interactive / Pointer are gone since #320). Mouse input comes from the TEST export vlqt_test_send_mouse; real
// (OS) pointer input is ignored by the test windows (VLQT_TEST_SYNTHETIC_INPUT=1, set in Program.Main).
static partial class InteractionTest
{
    sealed class NoMessage : IDisposable { public void Dispose() { } }

    // #319, scenario "interaction-chain": node level Hover -> Rectangle (Interaction pin) ->
    // Reconciler (handler objects) -> QtNative -> SinkRegistry.Dispatch -> pull in Hover.Update -> channels.
    public static void RunChain(Action<bool, string> check, Action<int> pump)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt interaction chain test");
        check(w != IntPtr.Zero, "chain: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var buf = new byte[1 << 16];
        var hover = new HoverNode(_ => new NoMessage());
        var hover2 = new HoverNode(_ => new NoMessage());
        var rect = new RectangleNode(_ => new NoMessage());
        var hovered = Channel.Create(false);
        var hovered2 = Channel.Create(false);
        bool attach = true, chainTwo = false;

        // One vvvv frame: nodes update (pull the state of the last pump), renderer reconciles + applies, Qt pumps.
        void Frame()
        {
            var chain = hover.Update(null, hovered);
            if (chainTwo)
                chain = hover2.Update(chain, hovered2);
            // (1, 1, 2, 1) units = (100, 100, 200, 100) DIP.
            var e = rect.Update(new Vector2(2, 1), Matrix.Translation(1, 1, 0), RectangleAnchor.TopLeft, new Color4(0.2f, 0.4f, 0.8f, 1), 0,
                new Color4(0, 0, 0, 1), 0, 1, false, false, attach ? chain : null, null, out _, out _);
            cmd.Reset();
            rec.Update(e, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                check(rc == 0, $"chain: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            pump(1);
            int n = Native.Poll(w, buf);
            check(n >= 0, $"chain: poll ({n})");
            var res = rec.Sinks.Dispatch(buf.AsSpan(0, Math.Max(n, 0)));
            check(res.Error is null, $"chain: dispatch ({res.Error})");
        }
        void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }
        void Mouse(int type, float x, float y) => Native.vlqt_test_send_mouse(w, type, x, y);
        int Handlers() => Native.vlqt_test_handler_count(w, rec.Root?.Id ?? 0);

        Mouse(Protocol.Mouse.Move, 700, 500);
        Frames(2);
        check(Handlers() == 1, $"chain: Hover -> one handler at the item ({Handlers()})");
        check(!hovered.Value, "chain: not hovered outside");

        Mouse(Protocol.Mouse.Move, 150, 150);
        Frames(2); // event in frame 1, pulled by the node in frame 2
        check(hovered.Value && hovered.LatestAuthor == "Qt", $"chain: Is Hovered follows the mouse, author Qt ({hovered.Value}, {hovered.LatestAuthor})");
        Mouse(Protocol.Mouse.Move, 180, 180);
        Frames(2);
        check(hovered.Value, "chain: still hovered");

        // A handler attached under a resting cursor reports hover with the next pointer move (Qt, as with Interactive in 06c).
        chainTwo = true;
        Frame();
        Mouse(Protocol.Mouse.Move, 181, 180);
        Frames(2);
        check(Handlers() == 2 && hovered.Value && hovered2.Value,
            $"chain: two Hover nodes -> two handlers, both hovered ({Handlers()}, {hovered.Value}, {hovered2.Value})");

        Mouse(Protocol.Mouse.Move, 700, 500);
        Frames(2);
        check(!hovered.Value && !hovered2.Value, "chain: hover out -> both false");

        Mouse(Protocol.Mouse.Move, 150, 150);
        Frames(2);
        check(hovered.Value && hovered2.Value, "chain: hovered again");
        attach = false;
        Frames(2);
        check(Handlers() == 0, $"chain: Interaction removed -> no handlers ({Handlers()})");
        check(!hovered.Value && !hovered2.Value, "chain: removed while hovered -> Is Hovered false");
        check(rec.Sinks.Count == 1, $"chain: only the element handle left ({rec.Sinks.Count})");

        attach = true;
        Frame();
        Mouse(Protocol.Mouse.Move, 151, 150);
        Frames(2);
        check(Handlers() == 2 && hovered.Value, $"chain: re-attached -> handlers + hovered ({Handlers()}, {hovered.Value})");

        cmd.Reset();
        rec.Update(null, Space.DIPTopLeft);
        if (cmd.Length > 0)
            check(Native.Apply(w, cmd.EndFrame()) == 0, "chain: clear");
        hover.Dispose();
        hover2.Dispose();
        rect.Dispose();
        Native.vlqt_window_destroy(w);
        pump(1);
    }

    // #319: the Interaction pin works at Text, Group and Button as well (Hover -> Is Hovered).
    public static void RunElements(Action<bool, string> check, Action<int> pump)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt interaction elements test");
        check(w != IntPtr.Zero, "elements: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var buf = new byte[1 << 16];
        var hover = new HoverNode(_ => new NoMessage());
        var hovered = Channel.Create(false);
        using var text = new TextNode(_ => new NoMessage());
        using var group = new GroupNode(_ => new NoMessage());
        using var button = new ButtonNode(_ => new NoMessage());
        // (1, 1, 2, 1) units = (100, 100, 200, 100) DIP.
        var bounds = new RectangleF(1, 1, 2, 1);
        var factories = new (string Name, Func<Interaction?, Element> Make)[]
        {
            ("Text", i => text.Update(bounds.Sz(), bounds.Tr(), RectangleAnchor.TopLeft, "Hover", "", 1f, FontWeight.Normal, false,
                new Color4(1, 1, 1, 1), TextHorizontalAlignment.Left, VerticalAlignment.Top, TextWrap.NoWrap, TextElide.None, 1, 0,
                TextAutofit.Fixed, 1, false, false, i, null, out _, out _)),
            ("Group", i => group.Update(Spread.Create<IElement?>(new Rectangle { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0) }), bounds.Sz(), false, bounds.Tr(),
                RectangleAnchor.TopLeft, 1, false, false, i, null, null, out _, out _)),
            ("Button", i => button.Update(bounds.Sz(), false, bounds.Tr(), RectangleAnchor.TopLeft, "Hover", true, false, null, 1,
                false, false, i, null, null, out _, out _, out _)),
        };

        foreach (var (name, make) in factories)
        {
            void Frame(bool attach)
            {
                cmd.Reset();
                rec.Update(make(hover.Update(null, hovered) is var chain && attach ? chain : null), Space.DIPTopLeft);
                if (cmd.Length > 0)
                {
                    var rc = Native.Apply(w, cmd.EndFrame());
                    check(rc == 0, $"elements {name}: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
                }
                pump(1);
                int n = Native.Poll(w, buf);
                var res = rec.Sinks.Dispatch(buf.AsSpan(0, Math.Max(n, 0)));
                check(res.Error is null, $"elements {name}: dispatch ({res.Error})");
            }

            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, 700, 500);
            Frame(true);
            Frame(true);
            check(!hovered.Value, $"elements {name}: not hovered outside");
            Native.vlqt_test_send_mouse(w, Protocol.Mouse.Move, 150, 150);
            Frame(true);
            Frame(true);
            check(hovered.Value && hovered.LatestAuthor == "Qt", $"elements {name}: Interaction pin -> Is Hovered ({hovered.Value})");
            Frame(false);
            Frame(false);
            check(!hovered.Value, $"elements {name}: Interaction removed -> Is Hovered false");
            cmd.Reset();
            rec.Update(null, Space.DIPTopLeft);
            if (cmd.Length > 0)
                check(Native.Apply(w, cmd.EndFrame()) == 0, $"elements {name}: clear");
        }
        hover.Dispose();
        Native.vlqt_window_destroy(w);
        pump(1);
    }
}
