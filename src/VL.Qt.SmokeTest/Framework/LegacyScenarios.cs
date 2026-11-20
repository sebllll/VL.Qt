using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

/// <summary>
/// Legacy adapter (W1-01, temporary): the scenarios not yet converted to NUnit tests, in the order of the former
/// Program.Main, run through NUnit with their old delegate signatures. Each row is one test case named and categorized
/// by the old scenario name (`dotnet test --filter TestCategory=apply`). Converting a scenario = rewriting its file as
/// NUnit test(s) + deleting its row here (CONVERTING.md). The file goes when the table is empty.
/// </summary>
[TestFixture, NonParallelizable]
public static class LegacyScenarios
{
    [Flags]
    internal enum Kind
    {
        Normal = 0,
        /// <summary>Runs only when selected by a filter (was: only when named with --only, or a separate flag).</summary>
        Explicit = 1,
        /// <summary>Runs in a helper process (OwnProcess); <see cref="Row.Scenarios"/> are the names the child runs.</summary>
        OwnProcess = 2,
    }

    internal sealed record Row(string Name, Action<Scenario>? Body, Kind Kind = Kind.Normal)
    {
        public string[] Categories { get; init; } = Array.Empty<string>();
        public string[]? Scenarios { get; init; }
        public Dictionary<string, string>? Env { get; init; }
        public string? OutSubDir { get; init; }
        public string? ExpectInQtLog { get; init; }
    }

    internal static readonly Row[] All =
    {

    };

    /// <summary>Body of a legacy row (helper executable), or null.</summary>
    internal static Action<Scenario>? Find(string name) =>
        All.FirstOrDefault(r => r.Body != null && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.Body;

    static IEnumerable<TestCaseData> Cases(bool ownProcess)
    {
        foreach (var row in All.Where(r => r.Kind.HasFlag(Kind.OwnProcess) == ownProcess))
        {
            var data = new TestCaseData(row.Name).SetName(row.Name).SetCategory(row.Name);
            foreach (var c in row.Categories)
                data.SetCategory(c);
            if (ownProcess)
                data.SetCategory("own-process");
            if (row.Kind.HasFlag(Kind.Explicit))
                data.Explicit("runs only when selected");
            yield return data;
        }
    }

    static IEnumerable<TestCaseData> InProcessCases() => Cases(false);
    static IEnumerable<TestCaseData> OwnProcessCases() => Cases(true);

    /// <summary>A not yet converted scenario, in the shared Qt application of the run.</summary>
    [TestCaseSource(nameof(InProcessCases))]
    public static void Legacy(string name)
    {
        var row = All.First(r => r.Name == name);
        Scenario.Run(name, row.Body!);
    }

    /// <summary>A not yet converted scenario that needs a process of its own.</summary>
    [TestCaseSource(nameof(OwnProcessCases))]
    public static void LegacyOwnProcess(string name)
    {
        var row = All.First(r => r.Name == name);
        var outDir = row.OutSubDir != null ? System.IO.Path.Combine(SmokeParameters.SmokeDir, row.OutSubDir, "grabs") : null;
        Scenario.RunLocal(s => OwnProcess.Run(s, row.Scenarios ?? new[] { row.Name }, row.Env, outDir, row.ExpectInQtLog));
    }
}
