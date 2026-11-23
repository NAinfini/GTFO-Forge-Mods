using System;
using ForgeEngine.Framework;

namespace ForgeMap;
/// <summary>The door and terminal interaction rows this provider publishes but the catalog does not carry yet.
/// Every row here is a **new** canonical id: the node-list rows that already have a landing place keep it
/// (`e-door-open` → `forge.trigger.interaction.door_state`, `e-door-unlock` → `forge.trigger.interaction.lock_state`,
/// `e-term-cmd`/`e-term-alarm` → `forge.trigger.interaction.terminal_command`), and only the facts no catalog row
/// names are declared here, so nothing is described twice.
///
/// Each row's ports are the facts the native side really carries, and nothing else:
///
/// - `door_scan` is the door's own chained-puzzle lock. The two callbacks the lock component owns —
///   `LG_SecurityDoor_Locks.OnPlayerActivateChainedPuzzle` and `OnChainedPuzzleSolved` — are the game's own
///   notifications that the puzzle on the door was activated and that it was solved, which is exactly the
///   checklist's "扫描开始 / 完成". The phase is not taken from the callback's name alone: the callback runs a
///   check of the puzzle instance's own `IsSolved` / `IsActive` readings, so a solved callback that fires while
///   the puzzle still reads unsolved is reported as the activation it really is. The door status the phase was
///   derived from travels beside the phase, because a plan that wants the native value should be able to read it
///   rather than trust this provider's reading of it.
/// - `lock_broken` is the weak lock's own replicated status: `LevelGeneration.LG_WeakLock.OnStateChange` with
///   `eWeakLockStatus.Unlocked`, or the door-lock component's own `SyncHackSuccess`. The lock kind
///   (`eWeakLockType.Melee` / `Hackable`) is the native member that tells a smashed lock from a hacked one.
///   The subject is the door the lock was spawned on, which is the only identity this provider can address: a
///   weak lock has no stable address of its own.
/// - `door_broken` is the weak door itself — the only door kind the game lets enemies break (`LG_WeakDoor`,
///   `LevelGeneration.LG_WeakDoor`). Authored interior doors resolve through their stable map-object address; the
///   row carries that exact door together with the zone and world position an author can filter on. The two stages
///   are the two sync callbacks the game's own
///   replication layer raises on the door — `LG_Door_Sync.OnDoorGotDamage` while it is being hit and
///   `LG_Door_Sync.OnDoorGotDestroyed` when it breaks. Both carry the `SNet_Player` that caused the damage, which
///   is the `attacker` port; it is optional because an environmental or enemy hit names no player.
///
/// Every row is `host` execution and publishes only on the authoritative peer, which is the rule the package's
/// existing map-object rows already follow.</summary>
internal static class DoorTerminalEventContract
{
    /// <summary>The four fact kinds these rows carry, in the order the module declares them. A binding or a
    /// fact key names one of these, never the capability again, so the two cannot drift apart.</summary>
    public const string DoorScanFact = "door_scan";
    public const string LockBrokenFact = "lock_broken";
    public const string DoorBrokenFact = "door_broken";
    public const string DoorNoiseFact = "door_noise";

    /// <summary>The two stages of `forge.trigger.interaction.door_broken`, in checklist order. A weak door is
    /// attacked first and broken second; the two are one author node's two phases rather than two rows, because
    /// the door, the zone and the position are the same three facts in both.</summary>
    public const string AttackedPhase = "attacked";
    public const string BrokenPhase = "broken";

    /// <summary>The provider-side binding id of one fact: this provider's own namespace plus the fact's own
    /// name, so the counterpart of a row is readable from either side. It is derived here and never spelled out
    /// twice, because a binding id that disagrees with the capability it serves fails registration.</summary>
    public static string Binding(string fact) => ModuleDefinition.ProviderId + ".binding.interaction." + fact;

    public static string Capability(string fact) => fact switch
    {
        DoorScanFact => Blocks.TriggerInteractionDoorScan.Id,
        LockBrokenFact => Blocks.TriggerInteractionLockBroken.Id,
        DoorBrokenFact => Blocks.TriggerInteractionDoorBroken.Id,
        DoorNoiseFact => Blocks.TriggerInteractionDoorNoise.Id,
        _ => throw new RuntimeContractException("door-terminal-event-fact", "Unknown door or terminal event fact.")
    };

    /// <summary>The `door_lock_cause` vocabulary this row's enum port carries, in member order, because the port
    /// publishes the member index and not the name: a hackable weak lock that was hacked and a melee weak lock
    /// that was smashed, the only two releases the weak-lock reader observes.</summary>
    public static readonly string[] LockCauses = { "hacked", "smashed" };

    /// <summary>The member index of one lock cause in <see cref="LockCauses"/>, or -1 for a cause outside the
    /// set, which is a programming error rather than a value a native read can produce.</summary>
    public static int LockCauseIndex(string cause)
    {
        for (int index = 0; index < LockCauses.Length; index++)
            if (string.Equals(LockCauses[index], cause, StringComparison.Ordinal)) return index;
        return -1;
    }

    /// <summary>The `door_phase` vocabulary the weak-door row's enum port carries, in member order. It is its own
    /// set because `interaction_phase` is the door's own interaction vocabulary (`requested`..`failed`) and this
    /// row's two stages are not members of it: a door under attack is not a requested interaction, and a door that
    /// broke is not a failed one. The set is declared here and registered with the framework and the catalog by
    /// the integration batch, exactly as the lock-cause set above is.</summary>
    public static readonly string[] DoorPhases = { AttackedPhase, BrokenPhase };

    /// <summary>The member index of one weak-door phase in <see cref="DoorPhases"/>, or -1 for a value outside
    /// the set.</summary>
    public static int DoorPhaseIndex(string phase)
    {
        for (int index = 0; index < DoorPhases.Length; index++)
            if (string.Equals(DoorPhases[index], phase, StringComparison.Ordinal)) return index;
        return -1;
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.door_scan", Blocks.TriggerInteractionDoorScan.Id,"forge.module.gtfo.map.observe.door_scan", "observe", new[] { "gtfo.map_object.read" }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.lock_broken", Blocks.TriggerInteractionLockBroken.Id,"forge.module.gtfo.map.observe.lock_broken", "observe", new[] { "gtfo.map_object.read" }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.door_broken", Blocks.TriggerInteractionDoorBroken.Id,"forge.module.gtfo.map.observe.door_broken", "observe", new[] { "gtfo.map_object.read" }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.door_noise", Blocks.TriggerInteractionDoorNoise.Id,"forge.module.gtfo.map.observe.door_noise", "observe", new[] { "gtfo.map_object.read" })
    };
}
