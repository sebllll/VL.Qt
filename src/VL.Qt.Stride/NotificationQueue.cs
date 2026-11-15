using System;
using System.Collections.Generic;
using VL.Lib.IO.Notifications;

namespace VL.Qt.Stride
{
    /// <summary>
    /// Takes the notifications of an Input Notifications pin from whatever thread they arrive on and hands them to the
    /// main thread in order (#119, render-texture.md "Timing"). <see cref="Subscribe"/> and <see cref="Drain"/> on the main
    /// thread only; the lock is short and allocation-free (the two lists are reused).
    /// </summary>
    internal sealed class NotificationQueue : IObserver<INotification>, IDisposable
    {
        readonly object syncRoot = new();
        List<INotification> _pending = new();
        List<INotification> _draining = new();
        IObservable<INotification>? _source;
        IDisposable? _subscription;

        /// <summary>Subscribes to <paramref name="source"/>; the same instance again does nothing, another one replaces the subscription and drops what is queued.</summary>
        public void Subscribe(IObservable<INotification>? source)
        {
            if (ReferenceEquals(source, _source))
                return;
            _subscription?.Dispose();
            _subscription = null;
            _source = source;
            Clear();
            _subscription = source?.Subscribe(this);
        }

        /// <summary>Drops what is queued.</summary>
        public void Clear()
        {
            lock (syncRoot)
                _pending.Clear();
        }

        /// <summary>Hands the queued notifications to <paramref name="scene"/> in arrival order.</summary>
        public void Drain(OffscreenRenderer scene)
        {
            lock (syncRoot)
                (_pending, _draining) = (_draining, _pending);
            // Not under the lock: Notify calls into QtNative, and the source may emit meanwhile.
            foreach (var notification in _draining)
                scene.Notify(notification);
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
