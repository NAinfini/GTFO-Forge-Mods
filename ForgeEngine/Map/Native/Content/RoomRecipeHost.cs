using System;
using System.Collections.Generic;
using System.Linq;
using AssetShards;
using GameData;
using LevelGeneration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using ForgeEngine.Framework;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

internal static class RoomRecipeCodes
{
    internal const string Constructed = "room.build-constructed";
    internal const string Registered = "room.build-registered";
    internal const string Placed = "room.build-placed";
    internal const string Navmesh = "room.build-navmesh";
    internal const string Aigraph = "room.build-aigraph";
    internal const string NavigationReadback = "room.build-navigation";
    internal const string Refused = "room.build-refused";
    internal const string Report = "room.build-report";
    internal const string PartsRemoved = "room.build-parts-removed";
    internal const string Sources = "room.build-sources";
    internal const string ConnectionCleaned = "room.build-connection-cleaned";
    internal const string PlugSelected = "room.build-plug-selected";
    internal const string GateSelected = "room.build-gate-selected";
    internal const string Parts = "room.build-parts";
    internal const string Marker = "room.build-marker";
}

internal static class RoomRecipeHost
{
    private static readonly Dictionary<string, RoomBuildRecipe> Recipes = new(StringComparer.Ordinal);
    /// <summary>The donor asset roots found so far, by prefab key; a donor not loaded yet is looked up again next time.</summary>
    private static readonly Dictionary<string, Transform> DonorOriginals = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (GameObject Template, List<ComplexResourceSetDataBlock> Sets)> Built = new(StringComparer.Ordinal);
    private static readonly List<LG_Geomorph> Instances = new();
    private static readonly HashSet<(string Source, string Path)> RefusedPlacedModelPaths = new();
    private static readonly HashSet<(string Source, string Path)> UnavailablePlacedModelPaths = new();
    private static GameObject? staging;
    private static bool navStarted, navDone;
    private static readonly System.Diagnostics.Stopwatch navClock = new();
    /// <summary>The prefab PlaceRoot is currently placing; the only non-null object a refused lookup can hand native code.</summary>
    internal static GameObject? PlacementInput;
    internal static void Activate(IReadOnlyDictionary<string, RoomBuildRecipe> recipes)
    {
        RoomBuildRecipe.RequireUniqueFunctionalHosts(recipes.Values);
        Recipes.Clear();
        foreach (var pair in recipes) Recipes.Add(pair.Key, pair.Value);
    }
    internal static void Prepare(bool hasRecipes, bool hasPlacedModels = false)
    {
        if ((hasRecipes || hasPlacedModels) && !MapFieldHost.Hooks.Offers(MapHookFeatures.RoomResourcesFeature))
            throw new InvalidOperationException("room-resource-capture-unavailable");
        if (hasRecipes && !MapFieldHost.Hooks.Offers(MapHookFeatures.RoomRecipesFeature))
            throw new InvalidOperationException("room-recipe-hooks-unavailable");
    }
    /// <summary>The one error record of a refused authored room, written only by the first request to end generation.</summary>
    private static void EndGeneration(string reason, string resource, string sourcePrefab, string phase, params RuntimeLogValue[] extra)
    {
        if (!RoomGenerationFailure.Request()) return;
        var values = new RuntimeLogValue[5 + extra.Length];
        values[0] = RuntimeLogValue.Quote("reason", reason); values[1] = RuntimeLogValue.Of("resource", resource);
        values[2] = RuntimeLogValue.Of("sourcePrefab", sourcePrefab); values[3] = RuntimeLogValue.Of("phase", phase);
        values[4] = RuntimeLogValue.Of("outcome", "end-playing");
        extra.CopyTo(values, 5);
        // log-values-max: 8 -- five fixed values and at most the 3 extra values the callers pass.
        Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused, "Custom room refused; generation is ending.", values);
    }
    internal static void MarkerStaged(RoomBuildRecipe recipe, RoomPlacementMarker marker, string sourcePrefab, string sourcePath,
        LG_MarkerDataBlockType? dataBlockType, uint dataBlockId, uint groupId)
        => Log(RuntimeLogLevel.Info, RoomRecipeCodes.Marker, "Authored room placement marker staged before native area marker collection.",
            RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("markerId", marker.Id),
            RuntimeLogValue.Of("area", marker.Area), RuntimeLogValue.Of("category", marker.Category),
            RuntimeLogValue.Of("markerDataBlockType", dataBlockType?.ToString() ?? "world-event"),
            RuntimeLogValue.Of("markerDataBlockId", (long)dataBlockId), RuntimeLogValue.Of("markerGroupId", (long)groupId),
            RuntimeLogValue.Of("sourcePrefab", sourcePrefab), RuntimeLogValue.Of("sourcePath", sourcePath),
            RuntimeLogValue.Of("phase", "before-area-marker-collection"));
    /// <summary>A donor asset's own spawner entering its native OnBuild is captured at its exact hierarchy path, when a
    /// recipe uses that path. AssetShardManifest keys need not equal the referenced GameObject's name.</summary>
    internal static void CaptureSources(LG_PrefabSpawner source)
    {
        if (source.m_prefab == null || Recipes.Count == 0) return;
        // Every spawner of every level passes here: a spawner outside every donor's hierarchy is turned away on its
        // root alone, before any parent walk or string is made.
        var sourceRoot = RootOf(source.transform);
        foreach (var recipe in Recipes.Values)
            foreach (var donor in recipe.Donors)
            {
                if (!DonorOriginals.TryGetValue(donor.Prefab, out var original) || original == null)
                {
                    original = ForgeCore.GameAssets.Get<GameObject>(donor.Prefab)?.transform;
                    if (original == null) { DonorOriginals.Remove(donor.Prefab); continue; }
                    DonorOriginals[donor.Prefab] = original;
                }
                if (RootOf(original) != sourceRoot) continue;
                var path = PathBelow(source.transform, original);
                if (path == null || !donor.Nodes.Contains(path, StringComparer.Ordinal)) continue;
                try
                {
                    if (RoomRecipeParts.CaptureSpawner(donor.Prefab, path, source)) Log(RuntimeLogLevel.Info, RoomRecipeCodes.Sources,
                        "Original spawner captured before its native OnBuild destroys it.",
                        RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("sourcePrefab", donor.Prefab),
                        RuntimeLogValue.Of("sourcePath", path), RuntimeLogValue.Of("phase", "before-native-spawner-build"));
                }
                catch (Exception error)
                {
                    Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused, "Room part prototype refused.",
                        RuntimeLogValue.Quote("reason", "recipe-source-capture: " + error.GetBaseException().Message),
                        RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("sourcePrefab", donor.Prefab),
                        RuntimeLogValue.Of("sourcePath", path), RuntimeLogValue.Of("phase", "before-native-spawner-build"));
                }
            }
    }
    private static IntPtr RootOf(Transform node)
    {
        while (node.parent != null) node = node.parent;
        return node.Pointer;
    }
    private static string? PathBelow(Transform node, Transform root)
    {
        var names = new List<string>();
        for (var parent = node; parent != null; parent = parent.parent)
        {
            if (parent.Pointer == root.Pointer) { names.Reverse(); return names.Count == 0 ? null : string.Join("/", names); }
            names.Add(parent.gameObject.name);
        }
        return null;
    }
    internal static bool IsRecipe(string resource) => Recipes.ContainsKey(resource);
    internal static bool HasFunctionalRecipes => Recipes.Values.Any(recipe => recipe.Hosts.Count != 0);
    internal static RoomBuildRecipe? FunctionalRecipe(MapLevelReference level, string function)
    {
        RoomBuildRecipe? found = null;
        foreach (var recipe in Recipes.Values)
            if (recipe.Design?.Function == function && recipe.Hosts.Any(host => host.Level == level))
            {
                if (found != null) throw new InvalidOperationException("recipe-second-functional-host");
                found = recipe;
            }
        return found;
    }
    internal static GameObject PreparedTemplate(ComplexResourceSetDataBlock set, RoomBuildRecipe recipe)
    {
        Prepare(set, recipe.ResourceKey);
        return Built[recipe.ResourceKey].Template;
    }
    internal static void SelectedStart(GameObject template)
    {
        foreach (var pair in Built)
            if (pair.Value.Template.Pointer == template.Pointer)
            {
                var recipe = Recipes[pair.Key];
                var level = LevelIdentity.Read() ?? throw new InvalidOperationException("recipe-functional-level-unreadable");
                var host = recipe.Hosts.Single(host => host.Level == level);
                Log(RuntimeLogLevel.Info, RoomRecipeCodes.Registered, "Native SetupFloor selected the authored FloorTransition input.",
                    RuntimeLogValue.Of("resource", pair.Key), RuntimeLogValue.Of("phase", "setup-floor-selection"),
                    RuntimeLogValue.Of("function", "start"), RuntimeLogValue.Of("instanceState", "staged"),
                    RuntimeLogValue.Of("roomDefinition", host.Room.RoomId), RuntimeLogValue.Of("revision", recipe.Revision), RuntimeLogValue.Of("level", level.ToString()),
                    RuntimeLogValue.Of("room", host.Room.InstanceId!), RuntimeLogValue.Of("zone", host.Room.ZoneAuthorId));
                return;
            }
        throw new InvalidOperationException("recipe-start-template-not-registered");
    }
    internal static void FunctionalFailure(Exception error, string phase)
    {
        var level = LevelIdentity.Read();
        const string function = "start";
        var recipe = Recipes.Values.FirstOrDefault(row => row.Design?.Function == function && row.Hosts.Any(host => host.Level == level));
        EndGeneration(error.GetBaseException().Message, recipe?.ResourceKey ?? "unresolved", recipe?.Template ?? "unresolved", phase,
            RuntimeLogValue.Of("function", function), RuntimeLogValue.Of("roomDefinition", recipe == null ? "unresolved" : "forge.room:" + recipe.Id),
            RuntimeLogValue.Of("revision", recipe?.Revision ?? "unresolved"));
    }

    internal static void CaptureLoadedSources(ComplexResourceSetDataBlock set)
    {
        // A source that another level places is not this level's to capture, and reporting it as unavailable made every
        // other level's run fail on an error about a room it never builds (RoomRecipeParts.RequestsFor).
        foreach (var request in RoomRecipeParts.RequestsFor(LevelIdentity.Read()))
        {
            // Use the native lookup's key rules. A source this level places that is not loaded yet stays eligible for a later set.
            GameObject? source;
            try
            {
                source = ForgeCore.GameAssets.Get<GameObject>(request.Key);
                if (source == null) throw new InvalidOperationException("recipe-source-not-loaded");
            }
            catch (Exception error)
            {
                foreach (var path in request.Value)
                    if (!RefusedPlacedModelPaths.Contains((request.Key, path))
                        && UnavailablePlacedModelPaths.Add((request.Key, path)))
                        Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused, "Placed-model source unavailable; waiting for a later resource set.",
                    RuntimeLogValue.Quote("reason", "placed-model-resource-capture: " + error.GetBaseException().Message),
                    RuntimeLogValue.Of("sourcePrefab", request.Key), RuntimeLogValue.Of("sourcePath", path),
                    RuntimeLogValue.Of("resourceSet", set.persistentID), RuntimeLogValue.Of("phase", "before-resource-arrays"));
                continue;
            }
            foreach (var path in request.Value)
            {
                if (RefusedPlacedModelPaths.Contains((request.Key, path))) continue;
                try
                {
                    if (RoomRecipeParts.Capture(request.Key, path, source)) Log(RuntimeLogLevel.Info, RoomRecipeCodes.Sources,
                        "Original placed-model prototype captured before native resource arrays expand its spawner.",
                        RuntimeLogValue.Of("sourcePrefab", request.Key), RuntimeLogValue.Of("sourcePath", path),
                        RuntimeLogValue.Of("sourceName", source.name), RuntimeLogValue.Of("phase", "before-resource-arrays"));
                }
                catch (Exception error)
                {
                    RefusedPlacedModelPaths.Add((request.Key, path));
                    Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused, "Placed-model part refused.",
                        RuntimeLogValue.Quote("reason", "placed-model-resource-capture: " + error.GetBaseException().Message),
                        RuntimeLogValue.Of("sourcePrefab", request.Key), RuntimeLogValue.Of("sourcePath", path),
                        RuntimeLogValue.Of("resourceSet", set.persistentID),
                        RuntimeLogValue.Of("phase", "before-resource-arrays"));
                }
            }
        }
        foreach (var recipe in Recipes.Values)
        {
            if (!recipe.AllowsResourceSet(set.persistentID))
            {
                RoomRecipeFunctions.Capture(recipe, required: false);
                continue;
            }
            try { RoomRecipeFunctions.Capture(recipe); }
            catch (Exception error)
            {
                Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused, "Room function donor refused.",
                    RuntimeLogValue.Quote("reason", "recipe-resource-capture: " + error.GetBaseException().Message),
                    RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("resourceSet", set.persistentID),
                    RuntimeLogValue.Of("phase", "before-resource-arrays"));
            }
            // Each donor is captured whole before its own resource arrays expand it; one failure refuses the room,
            // which Prepare later reports as the uncaptured prefab and path.
            foreach (var donor in recipe.Donors)
            {
                if (RoomRecipeParts.Captured(donor)) continue;
                try
                {
                    var source = ForgeCore.GameAssets.Get<GameObject>(donor.Prefab) ?? throw new InvalidOperationException("recipe-source-not-loaded: " + donor.Prefab);
                    RoomRecipeParts.Capture(donor, source);
                    Log(RuntimeLogLevel.Info, RoomRecipeCodes.Sources,
                        "Original prototypes captured before native resource arrays expand their spawners.",
                        RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("sourcePrefab", donor.Prefab),
                        RuntimeLogValue.Of("sourceName", source.name), RuntimeLogValue.Of("parts", donor.Nodes.Count),
                        RuntimeLogValue.Of("sourceChildren", source.transform.childCount), RuntimeLogValue.Of("phase", "before-resource-arrays"));
                }
                catch (Exception error)
                {
                    Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused, "Room part prototype refused; the room cannot be built.",
                        RuntimeLogValue.Quote("reason", "recipe-resource-capture: " + error.GetBaseException().Message),
                        RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("sourcePrefab", donor.Prefab),
                        RuntimeLogValue.Of("sourcePath", RoomRecipeParts.FailedPath(error) ?? "unresolved"), RuntimeLogValue.Of("resourceSet", set.persistentID),
                        RuntimeLogValue.Of("phase", "before-resource-arrays"));
                }
            }
        }
    }
    internal static bool Lookup(ComplexResourceSetDataBlock set, string path, ref GameObject result, out bool verify)
    {
        verify = false;
        if (!Recipes.TryGetValue(path, out var recipe)) return true;
        try
        {
            if (recipe.Design?.Function == "start") throw new InvalidOperationException("recipe-start-requires-setup-floor");
            Prepare(set, path); verify = true; return true;
        }
        catch (Exception error)
        {
            // A refused authored room ends generation: the one error record is written here and the factory
            // leaves through RoomGenerationFailure at its next update, never from inside this native frame.
            // Throwing is not an option: the IL2CPP trampoline swallows it and hands native generation a null room (c0000005).
            // The native frame still needs a non-null prefab for this single call, so it receives PlaceRoot's own
            // seeded input. Nothing consumes it as an authored room: Pending stops every zone job, TilePlaced and
            // the factory before the level can be built, and the level is torn down through AfterLevel.
            var source = ForgeCore.GameAssets.Get<GameObject>(recipe.Template);
            var input = PlacementInput ?? source;
            // sourcePrefab/sourcePath name the donor part that could not be cloned, else the template.
            EndGeneration(error.GetBaseException().Message, path, RoomRecipeParts.FailedPrefab(error) ?? recipe.Template, "room-lookup",
                RuntimeLogValue.Of("nativeInput", PlacementInput != null ? "placement-input" : source != null ? "source-prefab" : "none"),
                RuntimeLogValue.Of("sourcePath", RoomRecipeParts.FailedPath(error) ?? "unresolved"));
            // Without any input the native lookup runs unmodified; the pending exit still removes the level.
            if (input == null) return true;
            result = input;
            return false;
        }
    }
    internal static void Prepare(ComplexResourceSetDataBlock set, string path)
    {
        if (!Recipes.TryGetValue(path, out var recipe)) return;
        // Throws the refusal reason; Lookup is the one place that records it and ends generation.
        if (navStarted) throw new InvalidOperationException("recipe-navigation-already-started");
        if (!recipe.AllowsResourceSet(set.persistentID)) throw new InvalidOperationException("recipe-resource-set-mismatch");
        if (!Built.TryGetValue(path, out var entry))
        {
            staging ??= new GameObject("Forge room templates"); staging.SetActive(false);
            var template = RoomRecipeBuilder.Build(recipe, staging.transform);
            Log(RuntimeLogLevel.Info, RoomRecipeCodes.Sources, "Original part prototypes are available to the constructor.",
                RuntimeLogValue.Of("sourcePrefab", recipe.Template), RuntimeLogValue.Of("donors", recipe.Donors.Count),
                RuntimeLogValue.Of("phase", "ready-for-lookup"));
            entry = (template, new List<ComplexResourceSetDataBlock>());
            Built.Add(path, entry);
            Log(RuntimeLogLevel.Info, RoomRecipeCodes.Constructed, "Room template constructed.",
                RuntimeLogValue.Of("resource", path), RuntimeLogValue.Of("function", recipe.Design?.Function ?? "ordinary"),
                RuntimeLogValue.Of("roomDefinition", "forge.room:" + recipe.Id), RuntimeLogValue.Of("revision", recipe.Revision),
                RuntimeLogValue.Of("instanceState", "staged"), RuntimeLogValue.Of("parts", recipe.Parts.Count),
                RuntimeLogValue.Of("markers", recipe.Markers.Count),
                RuntimeLogValue.Of("donors", recipe.Donors.Count), RuntimeLogValue.Of("areas", recipe.Areas.Count),
                RuntimeLogValue.Of("cells", recipe.Areas.Sum(area => area.Cells.Count)), RuntimeLogValue.Of("exits", recipe.BuildDoorways.Count),
                RuntimeLogValue.Of("interiorLinks", recipe.InteriorLinks.Count()), RuntimeLogValue.Of("height", recipe.Height),
                RuntimeLogValue.Of("resourceSet", set.persistentID),
                RuntimeLogValue.Of("recipeHash", HashText(recipe.Sha256)));
        }
        // Register into the actual ComplexResourceSet lookup the unmodified GetCustomGeomorph consumes.
        if (set.m_allCustomGeomorphsPerPath.ContainsKey(path)
            && set.m_allCustomGeomorphsPerPath[path].Pointer != entry.Template.Pointer) throw new InvalidOperationException("recipe-custom-key-conflict");
        string key = path.ToUpperInvariant();
        if (AssetShardManager.s_loadedAssetsLookup.ContainsKey(key)
            && AssetShardManager.s_loadedAssetsLookup[key].Pointer != entry.Template.Pointer) throw new InvalidOperationException("recipe-loaded-key-conflict");
        set.m_allCustomGeomorphsPerPath[path] = entry.Template;
        AssetShardManager.s_loadedAssetsLookup[key] = entry.Template;
        if (!entry.Sets.Any(registered => registered.Pointer == set.Pointer)) entry.Sets.Add(set);
    }
    internal static void VerifyLookup(ComplexResourceSetDataBlock set, string path, GameObject? result)
    {
        if (!Recipes.TryGetValue(path, out var recipe)) return;
        if (!Built.TryGetValue(path, out var entry) || !entry.Sets.Any(registered => registered.Pointer == set.Pointer)
            || result == null || result.Pointer != entry.Template.Pointer)
        {
            // Native returned something other than the registered template; it may be vanilla geometry.
            EndGeneration("recipe-native-lookup-mismatch", path, recipe.Template, "room-lookup-verify");
            return;
        }
        Log(RuntimeLogLevel.Info, RoomRecipeCodes.Registered, "Native custom lookup returned the registered room template.",
            RuntimeLogValue.Of("resource", path), RuntimeLogValue.Of("resourceSet", (long)set.persistentID));
    }
    /// <summary>Raised for each recipe room right after the native generator placed it and before any later generation job.
    /// Read-only observers (ForgeDevelopment) subscribe through the public MapReadOnlyViews; Map never depends on them, and a handler must not throw.</summary>
    internal static event Action<LG_Geomorph>? RoomPlaced;
    /// <summary>A recipe room its placer turned or raised: its navigation bounds are sized again for the final turn and, when
    /// <see cref="Placed"/> already registered it, its position-lookup box is taken again at the final pose (otherwise that box keeps
    /// the pose of the native placement). A room <see cref="Placed"/> has not seen yet is registered there, at its final pose. A vanilla
    /// room is left alone.</summary>
    internal static void Moved(LG_Geomorph room)
    {
        if (room.m_geoPrefab == null) return;
        foreach (var entry in Built)
            if (entry.Value.Template.Pointer == room.m_geoPrefab.Pointer)
            {
                RoomRecipeNavigationBounds.Orient(room, Recipes[entry.Key], room.transform.rotation);
                if (Instances.Any(instance => instance.Pointer == room.Pointer)) RoomRecipeStackedVolumes.Register(room, Recipes[entry.Key]);
                return;
            }
    }
    /// <summary>Takes parts out of one placed room, before native builds its spawners: this instance then stands as if the recipe never had them, collision included.
    /// Each instance is a clone, so the template and the room's other instances keep them (a connection's door stands where these panels were, contract I-DOCK).</summary>
    internal static void RemoveParts(LG_Geomorph room, string roomId, string resource, string[] parts)
    {
        int missing = 0;
        foreach (var part in parts)
        {
            Transform? found = null;
            foreach (var area in room.m_areas) if ((found = area.transform.Find("EnvProps/" + part)) != null) break;
            if (found == null) missing++;
            else Object.DestroyImmediate(found.gameObject);
        }
        room.ScanPrefabSpawners();
        room.m_randomSelectors = new Il2CppReferenceArray<LG_RandomSelectorBase>(room.GetComponentsInChildren<LG_RandomSelectorBase>(true).ToArray());
        RoomRecipeSpatialReadback.Removed(room, parts);
        Log(missing == 0 ? RuntimeLogLevel.Info : RuntimeLogLevel.Error, RoomRecipeCodes.PartsRemoved, "Parts this room instance does not build, as its connections' doors stand there.",
            RuntimeLogValue.Of("resource", resource), RuntimeLogValue.Of("room", roomId), RuntimeLogValue.Of("removed", parts.Length - missing),
            RuntimeLogValue.Of("missing", missing), RuntimeLogValue.Quote("parts", string.Join(",", parts)));
    }
    internal static void Placed(LG_Geomorph? room)
    {
        if (room?.m_geoPrefab == null) return;
        if (Instances.Any(instance => instance.Pointer == room.Pointer)) return;
        foreach (var entry in Built)
            if (entry.Value.Template.Pointer == room.m_geoPrefab.Pointer)
            {
                try
                {
                    RoomRecipeDoorIdentity.Register(room, Recipes[entry.Key]);
                    RoomRecipeNavigationBounds.Orient(room, Recipes[entry.Key], room.transform.rotation);
                }
                catch (Exception error)
                {
                    if (RoomGenerationFailure.Request()) Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused,
                        "Placed room socket identities or navigation bounds do not match its recipe; generation is ending.",
                        RuntimeLogValue.Of("resource", entry.Key), RuntimeLogValue.Quote("reason", error.GetBaseException().Message));
                    return;
                }
                Instances.Add(room);
                RoomRecipeStackedVolumes.Register(room, Recipes[entry.Key]);
                RoomRecipeSpatialReadback.Placed(room, Recipes[entry.Key]);
                Log(RuntimeLogLevel.Info, RoomRecipeCodes.Placed, "Native generator placed the room.",
                    RuntimeLogValue.Of("resource", entry.Key), RuntimeLogValue.Of("function", Recipes[entry.Key].Design?.Function ?? "ordinary"),
                    RuntimeLogValue.Of("roomDefinition", "forge.room:" + Recipes[entry.Key].Id), RuntimeLogValue.Of("revision", Recipes[entry.Key].Revision),
                    RuntimeLogValue.Of("instanceState", "placed"), RuntimeLogValue.Of("areas", room.m_areas.Length),
                    RuntimeLogValue.Of("plugs", room.m_plugs.Count));
                RoomPlaced?.Invoke(room);
                return;
            }
    }
    internal static void Navigation(LG_BuildUnityGraphJob job)
    {
        if (Instances.Count == 0 || navStarted) return;
        foreach (var room in Instances) RoomRecipeBuilder.RequireSealedPorts(room);
        navStarted = true; navClock.Restart();
        Log(RuntimeLogLevel.Info, RoomRecipeCodes.Navmesh, "Native navigation build entered; completion is not yet confirmed.",
            RuntimeLogValue.Of("phase", "started"), RuntimeLogValue.Of("ignoreUnityBuildJob", LG_BuildUnityGraphJob.IgnoreUnityBuildJob));
    }
    internal static bool HasPlacedRecipes => Instances.Count != 0;
    /// <summary>The recipe the area's room was built from.</summary>
    /// <summary>MatchWithPortals (0x1A1E820) leaves a cluster without its course node when the flood missed any door: every native position
    /// lookup in that room then finds nothing (TryGetCourseNode 0x1A07900, PlayerAgent.UpdateGoodNodeAndArea 0x1309B40, C_MovingCuller.DetectNewNode
    /// 0x1369E50), so agents there keep a stale good position and only change node when they cross a portal. A recipe room with a partial hit keeps the
    /// cluster the flood built, by the native assignment the match itself uses; the missed door stays a report. A flood that touched no door started in the wrong place and stays failed.</summary>
    internal static void AcceptPartialScan(AIGraph.AIG_NodeClusterTools.NodeClusterScanData scan, bool matched)
    {
        var node = scan.m_sourceCourseNode; var hit = scan.CoursePortalsHit;
        if (matched || node == null || hit == null || hit.Count == 0 || scan.m_clusterResult == null || RecipeFor(node.m_area) == null) return;
        scan.m_clusterResult.AssignMatch(node);
    }
    internal static RoomBuildRecipe? RecipeFor(LG_Area? area)
    {
        var prefab = area?.m_geomorph?.m_geoPrefab;
        if (prefab == null) return null;
        foreach (var entry in Built)
            if (entry.Value.Template.Pointer == prefab.Pointer) return Recipes[entry.Key];
        return null;
    }
    internal static void NavigationDone()
    {
        if (!navStarted || navDone) return;
        navDone = true;
        Log(RuntimeLogLevel.Info, RoomRecipeCodes.Navmesh, "Native navigation job returned completed; walking and pathfinding still require game checks.",
            RuntimeLogValue.Of("phase", "completed"), RuntimeLogValue.Of("elapsedMs", navClock.ElapsedMilliseconds),
            RuntimeLogValue.Of("rooms", Instances.Count));
    }
    /// <summary>The FactoryDone postfix. Nothing may escape into the IL2CPP trampoline, which swallows it and lets the level
    /// finish; any failure is a refusal, and since LG_Factory.Update no longer runs the frame driver leaves the level.</summary>
    internal static void Complete()
    {
        try { CompleteRooms(); }
        catch (Exception error)
        {
            EndGeneration("recipe-factory-done-failed: " + error.GetBaseException().Message, "unresolved", "unresolved", "factory-done");
            RoomGenerationFailure.RecordException(error);
        }
        finally { RoomGenerationFailure.FactoryFinished(); }
    }
    private static void CompleteRooms()
    {
        if (HasFunctionalRecipes)
        {
            var level = LevelIdentity.Read() ?? throw new InvalidOperationException("recipe-functional-level-unreadable");
            if (FunctionalRecipe(level, "start") is { } expected
                && !Instances.Any(room => room.m_geoPrefab != null && Built.TryGetValue(expected.ResourceKey, out var built)
                    && room.m_geoPrefab.Pointer == built.Template.Pointer))
                throw new InvalidOperationException("recipe-functional-host-not-placed: " + expected.ResourceKey);
        }
        foreach (var room in Instances)
        {
            int courses = 0, pairs = 0;
            int CountLights(Transform node)
            {
                int count = node.gameObject.GetComponent<Light>() != null ? 1 : 0;
                for (int i = 0; i < node.childCount; i++) count += CountLights(node.GetChild(i));
                return count;
            }
            int lights = MapLog.Logs(RuntimeLogLevel.Info) ? CountLights(room.transform) : 0;
            foreach (var area in room.m_areas) if (area.m_courseNode != null) courses++;
            foreach (var plug in room.m_plugs) if (plug.m_pariedWith != null) pairs++;
            var recipe = RecipeFor(room.m_areas[0]);
            RoomFunctionalHost? functionalHost = null;
            if (recipe?.Design?.Function != null)
            {
                RoomRecipeFunctions.RequireInstance(room, recipe);
                var level = LevelIdentity.Read() ?? throw new InvalidOperationException("recipe-functional-level-unreadable");
                var host = recipe.Hosts.SingleOrDefault(host => host.Level == level)
                    ?? throw new InvalidOperationException("recipe-functional-host-not-declared");
                var zone = room.m_areas[0].m_zone ?? throw new InvalidOperationException("recipe-functional-zone-missing");
                if (zone.m_layer == null || (int)zone.m_dimensionIndex != host.Room.Dimension || (int)zone.m_layer.m_type != host.Room.Layer
                    || (int)zone.LocalIndex != host.Room.LocalIndex) throw new InvalidOperationException("recipe-functional-zone-mismatch");
                GeneratedRoomRegistry.Add(zone, host.Room, room);
                functionalHost = host;
            }
            // Both readbacks always run so their records exist even when the structure above already failed.
            var failures = new List<RuntimeLogValue>();
            bool partsPlaced = recipe != null && RoomRecipeSpatialReadback.CheckParts(room, recipe, failures) && RoomRecipeAttachments.Attach(room, recipe);
            if (recipe != null) RoomRecipeLooks.Paint(room, recipe);
            var report = new List<string>();
            bool linksValid = recipe != null && RoomRecipeSpatialReadback.Check(room, recipe, failures, report);
            if (report.Count > 0)
            {
                // Reported, not refused: the level goes on. One record per room, naming each area and door.
                for (int item = 0; item < report.Count; item++)
                    Log(RuntimeLogLevel.Error, RoomRecipeCodes.Report, "Room structure differs from the plan here; the level continues. Check the named area or door.",
                        RuntimeLogValue.Of("resource", recipe?.ResourceKey ?? "unresolved"),
                        RuntimeLogValue.Of("roomInstance", GeneratedRoomRegistry.LocatorFor(room)?.InstanceId ?? "unregistered"),
                        RuntimeLogValue.Of("position", FormattableString.Invariant($"({room.transform.position.x:0.##},{room.transform.position.y:0.##},{room.transform.position.z:0.##})")),
                        RuntimeLogValue.Of("item", item + 1), RuntimeLogValue.Of("items", report.Count),
                        RuntimeLogValue.Of("report", report[item].Length > 250 ? report[item].Substring(0, 250) : report[item]));
            }
            // No doorway is owed a partner: a room may be reached only by teleport, and every unpaired port is sealed (RequireSealedPorts).
            bool valid = courses == room.m_areas.Length && navDone && partsPlaced && linksValid;
            Log(RuntimeLogLevel.Info, RoomRecipeCodes.Aigraph, "Factory completed; this is structural readback, not enemy behavior acceptance.",
                RuntimeLogValue.Of("resource", recipe?.ResourceKey ?? "unresolved"), RuntimeLogValue.Of("function", recipe?.Design?.Function ?? "ordinary"),
                RuntimeLogValue.Of("room", functionalHost?.Room.InstanceId ?? "unresolved"), RuntimeLogValue.Of("zone", functionalHost?.Room.ZoneAuthorId ?? "unresolved"),
                RuntimeLogValue.Of("revision", recipe?.Revision ?? "unresolved"), RuntimeLogValue.Of("instanceState", "placed"),
                RuntimeLogValue.Of("actualArea", room.m_areas[0].UID.ToString()),
                RuntimeLogValue.Of("areas", room.m_areas.Length), RuntimeLogValue.Of("courseNodes", courses),
                RuntimeLogValue.Of("plugs", room.m_plugs.Count), RuntimeLogValue.Of("pairedPlugs", pairs), RuntimeLogValue.Of("realtimeLights", lights), RuntimeLogValue.Of("navmeshDone", navDone), RuntimeLogValue.Of("valid", valid));
            if (!valid)
            {
                RoomGenerationFailure.Request();
                var failed = new List<string>();
                if (courses != room.m_areas.Length) failed.Add("courseNodes");
                if (!navDone) failed.Add("navmeshDone");
                if (!partsPlaced) failed.Add("partsPlaced");
                if (!linksValid) failed.Add("linksValid");
                failures.InsertRange(0, new[] {
                    RuntimeLogValue.Of("reason", "recipe-spatial-readback-incomplete"), RuntimeLogValue.Of("failed", string.Join(",", failed)),
                    RuntimeLogValue.Of("areas", room.m_areas.Length), RuntimeLogValue.Of("courseNodes", courses),
                    RuntimeLogValue.Of("roomInstance", GeneratedRoomRegistry.LocatorFor(room)?.InstanceId ?? "unregistered"),
                    RuntimeLogValue.Of("pairedPlugs", pairs), RuntimeLogValue.Of("navmeshDone", navDone),
                    RuntimeLogValue.Of("partsPlaced", partsPlaced), RuntimeLogValue.Of("linksValid", linksValid),
                    RuntimeLogValue.Of("expected", $"areas={recipe?.Areas.Count ?? -1};courseNodes={room.m_areas.Length};navmeshDone=True;partsPlaced=True;linksValid=True"),
                    RuntimeLogValue.Of("resource", recipe?.ResourceKey ?? "unresolved") });
                Log(RuntimeLogLevel.Error, RoomRecipeCodes.Refused, "Room recipe structural readback refused.", failures.ToArray());
            }
        }
    }
    internal static void Reset(bool clearRecipes = false)
    {
        RefusedPlacedModelPaths.Clear(); UnavailablePlacedModelPaths.Clear(); DonorOriginals.Clear();
        RoomRecipeFunctions.Reset(clearRecipes); RoomRecipeSetupFloor.Current = null;
        Instances.Clear(); RoomRecipeDoorIdentity.Clear(); RoomRecipeSpatialReadback.Clear(); RoomRecipeSourceClearance.ForgetWalked(); ForgeCore.StackedVolumes.Clear(); RoomRecipeBuilder.ForgetSealedPorts(); RoomGenerationFailure.Reset(); navStarted = navDone = false; navClock.Reset();
        foreach (var entry in Built)
        {
            string key = entry.Key.ToUpperInvariant();
            if (AssetShardManager.s_loadedAssetsLookup.ContainsKey(key)
                && AssetShardManager.s_loadedAssetsLookup[key].Pointer == entry.Value.Template.Pointer) AssetShardManager.s_loadedAssetsLookup.Remove(key);
            foreach (var set in entry.Value.Sets)
                if (set.m_allCustomGeomorphsPerPath.ContainsKey(entry.Key)
                    && set.m_allCustomGeomorphsPerPath[entry.Key].Pointer == entry.Value.Template.Pointer)
                    set.m_allCustomGeomorphsPerPath.Remove(entry.Key);
        }
        Built.Clear(); if (staging != null) Object.DestroyImmediate(staging); staging = null;
        PlacementInput = null;
        if (clearRecipes) { Recipes.Clear(); RoomRecipeParts.Clear(); }
    }
    private static void Log(RuntimeLogLevel level, string code, string message, params RuntimeLogValue[] values)
    { if (MapLog.Logs(level)) MapLog.Record(level, code, message, values); }
    private static string HashText(string hash)
    {
        // Same grouped-hash spelling as existing content diagnostics; never interpreted as an account number.
        var chunks = new string[8]; for (int i = 0; i < chunks.Length; i++) chunks[i] = hash.Substring(i * 8, 8);
        return string.Join("-", chunks);
    }
}

[HarmonyPatch(typeof(ComplexResourceSetDataBlock), nameof(ComplexResourceSetDataBlock.GetCustomGeomorph))]
internal static class RoomRecipeLookup
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(ComplexResourceSetDataBlock __instance, string customGeoPath, ref GameObject __result, out bool __state)
        => RoomRecipeHost.Lookup(__instance, customGeoPath, ref __result, out __state);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(ComplexResourceSetDataBlock __instance, string customGeoPath, GameObject __result, bool __state)
    { if (__state) RoomRecipeHost.VerifyLookup(__instance, customGeoPath, __result); }
}
[HarmonyPatch(typeof(LG_LevelBuilder), nameof(LG_LevelBuilder.PlaceRoot))]
internal static class RoomRecipePlaced
{
    [HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
    internal static void Prefix(GameObject tilePrefab, out GameObject? __state)
    { __state = RoomRecipeHost.PlacementInput; RoomRecipeHost.PlacementInput = tilePrefab; }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Geomorph __result, GameObject? __state)
    { RoomRecipeHost.PlacementInput = __state; RoomRecipeHost.Placed(__result); }
    [HarmonyFinalizer]
    internal static Exception? Finalizer(GameObject? __state, Exception? __exception)
    {
        RoomRecipeHost.PlacementInput = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(ComplexResourceSetDataBlock), nameof(ComplexResourceSetDataBlock.PopulateFullResourceArrays))]
internal static class RoomRecipeResourceCapture
{ [HarmonyPrefix, HarmonyPriority(Priority.First)] internal static void Prefix(ComplexResourceSetDataBlock __instance) => RoomRecipeHost.CaptureLoadedSources(__instance); }
/// <summary>The navigation build: the room recipes' navigation first, then the solid-model window
/// (<see cref="ModelBuildWindow.Open"/>); the body runs only when both let it. The postfix and finalizer are the recipes'.</summary>
[HarmonyPatch(typeof(LG_BuildUnityGraphJob), nameof(LG_BuildUnityGraphJob.Build))]
internal static class UnityGraphBuildHooks
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_BuildUnityGraphJob __instance)
    {
        var recipes = RecipeNavigation(__instance);
        var window = ModelBuildWindow.Open(__instance);
        return recipes && window;
    }
    private static bool RecipeNavigation(LG_BuildUnityGraphJob job)
    {
        if (RoomGenerationFailure.Pending) return false;
        try { RoomRecipeHost.Navigation(job); return true; }
        catch (Exception error)
        {
            RoomRecipeDoorSelection.Fail("recipe-navigation-failed: " + error.GetBaseException().Message);
            return false;
        }
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(bool __result)
    { if (__result && !RoomGenerationFailure.Pending && !LG_BuildUnityGraphJob.IgnoreUnityBuildJob) RoomRecipeHost.NavigationDone(); }
    [HarmonyFinalizer]
    internal static Exception? Finalizer(Exception? __exception, ref bool __result)
    {
        if (__exception != null && RoomRecipeHost.HasPlacedRecipes) RoomRecipeDoorSelection.Fail("recipe-navigation-failed: " + __exception.GetBaseException().Message);
        if (RoomGenerationFailure.Pending) { __result = false; return null; }
        return __exception;
    }
}
/// <summary>The native cluster scan keeps the portals it touched in its own job data, which dies with the job, so this postfix copies them
/// out for the area readback. A recipe room whose flood reached some doors but not all also gets its cluster here; see <see cref="RoomRecipeHost.AcceptPartialScan"/>.</summary>
[HarmonyPatch(typeof(AIGraph.AIG_NodeClusterTools.NodeClusterScanData), nameof(AIGraph.AIG_NodeClusterTools.NodeClusterScanData.MatchWithPortals))]
internal static class RoomRecipeClusterScan
{ [HarmonyPostfix, HarmonyPriority(Priority.Last)] internal static void Postfix(AIGraph.AIG_NodeClusterTools.NodeClusterScanData __instance, bool __result) { RoomRecipeSpatialReadback.Scanned(__instance, __result); RoomRecipeHost.AcceptPartialScan(__instance, __result); } }
[HarmonyPatch(typeof(AIGraph.AIG_NodeClusterTools.NodeClusterScanData), nameof(AIGraph.AIG_NodeClusterTools.NodeClusterScanData.GetSourceNode))]
internal static class RoomRecipeClusterSource
{
    [HarmonyPrefix] internal static void Prefix(AIGraph.AIG_NodeClusterTools.NodeClusterScanData __instance) => RoomRecipeSourceClearance.Prepare(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)] internal static void Postfix(AIGraph.AIG_NodeClusterTools.NodeClusterScanData __instance, bool __result) => RoomRecipeSpatialReadback.SourceResolved(__instance, __result);
}

