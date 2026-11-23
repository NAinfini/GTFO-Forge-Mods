using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

// The generator category's two readback points, declared with the reader they feed. Each runs after the
// method the game itself runs when the generator or its group changed, and each one only chooses when to read:
// the callback's own arguments are never turned into fact values, because the instance is re-read afterwards
// and reports the state it is really in. The group postfix is listed in `MapNativeHooks.Types` and frozen in
// `evidence/map-hooks.json`; the cell readback follows ForgeCore's `GameHooks.GeneratorState`.

// The generator's own state replication callback. The state the callback carries names the player and the item
// that changed it — the one record of what changed, which the instance does not hold — while the powered
// reading the fact reports is read from the instance after the body returned.
// Subscribed to `ForgeCore.GameHooks.GeneratorState` (see `MapGameHooks`); ForgeCore patches the game method.
internal static class GeneratorCellReadback
{
    internal static void Changed(LG_PowerGenerator_Core generator, pPowerGeneratorState newState)
    {
        if (!Session.IsHost) return;
        if (MapHost.Session is { } session)
            session.Run(session.MapObjects, module => GeneratorObservation.CellChanged(generator, newState, module),
                "map-object observation");
    }
}

// The generator group's own state callback. The native group state carries a fog step and no member count, so
// this hook only chooses when to look: the module re-reads the group's members through the generator source and
// publishes the group fact only when every one of them reads powered.
[HarmonyPatch(typeof(LG_PowerGeneratorCluster), nameof(LG_PowerGeneratorCluster.OnStateChange))]
internal static class GeneratorClusterStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_PowerGeneratorCluster __instance)
    {
        if (!Session.IsHost) return;
        if (MapHost.Session is { } session)
            session.Run(session.MapObjects, module => GeneratorObservation.GroupChanged(__instance, module),
                "map-object observation");
    }
}

/// <summary>
/// The generator reader. It answers for the two native instance kinds of the generator category: a
/// `LG_PowerGenerator_Core` is one generator and a `LG_PowerGeneratorCluster` is the group the level parented it
/// under, and each is addressed by <see cref="MapObjectGeneratorAddress"/> — the zone the instance stands in,
/// read through the same <see cref="ZoneIndex"/> every other map-object reader uses, and the author's placement
/// identity registered against the exact built core. An unresolved subject produces no address, and its cause is reported
/// once per cause.
///
/// Nothing is synthesized here: the powered reading is the generator's own replicated status, and the group
/// counts are the group's own member array read at the moment of the read. The two facts a native callback
/// produces are published by the module this source is handed to, which re-reads the instance through this
/// source, so a fact never reports a state the instance is not in.
/// </summary>
internal sealed class GeneratorSource : IMapObjectSource
{
    private readonly OnceReporter _once;
    private readonly HashSet<(long World, IntPtr Pointer, string Reason)> _unresolved = new();

    internal GeneratorSource(Action<string> report) => _once = new OnceReporter(report);

    public string Category => MapObjectGeneratorAddress.Category;

    public MapObjectReference? Parse(string text) => MapObjectGeneratorAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance)
    {
        // Only the two native kinds of this category are read at all: an instance of any other type is not a
        // generator, so it answers nothing and is not reported — the door and terminal readers refuse a foreign
        // instance the same way. A generator the level placed but this world cannot address is reported once
        // per cause.
        if (instance is not LG_PowerGenerator_Core && instance is not LG_PowerGeneratorCluster) return null;
        if (GeneratorObservation.AddressOf(instance) is { } address) return address;
        var reason = GeneratorObservation.UnresolvedReason(instance);
        if (_unresolved.Add((GeneratorObservation.World, ((UnityEngine.Component)instance).Pointer, reason)))
        {
            GeneratorObservation.RecordIdentity("unresolved", instance, reason);
            Report("compat.generator-identity-unresolved instance=" + ((UnityEngine.Component)instance).GetInstanceID()
                + ": no current author placement, generator-group member or generator-group identity resolves to this native instance ("
                + GeneratorObservation.WhyUnresolved(instance) + ").");
        }
        return null;
    }

    public MapObjectObservation? Read(object instance) => GeneratorObservation.Read(instance);

    public double[]? Position(object instance) => GeneratorObservation.Position(instance);

    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => GeneratorObservation.AddressOf(instance) == address;

    public void Report(string message)
    {
        _once.Report(message, message);
    }
}

/// <summary>
/// The game-bound half of the generator category: the one native read every generator fact and the generator
/// value row are answered from, and the table of the instances this world has read.
///
/// The value row needs the native subject again when a plan asks, and an address is all the module holds, so this
/// half remembers the generators its own reads produced and drops them with the world they belong to. An address
/// the table does not hold is a generator this provider never read, and the reader refuses it rather than reading
/// a same-serial object of a world it no longer holds.
/// </summary>
internal static class GeneratorObservation
{
    internal const string IdentityLogCode = "map.generator-identity";
    internal static LG_PowerGenerator_Core? LabelReceiver(EntityReference reference)
    {
        const string prefix = EntityKinds.MapObject + ":";
        if (!reference.Id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return Generators.TryGetValue(reference.Id[prefix.Length..], out var generator)
            && generator != null && !generator.WasCollected ? generator : null;
    }
    private static readonly Dictionary<string, LG_PowerGenerator_Core> Generators = new(StringComparer.Ordinal);
    private sealed record Placement(MapObjectReference Address, LG_PowerGeneratorCluster? Cluster, string? Group = null);
    private static readonly Dictionary<IntPtr, Placement> Placements = new();
    private static long _world = long.MinValue;
    internal static long World => _world;

    /// <summary>One world's table. A new world epoch drops the native instances of the level that no longer
    /// exists, so an address of the old level cannot answer with an instance of the new one.</summary>
    private static void Observe(long world)
    {
        if (world == _world) return;
        RecordReset("world-reset", world);
        _world = world;
        Generators.Clear();
        Placements.Clear();
    }

    /// <summary>A host migration keeps the level and its generators: the table moves to the new world number.</summary>
    internal static void Follow(long from, long to)
    {
        if (_world == from) _world = to;
    }

    internal static void Clear()
    {
        RecordReset("cleared", long.MinValue);
        _world = long.MinValue;
        Generators.Clear(); Placements.Clear();
    }

    /// <summary>Called only with the consumer's exact resolved instance, after all identity and pose checks.
    /// Reusing an address or a body for a different placement is refused before any position write.</summary>
    internal static bool RegisterPlacement(long epoch, LG_PowerGenerator_Core generator, FixedItem row)
    {
        Observe(epoch);
        var authorId = row.TaskObject!.Scope == FixedTaskObjectScope.Zone ? row.TaskObject.PlacementId : row.Id;
        if (authorId == null || MapObjectGeneratorAddress.CreateAuthor(row.Locator.Dimension, row.Locator.Layer,
                row.Locator.LocalIndex, authorId) is not { } address) return false;
        return RegisterPlacement(epoch, generator, address, row.Group);
    }

    internal static bool RegisterPlacement(long epoch, LG_PowerGenerator_Core generator, MapObjectReference address, string? group = null)
    {
        Observe(epoch);
        if (Placements.TryGetValue(generator.Pointer, out var existing) && existing.Address != address
            || Generators.TryGetValue(address.ToString(), out var claimed) && claimed.Pointer != generator.Pointer)
        {
            RecordIdentity("registration-refused", generator, "placement-identity-conflict", address);
            return false;
        }
        // Fixed placement reparents the core into its authored Area for native culling. Retain the original
        // cluster association; that same cluster's native member array still owns its objective progress.
        Placements[generator.Pointer] = existing ?? new(address, generator.GetComponentInParent<LG_PowerGeneratorCluster>(), group);
        Generators[address.ToString()] = generator;
        if (existing == null) RecordIdentity("registered", generator, "none");
        MapHost.Session?.MapObjects?.RegisterPlacedGenerator(generator);
        return true;
    }

    /// <summary>Whether the table still belongs to the world the session is in. A session that is gone reads no
    /// table at all.</summary>
    private static bool Current() => MapHost.Session is not { } session || session.WorldEpoch == _world;

    /// <summary>One generator cell change, from the generator's own replicated state callback. The cell and the
    /// player come from the state the callback carries — the one record of what changed, which the instance
    /// itself does not hold — and the direction the fact reports is read back from the instance by the module.
    /// The generator is remembered so the value row can read it again.</summary>
    internal static void CellChanged(LG_PowerGenerator_Core generator, pPowerGeneratorState newState,
        MapObjectModule module)
    {
        Observe(module.WorldEpoch);
        RecordIdentity("cell-state", generator);
        if (AddressOf(generator) is { } address) Generators[address.ToString()] = generator;
        // One native callback, one row: the cell fact reports what the callback named and the running state the
        // same body left behind, re-read from the instance rather than taken from an argument.
        module.GeneratorCellChanged(generator, CellOf(newState.itemDataThatChangedTheState),
            LevelObjectObservation.ActorOf(newState.playerThatChangedTheState),
            ItemSlotObservation.ReferenceOf(newState.itemDataThatChangedTheState));
    }

    /// <summary>One group state change, from the group's own replicated state callback. The native cluster state
    /// carries a fog step and no member count, so the module re-reads the group's members through this half and
    /// publishes the group fact only when every one of them reads powered.</summary>
    internal static void GroupChanged(LG_PowerGeneratorCluster cluster, MapObjectModule module)
    {
        Observe(module.WorldEpoch);
        RecordIdentity("group-state", cluster);
        module.GeneratorGroupConnected(cluster);
    }

    /// <summary>A core with a resolved author placement is addressed by it. A core no placement names is addressed as
    /// a member of the native group it was built in: the group's identity plus its index in the group's member array.
    /// A core with neither has no address; it never falls back to a core serial or a scene search.</summary>
    internal static MapObjectReference? AddressOf(object instance)
    {
        if (instance is LG_PowerGenerator_Core generator)
        {
            if (generator.WasCollected || !Current()) return null;
            if (Placements.TryGetValue(generator.Pointer, out var placement)) return placement.Address;
            return MemberIndex(generator) is { } member && AddressOfGroup(member.Cluster) is { } group
                && ZoneIndex.Coordinates(LevelObjectObservation.ZoneOf(member.Cluster)!) is { } at
                ? MapObjectGeneratorAddress.CreateMember(at.Dimension, at.Layer, at.Zone, member.Cluster.m_serialNumber, member.Index)
                : null;
        }
        if (instance is LG_PowerGeneratorCluster cluster)
            return AddressOfGroup(cluster);
        return null;
    }

    /// <summary>The native group a core was built in and the core's index in its member array, or null when the core
    /// stands in no group's array.</summary>
    private static (LG_PowerGeneratorCluster Cluster, int Index)? MemberIndex(LG_PowerGenerator_Core generator)
    {
        var cluster = generator.GetComponentInParent<LG_PowerGeneratorCluster>();
        var members = cluster == null || cluster.WasCollected ? null : cluster.m_generators;
        if (members == null) return null;
        for (var i = 0; i < members.Length; i++)
            if (members[i] != null && members[i].Pointer == generator.Pointer) return (cluster!, i);
        return null;
    }

    /// <summary>The measured reason an instance has no address, so a report names which of the identity inputs was missing
    /// (the table of another world, no registered placement, or a group without a serial/zone) instead of the bare result.</summary>
    internal static string WhyUnresolved(object instance)
    {
        var path = instance is UnityEngine.Component component && !component.WasCollected
            ? component.transform.name + " parent=" + (component.transform.parent == null ? "none" : component.transform.parent.name) : "collected";
        if (instance is LG_PowerGenerator_Core core)
            return "core " + path + "; table world " + _world + (Current() ? " is current" : " is not the session's world")
                + ", registered placements " + Placements.Count + ", registered cores bound to this pointer "
                + (Placements.ContainsKey(core.Pointer) ? "yes" : "no")
                + ", group member index " + (MemberIndex(core) is { } member ? member.Index.ToString() : "none");
        if (instance is LG_PowerGeneratorCluster cluster)
        {
            var zone = LevelObjectObservation.ZoneOf(cluster);
            return "group " + path + "; serial " + cluster.m_serialNumber + ", zone "
                + (zone == null || zone.WasCollected ? "missing" : ZoneIndex.Coordinates(zone) == null ? "has no coordinates" : "resolved");
        }
        return "not a generator";
    }

    internal static string UnresolvedReason(object instance)
    {
        if (instance is UnityEngine.Component { WasCollected: true }) return "native-instance-collected";
        if (instance is LG_PowerGenerator_Core core)
            return !Current() ? "placement-table-world-mismatch"
                : Placements.ContainsKey(core.Pointer) ? "none"
                : MemberIndex(core) is not { } member ? "placement-unregistered"
                : UnresolvedReason(member.Cluster);
        if (instance is LG_PowerGeneratorCluster cluster)
        {
            if (cluster.m_serialNumber < 0) return "group-serial-unavailable";
            var zone = LevelObjectObservation.ZoneOf(cluster);
            return zone == null || zone.WasCollected ? "group-zone-unavailable"
                : ZoneIndex.Coordinates(zone) == null ? "group-zone-coordinates-unavailable" : "none";
        }
        return "not-a-generator";
    }

    private static void RecordReset(string phase, long nextWorld)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info) || _world == long.MinValue && Placements.Count == 0) return;
        MapLog.Record(RuntimeLogLevel.Info, IdentityLogCode, "Generator placement identity table lifecycle.",
            RuntimeLogValue.Of("phase", phase), _world == long.MinValue ? RuntimeLogValue.Of("tableWorld", "unavailable") : RuntimeLogValue.Of("tableWorld", _world),
            nextWorld == long.MinValue ? RuntimeLogValue.Of("nextWorld", "unavailable") : RuntimeLogValue.Of("nextWorld", nextWorld), RuntimeLogValue.Of("registeredPlacements", Placements.Count),
            RuntimeLogValue.Of("registeredCores", Generators.Count));
    }

    internal static void RecordIdentity(string phase, object instance, string? reason = null,
        MapObjectReference? requested = null)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info) || instance is not UnityEngine.Component component || component.WasCollected) return;
        reason ??= AddressOf(instance) == null ? UnresolvedReason(instance) : "none";
        var core = instance as LG_PowerGenerator_Core;
        var cluster = core == null ? instance as LG_PowerGeneratorCluster : ClusterOf(core);
        var zone = LevelObjectObservation.ZoneOf(component);
        var coordinates = zone == null || zone.WasCollected ? null : ZoneIndex.Coordinates(zone);
        MapLog.Record(RuntimeLogLevel.Info, IdentityLogCode, "Generator native instance and author identity readback.",
            RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", reason),
            RuntimeLogValue.Of("kind", core == null ? "group" : "core"), RuntimeLogValue.Of("instance", component.GetInstanceID()),
            RuntimeLogValue.Of("pointer", component.Pointer.ToInt64()),
            RuntimeLogValue.Of("address", AddressOf(instance)?.ToString() ?? "unresolved"),
            RuntimeLogValue.Of("requestedAddress", requested?.ToString() ?? "none"),
            _world == long.MinValue ? RuntimeLogValue.Of("tableWorld", "unavailable") : RuntimeLogValue.Of("tableWorld", _world), MapHost.Session is { } session ? RuntimeLogValue.Of("sessionWorld", session.WorldEpoch) : RuntimeLogValue.Of("sessionWorld", "unavailable"),
            RuntimeLogValue.Of("registeredPlacements", Placements.Count), RuntimeLogValue.Of("registered", Placements.ContainsKey(component.Pointer)),
            RuntimeLogValue.Of("nativeZone", zone?.Pointer.ToInt64() ?? 0),
            RuntimeLogValue.Of("zone", coordinates == null ? "unresolved" : $"{coordinates.Value.Dimension}:{coordinates.Value.Layer}:{coordinates.Value.Zone}"),
            RuntimeLogValue.Of("cluster", cluster?.Pointer.ToInt64() ?? 0),
            RuntimeLogValue.Of("status", core == null ? "not-a-core" : StatusOf(core).ToString()));
    }

    private static MapObjectReference? AddressOfGroup(LG_PowerGeneratorCluster component)
    {
        var serial = component.m_serialNumber;
        if (serial < 0) return null;
        var zone = LevelObjectObservation.ZoneOf(component);
        if (zone == null || zone.WasCollected) return null;
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return null;
        return MapObjectGeneratorAddress.CreateGroup(coordinates.Dimension, coordinates.Layer, coordinates.Zone, serial);
    }

    /// <summary>One instance re-read in one place: a generator answers its own powered reading beside the counts
    /// of the group it stands in, and a group answers the counts of its own members. Any other instance is not
    /// this category's and answers nothing.</summary>
    internal static MapObjectObservation? Read(object instance)
    {
        if (instance is LG_PowerGenerator_Core generator)
        {
            var (connected, total) = CountsOf(generator);
            return new MapObjectObservation(true, null, null, null,
                new MapObjectGeneratorSnapshot(StatusOf(generator) == ePowerGeneratorStatus.Powered, connected, total));
        }
        if (instance is LG_PowerGeneratorCluster cluster)
        {
            var (connected, total) = Counts(cluster);
            return new MapObjectObservation(true, null, null, null, null,
                new MapObjectGeneratorGroupSnapshot(connected, total));
        }
        return null;
    }

    internal static double[]? Position(object instance)
    {
        if (instance is not UnityEngine.Component component || component.WasCollected) return null;
        var position = component.transform.position;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

    /// <summary>The native group a generator belongs to, retained when its author placement binds before
    /// relocation; otherwise read from the original build hierarchy. A generator
    /// that stands under no group belongs to no group, and every count for it is zero, which the module reads as
    /// "no group" rather than as a completed one.</summary>
    internal static LG_PowerGeneratorCluster? ClusterOf(LG_PowerGenerator_Core generator)
    {
        var found = Current() && Placements.TryGetValue(generator.Pointer, out var placement)
            ? placement.Cluster : generator.GetComponentInParent<LG_PowerGeneratorCluster>();
        return found == null || found.WasCollected ? null : found;
    }

    /// <summary>The counts one generator reports: the author's group when its placement names one — every generator
    /// of this world placed with that group, wherever it stands — otherwise the native group it was built in.</summary>
    internal static (int Connected, int Total) CountsOf(LG_PowerGenerator_Core generator)
    {
        if (!Current() || !Placements.TryGetValue(generator.Pointer, out var placement) || placement.Group is not { } group)
            return Counts(ClusterOf(generator));
        int connected = 0, total = 0;
        foreach (var core in Generators.Values)
        {
            if (core == null || core.WasCollected || !Placements.TryGetValue(core.Pointer, out var other)
                || !string.Equals(other.Group, group, StringComparison.Ordinal)) continue;
            total++;
            if (StatusOf(core) == ePowerGeneratorStatus.Powered) connected++;
        }
        return (connected, total);
    }

    /// <summary>The two counts of one group: how many of its members read `Powered` and how many it holds. A
    /// group whose array does not read answers (0, 0).</summary>
    internal static (int Connected, int Total) Counts(LG_PowerGeneratorCluster? cluster)
    {
        if (cluster == null || cluster.WasCollected) return (0, 0);
        var members = cluster.m_generators;
        if (members == null) return (0, 0);
        int connected = 0, total = 0;
        for (int i = 0; i != members.Length; i++)
        {
            var member = members[i];
            if (member == null || member.WasCollected) continue;
            total++;
            if (StatusOf(member) == ePowerGeneratorStatus.Powered) connected++;
        }
        return (connected, total);
    }

    /// <summary>One generator's own replicated status. The state replicator's current state is the game's own
    /// reading of the generator; a replicator that does not read answers `UnPowered`, which is the value a
    /// generator holding no cell has.</summary>
    internal static ePowerGeneratorStatus StatusOf(LG_PowerGenerator_Core generator)
    {
        var replicator = generator.m_stateReplicator;
        return replicator == null ? ePowerGeneratorStatus.UnPowered : replicator.State.status;
    }

    /// <summary>One generator's powered reading and its group's counts, for
    /// `forge.condition.predicate.power`. The counts are the same derivation the group fact publishes, read at
    /// the moment of the query instead of remembered from the last change; a generator that stands under no
    /// group answers (0, 0), which is the group the level gave it.</summary>
    internal static JsonElement? ReadState(string generatorKey)
    {
        if (!Current() || !Generators.TryGetValue(generatorKey, out var generator) || generator.WasCollected)
            return null;
        var (connected, total) = CountsOf(generator);
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["value"] = RuntimeJson.From(StatusOf(generator) == ePowerGeneratorStatus.Powered),
            ["connected"] = RuntimeJson.From(connected),
            ["total"] = RuntimeJson.From(total)
        });
    }

    /// <summary>The cell one state change named, written as the item's own gear block id — the CRC the block
    /// carries. A state change with no item names none.</summary>
    private static string CellOf(Player.pItemData item)
    {
        uint crc = item.itemID_gearCRC;
        return crc == 0 ? "" : crc.ToString("x8", CultureInfo.InvariantCulture);
    }
}
