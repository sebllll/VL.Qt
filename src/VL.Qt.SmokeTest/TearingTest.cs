using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NUnit.Framework;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// Manual scenario (T0b), explicit NUnit test (category tearing). Its flags come from the run parameter TearingArgs:
/// <c>[--windows N] [--interval 0|1] [--fullscreen] [--pump-hz N] [--seconds N]</c>. N windows with a vertical bar sweeping fast across the whole width (tearing shows as a broken
/// bar), pumped like the vvvv main loop. Once per second a line per window (fps = swaps/s, present and pump p50/p99 from
/// vlqt_perf_sample, refresh rate of the screen) and one for the whole host pump; at the end a TEARING SUMMARY line per
/// window over the run without the first second (warm-up). Plausibility warnings: fps ~ 0, refresh n/a, locked session.
/// Windows: the display is kept awake (SetThreadExecutionState) and a locked session is detected (OpenInputDesktop).
/// </summary>
[TestFixture, NonParallelizable]
public static class TearingTest
{
    private sealed record Options(int Windows, int Interval, bool Fullscreen, int PumpHz, int Seconds);

    private const float BarFraction = 0.05f; // bar width relative to the window width
    private const float SweepSeconds = 0.6f; // one way across the window, ~3000 px/s on 1920 px: a clear tear line

    /// <summary>Manual swap/tearing measurement; the TEARING lines are in the test output.</summary>
    [Test, Explicit("manual measurement (#90)"), Category("tearing")]
    public static void Tearing() => Scenario.Run(s => s.Check(Run(SmokeParameters.TearingArgs) == 0, "tearing run"));

    private static int Run(string[] args)
    {
        if (!TryParse(args, out var o, out var error))
        {
            Console.WriteLine("TEARING " + error);
            Console.WriteLine("usage (parameter TearingArgs): [--windows N (1)] [--interval 0|1 (1)] [--fullscreen] [--pump-hz N (60, 0 = as fast as possible)] [--seconds N (20, 0 = until a window is closed / Ctrl+C)]");
            return 2;
        }

        var stop = false;
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop = true; };
        Console.CancelKeyPress += onCancel;
        using var timer = PumpTest.HighResTimer(); // 1 ms timer resolution like vvvv (Windows)
        var keepAwake = OperatingSystem.IsWindows() && Win.KeepDisplayOn(true);
        var windows = new List<IntPtr>();
        try
        {
            var display = CheckDisplay();
            Console.WriteLine(FormattableString.Invariant($"TEARING start windows={o.Windows} interval={o.Interval} fullscreen={(o.Fullscreen ? 1 : 0)} pump_hz={o.PumpHz} seconds={o.Seconds} display={display}{(keepAwake ? " keep-awake=on" : "")}"));
            if (display == "locked")
                Console.WriteLine("TEARING WARNING session is locked: the lock screen covers the windows, the measurement is not meaningful");

            var titles = new string[o.Windows];
            var sizes = new (float W, float H)[o.Windows];
            var monitors = o.Fullscreen && OperatingSystem.IsWindows() ? Win.Monitors() : new List<(float X, float Y)>();
            float width = Math.Clamp(1800f / o.Windows - 10, 320, 800), height = 360;
            for (int i = 0; i < o.Windows; i++)
            {
                titles[i] = $"VL.Qt tearing {i}";
                float x = float.NaN, y = float.NaN;
                if (o.Fullscreen && monitors.Count > 0)
                {
                    // Window i on screen i if there is one, else on the primary: the fullscreen goes to the screen with the
                    // largest share of the window.
                    var m = monitors[i < monitors.Count ? i : 0];
                    (x, y) = (m.X + 100, m.Y + 100);
                }
                else if (!o.Fullscreen)
                    (x, y) = (10 + i * (width + 10), 60);
                var w = Native.vlqt_window_create_at(x, y, (int)width, (int)height, titles[i], o.Interval);
                if (w == IntPtr.Zero)
                {
                    Console.WriteLine("TEARING window_create failed: " + Native.LastError());
                    return 1;
                }
                windows.Add(w);
                sizes[i] = (width, height);
                if (o.Fullscreen)
                    Native.vlqt_window_set_mode(w, Protocol.WindowMode.Borderless | Protocol.WindowMode.Fullscreen, Protocol.WindowMode.Borderless | Protocol.WindowMode.Fullscreen);
                var cmd = new CommandWriter();
                cmd.ClearColor(0.06f, 0.06f, 0.06f, 1);
                cmd.Create(1, Protocol.Type.Rectangle);
                cmd.SetColor(1, Protocol.Prop.Color, 1, 1, 1, 1);
                cmd.Reparent(1, 0, 0);
                var rc = Native.Apply(w, cmd.EndFrame());
                if (rc != 0)
                {
                    Console.WriteLine($"TEARING apply failed ({rc}): " + Native.LastError());
                    return 1;
                }
                Native.vlqt_window_show(w);
            }

            return Loop(o, windows, titles, sizes, display, () => stop);
        }
        finally
        {
            foreach (var w in windows) Native.vlqt_window_destroy(w);
            for (int i = 0; i < 2; i++) Native.vlqt_app_process_events();
            if (keepAwake) Win.KeepDisplayOn(false);
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static int Loop(Options o, List<IntPtr> windows, string[] titles, (float W, float H)[] sizes, string display0, Func<bool> stopped)
    {
        var n = windows.Count;
        var cmd = new CommandWriter();
        var poll = new byte[4096];
        var inv = CultureInfo.InvariantCulture;
        // Per second (cleared after each line) and for the run after the warm-up second.
        var secPresent = Enumerable.Range(0, n).Select(_ => new List<float>()).ToArray();
        var secPump = Enumerable.Range(0, n).Select(_ => new List<float>()).ToArray();
        var secHost = new List<float>();
        var secSwaps = new int[n];
        var totPresent = Enumerable.Range(0, n).Select(_ => new List<float>()).ToArray();
        var totPump = Enumerable.Range(0, n).Select(_ => new List<float>()).ToArray();
        var totHost = new List<float>();
        var totSwaps = new long[n];
        var refresh = Enumerable.Repeat("n/a", n).ToArray();
        var warnedZero = new bool[n];
        var display = display0;
        var displayEver = new HashSet<string> { display0 };

        long start = Stopwatch.GetTimestamp();
        long period = o.PumpHz > 0 ? Stopwatch.Frequency / o.PumpHz : 0;
        long deadline = start;
        int curSec = 0;
        double now = 0;
        double lastSample = 0;
        int frame = 0;

        while (!stopped())
        {
            if (period > 0)
            {
                deadline += period;
                PumpTest.ForeignMessageLoopUntil(deadline);
                if (Stopwatch.GetTimestamp() - deadline > period) deadline = Stopwatch.GetTimestamp(); // do not catch up after a stall
            }
            else
                PumpTest.ForeignMessageLoopUntil(0); // only drains the message queue

            now = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (o.Seconds > 0 && now >= o.Seconds) break;

            // One host pump = apply for every window, advance, process events of all windows (like QtApp).
            var h0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                var (w, h) = sizes[i];
                var barW = w * BarFraction;
                var phase = (now / (2 * SweepSeconds)) % 1.0;
                var tri = (float)Math.Abs(phase * 2 - 1); // 0..1..0
                cmd.Reset();
                cmd.SetPlace(1, tri * (w - barW), 0, barW, h);
                Native.Apply(windows[i], cmd.EndFrame());
            }
            Native.vlqt_driver_advance(now);
            Native.vlqt_app_process_events();
            var hostMs = (float)Stopwatch.GetElapsedTime(h0).TotalMilliseconds;
            now = Stopwatch.GetElapsedTime(start).TotalSeconds;
            frame++;

            // Window sizes (fullscreen changes them) and a closed window; polled seldom, the poll is not part of the measurement.
            if (frame % 20 == 1)
                for (int i = 0; i < n; i++)
                {
                    if (Native.vlqt_window_take_close_request(windows[i]) == 1) return Finish();
                    if (NativeCalls.Poll(windows[i], poll) > 0 && WindowPlacement.TryReadGeometry(poll, out var g) && g.Bounds.Width >= 1 && g.Bounds.Height >= 1)
                        sizes[i] = (g.Bounds.Width, g.Bounds.Height);
                }

            var sec = (int)now;
            if (sec != curSec)
            {
                FlushSecond(curSec);
                curSec = sec;
            }

            var warm = sec >= 1; // the first second (window creation, first presents, shader setup) is warm-up
            for (int i = 0; i < n; i++)
            {
                if (Native.vlqt_window_get_perf_sample(windows[i], out var s) != 0) continue;
                secPump[i].Add(s.pump);
                if (s.swaps > 0) secPresent[i].Add(s.present);
                secSwaps[i] += s.swaps;
                if (!warm) continue;
                totPump[i].Add(s.pump);
                if (s.swaps > 0) totPresent[i].Add(s.present);
                totSwaps[i] += s.swaps;
            }
            secHost.Add(hostMs);
            if (warm) totHost.Add(hostMs);
            lastSample = now;
        }
        return Finish();

        void FlushSecond(int sec)
        {
            display = CheckDisplay();
            displayEver.Add(display);
            for (int i = 0; i < n; i++)
            {
                refresh[i] = Refresh(windows[i], titles[i]);
                var fps = secSwaps[i];
                Console.WriteLine(string.Format(inv, "TEARING t={0}s win={1} fps={2:F1} present_p50={3:F2} present_p99={4:F2} pump_p50={5:F2} pump_p99={6:F2} refresh={7}{8}",
                    sec + 1, i, (double)fps, Percentile(secPresent[i], 0.50), Percentile(secPresent[i], 0.99),
                    Percentile(secPump[i], 0.50), Percentile(secPump[i], 0.99), refresh[i], sec == 0 ? " (warm-up)" : ""));
                if (fps < 1 && !warnedZero[i])
                {
                    warnedZero[i] = true;
                    Console.WriteLine($"TEARING WARNING win={i}: fps ~ 0 (display off or locked, window hidden or covered?)");
                }
                secPresent[i].Clear();
                secPump[i].Clear();
                secSwaps[i] = 0;
            }
            Console.WriteLine(string.Format(inv, "TEARING t={0}s hostpump_p50={1:F2} hostpump_p99={2:F2} display={3}", sec + 1, Percentile(secHost, 0.50), Percentile(secHost, 0.99), display));
            if (display == "locked")
                Console.WriteLine("TEARING WARNING session is locked: the measurement is not meaningful");
            secHost.Clear();
        }

        int Finish()
        {
            var measured = Math.Max(lastSample - 1.0, 0);
            var displayEnd = displayEver.Contains("locked") ? "locked" : displayEver.Contains("unknown") ? "unknown" : "ok";
            for (int i = 0; i < n; i++)
            {
                refresh[i] = Refresh(windows[i], titles[i]);
                var fps = measured > 0 ? totSwaps[i] / measured : 0;
                Console.WriteLine(string.Format(inv,
                    "TEARING SUMMARY win={0} windows={1} interval={2} fullscreen={3} fps={4:F1} present_p50={5:F2} present_p99={6:F2} pump_p50={7:F2} pump_p99={8:F2} hostpump_p50={9:F2} hostpump_p99={10:F2} refresh={11} display={12}",
                    i, n, o.Interval, o.Fullscreen ? 1 : 0, fps, Percentile(totPresent[i], 0.50), Percentile(totPresent[i], 0.99),
                    Percentile(totPump[i], 0.50), Percentile(totPump[i], 0.99), Percentile(totHost, 0.50), Percentile(totHost, 0.99),
                    refresh[i], displayEnd));
                if (fps < 1)
                    Console.WriteLine($"TEARING WARNING win={i}: fps ~ 0, no frames after the warm-up second (display off or locked, run too short?)");
                if (refresh[i] == "n/a")
                    Console.WriteLine($"TEARING WARNING win={i}: refresh rate n/a (display off or locked?), fps cannot be compared with the refresh rate");
            }
            if (displayEnd == "locked")
                Console.WriteLine("TEARING WARNING session was locked during the run: the measurement is not meaningful");
            return 0;
        }
    }

    // Refresh rate of the screen of the window: Windows API (the real mode of the monitor), else 1000 / budget_ms of the
    // window stats (QtNative falls back to 60 Hz there, so that is the second choice). n/a = unknown.
    private static string Refresh(IntPtr w, string title)
    {
        if (OperatingSystem.IsWindows() && Win.RefreshHz(title) is { } hz and > 1)
            return hz.ToString("F1", CultureInfo.InvariantCulture);
        if (!OperatingSystem.IsWindows() && Native.vlqt_window_get_perf_stats(w, out var st) == 0 && st.budget_ms > 0)
            return (1000.0 / st.budget_ms).ToString("F1", CultureInfo.InvariantCulture);
        return "n/a";
    }

    // ok | locked | unknown (non-Windows, or the check itself failed).
    private static string CheckDisplay() => OperatingSystem.IsWindows() ? Win.InputDesktopState() : "unknown";

    // Nearest rank, like vlqt_perf_stats.
    private static double Percentile(List<float> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static bool TryParse(string[] args, out Options o, out string error)
    {
        o = new Options(1, 1, false, 60, 20);
        string err = "";
        int windows = 1, interval = 1, pumpHz = 60, seconds = 20;
        bool fullscreen = false;
        for (int i = 0; i < args.Length; i++)
        {
            bool Int(string name, int min, int max, out int value)
            {
                value = 0;
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
                {
                    err = $"{name} needs an integer from {min} to {max}";
                    return false;
                }
                i++;
                return true;
            }
            switch (args[i])
            {
                case "--windows": if (!Int("--windows", 1, 8, out windows)) { error = err; return false; } break;
                case "--interval": if (!Int("--interval", 0, 1, out interval)) { error = err; return false; } break;
                case "--pump-hz": if (!Int("--pump-hz", 0, 10000, out pumpHz)) { error = err; return false; } break;
                case "--seconds": if (!Int("--seconds", 0, 86400, out seconds)) { error = err; return false; } break;
                case "--fullscreen": fullscreen = true; break;
            }
        }
        o = new Options(windows, interval, fullscreen, pumpHz, seconds);
        error = "";
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static class Win
    {
        private const uint EsContinuous = 0x80000000, EsDisplayRequired = 0x00000002;

        [StructLayout(LayoutKind.Sequential)] private struct Rc { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size; public Rc Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rc rect, IntPtr data);

        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string title);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr m, ref MonitorInfoEx mi);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, byte[] devMode);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(IntPtr obj, int index, byte[] info, uint length, out uint needed);

        // Keeps the display on while the thread lives; false = the call failed.
        public static bool KeepDisplayOn(bool on) => SetThreadExecutionState(on ? EsContinuous | EsDisplayRequired : EsContinuous) != 0;

        // Top-left of every monitor in native pixels (Qt 6 keeps a screen's top-left there), primary first.
        public static List<(float X, float Y)> Monitors()
        {
            var list = new List<(float X, float Y)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref Rc r, IntPtr d) => { list.Add((r.Left, r.Top)); return true; }, IntPtr.Zero);
            return list.OrderBy(m => m.X != 0 || m.Y != 0).ThenBy(m => m.X).ThenBy(m => m.Y).ToList();
        }

        // Current display frequency of the monitor the window is on (DEVMODEW, dmDisplayFrequency at offset 184); null = unknown.
        public static int? RefreshHz(string title)
        {
            var hwnd = FindWindowW(null, title);
            if (hwnd == IntPtr.Zero) return null;
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref mi)) return null;
            var dm = new byte[220];
            BitConverter.TryWriteBytes(dm.AsSpan(68, 2), (ushort)dm.Length); // dmSize
            if (!EnumDisplaySettingsW(mi.Device, -1 /* ENUM_CURRENT_SETTINGS */, dm)) return null;
            return (int)BitConverter.ToUInt32(dm, 184);
        }

        // The input desktop is "Default" for a normal session; the lock screen runs on "Winlogon" and cannot be opened
        // (access denied) from the user's session.
        public static string InputDesktopState()
        {
            var desk = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
            if (desk == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == 5 /* ERROR_ACCESS_DENIED */ ? "locked" : "unknown";
            try
            {
                var buf = new byte[256];
                if (!GetUserObjectInformationW(desk, 2 /* UOI_NAME */, buf, (uint)buf.Length, out _)) return "unknown";
                var name = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
                return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ? "ok" : "locked";
            }
            finally { CloseDesktop(desk); }
        }
    }
}
