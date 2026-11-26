using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reactive.Disposables;
using Microsoft.Extensions.Logging;
using VL.Core;

namespace VL.Qt
{
    /// <summary>
    /// Process-wide log target of VL.Qt: the "VL.Qt" logger for process-wide messages (pump, stats) and the
    /// receiver of all Qt/QtNative messages (qDebug … qFatal, QML/JS warnings, vlqt.* categories) from QtNative's message
    /// handler. Everything ends up in the app host's <see cref="ILoggerFactory"/>, i.e. in the vvvv Log window and in
    /// whatever logging providers are configured (files etc.).
    /// <list type="bullet">
    /// <item>Qt level → <see cref="LogLevel"/>: Debug → Debug, Info → Information, Warning → Warning, Critical → Error, Fatal → Critical.</item>
    /// <item>The Qt logging category is the log category, verbatim (e.g. <c>qml</c>, <c>js</c>, <c>qt.rhi.general</c>,
    /// <c>vlqt.gpu</c>), so it matches QT_LOGGING_RULES; uncategorized messages (<c>default</c>) use <c>Qt</c>.</item>
    /// <item>The newest attached app host's factory receives the messages; when it detaches, the previous one does.
    /// Messages arriving while none is attached (between an app restart's teardown and the next app) are kept (bounded) and
    /// flushed into the next factory.</item>
    /// </list>
    /// The callback may come from any Qt thread (render thread): the target is an immutable snapshot, swapped atomically.
    /// </summary>
    internal static class QtLog
    {
        /// <summary>Category for uncategorized Qt messages (Qt's "default" category).</summary>
        internal const string DefaultQtCategory = "Qt";
        private const int MaxPending = 256;

        private static readonly object s_syncRoot = new();
        private static readonly Queue<(LogLevel Level, string Category, string Message)> s_pending = new();
        // Attached targets, the newest last; the newest one receives the messages.
        private static readonly List<Target> s_attached = new();
        private static int s_dropped;
        private static volatile Target? s_target;

        /// <summary>Logger for process-wide VL.Qt messages, null while no app host is attached.</summary>
        internal static ILogger? Logger => s_target?.Main;

        /// <summary>
        /// Attaches the logger factory of <paramref name="appHost"/> (Initialization.Configure) until the host is disposed.
        /// An app host without a logger factory attaches nothing.
        /// </summary>
        internal static void Attach(AppHost appHost)
        {
            if (appHost.LoggerFactory is not { } factory)
                return;
            appHost.Services.GetOrAddService(_ => new Attachment(Attach(factory)), allowToAskParent: false);
        }

        /// <summary>
        /// Makes <paramref name="factory"/> the target: "VL.Qt" for own messages, one logger per Qt category. Disposing the
        /// result falls back to the previously attached factory, or to the pending queue when none is left.
        /// </summary>
        internal static IDisposable Attach(ILoggerFactory factory)
            => Add(new Target(factory.CreateLogger("VL.Qt"), factory));

        /// <summary>Tests: one logger for everything (Qt messages as "[{Category}] {Message}").</summary>
        internal static IDisposable Attach(ILogger logger)
            => Add(new Target(logger, null));

        /// <summary>Tests: detaches everything and drops the pending messages.</summary>
        internal static void Reset()
        {
            lock (s_syncRoot)
            {
                s_attached.Clear();
                s_target = null;
                s_pending.Clear();
                s_dropped = 0;
            }
        }

        private static IDisposable Add(Target target)
        {
            lock (s_syncRoot)
            {
                s_attached.Add(target);
                s_target = target;
                while (s_pending.TryDequeue(out var m))
                    target.Log(m.Level, m.Category, m.Message);
                if (s_dropped > 0)
                {
                    target.Main.LogWarning("{Count} Qt messages were dropped while no logger was set", s_dropped);
                    s_dropped = 0;
                }
            }
            return Disposable.Create(() =>
            {
                lock (s_syncRoot)
                {
                    s_attached.Remove(target);
                    s_target = s_attached.Count > 0 ? s_attached[^1] : null;
                }
            });
        }

        // Service of the app host: detaches its logger factory when the host is disposed.
        private sealed class Attachment : IDisposable
        {
            private readonly IDisposable _attached;
            public Attachment(IDisposable attached) => _attached = attached;
            public void Dispose() => _attached.Dispose();
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

        /// <summary>The log callback of vlqt_app_init: a static function pointer, so nothing needs to be kept alive.</summary>
        internal static unsafe delegate* unmanaged[Cdecl]<int, byte*, byte*, void> Fn => &OnLog;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe void OnLog(int level, byte* category, byte* message)
        {
            // Called from native code on any thread: no exception may escape (it would cross the native frames and end the process).
            try
            {
                Receive(level, Marshal.PtrToStringUTF8((IntPtr)category), Marshal.PtrToStringUTF8((IntPtr)message) ?? string.Empty);
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
