using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace VL.Qt
{
    /// <summary>
    /// Process-wide log target of VL.Qt (#258): the "VL.Qt" logger for process-wide messages (pump, stats) and the
    /// receiver of all Qt/QtNative messages (qDebug … qFatal, QML/JS warnings, vlqt.* categories) from QtNative's message
    /// handler. Everything ends up in the app host's <see cref="ILoggerFactory"/>, i.e. in the vvvv Log window and in
    /// whatever logging providers are configured (files etc.).
    /// <list type="bullet">
    /// <item>Qt level → <see cref="LogLevel"/>: Debug → Debug, Info → Information, Warning → Warning, Critical → Error, Fatal → Critical.</item>
    /// <item>The Qt logging category is the log category, verbatim (e.g. <c>qml</c>, <c>js</c>, <c>qt.rhi.general</c>,
    /// <c>vlqt.gpu</c>), so it matches QT_LOGGING_RULES; uncategorized messages (<c>default</c>) use <c>Qt</c>.</item>
    /// <item>Messages arriving while no logger is set (between an app restart's teardown and the next renderer) are kept
    /// (bounded) and flushed into the next logger.</item>
    /// </list>
    /// The callback may come from any Qt thread (render thread): the target is an immutable snapshot, swapped atomically.
    /// </summary>
    internal static class QtLog
    {
        /// <summary>Category for uncategorized Qt messages (Qt's "default" category).</summary>
        internal const string DefaultQtCategory = "Qt";
        private const int MaxPending = 256;

        // Delegate must stay reachable for the lifetime of the process (native keeps the pointer).
        internal static readonly Native.LogFn Fn = OnLog;

        private static readonly object s_syncRoot = new();
        private static readonly Queue<(LogLevel Level, string Category, string Message)> s_pending = new();
        private static int s_dropped;
        private static volatile Target? s_target;

        /// <summary>Logger for process-wide VL.Qt messages, null before the first app host set one.</summary>
        internal static ILogger? Logger => s_target?.Main;

        /// <summary>Uses the app host's factory: "VL.Qt" for own messages, one logger per Qt category.</summary>
        internal static void SetFactory(ILoggerFactory factory)
            => Set(new Target(factory.CreateLogger("VL.Qt"), factory));

        /// <summary>Tests: one logger for everything (Qt messages as "[{Category}] {Message}").</summary>
        internal static void SetLogger(ILogger? logger)
            => Set(logger is null ? null : new Target(logger, null));

        private static void Set(Target? target)
        {
            lock (s_syncRoot)
            {
                s_target = target;
                if (target is null)
                    return;
                while (s_pending.TryDequeue(out var m))
                    target.Log(m.Level, m.Category, m.Message);
                if (s_dropped > 0)
                {
                    target.Main.LogWarning("{Count} Qt messages were dropped while no logger was set", s_dropped);
                    s_dropped = 0;
                }
            }
        }

        /// <summary>Qt level of QtNative (0 debug … 4 fatal) as <see cref="LogLevel"/>.</summary>
        internal static LogLevel ToLogLevel(int level) => level switch
        {
            <= 0 => LogLevel.Debug,
            1 => LogLevel.Information,
            2 => LogLevel.Warning,
            3 => LogLevel.Error,
            _ => LogLevel.Critical,
        };

        /// <summary>Log category for a Qt category: verbatim, "default"/empty → <see cref="DefaultQtCategory"/>.</summary>
        internal static string ToCategory(string? qtCategory)
            => string.IsNullOrEmpty(qtCategory) || qtCategory == "default" ? DefaultQtCategory : qtCategory;

        private static void OnLog(int level, IntPtr category, IntPtr message)
        {
            // Called from native code: no exception may escape (it would cross the native frames and end the process).
            try
            {
                Receive(level, Marshal.PtrToStringUTF8(category), Marshal.PtrToStringUTF8(message) ?? string.Empty);
            }
            catch (Exception)
            {
                // A disposed logger factory (old app host during F8/F5) has nowhere to report to; the message is lost.
            }
        }

        /// <summary>One message from QtNative (also the test entry point).</summary>
        internal static void Receive(int level, string? qtCategory, string msg)
        {
            var logLevel = ToLogLevel(level);
            var cat = ToCategory(qtCategory);
            var target = s_target;
            if (target is not null)
            {
                target.Log(logLevel, cat, msg);
                return;
            }
            lock (s_syncRoot)
            {
                if (s_target is { } t)
                    t.Log(logLevel, cat, msg);
                else if (s_pending.Count < MaxPending)
                    s_pending.Enqueue((logLevel, cat, msg));
                else
                    s_dropped++;
            }
        }

        private sealed class Target
        {
            private readonly ILoggerFactory? _factory;
            private readonly ConcurrentDictionary<string, ILogger> _byCategory = new();

            public Target(ILogger main, ILoggerFactory? factory)
            {
                Main = main;
                _factory = factory;
            }

            public ILogger Main { get; }

            public void Log(LogLevel level, string category, string message)
            {
                if (_factory is null)
                {
                    Main.Log(level, "[{Category}] {Message}", category, message);
                    return;
                }
                var logger = _byCategory.GetOrAdd(category, static (c, f) => f.CreateLogger(c), _factory);
                logger.Log(level, "{Message}", message);
            }
        }
    }
}
