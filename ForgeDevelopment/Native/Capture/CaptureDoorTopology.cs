using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace ForgeDevelopment.Native;

// Metadata-only observation policy. A native field wrapper is admitted by its generated
// NativeFieldInfoPtr marker; a similarly named computed property is never invoked. In
// particular portal traversal/progression getters are not evidence of a cached field.
internal static class CaptureDoorTopology
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "_SpawnedDoor_k__BackingField", "_CoursePortal_k__BackingField", "_LastStatus_k__BackingField",
        "_IsOpen_k__BackingField", "m_linksFrom", "m_linksTo", "m_isTraversable", "m_isTraversableFromStart",
        "HasBeenOpenedDuringPlay", "m_nodeA", "m_nodeB", "m_cullPortal", "m_nodeCluster", "ID",
        "m_area", "m_courseNode", "m_geomorph", "m_zone", "m_destruction", "m_isDestroyed", "m_health",
        "m_healthMax", "m_currentStatus", "m_serialNumber", "m_type", "m_componentsInitialized",
        "m_nodesCollected", "m_hasGate", "m_gate", "m_door", "m_cullNode", "m_forwardsNode", "m_backwardsNode",
        "_UID_k__BackingField", "NodeID", "LocalIndex", "m_dimensionIndex", "m_layer", "_m_geoArea_k__BackingField",
        "_Gate_k__BackingField", "m_sync", "m_stateReplicator"
    };
    private static readonly Dictionary<(Type, string), (MemberInfo? Member, string? Reason)> Members = new();
    // The "Type.field" text of a fact, built once per pair: a door row reads about a hundred fields every sweep.
    private static readonly Dictionary<(Type, string), string> Sources = new();

    internal static LevelGuideDoorFact ReadField(object? owner, string name, string ownerMissingReason)
    {
        string source;
        if (owner == null) source = "native." + name;
        else if (!Sources.TryGetValue((owner.GetType(), name), out source!))
            Sources[(owner.GetType(), name)] = source = (owner.GetType().FullName ?? "native") + "." + name;
        if (!Fields.Contains(name)) return LevelGuideCaptureDoorFacts.Unknown(source, "native-field-not-reviewed");
        if (owner == null) return LevelGuideCaptureDoorFacts.Unknown(source, ownerMissingReason);
        try
        {
            var type = owner.GetType();
            if (!Members.TryGetValue((type, name), out var member))
            {
                member = Find(type, name);
                if (member.Reason != "native-field-uninitialized") Members[(type, name)] = member;
            }
            if (member.Reason != null) return LevelGuideCaptureDoorFacts.Unknown(source, member.Reason);
            var value = member.Member is FieldInfo field ? field.GetValue(owner)
                : ((PropertyInfo)member.Member!).GetValue(owner);
            return LevelGuideCaptureDoorFacts.Observed(source, value);
        }
        catch (Exception error)
        {
            var actual = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            return LevelGuideCaptureDoorFacts.Unknown(source, "native-field-read-failed:" + actual.GetType().Name);
        }
    }

    private static (MemberInfo? Member, string? Reason) Find(Type type, string name)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, instance);
            if (field != null) return (field, null);
            var property = current.GetProperty(name, instance);
            if (property == null) continue;
            var marker = current.GetField("NativeFieldInfoPtr_" + name, statics);
            if (property.CanRead && property.GetIndexParameters().Length == 0 && marker?.FieldType == typeof(IntPtr))
                return marker.GetValue(null) is IntPtr pointer && pointer != IntPtr.Zero
                    ? (property, null) : (null, "native-field-uninitialized");
            return (null, "native-field-wrapper-unverified");
        }
        return (null, "native-field-unavailable");
    }

    internal static bool IsCandidateGeomorph(string? name)
    {
        if (name == null) return false;
        var normalized = name.Trim();
        const string clone = "(Clone)";
        while (normalized.EndsWith(clone, StringComparison.Ordinal)) normalized = normalized[..^clone.Length].TrimEnd();
        // These resources have the exact native geo_64x64_ prefix. No substring,
        // size-family, or neighboring-room match is a candidate identity.
        const string nativePrefix = "geo_64x64_";
        if (normalized.StartsWith(nativePrefix, StringComparison.Ordinal)) normalized = normalized[nativePrefix.Length..];
        return normalized is "tech_data_center_HA_01" or "mining_refinery_HA_01" or "mining_storage_HA_06b";
    }
}

// These IDs bridge observations in one generation only. Pointers are lookup keys in
// memory, never the persisted identity; serial/name/position cannot merge two gates.
internal sealed class CaptureDoorTopologyReferences
{
    private readonly Dictionary<(string Kind, IntPtr Pointer), string> _references = new();
    private int _next;
    internal string SessionId { get; }
    internal string GenerationId { get; }
    internal CaptureDoorTopologyReferences(string sessionId, string generationId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(generationId))
            throw new InvalidDataException("Topology references require a session and generation.");
        SessionId = sessionId; GenerationId = generationId;
    }
    internal string Get(string sessionId, string generationId, string kind, IntPtr pointer)
    {
        if (sessionId != SessionId || generationId != GenerationId)
            throw new InvalidDataException("Topology reference belongs to a different generation.");
        if (pointer == IntPtr.Zero || string.IsNullOrWhiteSpace(kind))
            throw new InvalidDataException("Topology reference requires a live native identity and kind.");
        if (!_references.TryGetValue((kind, pointer), out var id))
            _references.Add((kind, pointer), id = "topology/" + GenerationId + "/" + kind + "/" + (++_next).ToString(CultureInfo.InvariantCulture));
        return id;
    }
}
