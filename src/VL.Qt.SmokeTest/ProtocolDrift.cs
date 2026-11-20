using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt;

// Drift check: protocol.h (single source of truth) <-> Protocol.cs, compared by name and value. Needs no GPU: Qt is not
// started (`dotnet test --filter TestCategory=drift`, the former --drift).
[TestFixture]
public static class ProtocolDrift
{
    static readonly Regex s_define = new(@"^\s*#define\s+VLQT_(\w+)\s+(-?\d+)\s*(/\*.*)?$");
    static readonly Regex s_enumMember = new(@"^\s*VLQT_(\w+)\s*=\s*(-?\d+)\s*,?\s*(/\*.*)?$");

    public static string DefaultHeaderPath => Path.Combine(AppContext.BaseDirectory, "protocol.h");

    /// <summary>protocol.h and Protocol.cs define the same constants with the same values.</summary>
    [Test, Category("drift")]
    public static void Drift() => Scenario.RunLocal(s =>
    {
        var drift = Run(DefaultHeaderPath);
        foreach (var e in drift)
            s.Info("DRIFT " + e);
        s.Check(drift.Count == 0, "protocol.h <-> Protocol.cs drift check");
    });

    /// <summary>Returns the list of mismatches (empty = no drift).</summary>
    internal static List<string> Run(string headerPath)
    {
        var errors = new List<string>();
        if (!File.Exists(headerPath))
        {
            errors.Add("protocol.h not found: " + headerPath);
            return errors;
        }
        var header = ParseHeader(File.ReadAllLines(headerPath), errors);
        var mirror = ReflectMirror(typeof(Protocol));
        errors.AddRange(Compare(header, mirror));
        if (header.Count == 0) errors.Add("protocol.h: no constants parsed");

        if (errors.Count > 0) return errors;

        // Self-test of the comparer: a changed value and a renamed constant must both be detected.
        var mutated = new Dictionary<string, long>(mirror);
        var first = mutated.Keys.First();
        mutated[first] += 1000;
        mutated["DRIFT_SELFTEST_EXTRA"] = 1;
        if (Compare(header, mutated).Count != 2) errors.Add("drift self-test: comparer missed an injected mismatch");
        return errors;
    }

    static Dictionary<string, long> ParseHeader(string[] lines, List<string> errors)
    {
        var result = new Dictionary<string, long>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = s_define.Match(lines[i]);
            if (!m.Success) m = s_enumMember.Match(lines[i]);
            if (!m.Success) continue;
            if (!result.TryAdd(m.Groups[1].Value, long.Parse(m.Groups[2].Value)))
                errors.Add($"protocol.h:{i + 1}: duplicate VLQT_{m.Groups[1].Value}");
        }
        return result;
    }

    // Top-level consts -> UPPER_SNAKE, nested class consts -> GROUP_UPPER_SNAKE.
    static Dictionary<string, long> ReflectMirror(System.Type root)
    {
        var result = new Dictionary<string, long>();
        void Add(System.Type t, string prefix)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
                result[prefix + ToUpperSnake(f.Name)] = Convert.ToInt64(f.GetRawConstantValue());
        }
        Add(root, "");
        foreach (var nested in root.GetNestedTypes(BindingFlags.Public))
            Add(nested, ToUpperSnake(nested.Name) + "_"); // WindowMode -> WINDOW_MODE_
        return result;
    }

    static List<string> Compare(Dictionary<string, long> header, Dictionary<string, long> mirror)
    {
        var errors = new List<string>();
        foreach (var (name, value) in header)
        {
            if (!mirror.TryGetValue(name, out var v)) errors.Add($"VLQT_{name} missing in Protocol.cs");
            else if (v != value) errors.Add($"VLQT_{name}: protocol.h {value} != Protocol.cs {v}");
        }
        foreach (var name in mirror.Keys.Where(n => !header.ContainsKey(n)))
            errors.Add($"Protocol.cs has VLQT_{name}, missing in protocol.h");
        return errors;
    }

    static string ToUpperSnake(string pascal)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && (char.IsLower(pascal[i - 1]) || char.IsDigit(pascal[i - 1])))
                sb.Append('_');
            sb.Append(char.ToUpperInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}
