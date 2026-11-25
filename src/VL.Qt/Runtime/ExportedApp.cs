using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using VL.Core;

namespace VL.Qt
{
    /// <summary>
    /// The one place that knows the vvvv export hosts: lifetime of exported apps (L7b). AppHost has no API to end an exported
    /// app: its main loop runs while AppHost.KeepAlive() is held (KeepAppAlive) or, in the WinForms host, a Form is open (IL of
    /// vvvv 8.0-0156, VL.AppServices). The Qt window is no Form, so in the WinForms window export the Renderer holds a
    /// KeepAlive while its window is open (<see cref="HoldWhileWindowOpen"/>); console exports are deliberately not kept alive,
    /// they need KeepAppAlive [System] in the top-level patch (docs/linux.md).
    /// <list type="bullet">
    /// <item>WinForms host: Application.Exit ends Application.Run, like Exit [System.Application] of VL.CoreLib.Windows.</item>
    /// <item>Console host (Linux, Windows Exe): the keep-alive count (private AppHostBase.refCount) is set to 0, so the loop
    /// ends after the current frame and the regular path runs (patch Dispose, AppHost.OnExit, logger, Shutdown, exit code 0).
    /// Fallback if the field is missing (newer vvvv): Environment.Exit(0), which skips the Dispose.</item>
    /// <item>Console host: SIGTERM/SIGINT end the app the same way (exit code 0); a second signal ends it the hard .NET way.</item>
    /// <item>Console host on Linux: after the regular end the process leaves with _exit, skipping the destructors of native
    /// libraries. The Qualcomm GPU driver of WSL arm64 hangs in exit() → _dl_fini → llvm_shutdown otherwise (L2d, L7a).</item>
    /// </list>
    /// </summary>
    internal static class ExportedApp
    {
        private const string refCountField = "refCount";

        private static readonly object s_syncRoot = new();
        private static AppHost? s_consoleHost; // set once by Attach (one AppHost per exported process)
        private static SynchronizationContext? s_mainContext;
        private static ILogger? s_logger;
        private static PosixSignalRegistration? s_sigTerm, s_sigInt;
        private static int s_signals;
        private static volatile bool s_ended; // AppHost.OnExit fired: the regular end ran, _exit is safe

        internal const string WindowsFormsAppHostType = "VL.App.WindowsForms.WindowsFormsAppHost";

        /// <summary>
        /// True if the app runs in the WinForms window host of exported apps (OutputType WinExe, package VL.App.WindowsForms).
        /// AppHost has no API for the host kind, so the generated entry point's host type is checked by name (no WinForms
        /// reference needed). vvvv 8.0-0156: WinExe → sealed VL.App.WindowsForms.WindowsFormsAppHost, console (Exe) →
        /// sealed VL.App.ConsoleHost.ConsoleAppHost (VL.App.Console), both derived from VL.AppServices.ExportedAppHost.
        /// The base chain is walked in case a future host derives from the WinForms host.
        /// </summary>
        public static bool IsWindowsFormsHost(AppHost appHost)
        {
            for (var t = appHost.GetType(); t != null; t = t.BaseType)
                if (t.FullName == WindowsFormsAppHostType)
                    return true;
            return false;
        }

        /// <summary>
        /// The keep-alive of a Renderer's open window: in the WinForms window export it keeps the app running until the Qt
        /// window is closed (X) or the node is disposed, like the former invisible keep-alive Form. Null in the editor and in
        /// console exports.
        /// </summary>
        public static IDisposable? HoldWhileWindowOpen(AppHost appHost)
            => appHost.IsExported && IsWindowsFormsHost(appHost) ? appHost.KeepAlive() : null;

        /// <summary>
        /// Called per app host by Initialization.Configure. In the exported console host (once per process): signal handlers
        /// and, on Linux, the _exit after the regular end, with or without a Renderer. Editor and WinForms host: nothing.
        /// </summary>
        public static void Attach(AppHost appHost)
        {
            if (!appHost.IsExported || IsWindowsFormsHost(appHost))
                return;
            lock (s_syncRoot)
            {
                if (s_consoleHost != null)
                    return;
                s_consoleHost = appHost;
                s_mainContext = SynchronizationContext.Current ?? appHost.SynchronizationContext;
                s_logger = appHost.LoggerFactory?.CreateLogger(typeof(ExportedApp));
            }
            appHost.OnExit.Subscribe(_ => s_ended = true);
            if (OperatingSystem.IsLinux())
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            {
                s_sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
                s_sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            }
        }

        /// <summary>Ends the exported app (Bound To Document, signals). Call on the main (Qt) thread.</summary>
        public static void Exit(AppHost appHost)
        {
            if (OperatingSystem.IsWindows() && IsWindowsFormsHost(appHost)
                && Type.GetType("System.Windows.Forms.Application, System.Windows.Forms") is { } application
                && application.GetProperty("MessageLoop")?.GetValue(null) is true
                && application.GetMethod("Exit", Type.EmptyTypes) is { } exit)
            {
                exit.Invoke(null, null);
                return;
            }
            if (ReleaseKeepAlive(appHost))
                return;
            s_logger?.LogWarning("Ending the app via Environment.Exit (AppHostBase.{Field} not found), patches are not disposed", refCountField);
            Environment.Exit(0);
        }

        // ShouldStayAlive = refCount > 0 (AppHostBase); the loop checks it at the end of each frame. Holders that dispose
        // their KeepAlive later count below 0, which no longer matters once the loop has ended.
        private static bool ReleaseKeepAlive(AppHost appHost)
        {
            for (var t = appHost.GetType(); t != null; t = t.BaseType)
            {
                if (t.GetField(refCountField, BindingFlags.Instance | BindingFlags.NonPublic) is { } field && field.FieldType == typeof(int))
                {
                    field.SetValue(appHost, 0);
                    return true;
                }
            }
            return false;
        }

        // Signal thread. The first signal ends the app on the main thread (between two frames), a second one is left to
        // .NET (immediate end, exit code 128 + signal) in case the main loop no longer runs.
        private static void OnSignal(PosixSignalContext context)
        {
            if (Interlocked.Increment(ref s_signals) > 1 || s_consoleHost is not { } appHost || s_mainContext is not { } mainContext)
                return;
            context.Cancel = true;
            s_logger?.LogInformation("{Signal} received, ending the app", context.Signal);
            mainContext.Post(_ => Exit(appHost), null);
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            if (!s_ended)
                return;
            Console.Out.Flush();
            Console.Error.Flush();
            SysExit(Environment.ExitCode);
        }

        [DllImport("libc", EntryPoint = "_exit")]
        private static extern void SysExit(int status);
    }
}
