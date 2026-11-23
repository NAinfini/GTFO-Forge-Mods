using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

/// <summary>The A1 start donor supplies real native components. Nothing recreates the elevator or objective state machine.
/// A root recipe takes the place of a floor transition: <c>start</c> the level's elevator, <c>dimension</c> the root of another
/// dimension (evidence/dimension-root-donor-20403457.json). Both are built from the same donor by the same code.</summary>
internal static class RoomRecipeFunctions
{
    internal const string StartSource = "Assets/AssetPrefabs/Complex/Mining/Geomorphs/geo_32x32_elevator_shaft_mining_03_tutorial.prefab";
    /// <summary>Clones parked under a hidden DontDestroyOnLoad parent, captured once when the resource set loads and kept for every
    /// later level. A clone something else destroyed is captured again instead of being handed to a build.</summary>
    private static readonly Dictionary<string, GameObject> Donors = new(StringComparer.Ordinal);
    private static GameObject? staging;
    private static string? Source(RoomBuildRecipe recipe) => recipe.Design?.Function is "start" or "dimension" ? StartSource : null;
    /// <summary><paramref name="required"/> is false for a resource set other than the room's own: a dimension's root recipe lives in that
    /// dimension's set while the donor shaft loads with a Mining set, which may load first or later.</summary>
    internal static void Capture(RoomBuildRecipe recipe, bool required = true)
    {
        string? path = Source(recipe);
        if (path == null || Donors.TryGetValue(path, out var kept) && kept != null) return;
        var source = ForgeCore.GameAssets.Get<GameObject>(path);
        if (source == null)
        {
            if (required) throw new InvalidOperationException("recipe-functional-source-not-loaded: " + path);
            return;
        }
        if (staging == null) { staging = new GameObject("Forge native function donors"); staging.SetActive(false); Object.DontDestroyOnLoad(staging); }
        Donors[path] = Object.Instantiate<GameObject>(source, staging.transform);
    }
    internal static GameObject TemplateSource(RoomBuildRecipe recipe, GameObject shell)
    {
        string? path = Source(recipe);
        if (path == null) return shell;
        return Donors.TryGetValue(path, out var donor) && donor != null ? donor
            : throw new InvalidOperationException("recipe-functional-source-not-captured-before-expansion");
    }
    internal static LG_Area? Preserve(GameObject root, RoomBuildRecipe recipe)
    {
        if (Source(recipe) == null) return null;
        // A dimension root keeps only what a vanilla dimension root carries: the floor transition and its spawn points under the
        // first area (LG_Dimension takes them, CompiledMapGenerator.AttachDimensionComponent). The elevator landing is the start's alone.
        bool start = recipe.Design!.Function == "start";
        var transition = root.GetComponent<LG_FloorTransition>() ?? throw new InvalidOperationException("recipe-start-floor-transition-missing");
        var landing = root.GetComponent<ElevatorShaftLanding>() ?? throw new InvalidOperationException("recipe-start-landing-missing");
        var area = root.transform.Find("Area_A")?.gameObject.GetComponent<LG_Area>()
            ?? throw new InvalidOperationException("recipe-start-area-missing");
        if (transition.m_spawnPoints == null || transition.m_spawnPoints.Length != 4 || start && (landing.m_securityScanAlign == null
            || landing.m_cargoCageAlign == null || landing.m_objectsToHideWhenDown == null || landing.m_objectsToHideWhenDown.Length != 14))
            throw new InvalidOperationException("recipe-start-functional-group-incomplete");
        var keep = new HashSet<IntPtr>();
        void Keep(Transform node)
        {
            if (node == null) throw new InvalidOperationException("recipe-start-reference-missing");
            keep.Add(node.Pointer);
        }
        foreach (var point in transition.m_spawnPoints) Keep(point);
        if (start)
        {
            foreach (var hidden in landing.m_objectsToHideWhenDown) Keep(hidden.transform);
            Keep(landing.m_securityScanAlign); Keep(landing.m_cargoCageAlign);
        }
        // These donor nodes were verified in A1's serialized tree. The vanilla shaft chamber is not part of the authored
        // room: its LG_PrefabSpawner_elevator_* pieces would hang inside it, and the donor's InvisibleWalls/Flightblocker
        // colliders fence the vanilla pit, which an authored room does not have. Only what the landing references stays
        // (above); the independent Ride/Cage/seat scene is left to vanilla.
        int removedShaft = 0;
        void Shaft(Transform node)
        {
            if (node.gameObject.GetComponent<LG_PrefabSpawner>() != null
                && node.name.StartsWith("LG_PrefabSpawner_elevator_", StringComparison.Ordinal) && !keep.Contains(node.Pointer)) removedShaft++;
            for (int i = 0; i < node.childCount; i++) Shaft(node.GetChild(i));
        }
        Shaft(area.transform);
        if (start)
            foreach (var name in new[] { "FloorFix", "SoundEmitter" })
                Keep(area.transform.Find(name) ?? throw new InvalidOperationException("recipe-start-donor-node-missing: " + name));
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.Sources, "Root room drops the vanilla shaft pieces and barrier colliders.",
                RuntimeLogValue.Of("function", recipe.Design.Function!), RuntimeLogValue.Of("phase", "start-shaft-prune"),
                RuntimeLogValue.Of("shaftRemoved", removedShaft),
                RuntimeLogValue.Of("resource", recipe.ResourceKey));
        bool Retained(Transform node)
        {
            if (keep.Contains(node.Pointer)) return true;
            bool descendant = false;
            for (int i = node.childCount - 1; i >= 0; i--)
            {
                var child = node.GetChild(i);
                if (Retained(child)) descendant = true; else Object.DestroyImmediate(child.gameObject);
            }
            return descendant;
        }
        Retained(area.transform);
        for (int i = root.transform.childCount - 1; i >= 0; i--)
            if (root.transform.GetChild(i).Pointer != area.transform.Pointer) Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
        if (!start) Object.DestroyImmediate(landing);
        return area;
    }
    internal static void RequireInstance(LG_Geomorph room, RoomBuildRecipe recipe)
    {
        if (recipe.Design?.Function == "start")
        {
            // LG_Floor.Root is the floor's Transform, not the independent FloorTransition instance.
            // Vanilla resolves its spawn through MainDimension/MainLayer/Zone 0/Area 0 below.
            if (Builder.CurrentFloor?.Root == null)
                throw new InvalidOperationException("recipe-start-floor-unavailable");
            var area = room.m_areas[0]; var zone = area.m_zone;
            if (zone == null || zone.m_layer == null || zone.m_areas == null || (int)zone.m_dimensionIndex != 0 || (int)zone.m_layer.m_type != 0 || zone.LocalIndex != 0
                || zone.m_areas.Count == 0 || zone.m_areas[0].Pointer != area.Pointer)
                throw new InvalidOperationException("recipe-start-spawn-area-mismatch");
        }
    }
    internal static void Reset(bool clear)
    {
        if (!clear) return; Donors.Clear(); if (staging != null) Object.DestroyImmediate(staging); staging = null;
    }
}
