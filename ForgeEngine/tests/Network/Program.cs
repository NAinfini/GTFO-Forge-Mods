using System;
using System.Collections.Generic;

namespace ForgeEngine.Network.Tests;

/// <summary>
/// The suite's entry point. Every group runs the engine's network on ForgeCore's real node over an in-memory session and
/// reports a count; a failure is an exit code, so a build script can gate on it.
/// </summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        var suite = new Suite();
        // ForgeCore catches what a message handler throws and writes it as an error: collected, so a check cannot pass over one.
        var errors = new List<string>();
        ForgeCore.Log.Sinks = _ => errors.Add;
        Group("handshake", () => HandshakeChecks.Run(suite));
        Group("state", () => EngineNetChecks.State(suite));
        Group("step", () => EngineNetChecks.Step(suite));
        Group("presentation", () => EngineNetChecks.Presentation(suite));
        Group("migration", () => EngineNetChecks.Migration(suite));
        Group("owner", () => EngineNetChecks.Owner(suite));
        suite.Case("suite: no handler failed");
        var failed = errors.FindAll(error => error.Contains("-failed"));
        suite.True(failed.Count == 0, "a network handler threw: " + string.Join(" || ", failed));
        return suite.Report();
    }

    /// <summary>Runs one group and names it first, so a group that never returns is named by the run rather than by
    /// whoever reads a stack trace afterwards.</summary>
    private static void Group(string name, Action run)
    {
        Console.WriteLine(":: " + name);
        Console.Out.Flush();
        run();
    }
}
