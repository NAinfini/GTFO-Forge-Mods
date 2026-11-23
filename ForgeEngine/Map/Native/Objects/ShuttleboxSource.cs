using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using GameData;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

// The one build point a shuttlebox is known by. A zone's `ExpeditionZoneData.DumbwaiterPlacements` are distributed by
// `LG_Distribute_DumbwaitersPerZone` as `ExpeditionFunction.Dumbwaiter` function markers, and the object the level
// spawned for a function marker is handed to the marker builder's own `SetupFunctionGO` with the builder's function
// and course node on it. The postfix only remembers which object was built for which zone; it reads nothing else
// and changes nothing. Listed in `MapNativeHooks.Types` and frozen in `evidence/map-hooks.json`.
[HarmonyPatch(typeof(LG_FunctionMarkerBuilder), nameof(LG_FunctionMarkerBuilder.SetupFunctionGO))]
internal static class ShuttleboxBuilt
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_FunctionMarkerBuilder __instance, GameObject GO)
    {
        try
        {
            if (__instance.m_function != ExpeditionFunction.Dumbwaiter) return;
            ShuttleboxRegistry.Built(__instance.m_node?.m_zone, GO);
        }
        catch (Exception failure)
        {
            ShuttleboxRegistry.Failed(failure);
        }
    }
}

/// <summary>
/// The shuttleboxes this level built, in the order the game built them, each with the zone its function marker
/// stood in. A shuttlebox's address key is its ordinal among its zone's shuttleboxes in that order. The table is
/// this package's own patch-level state: a row whose zone or object was destroyed with its level is dropped before
/// the next row is added and before any lookup, so a level never answers with another level's object.
///
/// A zone whose built count differs from its own `DumbwaiterPlacements` count is not addressed at all: the ordinals
/// would no longer name the placements the author wrote, so the zone is reported once as `map.shuttlebox-count` and
/// none of its shuttleboxes is named rather than a guessed one.
/// </summary>
internal static class ShuttleboxRegistry
{
    private readonly record struct Row(LG_Zone Zone, GameObject Root);

    private static readonly List<Row> Rows = new();
    private static Action<string>? _report;
    private static readonly OnceReporter Once = new(message => _report?.Invoke(message));

    /// <summary>The session's report, handed over when the native half is composed.</summary>
    internal static void Attach(Action<string> report) => _report = report;

    /// <summary>The level is cleaned up: its rows go and each problem is reported again for the next level.</summary>
    internal static void Reset()
    {
        Rows.Clear();
        Once.Reset();
    }

    internal static void Built(LG_Zone? zone, GameObject? root)
    {
        Prune();
        if (zone == null || zone.WasCollected || root == null || root.WasCollected)
        {
            ReportOnce("unplaced", "map.shuttlebox-unplaced: a shuttlebox was built with no zone or no object, so it "
                + "has no address.");
            return;
        }
        foreach (var row in Rows)
            if (row.Root.Pointer == root.Pointer) return;
        Rows.Add(new Row(zone, root));
    }

    internal static void Failed(Exception failure)
        => ReportOnce("failed:" + failure.GetType().Name, "map.shuttlebox-unplaced: reading a built shuttlebox failed ("
            + failure.GetType().Name + "), so it has no address.");

    /// <summary>The address of the shuttlebox whose object is <paramref name="part"/> or one of its parents, or null
    /// when the part belongs to no shuttlebox this level built or its zone cannot be addressed.</summary>
    internal static MapObjectReference? AddressOfPart(Transform? part)
    {
        Prune();
        for (var current = part; current != null; current = current.parent)
        {
            int index = IndexOf(current.gameObject);
            if (index >= 0) return AddressOf(index);
        }
        return null;
    }

    /// <summary>The address of one shuttlebox object, or null when it is not one this level built.</summary>
    internal static MapObjectReference? AddressOfRoot(GameObject? root)
    {
        Prune();
        int index = root == null ? -1 : IndexOf(root);
        return index < 0 ? null : AddressOf(index);
    }

    /// <summary>The shuttlebox object one address names, or null when this level built none by it.</summary>
    internal static GameObject? RootOf(MapObjectReference address)
    {
        Prune();
        for (int i = 0; i != Rows.Count; i++)
            if (AddressOf(i) == address) return Rows[i].Root;
        return null;
    }

    private static int IndexOf(GameObject target)
    {
        for (int i = 0; i != Rows.Count; i++)
            if (Rows[i].Root.Pointer == target.Pointer) return i;
        return -1;
    }

    private static MapObjectReference? AddressOf(int index)
    {
        var zone = Rows[index].Zone;
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return null;
        int ordinal = 0, built = 0;
        for (int i = 0; i != Rows.Count; i++)
        {
            if (Rows[i].Zone.Pointer != zone.Pointer) continue;
            if (i < index) ordinal++;
            built++;
        }
        int placements = zone.m_settings?.m_zoneData?.DumbwaiterPlacements?.Count ?? -1;
        if (placements != built)
        {
            ReportOnce("count:" + coordinates.Dimension + ":" + coordinates.Layer + ":" + coordinates.Zone,
                "map.shuttlebox-count zone=gtfo.zone:" + Text(coordinates.Dimension) + ":" + Text(coordinates.Layer) + ":"
                + Text(coordinates.Zone) + " built=" + Text(built) + " placements=" + Text(placements)
                + ": the zone built a different number of shuttleboxes than it places, so none of them is addressed.");
            return null;
        }
        return MapObjectShuttleboxAddress.Create(coordinates.Dimension, coordinates.Layer, coordinates.Zone, ordinal);
    }

    private static void Prune()
        => Rows.RemoveAll(row => row.Zone == null || row.Zone.WasCollected || row.Root == null || row.Root.WasCollected);

    private static void ReportOnce(string key, string message)
    {
        Once.Report(key, message);
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The shuttlebox reader. It answers for the object the level built for a shuttlebox and for any part under it —
/// the insertion target the game hands `CarryItemInteractionUtils.TryInsertItem` is a component of that object — and
/// addresses it by <see cref="MapObjectShuttleboxAddress"/>. It holds no state of its own besides the build table.
/// </summary>
internal sealed class ShuttleboxSource : IShuttleboxSource
{
    private readonly OnceReporter _once;

    internal ShuttleboxSource(Action<string> report)
    {
        _once = new OnceReporter(report);
        ShuttleboxRegistry.Attach(report);
    }

    public string Category => MapObjectShuttleboxAddress.Category;

    public MapObjectReference? Parse(string text) => MapObjectShuttleboxAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance) => instance switch
    {
        GameObject root => ShuttleboxRegistry.AddressOfRoot(root),
        Component part when !part.WasCollected => ShuttleboxRegistry.AddressOfPart(part.transform),
        _ => null
    };

    public MapObjectObservation? Read(object instance) => TryAddress(instance) == null ? null : new MapObjectObservation(true, null, null);

    public double[]? Position(object instance)
    {
        var transform = instance switch { GameObject root => root.transform, Component part => part.transform, _ => null };
        if (transform == null) return null;
        var position = transform.position;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

    public bool IsCurrentAddress(object instance, MapObjectReference address) => TryAddress(instance) == address;

    public bool Holds(MapObjectReference address) => ShuttleboxRegistry.RootOf(address) != null;

    public void Report(string message)
    {
        _once.Report(message, message);
    }
}
