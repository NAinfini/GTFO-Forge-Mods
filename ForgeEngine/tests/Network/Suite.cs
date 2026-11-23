using System;
using System.Collections.Generic;

namespace ForgeEngine.Network.Tests;

/// <summary>
/// Minimal assertions for the network suite. A failure is recorded with its context instead of thrown, so one run
/// reports every broken expectation rather than only the first.
/// </summary>
internal sealed class Suite
{
    private string _case = "";
    private int _passed;
    private int _failed;
    private int _cases;
    private readonly List<string> _failures = new();

    internal void Case(string name) { _case = name; _cases++; }

    internal void True(bool condition, string message)
    {
        if (condition) { _passed++; return; }
        _failed++;
        _failures.Add(_case + ": " + message);
    }

    internal void False(bool condition, string message) => True(!condition, message);

    internal void Equal<T>(T expected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) { _passed++; return; }
        _failed++;
        _failures.Add(_case + ": " + message + " (expected " + expected + ", got " + actual + ")");
    }

    internal int Report()
    {
        foreach (var failure in _failures) Console.WriteLine("FAIL " + failure);
        Console.WriteLine("network suite: " + _cases + " cases; " + _passed + " assertions passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }
}
