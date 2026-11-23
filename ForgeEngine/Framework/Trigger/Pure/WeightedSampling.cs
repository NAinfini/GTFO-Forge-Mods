using System;
using System.Collections.Generic;
using System.Numerics;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Pure;

internal enum WeightedSamplingMode { WithoutReplacement, WithReplacement }
internal sealed record WeightedCandidate(EntityReference Target, double Weight);

/// <summary>Selection occurrences, not a Runtime entity-list or a committed effect. Repeated occurrences are explicit.</summary>
internal sealed class WeightedSelection
{
    internal WeightedSelection(EntityReference[] selected, int requested, int input, int eligible,
        int entropyWords, WeightedSamplingMode mode)
    {
        Selected = Array.AsReadOnly(selected); RequestedCount = requested; InputCount = input;
        EligibleCount = eligible; EntropyWords = entropyWords; Mode = mode;
    }
    public IReadOnlyList<EntityReference> Selected { get; }
    public int RequestedCount { get; }
    public int InputCount { get; }
    public int EligibleCount { get; }
    public int ZeroWeightCount => InputCount - EligibleCount;
    public int SelectedCount => Selected.Count;
    public int UnfilledCount => RequestedCount - SelectedCount;
    public int EntropyWords { get; }
    public WeightedSamplingMode Mode { get; }
    public string Code => EligibleCount == 0 ? "no-positive-weight" : UnfilledCount > 0 ? "shortfall" : "selected";
}
/// <summary>Bounded pure sampling with exact integer masses for finite nonnegative binary64 weights.</summary>
internal static class WeightedSampling
{
    public const int MaximumCandidates = 4096;
    public const int MaximumSelections = 256;
    public const int MaximumEntropyWords = 65536;
    public const string Algorithm = "binary64-integer-mass-mulberry32-v1";

    public static WeightedSelection Sample(IReadOnlyList<WeightedCandidate> candidates, int count,
        long seed, WeightedSamplingMode mode, int entropyWordBudget = MaximumEntropyWords)
    {
        var inputCount = candidates?.Count ?? -1;
        if (inputCount < 0 || inputCount > MaximumCandidates)
            throw new RuntimeContractException("weighted-candidate-budget", "At most 4096 explicit candidates are allowed.");
        if (count < 1 || count > MaximumSelections)
            throw new RuntimeContractException("weighted-count", "Requested selections must be in [1,256].");
        if (mode is not (WeightedSamplingMode.WithoutReplacement or WeightedSamplingMode.WithReplacement))
            throw new RuntimeContractException("weighted-mode", "Sampling mode must be explicit.");
        if (entropyWordBudget < 1 || entropyWordBudget > MaximumEntropyWords)
            throw new RuntimeContractException("weighted-entropy-budget", "Entropy word budget must be in [1,65536].");
        var stream = new SeededStream(seed);
        var entries = new List<(EntityReference Target, BigInteger Mass)>();
        var seen = new HashSet<EntityReference>(); var gcd = BigInteger.Zero;
        for (var i = 0; i < inputCount; i++)
        {
            var candidate = candidates![i];
            if (candidate is null || candidate.Target is null)
                throw new RuntimeContractException("weighted-candidate-null", "Candidate and target must be present.");
            var target = RuntimeEntityReferences.Validate(candidate.Target);
            _ = ReferenceCollections.OrderKey(target);
            if (!seen.Add(target)) throw new RuntimeContractException("weighted-duplicate-candidate", "Duplicate full references have ambiguous weights.");
            if (!double.IsFinite(candidate.Weight) || candidate.Weight < 0)
                throw new RuntimeContractException("weighted-weight", "Weights must be finite and nonnegative.");
            if (candidate.Weight == 0) continue;
            var mass = Mass(candidate.Weight);
            gcd = BigInteger.GreatestCommonDivisor(gcd, mass);
            entries.Add((target, mass));
        }
        var values = entries.ToArray(); var keys = new string[values.Length];
        var total = BigInteger.Zero;
        for (var i = 0; i < values.Length; i++)
        {
            keys[i] = ReferenceCollections.OrderKey(values[i].Target);
            values[i].Mass /= gcd; total += values[i].Mass;
        }
        Array.Sort(keys, values, StringComparer.Ordinal);
        var selected = new List<EntityReference>(); var used = 0;
        while (selected.Count < count && total > 0)
        {
            var ticket = Below(total, ref stream, ref used, entropyWordBudget); var found = false;
            for (var i = 0; i < values.Length; i++)
            {
                if (ticket < values[i].Mass)
                {
                    selected.Add(values[i].Target); found = true;
                    if (mode == WeightedSamplingMode.WithoutReplacement)
                    { total -= values[i].Mass; values[i].Mass = BigInteger.Zero; }
                    break;
                }
                ticket -= values[i].Mass;
            }
            if (!found) throw new RuntimeContractException("weighted-invariant", "Ticket did not resolve to a positive mass.");
        }
        return new WeightedSelection(selected.ToArray(), count, inputCount, values.Length, used, mode);
    }
    private static BigInteger Mass(double weight)
    {
        // Express each exact binary64 value in units of the smallest positive subnormal.
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(weight));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var fraction = bits & 0x000fffffffffffffUL;
        return exponent == 0 ? new BigInteger(fraction)
            : new BigInteger(fraction | 0x0010000000000000UL) << (exponent - 1);
    }
    private static BigInteger Below(BigInteger bound, ref SeededStream stream, ref int used, int budget)
    {
        if (bound == BigInteger.One) return BigInteger.Zero;
        var bytes = (bound - 1).ToByteArray(isUnsigned: true, isBigEndian: false);
        var high = bytes[^1]; var bitCount = (bytes.Length - 1) * 8;
        while (high > 0) { bitCount++; high >>= 1; }
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var candidate = BigInteger.Zero;
            for (var offset = 0; offset < bitCount; offset += 32)
            {
                if (++used > budget)
                    throw new RuntimeContractException("weighted-entropy-budget", "Sampling exhausted the explicit entropy budget; no result is committed.");
                // Recover the exact word from the existing stream; do not introduce a second RNG.
                var word = (uint)(stream.NextUnit() * 4294967296d);
                var take = Math.Min(32, bitCount - offset);
                if (take < 32) word &= (1u << take) - 1u;
                candidate |= new BigInteger(word) << offset;
            }
            if (candidate < bound) return candidate;
        }
        throw new RuntimeContractException("weighted-rejection-budget", "Bounded rejection sampling exhausted its attempts.");
    }
}
