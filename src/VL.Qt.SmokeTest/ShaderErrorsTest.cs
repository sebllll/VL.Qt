using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt.Wire;
using VL.Qt;

// Bake errors, the bake cache and the rename warning of shader_resource.cpp (NFX-23), read from the resource events,
// not from pixels: no window, no GPU. A broken GLSL source ends Failed with one error line per glslang error,
// "fragment code, line <n>: <text>". The same source a second time comes from the cache (errors are cached too): its
// events are in the first poll after the wait, and no glslang run is needed. A uniform named like a GLSL builtin
// ("length") gives a rename warning; the cache keeps the warning for the next load of the same source.
[TestFixture, NonParallelizable]
public static class ShaderErrorsTest
{
    /// <summary>Shader bake errors, bake cache and rename warning, read from events.</summary>
    [Test, Category("shader-errors")]
    public static void ShaderErrors() => Scenario.Run(Run);

    // Two faulty lines (an undeclared identifier foo on line 9, a vec3 assigned to a float on line 10); glslang reports the first.
    private const string BrokenSource =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf {\n" +
        "    mat4 qt_Matrix;\n" +
        "    float qt_Opacity;\n" +
        "};\n" +
        "void main() {\n" +
        "    fragColor = vec4(foo, 0.0, 0.0, 1.0) * qt_Opacity;\n" +
        "    fragColor.g = bar;\n" +
        "}\n";

    private const string WithLength =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf {\n" +
        "    mat4 qt_Matrix;\n" +
        "    float qt_Opacity;\n" +
        "    float length;\n" +
        "};\n" +
        "void main() {\n" +
        "    fragColor = vec4(length, 0.0, 0.0, 1.0) * qt_Opacity;\n" +
        "}\n";

    private sealed record Res(ushort Ev, ulong Id, string Text);

    private static void Run(Scenario s)
    {
        var events = new List<Res>();
        var buf = new byte[64 * 1024];
        // Everything the status queue holds right now (one pump of the resources).
        int Poll()
        {
            var n = NativeCalls.ResPoll(buf);
            var count = 0;
            for (int at = 0; at < n; count++)
            {
                var len = BitConverter.ToUInt16(buf, at + 2);
                events.Add(new Res(BitConverter.ToUInt16(buf, at), BitConverter.ToUInt64(buf, at + 4), Encoding.UTF8.GetString(buf, at + 12, len - 8)));
                at += (4 + len + 3) & ~3;
            }
            return count;
        }
        IEnumerable<Res> Of(ulong id, ushort ev) => events.Where(e => e.Id == id && e.Ev == ev);

        // Loads the text and waits; the events of the load are in the first poll after the wait.
        ulong Load(string code, out int state, out double ms)
        {
            Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var id);
            var sw = Stopwatch.StartNew();
            var rc = Native.vlqt_shader_load_source(id, code, Protocol.ShaderStage.Fragment);
            state = rc == 0 ? Native.vlqt_res_wait(id, 20000) : Protocol.ResourceState.Failed;
            ms = sw.Elapsed.TotalMilliseconds;
            Poll();
            return id;
        }
        var ids = new List<ulong>();

        // --- broken source: one line per glslang error with a line number ---
        var first = Load(BrokenSource, out var state1, out var bakeMs);
        ids.Add(first);
        s.Check(state1 == Protocol.ResourceState.Failed, $"broken source -> Failed ({state1})");
        var errors1 = Of(first, Protocol.ResourceEventId.Error).ToList();
        s.Check(errors1.Count == 1, $"one error event for the resource ({errors1.Count})");
        var text1 = errors1.Count > 0 ? errors1[^1].Text : "";
        var lines1 = text1.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        s.Info($"error text: {text1.Replace("\n", " | ")}");
        s.Check(lines1.Length > 0 && lines1.All(l => Regex.IsMatch(l, @"^fragment code, line \d+: \S")),
            $"every line is 'fragment code, line <n>: <text>' ({lines1.Length} lines: {text1.Replace("\n", " | ")})");
        s.Check(lines1.Any(l => l.StartsWith("fragment code, line 9:") && l.Contains("foo")), "the error for foo names line 9");
        // glslang stops after the first error of a stage ("compilation terminated"): one line per error it reports, none for the
        // summary or the follow-up line. A second error in the source does not show up today.
        s.Check(lines1.Length == 1, $"one line per glslang error, no summary and no 'compilation terminated' line ({lines1.Length})");
        s.Check(!text1.Contains("compilation error") && !text1.Contains("terminated") && !text1.Contains("ERROR:"), "glslang's own wording is cleaned off");

        // --- the same source again: cache hit, same event in the first poll after the wait, no bake ---
        var second = Load(BrokenSource, out var state2, out var cachedMs);
        ids.Add(second);
        s.Check(state2 == Protocol.ResourceState.Failed, $"the same source again -> Failed ({state2})");
        var errors2 = Of(second, Protocol.ResourceEventId.Error).ToList();
        s.Check(errors2.Count == 1 && errors2[0].Text == text1, $"the same error event within one pump, same text ({errors2.Count} events)");
        // Timing is a hint (shared GPU/CPU machine), the events above are the check.
        if (cachedMs >= bakeMs * 0.6 && cachedMs >= 30)
            s.Warn($"the second load of the broken source was not faster than the first (first {bakeMs:F1} ms, again {cachedMs:F1} ms)");
        else
            s.Info($"bake {bakeMs:F1} ms, cache {cachedMs:F1} ms");

        // A source that differs by one character is another key: it bakes again and gives its own text.
        var third = Load(BrokenSource.Replace("foo", "fox"), out var state3, out _);
        ids.Add(third);
        var text3 = Of(third, Protocol.ResourceEventId.Error).LastOrDefault()?.Text ?? "";
        s.Check(state3 == Protocol.ResourceState.Failed && text3.Contains("fox") && !text3.Contains("foo"), $"a changed source is not taken from the cache ('{text3.Replace("\n", " | ")}')");

        // --- the rename warning: Ready, with a warning that names the uniform; the cache hit keeps the warning ---
        var named = Load(WithLength, out var stateNamed, out _);
        ids.Add(named);
        var warnings = Of(named, Protocol.ResourceEventId.Warning).ToList();
        s.Check(stateNamed == Protocol.ResourceState.Ready, $"uniform 'length' -> Ready ({stateNamed})");
        s.Check(warnings.Count == 1 && warnings[0].Text.Contains("'length'"), $"one rename warning that names 'length' ('{string.Join(" | ", warnings.Select(x => x.Text))}')");
        s.Check(!Of(named, Protocol.ResourceEventId.Error).Any(), "no error event for the renamed uniform");
        var namedAgain = Load(WithLength, out var stateAgain, out _);
        ids.Add(namedAgain);
        var warningsAgain = Of(namedAgain, Protocol.ResourceEventId.Warning).ToList();
        s.Check(stateAgain == Protocol.ResourceState.Ready && warningsAgain.Count == 1 && warningsAgain[0].Text == warnings.FirstOrDefault()?.Text,
            $"the cache hit gives the same warning again ({warningsAgain.Count} events)");

        // An ordinary source gives no warning.
        var plain = Load(WithLength.Replace("length", "amount"), out var statePlain, out _);
        ids.Add(plain);
        s.Check(statePlain == Protocol.ResourceState.Ready && !Of(plain, Protocol.ResourceEventId.Warning).Any() && !Of(plain, Protocol.ResourceEventId.Error).Any(),
            $"an ordinary uniform name gives no warning and no error ({statePlain})");

        foreach (var id in ids)
            Native.vlqt_res_release(id);
        s.Pump(2);
    }
}
