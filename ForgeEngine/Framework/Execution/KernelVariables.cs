using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>The state behaviors keep between events: the variable store and the trigger-gate store, the waits parked on a binding, the numeric state leases with their end and liveness sweep, and the variable scopes queued for release. It holds and settles its own rows; the controls that write a variable, park a wait or publish a change run in the layers above and reach it as data.</summary>
internal sealed class KernelVariables
{
    private readonly KernelWorld world;
    private readonly KernelRegistry registry;
    private readonly KernelPlans plans;
    private readonly EntityLives entityLives;

    internal KernelVariables(KernelWorld world, KernelRegistry registry, KernelPlans plans, EntityLives entityLives)
    {
        this.world = world;
        this.registry = registry;
        this.plans = plans;
        this.entityLives = entityLives;
    }

    internal Dictionary<(string Provider, string Id), NumericLease> stateLeases = new();
    /// <summary>The same leases by source key, kept beside <see cref="stateLeases"/>: one source key holds one contribution.</summary>
    private Dictionary<LeaseKey, NumericLease> leaseSources = new();

    internal bool HoldsSource(LeaseKey key) => leaseSources.ContainsKey(key);

    internal void AddLease(NumericLease lease)
    {
        stateLeases.Add((lease.Handle.ProviderId, lease.Handle.LeaseId), lease);
        leaseSources.Add(lease.Key, lease);
    }

    internal readonly Stack<List<NumericLease>> leaseScratchPool = new();

    internal void NumericDefinition(string id, string? version = null)
    {
        RuntimeJson.Require(RuntimeJson.IsId(id) && registry.Tables.Capabilities.TryGetValue(id, out _), "state-definition", id);
        var definition = registry.Tables.Capabilities[id];
        RuntimeJson.Require(RuntimeJson.Text(definition, "kind") == "state" && definition.GetProperty("parameters").TryGetProperty("valueType", out var type)
            && type.ValueKind == JsonValueKind.String && type.GetString() == "numeric-contribution", "state-value-type", id);
        if (version != null) RuntimeJson.Require(RuntimeJson.Text(definition, "version") == version, "state-version", id);
    }

    internal void EndLease(NumericLease lease, string status, string code)
    {
        lease.Handle.Status = status; lease.Handle.Code = code;
        if (stateLeases.Remove((lease.Handle.ProviderId, lease.Handle.LeaseId))) leaseSources.Remove(lease.Key);
    }

    internal void CleanStateLifetimes(List<StateLeaseReceipt>? reports)
    {
        if (stateLeases.Count == 0) return;
        // A lease added while this pass runs belongs to the next pass; a lease this pass ends is no longer in the
        // table, and the scratch copy keeps the same snapshot the removed ToArray produced.
        var scratch = leaseScratchPool.Count == 0 ? new List<NumericLease>() : leaseScratchPool.Pop();
        try
        {
            foreach (var lease in stateLeases.Values) scratch.Add(lease);
            for (var index = 0; index < scratch.Count; index++)
            {
                var lease = scratch[index];
                var handle = lease.Handle; var request = handle.Request; string? code = null;
                if (!registry.IsRegistered(handle.ProviderId, lease.Generation) || request.Target.WorldEpoch != world.WorldEpoch) code = "source-lifecycle";
                else if (world.cancelled.Contains((handle.ProviderId, request.ScopeId))) code = "scope-cancelled";
                else if (world.Tick >= handle.ExpiresAtTick) code = "lifetime-ended";
                else code = LeaseLivenessCode(lease);
                if (code == null) continue;
                var status = code == "lifetime-ended" ? "expired" : "cancelled";
                EndLease(lease, status, code);
                reports?.Add(new StateLeaseReceipt(handle.ProviderId, handle.LeaseId, request.DefinitionId, request.Target, status, code));
            }
        }
        finally { scratch.Clear(); leaseScratchPool.Push(scratch); }
    }

    /// <summary>Why one lease's target or source has lost its life, or null while both are current. At a standing
    /// lifecycle generation only the captured kinds are asked — so a kind whose owner reports its ends is answered
    /// from those reports — and where the number moved both references are checked and captured again.</summary>
    internal string? LeaseLivenessCode(NumericLease lease)
    {
        if (lease.Liveness != null && lease.ValidatedGeneration == world.lifecycleGeneration) return entityLives.EntityLivenessCode(lease.Liveness);
        var request = lease.Handle.Request;
        var liveness = new List<EntityCheck>(2);
        try { entityLives.CheckEntity(request.Target, liveness); if (request.Source != null) entityLives.CheckEntity(request.Source, liveness); }
        catch (RuntimeContractException ex) { return ex.Code; }
        lease.Liveness = liveness.ToArray(); lease.ValidatedGeneration = world.lifecycleGeneration;
        return null;
    }

    internal void StopStateSource(string? provider, string? scope, string code)
    {
        if (stateLeases.Count == 0) return;
        foreach (var lease in stateLeases.Values.Where(l => (provider == null || l.Handle.ProviderId == provider) && (scope == null || l.Key.Scope == scope)).ToArray())
            EndLease(lease, "cancelled", code);
    }

    internal RuntimeVariableStore variables = new();

    /// <summary>The trigger option state. It sits beside the variable store because both are host state a
    /// checkpoint carries, and it is a separate table because nothing in it is authored, named or read by a plan.</summary>
    internal RuntimeTriggerGateStore triggerGates = new();

    internal RuntimeTriggerGateStore NewTriggerGateStore()
    {
        var prepared = new RuntimeTriggerGateStore();
        foreach (var loaded in plans.Loaded.Values) prepared.Declare(loaded.Plan.Id, loaded.Plan.Entries);
        return prepared;
    }

    /// <summary>The suspended `g-wait` steps, by the binding they are waiting on. A wait is the dispatch's own
    /// resource: it is released by the timeout, by the event that wakes it, or by the world ending.</summary>
    internal Dictionary<string, List<WaitJob>> waits = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------------------------------------------------
    // Plan lifecycle
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Registers a loaded plan's declarations. Called after the plan parsed, so a conflicting name is a
    /// rejected plan rather than a half-loaded one. The entry points' own trigger options are registered in the same
    /// place and for the same reason: they are a structural property of the plan, proved once at load.</summary>
    internal void DeclareVariables(ResolvedPlan plan)
    {
        variables.Declare(plan.Id, plan.Variables);
        triggerGates.Declare(plan.Id, plan.Entries);
    }

    internal readonly HashSet<(string Kind, string Subject)> queuedScopeReleaseKeys = new();

    internal readonly List<(string Kind, string Subject)> queuedScopeReleases = new();

    internal void FlushScopeReleases()
    {
        if (queuedScopeReleases.Count == 0) return;
        var pending = queuedScopeReleases.ToArray();
        queuedScopeReleases.Clear(); queuedScopeReleaseKeys.Clear();
        foreach (var (kind, subject) in pending) variables.ClearScope(kind, subject);
    }

    /// <summary>This component's part of a host-migration image: every value, latch and gate row. Unlike an export
    /// this settles nothing: an image is not an acknowledgement that the next delta travelled. The native domains'
    /// state belongs to the host layer and is added to this part by the facade.</summary>
    internal void CaptureMigration(Dictionary<string, object?> image)
        => image["variables"] = RuntimeJson.Parse(variables.Save(world.WorldEpoch,
            triggerGates.SaveMigration(world.Tick, SerializeMigrationGateRelease)));

    /// <summary>Decodes an image's variable part into stores declared as this component's are; nothing live changes.
    /// The staged component takes them, and they go live with the one swap.</summary>
    internal (RuntimeVariableStore Variables, RuntimeTriggerGateStore Gates) PrepareMigration(string values, string gates,
        long capturedTick, Func<JsonElement, object?> release)
    {
        var preparedVariables = variables.Fork();
        preparedVariables.Restore(values);
        var preparedGates = NewTriggerGateStore();
        preparedGates.RestoreMigration(gates, capturedTick, release);
        return (preparedVariables, preparedGates);
    }

    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(KernelVariables other)
    {
        (variables, other.variables) = (other.variables, variables);
        (triggerGates, other.triggerGates) = (other.triggerGates, triggerGates);
        (waits, other.waits) = (other.waits, waits);
        (stateLeases, other.stateLeases) = (other.stateLeases, stateLeases);
        (leaseSources, other.leaseSources) = (other.leaseSources, leaseSources);
    }
}
