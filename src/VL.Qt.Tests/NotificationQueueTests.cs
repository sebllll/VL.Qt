using System;
using System.Collections.Generic;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.IO;
using VL.Lib.IO.Notifications;
using VL.Qt.Stride;

namespace VL.Qt.Tests
{
    /// <summary>NotificationQueue of QtTexture (#119), compiled in from VL.Qt.Stride: Input Notifications reach the scene.</summary>
    public class NotificationQueueTests
    {
        private sealed class Source : IObservable<INotification>
        {
            public readonly List<IObserver<INotification>> Observers = new();

            public IDisposable Subscribe(IObserver<INotification> observer)
            {
                Observers.Add(observer);
                return new Unsubscriber(() => Observers.Remove(observer));
            }

            public void Emit(INotification n)
            {
                foreach (var o in Observers.ToArray())
                    o.OnNext(n);
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly Action _action;
            public Unsubscriber(Action action) => _action = action;
            public void Dispose() => _action();
        }

        private static INotification Move(float x) => new MouseMoveNotification(new Vector2(x, 0), new Vector2(100, 100), Keys.None, null);

        [Test]
        public void Drain_HandsQueuedNotificationsToTheSinkInOrder()
        {
            var received = new List<INotification>();
            using var queue = new NotificationQueue(received.Add);
            var source = new Source();
            queue.Subscribe(source);
            var a = Move(1);
            var b = Move(2);
            source.Emit(a);
            source.Emit(b);
            Assert.That(received, Is.Empty);
            queue.Drain();
            Assert.That(received, Is.EqualTo(new[] { a, b }));
            queue.Drain();
            Assert.That(received, Has.Count.EqualTo(2));
        }

        // GetNotifications of VL.Stride returns a new observable per call: a patch hands a new source to every Update. What
        // arrived since the last Update must not be dropped when the source is replaced (every click was lost).
        [Test]
        public void Subscribe_NewSourceEveryFrame_KeepsWhatArrivedBefore()
        {
            var received = new List<INotification>();
            using var queue = new NotificationQueue(received.Add);
            var expected = new List<INotification>();
            Source? previous = null;
            for (int frame = 0; frame < 5; frame++)
            {
                // Between two Updates the input arrives on the source of the previous frame.
                if (previous is not null)
                {
                    var n = Move(frame);
                    expected.Add(n);
                    previous.Emit(n);
                }
                var source = new Source();
                queue.Subscribe(source);
                queue.Drain();
                if (previous is not null)
                    Assert.That(previous.Observers, Is.Empty, "the replaced source is unsubscribed");
                previous = source;
            }
            Assert.That(received, Is.EqualTo(expected));
        }

        [Test]
        public void Clear_DropsWhatIsQueued()
        {
            var received = new List<INotification>();
            using var queue = new NotificationQueue(received.Add);
            var source = new Source();
            queue.Subscribe(source);
            source.Emit(Move(1));
            queue.Clear();
            queue.Drain();
            Assert.That(received, Is.Empty);
        }
    }
}
