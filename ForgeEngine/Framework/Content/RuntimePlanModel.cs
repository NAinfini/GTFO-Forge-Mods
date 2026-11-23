using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Where one resolved step input's value comes from at dispatch: a literal, a wired trigger event output
/// slot, or the value frame another step published within the same entrypoint.</summary>
internal readonly record struct StepSlotRef(int Step, int Port);

/// <summary>The four step kinds a compiled entry holds.</summary>
internal enum StepKind { Action, Control, Query, Pure }

/// <summary>One resolved input, exactly one of <see cref="Literal"/>, <see cref="EventPort"/> or
/// <see cref="FromStep"/> is set. <see cref="Port"/> is the wire-side value contract used to validate the value at
/// dispatch. <see cref="Wrap"/> marks a non-nullable "one" output wired into a "many" input.
/// <see cref="Literal"/> is the value the plan document compiled, which is also what the frame's constant pool
/// encodes. Where the document's spelling of a reference is not the value a handler reads — a resource, whose
/// document form is `{id, revision}` and whose runtime form is `{resourceKind, resourceId}` — the loader resolves
/// the runtime form once at load into <see cref="RuntimeLiteral"/>, and every dispatch reads
/// <see cref="HandlerLiteral"/> instead of translating the literal per tick.</summary>
internal sealed record StepInput(string Name, string? EventPort, JsonElement? Literal, StepSlotRef? FromStep, bool Wrap, JsonElement Port)
{
    /// <summary>The canonical runtime form of this input's literal, or null where the compiled literal already is
    /// the value a handler reads. Written once, when the plan is loaded; never derived at dispatch.</summary>
    internal JsonElement? RuntimeLiteral { get; init; }
    /// <summary>The owner's own answer for a resource literal, resolved with <see cref="RuntimeLiteral"/> at load. A
    /// step that moves the reference without handing it to a handler (a variable write) reads this instead of
    /// resolving the reference again.</summary>
    internal ResourceRef? ResolvedLiteral { get; init; }
    /// <summary>The declared resource kind's owner lookup, bound while the plan loads. A retained frame re-asks this
    /// exact owner without routing the kind through the registry again.</summary>
    internal Func<string, ResourceRef?>? ResourceResolver { get; set; }
    /// <summary>The literal a handler is handed: the resolved runtime form where the loader produced one, the
    /// compiled literal itself otherwise. Null when this input is not a literal at all.</summary>
    internal JsonElement? HandlerLiteral => RuntimeLiteral ?? Literal;
    /// <summary>The port contract as dispatch reads it: typed once here, so a value is checked against a kind and a
    /// set of flags rather than against field names.</summary>
    internal PortView View { get; } = PortView.Of(Port);
    /// <summary>Whether the value is a result row, read through the activation's typed region.</summary>
    internal bool ResultPort => View.Type == "result";
    /// <summary>For an input wired to an event output: the event cell that carries it (-1 where the registered
    /// trigger declares no such output), and whether that cell is the very cell this input's handler is handed —
    /// both resolved by the kernel when the plan is loaded.</summary>
    internal int EventCell { get; set; } = -1;
    internal bool EventCellDecoded { get; set; }
}

/// <summary>The checks a promoted parameter adds to its already typed input port. The port owns kind/null/enum
/// validation; the parameter adds its required presence and numeric bounds. All definition reads happen at load,
/// so one dispatch compares values and numeric limits only.</summary>
internal readonly record struct PromotedParameterRule(int Slot, string Name, bool Required,
    bool HasMinimum, double Minimum, bool HasMaximum, double Maximum)
{
    internal void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            RuntimeJson.Require(!Required, "missing-field", Name);
            return;
        }
        if (value.ValueKind != JsonValueKind.Number) return;
        var number = value.GetDouble();
        if (HasMinimum) RuntimeJson.Require(number >= Minimum, "parameter-minimum", Name);
        if (HasMaximum) RuntimeJson.Require(number <= Maximum, "parameter-maximum", Name);
    }
}

/// <summary>One resolved step of a compiled entrypoint: <see cref="NodeKind"/> is `action`, `control`, `query` or
/// `pure`. <see cref="Successors"/> is empty for `pure`/`query` steps; for `action` it is 0 or 1 entries; for
/// `control` it has one entry per execution output, in declaration order, each either a step index within the same
/// entrypoint or null (path ends) — `pulse`/`body` included, since a region output names where the next activation
/// starts. <see cref="Control"/> is the control capability id the kernel dispatches by, or null elsewhere;
/// <see cref="ValuePort"/> is the output port id a when-present step publishes the value it tested under, or null
/// on every other step.
/// <see cref="Contract"/> is the resolved graph (inputs/outputs) used both to validate this step's own inputs and,
/// when this step is `pure`/`query`/`control`/`action`, to validate and address its outputs for downstream
/// `fromStepSlot` readers. <see cref="BindingIndex"/>/<see cref="HandlerIndex"/> are the plan's pin position and the kernel's
/// handler position; <see cref="Frames"/> is this step's slot table, built once with the plan (see
/// <see cref="PlanFrames"/>). <see cref="ProviderId"/>, <see cref="CapabilityId"/> and <see cref="Execution"/> are the
/// binding's own registration facts, read here because the plan is loaded against a frozen registry and the binding's
/// provider cannot change while the plan is live: a plan of an unregistered module is dropped with it, so the tier and
/// the owner are decided once instead of at every dispatch. <see cref="CommandSuffix"/> is the second half of this
/// step's command identity — the plan and the node, quoted by the JSON encoder — so a dispatch composes the identity
/// from two strings instead of serializing a four-element array per step.</summary>
/// <summary>One step's own duration options, resolved once at load: null where the card declared no `effect`
/// block, in which case the action is the fire-and-forget write it always was.</summary>
internal sealed record ResolvedStep(string NodeId, string NodeKind, string BindingId, int BindingIndex, int HandlerIndex,
    JsonElement Parameters, IReadOnlyList<StepInput> Inputs, IReadOnlySet<string> Promoted, IReadOnlyList<int?> Successors,
    JsonElement Contract, StepFrames Frames, JsonElement[] ParameterDefinitions, string ProviderId, string CapabilityId,
    string Execution, string CommandSuffix,
    string? Control = null, string? ValuePort = null, EffectOptions? Effect = null, CostOptions? Cost = null,
    LoopShape Loop = LoopShape.None)
{
    /// <summary><see cref="NodeKind"/> as the walk compares it, decided once at load.</summary>
    internal StepKind Kind { get; } = NodeKind switch
    {
        "action" => StepKind.Action,
        "control" => StepKind.Control,
        "query" => StepKind.Query,
        "pure" => StepKind.Pure,
        _ => throw new RuntimeContractException("node-kind", NodeKind)
    };
    /// <summary>An action whose binding executes as a presentation, decided once at load.</summary>
    internal bool PresentationTier { get; } = NodeKind == "action" && Execution == "presentation";
    internal VariableDeclaration? Variable { get; set; }
    /// <summary>What a presentation handler keeps for this one step while the plan is loaded (<see cref="CommandContext.StepState"/>):
    /// made once on the step's first command, read as a field after that, dropped with the plan.</summary>
    internal object? HandlerState { get; set; }
    /// <summary>For a step that holds plays (<see cref="HoldsPlay"/>), the synchronous loops (`repeat` without an interval,
    /// `for_each`) it runs inside, outermost first, resolved at load: their current rounds name the play its command
    /// carries. Empty outside a loop and on every other step.</summary>
    internal int[] PlayLoops { get; set; } = Array.Empty<int>();
    /// <summary>A presentation step whose handler keeps something per play (its shape says so, <c>HandlerShape.KeyedByPlay</c>):
    /// the host names the play on its command (`$play`) and the recipient requires it; every other presentation step's
    /// command carries none and is refused if it does. Decided at load from the frozen registry, the same on both sides.</summary>
    internal bool HoldsPlay { get; init; }
    internal int VariableInput { get; set; } = -1;
    internal int SubjectInput { get; set; } = -1;
    internal int WeaponSlotInput { get; set; } = -1;
    /// <summary>The fields of a `log.record` step, decoded once at plan load; null on every other step.</summary>
    internal RuntimeLogValue[]? RecordValues { get; set; }
    internal int ValueOutput { get; set; } = -1;
    internal int PreviousOutput { get; set; } = -1;
    internal int WrittenOutput { get; set; } = -1;
    internal RuntimeKernel.StepInputs? StoredInputs { get; set; }
    /// <summary>A presentation step's second frame, for the recipient command and recipient-state reads that decode
    /// the wire form while <see cref="StoredInputs"/> may still hold the dispatch; null on every other step.</summary>
    internal RuntimeKernel.StepInputs? BoundaryInputs { get; set; }
    /// <summary><see cref="Successors"/> with a missing edge as -1, resolved at plan load and shared read-only by every
    /// control frame of this step, so entering a control step does not copy it.</summary>
    internal int[] ControlSuccessors { get; set; } = Array.Empty<int>();
    /// <summary>A control step's input cells by <see cref="RuntimeKernel.ControlPort"/>, resolved at plan load (-1 where
    /// the step has no such input), so the control dispatch reads its frame by position and writes no input document.</summary>
    internal int[] ControlInputs { get; set; } = Array.Empty<int>();
    internal int EntityMemoBase { get; set; }
    internal int ResultRecipientInput { get; set; } = -1;
    /// <summary>The answer sheet a handler of this step writes its side outputs on, by the contract's output
    /// positions (what the generated <c>Out…</c> constants index); made the first time the step is armed.</summary>
    internal EvaluationOutputs? OutputSheet { get; set; }
    /// <summary>The step's recipients are a set (cardinality `many`), read from its contract once at load.</summary>
    internal bool ManyRecipients { get; set; }
    internal JsonElement[]? StoredOutputs { get; set; }
    /// <summary>Beside each stored output: the owner-resolved resource reference it carries, or null. A variable read
    /// publishes the reference the cell already holds resolved, so the step that consumes it reads it without a
    /// lookup, a parse or an allocation.</summary>
    internal ResourceRef?[]? StoredResolved { get; set; }
    internal long OutputActivation { get; set; } = -1;
    /// <summary>Entry-local slots invalidated by a new synchronous loop round: its reachable body plus all
    /// demand-evaluated observations. Resolved once at load; outer execution snapshots are not in this table.</summary>
    internal int[] RoundResetSteps { get; set; } = Array.Empty<int>();
    /// <summary>The capability the step's binding implements, as the registry held it when the plan loaded.</summary>
    internal JsonElement Capability { get; init; }
    internal bool ProducesSound { get; } = RuntimeJson.Rows(Contract, "outputs")
        .Any(port => port.TryGetProperty("id", out var id) && id.GetString() == "sound"
            && port.TryGetProperty("handleKind", out var kind) && kind.GetString() == "sound");
    /// <summary>An unused playback output needs no retained instance. Resolve its readers at load so ordinary
    /// one-shot audio does not consume a handle and dedicated emitter until the encounter ends.</summary>
    internal bool TracksSound { get; init; }
    /// <summary>The immediate execution successor explicitly decides on this action's own result through the
    /// canonical reader. Derived at load, never supplied by the plan or inferred from a later unrelated read.</summary>
    internal bool HasResultDecision { get; init; }
    /// <summary>Entry-local evaluated/control frames derived from this action's result. Re-entering the action
    /// invalidates only this dependency chain; unrelated activation-local observations retain their memo.</summary>
    internal int[] ResultDerivedSteps { get; init; } = Array.Empty<int>();
    internal bool StopsSoundInstance { get; } = CapabilityId == Blocks.ActionPresentationAudioStop.Id;
    internal JsonElement SoundInputPort { get; } = RuntimeJson.Rows(Contract, "inputs")
        .FirstOrDefault(port => port.TryGetProperty("id", out var id) && id.GetString() == "instance");
    /// <summary>Whether the step hands back a handle: such a command owes a cancel a later answer cannot yet name,
    /// so it neither answers later nor is split between holders.</summary>
    internal bool ReturnsHandle { get; } = RuntimeJson.Rows(Contract, "outputs").Any(port => port.TryGetProperty("handleKind", out _));
    /// <summary>The registered handler of the step's binding, or null where the binding registered none.</summary>
    internal CommandHandler? Handler { get; init; }
    internal EvaluatorHandler? Evaluator { get; init; }
    /// <summary>The execution tier resolved against the frozen registry at plan load. Owner-session adapters may
    /// promote a host-declared action to the owner tier, so the declaration string alone is not the answer.</summary>
    internal bool OwnerTier { get; init; }
    /// <summary>The provider's mutable log-level cell, captured at load. Elevation and unregistration mutate this
    /// same object, so a dispatch reads one field instead of resolving the provider name on every invocation.</summary>
    internal RuntimeLogGate LogGate { get; init; } = null!;
    internal IReadOnlyList<string> RequiredActorRoles { get; init; } = Array.Empty<string>();
    /// <summary>The parameters a handler reads for a step that promotes none: every enum index already resolved to
    /// its member name, once. Undefined where the step promotes a parameter, whose value is only known per dispatch.</summary>
    internal JsonElement HandlerParameters { get; init; }
    /// <summary>The compiled parameter constants in declaration order, decoded once at plan load. Promoted and
    /// omitted optional parameters are Missing cells and are filled only in the command instance that supplies them.</summary>
    internal CommandValue[] ParameterValues { get; init; } = Array.Empty<CommandValue>();
    /// <summary>Handler-ready literal inputs in authored input order. They are decoded once with the plan; dynamic
    /// event/from-step inputs leave a Missing cell and are decoded only when that command is dispatched.</summary>
    internal CommandValue[] InputConstantValues { get; init; } = Array.Empty<CommandValue>();
    /// <summary>The input port the capability's `recipients` declaration names, or null where it declares none
    /// readable.</summary>
    internal string? RecipientsInput { get; init; }
    internal int RecipientsInputSlot { get; set; } = -1;
    internal bool[] PresentationConstants { get; set; } = Array.Empty<bool>();
    /// <summary>Every value input a handler may read, in contract order. Optional unwired ports stay in this table
    /// with a Missing cell, which is what makes their offset a property of the contract rather than the wiring.</summary>
    internal string[] HandlerInputs { get; } = HandlerInputNames(Contract, Promoted);
    /// <summary>The physical frame offset paired with each <see cref="HandlerInputs"/> name.</summary>
    internal int[] HandlerInputOffsets { get; } = HandlerInputOffsetsOf(Contract, Frames, Promoted);
    /// <summary>A load-time physical-offset to dense-cell table. Reserved vector/collection tail slots map to -1;
    /// one command allocates only one typed cell per value port, never one per reserved physical slot.</summary>
    internal int[] HandlerInputDenseMap { get; } = HandlerInputDenseMapOf(Contract, Frames, Promoted);
    internal int HandlerInputCount => HandlerInputs.Length;
    /// <summary>Per resolved plan input, its dense command-cell index. A negative value encodes the declaration
    /// index of a promoted parameter as <c>-1-index</c>.</summary>
    internal int[] InputSlots { get; } = HandlerInputSlots(Inputs, Contract, Frames, Promoted, ParameterDefinitions);
    internal ValueKind[] HandlerParameterKinds { get; } = ParameterDefinitions.Select(ParameterKind).ToArray();
    internal int[] ParameterOverrideMap { get; } = ParameterOverrideMapOf(ParameterDefinitions, Promoted);
    internal int ParameterOverrideCount => Promoted.Count;
    /// <summary>Parameter names in declaration order, used only if a handler or wire boundary asks for a JSON bag.</summary>
    internal string[] HandlerParameterNames { get; } = ParameterDefinitions.Select(ParameterName).ToArray();
    /// <summary>Static parameters first, then promoted values, matching the former eager merge's stable field order.</summary>
    internal int[] HandlerParameterJsonOrder { get; } = ParameterJsonOrderOf(ParameterDefinitions, Promoted);
    /// <summary>Promoted-only required/bounds checks in declaration order. Type and enum membership are the
    /// corresponding pre-resolved input port's rule.</summary>
    internal PromotedParameterRule[] PromotedParameterRules { get; } = PromotedRulesOf(ParameterDefinitions, Promoted);
    /// <summary>The value outputs of a `pure`/`query` step as typed ports, with their ids in contract order: what
    /// the frame it publishes is checked against. Null for every other step kind.</summary>
    internal PortView[]? OutputViews { get; } = NodeKind is "pure" or "query" && Contract.TryGetProperty("outputs", out _)
        ? PortView.Of(RuntimeJson.Rows(Contract, "outputs")) : null;
    /// <summary>The cells a `pure`/`query` step's evaluator writes into, one per <see cref="OutputViews"/> port, owned
    /// by the step and refilled by each evaluation. What they hold is this activation's value only while
    /// <see cref="EvaluatedActivation"/> names it and the kernel's validated-slot table still holds the step.</summary>
    internal EvaluationOutputs? Evaluated { get; set; }
    internal long EvaluatedActivation { get; set; } = -1;
    /// <summary>The live references each entity output carried when it was evaluated, per output, kept while the
    /// evaluation answers the same ones.</summary>
    internal RuntimeKernel.EntityCheck[]?[]? EvaluatedEntities { get; set; }
    /// <summary>The context every evaluation of this step is handed, refilled each time.</summary>
    internal EvaluationContext? Context { get; set; }

    internal static ValueKind ParameterKind(JsonElement definition) => RuntimeJson.Text(definition, "type") switch
    {
        "boolean" => ValueKind.Boolean,
        "integer" => ValueKind.Integer,
        "number" => ValueKind.Number,
        "string" or "enum" or "object-address" => ValueKind.String,
        "vector3" => ValueKind.Vector3,
        "resource" => ValueKind.Resource,
        _ => ValueKind.Missing
    };

    private static string ParameterName(JsonElement definition) => RuntimeJson.Text(definition, "id");

    private static string[] HandlerInputNames(JsonElement contract, IReadOnlySet<string> promoted)
    {
        var ports = RuntimeJson.Rows(contract, "inputs");
        var names = new List<string>(ports.Length);
        foreach (var port in ports)
        {
            var name = RuntimeJson.Text(port, "id");
            if (!promoted.Contains(name) && RuntimeGraphContracts.FramePort(port).Width > 0) names.Add(name);
        }
        return names.ToArray();
    }

    private static int[] HandlerInputOffsetsOf(JsonElement contract, StepFrames frames, IReadOnlySet<string> promoted)
    {
        var ports = RuntimeJson.Rows(contract, "inputs");
        var slots = new List<int>(ports.Length);
        for (var index = 0; index < ports.Length; index++)
        {
            var name = RuntimeJson.Text(ports[index], "id");
            if (!promoted.Contains(name) && frames.Inputs[index].Width > 0)
                slots.Add(frames.Inputs[index].Slot - frames.InputBase);
        }
        return slots.ToArray();
    }

    private static int[] HandlerInputSlots(IReadOnlyList<StepInput> inputs, JsonElement contract, StepFrames frames,
        IReadOnlySet<string> promoted, JsonElement[] parameters)
    {
        var ports = RuntimeJson.Rows(contract, "inputs");
        var dense = 0;
        var denseByPort = new int[ports.Length];
        Array.Fill(denseByPort, -1);
        for (var index = 0; index < ports.Length; index++)
        {
            var name = RuntimeJson.Text(ports[index], "id");
            if (!promoted.Contains(name) && frames.Inputs[index].Width > 0) denseByPort[index] = dense++;
        }
        var slots = new int[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            var name = inputs[index].Name;
            if (promoted.Contains(name))
            {
                var parameter = Array.FindIndex(parameters, definition => RuntimeJson.Text(definition, "id") == name);
                RuntimeJson.Require(parameter >= 0, "promotion-frame", name);
                slots[index] = -1 - parameter;
                continue;
            }
            var port = Array.FindIndex(ports, candidate => RuntimeJson.Text(candidate, "id") == name);
            RuntimeJson.Require(port >= 0 && denseByPort[port] >= 0, "input-frame", name);
            slots[index] = denseByPort[port];
        }
        return slots;
    }

    private static int[] HandlerInputDenseMapOf(JsonElement contract, StepFrames frames, IReadOnlySet<string> promoted)
    {
        var width = 0;
        foreach (var input in frames.Inputs)
            if (input.Slot >= frames.InputBase) width = Math.Max(width, input.Slot - frames.InputBase + input.Width);
        var map = new int[width];
        Array.Fill(map, -1);
        var ports = RuntimeJson.Rows(contract, "inputs");
        var dense = 0;
        for (var index = 0; index < ports.Length; index++)
        {
            var name = RuntimeJson.Text(ports[index], "id");
            if (promoted.Contains(name) || frames.Inputs[index].Width <= 0) continue;
            map[frames.Inputs[index].Slot - frames.InputBase] = dense++;
        }
        return map;
    }

    private static int[] ParameterOverrideMapOf(JsonElement[] definitions, IReadOnlySet<string> promoted)
    {
        var map = new int[definitions.Length];
        Array.Fill(map, -1);
        var next = 0;
        for (var index = 0; index < definitions.Length; index++)
            if (promoted.Contains(RuntimeJson.Text(definitions[index], "id"))) map[index] = next++;
        return map;
    }

    private static int[] ParameterJsonOrderOf(JsonElement[] definitions, IReadOnlySet<string> promoted)
    {
        var order = new int[definitions.Length];
        var next = 0;
        for (var index = 0; index < definitions.Length; index++)
            if (!promoted.Contains(ParameterName(definitions[index]))) order[next++] = index;
        for (var index = 0; index < definitions.Length; index++)
            if (promoted.Contains(ParameterName(definitions[index]))) order[next++] = index;
        return order;
    }

    private static PromotedParameterRule[] PromotedRulesOf(JsonElement[] definitions, IReadOnlySet<string> promoted)
    {
        var rules = new List<PromotedParameterRule>(promoted.Count);
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var name = ParameterName(definition);
            if (!promoted.Contains(name)) continue;
            var hasMinimum = definition.TryGetProperty("minimum", out var minimum);
            var hasMaximum = definition.TryGetProperty("maximum", out var maximum);
            rules.Add(new PromotedParameterRule(index, name, RuntimeJson.Flag(definition, "required"),
                hasMinimum, hasMinimum ? minimum.GetDouble() : 0,
                hasMaximum, hasMaximum ? maximum.GetDouble() : 0));
        }
        return rules.ToArray();
    }
}

/// <summary>The shape a `repeat` or a `for_each` step runs in, decided once at load by which of its inputs the plan
/// filled and by its own structural options, so dispatch reads flags instead of asking the step's inputs or
/// parameters by name. The website states the same rules in `control-lowering.ts` (`requireControlMode`) and in the
/// `for_each` input pair of `graph.ts`.</summary>
[Flags]
internal enum LoopShape : byte
{
    None = 0,
    /// <summary>A `repeat` with an `interval`: a pulse series the one scheduler owns — `next` runs now and every
    /// pulse enters `body` as its own activation — with the series' cancellable `timer`.</summary>
    Scheduled = 1,
    /// <summary>A scheduled `repeat` whose first pulse waits one interval (`first_pulse=after_interval`).</summary>
    AfterInterval = 2,
    /// <summary>A `for_each` that walks `positions` and publishes `position`, rather than `candidates` and `item`.</summary>
    Positions = 4,
    /// <summary>A `for_each` whose list starts over from its top when it runs out before the budget
    /// (`shortfall=repeat`).</summary>
    Cycle = 8
}

/// <summary>One dispatch filter a trigger's structural enum parameter declares: the event output port that must
/// carry the same member for the entry to receive the event, and the member's index in that port's own set. It is
/// resolved once at load, so dispatch compares one integer and never a member name.</summary>
internal sealed record TriggerEnumFilter(string Port, int Member)
{
    /// <summary>The event cell that carries <see cref="Port"/>, resolved by the kernel when the plan is loaded.</summary>
    internal int Cell { get; set; } = -1;
}
internal sealed record EventCommandClaim(string? Address, int Steps, string? ResourceScope = null,
    IReadOnlyList<TriggerEnumFilter>? EnumFilters = null);

/// <summary>One compiled entrypoint: dispatch starts at <see cref="Start"/> (an index into <see cref="Steps"/>,
/// always an `action` or `control` step) and follows `successors`; `pure` steps are read on demand, never dispatched.
/// <see cref="TriggerContract"/> is the entry's trigger graph: its outputs are the event payload frame's ports.
/// <see cref="Scope"/> is the map object this entry's trigger is attached to, as the canonical address the plan
/// carries, or null for a trigger attached to no one object (rule 143.11): an entry with a scope is dispatched only
/// for events whose own subject is that object, and an entry without one for every event of its binding.
/// <see cref="Gate"/> is the entry's own trigger options, resolved once at load: null where the card declares none,
/// in which case every dispatch the entry claims is walked. <see cref="EnumFilters"/> are the dispatch filters the
/// trigger's structural enum parameters declare, resolved once at load; <see cref="EnumParameters"/> is the same
/// set of parameters by name, which the subscription table reads.</summary>
internal sealed record ResolvedEntry(string NodeId, string BindingId, int Start, IReadOnlyList<ResolvedStep> Steps, JsonElement TriggerContract,
    string? Scope = null, TriggerGateOptions? Gate = null, IReadOnlyDictionary<string, string>? EnumParameters = null,
    IReadOnlyList<TriggerEnumFilter>? EnumFilters = null, string? ResourceScope = null, string? MessageName = null)
{
    // MessageName is the one custom message a `message_received` entry listens for, resolved and validated once at
    // load: the subscription indexes it, so a message is claimed only by the entries that named it. Every other
    // trigger carries null.
    /// <summary>Upper bound on commands one dispatch of this entry can produce: only `action`/`control` steps are
    /// ever placed on the walked path; `pure` and `query` steps are read on demand and never appear in
    /// <see cref="RuntimeKernel"/>'s command budgets.</summary>
    internal int DispatchableStepCount { get; } = Steps.Count(s => s.NodeKind is not ("pure" or "query"));
    /// <summary>The trigger's own output ports, typed once: what a mount and a scope filter read an event's
    /// subjects from.</summary>
    internal PortView[] TriggerOutputs { get; } = PortView.Of(RuntimeJson.Rows(TriggerContract, "outputs"));
    /// <summary>The event cell of each <see cref="TriggerOutputs"/> port (-1 for an execution port and for a port the
    /// registered trigger does not declare), resolved by the kernel when the plan is loaded.</summary>
    internal int[] TriggerCells { get; set; } = Array.Empty<int>();
    /// <summary>The event cell the entry's gate accumulates its amount from; -1 where it names none.</summary>
    internal int GateAmountCell { get; set; } = -1;
    internal ResolvedAttachment[] Attachments { get; init; } = Array.Empty<ResolvedAttachment>();
    /// <summary>The steps a client may play ahead of the host's order (<see cref="RuntimeKernel.Predict"/>), as
    /// indexes into <see cref="Steps"/>; decided once, the first time a client reports an event of this entry.</summary>
    internal int[]? Predictable { get; set; }
}

/// <summary>The static command bound of one trigger bucket: the entries of one trigger binding that name the same
/// message (a message names its listeners, so two names never claim each other's events). Resource identities and
/// structural enum members are single event values, so incompatible entries never add. Inside one compatible group,
/// an entry with no object address claims every event, while an addressed entry is claimed only when the event
/// carries that object; one event carries at most one object per entity output port (any number for a list port), so
/// only the largest <c>scopesPerEvent</c> address groups run together. The website evaluates this same definition
/// over a whole level.</summary>
internal static class EventCommandBound
{
    internal static int Of(IEnumerable<EventCommandClaim> entries, int scopesPerEvent)
        => Compatible(entries.ToArray(), scopesPerEvent);

    private static int Compatible(EventCommandClaim[] entries, int scopesPerEvent)
    {
        var resources = entries.Where(entry => entry.ResourceScope != null).Select(entry => entry.ResourceScope!)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (resources.Length > 1)
            return resources.Max(resource => Compatible(entries.Where(entry => entry.ResourceScope == null
                || string.Equals(entry.ResourceScope, resource, StringComparison.Ordinal)).ToArray(), scopesPerEvent));
        var enumSplit = entries.SelectMany(entry => entry.EnumFilters ?? Array.Empty<TriggerEnumFilter>())
            .GroupBy(filter => filter.Port, StringComparer.Ordinal)
            .Select(group => (Port: group.Key, Members: group.Select(filter => filter.Member).Distinct().ToArray()))
            .FirstOrDefault(group => group.Members.Length > 1);
        if (enumSplit.Members is { Length: > 1 })
            return enumSplit.Members.Max(member => Compatible(entries.Where(entry =>
            {
                var filter = entry.EnumFilters?.FirstOrDefault(candidate => candidate.Port == enumSplit.Port);
                return filter == null || filter.Member == member;
            }).ToArray(), scopesPerEvent));
        var unscoped = 0;
        var scoped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Address == null) unscoped += entry.Steps;
            else scoped[entry.Address] = scoped.GetValueOrDefault(entry.Address) + entry.Steps;
        }
        return unscoped + scoped.Values.OrderByDescending(steps => steps).Take(scopesPerEvent).Sum();
    }

    internal static int ScopesPerEvent(JsonElement triggerContract)
    {
        var ports = RuntimeJson.Rows(triggerContract, "outputs").Where(port => RuntimeJson.Text(port, "type") == "entity").ToArray();
        return ports.Any(RuntimeGraphContracts.Many) ? int.MaxValue : ports.Length;
    }
}

internal sealed record ResolvedPlan(string Id, string ResourceId, string ResourceRevision, string Domain, RuntimeLimits Limits,
    IReadOnlyList<ResolvedEntry> Entries, IReadOnlySet<string> Bindings, IReadOnlyList<string> Permissions,
    IReadOnlyList<PlanAttachment> Attachments, PlanFrames Frames, IReadOnlyList<VariableDeclaration> Variables);

/// <summary>One mount target. A player's port names the player host; a gear block's optional port names the
/// canonical single-player subject whose current equipment receives the behaviour. No port means a direct event.</summary>
internal sealed record PlanAttachment(string Kind, string? Category, string Reference, string? Port = null, string? Level = null);
/// <summary>The registration, output slots and optional holder relation resolved once at load.</summary>
internal sealed record ResolvedAttachment(PlanAttachment Target, AttachmentMatcherRegistration Matcher,
    PortView[] Ports, EquipmentHolderMatcher? Holder, AttachmentScopeMatcher? Scope,
    (PortView Holder, PortView Equipment)? EquipmentAttachment)
{
    /// <summary>The event cells of <see cref="Ports"/> and of the two equipment roles, resolved by the kernel when
    /// the plan is loaded.</summary>
    internal int[] PortCells { get; set; } = Array.Empty<int>();
    internal int HolderCell { get; set; } = -1;
    internal int EquipmentCell { get; set; } = -1;
}
/// <summary>The identity a plan file resolves to before the rest of its content is validated: enough to group files by
/// planId for conflict detection, or to attach `plan` to a later rejection of the same file.</summary>
internal sealed record PlanIdentity(string Id, string ResourceId, string ResourceRevision);
