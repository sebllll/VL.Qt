using System;
using System.Threading;
using NUnit.Framework;
using VL.Qt;

// CAPI-01: vlqt_last_error is one global string. (1) A failing call on another thread replaces the message of the Qt
// thread's own failure (vlqt_font_add from a worker sets "not on the Qt GUI thread"; vlqt_shader_reflect,
// vlqt_res_read_audio and vlqt_image_write are documented for any thread too). (2) Not every negative return sets a
// message: vlqt_offscreen_is_bound(NULL) returns VLQT_OFFSCREEN_NULL_SCENE and the caller reads the text of an older,
// unrelated failure. Both checks describe the contract of the proposal: per thread, set on every failure.
[TestFixture, NonParallelizable]
public static class LastErrorTest
{
    /// <summary>CAPI-01: the last error is per thread and set by every failing call.</summary>
    [Test, Category("last-error")]
    [Ignore("CAPI-01: vlqt_last_error is one global string: a worker's failure replaces the Qt thread's message, and vlqt_offscreen_is_bound(NULL) fails without one (fails: both checks)")]
    public static void LastError() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        // A failing call on the Qt thread (this one): apply on a null window.
        var rc = Native.Apply(IntPtr.Zero, new byte[16]);
        var mine = Native.LastError();
        s.Check(rc != 0 && mine.Contains("null window"), $"apply on a null window fails with its message (rc {rc}, '{mine}')");

        // A failing call on another thread: vlqt_font_add refuses every thread but the Qt one.
        int workerRc = 0;
        var workerMessage = "";
        var worker = new Thread(() =>
        {
            workerRc = Native.FontAdd("no-such-font.ttf", out _, out _);
            workerMessage = Native.LastError();
        }) { Name = "last-error worker" };
        worker.Start();
        worker.Join();
        s.Check(workerRc == -1 && workerMessage.Contains("not on the Qt GUI thread"),
            $"font_add on a worker fails with its message (rc {workerRc}, '{workerMessage}')");
        var after = Native.LastError();
        s.Check(after == mine, $"the Qt thread still reads its own message after the worker failed ('{after}', expected '{mine}')");

        // A failing call that sets no message: the caller reads the text of the earlier failure.
        Native.Apply(IntPtr.Zero, new byte[16]);
        var before = Native.LastError();
        var bound = Native.vlqt_offscreen_is_bound(IntPtr.Zero);
        var message = Native.LastError();
        s.Check(bound < 0 && message != before,
            $"offscreen_is_bound(NULL) fails with a message of its own (rc {bound}, '{message}', the earlier one was '{before}')");
    }
}
