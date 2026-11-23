using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeMap.Fields;
using ForgeEngine.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;
using CargoQueue = Il2CppSystem.Collections.Generic.List<UnityEngine.Transform>;

namespace ForgeMap.Native;

/// <summary>
/// Preserves native Build and objective generation. For an authored entry cage, captures this landing generation's
/// queue entries by native identity, stages authored roots at the landing, removes only the captured occurrences,
/// and appends authored roots to the same queue. Generated objects keep their native placement and lifetime. Unrelated later
/// entries keep their order and multiplicity; changed provenance is refused rather than guessed.
///
/// MoveObjectiveItemsToCargo remains entirely native, including recall-state updates, last-slot saturation and
/// partial exceptions (h10-cargo-bodies-20403457.json). Its hooks only correlate and report the actual invocation;
/// no per-item substitute, second movement call, suppressed repeat or invented successful result is used.
/// Native callers' cargo-in-use gating and actual landing/recall/multiplayer behavior still need game verification.
/// </summary>
internal static class ElevatorCargoApplier
{
    private static SystemObjectPlan? _plan;
    private static PreparedCargo? _prepared;

    internal sealed class PreparedCargo(ElevatorCargoCage cage, ElevatorCargoRule rule, IntPtr queue, MapLevelReference? level)
    {
        internal readonly ElevatorCargoCage Cage = cage;
        internal readonly ElevatorCargoRule Rule = rule;
        internal readonly IntPtr Queue = queue;
        internal readonly MapLevelReference? Level = level;
        internal readonly List<IntPtr> OriginalIdentities = new();
        internal readonly List<Transform> Roots = new();
        internal bool Ready, Failed;
    }

    /// <summary>Per-invocation observation, never authority to change the native movement.</summary>
    internal sealed class MovementObservation(PreparedCargo prepared)
    {
        internal readonly PreparedCargo Prepared = prepared;
    }

    private readonly record struct Admission(int Index, IntPtr Identity);

    internal static void Activate(SystemObjectPlan plan) { _plan = plan.AnyCargo ? plan : null; _prepared = null; }

    private static ElevatorCargoRule? Current() => _plan?.For(LevelIdentity.Read())?.Cargo;

    private static bool IsEntry(ElevatorCargoCage cage)
    {
        var elevator = ElevatorRide.Cage;
        var entry = elevator == null || elevator.WasCollected ? null : elevator.m_cargoCage;
        return cage != null && !cage.WasCollected && entry != null && !entry.WasCollected && entry.Pointer == cage.Pointer;
    }

    internal static void Clear(ElevatorCargoCage cage)
    {
        if (_prepared == null) return;
        try
        {
            if (_prepared.Cage.Pointer == cage.Pointer) _prepared = null;
        }
        catch (Exception error)
        {
            // A collected previous wrapper invalidates only our receipt, never native Reset/Cleanup.
            _prepared = null;
            Fault(error);
        }
    }

    internal static void Built(bool complete)
    {
        try
        {
            if (!complete || Current() == null) return;
            var cage = EntryCage();
            if (cage == null)
                Refuse("no-cargo-cage", "the entry elevator's cargo cage did not read; native Build was preserved");
            else if (_prepared == null || _prepared.Cage.Pointer != cage.Pointer)
                Refuse("cargo-preparation-missing", "the native landing spawn was not observed; authored cargo was not applied");
        }
        catch (Exception error) { Fault(error); }
    }

    internal static void BuildFailed(Exception? error)
    {
        if (error == null) return;
        try
        {
            if (Current() == null) return;
            if (EntryCage() == null)
                Refuse("no-cargo-cage", "native Build threw before cargo generation; its exception is preserved");
            else Fault(error, "cargo-native-build");
        }
        catch (Exception observationError) { Fault(observationError, "cargo-build-observation"); }
    }

    private static ElevatorCargoCage? EntryCage()
    {
        var elevator = ElevatorRide.Cage;
        var cage = elevator == null || elevator.WasCollected ? null : elevator.m_cargoCage;
        return cage == null || cage.WasCollected ? null : cage;
    }

    internal static void AfterLandingSpawn(ElevatorCargoCage cage)
    {
        PreparedCargo? state = null;
        CargoQueue? queue = null;
        var admitted = new List<Admission>();
        int removed = 0;
        bool filtering = false;
        try
        {
            if (Current() is not { } cargo || !IsEntry(cage)) return;
            queue = cage.m_itemsToMoveToCargo;
            if (queue == null)
            {
                Refuse("cargo-queue-missing", "the native landing spawn did not initialize its queue");
                return;
            }
            state = _prepared = new PreparedCargo(cage, cargo, queue.Pointer, LevelIdentity.Read());
            // The decoded landing method creates this queue and populates it before this postfix. Capture exact
            // occurrences before authored spawning can change anything. Null/collected entries are not identities.
            for (int i = 0; i < queue.Count; i++) state.OriginalIdentities.Add(Identity(queue[i]));
            if (!cargo.Disable)
            {
                var slots = cage.m_placementSlots;
                if (slots == null || slots.Length == 0)
                {
                    state.Failed = true;
                    Refuse("no-free-slot", "the entry cage has no usable slot array; native cargo entries were retained");
                    return;
                }
                // More items than slots is legal: the native whole loop saturates at its final slot.
                var landing = ElevatorShaftLanding.CargoAlign;
                var node = Builder.GetElevatorArea()?.m_courseNode;
                if (landing == null || node == null)
                {
                    state.Failed = true;
                    Refuse("cargo-origin-missing", "the native landing align or elevator area's course node is missing");
                    return;
                }
                foreach (uint item in cargo.Items)
                    if (GameDataBlockBase<ItemDataBlock>.GetBlock(item) == null)
                    {
                        state.Failed = true;
                        Refuse("item-block-missing", "item=" + item.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                for (int i = 0; i < cargo.Items.Count; i++)
                {
                    uint item = cargo.Items[i];
                    var block = GameDataBlockBase<ItemDataBlock>.GetBlock(item)
                        ?? throw new InvalidOperationException("The authored item block disappeared after cargo validation.");
                    var align = new GameObject("ForgeElevatorCargo-" + i).transform;
                    align.SetParent(landing, false);
                    align.SetPositionAndRotation(landing.position, landing.rotation);
                    uint seed = FixedPlacement.SeedOf("elevator-cargo:" + i.ToString(CultureInfo.InvariantCulture));
                    double amount = (int)block.inventorySlot == FixedPlacement.ResourcePackSlot ? 1d
                        : (int)block.inventorySlot is 5 or 6 ? (double)block.ConsumableAmmoMax : 0d;
                    if (!PlacedItemSpawn.TrySpawn(item, align, node, amount, seed, out _, out _, out var cause))
                    {
                        state.Failed = true;
                        // Spawning can have native effects even on refusal. Keep its root at the landing; don't
                        // destroy it or imply those effects rolled back. No queue writes have happened yet.
                        Refuse(cause, "item=" + item.ToString(CultureInfo.InvariantCulture) + " placed=" + state.Roots.Count
                            + "/" + cargo.Items.Count + "; staged roots retained at landing; native cargo entries retained");
                        return;
                    }
                    state.Roots.Add(align);
                }
            }
            if (!Matches(state, cage) || !OriginalEntriesMatch(queue, state))
            {
                state.Failed = true;
                Refuse("cargo-generation-changed", "scope, queue or captured entry identities changed during staging; no queue policy writes were made");
                return;
            }
            // Remove only proved occurrences, in reverse index order. Later entries (even repeated identities)
            // are outside this capture. Never Clear, swap the queue, or guess new positions after a mismatch.
            filtering = true;
            for (int i = state.OriginalIdentities.Count - 1; i >= 0; i--)
            {
                if (!Matches(state, cage) || Identity(queue[i]) != state.OriginalIdentities[i])
                    throw new InvalidOperationException("The captured cargo occurrence changed during selective filtering.");
                queue.RemoveAt(i);
                removed++;
            }
            filtering = false;
            foreach (var root in state.Roots)
            {
                if (!Matches(state, cage))
                    throw new InvalidOperationException("The cargo scope or queue changed during authored admission.");
                int index = queue.Count;
                IntPtr identity = Identity(root);
                try { queue.Add(root); }
                finally
                {
                    // Add can throw after a write. Only an exact index/identity readback is a withdrawal receipt.
                    if (queue.Count > index && Identity(queue[index]) == identity) admitted.Add(new Admission(index, identity));
                }
                if (admitted.Count == 0 || admitted[admitted.Count - 1].Index != index)
                    throw new InvalidOperationException("The authored cargo admission identity could not be confirmed.");
            }
            if (!Matches(state, cage) || admitted.Count != state.Roots.Count || !AdmissionsMatch(queue, admitted))
                throw new InvalidOperationException("The cargo generation or admitted identities changed after authored admission.");
            state.Ready = true;
            Requested(state, "cargo-queue-prepared");
        }
        catch (Exception error)
        {
            if (state != null)
            {
                state.Failed = true;
                if (queue != null) WithdrawAdmissions(state, queue, admitted);
                if (filtering || removed != 0)
                    Refuse(filtering ? "selective-filter-partial" : "cargo-admission-partial", "removed-receipts=" + removed
                        + "/" + state.OriginalIdentities.Count + " queue-outcome=unverified; surviving entries and landing objects retained");
            }
            Fault(error, "cargo-queue-preparation");
        }
    }

    private static IntPtr Identity(Transform root)
    {
        if (root == null || root.WasCollected) throw new InvalidOperationException("A cargo root has no readable native identity.");
        return root.Pointer;
    }

    private static bool OriginalEntriesMatch(CargoQueue queue, PreparedCargo state)
    {
        if (queue.Count < state.OriginalIdentities.Count) return false;
        for (int i = 0; i < state.OriginalIdentities.Count; i++)
            if (Identity(queue[i]) != state.OriginalIdentities[i]) return false;
        return true;
    }

    private static bool AdmissionsMatch(CargoQueue queue, List<Admission> admitted)
    {
        foreach (var receipt in admitted)
            if (queue.Count <= receipt.Index || Identity(queue[receipt.Index]) != receipt.Identity) return false;
        return true;
    }

    private static bool Matches(PreparedCargo state, ElevatorCargoCage cage)
        => ReferenceEquals(_prepared, state) && ReferenceEquals(Current(), state.Rule)
            && Equals(LevelIdentity.Read(), state.Level) && IsEntry(cage) && state.Cage.Pointer == cage.Pointer
            && cage.m_itemsToMoveToCargo != null && cage.m_itemsToMoveToCargo.Pointer == state.Queue;

    private static void WithdrawAdmissions(PreparedCargo state, CargoQueue queue, List<Admission> admitted)
    {
        for (int i = admitted.Count - 1; i >= 0; i--)
        {
            try
            {
                var receipt = admitted[i];
                if (!Matches(state, state.Cage) || state.Cage.m_itemsToMoveToCargo == null || state.Cage.m_itemsToMoveToCargo.Pointer != state.Queue
                    || queue.Pointer != state.Queue || queue.Count <= receipt.Index || Identity(queue[receipt.Index]) != receipt.Identity)
                {
                    Refuse("cargo-withdrawal-unverified", "authored admission identity changed; queue-outcome=unverified; no guessed removal");
                    return;
                }
                queue.RemoveAt(receipt.Index);
            }
            catch (Exception error)
            {
                Refuse("cargo-withdrawal-unverified", "queue-outcome=unverified; authored admission withdrawal was interrupted");
                Fault(error, "cargo-admission-withdrawal");
                return;
            }
        }
        // Selectively removed original entries are not recreated from a stale snapshot. Native movement sees
        // the real surviving queue, and objects already spawned remain at the landing until native code moves them.
    }

    internal static MovementObservation? ObserveMovement(ElevatorCargoCage cage)
    {
        try
        {
            if (Current() == null || !IsEntry(cage)) return null;
            var state = _prepared;
            if (state == null || !Matches(state, cage))
            {
                Refuse("cargo-preparation-missing", "the native cargo queue changed or authored preparation was not observed; native movement is preserved");
                return null;
            }
            return new MovementObservation(state);
        }
        catch (Exception error) { Fault(error, "cargo-movement-observation"); return null; }
    }

    internal static void MovementFinished(ElevatorCargoCage cage, bool ranOriginal, Exception? error, MovementObservation? observation)
    {
        if (observation == null) return;
        try
        {
            var state = observation.Prepared;
            if (error != null)
            {
                Fault(error, "cargo-native-movement");
                return;
            }
            if (!ranOriginal) return;
            if (!Matches(state, cage))
            {
                Refuse("cargo-observation-stale", "generation changed during native movement; movement-outcome=unverified");
                return;
            }
            if (state.Ready && !state.Failed) Requested(state, "cargo-native-loop-returned");
        }
        catch (Exception observationError) { Fault(observationError, "cargo-movement-observation"); }
    }

    private static void Requested(PreparedCargo state, string stage)
    {
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplyRequested, "block=" + MapFieldSchema.ElevatorBlock
            + " level=" + state.Level + " DisableElevatorCargo=" + (state.Rule.Disable ? "true" : "false")
            + " enqueued=" + state.Roots.Count + "/" + state.Rule.Items.Count + " stage=" + stage
            + " movement-outcome=unverified recall-outcome=unverified",
            RuntimeLogValue.Of("block", MapFieldSchema.ElevatorBlock), RuntimeLogValue.Of("object", "elevator-cargo"));
    }

    private static void Refuse(string code, string detail) =>
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyRefused, "group=elevator-cargo object=elevator-cargo code=" + code
            + ": " + detail, RuntimeLogValue.Of("group", "elevator-cargo"), RuntimeLogValue.Of("code", code));

    private static void Fault(Exception error, string stage = "cargo-observation") =>
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed, "group=elevator-cargo stage=" + stage + " error="
            + RuntimeDiagnostics.Describe(error),
            RuntimeLogValue.Of("group", "elevator-cargo"), RuntimeLogValue.Of("exception", error.GetType().Name));
}

/// <summary>The cargo job's build: after a body that returned the rows apply; a body that threw is reported.</summary>
[HarmonyPatch(typeof(LG_SpawnItemsInCargoCageJob), nameof(LG_SpawnItemsInCargoCageJob.Build))]
internal static class ElevatorCargoFields
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(bool __result) => ElevatorCargoApplier.Built(__result);

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Finalizer(Exception? __exception) => ElevatorCargoApplier.BuildFailed(__exception);
}

[HarmonyPatch(typeof(ElevatorCargoCage), nameof(ElevatorCargoCage.SpawnObjectiveItemsInLandingArea))]
internal static class ElevatorCargoLandingFields
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ElevatorCargoCage __instance) => ElevatorCargoApplier.Clear(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ElevatorCargoCage __instance, bool __runOriginal)
    {
        if (__runOriginal) ElevatorCargoApplier.AfterLandingSpawn(__instance);
    }
}

[HarmonyPatch(typeof(ElevatorCargoCage), nameof(ElevatorCargoCage.MoveObjectiveItemsToCargo))]
internal static class ElevatorCargoMovementFields
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ElevatorCargoCage __instance, out ElevatorCargoApplier.MovementObservation? __state)
        => __state = ElevatorCargoApplier.ObserveMovement(__instance);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Finalizer(ElevatorCargoCage __instance, bool __runOriginal, Exception? __exception, ElevatorCargoApplier.MovementObservation? __state)
        => ElevatorCargoApplier.MovementFinished(__instance, __runOriginal, __exception, __state);
}

[HarmonyPatch(typeof(ElevatorCargoCage), nameof(ElevatorCargoCage.Reset))]
internal static class ElevatorCargoResetFields
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ElevatorCargoCage __instance) => ElevatorCargoApplier.Clear(__instance);
}

[HarmonyPatch(typeof(ElevatorCargoCage), nameof(ElevatorCargoCage.Cleanup))]
internal static class ElevatorCargoCleanupFields
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ElevatorCargoCage __instance) => ElevatorCargoApplier.Clear(__instance);
}

/// <summary>Finish the native End interpolation before vanilla starts releasing the still-seated players.</summary>
[HarmonyPatch(typeof(ElevatorRide._PlayerExit_d__119), nameof(ElevatorRide._PlayerExit_d__119.MoveNext))]
internal static class ElevatorCageEndAlignment
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(ElevatorRide._PlayerExit_d__119 __instance)
    {
        try
        {
            // The iterator factories are inlined in IL2CPP. Patch its actual MoveNext body, once at
            // initial state 0, before TrySetFirstNodeToAllPlayers, cargo movement or any player exit.
            if (__instance.__1__state != 0) return;
            var ride = __instance.__4__this;
            if (ride == null || ride.WasCollected)
            { Refused("ride-unavailable"); return; }
            if (ride.CurrentState != ElevatorRideState.PlayerExit) return;
            var cage = ride.m_elevatorCage;
            var end = ride.m_elevatorCageEndRef;
            if (cage == null || cage.WasCollected || end == null || end.WasCollected)
            { Refused("native-end-align-unavailable"); return; }
            var body = cage.transform;
            var before = body.position;
            // Build 20403457, ElevatorEnd.MoveNext 0x167ee70–0x167efcb: X/Z use the progress BEFORE
            // the last vertical step. If that step consumes all remaining height, the next iteration
            // skips position interpolation and enters PlayerExit with the old X/Z (even at the origin).
            // Commit that same native target once at End -> PlayerExit, before its original body runs.
            // CargoAlign is for the detached cargo child; it is not the cage's animation endpoint.
            body.position = end.position;
            EntryLandingApplier.CommitFacing(ride);
            ElevatorLandingObservation.EndCommitted(ride, before);
        }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ElevatorLandingInvalid,
                "Native cage endpoint commit failed before player release.",
                RuntimeLogValue.Of("phase", "native-end-commit"), RuntimeLogValue.Of("reason", "end-commit-failed"),
                RuntimeLogValue.Quote("exception", RuntimeDiagnostics.Describe(error.GetBaseException())));
        }
    }

    private static void Refused(string reason) =>
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ElevatorLandingInvalid,
            "Native cage endpoint could not be resolved before player release.",
            RuntimeLogValue.Of("phase", "native-end-commit"), RuntimeLogValue.Of("reason", reason));
}
