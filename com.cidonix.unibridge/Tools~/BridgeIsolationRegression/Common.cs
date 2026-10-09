using System;
using System.Collections.Generic;

namespace Cidonix.UniBridge.BridgeIsolationRegression;

public sealed class CaseResult
{
    public string Name { get; set; }
    public bool Passed { get; set; }
    public string Detail { get; set; }
    public object Evidence { get; set; }
}

public static class Check
{
    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run(List<CaseResult> cases, string name, Action action)
    {
        try { action(); cases.Add(new CaseResult { Name = name, Passed = true }); }
        catch (Exception ex) { cases.Add(new CaseResult { Name = name, Passed = false, Detail = ex.ToString() }); }
    }
}
