using System;
using System.Collections.Generic;
using System.Globalization;
using ChainedPuzzles;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Identity and native puzzle state of the level's selected win-condition item. No endpoint
/// inherits another endpoint's state, and the marker visibility is never treated as the scan state.</summary>
internal static class ExtractionObservation
{
    private static readonly Dictionary<string, Component> Items = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Ambiguous = new(StringComparer.Ordinal);
    private static long _world = long.MinValue;
    internal static void Clear() { Items.Clear(); Ambiguous.Clear(); _world = long.MinValue; }
    /// <summary>A host migration keeps the level: the exits already seen stay under the new world number.</summary>
    internal static void Follow(long from, long to) { if (_world == from) _world = to; }

    private static void World(long world)
    {
        if (_world == world) return;
        _world = world; Items.Clear(); Ambiguous.Clear();
    }

    /// <summary>The course node the endpoint was built in: the node the game hands its exit wave.</summary>
    internal static AIGraph.AIG_CourseNode? BuiltNode(Component item) => item switch
    {
        ElevatorShaftLanding landing => landing.SpawnNode,
        LG_LevelExitGeo exit => exit.SpawnNode,
        _ => null
    };

    internal static string? Address(Component item)
    {
        var node = BuiltNode(item);
        if (node?.m_zone is not { } zone || ZoneIndex.Coordinates(zone) is not { } location) return null;
        var kind = item is ElevatorShaftLanding ? LevelEndpointAddress.Elevator : LevelEndpointAddress.Exit;
        return LevelEndpointAddress.Extraction + "/" + location.Dimension.ToString(CultureInfo.InvariantCulture)
            + "/" + location.Layer.ToString(CultureInfo.InvariantCulture) + "/"
            + location.Zone.ToString(CultureInfo.InvariantCulture) + "/" + kind;
    }

    private static bool Selected(Component item)
    {
        var session = MapHost.Session;
        if (session == null) return true;
        if (session.ExtractionPoints is { } points) return points.Owns(item);
        // No admitted point configuration selects the level's native elevator. Other native exit geometry
        // must not become a second endpoint merely because no point document was loaded.
        return item is ElevatorShaftLanding landing && ElevatorShaftLanding.Current is { } current
            && !current.WasCollected && current.Pointer == landing.Pointer;
    }

    internal static bool AllowsNativeEndpoint(bool elevator, ExtractionPointSession? points, MapLevelReference? level)
    {
        if (points == null) return true;
        if (points.Rows != 0 && level == null)
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ExtractionHostRefused, "extraction level identity unavailable",
                RuntimeLogValue.Of("reason", "level-unavailable"));
            return false;
        }
        return elevator != points.UsesPoint(level);
    }

    private static ChainedPuzzleInstance? Puzzle(Component item) => item switch
    {
        ElevatorShaftLanding landing => landing.m_puzzle,
        LG_LevelExitGeo exit => exit.m_puzzle,
        _ => null
    };

    internal static void Remember(Component item, long world)
    {
        World(world);
        if (item == null || item.WasCollected || !Selected(item) || Address(item) is not { } key) return;
        if (Items.TryGetValue(key, out var existing) && existing.Pointer != item.Pointer)
        {
            if (existing != null && !existing.WasCollected) { Ambiguous.Add(key); return; }
        }
        Items[key] = item;
    }

    internal static Component? Resolve(EntityReference reference, long world)
    {
        if (reference.WorldEpoch != world || reference.LifeEpoch != 1
            || LevelEndpointAddress.Key(reference, LevelEndpointAddress.Extraction) is not { } key) return null;
        World(world);
        if (ElevatorShaftLanding.Current is { } landing && !landing.WasCollected) Remember(landing, world);
        return !Ambiguous.Contains(key) && Items.TryGetValue(key, out var item)
            && item != null && !item.WasCollected && Selected(item) && Address(item) == key ? item : null;
    }

    internal static EntityReference? Reference(string address, long world)
    {
        if (LevelEndpointAddress.Parse(address) is not { Category: LevelEndpointAddress.Extraction } parsed) return null;
        var reference = LevelEndpointAddress.Reference(parsed, world);
        return Resolve(reference, world) != null ? reference : null;
    }

    internal static bool? Read(EntityReference reference, long world)
    {
        if (reference.WorldEpoch != world)
            throw new RuntimeContractException(ObjectiveActionContract.ExtractionQueryStaleCode, reference.Id);
        var item = Resolve(reference, world);
        var puzzle = item == null ? null : Puzzle(item);
        return puzzle == null || puzzle.WasCollected ? null : puzzle.IsActive;
    }
}

// Each win-condition entry exists on both endpoint types, and each (type, entry) pair is its own native target with its own
// declaration. The elevator landing and the level exit geometry share one remembering body, so the six classes below
// differ only in the target and the reading they pass.

[HarmonyPatch(typeof(ElevatorShaftLanding), nameof(ElevatorShaftLanding.OnBuildDone))]
internal static class ExtractionLandingBuilt
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ElevatorShaftLanding __instance)
    {
        if (MapHost.Session is { } session) ExtractionObservation.Remember(__instance, session.WorldEpoch);
    }
}

[HarmonyPatch(typeof(LG_LevelExitGeo), nameof(LG_LevelExitGeo.OnBuildDone))]
internal static class ExtractionExitBuilt
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_LevelExitGeo __instance)
    {
        if (MapHost.Session is { } session) ExtractionObservation.Remember(__instance, session.WorldEpoch);
    }
}

[HarmonyPatch(typeof(ElevatorShaftLanding), nameof(ElevatorShaftLanding.ActivateWinCondition))]
internal static class ExtractionLandingActivated
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix()
        => ExtractionObservation.AllowsNativeEndpoint(true, MapHost.Session?.ExtractionPoints, LevelIdentity.Read());

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ElevatorShaftLanding __instance)
    {
        if (MapHost.Session is { } session) ExtractionObservation.Remember(__instance, session.WorldEpoch);
    }
}

// Keep the native landing and its scan construction intact. Only the level's selected extraction kind may
// occupy the native win-condition slots or activate the elevator's exit scan.
[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.RegisterElevatorExitWinConditionItem))]
internal static class ExtractionElevatorRegistered
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix()
        => ExtractionObservation.AllowsNativeEndpoint(true, MapHost.Session?.ExtractionPoints, LevelIdentity.Read());
}

[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.RegisterCustomGeoExitWinConditionItem))]
internal static class ExtractionCustomExitRegistered
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix()
        => ExtractionObservation.AllowsNativeEndpoint(false, MapHost.Session?.ExtractionPoints, LevelIdentity.Read());
}

[HarmonyPatch(typeof(LG_LevelExitGeo), nameof(LG_LevelExitGeo.ActivateWinCondition))]
internal static class ExtractionExitActivated
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix()
        => ExtractionObservation.AllowsNativeEndpoint(false, MapHost.Session?.ExtractionPoints, LevelIdentity.Read());

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_LevelExitGeo __instance)
    {
        if (MapHost.Session is { } session) ExtractionObservation.Remember(__instance, session.WorldEpoch);
    }
}

[HarmonyPatch(typeof(ElevatorShaftLanding), nameof(ElevatorShaftLanding.DeactivateWinCondition))]
internal static class ExtractionLandingDeactivated
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ElevatorShaftLanding __instance)
    {
        if (MapHost.Session is { } session) ExtractionObservation.Remember(__instance, session.WorldEpoch);
    }
}

[HarmonyPatch(typeof(LG_LevelExitGeo), nameof(LG_LevelExitGeo.DeactivateWinCondition))]
internal static class ExtractionExitDeactivated
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_LevelExitGeo __instance)
    {
        if (MapHost.Session is { } session) ExtractionObservation.Remember(__instance, session.WorldEpoch);
    }
}
