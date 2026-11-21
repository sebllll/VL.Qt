using System;
using System.Threading;
using NUnit.Framework;
using VL.Qt;

// CAPI-01 / NAPP-02: vlqt_last_error is the text of the last failing call on the calling thread, and every failing call
// sets one. Two checks: the isolation itself without Qt (core-abi: no GPU needed), and the case the contract is for, a
// vlqt_font_add on a worker thread (not the Qt thread) while the Qt thread fails an apply.
[TestFixture, NonParallelizable]
public static class LastErrorTest
{
    private const int rounds = 2000;

    /// <summary>
    /// Two threads fail different exports in a loop and each reads its own message right after each failure; a failing call
    /// that used to set no message (vlqt_offscreen_is_bound(NULL)) gives a text of its own, not an older one. Needs no Qt.
    /// </summary>
    [Test, Category("core-abi")]
    public static void LastErrorPerThread() => Scenario.RunLocal(s =>
    {
        // A failing call on this thread: apply on a null window.
        var rc = Native.Apply(IntPtr.Zero, new byte[16]);
        var mine = Native.LastError();
        s.Check(rc == Native.Result.NullHandle && mine.Contains("null window"), $"apply on a null window fails with its message (rc {rc}, '{mine}')");

        // vlqt_font_add on another thread fails too (NotInitialized without a Qt app in this process, WrongThread with one).
        var workerRc = Native.Result.Ok;
        var workerMessage = "";
        var worker = new Thread(() =>
        {
            workerRc = Native.FontAdd("no-such-font.ttf", out _, out _);
            workerMessage = Native.LastError();
        }) { Name = "last-error worker" };
        worker.Start();
        worker.Join();
        s.Check(workerRc is Native.Result.NotInitialized or Native.Result.WrongThread && workerMessage.Contains("font_add"),
            $"font_add on a worker fails with its message (rc {workerRc}, '{workerMessage}')");
        var after = Native.LastError();
        s.Check(after == mine, $"this thread still reads its own message after the worker failed ('{after}', expected '{mine}')");

        // A failing call that used to set no message: the text of the earlier failure would be read.
        Native.Apply(IntPtr.Zero, new byte[16]);
        var before = Native.LastError();
        var bound = Native.vlqt_offscreen_is_bound(IntPtr.Zero);
        var message = Native.LastError();
        s.Check(bound == (int)Native.Result.NullHandle && message.Length > 0 && message != before,
            $"offscreen_is_bound(NULL) fails with a message of its own (rc {bound}, '{message}', the earlier one was '{before}')");

        // Both threads at once: every failure is followed by its own text, never the other thread's.
        var (mismatchesHere, mismatchesWorker) = Race(
            () => (Native.Apply(IntPtr.Zero, new byte[16]) == Native.Result.NullHandle, Native.LastError().Contains("null window")),
            () => (Native.FontAdd("no-such-font.ttf", out _, out _) is Native.Result.NotInitialized or Native.Result.WrongThread, Native.LastError().Contains("font_add")));
        s.Check(mismatchesHere == 0 && mismatchesWorker == 0,
            $"{rounds} failures on each of two threads: own message every time (this thread {mismatchesHere} mismatches, worker {mismatchesWorker})");
    });

    /// <summary>
    /// With Qt running: vlqt_font_add on a worker thread refuses with WrongThread while the Qt thread fails an apply, in a loop;
    /// each thread reads its own message.
    /// </summary>
    [Test, Category("last-error")]
    public static void LastErrorQtThread() => Scenario.Run(s =>
    {
        s.Check(QtHost.IsQtThread, "the scenario runs on the Qt thread");
        var rc = Native.Apply(IntPtr.Zero, new byte[16]);
        var mine = Native.LastError();
        s.Check(rc == Native.Result.NullHandle && mine.Contains("null window"), $"apply on a null window fails with its message (rc {rc}, '{mine}')");

        var workerRc = Native.Result.Ok;
        var workerMessage = "";
        var worker = new Thread(() =>
        {
            workerRc = Native.FontAdd("no-such-font.ttf", out _, out _);
            workerMessage = Native.LastError();
        }) { Name = "last-error worker" };
        worker.Start();
        worker.Join();
        s.Check(workerRc == Native.Result.WrongThread && workerMessage.Contains("not on the Qt GUI thread"),
            $"font_add on a worker fails with its message (rc {workerRc}, '{workerMessage}')");
        var after = Native.LastError();
        s.Check(after == mine, $"the Qt thread still reads its own message after the worker failed ('{after}', expected '{mine}')");

        var (mismatchesQt, mismatchesWorker) = Race(
            () => (Native.Apply(IntPtr.Zero, new byte[16]) == Native.Result.NullHandle, Native.LastError().Contains("null window")),
            () => (Native.FontAdd("no-such-font.ttf", out _, out _) == Native.Result.WrongThread, Native.LastError().Contains("not on the Qt GUI thread")));
        s.Check(mismatchesQt == 0 && mismatchesWorker == 0,
            $"{rounds} failures on the Qt thread and on a worker at once: own message every time (Qt thread {mismatchesQt} mismatches, worker {mismatchesWorker})");
    });

    // Runs "here" on the calling thread and "there" on a worker, both rounds times at the same time (a barrier starts them
    // together); each delegate makes one failing call and returns whether it failed as expected and whether the text is its own.
    // Returns the number of rounds in which the code or the text was wrong, per side.
    private static (int here, int there) Race(Func<(bool failed, bool ownText)> here, Func<(bool failed, bool ownText)> there)
    {
        var barrier = new Barrier(2);
        var thereMismatches = 0;
        var worker = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < rounds; i++)
            {
                var (failed, ownText) = there();
                if (!failed || !ownText)
                    thereMismatches++;
            }
        }) { Name = "last-error race" };
        worker.Start();
        barrier.SignalAndWait();
        var hereMismatches = 0;
        for (int i = 0; i < rounds; i++)
        {
            var (failed, ownText) = here();
            if (!failed || !ownText)
                hereMismatches++;
        }
        worker.Join();
        return (hereMismatches, thereMismatches);
    }
}
