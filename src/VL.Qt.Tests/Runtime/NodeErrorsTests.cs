using System;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using VL.Lang;
using VL.Qt.Nodes;
using VL.Qt.Tests.Support;

namespace VL.Qt.Tests.Runtime
{
    /// <summary>The message store of a node: one entry per phase, one joined message, logged once per distinct text.</summary>
    public class NodeErrorsTests
    {
        private RecordingMessages _m = null!;
        private NodeErrors _errors = null!;

        [SetUp]
        public void SetUp()
        {
            _m = new RecordingMessages();
            _errors = _m.Errors;
        }

        [Test]
        public void OneWarning_ShownAsItIs()
        {
            _errors.Warn("icon missing", "Icon");
            Assert.That(_m.Shown, Is.EqualTo("icon missing"));
            Assert.That(_m.ShownSeverity, Is.EqualTo(MessageSeverity.Warning));
        }

        [Test]
        public void TwoPhases_BothShown_ClearingOneKeepsTheOther()
        {
            _errors.Warn("samples lowered", "Samples");
            _errors.Warn("icon missing", "Icon");
            Assert.That(_m.Shown, Is.EqualTo("samples lowered\nicon missing"));

            _errors.Clear("Samples");
            Assert.That(_m.Shown, Is.EqualTo("icon missing"));
            _errors.Clear("Icon");
            Assert.That(_m.Shown, Is.Null);
        }

        [Test]
        public void ReportedError_ComesFirst_ClearingItBringsTheWarningsBack()
        {
            _errors.Warn("A", "a");
            _errors.Warn("B", "b");
            _errors.Report(new InvalidOperationException("boom"), "AfterEvents");
            Assert.That(_m.Shown, Is.EqualTo("AfterEvents: boom\nA\nB"));
            Assert.That(_m.ShownSeverity, Is.EqualTo(MessageSeverity.Error));

            _errors.Clear("AfterEvents");
            Assert.That(_m.Shown, Is.EqualTo("A\nB"));
            Assert.That(_m.ShownSeverity, Is.EqualTo(MessageSeverity.Warning));
        }

        [Test]
        public void ClearOfAnotherPhase_LeavesTheMessage()
        {
            _errors.Warn("A", "a");
            _errors.Clear("b");
            Assert.That(_m.Shown, Is.EqualTo("A"));
            Assert.That(_m.Shows, Is.EqualTo(1), "not rebuilt");
        }

        [Test]
        public void SameText_LoggedAndShownOnce()
        {
            for (var i = 0; i < 5; i++)
                _errors.Warn("A", "a");
            Assert.That(_m.Logged, Is.EqualTo(new[] { "A" }));
            Assert.That(_m.Levels, Is.EqualTo(new[] { LogLevel.Warning }));
            Assert.That(_m.Shows, Is.EqualTo(1));
        }

        [Test]
        public void NewText_LoggedAgain_ClearedText_LoggedAgainWhenItReturns()
        {
            _errors.Warn("A", "a");
            _errors.Warn("A2", "a");
            _errors.Clear("a");
            _errors.Warn("A2", "a");
            Assert.That(_m.Logged, Is.EqualTo(new[] { "A", "A2", "A2" }));
        }

        [Test]
        public void Report_LoggedAsErrorWithTheException()
        {
            _errors.Report(new InvalidOperationException("boom"), "ApplyCommands");
            _errors.Report(new InvalidOperationException("boom"), "ApplyCommands");
            Assert.That(_m.Logged, Is.EqualTo(new[] { "ApplyCommands failed: boom" }));
            Assert.That(_m.Levels, Is.EqualTo(new[] { LogLevel.Error }));
        }

        [Test]
        public void SetEmpty_Clears()
        {
            _errors.Set("a", MessageSeverity.Error, "E");
            Assert.That(_m.Shown, Is.EqualTo("E"));
            _errors.Set("a", MessageSeverity.Error, "");
            Assert.That(_m.Shown, Is.Null);
        }

        [Test]
        public void LogOnly_LogsOncePerText_NothingShown()
        {
            _errors.LogOnly("disk full");
            _errors.LogOnly("disk full");
            _errors.LogOnly("");
            _errors.LogOnly("disk full");
            Assert.That(_m.Logged, Is.EqualTo(new[] { "disk full", "disk full" }));
            Assert.That(_m.Shown, Is.Null);
        }

        [Test]
        public void Run_ReportsAThrow_ClearsOnSuccess_NoAllocation()
        {
            _errors.Run("Poll", 1, static n => throw new InvalidOperationException($"fail {n}"));
            Assert.That(_m.Shown, Is.EqualTo("Poll: fail 1"));
            _errors.Run("Poll", 1, static _ => { });
            Assert.That(_m.Shown, Is.Null);

            // The first call creates the cached delegate of the static lambda.
            for (var i = 0; i < 2; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var j = 0; j < 100; j++)
                    _errors.Run("Poll", j, static _ => { });
                if (i == 1)
                    Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
            }
        }

        [Test]
        public void Run_LetsCancellationPass()
        {
            Assert.Throws<OperationCanceledException>(() => _errors.Run("Poll", 0, static _ => throw new OperationCanceledException()));
            Assert.That(_m.Shown, Is.Null);
        }

        [Test]
        public void Dispose_RemovesTheMessage()
        {
            _errors.Warn("A", "a");
            _errors.Dispose();
            Assert.That(_m.Shown, Is.Null);
        }

        [Test]
        public void NodeMessage_SetsItsPhase_SameTextIsNoChange_DisposeClears()
        {
            using var first = new NodeMessage(_errors, "first");
            var second = new NodeMessage(_errors, "second", MessageSeverity.Error);
            first.Set("W");
            second.Set("E");
            Assert.That(_m.Shown, Is.EqualTo("E\nW"));
            first.Set("W");
            Assert.That(_m.Shows, Is.EqualTo(2), "same text: nothing rebuilt");

            second.Dispose();
            Assert.That(_m.Shown, Is.EqualTo("W"));
            Assert.That(second.Text, Is.Null);
            first.Set("");
            Assert.That(_m.Shown, Is.Null);
        }

        [Test]
        public void NodeMessage_WithoutStore_KeepsTheTextOnly()
        {
            using var message = new NodeMessage(null, "a");
            message.Set("W");
            Assert.That(message.Text, Is.EqualTo("W"));
        }
    }
}
