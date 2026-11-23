using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

/// <summary>A paired connector is the native model whole: pairing, divider, crossing, both sides, gate and culling jobs all run as the
/// game builds them, and the recipe room only supplies its own side and the connector's pose. Only an unpaired port is sealed, and the
/// connector's random marker slots in the walkway in front of a recipe room's door are dropped (connected or capped alike): an authored room
/// may wall its approach at the door's width, and a crate or locker drawn there closes the way.
/// Runs before native selector/spawner jobs are queued, not after the markers have made objects:
/// LG_LateGeomorphScanJob queues one ladder job per LG_Ladder it finds, and a cap's marker products
/// carry ladders, so anything destroyed after that scan leaves jobs holding destroyed ladders.</summary>
internal static partial class RoomRecipeBuilder
{
    private static readonly HashSet<IntPtr> SealedPorts = new();
    private static readonly HashSet<IntPtr> OpenPorts = new();
    private static readonly HashSet<IntPtr> AuthoredCaps = new();
    internal static void BeforeSpawners(GameObject root)
    {
        var divider = root.GetComponent<LG_PortalDivider>();
        if (divider == null) return;
        LG_Plug? plug = null;
        for (var parent = root.transform.parent; parent != null && plug == null; parent = parent.parent)
            plug = parent.gameObject.GetComponent<LG_Plug>();
        if (plug == null) return;
        if (plug.m_pariedWith == null && !AuthoredCaps.Contains(plug.Pointer)) { SealUnusedPort(plug, divider); return; }
        ClearApproach(root, plug);
        if (plug.m_pariedWith != null) ClearApproach(root, plug.m_pariedWith);
    }
    /// <summary>How far beside the door edge and how deep into the room the walkway runs: one walker's width (agent type 0 is 1 m across)
    /// and the connector's own 8 m reach (plugReach on the website).</summary>
    private const float ApproachMargin = 1f, ApproachDepth = 8f;
    /// <summary>Drops the connector's marker producers standing in front of a recipe socket, room side (socket local z &lt; 0, outward is +Z),
    /// before LG_Factory collects them, so no random prop is ever drawn there. Static dressing stays; the website settles a connector whose
    /// dressing leaves the approach walkable (connectorApproachOpen).</summary>
    private static void ClearApproach(GameObject root, LG_Plug socket)
    {
        if (RoomRecipeHost.RecipeFor(socket.OriginalParentArea) == null || RoomRecipeDoorIdentity.Resolve(socket)?.Door is not { } door) return;
        var size = door.Size.Split('x');
        float half = float.Parse(size[0], CultureInfo.InvariantCulture) / 2 + ApproachMargin, height = float.Parse(size[1], CultureInfo.InvariantCulture);
        var origin = socket.transform.position;
        var inverse = Quaternion.Inverse(socket.transform.rotation);
        int removed = 0;
        foreach (var producer in root.GetComponentsInChildren<LG_MarkerProducer>(true))
        {
            var local = inverse * (producer.transform.position - origin);
            if (Math.Abs(local.x) > half || local.z > 0 || local.z < -ApproachDepth || local.y < -0.5f || local.y > height) continue;
            Object.DestroyImmediate(producer);
            removed++;
        }
        if (removed > 0 && MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.ConnectionCleaned, "Connector marker slots in front of a recipe door dropped before marker collection.",
                RuntimeLogValue.Of("prefab", root.name), RuntimeLogValue.Of("phase", "approach-markers"), RuntimeLogValue.Of("count", removed));
    }
    /// <summary>The room's author (or the map's) named the cap for this port: the native cap stays as built, with its own markers and spawners.</summary>
    internal static void KeepAuthoredCap(LG_Plug plug) => AuthoredCaps.Add(plug.Pointer);
    /// <summary>LG_BuildPlugJob spawns a cap and calls FindAndBuildSelectorsAndSpawners on it exactly as it
    /// does for a paired connector (call sites 0x1278D52 and 0x1279011 of build 20403457), so an unused
    /// recipe port is sealed at the same window as a paired side: before its marker producers, selectors
    /// and spawners are collected. The wall panels (one per 4 m of the doorway's authored width) are ordinary unbuilt spawners that the native
    /// spawner batch builds, and nothing native has seen the cap's content yet.
    /// Original cap prefabs serialize only behind; front and crossing are absent. Keep that native divider
    /// shape and put the replacement wall in its existing bucket. The panel is the template's
    /// <see cref="RoomBuildRecipe.SealWallSpawner"/>.</summary>
    private static void SealUnusedPort(LG_Plug plug, LG_PortalDivider divider)
    {
        var ownArea = plug.OriginalParentArea;
        var recipe = RoomRecipeHost.RecipeFor(ownArea);
        if (recipe == null) return;
        var wallRoot = divider.m_behind ?? throw new InvalidOperationException("recipe-cap-divider-behind-missing: " + divider.name);
        Clear(divider.m_inFront);
        Clear(wallRoot);
        Clear(divider.m_crossing);
        // The template donor's own flat panel, captured with every recipe; it never depends on what the author placed.
        var prototype = RoomRecipeParts.Get(recipe.Template, RoomBuildRecipe.SealWallSpawner);
        var door = RoomRecipeDoorIdentity.Resolve(plug)?.Door ?? throw new InvalidOperationException("recipe-cap-not-a-recipe-socket: " + plug.name);
        foreach (float offset in RoomBuildRecipe.SealPanelOffsets(door.Size))
        {
            var copy = Object.Instantiate<GameObject>(prototype, wallRoot);
            copy.name = "Forge unused port wall " + offset;
            copy.transform.position = plug.transform.position + plug.transform.rotation * new Vector3(offset, 2, 0);
            copy.transform.rotation = plug.transform.rotation * Quaternion.Euler(-90, 0, 0);
            copy.transform.localScale = Vector3.one;
            var spawner = copy.GetComponent<LG_PrefabSpawner>() ?? throw new InvalidOperationException("recipe-cap-not-spawner");
            spawner.m_isBuilt = false; spawner.m_area = ownArea; spawner.m_zone = ownArea!.m_zone;
            spawner.m_applyScale = true; spawner.m_disableCollision = false; spawner.m_useNoGraphLayerOverride = false;
        }
        SealedPorts.Add(plug.Pointer);
        Record(divider.name);
    }
    /// <summary>Every unpaired port of a placed recipe room except an exit the compiled plan explicitly leaves open
    /// must have been sealed by now; a requested cap whose native content survived is an explicit refusal.</summary>
    internal static void RequireSealedPorts(LG_Geomorph room)
    {
        foreach (var plug in room.m_plugs)
            if (plug.m_pariedWith == null && RoomRecipeHost.RecipeFor(plug.OriginalParentArea) != null
                && !OpenPorts.Contains(plug.Pointer) && !SealedPorts.Contains(plug.Pointer) && !AuthoredCaps.Contains(plug.Pointer))
                throw new InvalidOperationException("recipe-cap-unsealed: " + plug.name);
    }
    internal static void LeavePortOpen(LG_Plug plug) => OpenPorts.Add(plug.Pointer);
    internal static void ForgetSealedPorts() { SealedPorts.Clear(); OpenPorts.Clear(); AuthoredCaps.Clear(); }
    private static void Record(string prefab)
    {
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.ConnectionCleaned, "Unused native port sealed with original flat panels before spawner collection.",
                RuntimeLogValue.Of("prefab", prefab), RuntimeLogValue.Of("phase", "before-native-spawners"));
    }
    private static void Clear(Transform? part)
    {
        if (part == null) return; // Native caps have no geometry on the absent side.
        for (int i = part.childCount - 1; i >= 0; i--) Object.DestroyImmediate(part.GetChild(i).gameObject);
    }
}
[HarmonyPatch(typeof(LG_Factory), "FindAndBuildSelectorsAndSpawners")]
internal static class RoomRecipeConnectionSpawners
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(GameObject rootGO)
    {
        // The native trampoline would swallow a throw here and leave the job retrying; end generation instead.
        try { RoomRecipeBuilder.BeforeSpawners(rootGO); }
        catch (Exception error) { RoomRecipeDoorSelection.Fail("recipe-connection-failed: " + error.GetBaseException().Message); }
    }
}
