using System;
using System.Collections.Generic;
using System.Linq;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>The plans the kernel holds: the loaded-plan table, each plan's per-tick budget cell, and the subscription
/// table derived from them. It reads the registry below it; the facade drives a load or an unload through it and
/// tells the layers above what changed.</summary>
internal sealed class KernelPlans
{
    private readonly KernelRegistry registry;
    internal KernelPlans(KernelRegistry registry) { this.registry = registry; }

    internal readonly Dictionary<string, LoadedPlan> Loaded = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Subscription> Subscriptions = new(StringComparer.Ordinal);
    internal readonly PlanTickUsageTable TickUsage = new();
    /// <summary>Raised every time the subscription table is rebuilt.</summary>
    internal long SubscriptionGeneration;
    /// <summary>Plans fully parsed since the kernel was created (a reused plan is not counted).</summary>
    internal long Parsed;
    /// <summary>The receiver triggers a loaded card's `end_on` abilities name. An ending is a third consumer of a
    /// binding besides a mounted entry and a `g-wait`: the event is walked so the effects it ends end on the tick it
    /// lands in, and no entry point is dispatched for it.</summary>
    internal readonly HashSet<string> EndOnBindings = new(StringComparer.Ordinal);
    /// <summary>The bindings a loaded plan reads through a `query` step. A read has no subscription of its own, so this
    /// is the one place a provider learns that a plan can ask it something — rebuilt with the subscriptions.</summary>
    internal readonly HashSet<string> QueryBindings = new(StringComparer.Ordinal);
    /// <summary>Per binding, per structural enum parameter, the member names the loaded plans mounted on that
    /// binding actually compiled. Rebuilt with the subscription table, never per tick: a trigger whose parameter is
    /// a subscription key reads the exact set it has to observe from here.</summary>
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> subscribedEnumMembers = new(StringComparer.Ordinal);

    internal void RebuildSubscriptions()
    {
        TickUsage.Synchronize(Loaded);
        Subscriptions.Clear();
        EndOnBindings.Clear();
        QueryBindings.Clear();
        subscribedEnumMembers.Clear();
        var mounted = new Dictionary<string, List<Work>>(StringComparer.Ordinal);
        foreach (var plan in Loaded.Values.Where(ModulesLive).OrderBy(p => p.Plan.Id, StringComparer.Ordinal))
            foreach (var entry in plan.Plan.Entries)
            {
                if (!mounted.TryGetValue(entry.BindingId, out var work)) mounted.Add(entry.BindingId, work = new());
                // The template item names no subject: which entities a dispatch is about is decided by the mount
                // comparison of that dispatch, and the claimed copy carries the answer.
                work.Add(new Work(plan, entry, Array.Empty<EntityReference>()));
                // A trigger's structural enum members are a subscription property like the binding itself: the
                // provider that observes the trigger reads this table instead of scanning the whole set, and the
                // table is rebuilt exactly here, where the plans that carry them become facts.
                if (entry.EnumParameters is { Count: > 0 } members)
                    foreach (var (parameter, member) in members)
                    {
                        if (!subscribedEnumMembers.TryGetValue(entry.BindingId, out var byParameter))
                            subscribedEnumMembers.Add(entry.BindingId, byParameter = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
                        if (!byParameter.TryGetValue(parameter, out var values))
                            byParameter.Add(parameter, values = new HashSet<string>(StringComparer.Ordinal));
                        values.Add(member);
                    }
                // The `end_on` abilities of the same plan, resolved at load to the receiver's trigger binding: a
                // card that ends an effect when its receiver dies consumes that trigger whether or not it mounts an
                // entry point of its own on it.
                foreach (var step in entry.Steps)
                {
                    if (step.Effect is { } effect)
                        foreach (var ending in effect.EndTriggers)
                            if (EndOnBindings.Add(ending.BindingId))
                            {
                                var view = registry.BindingViews[ending.BindingId];
                                RuntimeJson.Require(view.EndSubjectIndex == ending.SubjectOutput
                                    && ending.SubjectOutput >= 0
                                    && view.Outputs[ending.SubjectOutput].Id == ending.Subject.Id,
                                    "effect-end-on", ending.BindingId);
                            }
                    // A `query` step is a consumer of the binding it reads: the provider that answers it may keep the
                    // state the read needs only while some loaded plan can make it.
                    if (step.NodeKind == "query") QueryBindings.Add(step.BindingId);
                }
            }
        foreach (var (binding, work) in mounted) Subscriptions.Add(binding, new Subscription(work.ToArray()));
        SubscriptionGeneration++;
    }

    /// <summary>Whether every module a loaded plan was resolved against is still registered under the same token.
    /// The answer only changes when the module table does, so a plan checked at the current table version is not
    /// asked again.</summary>
    internal bool ModulesLive(LoadedPlan plan)
    {
        if (plan.ModulesCheckedAt == registry.ModuleTableVersion) return true;
        foreach (var module in plan.Modules)
            if (!registry.IsRegistered(module.Key, module.Value)) return false;
        plan.ModulesCheckedAt = registry.ModuleTableVersion;
        return true;
    }

    /// <summary>The structural enum members the loaded plans mounted on one binding name, ordinal ascending.</summary>
    internal IReadOnlyList<string> SubscribedEnumMembers(string bindingId, string parameterId)
    {
        if (!subscribedEnumMembers.TryGetValue(bindingId, out var byParameter) || !byParameter.TryGetValue(parameterId, out var members))
            return Array.Empty<string>();
        return members.OrderBy(member => member, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Whether a work item still names the plan that is loaded under its id. Unloading a plan does not walk
    /// the queue, so a migration image asks this to capture the ownership the next dispatch would see.</summary>
    internal bool WorkLive(Work item) => Loaded.TryGetValue(item.Plan.Plan.Id, out var loaded) && ReferenceEquals(loaded, item.Plan);

    /// <summary>The load-bound declarations a detached migration image reads. No staged decoder writes their state.</summary>
    internal void CopyLoadBoundTo(KernelPlans image)
    {
        foreach (var pair in Loaded) image.Loaded.Add(pair.Key, pair.Value);
        foreach (var pair in Subscriptions) image.Subscriptions.Add(pair.Key, pair.Value);
        image.EndOnBindings.UnionWith(EndOnBindings);
        image.QueryBindings.UnionWith(QueryBindings);
    }
}
