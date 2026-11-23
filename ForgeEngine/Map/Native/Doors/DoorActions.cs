using System;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>What an open request may do about the door's own lock conditions. The two policies are the two
/// entries the door really offers: its own interaction entry, which still lets the lock, the interaction gate
/// and the glue decide, and its own force-open entry.</summary>
internal enum DoorBypassPolicy
{
    /// <summary>Ask through the door's own interaction entry. A door that reads locked, broken or not
    /// interactable is refused before anything is written instead of being asked anyway.</summary>
    Respect,
    /// <summary>Ask the door to force itself open, which is what the game's own force entry is for.</summary>
    Force
}

/// <summary>
/// The door half of the native execution layer: one method per door action, each taking the instance the address
/// layer resolved and the address it was resolved through, and each writing through the door's or its lock
/// component's own member. Nothing here is a plan, a handle or a result frame — a later binding resolves the
/// instance and the parameters and calls in, and this layer answers with the one decision it made.
///
/// Two rules hold for every method. The instance is re-checked against its address before anything is written,
/// so a door that was replaced, moved to another address or torn down under a stale reference is refused rather
/// than acted on. Security-door damage is refused because its override carries nothing; authored interior weak
/// doors use their implemented damage entry. A close carries no policy: the door's own interaction entry is the
/// one close entry the game has, and a request that asked for a crush or a forced close is no longer expressible.
///
/// A native read that throws is not caught here: an action runs inside the same session guard as the readbacks,
/// which disables the provider once instead of letting this layer swallow a failure the rest of the session
/// would keep acting on.
/// </summary>
internal static class DoorActions
{
    /// <summary>The native damage entry was entered. It never reaches a result: the damage row reports the health
    /// readback that follows, so this is this layer's own word and not a result code.</summary>
    internal const string DamageIssued = "door-damage-issued";

    /// <summary>Asks a door to open. The door's own status decides first: an open or opening door is left alone
    /// — the native interaction entry toggles, so asking an open door to open would close it — and a destroyed
    /// or unopenable door is refused instead of asked. A `Respect` request then passes the door's own lock
    /// condition, its own can-not-open status and its own interaction gate; a `Force` request goes through the
    /// door's force entry, which is the one entry allowed to ignore those conditions.</summary>
    internal static MapActionOutcome Open(LG_SecurityDoor door, MapObjectReference address, DoorBypassPolicy policy)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (Read(door) is not { } state) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(ResultCode.DoorDestroyed);
        if (IsOpen(state.Status)) return MapActionOutcome.AlreadyInState(ResultCode.DoorAlreadyOpen);
        if (policy == DoorBypassPolicy.Force)
        {
            door.ForceOpenSecurityDoor();
            return MapActionOutcome.Issued(ResultCode.DoorOpenIssued);
        }
        // The status before the lock reading: a door the level built as unable to open is not asked even when a
        // key would satisfy it, and the lock reading is what the door itself publishes rather than a second one.
        if (state.Status == (int)eDoorStatus.Closed_BrokenCantOpen) return MapActionOutcome.Refused(ResultCode.DoorClosedBroken);
        if (state.Locked) return MapActionOutcome.Refused(ResultCode.DoorLocked);
        if (!door.InteractionAllowed) return MapActionOutcome.Refused(ResultCode.DoorInteractionNotAllowed);
        // The argument is the entry's own `onlyUnlock` flag; a plain open asks for it as the game's own
        // interaction does, so the door unlocks and opens under its own rules.
        door.AttemptOpenCloseInteraction(false);
        return MapActionOutcome.Issued(ResultCode.DoorOpenIssued);
    }

    /// <summary>Asks an authored interior weak door to open through its own interaction entry. Unlike a security
    /// door it has no <c>ForceOpen*</c> member, so the force policy is refused before the toggling entry is reached.
    /// The last replicated status and interaction gate are the door's own state; a second open never toggles an
    /// already-open door closed.</summary>
    internal static MapActionOutcome Open(LG_WeakDoor door, MapObjectReference address, DoorBypassPolicy policy)
    {
        if (Unusable(door, address) is { } refused) return refused;
        int status = (int)door.LastStatus;
        if (status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(ResultCode.DoorDestroyed);
        if (IsOpen(status)) return MapActionOutcome.AlreadyInState(ResultCode.DoorAlreadyOpen);
        if (policy == DoorBypassPolicy.Force) return MapActionOutcome.Refused(ResultCode.DoorForceUnsupported);
        if (status == (int)eDoorStatus.Closed_BrokenCantOpen) return MapActionOutcome.Refused(ResultCode.DoorClosedBroken);
        if (MapObjectDoorStatus.IsLocked(status)) return MapActionOutcome.Refused(ResultCode.DoorLocked);
        if (!door.InteractionAllowed) return MapActionOutcome.Refused(ResultCode.DoorInteractionNotAllowed);
        door.AttemptOpenCloseInteraction(false);
        return MapActionOutcome.Issued(ResultCode.DoorOpenIssued);
    }

    /// <summary>Asks a door to close. A door that is not open is left alone, because the same interaction entry
    /// toggles. Nothing else is carried: the door's own entry is the one close entry the game has, and the
    /// catalog's two close policies are gone on both sides.</summary>
    internal static MapActionOutcome Close(LG_SecurityDoor door, MapObjectReference address)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (Read(door) is not { } state) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(ResultCode.DoorDestroyed);
        if (!IsOpen(state.Status)) return MapActionOutcome.AlreadyInState(ResultCode.DoorAlreadyClosed);
        door.AttemptOpenCloseInteraction(false);
        return MapActionOutcome.Issued(ResultCode.DoorCloseIssued);
    }

    /// <summary>Closes an authored interior weak door through the same native toggle, after its own replicated
    /// status proves the request cannot accidentally open it.</summary>
    internal static MapActionOutcome Close(LG_WeakDoor door, MapObjectReference address)
    {
        if (Unusable(door, address) is { } refused) return refused;
        int status = (int)door.LastStatus;
        if (status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(ResultCode.DoorDestroyed);
        if (!IsOpen(status)) return MapActionOutcome.AlreadyInState(ResultCode.DoorAlreadyClosed);
        door.AttemptOpenCloseInteraction(false);
        return MapActionOutcome.Issued(ResultCode.DoorCloseIssued);
    }

    /// <summary>Damages a door through the structural damage entry of the door kind this provider addresses.
    ///
    /// That entry is refused here instead of invoked, and the reason is native rather than a policy: on
    /// build 20403457 `LG_SecurityDoor.AttemptDamage` shares the folded empty body at file offset 0x351790
    /// (`C2 00 00` — `ret 0`), while the damageable `LG_WeakDoor` has its own implementation at 0x15C0800. A
    /// zone entrance is a security door and no address names a weak door, so a damage request against an
    /// addressed door would be a request nothing carries out; reporting it as issued would be a success this
    /// layer cannot back.
    ///
    /// The damage type, the source position and the source agent are the arguments the binding resolves and the
    /// signature the call would take, so they are validated first: an unusable request is named as unusable, and
    /// the source agent may be absent because the native signature carries no requirement that it exists.</summary>
    internal static MapActionOutcome Damage(LG_SecurityDoor door, MapObjectReference address, eDoorDamageType damageType,
        UnityEngine.Vector3 sourcePosition, Agents.Agent? sourceAgent)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (!Enum.IsDefined(typeof(eDoorDamageType), damageType)) return MapActionOutcome.Refused(ResultCode.DoorDamageTypeUnknown);
        if (!float.IsFinite(sourcePosition.x) || !float.IsFinite(sourcePosition.y) || !float.IsFinite(sourcePosition.z))
            return MapActionOutcome.Refused(ResultCode.DoorSourcePositionUnusable);
        if (Read(door) is not { } state) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(ResultCode.DoorDestroyed);
        // The source agent is deliberately not required: the native signature carries no requirement that one
        // exists and a damage source may well be the environment, so an absent agent is not a refusal.
        return MapActionOutcome.Refused(ResultCode.DoorNotDamageable);
    }

    /// <summary>Submits one native weak-door damage interaction. The caller maps the canonical request onto one
    /// exact native damage kind and owns the health readback; this layer only performs the final address/state
    /// check and enters the implemented vanilla path.</summary>
    internal static MapActionOutcome Damage(LG_WeakDoor door, MapObjectReference address, eDoorDamageType damageType,
        UnityEngine.Vector3 sourcePosition, Agents.Agent? sourceAgent)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (!Enum.IsDefined(typeof(eDoorDamageType), damageType)) return MapActionOutcome.Refused(ResultCode.DoorDamageTypeUnknown);
        if (!float.IsFinite(sourcePosition.x) || !float.IsFinite(sourcePosition.y) || !float.IsFinite(sourcePosition.z))
            return MapActionOutcome.Refused(ResultCode.DoorSourcePositionUnusable);
        if (sourceAgent != null && sourceAgent.WasCollected) return MapActionOutcome.Refused(ResultCode.DoorUnavailable);
        if (door.LastStatus == eDoorStatus.Destroyed) return MapActionOutcome.Refused(ResultCode.DoorDestroyed);
        // Forge's own damage is never attributed back to its source enemy as a vanilla door hit.
        var outer = WeakDoorEnemyDamageScope.Issuing;
        WeakDoorEnemyDamageScope.Issuing = true;
        try { door.AttemptDamage(damageType, sourcePosition, sourceAgent); }
        finally { WeakDoorEnemyDamageScope.Issuing = outer; }
        return MapActionOutcome.Issued(DamageIssued);
    }

    /// <summary>Whether the door's own status is a door standing open. Both open states count: a door that is
    /// still animating is open for a second request as much as one that finished.</summary>
    private static bool IsOpen(int status)
        => status == (int)eDoorStatus.Open || status == (int)eDoorStatus.Opening;

    /// <summary>The one gate every door action passes: the instance must read, and it must still be the door the
    /// address was resolved to. A door that was replaced or whose entrance zone changed is refused rather than
    /// acted on under a reference the address layer would no longer produce.</summary>
    private static MapActionOutcome? Unusable(LG_SecurityDoor door, MapObjectReference address)
        => door == null || door.WasCollected ? MapActionOutcome.Refused(ResultCode.DoorUnavailable)
            : DoorObservation.IsCurrentAddress(door, address) ? null
            : MapActionOutcome.Refused(ResultCode.StaleEntity);

    private static MapActionOutcome? Unusable(LG_WeakDoor door, MapObjectReference address)
        => door == null || door.WasCollected ? MapActionOutcome.Refused(ResultCode.DoorUnavailable)
            : DoorObservation.IsCurrentAddress(door, address) ? null
            : MapActionOutcome.Refused(ResultCode.StaleEntity);

    /// <summary>The door's own reading, taken once per action: the status, the lock condition and the key the
    /// lock wants all come from the one read the observation path uses, so a refusal here and a published state
    /// there can never describe two different doors.</summary>
    private static MapObjectDoorSnapshot? Read(LG_SecurityDoor door) => DoorObservation.Read(door)?.Door;
}
