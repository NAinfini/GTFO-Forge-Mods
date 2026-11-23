using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Pure;

internal enum EmptySelectionPolicy { EmitEmpty, Fail }

/// <summary>A bounded slice of supplied references; not world coverage or a gameplay receipt.</summary>
internal sealed class ReferenceSelection
{
    internal ReferenceSelection(EntityReference[] selected, int input, int available, int requested)
    {
        Selected = Array.AsReadOnly(selected);
        InputCount = input; AvailableCount = available; RequestedCount = requested;
    }
    public IReadOnlyList<EntityReference> Selected { get; }
    public int InputCount { get; }
    public int AvailableCount { get; }
    public int RequestedCount { get; }
    public int SelectedCount => Selected.Count;
    public int UnfilledCount => RequestedCount - SelectedCount;
}

/// <summary>Pure algebra on explicit references. No world discovery or validity/authority grant.</summary>
internal static class ReferenceCollections
{
    public const int MaximumCandidates = 4096;
    public const int MaximumSelection = 256;
    public static IReadOnlyList<EntityReference> Distinct(IReadOnlyList<EntityReference> candidates)
        => Array.AsReadOnly(Sorted(Unique(Snapshot(candidates))));
    public static IReadOnlyList<EntityReference> Union(IReadOnlyList<EntityReference> a, IReadOnlyList<EntityReference> b)
    {
        var left = Snapshot(a); var right = Snapshot(b);
        var result = Unique(left); var seen = new HashSet<EntityReference>(result);
        foreach (var reference in right) if (seen.Add(reference)) result.Add(reference);
        return Array.AsReadOnly(Sorted(result));
    }
    public static IReadOnlyList<EntityReference> Intersection(IReadOnlyList<EntityReference> a, IReadOnlyList<EntityReference> b)
        => SetOperation(a, b, true);
    public static IReadOnlyList<EntityReference> Difference(IReadOnlyList<EntityReference> a, IReadOnlyList<EntityReference> b)
        => SetOperation(a, b, false);
    public static ReferenceSelection Limit(IReadOnlyList<EntityReference> candidates, int count)
    {
        SelectionCount(count); var snapshot = Snapshot(candidates);
        return Slice(Unique(snapshot).ToArray(), snapshot.Length, count);
    }
    /// <summary>One random draw of <paramref name="count"/> targets. Without repeats the draws are a slice of one
    /// seeded permutation, so a candidate appears at most once and the answer is at most the candidate count long;
    /// with repeats every draw is independent, so the count is filled even from a smaller candidate set and the
    /// same candidate may come back more than once — which is exactly what the option asks for. The stream is
    /// constructed either way, so a seed outside the game's own unsigned range is refused before anything is
    /// drawn.</summary>
    public static ReferenceSelection Random(IReadOnlyList<EntityReference> candidates, long seed, int count, bool allowRepeats = false)
    {
        SelectionCount(count); var snapshot = Snapshot(candidates);
        if (!allowRepeats) return Slice(Permuted(snapshot, seed), snapshot.Length, count);
        var available = Sorted(Unique(snapshot));
        var stream = new SeededStream(seed);
        if (available.Length == 0) return new ReferenceSelection(Array.Empty<EntityReference>(), snapshot.Length, 0, count);
        var selected = new EntityReference[count];
        for (var i = 0; i < selected.Length; i++) selected[i] = available[(int)(stream.NextUnit() * available.Length)];
        return new ReferenceSelection(selected, snapshot.Length, available.Length, count);
    }
    /// <summary>The weighted form of the same draw: one finite, non-negative weight per candidate, sampled without
    /// replacement unless repeats are allowed. A candidate with weight 0 is never drawn, and a set whose weights are
    /// all 0 answers empty rather than a candidate nobody weighted.</summary>
    public static IReadOnlyList<EntityReference> Weighted(IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> weights, long seed, int count, bool allowRepeats = false)
    {
        var snapshot = Snapshot(candidates);
        if (weights is null || weights.Count != snapshot.Length)
            throw new RuntimeContractException("weighted-weight", "One weight is required per candidate.");
        var weighted = new WeightedCandidate[snapshot.Length];
        for (var index = 0; index < weighted.Length; index++) weighted[index] = new WeightedCandidate(snapshot[index], weights[index]);
        return WeightedSampling.Sample(weighted, count, seed,
            allowRepeats ? WeightedSamplingMode.WithReplacement : WeightedSamplingMode.WithoutReplacement).Selected;
    }
    /// <summary>The website selector's structural empty policy: fail rejects an empty result, emit-empty passes it on.</summary>
    public static IReadOnlyList<EntityReference> ApplyEmptyPolicy(IReadOnlyList<EntityReference> selected, EmptySelectionPolicy policy)
    {
        if (selected is null)
            throw new RuntimeContractException("pure-collection-null", "Selection must not be null.");
        return policy switch
        {
            EmptySelectionPolicy.EmitEmpty => selected,
            EmptySelectionPolicy.Fail => selected.Count == 0
                ? throw new RuntimeContractException("pure-empty-selection", "Empty selection rejected by its empty policy.") : selected,
            _ => throw new RuntimeContractException("pure-operation", "Unknown empty selection policy.")
        };
    }
    private static IReadOnlyList<EntityReference> SetOperation(IReadOnlyList<EntityReference> a,
        IReadOnlyList<EntityReference> b, bool intersection)
    {
        var left = Snapshot(a); var right = Snapshot(b);
        var membership = new HashSet<EntityReference>(right); var result = new List<EntityReference>();
        foreach (var reference in Unique(left))
            if (membership.Contains(reference) == intersection) result.Add(reference);
        return Array.AsReadOnly(Sorted(result));
    }
    private static EntityReference[] Snapshot(IReadOnlyList<EntityReference> candidates)
    {
        if (candidates is null)
            throw new RuntimeContractException("pure-collection-null", "Candidate collection must not be null.");
        if (candidates.Count < 0 || candidates.Count > MaximumCandidates)
            throw new RuntimeContractException("pure-collection-budget", "Candidate input exceeds 4096 references.");
        var result = new EntityReference[candidates.Count];
        // Validate the whole input before filtering, deduplication or limit; never hide a bad tail.
        for (var i = 0; i < result.Length; i++)
        {
            if (candidates[i] is null)
                throw new RuntimeContractException("pure-reference-null", "A candidate reference is null.");
            var reference = RuntimeEntityReferences.Validate(candidates[i]);
            ValidateUnicode(reference.Id); result[i] = reference;
        }
        return result;
    }
    private static void ValidateUnicode(string id)
    {
        for (var i = 0; i < id.Length; i++)
        {
            if (!char.IsSurrogate(id[i])) continue;
            if (!char.IsHighSurrogate(id[i]) || i + 1 >= id.Length || !char.IsLowSurrogate(id[i + 1]))
                throw new RuntimeContractException("pure-reference-encoding", "Unpaired UTF-16 surrogate in entity ID.");
            i++;
        }
    }
    private static List<EntityReference> Unique(EntityReference[] candidates)
    {
        var seen = new HashSet<EntityReference>(); var result = new List<EntityReference>(candidates.Length);
        foreach (var reference in candidates) if (seen.Add(reference)) result.Add(reference);
        return result;
    }
    private static EntityReference[] Sorted(List<EntityReference> values)
    {
        if (values.Count > MaximumCandidates)
            throw new RuntimeContractException("pure-collection-output-budget", "Distinct output exceeds 4096; no partial output.");
        var result = values.ToArray(); var keys = new string[result.Length];
        for (var i = 0; i < result.Length; i++) keys[i] = OrderKey(result[i]);
        Array.Sort(keys, result, StringComparer.Ordinal); return result;
    }
    internal static string OrderKey(EntityReference reference)
    {
        ValidateUnicode(reference.Id);
        var id = reference.Id.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return string.Create(CultureInfo.InvariantCulture, $"[{reference.WorldEpoch},\"{id}\",{reference.LifeEpoch}]");
    }
    private static EntityReference[] Permuted(EntityReference[] candidates, long seed)
    {
        var stream = new SeededStream(seed); // Validate even with zero or one candidate.
        var result = Sorted(Unique(candidates));
        for (var i = result.Length - 1; i > 0; i--)
        {
            var j = (int)(stream.NextUnit() * (i + 1));
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }
    private static ReferenceSelection Slice(EntityReference[] available, int input, int count)
    {
        var selected = new EntityReference[Math.Min(count, available.Length)];
        Array.Copy(available, selected, selected.Length);
        return new ReferenceSelection(selected, input, available.Length, count);
    }
    private static void SelectionCount(int count)
    {
        if (count < 1 || count > MaximumSelection)
            throw new RuntimeContractException("pure-selection-count", "Requested selection must be in [1, 256].");
    }
}
