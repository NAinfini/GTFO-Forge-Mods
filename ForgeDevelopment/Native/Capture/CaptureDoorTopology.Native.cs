using System;
using System.Collections.Generic;
using System.Globalization;
using AIGraph;
using Il2CppInterop.Runtime.InteropTypes;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

// This observer reads raw native references and cached fields. It never opens a door,
// recalculates a graph, completes navigation initialization, or infers sound occlusion.
internal static class CaptureDoorTopologyNative
{
    private sealed record NativeReference(string Id, string Pointer);
    private static CaptureDoorTopologyReferences? _references;
    private static readonly Dictionary<IntPtr, (LG_Gate Gate, bool PlugFlipped)> Built = new();

    internal static void Clear() { _references = null; Built.Clear(); }

    private static CaptureDoorTopologyReferences Scope()
    {
        if (_references == null || _references.SessionId != RuntimeDiagnostics.SessionId
            || _references.GenerationId != RuntimeDiagnostics.GenerationId)
        {
            Built.Clear();
            _references = new CaptureDoorTopologyReferences(RuntimeDiagnostics.SessionId, RuntimeDiagnostics.GenerationId);
        }
        return _references;
    }

    // Called from the existing, audited Build observer, only after a successful Build.
    internal static void ObserveBuild(LG_FactoryJob job, bool complete, bool originalRan, Exception? error)
    {
        if (!originalRan || !complete || error != null) return;
        var build = job.TryCast<LG_BuildGateJob>();
        if (build?.m_gate is not { } gate || gate.WasCollected) return;
        Scope();
        Built[gate.Pointer] = (gate, build.m_plugWasFlipped);
    }

    internal static IEnumerable<CaptureRow?> GateRows(LG_Floor floor)
    {
        var scope = Scope();
        var seen = new HashSet<IntPtr>();
        foreach (var component in ForgeCore.GameFloor.Components(floor.transform))
        {
            if (scope.GenerationId != RuntimeDiagnostics.GenerationId)
                throw new InvalidOperationException("Topology generation changed during gate inventory.");
            if (component == null || component.WasCollected) { yield return null; continue; }
            // TryCast validates inheritance against the live class. The independent internal
            // gate cast also keeps an unsupported shape visible instead of silently dropping it.
            Il2CppObjectBase? gate = component.TryCast<LG_InternalGate>();
            gate ??= component.TryCast<LG_Gate>();
            if (gate == null || !seen.Add(gate.Pointer)) { yield return null; continue; }
            yield return GateRow(gate);
        }
        foreach (var entry in Built.Values)
        {
            if (scope.GenerationId != RuntimeDiagnostics.GenerationId)
                throw new InvalidOperationException("Topology generation changed during gate inventory.");
            if (!entry.Gate.WasCollected && seen.Add(entry.Gate.Pointer)) yield return GateRow(entry.Gate);
            else yield return null;
        }
    }

    private static CaptureRow GateRow(Il2CppObjectBase gate)
    {
        gate = (Il2CppObjectBase?)gate.TryCast<LG_InternalGate>() ?? gate;
        var reference = Reference(gate, "gate");
        var row = new CaptureRow("gateTopology", reference, "native gate");
        try
        {
            AppendGate(row, gate, includeLayout: true);
            var door = Read(gate, "_SpawnedDoor_k__BackingField", "native-gate-unavailable");
            Write(row, "topologyDoorRef", AsReference(door, "door"));
            var native = door.Value as Il2CppObjectBase;
            Component? core = native?.TryCast<LG_SecurityDoor>();
            core ??= native?.TryCast<LG_WeakDoor>();
            if (core != null) AppendDoorState(row, core);
            else Write(row, "topologyDoorStatus", LevelGuideCaptureDoorFacts.Unknown(
                "door._LastStatus_k__BackingField", door.Status == "unknown" ? door.MissingReason!
                    : door.Value == null ? "gate-has-no-spawned-door" : "spawned-door-implementation-unverified"));
        }
        catch (Exception error) { row.Field("topologyReadFailure", "native-gate-observation-failed:" + error.GetType().Name); }
        return row;
    }

    // The ordinary full/change door rows use the exact same reference bridge and cached
    // state. Static layout rows describe endpoints; live rows can join them in this scope.
    internal static void AppendDoor(CaptureRow row, Component door)
    {
        try
        {
            // Do not evaluate door.Gate at the call site: even acquiring the reference
            // belongs inside the observer's protected, reviewed raw-field read.
            var gate = Read(door, "_Gate_k__BackingField", "door-unavailable");
            if (gate.Status == "observed" && gate.Value != null && gate.Value is not Il2CppObjectBase)
                gate = LevelGuideCaptureDoorFacts.Unknown(gate.Source, "native-gate-reference-type-unverified");
            AppendGate(row, gate.Value as Il2CppObjectBase, includeLayout: false, gate);
            Write(row, "topologyDoorRef", AsReference(LevelGuideCaptureDoorFacts.Observed("native-door", door), "door"));
            AppendDoorState(row, door);
        }
        catch (Exception error) { row.Field("topologyReadFailure", "native-door-observation-failed:" + error.GetType().Name); }
    }

    internal static void AppendArea(CaptureRow row, LG_Area? area, AIG_CourseNode? node)
    {
        AddScope(row);
        Write(row, "topologyAreaRef", AsReference(LevelGuideCaptureDoorFacts.Observed("LG_Zone.m_areas", area), "area"));
        Write(row, "topologyCourseNodeRef", AsReference(LevelGuideCaptureDoorFacts.Observed("LG_Zone.m_courseNodes", node), "courseNode"));
        Write(row, "topologyNodeClusterRef", AsReference(Read(node, "m_nodeCluster", "area-course-node-unavailable"), "nodeCluster"));
    }

    private static void AddScope(CaptureRow row, bool includeObservationTick = true)
    {
        var scope = Scope();
        row.Field("topologySessionId", scope.SessionId);
        row.Field("topologyGenerationId", scope.GenerationId);
        // Change-row signatures must not change merely because another frame passed.
        // Live changes already receive their observation tick from CaptureStateWriter.
        if (includeObservationTick) row.Field("topologyObservedTick", RuntimeDiagnostics.SimulationTick?.ToString(CultureInfo.InvariantCulture));
        row.Field("topologyEvidence", "cached-native-field-observation");
    }

    private static void AppendGate(CaptureRow row, Il2CppObjectBase? gate, bool includeLayout, LevelGuideDoorFact? gateEvidence = null)
    {
        AddScope(row, includeObservationTick: includeLayout);
        var gateFact = gateEvidence ?? LevelGuideCaptureDoorFacts.Observed("native-gate-reference", gate);
        Write(row, "topologyGateRef", gateFact.Status == "unknown" ? gateFact : gate == null
            ? LevelGuideCaptureDoorFacts.Unknown(gateFact.Source, "door-gate-unavailable") : AsReference(gateFact, "gate"));
        Write(row, "topologyGateTraversable", Child(gateFact, "m_isTraversable", "door-gate-unavailable"));
        Write(row, "topologyGateOpenedDuringPlay", Child(gateFact, "HasBeenOpenedDuringPlay", "door-gate-unavailable"));
        var portal = Child(gateFact, "_CoursePortal_k__BackingField", "door-gate-unavailable");
        Write(row, "topologyCoursePortalRef", AsReference(portal, "coursePortal"));
        var cullPortal = Child(portal, "m_cullPortal", "course-portal-unavailable");
        Write(row, "topologyCullPortalRef", AsReference(cullPortal, "cullPortal"));
        Write(row, "topologyCullPortalOpenCached", Child(cullPortal, "_IsOpen_k__BackingField", "cull-portal-unavailable"));
        Write(row, "topologyPortalTraversable", LevelGuideCaptureDoorFacts.Unknown("AIG_CoursePortal.IsTraversable", "native-getter-purity-unverified"));
        Write(row, "topologyPortalProgressionLocked", LevelGuideCaptureDoorFacts.Unknown("AIG_CoursePortal.IsProgressionLocked", "native-getter-purity-unverified"));
        // Gate traversal, culling openness, door status and acoustic propagation are separate observations.
        row.Field("topologySoundOcclusion", null);
        row.Field("topologySoundOcclusionMissingReason", "native-acoustic-edge-semantics-unverified");
        var from = Child(gateFact, "m_linksFrom", "door-gate-unavailable");
        var to = Child(gateFact, "m_linksTo", "door-gate-unavailable");
        Write(row, "topologyFromAreaRef", AsReference(from, "area"));
        Write(row, "topologyToAreaRef", AsReference(to, "area"));
        if (!includeLayout) return;
        if (gate != null)
        {
            var component = gate.TryCast<Component>();
            row.Field("topologyGateClass", component?.GetIl2CppType().FullName);
            row.Field("topologyGatePath", RuntimeDiagnostics.PathOf(component?.transform));
            row.Field("topologyBuildObserved", Built.ContainsKey(gate.Pointer).ToString());
            row.Field("topologyPlugFlipped", Built.TryGetValue(gate.Pointer, out var built) ? built.PlugFlipped.ToString() : null);
            row.Field("topologyPlugFlippedMissingReason", Built.ContainsKey(gate.Pointer) ? null : "gate-build-not-observed");
        }
        Write(row, "topologyGateTypeCached", Read(gate, "m_type", "door-gate-unavailable"));
        Write(row, "topologyInternalComponentsInitialized", Read(gate, "m_componentsInitialized", "door-gate-unavailable"));
        Write(row, "topologyGateTraversableFromStart", Read(gate, "m_isTraversableFromStart", "door-gate-unavailable"));
        AppendSide(row, "topologyFrom", from);
        AppendSide(row, "topologyTo", to);
        var nodeA = Child(portal, "m_nodeA", "course-portal-unavailable");
        var nodeB = Child(portal, "m_nodeB", "course-portal-unavailable");
        AppendNode(row, "topologyPortalA", nodeA);
        AppendNode(row, "topologyPortalB", nodeB);
        Write(row, "topologyPortalGateRef", AsReference(Child(portal, "m_gate", "course-portal-unavailable"), "gate"));
        Write(row, "topologyPortalDoorRef", AsReference(Child(portal, "m_door", "course-portal-unavailable"), "door"));
        Write(row, "topologyCullForwardsNodeRef", AsReference(Child(cullPortal, "m_forwardsNode", "cull-portal-unavailable"), "cullNode"));
        Write(row, "topologyCullBackwardsNodeRef", AsReference(Child(cullPortal, "m_backwardsNode", "cull-portal-unavailable"), "cullNode"));
    }

    private static void AppendSide(CaptureRow row, string prefix, LevelGuideDoorFact area)
    {
        Write(row, prefix + "AreaUid", Child(area, "_UID_k__BackingField", "gate-area-unavailable"));
        Write(row, prefix + "GeoArea", Child(area, "_m_geoArea_k__BackingField", "gate-area-unavailable"));
        var zone = Child(area, "m_zone", "gate-area-unavailable");
        Write(row, prefix + "ZoneRef", AsReference(zone, "zone"));
        Write(row, prefix + "ZoneId", Child(zone, "ID", "gate-zone-unavailable"));
        Write(row, prefix + "ZoneLocalIndex", Child(zone, "LocalIndex", "gate-zone-unavailable"));
        Write(row, prefix + "Dimension", Child(zone, "m_dimensionIndex", "gate-zone-unavailable"));
        Write(row, prefix + "Layer", Child(Child(zone, "m_layer", "gate-zone-unavailable"), "m_type", "gate-layer-unavailable"));
        var room = Child(area, "m_geomorph", "gate-area-unavailable");
        Write(row, prefix + "GeomorphRef", AsReference(room, "geomorph"));
        LevelGuideDoorFact name;
        try
        {
            name = room.Status == "unknown" ? LevelGuideCaptureDoorFacts.Unknown(room.Source + ".name", room.MissingReason!)
                : room.Value is LG_Geomorph geomorph && !geomorph.WasCollected
                    ? LevelGuideCaptureDoorFacts.Observed("LG_Geomorph.name", geomorph.name)
                    : LevelGuideCaptureDoorFacts.Unknown("LG_Geomorph.name", "gate-geomorph-unavailable");
        }
        catch (Exception error) { name = LevelGuideCaptureDoorFacts.Unknown("LG_Geomorph.name", "native-name-read-failed:" + error.GetType().Name); }
        Write(row, prefix + "Geomorph", name);
        Write(row, prefix + "CandidateGeomorph", name.Status == "unknown" ? LevelGuideCaptureDoorFacts.Unknown(name.Source, name.MissingReason!)
            : LevelGuideCaptureDoorFacts.Observed(name.Source, CaptureDoorTopology.IsCandidateGeomorph(name.Value as string)));
        AppendNode(row, prefix, Child(area, "m_courseNode", "gate-area-unavailable"));
    }

    private static void AppendNode(CaptureRow row, string prefix, LevelGuideDoorFact node)
    {
        Write(row, prefix + "CourseNodeRef", AsReference(node, "courseNode"));
        Write(row, prefix + "CourseNodeId", Child(node, "NodeID", "gate-course-node-unavailable"));
        Write(row, prefix + "NodeAreaRef", AsReference(Child(node, "m_area", "gate-course-node-unavailable"), "area"));
        var cluster = Child(node, "m_nodeCluster", "gate-course-node-unavailable");
        Write(row, prefix + "NodeClusterRef", AsReference(cluster, "nodeCluster"));
        Write(row, prefix + "NodeClusterId", Child(cluster, "ID", "gate-node-cluster-unavailable"));
        Write(row, prefix + "CullNodeRef", AsReference(Child(node, "m_cullNode", "gate-course-node-unavailable"), "cullNode"));
    }

    private static void AppendDoorState(CaptureRow row, Component door)
    {
        row.Field("topologyDoorClass", door.GetIl2CppType().FullName);
        row.Field("topologyDoorKind", door.TryCast<LG_WeakDoor>() != null ? "weak"
            : door.TryCast<LG_SecurityDoor>() != null ? "security" : "unknown");
        var serial = Read(door, "m_serialNumber", "door-unavailable");
        Write(row, "topologyDoorSerial", serial);
        var gate = Read(door, "_Gate_k__BackingField", "door-unavailable");
        var sync = Read(door, "m_sync", "door-unavailable");
        if (sync.Value is Il2CppObjectBase nativeSync)
            sync = LevelGuideCaptureDoorFacts.Observed(sync.Source, nativeSync.TryCast<LG_Door_Sync>());
        var state = Child(sync, "m_stateReplicator", "door-sync-implementation-unavailable");
        var setup = gate.Status == "observed" && gate.Value is Il2CppObjectBase nativeGate && !nativeGate.WasCollected
            && (door.TryCast<LG_SecurityDoor>() == null || serial.Value is int number && number >= 100);
        var reason = gate.Status == "unknown" ? gate.MissingReason : serial.Status == "unknown" ? serial.MissingReason
            : sync.Status == "unknown" ? sync.MissingReason : state.Status == "unknown" && setup ? state.MissingReason
            : LevelGuideCaptureDoorFacts.Readiness(setup, sync.Value != null, state.Value != null);
        row.Field("topologyDoorSetupObserved", setup.ToString());
        Write(row, "topologyDoorStatus", reason == null ? Read(door, "_LastStatus_k__BackingField", "door-unavailable")
            : LevelGuideCaptureDoorFacts.Unknown("door._LastStatus_k__BackingField", reason));
        if (door.TryCast<LG_WeakDoor>() is not { } weak) return;
        if (reason != null)
        {
            foreach (var field in new[] { "topologyWeakDestroyed", "topologyWeakHealth", "topologyWeakDestructionStatus" })
                Write(row, field, LevelGuideCaptureDoorFacts.Unknown("LG_WeakDoor.m_destruction", reason));
            return;
        }
        var destruction = Read(weak, "m_destruction", "door-unavailable");
        if (destruction.Value is Il2CppObjectBase native)
            destruction = LevelGuideCaptureDoorFacts.Observed(destruction.Source, native.TryCast<LG_WeakDoor_Destruction>());
        Write(row, "topologyWeakDestroyed", Child(destruction, "m_isDestroyed", "weak-door-destruction-unavailable"));
        Write(row, "topologyWeakHealth", Child(destruction, "m_health", "weak-door-destruction-unavailable"));
        Write(row, "topologyWeakDestructionStatus", Child(destruction, "m_currentStatus", "weak-door-destruction-unavailable"));
    }

    private static LevelGuideDoorFact Read(object? owner, string name, string missing)
    {
        try
        {
            return owner is Il2CppObjectBase native && (native.WasCollected || native.Pointer == IntPtr.Zero)
                ? LevelGuideCaptureDoorFacts.Unknown((owner.GetType().FullName ?? "native") + "." + name, "native-object-collected")
                : CaptureDoorTopology.ReadField(owner, name, missing);
        }
        catch (Exception error) { return LevelGuideCaptureDoorFacts.Unknown("native." + name, "native-field-read-failed:" + error.GetType().Name); }
    }

    private static LevelGuideDoorFact Child(LevelGuideDoorFact owner, string name, string missing)
        => owner.Status == "unknown" ? LevelGuideCaptureDoorFacts.Unknown(owner.Source + "->" + name, owner.MissingReason!)
            : Read(owner.Value, name, missing);

    private static string Reference(Il2CppObjectBase native, string kind)
    {
        var scope = Scope();
        return scope.Get(RuntimeDiagnostics.SessionId, RuntimeDiagnostics.GenerationId, kind, native.Pointer);
    }

    private static LevelGuideDoorFact AsReference(LevelGuideDoorFact fact, string kind)
    {
        if (fact.Status == "unknown" || fact.Value == null) return fact;
        if (fact.Value is not Il2CppObjectBase native) return LevelGuideCaptureDoorFacts.Unknown(fact.Source, "native-reference-type-unverified");
        if (native.WasCollected || native.Pointer == IntPtr.Zero) return LevelGuideCaptureDoorFacts.Unknown(fact.Source, "native-object-collected");
        return LevelGuideCaptureDoorFacts.Observed(fact.Source, new NativeReference(Reference(native, kind),
            "0x" + native.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
    }

    private static void Write(CaptureRow row, string field, LevelGuideDoorFact fact)
    {
        var value = fact.Value;
        if (!FieldNames.TryGetValue(field, out var names))
            FieldNames[field] = names = (field.EndsWith("Ref", StringComparison.Ordinal) ? field[..^3] + "Pointer" : null,
                field + "Observation", field + "Source", field + "MissingReason");
        if (names.Pointer != null)
            row.Field(names.Pointer, value is NativeReference pointer ? pointer.Pointer : null);
        if (value is NativeReference reference)
            fact = LevelGuideCaptureDoorFacts.Observed(fact.Source, value = reference.Id);
        if (value != null && value is not (string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum))
            fact = LevelGuideCaptureDoorFacts.Unknown(fact.Source, "native-scalar-type-unverified");
        row.Field(field, fact.Value == null ? null : Convert.ToString(fact.Value, CultureInfo.InvariantCulture));
        row.Field(names.Observation, fact.Status);
        row.Field(names.Source, fact.Source);
        if (fact.MissingReason != null) row.Field(names.MissingReason, fact.MissingReason);
    }

    // The derived field names of one topology field, built once; Write runs for every field of every door row of every sweep.
    private static readonly Dictionary<string, (string? Pointer, string Observation, string Source, string MissingReason)> FieldNames
        = new(StringComparer.Ordinal);
}
