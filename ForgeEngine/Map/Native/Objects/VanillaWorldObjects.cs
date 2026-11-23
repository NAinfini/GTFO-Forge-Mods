using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AssetShards;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Generation-time exact component addresses, bound by prefab pointer and room receipt.
/// Neither dispatch nor trigger callbacks traverse the native hierarchy.</summary>
internal static class VanillaWorldObjects
{
    internal const string Prefix = "world_event/";
    private sealed record Entry(Component Component, LG_Geomorph Room, string? Instance, string Kind,
        ZoneCoordinates Zone, LG_WorldEventObject? WorldObject, string Source);
    internal readonly record struct AnimationReceiver(LG_WorldEventAnimationTrigger Animation,
        IntPtr AnimationPointer, IntPtr SyncPointer, IntPtr ReplicaPointer);

    /// <summary>One requested zone's complete, prevalidated animation receiver set. Preparation for every region
    /// finishes before any region is applied, so a full native query buffer or an unreadable receiver cannot leave
    /// a prefix of the request written.</summary>
    internal sealed class AnimationRegion
    {
        private readonly AnimationReceiver[] _receivers;
        internal AnimationRegion(ZoneCoordinates zone, AnimationReceiver[] receivers)
        { Zone = zone; _receivers = receivers; }

        internal ZoneCoordinates Zone { get; }

        /// <summary>The prevalidated components of this region, for a reader that checks each one's own replicated trigger bit.</summary>
        internal IReadOnlyList<AnimationReceiver> Receivers => _receivers;

        internal MapActionOutcome Apply(bool enabled)
        {
            // Recheck every captured identity before this region's first call. A receiver can disappear after the
            // batch preflight if an earlier independent region's native callback tears down the world.
            for (int index = 0; index < _receivers.Length; index++)
                if (!Current(_receivers[index], out _))
                    return MapActionOutcome.Issued(ResultCode.ReadbackUnconfirmed);
            bool accepted = false;
            for (int index = 0; index < _receivers.Length; index++)
                accepted |= enabled ? _receivers[index].Animation.Trigger() : _receivers[index].Animation.ResetTrigger();
            // This proves each original SyncTrigger's replicated bit, not animation completion or rendered frames.
            for (int index = 0; index < _receivers.Length; index++)
                if (!Current(_receivers[index], out var sync) || sync!.HasBeenTriggered != enabled)
                    return MapActionOutcome.Issued(ResultCode.ReadbackUnconfirmed);
            return accepted ? MapActionOutcome.Issued(enabled ? ResultCode.AnimationTriggered : ResultCode.AnimationReset)
                : MapActionOutcome.AlreadyInState(ResultCode.AlreadyInState);
        }

        private static bool Current(AnimationReceiver receiver, out SyncTrigger? sync)
        {
            sync = null;
            var animation = receiver.Animation;
            if (receiver.AnimationPointer == IntPtr.Zero || animation == null || animation.WasCollected
                || animation.Pointer != receiver.AnimationPointer) return false;
            sync = animation.m_syncTrigger;
            var replica = sync?.m_stateReplicator;
            return receiver.SyncPointer != IntPtr.Zero && receiver.ReplicaPointer != IntPtr.Zero
                && sync != null && sync.Pointer == receiver.SyncPointer
                && replica != null && replica.Pointer == receiver.ReplicaPointer;
        }
    }
    private static readonly Dictionary<string, Entry> Objects = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, string> Addresses = new();
    private static readonly HashSet<string> Ambiguous = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static long _epoch;

    internal static void Clear()
    {
        _epoch = 0;
        Objects.Clear(); Addresses.Clear(); Ambiguous.Clear();
    }

    /// <summary>A host migration keeps the level and its world objects: the table moves to the new world number.</summary>
    internal static void Follow(long from, long to)
    {
        if (_epoch == from) _epoch = to;
    }

    internal static void Build(long worldEpoch)
    {
        Clear();
        if (worldEpoch <= 0) return;
        _epoch = worldEpoch;
        var paths = new Dictionary<IntPtr, List<string>>();
        var assets = AssetShardManager.s_loadedAssetsLookup;
        if (assets == null) return;
        foreach (var pair in assets)
        {
            if (pair.Value == null || pair.Value.WasCollected || !pair.Key.StartsWith("ASSETS/", StringComparison.Ordinal)) continue;
            if (!paths.TryGetValue(pair.Value.Pointer, out var names)) paths.Add(pair.Value.Pointer, names = new());
            names.Add(pair.Key);
        }
        var index = ZoneIndex.Current(worldEpoch);
        foreach (var zone in index.Zones())
        {
            if (!index.TryCoordinates(zone, out var coordinates) || zone.m_areas == null) continue;
            var rooms = new Dictionary<IntPtr, LG_Geomorph>();
            var counts = new Dictionary<IntPtr, int>();
            foreach (var area in zone.m_areas)
            {
                var room = area == null || area.WasCollected ? null : area.m_geomorph;
                var prefab = room == null || room.WasCollected ? null : room.m_geoPrefab;
                if (prefab == null || prefab.WasCollected || rooms.ContainsKey(room!.Pointer)) continue;
                rooms.Add(room.Pointer, room);
                counts[prefab.Pointer] = counts.TryGetValue(prefab.Pointer, out var count) ? count + 1 : 1;
            }
            foreach (var room in rooms.Values)
            {
                var prefab = room.m_geoPrefab!;
                if (!paths.TryGetValue(prefab.Pointer, out var names)) continue;
                names.Sort(StringComparer.Ordinal);
                var locator = GeneratedRoomRegistry.LocatorFor(room);
                if (locator != null && (locator.Dimension != coordinates.Dimension || locator.Layer != coordinates.Layer
                    || locator.LocalIndex != coordinates.Zone || GeneratedRoomRegistry.Find(zone, locator)?.Pointer != room.Pointer
                    || !names.Contains(locator.SourcePrefab.ToUpperInvariant()))) continue;
                foreach (var source in names)
                {
                    if (locator != null && source != locator.SourcePrefab.ToUpperInvariant()) continue;
                    if (counts[prefab.Pointer] == 1) AddRoom(room, coordinates, null, source);
                    if (locator?.InstanceId is { } instance) AddRoom(room, coordinates, instance, source);
                }
            }
        }
        if (MapLog.Logs(RuntimeLogLevel.Info))
            foreach (var address in Addresses.Values)
            {
                var entry = Objects[address];
                MapLog.Write(RuntimeLogLevel.Info, MapLogCodes.InstanceCaptured, new RuntimeLogFacts
                {
                    Entity = address,
                    Detail = "Native world object component captured.",
                    Values = new[] { RuntimeLogValue.Quote("room", entry.Instance ?? "native"),
                        RuntimeLogValue.Of("component", entry.Kind), RuntimeLogValue.Quote("prefab", entry.Source),
                        RuntimeLogValue.Of("nativeInstance", entry.Component.GetInstanceID()),
                        RuntimeLogValue.Of("dimension", entry.Zone.Dimension), RuntimeLogValue.Of("layer", entry.Zone.Layer),
                        RuntimeLogValue.Of("zone", entry.Zone.Zone) }
                });
            }
    }

    private static void AddRoom(LG_Geomorph room, ZoneCoordinates zone, string? instance, string source)
    {
        var root = room.gameObject;
        AddComponents(root.GetComponentsInChildren<LG_WorldEventObject>(true), "world_event");
        AddComponents(root.GetComponentsInChildren<LG_InteractWorldEventTrigger>(true), "interact");
        AddComponents(root.GetComponentsInChildren<LG_LookatWorldEventTrigger>(true), "lookat");
        AddComponents(root.GetComponentsInChildren<LG_WorldEventAnimationTrigger>(true), "animation");

        void AddComponents<T>(IEnumerable<T> components, string kind) where T : Component
        {
            var ordinals = new Dictionary<IntPtr, int>();
            foreach (var component in components)
            {
                if (component == null || component.WasCollected) continue;
                var owner = component.gameObject;
                var ordinal = ordinals.TryGetValue(owner.Pointer, out var prior) ? prior : 0;
                ordinals[owner.Pointer] = ordinal + 1;
                var segments = Segments(component.transform, room.transform);
                if (segments == null) continue;
                var address = Encode(zone, instance, source, segments, kind, ordinal);
                var world = WorldObject(component.transform, room.transform);
                if (Ambiguous.Contains(address)) continue;
                if (Objects.TryGetValue(address, out var known) && known.Component.Pointer != component.Pointer)
                {
                    Objects.Remove(address); Ambiguous.Add(address); continue;
                }
                Objects[address] = new(component, room, instance, kind, zone, world, source);
                if (!Addresses.ContainsKey(component.Pointer) || instance != null) Addresses[component.Pointer] = address;
            }
        }
    }

    private static List<object[]>? Segments(Transform node, Transform root)
    {
        var segments = new List<object[]>();
        while (node.Pointer != root.Pointer)
        {
            var parent = node.parent;
            if (parent == null || parent.WasCollected) return null;
            int sameName = 0;
            bool found = false;
            for (int i = 0; i < parent.childCount; i++)
            {
                var sibling = parent.GetChild(i);
                if (sibling.Pointer == node.Pointer) { found = true; break; }
                if (sibling.gameObject.name == node.gameObject.name) sameName++;
            }
            if (!found) return null;
            segments.Add(new object[] { node.gameObject.name, sameName });
            node = parent;
        }
        segments.Reverse();
        return segments;
    }

    private static LG_WorldEventObject? WorldObject(Transform node, Transform root)
    {
        while (true)
        {
            var world = node.gameObject.GetComponent<LG_WorldEventObject>();
            if (world != null && !world.WasCollected) return world;
            if (node.Pointer == root.Pointer || node.parent == null) return null;
            node = node.parent;
        }
    }

    internal static string Encode(ZoneCoordinates zone, string? instance, string source, IReadOnlyList<object[]> segments,
        string kind, int ordinal)
    {
        // The identity is the first 128 bits of SHA-256 over the same JSON tuple the website hashes
        // (map-vanilla-world-objects.ts), so an address stays short enough for a log entity (MaxLogText) however deep the
        // object sits. Nothing decodes it: both sides recompute it, and two components on one address are kept Ambiguous.
        var json = JsonSerializer.Serialize(new object?[] { instance, source.ToUpperInvariant(), segments, kind, ordinal }, JsonOptions);
        var key = "v2." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)), 0, 16).ToLowerInvariant();
        return Prefix + zone.Dimension.ToString(CultureInfo.InvariantCulture) + "/" + zone.Layer.ToString(CultureInfo.InvariantCulture)
            + "/" + zone.Zone.ToString(CultureInfo.InvariantCulture) + "/" + key;
    }

    internal static bool Ready(long worldEpoch) => worldEpoch > 0 && worldEpoch == _epoch;

    internal static bool StaleComponent(long worldEpoch, Component component)
        => worldEpoch != _epoch && component != null && Addresses.ContainsKey(component.Pointer);

    internal static string? Address(long worldEpoch, Component component)
        => worldEpoch == _epoch && component != null && !component.WasCollected
            && Addresses.TryGetValue(component.Pointer, out var address) ? address : null;

    internal static LG_WorldEventObject? WorldObjectFor(long worldEpoch, Component component)
        => Address(worldEpoch, component) is { } address && Objects.TryGetValue(address, out var entry)
            && entry.WorldObject != null && !entry.WorldObject.WasCollected ? entry.WorldObject : null;
    internal static EntityReference? Reference(long worldEpoch, string address)
        => TryResolve(worldEpoch, address, out _, out _) ? new EntityReference(EntityKinds.MapObject + ":" + address, worldEpoch, 1) : null;

    internal static bool IsCurrent(EntityReference reference)
        => reference.LifeEpoch == 1 && reference.Id.StartsWith(EntityKinds.MapObject + ":" + Prefix, StringComparison.Ordinal)
            && TryResolve(reference.WorldEpoch, reference.Id.Substring(EntityKinds.MapObject.Length + 1), out _, out _);
    internal static bool TryResolve(long worldEpoch, string address, out Component? component, out string reason)
    {
        component = null;
        reason = ResultCode.EnvironmentTargetUnsupported;
        if (!address.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (worldEpoch != _epoch || worldEpoch <= 0) { reason = ResultCode.StaleEntity; Diagnose(address, null, reason); return false; }
        if (!Objects.TryGetValue(address, out var entry) || entry.Component == null || entry.Component.WasCollected
            || entry.Room == null || entry.Room.WasCollected) { Diagnose(address, null, reason); return false; }
        component = entry.Component;
        reason = "";
        return true;
    }

    internal static MapActionOutcome Animate(long worldEpoch, string address, ZoneCoordinates zone, bool enabled)
    {
        if (!TryResolve(worldEpoch, address, out var component, out var reason)) return MapActionOutcome.Refused(reason);
        var entry = Objects[address];
        if (!entry.Zone.Equals(zone) || component is not LG_WorldEventAnimationTrigger animation || entry.WorldObject == null)
        { Diagnose(address, entry, ResultCode.EnvironmentTargetUnsupported); return MapActionOutcome.Refused(ResultCode.EnvironmentTargetUnsupported); }
        bool registered = false;
        var components = entry.WorldObject.WorldEventComponents;
        if (components != null)
            for (int i = 0; i < components.Length; i++)
                if (components[i]?.Pointer == animation.Pointer) { registered = true; break; }
        if (!registered) { Diagnose(address, entry, ResultCode.EnvironmentTargetUnsupported); return MapActionOutcome.Refused(ResultCode.EnvironmentTargetUnsupported); }
        // Without its replicator the native entry falls back to local animation and may return false after
        // changing objects. IsValid is constant true in this build; it cannot establish a usable native state.
        var sync = animation.m_syncTrigger;
        var replica = sync?.m_stateReplicator;
        if (sync == null || replica == null || sync.Pointer == IntPtr.Zero || replica.Pointer == IntPtr.Zero)
        { Diagnose(address, entry, ResultCode.LevelEventUnavailable); return MapActionOutcome.Refused(ResultCode.LevelEventUnavailable); }
        bool accepted = enabled ? animation.Trigger() : animation.ResetTrigger();
        var current = animation.m_syncTrigger;
        var currentReplica = current?.m_stateReplicator;
        if (animation.WasCollected || current == null || current.Pointer != sync.Pointer
            || currentReplica == null || currentReplica.Pointer != replica.Pointer)
            return MapActionOutcome.Issued(ResultCode.ReadbackUnconfirmed);
        // This observes the native replicated trigger bit, not the request's bool or animation completion.
        if (current.HasBeenTriggered == enabled)
            return accepted ? MapActionOutcome.Issued(enabled ? ResultCode.AnimationTriggered : ResultCode.AnimationReset)
                : MapActionOutcome.AlreadyInState(ResultCode.AlreadyInState);
        if (!accepted)
        { Diagnose(address, entry, ResultCode.LevelEventUnavailable); return MapActionOutcome.Refused(ResultCode.LevelEventUnavailable); }
        return MapActionOutcome.Issued(ResultCode.ReadbackUnconfirmed);
    }

    /// <summary>Prepare the complete receiver set for every requested zone through the same native key query and
    /// place predicate used by world events. The build's fixed result array has no overflow flag: filling it is an
    /// explicit refusal. Every component and replicator is validated before the first Trigger/ResetTrigger call.</summary>
    internal static bool TryPrepareAnimations(long worldEpoch, string filter, IReadOnlyList<ZoneCoordinates> zones,
        out AnimationRegion[] regions, out string reason)
    {
        regions = Array.Empty<AnimationRegion>();
        reason = ResultCode.EnvironmentTargetUnsupported;
        if (worldEpoch <= 0 || zones.Count == 0) return false;
        var index = ZoneIndex.Current(worldEpoch);
        var nativeZones = new LG_Zone[zones.Count];
        var matches = new List<AnimationReceiver>[zones.Count];
        for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
        {
            if (index.ZoneAt(zones[zoneIndex]) is not { } native) return false;
            nativeZones[zoneIndex] = native;
            matches[zoneIndex] = new List<AnimationReceiver>();
        }
        var results = WorldEventManager.WorldEventObjectResults;
        int count = WorldEventManager.GetWorldEventObjectsNonAlloc(filter, results);
        if (count < 0) { reason = ResultCode.LevelEventUnavailable; return false; }
        if (count >= results.Length) { reason = ResultCode.AnimationQueryBudget; return false; }
        for (int objectIndex = 0; objectIndex < count; objectIndex++)
        {
            var obj = results[objectIndex];
            if (obj == null || obj.WasCollected || obj.WorldEventComponents == null) continue;
            for (int zoneIndex = 0; zoneIndex < nativeZones.Length; zoneIndex++)
            {
                if (!WorldEventManager.CheckWorldEventObjectPlaceFilter(obj, null, null, nativeZones[zoneIndex], null)) continue;
                var components = obj.WorldEventComponents;
                for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
                {
                    var animation = components[componentIndex]?.TryCast<LG_WorldEventAnimationTrigger>();
                    if (animation == null || animation.WasCollected) continue;
                    var sync = animation.m_syncTrigger;
                    var replica = sync?.m_stateReplicator;
                    if (sync == null || replica == null || animation.Pointer == IntPtr.Zero
                        || sync.Pointer == IntPtr.Zero || replica.Pointer == IntPtr.Zero)
                    { reason = ResultCode.LevelEventUnavailable; return false; }
                    matches[zoneIndex].Add(new AnimationReceiver(animation, animation.Pointer,
                        sync.Pointer, replica.Pointer));
                }
            }
        }
        regions = new AnimationRegion[zones.Count];
        for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
        {
            if (matches[zoneIndex].Count == 0)
            { regions = Array.Empty<AnimationRegion>(); reason = ResultCode.LevelEventUnavailable; return false; }
            regions[zoneIndex] = new AnimationRegion(zones[zoneIndex], matches[zoneIndex].ToArray());
        }
        reason = "";
        return true;
    }

    internal static MapActionOutcome Interaction(long worldEpoch, string address, bool enabled)
    {
        if (!TryResolve(worldEpoch, address, out var component, out _)) return MapActionOutcome.Refused(ResultCode.MapStateUnsupportedRecipient);
        if (component is not LG_InteractWorldEventTrigger interact)
        { Diagnose(address, Objects[address], ResultCode.MapStateUnsupportedRecipient); return MapActionOutcome.Refused(ResultCode.MapStateUnsupportedRecipient); }
        if (interact.IsActive == enabled) return MapActionOutcome.AlreadyInState(ResultCode.Committed);
        try { interact.SetActive(enabled); }
        catch (Exception error) { CommitFailed(address, error.GetType().Name); throw; }
        return interact.IsActive == enabled ? MapActionOutcome.Issued(ResultCode.Committed) : MapActionOutcome.Refused(ResultCode.DoorInteractionUnreadable);
    }

    internal static void CommitFailed(string address, string reason)
        => Diagnose(address, Objects.TryGetValue(address, out var entry) ? entry : null, reason);

    private static void Diagnose(string address, Entry? entry, string reason)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Error)) return;
        MapLog.Write(RuntimeLogLevel.Error, MapLogCodes.InstanceRefused, new RuntimeLogFacts
        {
            Entity = address,
            Detail = "Native world object refused.",
            Values = new[] { RuntimeLogValue.Quote("room", entry?.Instance ?? "unresolved"),
                RuntimeLogValue.Quote("component", entry?.Kind ?? "unresolved"), RuntimeLogValue.Of("reason", reason) }
        });
    }
}


