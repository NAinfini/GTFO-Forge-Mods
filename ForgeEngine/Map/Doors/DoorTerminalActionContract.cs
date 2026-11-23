using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The door lock row the node list asks for under `a-door` — `forge.action.map.door_lock`, whose
/// `operation` member picks locking or unlocking — declared as this provider's own binding over the reviewed
/// contract. The row names the doors themselves: no lease handle stands behind a lock (ruling 160.3), because the
/// lock is the door's own `eDoorStatus`.
///
/// What the native side really has for a lock:
///
/// - The no-key lock is the door's own `LockDoorWithNoKey` request, which goes through the door sync
///   interaction; the status it writes is the door's `eDoorStatus`, so a lock is a native state and not a
///   bookkeeping entry on the Forge side. The game has no "any key" lock (`SetupSimpleLock` is an empty method).
/// - `LevelGeneration.LG_SecurityDoor_Locks.OnApproached` also proves the lock component is the door's own
///   replicated component, so a lock this provider writes is the game's lock and not a shadow one.
///
/// What it really has for an unlock: `LevelGeneration.LG_Door_Sync.AttemptDoorInteraction(eDoorInteractionType,
/// float, float, Vector3, Agent)` is the door's synchronized interaction entry, and `eDoorInteractionType.Unlock`
/// is a member of the native enum (`eDoorInteractionType` in `Modules-ASM.dll`: `Open`, `SetLockedWithChainedPuzzle_Alarm`,
/// `SetLockedWithChainedPuzzle`, `SetLockedNoKey`, `ActivateChainedPuzzle`, `Unlock`, `Close`, `DoDamage`,
/// `SetGluedMaxEnabled`, `SetGluedMaxDisabled`, `SetGlueLevel`, `Approach`). `LG_Door_Sync.TryUnlockDoor(pDoorInteraction)`
/// is the private body that type dispatches into, and the interaction type is what it switches on, so the
/// interaction type — not the method name — is the evidence. `LG_SecurityDoor_Locks.SetupAsLockedNoKey` is the
/// same native setup the game's own generator lock uses (`eProgressionPuzzleType.Locked_No_Key = 3` maps to
/// `eDoorStatus.Closed_LockedWithNoKey`), which is what makes a generator-group door expressible with these two
/// rows and no Forge-side lock table.
///
/// Locking is always the no-key lock and unlocking never spends a key: neither native entry takes a key item, so
/// the row offers no key choice.</summary>
internal static class DoorTerminalActionContract
{
    public const string LockPermission = "door.lock";

    public const string LockHandlerName = "gtfo.map_object.door_lock";

    /// <summary>Internal binding ids, by capability: this provider's own namespace plus the capability's own
    /// suffix, exactly as the open and close rows spell theirs.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>The row's ports: the recipient collection and the `operation` that picks locking or unlocking.
    /// No handle is emitted, because the lock a plan asked for is the door's own `eDoorStatus` and a handle to it would be Forge bookkeeping with nothing
    /// native behind it.</summary>
    public static readonly HandlerShape LockShape = new HandlerShape()
        .Inputs("doors").Outputs("result").Parameters("operation");
    public static readonly HandlerSlot Doors = LockShape.InputSlot("doors");
    public static readonly HandlerSlot Operation = LockShape.ParameterSlot("operation");

    /// <summary>`forge.action.map.door_alarm`: the doors, the scene scan that replaces their alarm and the `operation`
    /// that picks replacing or putting the door's own alarm back. It changes the door's lock, so it asks for the lock
    /// permission.</summary>
    public const string AlarmHandlerName = "gtfo.map_object.door_alarm";
    public static readonly HandlerShape AlarmShape = new HandlerShape()
        .Inputs("doors", "scan").Outputs("result").Parameters("operation");
    public static readonly HandlerSlot AlarmScan = AlarmShape.InputSlot("scan");
    public static readonly HandlerSlot AlarmOperation = AlarmShape.ParameterSlot("operation");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.action.map.door_lock", Blocks.ActionMapDoorLock.Id, LockHandlerName, "execute", new[] { LockPermission }, LockShape),
        new ProviderBinding("forge.module.gtfo.map.binding.action.map.door_alarm", Blocks.ActionMapDoorAlarm.Id, AlarmHandlerName, "execute", new[] { LockPermission }, AlarmShape)
    };
}
