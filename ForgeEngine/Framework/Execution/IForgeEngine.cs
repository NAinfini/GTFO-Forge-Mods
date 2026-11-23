using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>The shared provider API. The host alone controls startup, worlds, plans, ticks and replicated state.</summary>
public interface IForgeEngine
{
    RuntimeIdentity Identity { get; }
    RuntimeLimits Limits { get; }
    long WorldEpoch { get; }
    long CurrentTick { get; }
    double TicksPerSecond { get; }
    int QueuedEvents { get; }
    int LoadedPlans { get; }
    long SubscriptionGeneration { get; }
    RuntimeStartupState StartupState { get; }
    /// <summary>Null while fact admission is open; otherwise runtime-suspended or runtime-stopped.
    /// This does not replace domain native readiness or alter host-migration buffering.</summary>
    string? FactAdmissionClosure { get; }
    RuntimeLifecycleSnapshot Lifecycle { get; }
    bool IsRegistrationOpen { get; }
    long LifecycleFaultCount { get; }
    RuntimeLifecycleFault? LastLifecycleFault { get; }
    long CostFaultCount { get; }
    RuntimeCostFault? LastCostFault { get; }
    bool HostMigrationPaused { get; }
    int LiveEffects { get; }
    RuntimeEventRows? EventRows { get; }

    RuntimeModuleHandle RegisterModule(RuntimeModule module, RuntimeLogLevel level);
    string ExportManifest();
    /// <summary>Gets the typed service a registered provider published, or false when none is published; it leaves with that provider's registration.</summary>
    bool TryService<T>([MaybeNullWhen(false)] out T service) where T : class;
    RuntimeBehaviorTraceSubscription ObserveBehaviorTrace(Action<RuntimeBehaviorTraceRecord> callback);
    AuthoringRoomResolution ResolveAuthoringRoom(long worldEpoch, string sourcePrefab, AuthoringRoomScope scope);

    void SetFactionRelations(RuntimeFactionRelations value);
    RuntimeEntityQueryResult EnumerateEntityCandidates(string kind);
    RuntimeEntityQueryResult InspectActor(RuntimeActorContext actors, string role);
    RuntimeEntityQueryResult InspectEntities(IReadOnlyList<EntityReference> references);
    /// <summary>Allocation-free forms of the reads above for per-frame callers: the same refusals, validation and
    /// budget, with the answer written into the caller's buffer instead of a result object.</summary>
    bool TryEnumerateEntityCandidates(string kind, List<EntityReference> candidates, out string code);
    bool TryInspectEntitySnapshots(IReadOnlyList<EntityReference> references, List<RuntimeEntitySnapshot?> snapshots, out string code);
    bool TryInspectEntitySnapshot(EntityReference reference, out RuntimeEntitySnapshot? snapshot, out string code);
    bool TryInspectEntityPoses(IReadOnlyList<EntityReference> references, List<EntityPose> poses, out string code);
    RuntimeEntityQueryResult InspectChildren(EntityReference parent);
    EntityZoneResolution ZoneOfEntity(EntityReference entity);
    EntityAreaResolution AreaOfEntity(EntityReference entity);
    object? EntityInstance(EntityReference reference);
    EntityReference? ResolveEntityInstance(string kind, object instance);
    EntityReference? ResolveEntity(object instance);
    bool IsEntityCurrent(EntityReference reference);

    JsonElement ResolveGraphContract(string capabilityId, string capabilityVersion, JsonElement parameters);
    bool IsHandleLive(RuntimeModuleHandle owner, RuntimeHandle value);
    int CancelHandle(RuntimeModuleHandle owner, RuntimeHandle value, HandlePort port, string detail);
    void ReportGeneratedLayout(in RuntimeLogLayout layout);
    RuntimeResources EnumerateResources(string kind);
    RuntimeResources ResolveResources(string kind, IReadOnlyList<string> resourceIds);
    RuntimeResources ResolveResourceRefs(IReadOnlyList<ResourceRef> references);
    NumericStateResult EvaluateNumericState(EntityReference target, string definitionId, string stackGroup, double baseValue);
    (int Gates, int Scopes) GateStateCounts();
    (long Fired, long Accumulated)? GateStateOf(string planId, string nodeId, string subject = "");
    int ReleaseVariableScope(string scopeKind, EntityReference subject);
    RuntimeEventRow? EventRow(long index);
}
