using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using VL.Lang;

namespace VL.Qt.Tests
{
    /// <summary>
    /// A <see cref="NodeErrors"/> on a recording logger and a recording node message: what a node logs and what it shows at
    /// the node, without a NodeContext.
    /// </summary>
    internal sealed class RecordingMessages : ILogger
    {
        public RecordingMessages() => Errors = new NodeErrors(this, Show);

        /// <summary>The store to pass to the node.</summary>
        public NodeErrors Errors { get; }

        /// <summary>Every logged text, in order.</summary>
        public List<string> Logged { get; } = new();

        /// <summary>Levels of <see cref="Logged"/>.</summary>
        public List<LogLevel> Levels { get; } = new();

        /// <summary>Message shown at the node, null if none.</summary>
        public string? Shown { get; private set; }

        /// <summary>Severity of <see cref="Shown"/>.</summary>
        public MessageSeverity ShownSeverity { get; private set; }

        /// <summary>Number of times a message was put on the node.</summary>
        public int Shows { get; private set; }

        private IDisposable Show(MessageSeverity severity, string text)
        {
            Shows++;
            Shown = text;
            ShownSeverity = severity;
            return new Removal(this, text);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logged.Add(formatter(state, exception));
            Levels.Add(logLevel);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        private sealed class Removal(RecordingMessages owner, string text) : IDisposable
        {
            public void Dispose()
            {
                if (owner.Shown == text)
                    owner.Shown = null;
            }
        }
    }
}
