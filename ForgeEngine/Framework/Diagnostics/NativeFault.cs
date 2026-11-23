using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>One native failure site and its total observations. A snapshot is detached from the live counters.</summary>
internal readonly record struct NativeFaultSnapshot(string SiteId, long Count, string FirstError);

/// <summary>Reports recoverable native adapter failures once per site while retaining their counts for author tools.</summary>
internal static class NativeFault
{
    private sealed class Site
    {
        internal Site(Exception first)
        {
            First = first;
            FirstError = first.GetType().Name + ": " + first.Message;
        }

        internal Exception? First;
        internal string FirstError;
        internal long Count;
        internal bool Warned;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Site> Sites = new(StringComparer.Ordinal);
    private static Action<string, Exception>? warningSink;

    /// <summary>Sets the process's BepInEx warning writer. Faults seen before startup are reported once here.</summary>
    public static void SetWarningSink(Action<string, Exception>? sink)
    {
        List<(string Id, Exception Error)>? pending = null;
        lock (Gate)
        {
            warningSink = sink;
            if (sink == null) return;
            foreach (var (id, site) in Sites)
            {
                if (site.Warned || site.First == null) continue;
                site.Warned = true;
                (pending ??= new()).Add((id, site.First));
                site.First = null;
            }
        }
        if (pending != null)
            foreach (var (id, error) in pending) Warn(sink, id, error);
    }

    /// <summary>Records a failure without formatting or writing a second warning at the same fixed call site.</summary>
    public static void Note(string siteId, Exception error)
    {
        Action<string, Exception>? sink = null;
        Exception? first = null;
        lock (Gate)
        {
            if (!Sites.TryGetValue(siteId, out var site)) Sites.Add(siteId, site = new Site(error));
            if (site.Count < long.MaxValue) site.Count++;
            if (!site.Warned && warningSink != null)
            {
                site.Warned = true;
                sink = warningSink;
                first = site.First;
                site.First = null;
            }
        }
        if (sink != null && first != null) Warn(sink, siteId, first);
    }

    /// <summary>Returns site counts for ForgeDevelopment without exposing mutable bookkeeping.</summary>
    public static IReadOnlyList<NativeFaultSnapshot> Snapshot()
    {
        lock (Gate)
        {
            var result = new NativeFaultSnapshot[Sites.Count];
            var index = 0;
            foreach (var (id, site) in Sites)
                result[index++] = new NativeFaultSnapshot(id, site.Count, site.FirstError);
            Array.Sort(result, (left, right) => StringComparer.Ordinal.Compare(left.SiteId, right.SiteId));
            return Array.AsReadOnly(result);
        }
    }

    private static void Warn(Action<string, Exception> sink, string id, Exception error)
    {
        try { sink(id, error); }
        catch (Exception) { /* Logging cannot replace the native failure being handled; Snapshot keeps the fault. */ }
    }
}
