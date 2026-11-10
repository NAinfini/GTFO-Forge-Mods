using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeReplay;

/// <summary>What a replay could not restore and skipped so the rest plays on: an enemy, item, gear, object or event that this game's data,
/// mods or level do not have (or have otherwise than the recording). Each different thing counts once; the first of each reason is written
/// to the log with its example at once, and every reason with its count when the replay ends; the replay screen shows the count as a quiet
/// notice. A thing that comes right after all (a spawn retried a second later, a terminal found once the level had it) is resolved and no
/// longer counts: what is reported is what stayed unrestored. Main thread only.</summary>
internal static class Skipped
{
    private static readonly Dictionary<string, (string Reason, string Example)> Pending = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Reasons = new(StringComparer.Ordinal);

    /// <summary>How many different things are still unrestored in this replay.</summary>
    internal static int Count => Pending.Count;

    /// <summary>One thing that could not be restored: <paramref name="reason"/> is the fixed kind, <paramref name="example"/> which one
    /// (an ID, a place, the game's error).</summary>
    internal static void Note(string reason, string example)
    {
        var key = reason + "\n" + example;
        if (!Pending.TryAdd(key, (reason, example))) return;
        Reasons.TryGetValue(reason, out var count);
        Reasons[reason] = count + 1;
        if (count == 0) Plugin.Logger.Error("Replay skipped what it could not restore: " + reason + " (first: " + example + ").");
    }

    /// <summary>The thing noted earlier was restored after all (a retry succeeded): it stops counting and is not reported.</summary>
    internal static void Resolve(string reason, string example)
    {
        if (!Pending.Remove(reason + "\n" + example)) return;
        if (!Reasons.TryGetValue(reason, out var count)) return;
        if (count <= 1) Reasons.Remove(reason); else Reasons[reason] = count - 1;
    }

    /// <summary>The replay ended: the reasons with their counts, once, and a clean count for the next replay.</summary>
    internal static void End()
    {
        if (Reasons.Count > 0)
            Plugin.Logger.Error("The replay skipped " + Pending.Count + " things it could not restore: " + string.Join("; ", Reasons.Select(r => r.Key + " x" + r.Value)) + ".");
        Pending.Clear(); Reasons.Clear();
    }
}
