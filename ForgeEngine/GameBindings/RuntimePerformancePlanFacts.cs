using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>Observed metadata of an already admitted plan set, not author-declared settings.</summary>
internal sealed record RuntimePerformancePlanSnapshot(long Generation, string ContentSha256,
    int Plans, int Subscriptions, bool Selectors, bool Presentation, int FanOutPerHit);

internal static class RuntimePerformancePlanFacts
{
    /// <param name="capabilityOf">The block a binding id carries on this runtime: a plan names its bindings by id alone.</param>
    internal static RuntimePerformancePlanSnapshot Read(IEnumerable<(JsonElement Plan, string Digest)> documents, long generation,
        Func<string, string> capabilityOf)
    {
        var plans = 0; var subscriptions = 0; var selectors = false; var presentation = false; var fanOut = 0;
        var digests = new List<string>();
        foreach (var document in documents)
        {
            plans++; digests.Add(document.Digest.ToLowerInvariant());
            var plan = document.Plan;
            var bindings = plan.GetProperty("bindings").EnumerateArray().Select(binding => capabilityOf(binding.GetString()!)).ToArray();
            foreach (var entry in plan.GetProperty("entrypoints").EnumerateArray())
            {
                subscriptions++;
                foreach (var step in entry.GetProperty("steps").EnumerateArray())
                {
                    var capability = bindings[step.GetProperty("binding").GetInt32()];
                    selectors |= capability.StartsWith("forge.selector.", StringComparison.Ordinal);
                    presentation |= capability.StartsWith("forge.action.presentation.", StringComparison.Ordinal);
                    if (capability == Blocks.ControlFlowSequence.Id
                        && bindings[entry.GetProperty("binding").GetInt32()] == Blocks.TriggerCombatDamageApplied.Id)
                        foreach (var constant in step.GetProperty("constants").EnumerateArray())
                            if (constant.ValueKind == JsonValueKind.Number && constant.TryGetInt32(out var count)) fanOut = Math.Max(fanOut, count);
                }
            }
        }
        digests.Sort(StringComparer.Ordinal);
        var hash = ForgeCore.Hash.Sha256Hex(string.Join("\n", digests));
        return new(generation, hash, plans, subscriptions, selectors, presentation, fanOut);
    }
}
