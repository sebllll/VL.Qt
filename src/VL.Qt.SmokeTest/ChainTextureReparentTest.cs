using System;
using System.Buffers.Binary;
using System.Drawing;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// NCMD-17: the texture element of a SHADER link (here the layer of a Blend) is parked hidden under the root like a mask,
// but REPARENT does not check it (a mask attached to a chain is refused, commands.cpp maskHost). At SET time a texture
// element with a parent is an error ("the texture element has a parent (REPARENT); a texture of a chain link is an
// element of its own"). So a REPARENT of the parked texture must be refused, or detach it from the chain with that error
// line; it must not leave the element in the tree while the chain still samples it. Orange R at (250, 150) 300 x 150 DIP
// with Blend, blue layer L at (0, 0) 300 x 150. Grabs: <OutDir>/chain-texture-reparent_*.png.
[TestFixture, NonParallelizable]
public static class ChainTextureReparentTest
{
    /// <summary>NCMD-17: REPARENT of the texture element of a chain link is refused or reported.</summary>
    [Test, Category("chain-texture-reparent")]
    [Ignore("NCMD-17: REPARENT of a chain link's texture element is accepted without a check or error, the element is drawn in the tree and still sampled by the chain (fails: rc 0, no EVENT_ERROR, blue in both places)")]
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

        // The host REPARENTs the texture element under the root.
        errors = "";
        cmd.Reset();
        cmd.Reparent(L, 0, 1);
        rc = Native.Apply(w, cmd.EndFrame());
        var message = rc != 0 ? Native.LastError() : "";
        s.Pump(4);
        Poll();
        s.Info($"REPARENT of the texture: rc {rc} '{message}', errors '{errors}'");
        using (var a = s.Grab(w, "chain-texture-reparent_after.png"))
        {
            var inTree = Blueish(Px(a, 100, 75));
            var sampled = Blueish(Px(a, RX + RW / 2, RY + RH / 2));
            s.Info($"after REPARENT: texture drawn in the tree {inTree} ({Px(a, 100, 75)}), chain still samples it {sampled} ({Px(a, RX + RW / 2, RY + RH / 2)}), element base orange {Orangeish(Px(a, RX + RW / 2, RY + RH / 2))}");
            s.Check(rc != 0 || errors.Contains("texture element"),
                $"REPARENT of a chain's texture is refused or reported as a chain error (rc {rc}, errors '{errors}')");
            gpu(!(inTree && sampled), $"the element is not both in the tree and the chain's texture (tree {inTree}, sampled {sampled})");
        }

        cmd.Reset();
        cmd.BeginEffectChain(R);
        cmd.EndEffectChain();
        cmd.Destroy(R);
        cmd.Destroy(L);
        Native.Apply(w, cmd.EndFrame());
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
        Native.vlqt_res_release(blend);
    }
}
