using System;
using System.Buffers.Binary;
using System.Drawing;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// NCMD-17: the texture element of a SHADER link (here the layer of a Blend) is parked hidden under the root like a mask.
// A REPARENT of it while a chain samples it is refused (-8, like an attached mask), so the element is never in the tree
// and the chain's texture at once; once no chain samples it, a REPARENT shows it again. Orange R at (250, 150)
// 300 x 150 DIP with Blend, blue layer L at (0, 0) 300 x 150. Grabs: <OutDir>/chain-texture-reparent_*.png.
[TestFixture, NonParallelizable]
public static class ChainTextureReparentTest
{
    /// <summary>NCMD-17: REPARENT of the texture element of a chain link is refused while the chain samples it.</summary>
    [Test, Category("chain-texture-reparent")]
    public static void ChainTextureReparent() => Scenario.Run(Run);

    private const uint R = 1, L = 2, HR = 101, HL = 102;
    private const float RX = 250, RY = 150, RW = 300, RH = 150;

    private static Color Px(Bitmap a, float x, float y) => a.GetPixel((int)(x * a.Width / 800), (int)(y * a.Height / 600));
    private static bool Blueish(Color c) => c.B > c.R + 40;
    private static bool Orangeish(Color c) => c.R > c.B + 12 && c.R > 0x30;

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "Blend is a shader effect");
        Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var blend);
        Native.vlqt_shader_load(blend, "builtin:blend", Protocol.ShaderStage.Fragment);
        s.Check(Native.vlqt_res_wait(blend, 10000) == Protocol.ResourceState.Ready, "shader builtin:blend Ready");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt chain texture reparent test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(2);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        var errors = "";
        void Poll()
        {
            int n = NativeCalls.Poll(w, buf);
            var b = buf.AsSpan(0, Math.Max(n, 0));
            int off = 0;
            while (off + 12 <= b.Length)
            {
                ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 4)..]);
                if (len < 4 || off + 8 + len > b.Length)
                    break;
                if (ev == Protocol.EventId.Error && len > 8)
                    errors += $"[h{BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 8)..])}] " + System.Text.Encoding.UTF8.GetString(b.Slice(off + 12, len - 4)) + " ";
                off += (8 + len + 3) & ~3;
            }
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(R, Protocol.ElementType.Rectangle);
        cmd.SetPlace(R, RX, RY, RW, RH);
        cmd.SetColor(R, Protocol.PropertyId.Color, 1, 0.5f, 0.1f, 1);
        cmd.Reparent(R, 0, 0);
        cmd.Bind(R, HR);
        cmd.Create(L, Protocol.ElementType.Rectangle);
        cmd.SetPlace(L, 0, 0, RW, RH);
        cmd.SetColor(L, Protocol.PropertyId.Color, 0, 0.4f, 1, 1);
        cmd.Bind(L, HL);
        cmd.BeginEffectChain(R);
        cmd.BeginEffectChainLink(Protocol.EffectTemplate.Single, [blend], 0, 0, 0, 0, 0);
        cmd.UniformI32("mode", 0);
        cmd.UniformF32("layerOpacity", 1);
        cmd.UniformRef("layer", L);
        cmd.EndLink();
        cmd.EndEffectChain();
        var rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"scene and chain ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(8);
        Poll();
        s.Check(errors == "", $"no error with the parked texture ('{errors}')");
        using (var a = s.Grab(w, "chain-texture-reparent_before.png"))
        {
            gpu(Blueish(Px(a, RX + RW / 2, RY + RH / 2)), $"Blend: the layer lies on the element ({Px(a, RX + RW / 2, RY + RH / 2)})");
            var c = Px(a, 100, 75);
            s.Check(!Blueish(c), $"the parked texture is not drawn in the tree ({c})");
        }

        // The host REPARENTs the texture element under the root: refused, nothing changes.
        errors = "";
        cmd.Reset();
        cmd.Reparent(L, 0, 1);
        rc = Native.Apply(w, cmd.EndFrame());
        var message = rc != 0 ? Native.LastError() : "";
        s.Pump(4);
        Poll();
        s.Check(rc == Native.Result.Invalid && message.Contains($"element {L} is the texture of a chain link of {R}"),
            $"REPARENT of a chain's texture is refused (rc {rc}, '{message}')");
        s.Check(errors == "", $"no chain error after the refused REPARENT ('{errors}')");
        using (var a = s.Grab(w, "chain-texture-reparent_refused.png"))
        {
            var c = Px(a, 100, 75);
            s.Check(!Blueish(c), $"the refused texture is not drawn in the tree ({c})");
            gpu(Blueish(Px(a, RX + RW / 2, RY + RH / 2)), $"the chain still samples the texture ({Px(a, RX + RW / 2, RY + RH / 2)})");
        }

        // Chain off: no chain samples L any more, so a REPARENT shows it in the tree.
        cmd.Reset();
        cmd.BeginEffectChain(R);
        cmd.EndEffectChain();
        cmd.Reparent(L, 0, 1);
        rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"chain off, then REPARENT of the former texture ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(4);
        using (var a = s.Grab(w, "chain-texture-reparent_released.png"))
        {
            var c = Px(a, 100, 75);
            s.Check(Blueish(c), $"the former texture is drawn in the tree ({c})");
            var e = Px(a, RX + RW / 2, RY + RH / 2);
            s.Check(Orangeish(e), $"the element is drawn without the chain ({e})");
        }

        cmd.Reset();
        cmd.Destroy(R);
        cmd.Destroy(L);
        Native.Apply(w, cmd.EndFrame());
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
        Native.vlqt_res_release(blend);
    }
}
