using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the three world-event rows: the one native entry the condition action reaches, and the
/// two trigger components a level's world event objects carry.
///
/// <b>The action.</b> `eWardenObjectiveEventType.SetWorldEventCondition` (19) through
/// `WorldEventManager.ExecuteEvent(WardenObjectiveEventData)` is the same entry every vanilla mount point uses: the
/// struct's `Condition` member is a `WorldEventConditionPair` — `ConditionIndex` plus `IsTrue` — and the executor
/// carries it to every client through the manager's own state replicator. The condition slots are the machine's own
/// (`pWorldEventManagerState.WorldEventConditions`, `WORLD_EVENT_CONDITION_COUNT` = 16), so an index outside that
/// range is refused before the call rather than truncated into a slot the author did not name. Nothing here writes
/// the condition byte array: `WorldEventManager.SetCondition` exists, but it writes one machine's copy outside the
/// replicator, which is exactly the write this layer must not make.
///
/// <b>The triggers.</b> The two components are `LG_InteractWorldEventTrigger` (an `Interact_Timed`) and
/// `LG_LookatWorldEventTrigger` (an `ICameraRayInteractor`). Both carry a `WorldEventTrigger_Sync`, and both
/// expose the game's own activation entries: `Trigger(SNet_Player, Item)` for the press and `ResetTrigger` for the
/// reusable reset. The patches below read those two entries and their results — a `Trigger` that returned false
/// did not activate anything and publishes nothing — so the fact is the activation the game performed. Both
/// patches are postfixes on non-virtual bodies `SNet_Replication`'s master side calls, which is why the host guard
/// inside is the whole authority rule: a client applies replicated state and must not publish an activation it did
/// not decide.
///
/// A trigger's own identity is its `LG_WorldEventObject.WorldEventObjectKey`, which is the same text a
/// `WardenObjectiveEventData` names its target with (`WorldEventObjectFilter`) — so a plan wires the key the
/// action's own authored filter spells, and the zone and position travel beside it for the filters a key cannot
/// answer.
///
/// The four patches below are listed in `MapNativeHooks.Types`; Runtime installs them once for the process, and a
/// callback does nothing until the session attaches this half through <see cref="Attach"/>.</summary>
internal sealed class WorldEventFacts
{
    /// <summary>The one attached half. A native callback is static, so the session hands its kernel and
    /// registration to the half through this slot before the first plan runs; a callback that fires before the
    /// attach publishes nothing rather than throwing through a patch.</summary>
    internal static WorldEventFacts? Current { get; private set; }

    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly Row _interact, _lookat;
    private readonly Func<bool> _authority;
    /// <summary>The current observed moment of each native component and fact. Repeated readbacks are silent;
    /// a reset permits the next activation.</summary>
    private readonly Dictionary<(IntPtr Component, string Fact), string> _states = new();
    private readonly OnceReporter _once;
    private readonly ForgeEngine.GameBindings.WorldFault _fault = new();
    private bool _disposed;
    private long _transition;
    /// <summary>The world <see cref="_states"/> was filled in: component pointers are reused by the next level.</summary>
    private long _statesWorld = -1;

    private WorldEventFacts(IForgeEngine kernel, RuntimeModuleHandle registration, Func<bool> authority,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        var gates = registration.SubscriptionGates();
        _interact = new Row(gates, WorldEventContract.WorldEventInteractFact);
        _lookat = new Row(gates, WorldEventContract.WorldEventLookatFact);
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
    }

    /// <summary>One of the two trigger rows: its fact, its binding and its subscription gate, resolved once at
    /// attach. The kernel refreshes the gate in place wherever the subscription table changes, so a trigger nobody
    /// listens to reads one field. A registration that does not declare the binding has no gate and nothing is
    /// published for it.</summary>
    private sealed class Row
    {
        internal Row(IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates, string fact)
        {
            Fact = fact;
            Binding = WorldEventContract.Binding(WorldEventContract.TriggerCapability(fact));
            Gate = gates.TryGetValue(Binding, out var gate) ? gate : null;
        }

        internal readonly string Fact;
        internal readonly string Binding;
        internal readonly RuntimeSubscriptionGate? Gate;
        internal bool Observed => Gate is { HasSubscribers: true };
    }

    /// <summary>Makes the half the static callbacks answer through, answering it so the session that owns it can
    /// detach exactly what it attached. The four patches are Runtime's; this only fills the slot they read.</summary>
    internal static WorldEventFacts Attach(IForgeEngine kernel, RuntimeModuleHandle registration,
        Func<bool> authority, Action<string> report)
    {
        var half = new WorldEventFacts(kernel, registration, authority, report);
        Current = half;
        return half;
    }

    /// <summary>Releases one attachment: the static slot first, so a callback already inside the patches finds
    /// nothing, then the half's own state.</summary>
    internal static void Detach(WorldEventFacts half)
    {
        ArgumentNullException.ThrowIfNull(half);
        if (ReferenceEquals(Current, half)) Current = null;
        half.Dispose();
    }

    /// <summary>A host migration keeps the level: the moments already seen stay under the new world number.</summary>
    internal void Follow(long from, long to) { if (_statesWorld == from) _statesWorld = to; }

    /// <summary>A checkpoint put the native trigger state back: a trigger pressed after the save is unpressed again, so its
    /// next press is a new moment, not a repeat of the one remembered.</summary>
    internal void CheckpointRestored() => _states.Clear();

    internal bool Faulted => _fault.Holds(_kernel.WorldEpoch);

    /// <summary>How many distinct trigger states this half has published, for a case to assert that a repeated
    /// activation is not news.</summary>
    internal int Published { get; private set; }

    // ---- the action ---------------------------------------------------------------------------------

    /// <summary>The condition action: it validates nothing of its own beyond the host guard, which the session's
    /// dispatch already answered — the two decisions the row makes live in `WorldEventContract.TryCondition`, in
    /// the game-independent assembly, so a case can drive every refusal without a game. What is left here is the
    /// one native call, the struct it takes, and the outcome a caller reads.</summary>
    internal static CommandResult Condition(CommandContext context)
    {
        if (!WorldEventContract.TryCondition(context, out var request, out string? code))
            return WorldEventContract.ConditionResult(context, Status.Rejected, Commit.None, code!);
        try
        {
            // Refuse before writing if the level's replicated condition state cannot be read.
            try
            {
                if (ConditionSolved(request.Index) == null)
                    return WorldEventContract.ConditionResult(context, Status.Rejected, Commit.None,
                        ResultCode.WorldEventConditionUnavailable);
            }
            catch (Exception containedError)
            {
                MapLog.Contained("world-event-actions.condition", containedError);
                return WorldEventContract.ConditionResult(context, Status.Rejected, Commit.None,
                    ResultCode.WorldEventConditionUnavailable);
            }
            var data = new WardenObjectiveEventData { Type = eWardenObjectiveEventType.SetWorldEventCondition };
            data.Condition = new WorldEventConditionPair { ConditionIndex = request.Index, IsTrue = request.Solved };
            WorldEventManager.ExecuteEvent(data, 0f);
            // The executor returns nothing, so the replicated slot is the receipt: a value other than the asked one is not a commit.
            if (ConditionSolved(request.Index) != request.Solved)
                return WorldEventContract.ConditionResult(context, Status.Failed, Commit.Unknown, ResultCode.WorldEventConditionUnconfirmed);
        }
        catch (Exception error)
        {
            // The executor may already have written the replicated condition before it threw, so the outcome is
            // reported as unknown: claiming nothing happened is a claim this layer cannot make.
            Current?.ReportOnce("commit:" + request.Index.ToString(CultureInfo.InvariantCulture),
                "world event condition action threw: " + error.GetType().Name);
            return WorldEventContract.ConditionResult(context, Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        }
        return WorldEventContract.ConditionResult(context, Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
    }

    // ---- the condition read -----------------------------------------------------------------------

    /// <summary>The condition read's one native reading: the slot's replicated value off the manager's own state
    /// replicator, which is the same state the action writes through and the same state every client receives.
    /// `pWorldEventManagerState.GetWorldEventCondition(index)` is the machine's own answer for a slot of
    /// `WorldEventConditions` — `WORLD_EVENT_CONDITION_COUNT` slots — so no byte is decoded here. A machine with
    /// no manager, no replicator or no replicated state answers null, and the row refuses by name rather than
    /// reporting an unsolved slot; a read that cannot be made is not a condition that is false.</summary>
    internal static bool? ConditionSolved(int index) => EnvironmentActions.ReplicatedState()?.GetWorldEventCondition(index);


    // ---- the two trigger rows -----------------------------------------------------------------------

    /// <summary>One activation of the interact trigger. It publishes the object's own key, the trigger state
    /// `true`, the player whose interaction did it, the zone the object stands in and its world position.</summary>
    internal void InteractTriggered(LG_InteractWorldEventTrigger trigger, SNet_Player? source)
        => Trigger(trigger, _interact, WorldEventContract.TriggeredMoment, source);

    /// <summary>The interact trigger's reusable reset, which is the same row's other moment: the trigger state
    /// goes back to `false`.</summary>
    internal void InteractReset(LG_InteractWorldEventTrigger trigger, SNet_Player? source)
        => Trigger(trigger, _interact, WorldEventContract.ResetMoment, source);

    /// <summary>One activation of the look-at trigger, carrying one port more than the interact row: the trigger's
    /// own maximum look-at distance, which is the field this component kind exists for.</summary>
    internal void LookatTriggered(LG_LookatWorldEventTrigger trigger, SNet_Player? source)
        => Trigger(trigger, _lookat, WorldEventContract.TriggeredMoment, source);

    /// <summary>The look-at trigger's own reusable reset.</summary>
    internal void LookatReset(LG_LookatWorldEventTrigger trigger, SNet_Player? source)
        => Trigger(trigger, _lookat, WorldEventContract.ResetMoment, source);

    /// <summary>Exactly one fact per observed native transition, using its precise address when available.</summary>
    private void Trigger(Component? component, Row row, string moment, SNet_Player? source)
    {
        if (_disposed || Faulted || !_authority() || component == null || component.WasCollected) return;
        if (_statesWorld != _kernel.WorldEpoch) { _states.Clear(); _statesWorld = _kernel.WorldEpoch; }
        if (!VanillaWorldObjects.Ready(_kernel.WorldEpoch) || VanillaWorldObjects.StaleComponent(_kernel.WorldEpoch, component)) return;
        var address = VanillaWorldObjects.Address(_kernel.WorldEpoch, component);
        var owner = component.gameObject;
        var world = VanillaWorldObjects.WorldObjectFor(_kernel.WorldEpoch, component) ?? owner.GetComponent<LG_WorldEventObject>();
        string? key = address ?? (world != null && !world.WasCollected ? world.WorldEventObjectKey : null);
        if (string.IsNullOrEmpty(key)) return;
        var state = (component.Pointer, row.Fact);
        if (_states.TryGetValue(state, out var prior) && prior == moment) return;
        _states[state] = moment;
        if (!row.Observed) return;
        bool triggered = WorldEventContract.IsTriggered(moment);
        double? distance = component is LG_LookatWorldEventTrigger lookat ? lookat.GetCamHoverMaxDistance : null;
        var entity = source == null ? null : PlayerReference(source);
        var zone = ZoneReference(owner);
        var transform = owner.transform;
        // The two rows share their first five outputs; only the look-at row has a distance. A port this activation
        // cannot observe is left unwritten, which is how an event says "not observable".
        var raised = _registration.BeginEvent(row.Gate!);
        var values = raised.Values;
        values.Text(Blocks.TriggerInteractionWorldEventInteract.OutKey, key!);
        values.Boolean(Blocks.TriggerInteractionWorldEventInteract.OutTriggered, triggered);
        if (entity != null) values.Entity(Blocks.TriggerInteractionWorldEventInteract.OutSource, entity);
        if (zone != null) values.Resource(Blocks.TriggerInteractionWorldEventInteract.OutZone, zone);
        if (transform != null)
        {
            Vector3 point = transform.position;
            values.Vector(Blocks.TriggerInteractionWorldEventInteract.OutPosition, point.x, point.y, point.z);
        }
        if (distance is { } meters) values.Number(Blocks.TriggerInteractionWorldEventLookat.OutLookatDistance, meters);
        var epoch = _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);
        var result = raised.Raise(
            "gtfo.world_event:" + epoch + ":" + key + ":" + (triggered ? "1" : "0") + ":" + (++_transition).ToString(CultureInfo.InvariantCulture),
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + epoch);
        if (result.Status == "queued") Published++;
    }

    /// <summary>The zone a world event object stands in, in the `gtfo.zone` grammar every other zone reader of
    /// this package answers with. An object outside a zone's own area is published without the port rather than
    /// with an invented zone.</summary>
    private static ResourceRef? ZoneReference(GameObject owner)
    {
        var area = owner.GetComponentInParent<LG_Area>();
        var zone = area == null || area.WasCollected ? null : area.m_zone;
        if (zone == null || zone.WasCollected) return null;
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return null;
        return new ResourceRef(RuntimeZones.ResourceKind,
            RuntimeZones.Id(coordinates.Dimension, coordinates.Layer, coordinates.Zone));
    }

    /// <summary>The player entity one activation names, through the player directory: a callback the
    /// game raises carries the `SNet_Player`, and `IPlayerDirectory` is the half that already resolves that
    /// player to the reference its player rows publish under. A player the directory cannot resolve yields no
    /// port rather than a partial reference.</summary>
    internal static EntityReference? PlayerReference(SNet_Player? source)
    {
        if (source == null || source.WasCollected || MapHost.Session is not { } session) return null;
        return session.TryOptionalPlayerService(static (s, value) => s.ReferenceOf(value), source,
            "world-event-player", out EntityReference? reference) ? reference : null;
    }

    /// <summary>Runs one trigger callback under this half's own guard: a callback that fails disables the producer
    /// for the rest of the level instead of leaving a broken one running, which is the rule every native
    /// callback of this package follows.</summary>
    internal void Guard(Action<WorldEventFacts> callback)
    {
        if (_disposed || Faulted) return;
        try { callback(this); }
        catch (Exception error)
        {
            _fault.Mark(_kernel.WorldEpoch);
            ReportOnce("guard", "World event trigger observation disabled for this level after native callback "
                + "failure: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _states.Clear();
    }
}

// The four patches below are postfixes on the game's own activation entries, one pair per component kind. Both
// entry results are read: a `Trigger` that returned false activated nothing, and publishing an activation the game
// refused would be a fact no level produced.

[HarmonyPatch(typeof(LG_InteractWorldEventTrigger),
    nameof(LG_InteractWorldEventTrigger.Trigger))]
internal static class InteractTriggeredReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_InteractWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.InteractTriggered(__instance, source));
    }
}

[HarmonyPatch(typeof(LG_InteractWorldEventTrigger),
    nameof(LG_InteractWorldEventTrigger.ResetTrigger))]
internal static class InteractResetReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_InteractWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.InteractReset(__instance, source));
    }
}

[HarmonyPatch(typeof(LG_LookatWorldEventTrigger),
    nameof(LG_LookatWorldEventTrigger.Trigger))]
internal static class LookatTriggeredReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_LookatWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.LookatTriggered(__instance, source));
    }
}

[HarmonyPatch(typeof(LG_LookatWorldEventTrigger),
    nameof(LG_LookatWorldEventTrigger.ResetTrigger))]
internal static class LookatResetReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_LookatWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.LookatReset(__instance, source));
    }
}
