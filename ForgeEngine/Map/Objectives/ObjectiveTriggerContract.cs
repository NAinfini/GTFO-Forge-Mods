using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The objective-machine trigger row this provider publishes (`forge.trigger.objective.state_changed`): one
/// layer's objective moved, and the `change` port says which of its three readings moved — `state` (the status),
/// `phase` (its chain position) or `progress` (the objective-item count). It is one row because it is one native
/// callback's own report: `WardenObjectiveManager.OnStateChange` carries the machine's whole replicated state, and
/// the native half reads the layer's status, chain index and item counters from that state after the body returned
/// (`evidence/objective-hooks.json`, hooks `ObjectiveStateChange` and `ObjectiveStatusCheck`).
///
/// **The layer is the subject.** The objective machine keeps exactly three fixed layer slots and no per-instance
/// objective identity, so a layer is what an author selects — the same three names
/// (`main`/`secondary`/`third`) the level-event rows publish an objective under and the `v-obj` value row reads.
/// The fact is published per layer and per kind of change, so a plan that reacts to a status change is not woken by
/// a chain step of the same callback, and the ports carry that layer's own reading.
///
/// **What each port means.** `state` and `previous` are the `eWardenObjectiveStatus` member names the layer holds
/// and held, in the value row's own vocabulary (`not_discovered`, `discovered`, `started`, `partially_solved`,
/// `item_solved`). `phase` and `previous_phase` are the layer's `chainIndex` — the objective chain step, which is
/// what a reactor wave, a "node" of a multi-node objective and an overload step all are. `count` and `total` are the
/// item counters the replicated state itself holds: how many of the objective's item slots are set and how many
/// required items the state declares. Every fact carries all of them, so a plan reads the whole transition from
/// whichever change woke it.
///
/// Nothing here writes the machine. This is an observation, so its binding carries role `observe`, declares no
/// handler shape and reads under the objective read permission the level-event rows already carry.</summary>
internal static class ObjectiveTriggerContract
{
    public const string StateHandler = "gtfo.map.objective_state_changed";

    /// <summary>The fact the row publishes, spelled without the provider: it is the second half of the event id
    /// and the name a diagnostic reports, and it never travels as a port.</summary>
    public const string StateFact = "objective.state_changed";

    /// <summary>The three members of the row's `change` port (the `objective_change` enum set), in its order.</summary>
    public const string StateChange = "state";
    public const string PhaseChange = "phase";
    public const string ProgressChange = "progress";

    /// <summary>The `change` port's wire value for one member. An enum port travels as the member's index in the
    /// `objective_change` set (the kernel refuses a member name as `invalid-enum`), so the index is resolved from the
    /// SDK's own set once and never spelled here a second time.</summary>
    public static int ChangeMember(string change)
    {
        var members = RuntimeEnumSets.Members("objective_change");
        for (var index = 0; index < members.Count; index++)
            if (string.Equals(members[index], change, StringComparison.Ordinal)) return index;
        throw new RuntimeContractException("objective-change-unknown", "Not an objective_change member: " + change);
    }

    /// <summary>The permission the row reads under. It is the objective namespace's own read permission,
    /// which the level-event observation rows and the `v-obj` value row already declare: an author who may read an
    /// objective's state may subscribe to its transitions.</summary>
    public const string ReadPermission = LevelEventContract.ObjectiveReadPermission;

    /// <summary>The binding id of one capability. The rule is the level-event contract's own, because these rows
    /// are published by the level-event module beside that family's rows: one module derives the binding an event
    /// travels under from the capability alone, and a second spelling here would publish facts under a binding
    /// nothing declared.</summary>
    public static string Binding(string capabilityId) => LevelEventContract.Binding(capabilityId);

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_objective_state_changed", Blocks.TriggerObjectiveStateChanged.Id, StateHandler, "observe", new[] { ReadPermission })
    };
}
