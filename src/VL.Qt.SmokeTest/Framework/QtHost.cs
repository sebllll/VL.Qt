using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using VL.Qt;

/// <summary>
/// The one Qt application of a test run: the thread that owns it, the Qt log, the frame clock of <see cref="Pump"/>.
/// In the test host Qt lives on a dedicated STA thread (like vvvv's main thread) and every scenario runs there through
/// <see cref="Invoke"/>; in the helper executable (Program.Main) the main thread is the Qt thread. Qt is started on first
/// use, so tests that need no GPU (protocol drift) never start it.
/// </summary>
internal static class QtHost
{
    private static readonly object s_syncRoot = new();
    // Separate from s_syncRoot: the log callback takes that one on the Qt thread while EnsureStarted waits for the init.
    private static readonly object s_startLock = new();
    private static readonly StringBuilder s_log = new();
    private static readonly Native.LogFn s_logFn = OnLog;
    private static BlockingCollection<Action>? s_queue;
    private static Thread? s_thread;
    private static double s_time;
    private static bool s_started;

    /// <summary>Result of the first vlqt_app_init (0 = ok) and its error text.</summary>
    public static int InitResult { get; private set; } = int.MinValue;
    public static string InitError { get; private set; } = "";

    /// <summary>Name of the scenario that did not return in time; the Qt thread is blocked by it and no later scenario can run.</summary>
    public static string? BlockedBy { get; set; }

    // WSL with GPU passthrough (WSLg): Present copies to the host compositor (L4e).
    public static bool Wslg => !OperatingSystem.IsWindows() && System.IO.File.Exists("/dev/dxg");
    // WSLg with the xcb platform (the default of an export): Weston's X window manager places windows itself.
    public static bool WslgXcb => Wslg && Environment.GetEnvironmentVariable("QT_QPA_PLATFORM") is null or "xcb";
    // QRhi OpenGL on Mesa (Linux): GL renderer line from qt.rhi.general, null otherwise.
    public static string? MesaGl { get; private set; }
    // The active Qt Quick backend is the software one (logged when the first window loads its scenegraph), e.g. Linux
    // offscreen without QT_QUICK_BACKEND=rhi (tools/test-linux.sh --software, CI / GPU-less systems).
    public static bool QuickSoftware { get; private set; }

    /// <summary>The Qt log of the whole run so far.</summary>
    public static string Log
    {
        get
        {
            lock (s_syncRoot)
                return s_log.ToString();
        }
    }

    /// <summary>Length of the Qt log so far (start of a scenario's part of it).</summary>
    public static int LogLength
    {
        get
        {
            lock (s_syncRoot)
                return s_log.Length;
        }
    }

    public static string LogSince(int start)
    {
        lock (s_syncRoot)
            return start < s_log.Length ? s_log.ToString(start, s_log.Length - start) : "";
    }

    private static void OnLog(int level, IntPtr category, IntPtr message)
    {
        var line = $"[{level}] {Marshal.PtrToStringUTF8(category)}: {Marshal.PtrToStringUTF8(message)}";
        lock (s_syncRoot)
            s_log.AppendLine(line);
        if (line.Contains("qt.scenegraph.general: Loading backend software"))
            QuickSoftware = true;
        if (line.Contains("qt.rhi.general: OpenGL VENDOR:") && line.Contains("Mesa"))
            MesaGl = line[(line.IndexOf("RENDERER:") is var r and >= 0 ? r : 0)..];
    }

    /// <summary>True on the thread that owns Qt.</summary>
    public static bool IsQtThread => s_thread == Thread.CurrentThread;

    /// <summary>Test host: starts the Qt thread and initializes Qt there (once per run).</summary>
    public static void EnsureStarted()
    {
        lock (s_startLock)
        {
            if (s_started)
                return;
            s_started = true;
            s_queue = new BlockingCollection<Action>();
            var ready = new ManualResetEventSlim();
            s_thread = new Thread(() =>
            {
                Init();
                ready.Set();
                foreach (var work in s_queue.GetConsumingEnumerable())
                    work();
            })
            { Name = "Qt (SmokeTest)", IsBackground = true };
            // Like vvvv's main thread; Qt needs STA for OleInitialize.
            if (OperatingSystem.IsWindows())
                s_thread.SetApartmentState(ApartmentState.STA);
            s_thread.Start();
            ready.Wait();
        }
    }

    /// <summary>Helper executable: the calling thread (Main, STA) becomes the Qt thread.</summary>
    public static void StartOnCurrentThread()
    {
        lock (s_startLock)
        {
            if (s_started)
                throw new InvalidOperationException("Qt is already started");
            s_started = true;
            s_thread = Thread.CurrentThread;
        }
        Init();
    }

    private static void Init()
    {
        // Test windows ignore the real cursor (only vlqt_test_send_mouse input), read by QtNative per window (06c).
        SetEnv("VLQT_TEST_SYNTHETIC_INPUT", "1");
        // Controls hover (base style Hovered state): Qt turns it off where the platform has no hover effects
        // (Linux offscreen in CI), so the style test would see Normal there. Same behaviour on every platform.
        SetEnv("QT_QUICK_CONTROLS_HOVER_ENABLED", "1");
        // Linux: the GL renderer/version line (qt.rhi.general) needs QSG_INFO; used to recognize Mesa GL.
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("QSG_INFO") == null)
            SetEnv("QSG_INFO", "1");
        // QtTexture leak checks read ReportLiveDeviceObjects, which needs the debug layer; OwnProcess passes the request
        // as VLQT_SMOKE_DEBUG_LAYER. QtNative reads it with qgetenv (the C runtime's copy of the environment).
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("VLQT_SMOKE_DEBUG_LAYER") == "1")
        {
            SetEnv("QSG_RHI_DEBUG_LAYER", "1");
            _putenv_s("QSG_RHI_DEBUG_LAYER", "1");
        }
        InitResult = Native.vlqt_app_init(Native.NativeDir, s_logFn);
        InitError = InitResult == 0 ? "" : Native.LastError();
    }

    /// <summary>Second vlqt_app_init: must be a no-op returning 0 (core-app check).</summary>
    public static int InitAgain() => Native.vlqt_app_init(Native.NativeDir, s_logFn);

    /// <summary>Runs <paramref name="work"/> on the Qt thread and waits up to <paramref name="timeout"/>; false on timeout.</summary>
    public static bool Invoke(Action work, TimeSpan timeout)
    {
        if (IsQtThread)
        {
            work();
            return true;
        }
        EnsureStarted();
        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim();
        s_queue!.Add(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                done.Set();
            }
        });
        if (!done.Wait(timeout))
            return false;
        error?.Throw();
        return true;
    }

    /// <summary>
    /// Frame clock of <see cref="Pump"/> in seconds. A scenario that drives vlqt_driver_advance itself (clock jumps) sets it
    /// to the latest time it used, so the clock of later scenarios never goes back.
    /// </summary>
    public static double Time
    {
        get => s_time;
        set => s_time = value;
    }

    /// <summary>Advances the frame clock by 1/60 s and processes Qt events, <paramref name="frames"/> times (Qt thread only).</summary>
    public static void Pump(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            s_time += 1.0 / 60.0;
            Native.vlqt_driver_advance(s_time);
            Native.vlqt_app_process_events();
        }
    }

    // Linux: Environment.SetEnvironmentVariable only changes .NET's copy, QtNative reads libc getenv (L2b finding) ->
    // also call libc setenv there. Must run before QtNative reads the variable.
    public static void SetEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsWindows() && setenv(name, value, 1) != 0)
            throw new InvalidOperationException($"setenv {name} failed");
    }

    [DllImport("ucrtbase")] private static extern int _putenv_s([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
    [DllImport("libc")] private static extern int setenv(string name, string value, int overwrite);
}
