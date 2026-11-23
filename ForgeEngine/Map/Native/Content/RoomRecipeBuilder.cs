using System;
using System.Linq;
using AIGraph;
using LevelGeneration;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

/// <summary>Only called before the native custom-prefab lookup. Never modifies an original asset.</summary>
internal static partial class RoomRecipeBuilder
{
    internal static GameObject Build(RoomBuildRecipe recipe, Transform staging)
    {
        var source = ForgeCore.GameAssets.Get<GameObject>(recipe.Template) ?? throw new InvalidOperationException("recipe-source-not-loaded");
        // Every donor must hold its prototypes; one captured before its resource arrays expanded stays usable.
        foreach (var donor in recipe.Donors)
        {
            if (RoomRecipeParts.Captured(donor)) continue;
            var loaded = donor.Prefab == recipe.Template ? source : ForgeCore.GameAssets.Get<GameObject>(donor.Prefab)
                ?? throw new InvalidOperationException("recipe-donor-not-loaded: " + donor.Prefab);
            RoomRecipeParts.Capture(donor, loaded);
        }
        // Keep the original geomorph/node-volume components, discard every original child.
        // The inactive staging parent keeps templates out of navigation, lights and culling collection.
        var root = Object.Instantiate<GameObject>(RoomRecipeFunctions.TemplateSource(recipe, source), staging);
        try
        {
            root.name = recipe.ResourceKey;
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            var preservedArea = RoomRecipeFunctions.Preserve(root, recipe);
            if (preservedArea == null) for (int i = root.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
            var geo = root.GetComponent<LG_Geomorph>() ?? throw new InvalidOperationException("recipe-source-not-geomorph");
            var volume = root.GetComponent<AIG_GeomorphNodeVolume>()
                ?? throw new InvalidOperationException("recipe-source-node-volume-missing");
            // Discard the donor's override; the new room supplies its own (RoomRecipeNavigationBounds) below.
            volume.m_sizeOverrideTransform = null;
            geo.m_nodeVolume = volume;
            geo.m_areasSetup = false; geo.m_gatesSetup = false; geo.m_buildScanPerformed = false; geo.m_placed = false;
            geo.m_geoPrefab = null; geo.m_tile = null!; geo.m_zone = null; geo.m_mapMarker = null;
            geo.m_plugs = new Il2CppSystem.Collections.Generic.List<LG_Plug>();
            geo.m_prefabSpawners = new Il2CppReferenceArray<LG_PrefabSpawner>(0);
            geo.m_randomSelectors = new Il2CppReferenceArray<LG_RandomSelectorBase>(0);
            // One LG_Area per exported area, in export order; a start room's first area is the donor's native Area_A, where vanilla resolves the spawn.
            var areas = recipe.Areas.Select((definition, index) =>
            {
                bool preserved = index == 0 && preservedArea != null;
                var areaRoot = preserved ? preservedArea!.gameObject : Child(root.transform, definition.Id);
                var area = preserved ? preservedArea! : areaRoot.AddComponent<LG_Area>();
                area.m_geomorph = geo; area.m_size = LG_AreaSize.Medium;
                area.m_gates = new Il2CppSystem.Collections.Generic.List<LG_Gate>();
                area.m_zoneExpanders = new Il2CppSystem.Collections.Generic.List<LG_ZoneExpander>();
                var graphRoot = Child(areaRoot.transform, "AreaAIGraphSource");
                graphRoot.transform.localPosition = V(definition.GraphSource);
                var graph = graphRoot.AddComponent<LG_AreaAIGraphSource>();
                graph.m_positionCalculated = false; area.m_groupSource = graph;
                return area;
            }).ToArray();
            geo.m_areas = areas;
            RoomRecipeNavigationBounds.Build(geo, volume, areas[0], recipe);
            RoomRecipeSockets.Build(geo, recipe);
            BuildInteriorGates(geo, recipe);
            RoomRecipeDoorIdentity.Register(geo, recipe);
            var props = areas.Select(a => Child(a.transform, "EnvProps")).ToArray();
            int spawners = 0;
            foreach (var part in recipe.Parts)
            {
                int index = recipe.AreaIndex(part.Area);
                var area = areas[index];
                var selected = RoomRecipeParts.Get(part.Prefab, part.Node);
                var copy = Object.Instantiate<GameObject>(selected, props[index].transform);
                copy.name = part.Id;
                copy.transform.localPosition = V(part.Position);
                copy.transform.localRotation = Quaternion.Euler(V(part.Rotation));
                copy.transform.localScale = V(part.Scale);
                // Any vanilla object is cloned whole; a spawner among them is reset so native expansion builds it in this area.
                foreach (var spawner in copy.GetComponentsInChildren<LG_PrefabSpawner>(true))
                {
                    spawner.m_isBuilt = false; spawner.m_area = area; spawner.m_zone = null;
                    if (spawner.gameObject.Pointer != copy.Pointer) continue;
                    spawners++; spawner.m_applyScale = true;
                    if (!RoomRecipeParts.KeepsSpawnerFlags(part.Node)) { spawner.m_disableCollision = false; spawner.m_useNoGraphLayerOverride = false; }
                }
            }
            foreach (var wall in recipe.AirWalls) RoomRecipeAirWalls.Build(props[recipe.AreaIndex(wall.Area)].transform, wall);
            RoomRecipeColliders.Build(props, recipe);
            RoomRecipeJumpLinks.Build(root.transform, recipe);
            RoomRecipeLadders.Build(root.transform, areas, recipe);
            RoomRecipeMarkers.Build(root, areas, recipe);
            geo.ScanPrefabSpawners();
            if (geo.m_prefabSpawners.Length < spawners) throw new InvalidOperationException("recipe-spawner-count");
            // Random selectors inside cloned vanilla objects roll as they do in their own room.
            geo.m_randomSelectors = new Il2CppReferenceArray<LG_RandomSelectorBase>(root.GetComponentsInChildren<LG_RandomSelectorBase>(true).ToArray());
            root.SetActive(true); // activeSelf for PlaceRoot's clone, inactive in the template staging hierarchy.
            return root;
        }
        catch { Object.DestroyImmediate(root); throw; }
    }
    private static GameObject Child(Transform parent, string name)
    {
        var child = new GameObject(name); child.transform.SetParent(parent, false); return child;
    }
    private static Vector3 V(float[] values) => new(values[0], values[1], values[2]);
}
