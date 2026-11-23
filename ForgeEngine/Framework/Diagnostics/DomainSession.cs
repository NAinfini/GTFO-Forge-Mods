using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>The session plumbing every domain package (Map, Enemy, Player, Weapon) needs around its own native
/// session: one report of a cleanup failure that never replaces the primary exception, the owning-thread
/// check, and the collect-the-failure wrapper. The package keeps its own session state; the rules live here once.</summary>
internal static class DomainSession
{
    /// <summary>Attaches <paramref name="cleanup"/> to <paramref name="original"/> and reports it once. A hostile exception
    /// accessor or a broken <paramref name="report"/> must not replace the primary exception, so each failure is swallowed.</summary>
    public static void PreserveCleanupFailure(Exception original, string key, Exception cleanup, Action<string> report)
    {
        string diagnostic = key + ": " + RuntimeDiagnostics.Describe(cleanup);
        try { original.Data[key] = cleanup; }
        catch (Exception attachment) { diagnostic += "; attachment=" + attachment.GetType().Name; }
        try { report(diagnostic); }
        catch (Exception) { } // Last resort: the report sink itself failed, and the primary exception must still propagate unchanged.
    }

    /// <summary>Refuses a call from any thread but the session's owning simulation thread (`wrong-thread`).</summary>
    public static void CheckThread(int owningThreadId, string domain)
    {
        if (Environment.CurrentManagedThreadId != owningThreadId)
            throw new RuntimeContractException("wrong-thread", domain + " session requires its owning simulation thread.");
    }

    /// <summary>Runs one cleanup step and collects its failure instead of throwing, so the later steps still run.</summary>
    public static void Cleanup(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }
}
