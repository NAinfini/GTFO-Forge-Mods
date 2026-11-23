using System;
using System.Collections.Generic;

namespace ForgeMap;

internal enum FixedTaskObjectKind { Generator, Hsu, UplinkTerminal, Terminal, DownloadTerminal, GeneratorCluster }
internal enum FixedTaskObjectScope { Objective, Door, Zone }

/// <summary>Native objective collection ordinal, an existing door lock, or an authored zone placement entry.
/// This identity never names a nearest object or a hierarchy traversal ordinal.</summary>
internal sealed record FixedTaskObjectRole(FixedTaskObjectKind Kind, FixedTaskObjectScope Scope,
    int Layer, int ChainIndex, int Index, int Count, bool Receiver, MapObjectReference? TargetDoor)
{
    public string? PlacementId { get; init; }

    public string Key(RoomLocator locator) => Kind + "/" + Scope + "/" + (Scope switch
    {
        FixedTaskObjectScope.Door => TargetDoor!.ToString(),
        FixedTaskObjectScope.Zone => locator.Dimension + "/" + locator.Layer + "/" + locator.LocalIndex + "/" + (Kind == FixedTaskObjectKind.Terminal ? Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : PlacementId),
        _ => Layer + "/" + ChainIndex + "/" + Index + "/" + Receiver
    });

    public static string Wire(FixedTaskObjectKind kind) => kind switch
    {
        FixedTaskObjectKind.Generator => "generator",
        FixedTaskObjectKind.Hsu => "hsu",
        FixedTaskObjectKind.Terminal => "terminal",
        FixedTaskObjectKind.DownloadTerminal => "download-terminal",
        FixedTaskObjectKind.GeneratorCluster => "generator-cluster",
        _ => "uplink-terminal"
    };
}

internal enum TaskObjectMatchKind { Matched, Unmatched, Ambiguous, NotSpawned }
internal readonly record struct TaskObjectCandidate(int NativeIndex, int Ordinal, int InstanceId);
internal readonly record struct TaskObjectMatch(TaskObjectMatchKind Kind, int NativeIndex);

/// <summary>Count changes invalidate the objective ordinal. Duplicated pointers and repeated ordinals are
/// ambiguous even if the count happens to agree. Called once at generation completion, never per tick.
/// A list the game itself orders by placement (a zone's spawned terminals, which every terminal address already
/// indexes) may be shorter than the authored count when the room offers fewer spots than placements: with
/// <c>shortList</c> each ordinal the list reaches matches and only the ordinals past its end are
/// <see cref="TaskObjectMatchKind.NotSpawned"/>, so one missing terminal costs that terminal alone.</summary>
internal static class FixedTaskObjectMatching
{
    public static TaskObjectMatch Match(IReadOnlyList<TaskObjectCandidate> candidates, int ordinal, int expectedCount,
        bool shortList = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (expectedCount <= 0 || ordinal < 0 || ordinal >= expectedCount) return new(TaskObjectMatchKind.Unmatched, -1);
        if (candidates.Count < expectedCount)
        {
            if (!shortList) return new(TaskObjectMatchKind.Unmatched, -1);
            if (ordinal >= candidates.Count) return new(TaskObjectMatchKind.NotSpawned, -1);
        }
        if (candidates.Count > expectedCount) return new(TaskObjectMatchKind.Ambiguous, -1);
        var identities = new HashSet<int>();
        var ordinals = new HashSet<int>();
        var index = -1;
        foreach (var candidate in candidates)
        {
            if (!identities.Add(candidate.InstanceId) || !ordinals.Add(candidate.Ordinal))
                return new(TaskObjectMatchKind.Ambiguous, -1);
            if (candidate.Ordinal == ordinal) index = candidate.NativeIndex;
        }
        return index < 0 ? new(TaskObjectMatchKind.Unmatched, -1) : new(TaskObjectMatchKind.Matched, index);
    }
}
