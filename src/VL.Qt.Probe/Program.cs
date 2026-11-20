using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using VL.Qt;

// L3a: opens one Qt window from C# (rectangle + text), pumps it for a while, grabs a PNG.
// Usage: VL.Qt.Probe [--seconds N] [--grab <png>]   (N = 0: run until the window is closed)
internal static class Program
{
    private static readonly Native.LogFn s_logFn = (level, cat, msg) =>
        Console.WriteLine($"[{level}] {Marshal.PtrToStringUTF8(cat)}: {Marshal.PtrToStringUTF8(msg)}");

    [STAThread] // Windows: Qt's OleInitialize needs an STA thread; no effect on Linux.
    private static int Main(string[] args)
    {
        double seconds = 10;
        string grab = Path.Combine(AppContext.BaseDirectory, "probe.png");
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--seconds") seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--grab") grab = Path.GetFullPath(args[i + 1]);
        }

        Console.WriteLine($"VL.Qt.Probe: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        Console.WriteLine($"VL.Qt.dll:   {typeof(Native).Assembly.Location}");
        Native.EnsureLoaded();
        Console.WriteLine($"native dir:  {Native.NativeDir}");
        if (Native.vlqt_app_init(Native.NativeDir, s_logFn) != 0)
            return Fail("vlqt_app_init: " + Native.LastError());

        var w = Native.vlqt_window_create(800, 600, "VL.Qt Probe (L3a)");
        if (w == IntPtr.Zero)
            return Fail("vlqt_window_create: " + Native.LastError());

        // #202020 background, orange rounded rectangle, white text (DIP, top-left).
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(1, Protocol.Type.Rectangle);
        cmd.SetVec2(1, Protocol.Prop.Size, 300, 200);
        cmd.SetI32(1, Protocol.Prop.Anchor, 0x1 | 0x20); // TopLeft; position = Transform translation
        cmd.SetMat4(1, Protocol.Prop.Transform, Stride.Core.Mathematics.Matrix.Translation(100, 100, 0));
        cmd.SetColor(1, Protocol.Prop.Color, 1, 0.5f, 0, 1);
        cmd.SetF32(1, Protocol.Prop.Radius, 20);
        cmd.Reparent(1, 0, 0);
        cmd.Create(2, Protocol.Type.Text);
        cmd.SetVec2(2, Protocol.Prop.Size, 600, 80);
        cmd.SetI32(2, Protocol.Prop.Anchor, 0x1 | 0x20);
        cmd.SetMat4(2, Protocol.Prop.Transform, Stride.Core.Mathematics.Matrix.Translation(100, 350, 0));
        cmd.SetString(2, Protocol.Prop.Text, "Hello from VL.Qt");
        cmd.SetF32(2, Protocol.Prop.FontSize, 48);
        cmd.SetColor(2, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(2, 0, 1);
        var rc = Native.Apply(w, cmd.EndFrame());
        if (rc != 0)
            return Fail($"apply ({rc}): " + Native.LastError());
        Native.vlqt_window_show(w);

        // ~60 Hz pump on wall-clock time; grab after ~1 s, stop after `seconds` or on close.
        var sw = Stopwatch.StartNew();
        bool grabbed = false;
        int frames = 0;
        while (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds)
        {
            Native.vlqt_driver_advance(sw.Elapsed.TotalSeconds);
            Native.vlqt_app_process_events();
            frames++;
            if (!grabbed && sw.Elapsed.TotalSeconds >= 1)
            {
                grabbed = true;
                var g = Native.vlqt_window_grab(w, grab);
                Console.WriteLine(g == 0 ? $"grab:        {grab}" : $"grab failed ({g}): {Native.LastError()}");
            }
            if (Native.vlqt_window_take_close_request(w) != 0)
                break;
            Thread.Sleep(16);
        }
        var visible = Native.vlqt_window_is_visible(w) != 0;
        Native.vlqt_window_destroy(w);
        Console.WriteLine($"done:        {frames} frames in {sw.Elapsed.TotalSeconds:F1} s, window visible {visible}");
        return grabbed ? 0 : Fail("window closed before the grab");
    }

    private static int Fail(string what)
    {
        Console.WriteLine("FAIL " + what);
        return 1;
    }
}
