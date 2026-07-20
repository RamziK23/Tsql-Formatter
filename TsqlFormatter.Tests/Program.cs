using System;
using System.Linq;
using TsqlFormatter.Tests;

bool verbose    = args.Contains("-v") || args.Contains("--verbose");
string? ruleArg = null;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--rule" && i + 1 < args.Length) ruleArg = args[i + 1];

var cases = TestCases.All();
return TestRunner.RunAll(cases, verbose, ruleArg);
