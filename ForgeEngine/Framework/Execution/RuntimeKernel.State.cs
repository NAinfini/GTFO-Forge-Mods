using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    public const int MaximumStateLeases = 512;
    internal sealed record LeaseKey(string Provider, EntityReference Target, EntityReference? Source,
        string Scope, string Definition, string StackGroup);
    internal sealed record NumericLease(RuntimeStateLeaseHandle Handle, long Generation, LeaseKey Key, string Order)
    {
        /// <summary>The target and source as they were checked, with the kind that answered for each, and the lifecycle
        /// generation of that check. A pass at the same generation asks only these; a pass after the number moved checks
        /// the references again against the kinds registered now.</summary>
        internal EntityCheck[]? Liveness;
        internal long ValidatedGeneration;
    }
    internal NumericLeaseResult AcquireNumericLease(RuntimeModuleHandle owner, NumericLeaseRequest request)
    {
        Thread();
        try
        {
            RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
            RuntimeJson.Require(world.worldStarted && world.worldHost == true, "not-host", "Numeric state requires an authoritative simulation tick.");
            RuntimeJson.Text(request.LeaseId); RuntimeJson.Text(request.ScopeId); RuntimeJson.Text(request.StackGroup);
            RuntimeJson.Text(request.DefinitionVersion); kernelVariables.NumericDefinition(request.DefinitionId, request.DefinitionVersion);
            RuntimeJson.Integer(request.DurationTicks, 1);
            RuntimeJson.Require(request.DurationTicks <= RuntimeJson.MaxSafeInteger - CurrentTick, "lease-overflow", request.LeaseId);
            RuntimeJson.Require(double.IsFinite(request.Additive) && double.IsFinite(request.Multiplier) && request.Multiplier >= 0, "state-number", request.LeaseId);
            RuntimeJson.Require(!world.cancelled.Contains((owner.ProviderId, request.ScopeId)), "scope-cancelled", request.ScopeId);
            var liveness = new List<EntityCheck>(2);
            entityLives.CheckEntity(request.Target, liveness); if (request.Source != null) entityLives.CheckEntity(request.Source, liveness);
            var key = new LedgerKey(owner.ProviderId, LedgerSpace.Lease, request.LeaseId);
            var fingerprint = LedgerFingerprint(RuntimeJson.StableText(RuntimeJson.From(request)));
            if (eventQueue.history.TryGetValue(key, out var previous)) return new NumericLeaseResult(previous.Fingerprint == fingerprint ? "duplicate" : "rejected", previous.Fingerprint == fingerprint ? "duplicate-lease" : "lease-id-conflict", null);
            RuntimeJson.Require(kernelFlow.LedgerHasRoom(), "event-history-budget", "Replay ledger capacity reached.");
            // Cleanup precedes capacity/key checks so expired contributions never block a new source lease.
            kernelVariables.CleanStateLifetimes(null);
            RuntimeJson.Require(kernelVariables.stateLeases.Count < MaximumStateLeases, "state-lease-budget", "Active state lease capacity reached.");
            var sourceKey = new LeaseKey(owner.ProviderId, request.Target, request.Source, request.ScopeId, request.DefinitionId, request.StackGroup);
            RuntimeJson.Require(!kernelVariables.HoldsSource(sourceKey), "state-source-conflict", "A source key has one contribution; release it before creating a new lease.");
            var handle = new RuntimeStateLeaseHandle(this, owner.ProviderId, request) { ExpiresAtTick = CurrentTick + request.DurationTicks };
            eventQueue.history.Add(key, new LedgerRow(fingerprint, Math.Max(0, CurrentTick)));
            kernelVariables.AddLease(new NumericLease(handle, owner.Generation, sourceKey, RuntimeJson.StableText(RuntimeJson.From(sourceKey)))
            { Liveness = liveness.ToArray(), ValidatedGeneration = world.lifecycleGeneration });
            return new NumericLeaseResult("acquired", "contribution-recorded", handle);
        }
        catch (RuntimeContractException ex) { return new NumericLeaseResult("rejected", ex.Code, null); }
    }
    internal bool ReleaseLease(RuntimeStateLeaseHandle handle)
    {
        // Terminal handle disposal is cleanup, not new runtime work.
        ReadThread(); NoLifecycleMutation();
        if (!kernelVariables.stateLeases.TryGetValue((handle.ProviderId, handle.LeaseId), out var lease) || !ReferenceEquals(lease.Handle, handle)) return false;
        kernelVariables.EndLease(lease, "released", "explicit-release"); return true;
    }
    public NumericStateResult EvaluateNumericState(EntityReference target, string definitionId, string stackGroup, double baseValue)
    {
        Thread(); RuntimeJson.Require(world.worldStarted && world.worldHost == true, "not-host", "Numeric state requires an authoritative simulation tick.");
        kernelVariables.NumericDefinition(definitionId); RuntimeJson.Text(stackGroup); RuntimeJson.Require(double.IsFinite(baseValue), "state-number", "Base value must be finite.");
        entityLives.CheckEntity(target); kernelVariables.CleanStateLifetimes(null);
        var additive = 0d; var multiplier = 1d; var count = 0;
        // Stable source order makes the result independent of registration/acquisition order. Never undo by division.
        foreach (var lease in kernelVariables.stateLeases.Values.Where(l => l.Key.Target == target && l.Key.Definition == definitionId && l.Key.StackGroup == stackGroup).OrderBy(l => l.Order, StringComparer.Ordinal))
        { additive += lease.Handle.Request.Additive; multiplier *= lease.Handle.Request.Multiplier; count++; }
        var value = (baseValue + additive) * multiplier;
        RuntimeJson.Require(double.IsFinite(additive) && double.IsFinite(multiplier) && double.IsFinite(value), "state-overflow", definitionId);
        return new NumericStateResult(baseValue, additive, multiplier, value, count);
    }
}
