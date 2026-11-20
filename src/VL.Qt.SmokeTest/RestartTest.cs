using System;
using NUnit.Framework;
using System.Runtime.InteropServices;
using VL.Qt;

// vvvv crashed on quick repeated F9. QCoreApplication::processEvents also dispatches the Win32 messages of vvvv's
// thread; a WinForms callback in there (the runtime restart) disposed the Renderer, i.e. destroyed a window of the
// running pump, and the pump then ended the frame of that window (PerfMonitor) through a dangling pointer.
// Part 1 (Windows): a message-only window posted to itself destroys the pump's window from inside processEvents and
// creates the next one at once (a restart), several rounds without waiting. Part 2: restarts while a new shader text
// with one more uniform still bakes, in the orders vvvv may dispose in, with the resource workers shut down.
// Passes when nothing crashes.
[TestFixture, NonParallelizable]
public static class RestartTest
{
    private const uint Effect = 10;
    private const int rounds = 24;
    private const uint wmApp = 0x8000;
    private const int gwlpWndProc = -4;
    private static readonly IntPtr s_hwndMessage = new(-3);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string cls, string? name, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static string Source(bool thickness, int round) =>
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf {\n" +
        "    mat4 qt_Matrix;\n" +
        "    float qt_Opacity;\n" +
        "    float time;\n" +
        (thickness ? "    float thickness;\n" : "") +
        "};\n" +
        "void main()\n" +
        "{\n" +
        "    float s = step(0.5, fract(qt_TexCoord0.x * 8.0 + time));\n" +
        (thickness ? "    s *= step(thickness, fract(qt_TexCoord0.y * 8.0));\n" : "") +
        "    fragColor = vec4(s, s, s, 1.0) * qt_Opacity;\n" +
        "}\n" +
        "// round " + round + "\n";

    // A window with one ShaderEffect on shader text <paramref name="shader"/> (Ready or still baking).
    private static IntPtr Scene(CommandWriter cmd, ulong shader, bool thickness)
    {
        var w = Native.vlqt_window_create(400, 300, "VL.Qt shader restart test");
        Native.vlqt_window_show(w);
        cmd.Reset();
        cmd.Create(Effect, Protocol.Type.ShaderEffect);
        cmd.SetPlace(Effect, 0, 0, 400, 300);
        cmd.SetU64(Effect, Protocol.Prop.FragmentShader, shader);
        cmd.BeginUniforms(Effect, Protocol.UniformSet.Clear);
        cmd.UniformF32("time", 0.25f);
        if (thickness)
            cmd.UniformF32("thickness", 0.5f);
        cmd.EndUniforms();
        cmd.Reparent(Effect, 0, 1000);
        Native.Apply(w, cmd.EndFrame());
        return w;
    }

    private static ulong Load(bool thickness, int round)
    {
        Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
        Native.vlqt_res_load_shader_source(id, Source(thickness, round), Protocol.ShaderStage.Fragment);
        return id;
    }

    /// <summary>Restart of the app.</summary>
    [Test, Category("restart")]
    public static void Restart() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        if (OperatingSystem.IsWindows())
            RunReentrant(s);
        RunBaking(s);
    }

    // Part 1: the window of the pump is destroyed (and the next one created) inside vlqt_app_process_events_for.
    private static void RunReentrant(Scenario s)
    {
        var cmd = new CommandWriter();
        var current = IntPtr.Zero;
        var shader = 0UL;
        var restarts = 0;
        var round = 0;
        IntPtr previous = IntPtr.Zero;
        WndProc proc = (hwnd, msg, wParam, lParam) =>
        {
            if (msg != wmApp)
                return CallWindowProcW(previous, hwnd, msg, wParam, lParam);
            // The restart: old scene gone, the next one built at once (its shader text still bakes).
            Native.vlqt_window_destroy(current);
            Native.vlqt_res_release(shader);
            shader = Load(true, 1000 + round);
            current = Scene(cmd, shader, true);
            restarts++;
            return IntPtr.Zero;
        };
        var hwnd = CreateWindowExW(0, "Message", null, 0, 0, 0, 0, 0, s_hwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        s.Check(hwnd != IntPtr.Zero, "message-only window for the reentrant restart");
        if (hwnd == IntPtr.Zero)
            return;
        previous = SetWindowLongPtrW(hwnd, gwlpWndProc, Marshal.GetFunctionPointerForDelegate(proc));

        shader = Load(false, 1000);
        current = Scene(cmd, shader, false);
        var windows = new IntPtr[1];
        for (round = 0; round < rounds; round++)
        {
            windows[0] = current;
            PostMessageW(hwnd, wmApp, IntPtr.Zero, IntPtr.Zero);
            Native.vlqt_app_process_events_for(windows, 1); // destroys windows[0] from inside, then ends its frame
        }
        windows[0] = current;
        Native.vlqt_app_process_events_for(windows, 1);
        s.Check(restarts == rounds, $"{restarts} of {rounds} restarts ran inside the pump");
        s.Check(true, "window destroyed inside vlqt_app_process_events_for, no crash");

        Native.vlqt_window_destroy(current);
        Native.vlqt_res_release(shader);
        SetWindowLongPtrW(hwnd, gwlpWndProc, previous);
        DestroyWindow(hwnd);
        GC.KeepAlive(proc);
        Native.vlqt_res_shutdown();
    }

    // Part 2: restarts while a new shader text with one more uniform bakes.
    private static void RunBaking(Scenario s)
    {
        var cmd = new CommandWriter();
        for (int round = 0; round < rounds; round++)
        {
            var a = Load(false, round);
            s.Check(Native.vlqt_res_wait(a, 20000) == Protocol.ResState.Ready, $"round {round}: text A Ready");
            var w = Scene(cmd, a, false);
            s.Pump(2);

            // Live edit: text B with one more uniform, set while it bakes.
            var b = Load(true, round);
            cmd.Reset();
            cmd.SetU64(Effect, Protocol.Prop.FragmentShader, b);
            cmd.BeginUniforms(Effect, Protocol.UniformSet.Clear);
            cmd.UniformF32("time", 0.25f);
            cmd.UniformF32("thickness", 0.5f);
            cmd.EndUniforms();
            Native.Apply(w, cmd.EndFrame());
            s.Pump(round % 2); // with or without a frame between the edit and the restart

            switch (round % 3)
            {
                case 0:
                    cmd.Reset();
                    cmd.Destroy(Effect);
                    Native.Apply(w, cmd.EndFrame());
                    Native.vlqt_res_release(a);
                    Native.vlqt_res_release(b);
                    Native.vlqt_window_destroy(w);
                    break;
                case 1:
                    Native.vlqt_window_destroy(w);
                    Native.vlqt_res_release(b);
                    Native.vlqt_res_release(a);
                    break;
                default:
                    Native.vlqt_res_release(b);
                    Native.vlqt_res_release(a);
                    cmd.Reset();
                    cmd.Destroy(Effect);
                    Native.Apply(w, cmd.EndFrame());
                    Native.vlqt_window_destroy(w);
                    break;
            }
            Native.vlqt_res_shutdown();
            s.Pump(1);
        }
        s.Check(true, $"{rounds} restarts while a new shader text bakes, no crash");
    }
}
