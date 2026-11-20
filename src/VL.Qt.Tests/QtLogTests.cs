using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace VL.Qt.Tests
{
    /// <summary>
    /// #258: Qt/QtNative messages reach the app host's logger factory with mapped level and the Qt category as log category;
    /// messages without a logger are kept and flushed into the next one. No Qt here: <see cref="QtLog.Receive"/> is what
    /// the native callback calls.
    /// </summary>
    [NonParallelizable] // QtLog is process-wide
    public class QtLogTests
    {
        private sealed record Entry(string Category, LogLevel Level, string Message);

        private sealed class Factory : ILoggerFactory
        {
            public readonly List<Entry> Entries = new();
            public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }
        }

        private sealed class Logger : ILogger
        {
            private readonly Factory _factory;
            private readonly string _category;
            public Logger(Factory factory, string category) { _factory = factory; _category = category; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _factory.Entries.Add(new Entry(_category, logLevel, formatter(state, exception)));
        }

        [SetUp]
        public void Reset() => QtLog.SetLogger(null);

        [TearDown]
        public void Clear() => QtLog.SetLogger(null);

        [TestCase(0, LogLevel.Debug)]
        [TestCase(1, LogLevel.Information)]
        [TestCase(2, LogLevel.Warning)]
        [TestCase(3, LogLevel.Error)]
        [TestCase(4, LogLevel.Critical)]
        public void Levels(int qt, LogLevel expected) => Assert.That(QtLog.ToLogLevel(qt), Is.EqualTo(expected));

        [TestCase("qml", "qml")]
        [TestCase("qt.rhi.general", "qt.rhi.general")]
        [TestCase("vlqt.gpu", "vlqt.gpu")]
        [TestCase("default", "Qt")]
        [TestCase("", "Qt")]
        [TestCase(null, "Qt")]
        public void Categories(string? qt, string expected) => Assert.That(QtLog.ToCategory(qt), Is.EqualTo(expected));

        [Test]
        public void QtCategoryBecomesLogCategory()
        {
            var factory = new Factory();
            QtLog.SetFactory(factory);
            QtLog.Receive(2, "qml", "Cannot assign to non-existent property \"foo\"");
            QtLog.Receive(3, "default", "boom");
            QtLog.Logger!.LogInformation("own {Value}", 1);

            Assert.That(factory.Entries, Is.EqualTo(new[]
            {
                new Entry("qml", LogLevel.Warning, "Cannot assign to non-existent property \"foo\""),
                new Entry("Qt", LogLevel.Error, "boom"),
                new Entry("VL.Qt", LogLevel.Information, "own 1"),
            }));
        }

        [Test]
        public void MessagesWithoutLoggerAreFlushedIntoTheNext()
        {
            QtLog.Receive(2, "qt.qpa", "early");
            var factory = new Factory();
            QtLog.SetFactory(factory);

            Assert.That(factory.Entries, Is.EqualTo(new[] { new Entry("qt.qpa", LogLevel.Warning, "early") }));
        }

        [Test]
        public void OverflowIsCountedAndReported()
        {
            for (int i = 0; i < 300; i++)
                QtLog.Receive(0, "x", "m");
            var factory = new Factory();
            QtLog.SetFactory(factory);

            Assert.That(factory.Entries, Has.Count.EqualTo(257));
            Assert.That(factory.Entries[^1].Message, Does.Contain("44 Qt messages were dropped"));
        }

        [Test]
        public void NewFactoryReplacesTheOld()
        {
            var first = new Factory();
            var second = new Factory();
            QtLog.SetFactory(first);
            QtLog.SetFactory(second);
            QtLog.Receive(1, "qml", "after restart");

            Assert.That(first.Entries, Is.Empty);
            Assert.That(second.Entries, Has.Count.EqualTo(1));
        }
    }
}
