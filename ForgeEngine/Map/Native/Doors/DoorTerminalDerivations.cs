using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The two things a door's chained-puzzle lock can report, as the callback that carried the report. It
/// is deliberately not the phase: the callback names which of the lock's two notifications ran, while the phase
/// the row publishes is decided from the puzzle instance's own reading (see
/// <see cref="DoorTerminalDerivations.ScanPhase"/>), because a "solved" notification that fires while the puzzle
/// still reads unsolved is an activation.</summary>
internal enum DoorScanStage
{
    /// <summary>`LG_SecurityDoor_Locks.OnPlayerActivateChainedPuzzle` ran: the puzzle on the door was activated.</summary>
    Activated,
    /// <summary>`LG_SecurityDoor_Locks.OnChainedPuzzleSolved` ran: the puzzle on the door reported solved.</summary>
    Solved
}

/// <summary>The door and terminal interaction derivations, and nothing else: every method here is a pure reading
/// of values the native side produced. The native callbacks in `Native/Doors/DoorTerminalFacts.cs` do the reading and
/// hand the values in, which is what makes the whole derivation reachable from a test without a game — and what
/// keeps a callback and a row from describing the same transition two different ways.
///
/// The status numbers are `LevelGeneration.eDoorStatus` as this build declares it (`Modules-ASM.dll`,
/// `eDoorStatus`: `None`=0, `Closed`=1, `Closed_BrokenCantOpen`=2, `Closed_LockedWithKeyItem`=3,
/// `Closed_LockedWithChainedPuzzle_Alarm`=4, `Closed_LockedWithChainedPuzzle`=5,
/// `Closed_LockedWithPowerGenerator`=6, `Closed_LockedWithNoKey`=7, `ChainedPuzzleActivated`=8, `Unlocked`=9,
/// `Open`=10, `Destroyed`=11, `GluedMax`=12, `TryOpenStuckInGlue`=13, `TryOpenStuckBroken`=14,
/// `Closed_LockedWithBulkheadDC`=15, `Opening`=16). The status names come from `MapObjectDoorStatus.Name`, which
/// is this package's single spelling of the same enum.</summary>
internal static class DoorTerminalDerivations
{
    /// <summary>The five unique-command slots the map editor offers per terminal. The native command enum has
    /// exactly five `UniqueCommand*` members, which is the whole reason the count is five: the slots are the
    /// game's own, not a Forge table.</summary>
    internal const int UniqueCommandSlots = 5;

    /// <summary>The `interaction_phase` member index a scan callback carries. The puzzle's own solved reading is
    /// the authority when the lock could produce one: a callback that ran while the puzzle reads solved is the
    /// completion and one that ran while it does not is the activation, whatever the callback was named. The
    /// indices are the framework's own enum order (`interaction_phase`: requested, started, completed, cancelled,
    /// failed), which is what `MapObjectPhases` spells in the same order.
    ///
    /// The one asymmetry is deliberate: `IsSolved` has no "unsolved because it did not start" reading, so a
    /// callback that named the completion and found the puzzle unsolved is reported as the activation this
    /// reading really saw, rather than as a completion nothing confirmed.</summary>
    internal static int ScanPhase(DoorScanStage stage, bool? puzzleSolved) => puzzleSolved switch
    {
        true => MapObjectPhases.Completed,
        false => MapObjectPhases.Started,
        null => stage == DoorScanStage.Solved ? MapObjectPhases.Completed : MapObjectPhases.Started
    };

    /// <summary>The `door_lock_cause` member name for a lock release. A weak lock that was destroyed by melee is
    /// `smashed`; a hackable lock that opened was hacked. That is the distinction `eWeakLockType` (`Melee` /
    /// `Hackable`) gives natively.</summary>
    internal static string LockCause(bool smashed) => smashed ? "smashed" : "hacked";

    // ---- the weak door -----------------------------------------------------------------------------

    /// <summary>One weak door's runtime entity: it is a `gtfo.map_object` of the `door` category, exactly like
    /// the entrance gate the door actions open, close and lock. A weak door is not a zone's entrance — several of
    /// them stand in one zone — so its address carries its own serial instead of the entrance token, which is
    /// what makes it the one identity an author can wire both the door actions and this fact to. The address is
    /// built by the category's own factory, so a reference of this half can never spell an address the door
    /// reader would refuse.</summary>
    internal static EntityReference? WeakDoorReference(int? dimension, int? layer, int? zone, int? serial, long world)
        => MapObjectDoorAddress.CreateWeak(dimension, layer, zone, serial) is { } address
            ? new EntityReference(EntityKinds.MapObject + ":" + address, world, 1)
            : null;

    /// <summary>One weak door's durability as its own destruction component holds it: `m_health` (what is left)
    /// or `m_healthMax` (the most it holds), the two members `door_broken` reports beside the stage. A door whose
    /// component does not read answers null, and the port is then published absent rather than as a guess.</summary>
    internal static double? WeakDoorHealth(LG_WeakDoor door, bool max)
    {
        try
        {
            var destruction = door.m_destructionComp?.TryCast<LG_WeakDoor_Destruction>();
            if (destruction == null || destruction.WasCollected) return null;
            double value = max ? destruction.m_healthMax : destruction.m_health;
            return double.IsFinite(value) ? value : null;
        }
        catch (Exception containedError) { MapLog.Contained("door-terminal-derivations.weak-door-health", containedError); return null; }
    }

    /// <summary>The serial one weak door carries inside its zone. The game assigns one instance id per object
    /// inside one level and this is it, which is the same per-object identity `LevelObjectObservation` keys its
    /// containers and level items by. A missing or destroyed object has none, and a door with no serial is
    /// published under no reference rather than under a shared one.</summary>
    internal static int? WeakDoorSerial(LG_WeakDoor door)
    {
        if (door == null || door.WasCollected) return null;
        var gameObject = door.gameObject;
        if (gameObject == null || gameObject.WasCollected) return null;
        int serial = gameObject.GetInstanceID();
        return serial < 0 ? null : serial;
    }

    /// <summary>One object's own world position, as the three finite numbers a `vector3` port carries, or null
    /// when the transform or a coordinate does not read. A snapshot carries three real numbers, so this answers
    /// no position rather than a substituted origin.</summary>
    internal static double[]? Position(UnityEngine.Transform? transform)
    {
        if (transform == null || transform.WasCollected) return null;
        return Position(transform.position);
    }

    /// <summary>One finite native world point, as the three numbers a <c>vector3</c> output carries.</summary>
    internal static double[]? Position(UnityEngine.Vector3 position)
    {
        return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
            ? new[] { (double)position.x, (double)position.y, (double)position.z } : null;
    }

    // ---- the terminal command row ------------------------------------------------------------------

    /// <summary>Whether one command is the command an author named by slot, and which slot. Both spellings the
    /// row accepts meet here: a `unique1`..`unique5` name and the game's own one-based slot number are the same
    /// command, and a request that named neither a slot nor a command resolves to nothing rather than to the
    /// first command of the terminal.</summary>
    internal static int CommandSlot(string? commandText, int? slot)
    {
        if (slot is { } declared) return declared is >= 1 and <= UniqueCommandSlots ? declared : 0;
        if (commandText == null) return 0;
        var text = commandText.Trim();
        const string prefix = "unique";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        int suffix = text.Length > prefix.Length && int.TryParse(text[prefix.Length..], NumberStyles.None,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        return suffix is >= 1 and <= UniqueCommandSlots ? suffix : 0;
    }
}

/// <summary>The per-world state a fact stream needs and nothing else: which state of which object was published
/// last, so a native callback that fires twice for one state publishes one fact. The key is the fact kind and
/// the object's own identity — its map address, or the entity id of an object that has no address — so two facts
/// about one door cannot collide, and the state is the values the fact carries: a repeated sync produces the same
/// reading and publishes nothing while reporting the transition it was published with. Both are values, so
/// remembering a reading allocates nothing once the object has been seen.
///
/// The table holds no native reference: a world change invalidates every address it could name, so holding the
/// keys would only hold dead text, and the owner clears it on a world transition.</summary>
internal sealed class DoorTerminalFactLedger
{
    private readonly Dictionary<DoorTerminalSubject, (FactReading State, int? ScanIndex, long Transition)> _published = new();
    private long _count;

    /// <summary>Whether one state is new. A state that was already published under this key answers false and
    /// reports the transition it was published with; a new one consumes exactly one transition.</summary>
    internal bool Observe(in DoorTerminalSubject key, in FactReading state, out long transition, int? scanIndex = null)
    {
        // Terminal commands and native noise submissions are occurrences, not persistent door/lock/scan state.
        // Two accepted executions or submissions with identical values must reach the same subscribed plan twice.
        if (key.Fact != DoorTerminalPublisher.CommandFact
            && key.Fact != DoorTerminalEventContract.DoorNoiseFact
            && _published.TryGetValue(key, out var last) && last.State == state && last.ScanIndex == scanIndex)
        {
            transition = last.Transition;
            return false;
        }
        transition = ++_count;
        _published[key] = (state, scanIndex, transition);
        return true;
    }

    /// <summary>Forgets the state published under one key, so the next reading of it is new again. It is for a state
    /// whose object was put back to where it was before that state, by something that is not a fact of its own.</summary>
    internal void Forget(in DoorTerminalSubject key) => _published.Remove(key);

    /// <summary>Drops every key. Called on a world transition, where no address survives.</summary>
    internal void Clear()
    {
        if (_published.Count != 0) _published.Clear();
        _count = 0;
    }
}

/// <summary>One object of one door or terminal fact: the fact kind and either the object's map address or, for an
/// object that has no address (a weak door), its entity id. A value, so looking one up builds no key text.</summary>
internal readonly record struct DoorTerminalSubject(string Fact, MapObjectReference? Address, string? Entity);
