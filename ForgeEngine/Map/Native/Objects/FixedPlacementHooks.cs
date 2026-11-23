using System;
using System.Collections.Generic;
using CullingSystem;
using ForgeEngine.Framework;
using HarmonyLib;
using Player;
using UnityEngine;
using LevelGeneration;

namespace ForgeMap.Native;

// Vanilla links corrupted uplinks in FinalLogicLinking (76), after culling (51).
// Authored endpoints must exist before their original Area bounds are collected.
// Move the original job, not its result or a second linking implementation.
[HarmonyPatch(typeof(LG_Factory), nameof(LG_Factory.InjectJob))]
internal static class AuthoredUplinkLinking
{
    [HarmonyPrefix, HarmonyPriority(Priority.Normal)]
    internal static void Prefix(LG_FactoryJob job, ref LG_Factory.BatchName batchName)
    {
        if ((int)batchName == 76 && MapHost.Session?.Placements is { } placements)
            Schedule(job, ref batchName, placements.GeneratorRows);
    }

    internal static void Schedule(LG_FactoryJob job, ref LG_Factory.BatchName batchName, IReadOnlyList<FixedItem> rows)
    {
        if ((int)batchName != 76 || job.TryCast<LG_TerminalCorruptedUplinkLinkerJob>() is not { } linker
            || linker.m_zone is not { } zone || zone.m_layer == null) return;
        foreach (var row in rows)
            if (row.TaskObject is { Kind: FixedTaskObjectKind.UplinkTerminal, Receiver: true }
                && row.Locator.Dimension == (int)zone.m_dimensionIndex && row.Locator.Layer == (int)zone.m_layer.m_type
                && row.Locator.LocalIndex == (int)zone.LocalIndex)
            { batchName = (LG_Factory.BatchName)50; return; }
    }
}

/// <summary>
/// Task objects move before the first native Area culling collection (<see cref="PlacementBeforeCulling"/>).
/// Ordinary fixed spawns retain the native FactoryDone stage (<see cref="FactoryDoneHooks"/>). Both entries
/// use the same session documents and world epoch on every peer.
///
/// The callback runs on every machine, because every machine generates the level locally; which of them places
/// what is the session's own decision (items on every peer, enemies on the host alone). Nothing is read from the
/// patched body: the level's own identity, the authored documents and the world epoch are the session's, so this
/// hook chooses when to ask.
///
/// The generation entry points are recorded in `evidence/map-hooks.json`; cargo setup is verified by the cargo adapter cases. The classes are listed in
/// `MapNativeHooks.Types`, which is the list the plugin declares to Runtime.
/// </summary>
[HarmonyPatch(typeof(C_BuildCullerNode), nameof(C_BuildCullerNode.Build))]
internal static class PlacementBeforeCulling
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix()
    {
        ZoneIndex.Reset();
        if (MapHost.Session is { } session)
        {
            session.Build(session.PlacedModels, models => models.Place(), "placed model before native culling");
            session.Build(session.Placements, placements => TaskObjectPlacement.Place(session.WorldEpoch,
                placements.GeneratorRows, message => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FixedPlacementRefused, message)),
                "task placement before native culling");
        }
    }
}

/// <summary>Observes one native objective-cargo setup and records only the queue delta that call produced.</summary>
[HarmonyPatch(typeof(ElevatorCargoCage), nameof(ElevatorCargoCage.SetupForWardenObjective))]
internal static class ObjectiveCargoSetup
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(ElevatorCargoCage __instance, int __0, out FixedPlacementSession.CargoBuild? __state)
    {
        __state = null;
        if (MapHost.Session is not { } session) return;
        FixedPlacementSession.CargoBuild? build = null;
        session.Build(session.Placements, placements => build = placements.BeginObjectiveCargo(__instance, __0),
            "native cargo setup receipt");
        __state = build;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(FixedPlacementSession.CargoBuild? __state)
    {
        if (__state == null || MapHost.Session is not { } session) return;
        session.Build(session.Placements, placements => placements.CompleteObjectiveCargo(__state), "native cargo setup delta");
    }
}

/// <summary>Only the authored dimension instance in this world receives an authored answer. The native
/// query still runs once, including its random selection. Checkpoint pose replay and ordinary revive do not
/// use this entry; random/main-dimension course-graph warp paths remain outside this hook.</summary>
[HarmonyPatch(typeof(LG_Dimension), nameof(LG_Dimension.GetSpawnPoint))]
internal static class DimensionLandingPointResult
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Dimension __instance, PlayerAgent player, ref Transform __result)
    {
        var session = MapHost.Session;
        if (session == null) return;
        Transform? authored = null;
        session.Run(session.Placements, placements =>
        {
            if (placements.TryLandingPoint(__instance, player, out var point)) authored = point;
        }, "dimension landing selection");
        if (authored != null) __result = authored;
    }
}

/// <summary>Owns only appended author transforms, never the native list or spawn mode. Author slot order
/// is independent of the original list prefix. Teardown and failed registration remove only owned entries.</summary>
internal sealed class FixedLandingPoints
{
    private sealed record Entry(LG_Dimension Level, IntPtr LevelPointer, Transform[] Points, HashSet<IntPtr> Identities, Action Remove)
    {
        internal bool Ready;
        internal int Destroyed;
    }
    private readonly Dictionary<IntPtr, Entry> _entries = new();
    private long _epoch = long.MinValue;

    internal void Register(long epoch, LG_Dimension level, Transform[] points)
    {
        if (epoch == 0 || level is null || level.WasCollected || level == null || level.m_spawnPoints == null
            || points.Length != FixedPlacement.MaximumLandingSlots)
            throw new ArgumentException("Invalid authored dimension landing registration.");
        foreach (var point in points)
            if (point is null || point.WasCollected || point == null) throw new ArgumentException("Missing authored landing transform.");
        if (_epoch != epoch) { Clear(); _epoch = epoch; }
        if (_entries.ContainsKey(level.Pointer)) throw new InvalidOperationException("Dimension landings already registered.");
        var list = level.m_spawnPoints;
        var owned = (Transform[])points.Clone();
        var identities = new HashSet<IntPtr>();
        foreach (var point in owned)
            if (!identities.Add(point.Pointer)) throw new ArgumentException("Duplicate authored landing transform.");
        var before = new IntPtr[list.Count];
        for (var i = 0; i < before.Length; i++)
        {
            before[i] = list[i]?.Pointer ?? IntPtr.Zero;
            if (identities.Contains(before[i])) throw new ArgumentException("Authored landing already in native list.");
        }
        // Track each actual receiving alias around the native call. A failed append may have written
        // before throwing, or the field may have changed; no owned object is destroyed until all known
        // aliases have confirmed its withdrawal. The old native prefix is never restored from a snapshot.
        var aliases = new Dictionary<IntPtr, Action>();
        void CaptureAlias()
        {
            // A confirmed destroyed/collected dimension has no live field to inspect. Unknown read faults
            // still propagate and retain the receipt; they are not treated as successful cleanup.
            if (level is null || level.WasCollected || level == null) return;
            var current = level.m_spawnPoints;
            if (current is null || current.WasCollected || aliases.ContainsKey(current.Pointer)) return;
            aliases.Add(current.Pointer, () =>
            {
                // A collected native list is inaccessible, not a live table whose entries were removed.
                // Retire its ownership receipt without reading Count or restoring an obsolete snapshot.
                if (current.WasCollected) return;
                for (var i = current.Count - 1; i >= 0; i--)
                    if (current[i] is { } candidate && identities.Contains(candidate.Pointer)) current.RemoveAt(i);
                for (var i = 0; i < current.Count; i++)
                    if (current[i] is { } candidate && identities.Contains(candidate.Pointer))
                        throw new InvalidOperationException("Authored landing withdrawal was not observed.");
            });
        }
        CaptureAlias();
        var entry = new Entry(level, level.Pointer, owned, identities, () =>
        {
            var failures = new List<Exception>();
            try { CaptureAlias(); } catch (Exception error) { failures.Add(error); }
            foreach (var remove in new List<Action>(aliases.Values))
                try { remove(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Authored landing aliases remain attached.", failures);
        });
        _entries.Add(level.Pointer, entry);
        try
        {
            for (var i = 0; i < owned.Length; i++)
            {
                CaptureAlias();
                try { level.AddSpawnPoint(owned[i]); }
                catch
                {
                    try { CaptureAlias(); }
                    // Rollback retains the receipt while its receiving alias cannot be read.
                    catch (Exception aliasError) { MapLog.Contained("fixed-placement.landing-alias-rollback", aliasError); }
                    throw;
                }
                CaptureAlias();
                if (level.m_spawnPoints == null || level.m_spawnPoints.Pointer != list.Pointer
                    || list.Count != before.Length + i + 1)
                    throw new InvalidOperationException("Native landing append was not observed.");
                for (var j = 0; j < before.Length; j++)
                    if ((list[j]?.Pointer ?? IntPtr.Zero) != before[j])
                        throw new InvalidOperationException("Native landing prefix changed during append.");
                for (var j = 0; j <= i; j++)
                    if (list[before.Length + j]?.Pointer != owned[j].Pointer)
                        throw new InvalidOperationException("Native landing identity changed during append.");
            }
            entry.Ready = true;
        }
        catch
        {
            try { entry.Remove(); _entries.Remove(level.Pointer); }
            // Keep the receipt for Clear; the registration exception is the one rethrown.
            catch (Exception removeError) { MapLog.Contained("fixed-placement.landing-entry-rollback", removeError); }
            throw;
        }
    }

    internal bool Owns(Transform point)
    {
        foreach (var entry in _entries.Values)
            if (entry.Identities.Contains(point.Pointer)) return true;
        return false;
    }

    internal bool TrySelect(long epoch, LG_Dimension level, PlayerAgent? player, out Transform? point)
    {
        point = null;
        if (epoch == 0 || epoch != _epoch || level is null || level.WasCollected || level == null || player is null || player.WasCollected || player == null
            || !_entries.TryGetValue(level.Pointer, out var entry) || !entry.Ready || entry.Level.WasCollected) return false;
        var slot = player.PlayerSlotIndex;
        if (slot < 0) return false;
        var found = entry.Points[slot % entry.Points.Length];
        if (found is null || found.WasCollected || found == null) return false;
        point = found;
        return true;
    }

    internal void Clear()
    {
        var entries = new List<Entry>(_entries.Values);
        foreach (var entry in entries) entry.Ready = false;
        var failures = new List<Exception>();
        foreach (var entry in entries)
        {
            try
            {
                entry.Remove();
                while (entry.Destroyed < entry.Points.Length)
                {
                    var point = entry.Points[entry.Destroyed];
                    if (point is not null && !point.WasCollected && point != null) UnityEngine.Object.Destroy(point.gameObject);
                    entry.Destroyed++;
                }
                _entries.Remove(entry.LevelPointer);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (_entries.Count == 0) _epoch = long.MinValue;
        if (failures.Count != 0) throw new AggregateException("Authored landing cleanup is incomplete.", failures);
    }
}
