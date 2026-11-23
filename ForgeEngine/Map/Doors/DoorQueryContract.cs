using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `v-door` row: the read-only door state (`forge.query.map.door_state`). It is the value half of
/// the door rows this provider already publishes — the five states the checklist names, derived from the door's
/// own native status and lock component, and read on demand by a `query` step.
///
/// Naming and family follow `forge.query.environment.state`, the other value row of this package: the id spells
/// the `query` tier the plan node runs in, while the capability's `kind` is `modifier`, which is the
/// value-producing family the registry registers evaluators for. The id says `query` because the step an author
/// places is a query step; the two names are different fields and neither is derived from the other.
///
/// Two of the five states are the ones the previous batch could not answer and this one must:
///
/// - **要扫描** is a door whose own chained puzzle is the next step: `eDoorStatus.Closed_LockedWithChainedPuzzle` (5)
///   and `Closed_LockedWithChainedPuzzle_Alarm` (4). It is derived, never stored: the status is the only thing read.
///   `Closed_LockedWithBulkheadDC` (15) is locked, like the generator lock (6): the door waits on its bulkhead
///   controller's own scan, and only that scan's completion hands the door to its puzzle or unlocks it
///   (`Map/evidence/door-bulkhead-dc-lock-20403457.json`).
/// - **被打破** is `eDoorStatus.Destroyed` (11). A weak door reads the same status, which is what makes the two
///   door kinds one vocabulary; a weak door is read through its room address (`weakroom-…`) or its interior-door
///   address, and has no key, puzzle or enemy wave of its own.
///
/// The two detailed readings travel beside the coarse state rather than replacing it: `detail` is the native
/// `door_state` member (its index, like every enum port) and `locked` is the lock component's own answer, so a plan that needs the exact
/// status is not forced to infer it back from the five-member derivation. `glued` and `stuck` name the two
/// obstructions the game's own status carries (`GluedMax`, `TryOpenStuckInGlue`, `TryOpenStuckBroken`,
/// dump.cs:688678-688680) and `puzzle` carries the chained puzzle the door's lock holds. Every reading is the
/// door's own native member — nothing here is a Forge-side state ledger.</summary>
internal static class DoorQueryContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.map.door_state";
    public const string HandlerName = "gtfo.map.door_state";

    /// <summary>The permission the row's own read needs. It is the map-object namespace's read permission, which
    /// is what every door observation of this package already declares.</summary>
    public const string Permission = MapObjectContract.MapObjectReadPermission;

    /// <summary>The refusal an input that names no door this provider owns gets. The trigger rows of this package
    /// answer for one entity kind, and this row answers for the same one.</summary>
    public const string InputKindCode = "door-state-input-kind";
    /// <summary>The refusal a door reference the world no longer answers for gets, reported by name rather than
    /// answered as a status the door is not in.</summary>
    public const string InputStaleCode = "door-state-input-stale";
    /// <summary>The refusal an address that is not a door address gets. A terminal reference is a map object too,
    /// and reading it as a door would answer with a state the terminal never had.</summary>
    public const string InputAddressCode = "door-state-input-address";
    /// <summary>The refusal a door whose own status value is outside `eDoorStatus` gets. A value no member names
    /// is refused instead of being reported as the nearest state.</summary>
    public const string StatusCode = "door-state-status";

    /// <summary>The one shape of this handler: the door the read is about, and the eight facts it answers. The
    /// `puzzle` output is the chained puzzle the door's own lock component holds, as the one resource reference
    /// `forge.action.map.scan_state` takes: the resource id is its DataBlock definition id, the same id the scan
    /// row's chained-puzzle resource provider publishes (`ChainedPuzzleLookup.ById`, `ChainedPuzzleLookup.Enumerate`).
    /// A door whose lock holds no puzzle answers null rather than an id no provider resolves, and a door that wants
    /// no key answers null for `key`.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("door").Outputs("state", "detail", "locked", "key", "puzzle", "glued", "stuck", "is_bloody");

    /// <summary>The `door_query_state` vocabulary, in member order, because the port publishes the member index
    /// and not the name. The five states are the checklist's own and the order is the checklist's own. The set is
    /// declared here and offered to the framework and the catalog by the integration batch: this runtime refuses
    /// an enum port whose set is not registered, so the row and the set travel together.</summary>
    public static readonly string[] States = { "closed", "open", "locked", "needs_scan", "broken" };

    public const int Closed = 0, Open = 1, Locked = 2, NeedsScan = 3, Broken = 4;

    /// <summary>The five states derived from one `eDoorStatus` value, and nothing else. This is the whole
    /// derivation and the only place a native status is classified for the value row, so the row and the
    /// weak-door trigger cannot describe one door two different ways:
    ///
    /// - `Destroyed` (11) is broken.
    /// - The two unsolved-puzzle statuses (4, 5) need a scan at the door before it opens.
    /// - `Open` (10) and `Opening` (16) are open.
    /// - The locked statuses (3, 6, 7, 15) are locked: a key, a generator, no key at all, or the bulkhead controller.
    /// - Everything else — `None`, `Closed`, `Closed_BrokenCantOpen`, `ChainedPuzzleActivated`, `Unlocked`,
    ///   `GluedMax` and the two stuck states — is closed. `Unlocked` is an unlocked but still shut door, which
    ///   is closed for the author's five-state question; the exact native value stays readable through `detail`.
    ///
    /// A status outside the enum is refused rather than mapped to the nearest member.</summary>
    public static int StateOf(int status) => status switch
    {
        11 => Broken,
        4 or 5 => NeedsScan,
        10 or 16 => Open,
        3 or 6 or 7 or 15 => Locked,
        0 or 1 or 2 or 8 or 9 or 12 or 13 or 14 => Closed,
        _ => throw new RuntimeContractException(StatusCode, "Unknown door status value: " + status)
    };

    /// <summary>One door as the evaluator reads it: the native status, whether a lock holds the door, the key
    /// it currently wants, the chained puzzle definition its lock holds (empty when it holds none), and whether
    /// the level set the door up with an active enemy wave — the door the game calls a blood door. It is the same
    /// readings the door observation half publishes, handed over as a value because the game-independent assembly
    /// this contract lives in holds no game type.</summary>
    public readonly record struct DoorSample(int Status, bool Locked, string? Key, bool Bloody, string? Puzzle = null);

    /// <summary>The one read this row needs from the world: the door a reference names right now, or null when
    /// this world does not answer for it. The game-bound half supplies it; a registration that carries none
    /// answers every read with a refusal instead of a state, which is what keeps a row that cannot be read from
    /// looking like a door that read as closed.</summary>
    public sealed record DoorReaders(Func<EntityReference, DoorSample?> Read);

    /// <summary>The evaluator, keyed by handler name: what a registration adds to its evaluator table beside the
    /// selector's. The input is refused by name when it names no door this provider owns, and a door whose own
    /// status does not read is refused rather than answered with a state the door was never in.</summary>
    public static EvaluatorHandler Evaluator(DoorReaders readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return (context, outputs) => Evaluate(context, outputs, readers);
    }

    private static void Evaluate(EvaluationContext context, EvaluationOutputs outputs, DoorReaders readers)
    {
        var door = RuntimeJson.Entity(context.RequiredInput("door"));
        if (!EntityKinds.Is(door, EntityKinds.MapObject))
            throw new RuntimeContractException(InputKindCode, "This provider reads the state of a map object, not of " + door.Id);
        if (MapObjectDoorAddress.TryParse(door.Id[(EntityKinds.MapObject.Length + 1)..]) is not { } address)
            throw new RuntimeContractException(InputAddressCode, "The input is not a door address: " + door.Id);
        // A weak door serial names no door any reader resolves (doors are addressed by zone and room), so it is
        // refused as an address rather than read as a door that is not current.
        if (MapObjectDoorAddress.ParseWeakSerial(address.Key) != null)
            throw new RuntimeContractException(InputAddressCode, "A weak door serial does not select a door: " + door.Id);
        var sample = readers.Read(door) ?? throw new RuntimeContractException(InputStaleCode, "The door is not current: " + door.Id);
        int state = StateOf(sample.Status);
        // A query frame carries every output port. A door that wants no key or whose lock holds no puzzle answers
        // those two ports with an explicit null (both are nullable), never by leaving them out or refusing.
        outputs.Enum(0, state);
        // An enum port carries the member index; `door_state` lists `eDoorStatus` in value order, and StateOf
        // has already refused a status outside it.
        outputs.Enum(1, sample.Status);
        outputs.Boolean(2, sample.Locked);
        if (string.IsNullOrEmpty(sample.Key)) outputs.Null(3); else outputs.Text(3, sample.Key!);
        // The puzzle reference travels in the same spelling a resource port reads (`resourceKind` plus
        // `resourceId`), so an author wires this output straight into `scan_state`'s `scan` input.
        outputs.Resource(4, string.IsNullOrEmpty(sample.Puzzle) ? null : new ResourceRef(AlarmWaveContract.ChainedPuzzleKind, sample.Puzzle!));
        // The glue and stuck states are the door's own status values, not a second reading: they travel as
        // named fields so a plan does not have to compare `detail` against a status name. Whether the door is
        // a blood door is the level's own answer about it — an active enemy wave — and not a second state.
        outputs.Boolean(5, MapObjectDoorStatus.IsGlued(sample.Status));
        outputs.Boolean(6, MapObjectDoorStatus.IsStuck(sample.Status));
        outputs.Boolean(7, sample.Bloody);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.QueryMapDoorState.Id, HandlerName, "observe", new[] { Permission }, Shape)
    };
}
