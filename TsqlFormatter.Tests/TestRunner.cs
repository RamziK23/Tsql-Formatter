using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Tests
{

/// <summary>
/// A single golden test case: feed Input to the formatter, expect Expected output.
/// </summary>
public sealed class TestCase
{
    public string Rule    { get; init; } = "";
    public string Name    { get; init; } = "";
    public string Input   { get; init; } = "";
    public string Expected{ get; init; } = "";
}

public static class TestRunner
{
    // ── ANSI colors ──────────────────────────────────────────────────────────
    const string Reset = "\u001b[0m";
    const string Red   = "\u001b[31m";
    const string Green = "\u001b[32m";
    const string Yellow= "\u001b[33m";
    const string Cyan  = "\u001b[36m";
    const string Dim   = "\u001b[2m";

    public static string Format(string sql)
    {
        // Use the real CLI entry point so fragment detection is exercised too.
        return FormatterEngine.FormatSource(sql);
    }

    /// <summary>Normalize for comparison: trim trailing whitespace per line, unify newlines, trim doc edges.</summary>
    static string Normalize(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n')
                     .Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim('\n');
    }

    public static int RunAll(List<TestCase> cases, bool verbose, string? ruleFilter)
    {
        if (ruleFilter != null)
            cases = cases.Where(c => c.Rule.Equals(ruleFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        int passed = 0, failed = 0;
        var failures = new List<(TestCase tc, string actual, string error)>();

        // Group by rule for a clean summary
        foreach (var group in cases.GroupBy(c => c.Rule).OrderBy(g => g.Key))
        {
            Console.WriteLine($"\n{Cyan}━━ Rule {group.Key} ━━{Reset}");
            foreach (var tc in group)
            {
                string actual;
                try
                {
                    actual = Format(tc.Input);
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add((tc, "", "EXCEPTION: " + ex.Message));
                    Console.WriteLine($"  {Red}✗ {tc.Name}  (threw {ex.GetType().Name}){Reset}");
                    continue;
                }

                if (Normalize(actual) == Normalize(tc.Expected))
                {
                    passed++;
                    Console.WriteLine($"  {Green}✓{Reset} {tc.Name}");
                    if (verbose)
                        Console.WriteLine(Indent(actual, "      " + Dim) + Reset);
                }
                else
                {
                    failed++;
                    failures.Add((tc, actual, ""));
                    Console.WriteLine($"  {Red}✗ {tc.Name}{Reset}");
                }
            }
        }

        // ── Detailed diffs for failures ───────────────────────────────────────
        if (failures.Count > 0)
        {
            Console.WriteLine($"\n{Yellow}═══ FAILURES ═══{Reset}");
            foreach (var (tc, actual, error) in failures)
            {
                Console.WriteLine($"\n{Red}● {tc.Rule} / {tc.Name}{Reset}");
                if (error.Length > 0)
                {
                    Console.WriteLine($"  {Red}{error}{Reset}");
                    continue;
                }
                PrintDiff(Normalize(tc.Expected), Normalize(actual));
            }
        }

        // ── Idempotency: formatting the output again must yield the same output ──
        int idemChecked = 0, idemFailed = 0;
        var idemFailures = new List<(string name, string once, string twice)>();
        foreach (var tc in cases)
        {
            string once, twice;
            try
            {
                once  = Format(tc.Input);
                twice = Format(once);
            }
            catch { continue; }
            idemChecked++;
            if (Normalize(once) != Normalize(twice))
            {
                idemFailed++;
                idemFailures.Add((tc.Rule + " / " + tc.Name, once, twice));
            }
        }
        if (idemFailed > 0)
        {
            Console.WriteLine($"\n{Yellow}═══ IDEMPOTENCY FAILURES ═══{Reset}");
            foreach (var (name, once, twice) in idemFailures)
            {
                Console.WriteLine($"\n{Red}● {name}{Reset}");
                PrintDiff(Normalize(once), Normalize(twice));
            }
        }
        string idemColor = idemFailed == 0 ? Green : Red;
        Console.WriteLine($"{idemColor}idempotency: {idemChecked - idemFailed}/{idemChecked} stable{Reset}");

        // ── Summary ───────────────────────────────────────────────────────────
        int total = passed + failed;
        string color = (failed == 0 && idemFailed == 0) ? Green : Red;
        Console.WriteLine($"\n{color}═══ {passed}/{total} passed, {failed} failed ═══{Reset}\n");
        return (failed == 0 && idemFailed == 0) ? 0 : 1;
    }

    static void PrintDiff(string expected, string actual)
    {
        var exp = expected.Split('\n');
        var act = actual.Split('\n');
        int max = Math.Max(exp.Length, act.Length);

        Console.WriteLine($"  {Dim}expected (left) vs actual (right):{Reset}");
        for (int i = 0; i < max; i++)
        {
            string e = i < exp.Length ? exp[i] : "";
            string a = i < act.Length ? act[i] : "";
            if (e == a)
            {
                Console.WriteLine($"    {Dim}{Vis(e)}{Reset}");
            }
            else
            {
                Console.WriteLine($"  {Green}- {Vis(e)}{Reset}");
                Console.WriteLine($"  {Red}+ {Vis(a)}{Reset}");
            }
        }
    }

    // Make tabs/spaces visible in diffs
    static string Vis(string s) => s.Replace("\t", "→   ");

    static string Indent(string s, string prefix) =>
        string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => prefix + l));
}

}
