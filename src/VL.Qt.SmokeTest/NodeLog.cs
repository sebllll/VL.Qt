using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using VL.Qt;

/// <summary>
/// A <see cref="NodeErrors"/> on a recording logger, for nodes run without vvvv: what the node logs and what it shows at
/// the node (the image and file nodes have no Error output).
/// </summary>
internal sealed class NodeLog : ILogger
{
    private readonly List<string> _logged = new();
    private int _taken;

    public NodeLog() => Errors = new NodeErrors(this, (_, text) =>
    {
        Shown = text;
        return new Removal(this, text);
    });

    /// <summary>The store to pass to the node.</summary>
    public NodeErrors Errors { get; }

    /// <summary>Message shown at the node, null if none.</summary>
    public string? Shown { get; private set; }

    /// <summary>Returns what was logged since the last call (joined), empty if nothing.</summary>
    public string Take()
    {
        var text = _taken == _logged.Count ? "" : string.Join("; ", _logged.GetRange(_taken, _logged.Count - _taken));
        _taken = _logged.Count;
        return text;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _logged.Add(formatter(state, exception));

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    private sealed class Removal(NodeLog owner, string text) : IDisposable
    {
        public void Dispose()
        {
            if (owner.Shown == text)
                owner.Shown = null;
        }
    }
}
