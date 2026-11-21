using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// L3a: opens one Qt window from C# (rectangle + text), pumps it for a while, grabs a PNG.
// Usage: VL.Qt.Probe [--seconds N] [--grab <png>]   (N = 0: run until the window is closed)
internal static class Program
{
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnLog(int level, byte* category, byte* message)
    {
        // Called by QtNative on any thread; must not throw.
        try
        {
            Console.WriteLine($"[{level}] {Marshal.PtrToStringUTF8((IntPtr)category)}: {Marshal.PtrToStringUTF8((IntPtr)message)}");
        }
        catch (Exception)
        {
        }
    }

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
        QtNativeLibrary.EnsureLoaded();
        Console.WriteLine($"native dir:  {QtNativeLibrary.Directory}");
        Native.Result initResult;
        unsafe
        {
            initResult = Native.vlqt_app_init(QtNativeLibrary.Directory, &OnLog);
        }
        if (initResult != Native.Result.Ok)
            return Fail("vlqt_app_init: " + Native.LastError());

        var w = Native.vlqt_window_create(float.NaN, float.NaN, 800, 600, "VL.Qt Probe (L3a)", 0);
        if (w == SceneHandle.Null)
            return Fail("vlqt_window_create: " + Native.LastError());

        // #202020 background, orange rounded rectangle, white text (DIP, top-left).
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(1, Protocol.ElementType.Rectangle);
        cmd.SetVec2(1, Protocol.PropertyId.Size, 300, 200);
        cmd.SetI32(1, Protocol.PropertyId.Anchor, 0x1 | 0x20); // TopLeft; position = Transform translation
        cmd.SetMat4(1, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation(100, 100, 0));
        cmd.SetColor(1, Protocol.PropertyId.Color, 1, 0.5f, 0, 1);
        cmd.SetF32(1, Protocol.PropertyId.Radius, 20);
        cmd.Reparent(1, 0, 0);
        cmd.Create(2, Protocol.ElementType.Text);
        cmd.SetVec2(2, Protocol.PropertyId.Size, 600, 80);
        cmd.SetI32(2, Protocol.PropertyId.Anchor, 0x1 | 0x20);
        cmd.SetMat4(2, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation(100, 350, 0));
        cmd.SetString(2, Protocol.PropertyId.Text, "Hello from VL.Qt");
        cmd.SetF32(2, Protocol.PropertyId.FontSize, 48);
        cmd.SetColor(2, Protocol.PropertyId.Color, 1, 1, 1, 1);
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
            Native.vlqt_app_advance_animations(sw.Elapsed.TotalSeconds);
            Native.vlqt_app_process_events_for(null, 0);
            frames++;
            if (!grabbed && sw.Elapsed.TotalSeconds >= 1)
            {
                grabbed = true;
                var g = NativeTest.vlqt_test_window_grab(w, grab);
                Console.WriteLine(g == 0 ? $"grab:        {grab}" : $"grab failed ({g}): {Native.LastError()}");
            }
            if (Native.vlqt_window_take_close_request(w) != 0)
                break;
            Thread.Sleep(16);
        }
        var visible = Native.vlqt_window_is_visible(w) != 0;
        Native.vlqt_scene_destroy(w);
        Console.WriteLine($"done:        {frames} frames in {sw.Elapsed.TotalSeconds:F1} s, window visible {visible}");
        return grabbed ? 0 : Fail("window closed before the grab");
    }

    private static int Fail(string what)
    {
        Console.WriteLine("FAIL " + what);
        return 1;
    }
}
