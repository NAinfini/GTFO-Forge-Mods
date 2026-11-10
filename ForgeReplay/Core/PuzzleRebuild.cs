using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>How a jump rebuilds the scans and clusters from the state at the moment, whatever they showed before: their game handlers change one
/// another (a cluster switches its scans, a reveal running on game time sets its scan waiting), so what was last set says nothing about what they
/// show now. Every one the recording has touched goes off first, clusters before scans, then each takes its state, clusters first so a scan's own state
/// has the last word; a scan with no state at this moment is off again last. The holopath is no step: scan and cluster handlers draw and hide their own.</summary>
internal static class PuzzleRebuild
{
    /// <summary>One step: put the object off (<paramref name="Payload"/> null) or into the recorded state event.</summary>
    internal readonly record struct Step(ushort Kind, ulong Key, byte[]? Payload);

    internal static bool Owns(ushort kind) => kind is EventKind.Cluster or EventKind.Scan;

    internal static List<Step> Steps(IEnumerable<(ushort Kind, ulong Key)> touched, WorldState state)
    {
        var steps = new List<Step>();
        var clusters = new List<ulong>();
        var scans = new List<ulong>();
        foreach (var (kind, key) in touched)
        {
            if (kind == EventKind.Cluster) clusters.Add(key);
            else if (kind == EventKind.Scan) scans.Add(key);
        }
        foreach (var key in clusters) steps.Add(new Step(EventKind.Cluster, key, null));
        foreach (var key in scans) steps.Add(new Step(EventKind.Scan, key, null));
        foreach (var (id, payload) in state.Extras) if (id.Item1 == EventKind.Cluster) steps.Add(new Step(id.Item1, id.Item2, payload));
        foreach (var (id, payload) in state.Objects) if (id.Item1 == EventKind.Scan) steps.Add(new Step(id.Item1, id.Item2, payload));
        foreach (var key in scans) if (!state.Objects.ContainsKey((EventKind.Scan, key))) steps.Add(new Step(EventKind.Scan, key, null));
        return steps;
    }
}
