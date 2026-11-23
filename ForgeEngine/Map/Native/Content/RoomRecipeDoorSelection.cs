using System;
using System.Collections.Generic;
using Expedition;
using ForgeEngine.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;
using PlugPools = Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.GameObject>>;

namespace ForgeMap.Native;

/// <summary>Constrain native resource requests; retain native pairing, height selection, divider and door jobs.
/// Context exists only for the synchronous Build call, and is restored even when native construction throws.</summary>
internal static class RoomRecipeDoorSelection
{
    internal sealed record Selection(RoomBuildRecipe Recipe, RoomBuildDoor Door)
    {
        internal string Family = "";
        internal bool WithGate;
        internal SubComplex RequestedSub, SelectedSub;
    }
    [ThreadStatic] internal static Selection? Current;

    /// <summary>The end of one plug or gate build, from its finalizer: a throw while a recipe door was selected fails
    /// the room build, the selection the build started from comes back, and a build the room generation already
    /// failed answers false instead of throwing on.</summary>
    internal static Exception? Leave(Exception? exception, ref bool result, Selection? state)
    {
        if (exception != null && Current != null && !RoomGenerationFailure.Pending)
            Fail("recipe-door-build-failed: " + exception.GetBaseException().Message);
        Current = state;
        if (exception != null && RoomGenerationFailure.Pending) { result = false; return null; }
        return exception;
    }

    internal static Selection? Enter(LG_Plug plug)
    {
        Selection? Socket(LG_Plug socket)
        {
            var identity = RoomRecipeDoorIdentity.Resolve(socket);
            return identity is { } row ? new(row.Recipe, row.Door) : null;
        }
        var own = Socket(plug);
        var other = plug.m_pariedWith == null ? null : Socket(plug.m_pariedWith);
        if (own != null && other != null && own.Door.Size != other.Door.Size) throw Fail("recipe-door-size-mismatch");
        // Native BuildPlug requests the Transition pool (8) when the two sockets' SubComplex differ. Its plugs raise the
        // gate 1 m on a stair platform whose stairs and plinth sit in the half that a recipe room clears, so a recipe
        // room joined to a native room takes that room's SubComplex and gets an ordinary flat connector of that pool.
        // Two recipe sockets may carry different SubComplexes (a converted room keeps its plugs'), which would ask the same Transition
        // pool, so the pair takes this socket's SubComplex and gets the same ordinary flat connector.
        if (plug.m_pariedWith != null && (own == null) != (other == null))
        {
            var recipeSocket = own != null ? plug : plug.m_pariedWith;
            var nativeSocket = own != null ? plug.m_pariedWith : plug;
            recipeSocket.m_subComplex = nativeSocket.m_subComplex;
        }
        else if (plug.m_pariedWith != null && own != null && other != null)
            plug.m_pariedWith.m_subComplex = plug.m_subComplex;
        return plug.m_pariedWith == null ? null : own ?? other;
    }
    internal static Selection? Enter(LG_Gate? gate)
    {
        if (gate == null) return null;
        var identity = RoomRecipeDoorIdentity.Resolve(gate);
        return identity is { } row ? new(row.Recipe, row.Door) : null;
    }
    private static bool IsPhysicalDoor(Selection selection) => selection.Door.Kind == "door";
    internal static LG_GateType GateType(string size) => size switch
    {
        "4x4" => LG_GateType.Small,
        "8x4" => LG_GateType.Medium,
        _ => throw Fail("recipe-door-size-invalid")
    };
    internal static bool Plug(ComplexResourceSetDataBlock set, bool withGate, uint seed, SubComplex sub,
        string family, ref GameObject result)
    {
        if (CompiledMapGenerator.ConnectorOverride(family, ref result)) return false;
        if (Current == null) return true;
        if (!IsPhysicalDoor(Current)) throw Fail("recipe-free-passage-plug-selector");
        var gated = family switch { "straight" => set.m_straightPlugsWithGates, "single" => set.m_singleDropPlugsWithGates, _ => set.m_doubleDropPlugsWithGates };
        var open = withGate ? null : family switch { "straight" => set.m_straightPlugsNoGates, "single" => set.m_singleDropPlugsNoGates, _ => set.m_doubleDropPlugsNoGates };
        result = Select(set, gated, open, sub, seed, Current.Door.Size, out var selectedSub);
        Current.Family = family; Current.WithGate = withGate;
        Current.RequestedSub = sub; Current.SelectedSub = selectedSub;
        return false;
    }
    internal static void Spawned(GameObject prefab, GameObject spawned)
    {
        if (Current == null) return;
        if (!IsPhysicalDoor(Current)) throw Fail("recipe-free-passage-plug-spawn");
        if (!Matches(prefab, Current.Door.Size) || !Matches(spawned, Current.Door.Size))
            throw Fail("recipe-plug-size-mismatch");
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.PlugSelected, "Native plug spawned for the authored doorway size.",
                RuntimeLogValue.Of("resource", Current.Recipe.ResourceKey), RuntimeLogValue.Of("door", Current.Door.Name),
                RuntimeLogValue.Of("size", Current.Door.Size), RuntimeLogValue.Of("prefab", prefab.name),
                RuntimeLogValue.Of("family", Current.Family), RuntimeLogValue.Of("withGate", Current.WithGate),
                RuntimeLogValue.Of("subComplex", (int)Current.RequestedSub), RuntimeLogValue.Of("selectedSubComplex", (int)Current.SelectedSub));
    }
    private static bool Matches(GameObject prefab, string size)
    {
        var wanted = GateType(size);
        var gates = prefab.GetComponentsInChildren<LG_Gate>(true);
        var width = size == "4x4" ? 4 : 8;
        for (int index = 0; index < gates.Length; index++)
        {
            var gate = gates[index];
            var measured = Measure(gate);
            if (gate.Type != wanted || measured.x != width || measured.y != 4) return false;
        }
        return gates.Length != 0;
    }
    /// <summary>LG_Gate.GetBoundingSize goes through GetAlignRef, which a free-passage gate of an open connector
    /// prefab answers with a null reference until it is instantiated (NullReferenceException inside the SpawnPlug
    /// and GetPlugStraight trampolines). The native size of every gate type is fixed by its Type
    /// (catalog/native/room-connector-coverage.json gateDimensions), so a free-passage gate is measured from that.</summary>
    private static Vector3 Measure(LG_Gate gate) => gate.TryCast<LG_FreePassageGate>() != null
        ? gate.Type switch { LG_GateType.Small => new Vector3(4, 4, 1), LG_GateType.Medium => new Vector3(8, 4, 1), _ => new Vector3(8, 8, 1) }
        : gate.GetBoundingSize();
    /// <summary>An unpinned connection's connector when a Forge doorway takes part, or null when none does. The family pools mix opening
    /// sizes (a single-drop pool holds 4x4 and 8x4 connectors), and a recipe doorway only accepts a gate of its own size.</summary>
    internal static GameObject? SelectForDoor(LG_Plug a, LG_Plug b, ComplexResourceSetDataBlock set, PlugPools? gated, PlugPools? open, SubComplex sub, uint seed)
    {
        if ((RoomRecipeDoorIdentity.Resolve(a) ?? RoomRecipeDoorIdentity.Resolve(b)) is not { } row || row.Door.Kind != "door") return null;
        var outer = Current;
        Current = new(row.Recipe, row.Door);
        try { return Select(set, gated, open, sub, seed, row.Door.Size, out _); }
        finally { Current = outer; }
    }
    internal static GameObject Select(ComplexResourceSetDataBlock set, PlugPools? gated, PlugPools? open, SubComplex sub, uint seed, string size, out SubComplex selectedSub)
    {
        _ = GateType(size);
        List<GameObject> Candidates(SubComplex slot)
        {
            var found = new List<GameObject>();
            void Add(PlugPools? pools)
            {
                if (pools == null || (int)slot < 0 || (int)slot >= pools.Length || pools[(int)slot] == null) return;
                foreach (var prefab in pools[(int)slot])
                {
                    if (prefab == null) continue;
                    // A name is not size evidence. Measure the actual native gate, including open connectors.
                    if (!Matches(prefab, size)) continue;
                    found.Add(prefab);
                }
            }
            Add(gated);
            Add(open);
            return found;
        }
        selectedSub = sub;
        var candidates = Candidates(sub);
        if (candidates.Count == 0 && sub != SubComplex.All)
        {
            selectedSub = SubComplex.All;
            candidates = Candidates(selectedSub);
        }
        if (candidates.Count == 0) throw Fail("recipe-plug-size-unavailable: " + size);
        var selected = new PlugPools(Math.Max((int)selectedSub, (int)SubComplex.All) + 1);
        // The candidate array is held only by this one: stored with the collector's write barrier for the game's pick below.
        ForgeCore.Il2CppArrays.Store(selected, (int)selectedSub, ForgeCore.Il2CppArrays.ToGameReferences(candidates.ToArray()));
        return set.GetRandomPrefab(seed, selected, selectedSub);
    }
    /// <summary>A physical door recipe doorway must get a gate of its door size. An exit joined through a door-less connector gets that
    /// connector's free-passage gate, whose Type is FreePassage rather than a size, and no door to size; <see cref="GateBuilt"/> checks it.</summary>
    internal static void CheckGate(LG_Gate gate)
    {
        if (Current == null || !IsPhysicalDoor(Current) || OpenExit(Current, gate)) return;
        if (gate.Type != GateType(Current.Door.Size) && !NarrowInWide(gate.Type, Current.Door.Size)) throw Fail("recipe-gate-size-mismatch");
    }
    /// <summary>A 4x4 gate in an 8x4 opening, accepted only for a connector the plan itself pinned (the 12 m drop connectors exist only with a 4x4 door).
    /// The native random pick (<see cref="Select"/>) and the spawn check stay strict: an unpinned connector never narrows an opening.</summary>
    private static bool NarrowInWide(LG_GateType gate, string size) => gate == LG_GateType.Small && size == "8x4";
    private static bool OpenExit(Selection selection, LG_Gate gate) => selection.Door.ToArea == null && gate.TryCast<LG_FreePassageGate>() != null;
    /// <summary>Runs after a native gate build returned true. The five Get*Gate selectors cannot carry a Forge declaration
    /// (their entries start with a conditional jump that the runtime refuses to relocate), so the doorway is checked here instead:
    /// a physical door recipe doorway must end with a spawned door, and a free-passage doorway with none. An exit doorway joins the
    /// level through whatever connector the map's connection asks for; a door-less connector carries a free-passage gate and, like a
    /// free-passage doorway, must end open with no door.</summary>
    internal static void GateBuilt(LG_Gate gate)
    {
        var selection = Current;
        if (selection == null) return;
        var openExit = OpenExit(selection, gate);
        if (!IsPhysicalDoor(selection) || openExit)
        {
            if ((selection.Door.ToArea != null || openExit) && (gate.SpawnedDoor != null || !gate.m_isTraversableFromStart))
                throw Fail("recipe-free-passage-native-state");
            return;
        }
        var spawned = gate.SpawnedDoor;
        if (spawned == null) throw Fail("recipe-gate-door-not-spawned: " + selection.Door.Size);
        ApplyDoorKind(selection, spawned.TryCast<LG_WeakDoor>());
        if (MapLog.Logs(RuntimeLogLevel.Info))
        {
            var component = spawned.TryCast<Component>();
            var security = spawned.TryCast<LG_SecurityDoor>();
            var factory = security != null ? "security-" + security.m_securityDoorType : spawned.TryCast<LG_WeakDoor>() != null ? "weak" : "unresolved";
            MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.GateSelected, "Native gate build spawned a door for the authored doorway size.",
                RuntimeLogValue.Of("resource", selection.Recipe.ResourceKey), RuntimeLogValue.Of("door", selection.Door.Name),
                RuntimeLogValue.Of("size", selection.Door.Size), RuntimeLogValue.Of("gateType", (int)gate.Type),
                RuntimeLogValue.Of("prefab", component?.gameObject.name ?? "unresolved"),
                RuntimeLogValue.Of("factory", factory), RuntimeLogValue.Of("doorKind", selection.Door.DoorKind ?? "native-roll"));
        }
    }
    /// <summary>The author's door for a recipe interior doorway, or null for the level's own door-balance roll.</summary>
    internal static string? AuthoredDoorKind(LG_Gate? gate)
        => gate == null || gate.WasCollected ? null : RoomRecipeDoorIdentity.Resolve(gate) is { } row ? row.Door.DoorKind : null;
    /// <summary>The lock type an authored interior door kind names (`weak-melee`, `weak-hack`), or null when the kind names none.</summary>
    internal static eWeakLockType? AuthoredLockType(string? kind) => kind switch
    {
        "weak-melee" => eWeakLockType.Melee,
        "weak-hack" => eWeakLockType.Hackable,
        _ => null
    };
    /// <summary>An authored interior door after its native build. Doors always start closed. `weak`
    /// keeps the native lock setup from running (<see cref="WeakLockSetupHooks"/>). `weak-locked` keeps a
    /// lock the native roll gave it; when the roll gave none, the lock is created through the same native entry with the melee
    /// lock. `weak-melee` and `weak-hack` get that lock type: a rolled lock was already built with it (<see cref="WeakLockSetupHooks"/>),
    /// and when the roll gave none it is created through the same native entry. A door that ends in another state ends generation
    /// instead of loading a room that differs from its document.</summary>
    private static void ApplyDoorKind(Selection selection, LG_WeakDoor? weak)
    {
        var kind = selection.Door.DoorKind;
        if (kind == null) return;
        if (weak == null) throw Fail("recipe-door-kind-not-weak-door: " + kind);
        var wantLock = kind != "weak";
        var type = AuthoredLockType(kind);
        var locked = weak.WeakLocks != null && weak.WeakLocks.Length != 0;
        if (wantLock && !locked)
        {
            weak.SetupWeakLock(type ?? eWeakLockType.Melee);
            locked = weak.WeakLocks != null && weak.WeakLocks.Length != 0;
        }
        if (locked != wantLock) throw Fail("recipe-door-kind-lock-state: " + kind);
        if (type is { } wanted)
            foreach (var weakLock in weak.WeakLocks!)
                if (weakLock == null || weakLock.m_lockType != wanted) throw Fail("recipe-door-kind-lock-type: " + kind);
    }
    internal static InvalidOperationException Fail(string reason)
    {
        if (RoomGenerationFailure.Request() && MapLog.Logs(RuntimeLogLevel.Error)) MapLog.Record(RuntimeLogLevel.Error, RoomRecipeCodes.Refused,
            "Doorway construction refused; generation is ending.", RuntimeLogValue.Quote("reason", reason),
            RuntimeLogValue.Of("resource", Current?.Recipe.ResourceKey ?? "unresolved"),
            RuntimeLogValue.Of("door", Current?.Door.Name ?? "unresolved"), RuntimeLogValue.Of("outcome", "end-playing"));
        return new InvalidOperationException(reason);
    }
}
[HarmonyPatch(typeof(LG_BuildPlugJob), nameof(LG_BuildPlugJob.Build))]
internal static class RoomRecipePlugBuild
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_BuildPlugJob __instance, ref bool __result, out RoomRecipeDoorSelection.Selection? __state)
    {
        __state = RoomRecipeDoorSelection.Current;
        if (RoomGenerationFailure.Pending) { RoomRecipeDoorSelection.Current = null; return false; }
        RoomRecipeDoorSelection.Current = RoomRecipeDoorSelection.Enter(__instance.m_plug);
        return CompiledMapGenerator.PlugBuildStarting(__instance.m_plug, ref __result);
    }
    [HarmonyFinalizer]
    internal static Exception? Finalizer(Exception? __exception, ref bool __result, RoomRecipeDoorSelection.Selection? __state)
    {
        if (CompiledMapGenerator.PlugBuildFinished(__exception))
        {
            RoomRecipeDoorSelection.Current = __state;
            __result = false;
            return null;
        }
        return RoomRecipeDoorSelection.Leave(__exception, ref __result, __state);
    }
}
[HarmonyPatch(typeof(LG_BuildPlugBaseJob), nameof(LG_BuildPlugBaseJob.SpawnPlug))]
internal static class RoomRecipePlugSpawn
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(GameObject prefab, GameObject __result)
    {
        if (!CompiledMapGenerator.PlugSpawned(__result)) RoomRecipeDoorSelection.Spawned(prefab, __result);
    }
}
/// <summary>The gate build: the room recipes' door selection first, then the entrance-gate fields the job builds its
/// door from (<see cref="DoorBuildApplier.ApplyGate"/>); after the body the recipe's door is checked, then the
/// fields' door is initialized (<see cref="DoorBuildApplier.BuiltGate"/>). The finalizer is the recipes'.</summary>
[HarmonyPatch(typeof(LG_BuildGateJob), nameof(LG_BuildGateJob.Build))]
internal static class GateBuildHooks
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_BuildGateJob __instance, out RoomRecipeDoorSelection.Selection? __state)
    {
        var run = RecipeGate(__instance, out __state);
        DoorBuildApplier.ApplyGate(__instance);
        return run;
    }
    private static bool RecipeGate(LG_BuildGateJob job, out RoomRecipeDoorSelection.Selection? state)
    {
        state = RoomRecipeDoorSelection.Current;
        if (RoomGenerationFailure.Pending) { RoomRecipeDoorSelection.Current = null; return false; }
        RoomRecipeDoorSelection.Current = job.m_plug == null ? RoomRecipeDoorSelection.Enter(job.m_gate) : RoomRecipeDoorSelection.Enter(job.m_plug);
        if (RoomRecipeDoorSelection.Current != null)
        {
            var gate = job.m_gate ?? throw RoomRecipeDoorSelection.Fail("recipe-gate-missing");
            var kind = RoomRecipeDoorSelection.Current.Door.Kind;
            if (kind == "door" && RoomRecipeDoorSelection.Current.Door.ToArea != null && gate.TryCast<LG_InternalGate>() == null)
                throw RoomRecipeDoorSelection.Fail("recipe-interior-door-kind-mismatch");
            RoomRecipeDoorSelection.CheckGate(gate);
            if (RoomRecipeDoorSelection.Current.Door.ToArea != null
                && (gate.ExpanderStatus != LG_ZoneExpanderStatus.Connected || gate.m_isZoneSource || gate.HasGateWallRemover))
                throw RoomRecipeDoorSelection.Fail("recipe-interior-door-not-connected");
        }
        return true;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_BuildGateJob __instance, bool __result)
    {
        RecipeGateBuilt(__instance, __result);
        if (__result) DoorBuildApplier.BuiltGate(__instance);
    }
    private static void RecipeGateBuilt(LG_BuildGateJob job, bool built)
    {
        if (!built || RoomRecipeDoorSelection.Current == null) return;
        RoomRecipeDoorSelection.GateBuilt(job.m_gate ?? throw RoomRecipeDoorSelection.Fail("recipe-gate-missing"));
    }
    [HarmonyFinalizer]
    internal static Exception? Finalizer(Exception? __exception, ref bool __result, RoomRecipeDoorSelection.Selection? __state)
        => RoomRecipeDoorSelection.Leave(__exception, ref __result, __state);
}
/// <summary>The weak-lock setup. An interior doorway the author made a plain weak door gets no lock: the native lock
/// setup does not run for it, the same as the native no-lock roll, which never calls it. Then the authored weak-lock
/// type replaces the argument the game is about to build the lock from (<see cref="DoorBuildApplier.WeakLockFor"/>); a room door
/// the author gave a lock type (`weak-melee`, `weak-hack`) keeps its own over the zone's.</summary>
[HarmonyPatch(typeof(LG_WeakDoor), nameof(LG_WeakDoor.SetupWeakLock))]
internal static class WeakLockSetupHooks
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_WeakDoor __instance, ref eWeakLockType type)
    {
        var kind = RoomRecipeDoorSelection.AuthoredDoorKind(__instance.Gate);
        if (DoorBuildApplier.WeakLockFor(__instance) is { } authored) type = authored;
        if (RoomRecipeDoorSelection.AuthoredLockType(kind) is { } own) type = own;
        return kind is not "weak";
    }
}
[HarmonyPatch(typeof(ComplexResourceSetDataBlock), nameof(ComplexResourceSetDataBlock.GetPlugStraight))]
internal static class RoomRecipePlugStraight
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(ComplexResourceSetDataBlock __instance, bool forceGate, uint seed, SubComplex subcomplex, ref GameObject __result)
        => RoomRecipeDoorSelection.Plug(__instance, forceGate, seed, subcomplex, "straight", ref __result);
}
[HarmonyPatch(typeof(ComplexResourceSetDataBlock), nameof(ComplexResourceSetDataBlock.GetPlugSingleDrop))]
internal static class RoomRecipePlugSingleDrop
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(ComplexResourceSetDataBlock __instance, bool forceGate, uint seed, SubComplex subcomplex, ref GameObject __result)
        => RoomRecipeDoorSelection.Plug(__instance, forceGate, seed, subcomplex, "single", ref __result);
}
[HarmonyPatch(typeof(ComplexResourceSetDataBlock), nameof(ComplexResourceSetDataBlock.GetPlugDoubleDrop))]
internal static class RoomRecipePlugDoubleDrop
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(ComplexResourceSetDataBlock __instance, bool forceGate, uint seed, SubComplex subcomplex, ref GameObject __result)
        => RoomRecipeDoorSelection.Plug(__instance, forceGate, seed, subcomplex, "double", ref __result);
}
[HarmonyPatch(typeof(ComplexResourceSetDataBlock), nameof(ComplexResourceSetDataBlock.GetPlugCap))]
internal static class CompiledPlugCap
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(ref GameObject __result)
        => !CompiledMapGenerator.CapOverride(ref __result);
}
[HarmonyPatch(typeof(LG_Floor), nameof(LG_Floor.PrepZone))]
internal static class CompiledLoopStatus
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)] internal static void Postfix() => CompiledMapGenerator.ZonePrepared();
}
