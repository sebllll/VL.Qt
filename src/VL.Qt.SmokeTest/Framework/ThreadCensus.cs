using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

/// <summary>
/// The threads of this process by the module their start address lies in, for leak checks that count threads. Windows starts
/// the workers of its thread pool in ntdll, and .NET starts its own threads (thread pool, tiered JIT, finalizer) in coreclr; any
/// other thread starts in the module that created it (avcodec for FFmpeg's decoder threads, Qt6Core for a QThread, the GPU
/// driver's DLL). Both pools grow and shrink with the work of the whole process: after the earlier scenarios of a full SmokeTest
/// run about 70 to 120 Windows pool workers are idle and retire or start at any time, so the total count is no measure of
/// what one scenario leaks. Outside Windows (no start address) every thread counts under "?".
/// </summary>
internal static unsafe class ThreadCensus
{
    /// <summary>Returns the thread count per start module ("ntdll.dll", "avcodec-63.dll"; "?" if the address is unknown).</summary>
    public static Dictionary<string, int> Take()
    {
        var census = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var p = Process.GetCurrentProcess();
        if (!OperatingSystem.IsWindows())
        {
            census["?"] = p.Threads.Count;
            return census;
        }
        var modules = p.Modules.Cast<ProcessModule>()
            .Select(m => (name: m.ModuleName, start: (long)m.BaseAddress, end: (long)m.BaseAddress + m.ModuleMemorySize))
            .ToList();
        foreach (ProcessThread t in p.Threads)
        {
            long a = (long)StartAddress(t.Id);
            var name = modules.FirstOrDefault(m => a >= m.start && a < m.end).name ?? "?";
            census[name] = census.GetValueOrDefault(name) + 1;
        }
        return census;
    }

    /// <summary>Returns the number of threads that belong to neither the Windows nor the .NET thread pool (see the type).</summary>
    public static int OutsidePools(Dictionary<string, int> census) =>
        census.Where(kv => !kv.Key.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)
            && !kv.Key.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);

    /// <summary>Returns the modules whose thread count changed from a to b, e.g. "avcodec-63.dll +2, ntdll.dll -1" ("none" if none).</summary>
    public static string Diff(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        var changes = a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => (k, d: b.GetValueOrDefault(k) - a.GetValueOrDefault(k)))
            .Where(x => x.d != 0)
            .Select(x => $"{x.k} {x.d:+0;-0}");
        var text = string.Join(", ", changes);
        return text.Length > 0 ? text : "none";
    }

    // Win32 start address of the thread (ThreadQuerySetWin32StartAddress); ProcessThread.StartAddress gives the start in
    // ntdll for every thread.
    private static IntPtr StartAddress(int threadId)
    {
        var h = OpenThread(threadQueryInformation, false, threadId);
        if (h == IntPtr.Zero)
            return IntPtr.Zero;
        try
        {
            IntPtr address;
            return NtQueryInformationThread(h, threadQuerySetWin32StartAddress, &address, IntPtr.Size, out _) == 0 ? address : IntPtr.Zero;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private const int threadQueryInformation = 0x0040;
    private const int threadQuerySetWin32StartAddress = 9;

    [DllImport("ntdll")] private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, IntPtr* info, int length, out int returned);
    [DllImport("kernel32")] private static extern IntPtr OpenThread(int access, bool inherit, int threadId);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
}
