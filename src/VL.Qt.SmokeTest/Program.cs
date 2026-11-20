using System;
using System.IO;
using System.Linq;

/// <summary>
/// Helper executable of the scenarios that need a process of their own (OwnProcess): `VL.Qt.SmokeTest --scenario a,b`
/// starts Qt on the main thread, runs the named scenarios in this order and writes one line per check (PASS/FAIL/WARN/SKIP)
/// and a final <see cref="DoneLine"/>; exit code 0 if no check failed. Options come from the environment (SmokeParameters).
/// The tests themselves run through `dotnet test` (NUnit), not through this entry point.
/// </summary>
static class Program
{
    public const string DoneLine = "SMOKE-HELPER DONE";

    [STAThread] // like vvvv's main thread; Qt needs STA for OleInitialize
    static int Main(string[] args)
    {
        var si = Array.IndexOf(args, "--scenario");
        if (si < 0 || si + 1 >= args.Length)
        {
            Console.WriteLine("VL.Qt.SmokeTest is an NUnit project: run it with `dotnet test src/VL.Qt.SmokeTest -c Release`.");
            Console.WriteLine("Helper mode (started by OwnProcess): VL.Qt.SmokeTest --scenario <name>[,<name>...]");
            return 2;
        }
        var names = args[si + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var stdout = Console.Out;
        PerfLimits.Configure(SmokeParameters.PerfArgs());
        Scenario.WriteTsv = false;
        int failures = 0;
        QtHost.StartOnCurrentThread();
        if (QtHost.InitResult != 0)
        {
            stdout.WriteLine($"FAIL app_init ({QtHost.InitResult}) {QtHost.InitError}");
            failures++;
        }
        else
        {
            foreach (var name in names)
            {
                var body = OwnProcess.Find(name);
                if (body == null)
                {
                    stdout.WriteLine($"FAIL unknown scenario '{name}'");
                    failures++;
                    continue;
                }
                failures += Scenario.RunInProcess(name, body, stdout).Failures.Count;
            }
        }
        if (Environment.GetEnvironmentVariable("VLQT_SMOKE_QTLOG") is { Length: > 0 } logPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            File.WriteAllText(logPath, QtHost.Log);
        }
        stdout.WriteLine($"{DoneLine} {failures} failure(s)");
        stdout.Flush();
        return failures == 0 ? 0 : 1;
    }
}
