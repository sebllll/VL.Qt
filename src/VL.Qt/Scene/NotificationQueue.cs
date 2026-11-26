using System;
using System.Collections.Generic;
using VL.Lib.IO.Notifications;

namespace VL.Qt
{
    /// <summary>
    /// Takes the notifications of an Input Notifications pin from whatever thread they arrive on and hands them to the
    /// main thread in order (render-texture.md "Timing"). <see cref="Subscribe"/> and <see cref="Drain"/> on the main
    /// thread only; the lock is short and allocation-free (the two lists are reused).
    /// </summary>
    internal sealed class NotificationQueue : IObserver<INotification>, IDisposable
    {
        private readonly object syncRoot = new();
        private readonly Action<INotification> _sink;
        private List<INotification> _pending = new();
        private List<INotification> _draining = new();
        private IObservable<INotification>? _source;
        private IDisposable? _subscription;

        /// <param name="sink">Receives the notifications in <see cref="Drain"/>, on the main thread.</param>
        public NotificationQueue(Action<INotification> sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>Subscribes to <paramref name="source"/>; the same instance again does nothing, another one replaces the subscription
        /// and keeps what is queued.</summary>
        /// <remarks>
        /// A patch that builds its input in every frame (e.g. GetNotifications of VL.Stride, an operation that returns a new
        /// observable per call) hands a new source to every Update. The notifications that arrived since the last Update
        /// came from the previous source and are still due: dropping them here lost every click.
        /// </remarks>
        public void Subscribe(IObservable<INotification>? source)
        {
            if (ReferenceEquals(source, _source))
                return;
            _subscription?.Dispose();
            _subscription = null;
            _source = source;
            _subscription = source?.Subscribe(this);
        }

        /// <summary>Drops what is queued.</summary>
        public void Clear()
        {
            lock (syncRoot)
                _pending.Clear();
        }

        /// <summary>Hands the queued notifications to the sink in arrival order.</summary>
        public void Drain()
        {
            lock (syncRoot)
                (_pending, _draining) = (_draining, _pending);
            // Not under the lock: the sink calls into QtNative, and the source may emit meanwhile.
            foreach (var notification in _draining)
                _sink(notification);
            _draining.Clear();
        }

        public void OnNext(INotification value)
        {
            lock (syncRoot)
                _pending.Add(value);
        }

        // A failing or ending input stream just stops the input; the scene stays as it is.
        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            _subscription = null;
            _source = null;
            Clear();
        }
    }
}