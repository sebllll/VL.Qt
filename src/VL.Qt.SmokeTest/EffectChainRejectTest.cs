using System.Drawing;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// A rejected SET_EFFECT_CHAIN changes nothing, also when its first links would park a new mask and a new
// texture element: the whole command (wire format, ids, masks) is checked before anything is parked. Orange R at
// (250, 150) 300 x 150 DIP gets a chain of a MULTI run with mask M, a Blend with layer T and a MULTI run with mask P;
// P (green, 200 x 100 at the origin) is in the tree, so the third link is -8. M and T are created without REPARENT
// (like the reconciler). A parked element is a QML item under the window root, so the item count of the F5 scene page
// tells whether anything was parked. Grab: <OutDir>/effect-chain-reject.png.
[TestFixture, NonParallelizable]
public static class EffectChainRejectTest
{
    /// <summary>A SET_EFFECT_CHAIN rejected at its last link parks none of the masks and textures of the links before.</summary>
    [Test, Category("effect-chain-reject")]
    public static void EffectChainReject() => Scenario.Run(Run);

    private const uint R = 1, M = 2, T = 3, P = 4;

    private static Color Px(Bitmap a, float x, float y) => a.GetPixel((int)(x * a.Width / 800), (int)(y * a.Height / 600));
    private static bool Greenish(Color c) => c.G > c.R + 40 && c.G > c.B + 40;
    private static bool Orangeish(Color c) => c.R > c.B + 12 && c.R > 0x30;

    private static int Items(SceneHandle w)
    {
        NativeCalls.Poll(w, new byte[1 << 16]);
        var m = Regex.Match(NativeCalls.PerfPageText(w, 2), @"QML items\s+(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    private static void Run(Scenario s)
    {
        Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var blend);
        Native.vlqt_shader_load(blend, "builtin:blend", Protocol.ShaderStage.Fragment);
        s.Check(Native.vlqt_res_wait(blend, 10000) == Protocol.ResourceState.Ready, "shader builtin:blend Ready");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt effect chain reject test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(2);
        var cmd = new CommandWriter();
        void WriteChain(bool withP)
        {
            cmd.BeginEffectChain(R);
            cmd.BeginMultiLink();
            cmd.MultiEffectRef(Protocol.MultiEffectKey.MaskSource, M);
            cmd.EndLink();
            cmd.BeginEffectChainLink(Protocol.EffectTemplate.Single, [blend], 0, 0, 0, 0, 0);
            cmd.UniformI32("mode", 0);
            cmd.UniformF32("layerOpacity", 1);
            cmd.UniformRef("layer", T);
            cmd.EndLink();
            if (withP)
            {
                cmd.BeginMultiLink();
                cmd.MultiEffectRef(Protocol.MultiEffectKey.MaskSource, P);
                cmd.EndLink();
            }
            cmd.EndEffectChain();
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(R, Protocol.ElementType.Rectangle);
        cmd.SetPlace(R, 250, 150, 300, 150);
        cmd.SetColor(R, Protocol.PropertyId.Color, 1, 0.5f, 0.1f, 1);
        cmd.Reparent(R, 0, 0);
        cmd.Create(P, Protocol.ElementType.Rectangle);
        cmd.SetPlace(P, 0, 0, 200, 100);
        cmd.SetColor(P, Protocol.PropertyId.Color, 0.1f, 0.8f, 0.2f, 1);
        cmd.Reparent(P, 0, 1);
        cmd.Create(M, Protocol.ElementType.Rectangle);
        cmd.SetPlace(M, 0, 0, 300, 150);
        cmd.SetColor(M, Protocol.PropertyId.Color, 1, 1, 1, 1);
        cmd.Create(T, Protocol.ElementType.Rectangle);
        cmd.SetPlace(T, 0, 0, 300, 150);
        cmd.SetColor(T, Protocol.PropertyId.Color, 0, 0.4f, 1, 1);
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"scene ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(4);
        var itemsBefore = Items(w);

        // The third link's mask P has a parent: the command is rejected after links 1 and 2 passed.
        cmd.Clear();
        WriteChain(withP: true);
        rc = Native.Apply(w, cmd.EndFrame());
        var message = rc != 0 ? Native.LastError() : "";
        s.Check(rc == Native.Result.Invalid && message.Contains($"mask {P} already has a parent"),
            $"the chain with a mask in the tree is rejected (rc {rc}, '{message}')");
        s.Pump(4);
        var itemsRejected = Items(w);
        s.Check(itemsBefore > 0 && itemsRejected == itemsBefore,
            $"the rejected chain parks nothing (items {itemsRejected} vs {itemsBefore})");
        using (var a = s.Grab(w, "effect-chain-reject.png"))
        {
            var p = Px(a, 100, 50);
            s.Check(Greenish(p), $"P is still drawn where it was ({p})");
            var r = Px(a, 400, 225);
            s.Check(Orangeish(r), $"R is drawn without a chain ({r})");
        }

        // The same chain without the third link is taken, and parks M and T (so the item count above can tell).
        cmd.Clear();
        WriteChain(withP: false);
        rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"the chain without the mask in the tree ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(4);
        var itemsTaken = Items(w);
        s.Check(itemsTaken > itemsBefore, $"the taken chain parks its mask and texture (items {itemsTaken} vs {itemsBefore})");

        cmd.Clear();
        cmd.BeginEffectChain(R);
        cmd.EndEffectChain();
        cmd.Destroy(R);
        cmd.Destroy(P);
        cmd.Destroy(M);
        cmd.Destroy(T);
        Native.Apply(w, cmd.EndFrame());
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
        Native.vlqt_res_release(blend);
    }
}
