using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// Margin limit of an effect chain: the effect area, and with it every layer texture of the chain, grows by the
// links' paddings and by MultiEffect's auto padding (blurMax x (1 + blurMultiplier)); Qt bounds none of them. QtNative
// cuts the margins of all links together to the window's larger side per side (at least 1024 DIP), so an extreme value
// (the apply-fuzz OOM on llvmpipe: 16k x 16k layers) stays bounded. The largest layer is read from the Scene page of the
// perf overlay ("largest layer W x H DIP", the size Qt is asked for). Runs on every backend, the software one included.
[TestFixture, NonParallelizable]
public static class EffectMarginTest
{
    [Test, Category("effect-margin")]
    public static void EffectMargin() => Scenario.Run(Run);

    private const uint R = 1, FeedbackIdR = 41;
    private const float RW = 100, RH = 50;
    private const float Limit = 1024; // window 800 x 600: the floor
    private const float Bound = RW + 2 * Limit;

    // The poll also deletes retired stages (one frame late) and holds the EVENT_ERROR of R: lastError.
    private static (int w, int h) Largest(SceneHandle w, out string? lastError)
    {
        var poll = new byte[1 << 16];
        var n = NativeCalls.Poll(w, poll);
        lastError = ErrorText(poll.AsSpan(0, Math.Max(n, 0)), FeedbackIdR);
        var m = Regex.Match(NativeCalls.PerfPageText(w, 2), @"largest layer (-?\d+) x (-?\d+) DIP");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (-2, -2);
    }

    // Last EVENT_ERROR text for feedbackId in the poll buffer, null if none.
    private static string? ErrorText(ReadOnlySpan<byte> b, uint feedbackId)
    {
        string? text = null;
        int off = 0;
        while (off + 12 <= b.Length)
        {
            var ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4));
            if (len < 4 || off + 8 + len > b.Length) break;
            if (ev == Protocol.EventId.Error && BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)) == feedbackId)
                text = Encoding.UTF8.GetString(b.Slice(off + 12, len - 4));
            off += (8 + len + 3) & ~3;
        }
        return text;
    }

    private static void Run(Scenario s)
    {
        Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var wave);
        Native.vlqt_shader_load(wave, "builtin:wave", Protocol.ShaderStage.Fragment);
        s.Check(Native.vlqt_res_wait(wave, 10000) == Protocol.ResourceState.Ready, "effect-margin: shader builtin:wave Ready");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt effect margin test");
        s.Check(w != SceneHandle.Null, "effect-margin: window_create");
        Native.vlqt_window_show(w);
        s.Pump(4);
        var cmd = new CommandWriter();
        var poll = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"effect-margin: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Clear();
            s.Pump(4);
        }
        void Wave(CommandWriter c, float pad)
        {
            c.BeginEffectChainLink(Protocol.EffectTemplate.Single, [wave], pad, pad, pad, pad, 0);
            c.UniformF32("amplitude", 0.05f);
            c.EndLink();
        }
        void Blur(CommandWriter c, int blurMax, float multiplier, float pad)
        {
            c.BeginMultiLink();
            c.MultiEffectBool(Protocol.MultiEffectKey.BlurEnabled, true);
            c.MultiEffectF32(Protocol.MultiEffectKey.Blur, 0.5f);
            c.MultiEffectI32(Protocol.MultiEffectKey.BlurMax, blurMax);
            c.MultiEffectF32(Protocol.MultiEffectKey.BlurMultiplier, multiplier);
            if (pad != 0)
            {
                c.MultiEffectBool(Protocol.MultiEffectKey.AutoPadding, false);
                c.MultiEffectRect(Protocol.MultiEffectKey.PaddingRect, pad, pad, pad, pad);
            }
            c.EndLink();
        }
        void Chain(params Action<CommandWriter>[] links)
        {
            cmd.BeginEffectChain(R);
            foreach (var l in links)
                l(cmd);
            cmd.EndEffectChain();
        }

        cmd.Create(R, Protocol.ElementType.Rectangle);
        cmd.SetPlace(R, 350, 275, RW, RH);
        cmd.SetColor(R, Protocol.PropertyId.Color, 1, 1, 1, 1);
        cmd.Reparent(R, 0, 0);
        cmd.Bind(R, FeedbackIdR);
        CheckApply("scene");

        // Normal margins are not touched: padding 20 + blur 32 x (1 + 0.5) = 68 per side.
        Chain(c => Wave(c, 20), c => Blur(c, 32, 0.5f, 0));
        CheckApply("wave 20 -> blur 32");
        var normal = Largest(w, out var err);
        s.Check(Math.Abs(normal.w - (RW + 2 * 68)) <= 2 && Math.Abs(normal.h - (RH + 2 * 68)) <= 2, $"effect-margin: normal margins kept, largest layer {normal}");
        s.Check(string.IsNullOrEmpty(err), $"effect-margin: normal margins, no error (\"{err}\")");

        // SHADER padding 1e6 per side, then a second link on top: all links together stay within the limit.
        Chain(c => Wave(c, 1e6f), c => Wave(c, 4000));
        CheckApply("wave 1e6 -> wave 4000");
        var shader = Largest(w, out err);
        s.Check(shader.w > 0 && shader.w <= Bound && shader.h <= RH + 2 * Limit, $"effect-margin: SHADER padding cut, largest layer {shader} <= {Bound}");
        s.Check(err != null && err.Contains("link 0: padding cut") && err.Contains("link 1: padding cut"), $"effect-margin: cut reported at the element (\"{err}\")");

        // MULTI auto padding (blurMax 1e6, multiplier 1e6) and paddingRect 1e6.
        Chain(c => Blur(c, 1_000_000, 1e6f, 0));
        CheckApply("blur 1e6 x 1e6");
        var auto = Largest(w, out _);
        s.Check(auto.w > 0 && auto.w <= Bound && auto.h <= RH + 2 * Limit, $"effect-margin: MULTI auto padding cut, largest layer {auto} <= {Bound}");
        Chain(c => Blur(c, 32, 0, 1e6f), c => Wave(c, 1e6f));
        CheckApply("blur paddingRect 1e6 -> wave 1e6");
        var rect = Largest(w, out _);
        s.Check(rect.w > 0 && rect.w <= Bound && rect.h <= RH + 2 * Limit, $"effect-margin: MULTI paddingRect cut, largest layer {rect} <= {Bound}");

        // NaN and infinity: 0 and the limit, never a NaN area.
        Chain(c => Wave(c, float.NaN), c => Blur(c, 32, float.NaN, float.PositiveInfinity));
        CheckApply("NaN / infinity");
        var nan = Largest(w, out _);
        s.Check(nan.w > 0 && nan.w <= Bound && nan.h <= RH + 2 * Limit, $"effect-margin: NaN / infinity bounded, largest layer {nan}");

        cmd.BeginEffectChain(R);
        cmd.EndEffectChain();
        CheckApply("chain off");
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
        Native.vlqt_res_release(wave);
        s.Pump(2);
    }
}
