using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `v-obj` row: the read-only state of one objective layer — where it stands, how far along its chain
/// it is, and the countdown value the objective machine holds (`forge.query.objective.state`).
///
/// **One row, and the layer it is about is an input.** The objective machine keeps exactly three fixed layer slots
/// and no per-instance objective identity (`pWardenObjectiveState` carries `main_*`, `second_*` and `third_*` with
/// no id of its own), so "the current layer's objective" is not a thing the game can answer: a layer is what the
/// author selects. The layer travels as the `objective` resource reference the level-event rows already publish
/// (`layer:main`, `layer:secondary`, `layer:third`), so a plan that reads a state and a plan that reacts to that
/// layer's events name the same place with the same text.
///
/// **Where the timer numbers come from.** One member of `pWardenObjectiveState` is published, and it is read as
/// it stands rather than derived:
///
/// - `GetStartTimeFromLayer(layer)` — published as `start_time`, in ticks like every other time this runtime
///   publishes. The accessor takes the layer, which is the strongest available confirmation that the time field
///   belongs to the objective machine's clock and not to this provider; the native field is seconds and the
///   conversion to ticks happens once, where the payload is built.
///
/// **The countdown is `remaining`, and the formula is the game's own.** `WO_Survival.OnUpdate` (RVA 0x150E930,
/// build 20403457) is the one place the objective machine turns `pWardenObjectiveState.extraTime` into a countdown;
/// read as native code, it is:
///
/// ```
/// activateDeadline = GetStartTimeFromLayer(layer) + data.Survival_TimeToActivate + state.extraTime
/// deadline         = activateDeadline > Clock.ExpeditionProgressionTime ? activateDeadline
///                                                                    : activateDeadline + data.Survival_TimeToSurvive
/// remaining        = deadline - Clock.ExpeditionProgressionTime
/// ```
///
/// with `data.Survival_TimeToActivate` at `WardenObjectiveDataBlock` +0x268 and `Survival_TimeToSurvive` at +0x26c.
/// Every term of it is a named member of the build: the start time and the extra time are the state's own, the two
/// durations are the objective's own data block, and `Clock.ExpeditionProgressionTime` is the clock the game's own
/// HUD countdown is computed against (the same static field `Clock.get_Time` reads beside it). `extraTime` is
/// therefore an **extension of the deadline**, not a remaining value: the one coroutine that writes it
/// (`WorldEventManager.<DoExcecuteEvent>d__41.MoveNext`, RVA 0x1683CD0) sets it to `GetExtraTime() + duration` for
/// the `AddToTimer` event and to zero for `ResetTimer`, and `WO_Survival` adds it to the deadline. It is one field
/// of the one state, so it is level-wide rather than per layer, which is what the game does too.
///
/// **Two kinds are not a number here.** `remaining` is declared nullable and answers the null the port declares
/// when the layer's objective type has no countdown this row can read. `Survival` is the only type whose countdown
/// the objective machine computes for a layer — `WardenObjectiveManager.UpdateFromInLevel` (the level GUI) renders
/// the same formula for the local player's own layer. `TimedTerminalSequence` does have a countdown, but not one a
/// layer can be asked about: it lives per terminal, on `TimedTerminalSequencePuzzle` (`round.StartTime` +
/// `TimedTerminalSequence_TimePerRound`, and `round.ConfirmationStartTime` + the puzzle's `ConfirmationTime`,
/// compared against the same clock), which is reached through `LG_ComputerTerminal` and is not part of
/// `pWardenObjectiveState`; `WO_TimedTerminalSequence.OnUpdate` only forwards to its source terminal. A level with
/// several timed terminals has several independent countdowns, so "the layer's remaining time" is not a quantity
/// the game has, and this row publishes no number for it rather than deriving one from an arbitrarily chosen
/// terminal. Taken together, `timed` and a null `remaining` say exactly that: a countdown exists for the type and
/// this row cannot read it at a layer, where `timed` false and a null `remaining` say the type has none at all.
///
/// `remaining` is in the row's own tick unit, converted exactly like `start_time`.
///
/// Everything else is a direct member read: the layer's status and sub-objective through
/// `GetLayerStatus` / `GetLayerSubStatus`, the chain position through `GetChainIndexForLayer`, the two flags and
/// the objective-item counters. Nothing here writes the objective machine and nothing here keeps a Forge-side
/// ledger; a field the world cannot answer refuses the whole read by name, because a state reported as zeros would
/// read exactly like an objective that really is at chain 0 with no items solved.</summary>
internal static class LevelObjectiveValueContract
{
    /// <summary>The Map package's provider id. Written here rather than read from
    /// <see cref="ModuleDefinition"/> because this file is also compiled by the focused test project, which must
    /// not drag the whole provider declaration in to build one binding id; a case asserts the two agree.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    public const string BindingId = ProviderId + ".binding.query.objective.state";
    /// <summary>The read half's own handler name. The write row of the same domain (`forge.action.map.objective_state`)
    /// already owns `gtfo.map.objective_state`, and one registration carries exactly one shape per handler name, so
    /// the value row names its read rather than sharing a name with a handler whose ports are another row's. The
    /// vocabulary is the read permission's: `objective.read`.</summary>
    public const string HandlerName = "gtfo.map.objective_read";

    /// <summary>The permission the read needs: the same objective namespace read the level-event observation rows
    /// already declare, and no write permission, because this row writes nothing.</summary>
    public const string Permission = LevelEventContract.ObjectiveReadPermission;

    /// <summary>The input port: the layer whose objective is read, as the `objective` resource reference.</summary>
    public const string InputPort = "objective";

    /// <summary>The refusal an input of another resource kind gets. The reference names a place, and a map or a
    /// zone reference is not one this provider can read an objective from.</summary>
    public const string InputKindCode = "objective-state-input-kind";
    /// <summary>The refusal an input that is not a layer reference gets: the machine has three layers and no
    /// objective identity of its own, so an id the provider cannot spell as a layer names nothing.</summary>
    public const string InputLayerCode = "objective-state-input-layer";
    /// <summary>The refusal a layer this build has no objective data for gets, read from the game's own
    /// `HasWardenObjectiveDataForLayer` rather than from a Forge-side table.</summary>
    public const string NoObjectiveCode = "objective-state-no-objective";
    /// <summary>The refusal a state the process cannot read gets. It is one code for the whole read because
    /// `pWardenObjectiveState` is one value: a state that cannot be read has no fields to report individually.</summary>
    public const string UnavailableCode = "objective-state-unavailable";

    /// <summary>The one shape of this handler: the layer it reads and the facts it answers, in the same
    /// order the reviewed behavior-operator row declares them. Declared here so the native read layer cannot
    /// describe a second layout; the registration composes this into its shape table.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs(InputPort, "terminal")
        .Outputs("kind", "timed", "phase", "sub_phase", "chain_index", "start_time",
            "solve_on_death", "exit_wave_triggered", "items_solved", "required_items", "remaining", "progress", "round", "connected_terminal");
    private static readonly HandlerSlot LayerSlot = Shape.InputSlot(InputPort);
    private static readonly HandlerSlot TerminalSlot = Shape.InputSlot("terminal");

    /// <summary>The three layer names, in the machine's own slot order. It is the level-event contract's own list
    /// and not a second one: one package spells a layer one way, and a case asserts the two agree.</summary>
    public static IReadOnlyList<string> Layers => LevelEventContract.Layers;

    /// <summary>The resource kind and schema of the input port, the same pair the level-event rows publish an
    /// objective under. Spelled as constants because this contract compiles without the trigger family beside it,
    /// and asserted against the level-event rows by a case. The kind is <see cref="ObjectiveLayerReference"/>'s
    /// own, so a reference's kind and its id grammar have one spelling.</summary>
    public const string ObjectiveResourceKind = ObjectiveLayerReference.Kind;

    /// <summary>The layer reference prefix the level-event rows publish an objective under. It is
    /// <see cref="ObjectiveLayerReference"/>'s own prefix, so the one grammar has one spelling.</summary>
    public const string LayerReferencePrefix = ObjectiveLayerReference.Prefix;

    /// <summary>`eWardenObjectiveStatus`'s members with their own values, in member order. The `phase` port publishes the
    /// member's index in the `objective_status` set (<see cref="StatusMember"/>): the enum's values are not an ordinal run (0, 10, 20, 30, 40), so an index-shaped
    /// vocabulary would have to invent the gaps the game does not have.</summary>
    public static readonly IReadOnlyList<(int Value, string Name)> Statuses = Array.AsReadOnly(new[]
    {
        (0, "not_discovered"),
        (10, "discovered"),
        (20, "started"),
        (30, "partially_solved"),
        (40, "item_solved")
    });

    /// <summary>The `sub_phase` and `kind` ports publish native `eWardenSubObjectiveStatus` and
    /// `eWardenObjectiveType` values as their index in the `objective_sub_status` and `objective_type` sets, which
    /// list the members in the enums' own order, "help" sub-steps included.</summary>
    private static readonly int SubStatusCount = RuntimeEnumSets.Members("objective_sub_status").Count;

    /// <summary>The two objective types the game builds a countdown for. `timed` is derived from the type alone:
    /// `Survival` runs on its own `Survival_TimeToActivate` / `Survival_TimeToSurvive`, which `remaining` reads,
    /// and `TimedTerminalSequence` on the terminal sequence's own per-round timers, which are not a layer's
    /// quantity and which `remaining` therefore answers as null. No other type has a timer at all.</summary>
    public const int SurvivalKind = 11, TimedTerminalSequenceKind = 15;

    public static bool IsTimed(int kind) => kind == SurvivalKind || kind == TimedTerminalSequenceKind;

    /// <summary>The layer name of an `objective` reference id, or null when the id is not one this package
    /// writes. The grammar is <see cref="ObjectiveLayerReference"/>'s and the layer list is this contract's own,
    /// so "layer:secondary" is a layer reference wherever it appears and "layer:fourth" is not.</summary>
    public static string? LayerOf(string? referenceId)
    {
        if (ObjectiveLayerReference.LayerOf(referenceId) is not { } layer) return null;
        foreach (var known in Layers) if (string.Equals(known, layer, StringComparison.Ordinal)) return known;
        return null;
    }

    /// <summary>The member name one `eWardenObjectiveStatus` value spells, for the journal and the evidence. A
    /// value outside the five members is a refusal at the contract boundary, because reporting it as the nearest
    /// member would describe an objective state the game never had.</summary>
    public static string StatusName(int value)
        => TryStatusName(value, out var name)
            ? name
            : throw new RuntimeContractException("objective-status-unknown", "Not an eWardenObjectiveStatus value: " + value);

    /// <summary>The wire value the `phase` port publishes for one `eWardenObjectiveStatus` value: the member's index
    /// in the `objective_status` enum set (the kernel refuses a member name as `invalid-enum`), resolved from the
    /// SDK's own set so the order is spelled once. The game values 0, 10..40 are not the index.</summary>
    public static int StatusMember(int value)
    {
        var name = StatusName(value);
        var index = StatusMemberOf(name);
        return index >= 0 ? index : throw new RuntimeContractException("objective-status-unknown", "Not an objective_status member: " + name);
    }

    /// <summary>The `objective_status` index of one status name, or -1 for a name outside the set.</summary>
    public static int StatusMemberOf(string? name)
    {
        var members = RuntimeEnumSets.Members("objective_status");
        for (var index = 0; index < members.Count; index++)
            if (string.Equals(members[index], name, StringComparison.Ordinal)) return index;
        return -1;
    }

    /// <summary>The same reading without the refusal, for a caller that has to decide whether it may publish at
    /// all rather than answer a plan: the objective trigger rows read a status on every state change, and a value
    /// outside the five members must publish nothing instead of faulting the session that owns the hook.</summary>
    public static bool TryStatusName(int value, out string name)
    {
        foreach (var (member, memberName) in Statuses)
            if (member == value) { name = memberName; return true; }
        name = "";
        return false;
    }

    /// <summary>One sub-objective value as its set member, or a refusal for a value past the enum's own last member.</summary>
    public static int SubStatusMember(int value) => value >= 0 && value < SubStatusCount
        ? value
        : throw new RuntimeContractException("objective-sub-status-unknown", "Not an eWardenSubObjectiveStatus value: " + value);

    /// <summary>What one layer's own countdown is, in the three states the world can be in. `Absent` is a type the
    /// objective machine computes no countdown for at all; `Running` carries the seconds the game's own formula
    /// answers; `Unreadable` is a type whose countdown exists but is not a quantity this row can ask a layer
    /// for — `TimedTerminalSequence`, whose countdown is per terminal. Anything but `Running` is published as the
    /// null `remaining` declares, so no state is ever reported as a number the world did not answer.</summary>
    public enum CountdownReading { Absent, Running, Unreadable }

    /// <summary>One objective layer exactly as the native read found it. It is handed over as a value because the
    /// game-independent assembly this contract lives in holds no game type, and it holds only fields the read
    /// really made: the caller that cannot read the state answers null instead of filling this in.</summary>
    public readonly record struct LayerSample(
        int Kind, int Status, int SubStatus, int ChainIndex, float StartTime,
        bool SolveOnDeath, bool ExitWaveTriggered, int ItemsSolved, int RequiredItems,
        CountdownReading Countdown, float RemainingSeconds);

    /// <summary>The one read this row needs from the world: the objective state of one layer, or null when this
    /// process cannot read it. The game-bound half supplies it; a registration that carries none answers every
    /// read with a refusal instead of a state, which is what keeps a row that cannot be read from looking like an
    /// objective that reads as not discovered.</summary>
    public sealed record TerminalSample(string Progress, string? Round);
    public sealed record LayerReader(Func<string, LayerSample?> ReadLayer,
        Func<EntityReference, string, long, TerminalSample?>? ReadTerminal = null,
        Func<string, long, EntityReference?>? ReadConnectedTerminal = null);

    /// <summary>The evaluator, keyed by handler name: what a registration adds to its evaluator table beside the
    /// selectors'. The input is refused by name when it is not a layer reference of this package, and a layer the
    /// objective machine has no objective for is refused rather than answered with zeros.</summary>
    public static EvaluatorHandler Evaluator(LayerReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) => Evaluate(context, outputs, reader);
    }

    private static void Evaluate(EvaluationContext context, EvaluationOutputs outputs, LayerReader reader)
    {
        // The typed frame hands over the reference decoded; only a compiled literal in its id-only spelling is read from the document.
        var layer = LayerOf(ObjectiveLayerReference.InputId(context, LayerSlot, InputPort, InputKindCode));
        if (layer == null)
            throw new RuntimeContractException(InputLayerCode, "The input is not one of this level's objective layers.");
        var sample = reader.ReadLayer(layer)
            ?? throw new RuntimeContractException(NoObjectiveCode, "This layer has no objective this process can read: " + layer);
        // The status is validated before the payload is built, so a value outside the vocabulary is refused rather
        // than shipped as an integer the author's own switch would then have to guess about.
        int phase = StatusMember(sample.Status);
        int subPhase = SubStatusMember(sample.SubStatus);
        // The countdown is the row's one nullable value: a type with no countdown, and a type whose countdown is
        // per terminal, both answer the null the port declares rather than a zero or a number derived from
        // somewhere else. `timed` beside it says which of the two a reader is looking at.
        double? remaining = sample.Countdown == CountdownReading.Running ? Ticks(context, sample.RemainingSeconds) : null;
        TerminalSample? terminal = null;
        if (context.TryEntity(TerminalSlot, out var terminalEntity))
        {
            var read = reader.ReadTerminal ?? throw new RuntimeContractException("terminal-reader-unavailable", "terminal");
            terminal = read(terminalEntity, layer, context.Query.WorldEpoch);
        }
        var connected = reader.ReadConnectedTerminal is { } readConnection
            ? readConnection(layer, context.Query.WorldEpoch)
            : throw new RuntimeContractException(UnavailableCode, "The connected terminal reader is unavailable.");
        outputs.Enum(0, sample.Kind);
        outputs.Boolean(1, IsTimed(sample.Kind));
        outputs.Enum(2, phase);
        outputs.Enum(3, subPhase);
        outputs.Integer(4, sample.ChainIndex);
        outputs.Number(5, Ticks(context, sample.StartTime));
        outputs.Boolean(6, sample.SolveOnDeath);
        outputs.Boolean(7, sample.ExitWaveTriggered);
        outputs.Integer(8, sample.ItemsSolved);
        outputs.Integer(9, sample.RequiredItems);
        if (remaining is { } seconds) outputs.Number(10, seconds); else outputs.Null(10);
        if (terminal?.Progress is { } progress) outputs.Text(11, progress); else outputs.Null(11);
        if (terminal?.Round is { } round) outputs.Text(12, round); else outputs.Null(12);
        outputs.Entity(13, connected);
    }

    /// <summary>One native time in the ticks this runtime publishes times in. The native field is seconds, and the
    /// kernel's tick is the level's own simulation step, whose rate the host reported for this world; a world that
    /// reported none refuses the read by name rather than being converted at a rate nobody declared. `remaining` and
    /// `start_time` are converted by this one function, so the row cannot publish two time bases.</summary>
    private static double Ticks(EvaluationContext context, double seconds) => seconds * context.TicksPerSecond;

    /// <summary>The one evaluator, keyed by handler name. The game-bound half supplies the read; this contract
    /// owns the refusals, so a registration that carries none is a refusal by name and not a state of zeros.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(LayerReader reader)
        => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal) { [HandlerName] = Evaluator(reader) };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.QueryObjectiveState.Id, HandlerName, "observe", new[] { Permission }, Shape)
    };
}
