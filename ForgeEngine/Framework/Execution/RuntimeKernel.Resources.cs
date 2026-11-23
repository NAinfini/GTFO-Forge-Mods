using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.Framework;

/// <summary>
/// The runtime resources a provider owns, and the one budgeted read a `query` step reaches them through. A
/// resource is not discovered here: the kernel holds no world and scans nothing, so a kind is answerable only
/// because the package that knows it registered its own provider — the same ownership rule entity candidates
/// follow. A kind nobody registered is a refusal with a code, never an empty answer, because an empty list would
/// be indistinguishable from a world that really holds none.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>Resources one read may answer with, for the same reason an entity enumeration is bounded: a longer
    /// answer is refused rather than truncated, since a short list reads as a smaller world.</summary>
    public const int MaximumResourcesPerRead = 256;
    private RuntimeResources RejectedResources(string code)
        => new("rejected", code, Lifecycle, Array.Empty<ResourceRef>());

    /// <summary>Every resource of one kind the provider that owns that kind currently holds. This is the resource
    /// half of <see cref="EnumerateEntityCandidates"/>: the provider's own table, read under the query budget,
    /// with a table that throws or answers with another kind's reference refused by name. A kind is a member of the
    /// shared resource-kind vocabulary and not a dotted id, so a name no provider owns is answered the same way an
    /// absent optional package is: `resource-unavailable`, never a refusal that reads like a contract violation.</summary>
    public RuntimeResources EnumerateResources(string kind)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(kind);
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return RejectedResources("runtime-not-ready");
        if (!registry.Tables.ResourceProviders.TryGetValue(kind, out var provider))
            return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
        ResetEntityBudget();
        if (!ResourceBudgetAvailable(0)) return RejectedResources("entity-query-tick-budget");
        ResourceRef[] captured;
        observingEntities = true;
        try
        {
            IReadOnlyList<ResourceRef> answered;
            try { answered = provider.Provider.Enumerate(); }
            catch (Exception error)
            {
                log.LogCallbackFailed("resource-provider-failed kind=" + kind, error);
                return RejectedResources("resource-provider-failed");
            }
            if (answered == null) return RejectedResources("resource-provider-failed");
            captured = answered.ToArray();
            foreach (var reference in captured)
            {
                try { ValidateResourceAnswer(kind, reference); }
                catch (RuntimeContractException error) { return RejectedResources(error.Code); }
            }
        }
        finally { observingEntities = false; }
        if (captured.Length > MaximumResourcesPerRead || !ResourceBudgetAvailable(captured.Length))
            return RejectedResources("entity-query-budget");
        if (!ChargeEntityQuery(captured.Length)) return RejectedResources("entity-query-tick-budget");
        return new("complete", "resources-enumerated", Lifecycle, captured);
    }

    /// <summary>Resolves a set of ids through the owner of their kind, in one budgeted read. A provider that
    /// answers nothing for an id it does not hold is the one refusal this read reports as `resource-unavailable`:
    /// the reference is not there, which is a different fact from a spent budget or a malformed table.</summary>
    public RuntimeResources ResolveResources(string kind, IReadOnlyList<string> resourceIds)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(kind); ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Count > MaximumResourcesPerRead) return RejectedResources("entity-query-budget");
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return RejectedResources("runtime-not-ready");
        if (!registry.Tables.ResourceProviders.TryGetValue(kind, out var provider))
            return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
        ResetEntityBudget();
        if (!ResourceBudgetAvailable(resourceIds.Count)) return RejectedResources("entity-query-tick-budget");
        var captured = new ResourceRef[resourceIds.Count];
        observingEntities = true;
        try
        {
            for (var index = 0; index < resourceIds.Count; index++)
            {
                var id = RuntimeJson.Text(resourceIds[index]);
                ResourceRef? reference;
                try { reference = provider.Provider.Resolve(id); }
                catch (Exception error)
                {
                    log.LogCallbackFailed("resource-provider-failed kind=" + kind + " id=" + id, error);
                    return RejectedResources("resource-provider-failed");
                }
                if (reference == null) return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
                try { ValidateResourceAnswer(kind, reference); }
                catch (RuntimeContractException error) { return RejectedResources(error.Code); }
                captured[index] = reference;
            }
        }
        finally { observingEntities = false; }
        if (!ChargeEntityQuery(resourceIds.Count)) return RejectedResources("entity-query-tick-budget");
        return new("complete", "resources-resolved", Lifecycle, captured);
    }

    /// <summary>The same read for references the caller already holds: every one of them must still be there, and
    /// each is checked against the kind it names rather than against a kind the caller supplies.</summary>
    public RuntimeResources ResolveResourceRefs(IReadOnlyList<ResourceRef> references)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(references);
        if (references.Count > MaximumResourcesPerRead) return RejectedResources("entity-query-budget");
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return RejectedResources("runtime-not-ready");
        ResetEntityBudget();
        if (!ResourceBudgetAvailable(references.Count)) return RejectedResources("entity-query-tick-budget");
        var captured = new ResourceRef[references.Count];
        observingEntities = true;
        try
        {
            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                try { ValidateResourceAnswer(reference?.ResourceKind ?? "", reference); }
                catch (RuntimeContractException error) { return RejectedResources(error.Code); }
                if (!registry.Tables.ResourceProviders.TryGetValue(reference!.ResourceKind, out var provider))
                    return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
                ResourceRef? current;
                try { current = provider.Provider.Resolve(reference.ResourceId); }
                catch (Exception error)
                {
                    log.LogCallbackFailed("resource-provider-failed kind=" + reference.ResourceKind + " id=" + reference.ResourceId, error);
                    return RejectedResources("resource-provider-failed");
                }
                if (current == null) return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
                captured[index] = current;
            }
        }
        finally { observingEntities = false; }
        if (!ChargeEntityQuery(references.Count)) return RejectedResources("entity-query-tick-budget");
        return new("complete", "resources-resolved", Lifecycle, captured);
    }

    /// <summary>One provider's answer is only trusted after the same routed check a published reference passes:
    /// the kind must be the one that was asked for, and the reference must carry a usable id.</summary>
    private static void ValidateResourceAnswer(string kind, ResourceRef? reference)
    {
        RuntimeJson.Require(reference != null, RuntimeAbiCodes.ResourceUnavailable, kind);
        RuntimeJson.Require(reference!.ResourceKind == kind, RuntimeAbiCodes.ResourceKind, reference.ResourceId);
        RuntimeJson.Text(reference.ResourceId);
    }

    /// <summary>The text one written sheet renders to, before its event is admitted: what the payload budget judges.</summary>
    internal ReadOnlySpan<byte> SheetText(EvaluationOutputs sheet) => executor.SheetText(sheet);
    internal bool ToleratesMissing(string kind) => executor.ToleratesMissing(kind);
    internal ResourceRef? ResolveCompiledResource(string kind, string resourceId) => executor.ResolveCompiledResource(kind, resourceId);
    internal void RequireResourceRevision(string kind, string resourceId, string revision, string at) => executor.RequireResourceRevision(kind, resourceId, revision, at);

    /// <summary>How many cost release or commit calls failed, and the last one. Other holdings are still settled;
    /// the author layer can read the failure code and item rather than assume every amount was confirmed.</summary>
    public long CostFaultCount => executor.CostFaultCount;
    public RuntimeCostFault? LastCostFault => executor.LastCostFault;
}
