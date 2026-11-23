using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;


/// <summary>
/// schemaVersion "1.0.0" plan (Runtime API 1.0.0): a graph of `action`/`control`/`query`/`pure` steps per entrypoint,
/// walked from `start` along explicit `successors`, plus the required non-empty `attachments[]` that says which
/// world objects the plan is mounted on. `pure` and `query` steps are read on demand by consumers via
/// `fromStepSlot` and evaluated at most once per activation; `query` may read the world through the kernel's
/// budgeted interface, `pure` may not touch it at all. Every layout, successor, attachment and slot reference is
/// re-derived from the registered contract and must match exactly; nothing in the file is trusted. There is no v3
/// fallback: a file whose `schemaVersion` is not "1.0.0" is rejected outright (`plan-version`).
/// </summary>
internal static partial class RuntimePlan
{
    /// <summary>A common result input names the concrete row region of an action in this entrypoint. Neither an
    /// event, another evaluated step nor an undeclared output can supply that region. The loader validates the
    /// source index before this check and retains the producer's concrete port afterwards.</summary>
    internal static void RequireCommonResultSource(bool fromStep, string? producerKind, JsonElement contract,
        JsonElement port, string at)
        => RuntimeJson.Require(fromStep && producerKind == "action" && port.ValueKind == JsonValueKind.Object
            && RuntimeGraphContracts.IsResult(port) && !RuntimeGraphContracts.IsCommonResultPort(port)
            && RuntimeGraphContracts.ResultPortName(contract) == RuntimeJson.Text(port, "id"), "port-mismatch", at);

    /// <summary>One value port a control step's registered contract must declare, so the kernel can read the
    /// input the control is defined by without consulting a name it did not verify at load.</summary>
    private sealed record ControlPort(string Id, string Type, string? Unit = null, string? HandleKind = null, string? Lifetime = null, bool Many = false);
    /// <summary>The first-step control vocabulary, each keyed by its capability id. The kernel dispatches all of
    /// them from one place, so a control outside this table is refused by id (`control-unsupported`) rather than
    /// walked with guessed semantics. `Exits` is the execution outputs in declaration order, with `next` first;
    /// a region output (`pulse`/`body`) names the step the next activation starts from and is never an exit.</summary>
    private sealed record ControlShape(string[] Exits, ControlPort[] Inputs, ControlPort[] Outputs);
    // Declared before `ControlShapes`, which reads it: static fields initialize in textual order.
    private static readonly ControlPort VariableHandlePort = new("variable", "handle", HandleKind: "request", Lifetime: "session");
    /// <summary>Whether a control step's execution outputs are the ones its kind declares. A fixed shape lists
    /// them by id; a variadic one is judged by its pattern, which is the same rule the resolved contract already
    /// applied to the count.</summary>
    private static bool ExitsMatch(string capabilityId, string[] execIds, ControlShape shape)
    {
        if (shape.Exits.Length != 0) return execIds.SequenceEqual(shape.Exits);
        if (capabilityId == Blocks.ControlFlowSequence.Id)
            return execIds.Length >= 1 && execIds.Select((id, index) => id == "step_" + (index + 1)).All(match => match);
        if (capabilityId == Blocks.ControlFlowEnumSwitch.Id)
        {
            // `case_1`..`case_N` in declaration order, then the single `otherwise` the unmatched value leaves through.
            if (execIds.Length < 3 || execIds[^1] != "otherwise") return false;
            for (var index = 0; index < execIds.Length - 1; index++)
                if (execIds[index] != "case_" + (index + 1)) return false;
            return true;
        }
        return execIds.Length == 0;
    }
    private static readonly Dictionary<string, ControlShape> ControlShapes = new(StringComparer.Ordinal)
    {
        [Blocks.ControlFlowBranch.Id] = new(new[] { "then", "otherwise" },
            new[] { new ControlPort("condition", "boolean") }, Array.Empty<ControlPort>()),
        [Blocks.ControlFlowSequence.Id] = new(Array.Empty<string>(), Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        [Blocks.ControlFlowDelay.Id] = new(new[] { "next" },
            new[] { new ControlPort("duration", "integer", "tick") },
            new[] { new ControlPort("timer", "handle", HandleKind: "timer", Lifetime: "encounter") }),
        // One row, two shapes (see LoopShape): counted inside one activation, or — with an `interval` — a pulse
        // series the one scheduler owns, whose `timer` is the cancellable handle of that series.
        [Blocks.ControlFlowRepeat.Id] = new(new[] { "next", "body" },
            new[] { new ControlPort("interval", "integer", "tick"), new ControlPort("count", "integer") },
            new[] { new ControlPort("index", "integer"), new ControlPort("timer", "handle", HandleKind: "timer", Lifetime: "encounter") }),
        // One walk over either list: `item` is the candidate a round is about, `position` the point, in metres.
        [Blocks.ControlFlowForEach.Id] = new(new[] { "next", "body" },
            new[] { new ControlPort("candidates", "entity", Many: true), new ControlPort("positions", "vector3", Many: true), new ControlPort("budget", "integer") },
            new[] { new ControlPort("item", "entity"), new ControlPort("position", "vector3", "m"), new ControlPort("index", "integer") }),
        // The enum fan-out: the exits are `case_1`..`case_N` then `otherwise`, so the pattern carries them and this
        // row pins only the value port the branch is drawn from. Its `schema` — the set the node chose — is resolved
        // from the node's own contract, which is where the kernel reads the member order the case exits follow.
        [Blocks.ControlFlowEnumSwitch.Id] = new(Array.Empty<string>(),
            new[] { new ControlPort("value", "enum") }, Array.Empty<ControlPort>()),
        [Blocks.ControlFlowCancel.Id] = new(new[] { "next" },
            new[] { new ControlPort("task", "handle", HandleKind: "timer", Lifetime: "encounter") },
            new[] { new ControlPort("cancelled", "integer") }),
        // The rolling window: the same live timer handle, re-armed from now. Its ports are `cancel`'s with a boolean
        // answer instead of a count, because a restart either found a live schedule or failed by name.
        [Blocks.ControlFlowRestart.Id] = new(new[] { "next" },
            new[] { new ControlPort("task", "handle", HandleKind: "timer", Lifetime: "encounter") },
            new[] { new ControlPort("restarted", "boolean") }),
        // The when-present guard (rule 142.3). Its two exits are its whole execution shape; its one value port is
        // the author's own class rather than one this table pins, so the pair is checked where the step is loaded
        // (`PresentValuePort`) instead of being restated here.
        [Blocks.ControlFlowPresent.Id] = new(new[] { "present", "missing" }, Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        // The value ramp: one exit, because every pulse re-enters `next` rather than a region of its own. The two
        // ends, the curve and the two fall-back legs are the whole of its inputs, and the handle it publishes is the
        // schedule's own — the pair its row declares, so a later cancel reaches the same envelope.
        [Blocks.ControlFlowRamp.Id] = new(new[] { "next" },
            new[]
            {
                new ControlPort("from", "number"), new ControlPort("to", "number"),
                new ControlPort("duration", "integer", "tick"), new ControlPort("exponent", "number"),
                new ControlPort("release_duration", "integer", "tick"), new ControlPort("release_delay", "integer", "tick")
            },
            new[]
            {
                new ControlPort("value", "number"),
                new ControlPort("handle", "handle", HandleKind: "effect", Lifetime: "entity_life")
            }),
        // The action group: its exits are fixed (RuntimeKernel.Groups reads them by position), the four optional
        // counts are ticks or plain integers, and its handle is a timer so the one cancel path ends the whole group.
        [Blocks.ControlFlowGroup.Id] = new(new[] { "body", "done", "failed", "timed_out", "full" },
            new[]
            {
                new ControlPort("time_limit", "integer", "tick"), new ControlPort("attempts", "integer"),
                new ControlPort("retry_delay", "integer", "tick"), new ControlPort("max_active", "integer")
            },
            new[] { new ControlPort("group", "handle", HandleKind: "timer", Lifetime: "encounter"), new ControlPort("attempt", "integer") }),
        // The variable and message rows the kernel dispatches itself. A row's `value` port is not
        // restated here: a `g-var` node resolves it through its own `value_type` constant, so the table fixes the
        // execution exits and the ports whose shape is the same for every use of the row.
        // `variable` is the declaration's own handle, the wire a read or a write may name its declaration by instead
        // of the `name` parameter (`VariableContracts.VariableHandleParameter`).
        [Blocks.VariableStoreRead.Id] = new(new[] { "next" },
            new[] { VariableHandlePort, new ControlPort("subject", "entity"), new ControlPort("slot", "integer") }, Array.Empty<ControlPort>()),
        [Blocks.VariableStoreWrite.Id] = new(new[] { "next" },
            new[] { VariableHandlePort, new ControlPort("subject", "entity"), new ControlPort("slot", "integer") },
            new[] { new ControlPort("written", "boolean") }),
        [Blocks.ControlFlowOnce.Id] = new(new[] { "first", "later" }, Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        [Blocks.ControlFlowWaitEvent.Id] = new(new[] { "received", "timeout" }, Array.Empty<ControlPort>(),
            new[] { new ControlPort("message", "string"), new ControlPort("payload", "number") }),
        [Blocks.ControlMessageEmit.Id] = new(new[] { "next" }, Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        [Blocks.ControlLogRecord.Id] = new(new[] { "next" }, Array.Empty<ControlPort>(), Array.Empty<ControlPort>())
    };
    /// <summary>The when-present guard (rule 142.3): the one control whose value output the loader resolves from the
    /// plan's own contract instead of pinning it here, because the author chose the class it carries.</summary>
    private const string PresentControl = Blocks.ControlFlowPresent.Id;
    private const string RepeatControl = Blocks.ControlFlowRepeat.Id;
    private const string ForEachControl = Blocks.ControlFlowForEach.Id;

    /// <summary>
    /// The shape one `repeat` or `for_each` step runs in. A `repeat` without an `interval` is a counted loop inside
    /// one activation and must say how many rounds; with one it is a pulse series whose `count` may be left out (an
    /// unbounded series only cancellation, its scope or its world ends). A `for_each` walks exactly one of its two
    /// lists. The two structural options are read here, in their compiled member form, so no dispatch reads them.
    /// </summary>
    private static LoopShape LoopShapeOf(Node node, IReadOnlyList<StepInput> inputs)
    {
        bool Filled(string port) { foreach (var input in inputs) if (input.Name == port) return true; return false; }
        bool Member(string parameter, string member)
        {
            foreach (var definition in RuntimeJson.Rows(node.Graph, "parameters"))
                if (RuntimeJson.Text(definition, "id") == parameter)
                    return node.Parameters.TryGetProperty(parameter, out var value) && value.ValueKind != JsonValueKind.Null
                        && RuntimeGraphContracts.EnumMemberName(definition, value) == member;
            return false;
        }
        switch (node.Control)
        {
            case RepeatControl:
                if (Filled("interval")) return LoopShape.Scheduled | (Member("first_pulse", "after_interval") ? LoopShape.AfterInterval : LoopShape.None);
                RuntimeJson.Require(Filled("count"), RuntimeAbiCodes.ControlShape, node.Id + ".count");
                return LoopShape.None;
            case ForEachControl:
                var positions = Filled("positions");
                RuntimeJson.Require(positions != Filled("candidates"), RuntimeAbiCodes.ControlShape, node.Id + ".candidates");
                return (positions ? LoopShape.Positions : LoopShape.None) | (Member("shortfall", "repeat") ? LoopShape.Cycle : LoopShape.None);
            default:
                return LoopShape.None;
        }
    }

    /// <summary>Refuses a read of a loop output its shape never publishes: a counted `repeat` holds no schedule and so
    /// no `timer`, and a `for_each` publishes `item` over candidates and `position` over positions, never both.</summary>
    private static void RequireLoopOutput(Node source, string port)
    {
        var refused = source.Control switch
        {
            RepeatControl => port == "timer" && (source.Loop & LoopShape.Scheduled) == 0,
            ForEachControl => (source.Loop & LoopShape.Positions) != 0 ? port == "item" : port == "position",
            _ => false
        };
        RuntimeJson.Require(!refused, RuntimeAbiCodes.ControlShape, source.Id + "." + port);
    }
    /// <summary>The port id the kernel reads the tested value from. The forwarded value needs no such constant: the
    /// loader hands the kernel the port id the plan declared (`ResolvedStep.ValuePort`).</summary>
    private const string PresentValueInput = "value";

    /// <summary>
    /// The value ports of a when-present step, checked as the pair they are: the port the kernel reads the tested
    /// value from, and the one non-execution output the `present` branch forwards it through. Both carry one value
    /// contract of a class this runtime can move — the class itself is already resolved, because the plan's layout
    /// is proved equal to the registered contract re-derived from this node's own `value_type` — so the two ports
    /// are the value that was tested and never a second one. The returned id is the plan's own name for the forward
    /// port: the kernel publishes the value under it, so no spelling of it is repeated here.
    /// </summary>
    private static string PresentValuePort(JsonElement[] inputs, JsonElement[] outputs, string nodeId)
    {
        var tested = inputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == PresentValueInput);
        var forwarded = outputs.Where(p => RuntimeJson.Text(p, "type") != "execution").ToArray();
        RuntimeJson.Require(tested.ValueKind == JsonValueKind.Object
            && Wire.ValueTypeParameterTypes.Contains(RuntimeJson.Text(tested, "type")),
            RuntimeAbiCodes.ControlShape, nodeId + "." + PresentValueInput);
        RuntimeJson.Require(forwarded.Length == 1 && RuntimeGraphContracts.ValueTypeMatches(tested, forwarded[0]),
            RuntimeAbiCodes.ControlShape, nodeId + "." + PresentValueInput);
        return RuntimeJson.Text(forwarded[0], "id");
    }
    private static readonly byte[] LineFeed = { (byte)'\n' };
    private sealed record Node(string Id, string Kind, string BindingId, int HandlerIndex, JsonElement Graph, JsonElement Parameters,
        JsonElement[] Constants, JsonElement Contract, IReadOnlySet<string> Promoted,
        string? Control = null, int BodyOutput = -1, string? ValuePort = null, string? TriggerAddress = null,
        EffectOptions? Effect = null, CostOptions? Cost = null, LoopShape Loop = LoopShape.None)
    {
        internal BindingContract Registration { get; init; } = null!;
        internal NodeContract Resolved { get; init; } = null!;
        internal JsonElement[] Inputs { get; init; } = Array.Empty<JsonElement>();
        internal JsonElement[] Outputs { get; init; } = Array.Empty<JsonElement>();
    }

    /// <summary>The first slice of validation, shared by <see cref="Parse"/> and <see cref="PeekIdentity"/>: shape, version,
    /// runtime lock, authority/failure policy and the planId/resource identity. A file that fails here has no identity and
    /// is rejected on its own error without joining conflict-by-planId grouping.</summary>
    internal static (JsonElement Plan, PlanIdentity Identity) Identify(PlanDocument document, RuntimeIdentity runtime)
    {
        var plan = document.Plan();
        RuntimeJson.Shape(plan, "schemaVersion kind planId resource runtime domain layoutHash permissions dependencies limits bindings entrypoints attachments",
            "variables");
        // A version of another format (an earlier numeric one included) is a plan of another version, not a malformed field.
        var version = plan.GetProperty("schemaVersion");
        RuntimeJson.Require(version.ValueKind == JsonValueKind.String && version.GetString() == ForgeCore.FormatVersion.Current
            && RuntimeJson.Text(plan, "kind") == "forge-engine-plan", "plan-version", "Unsupported plan version.");
        RequireRuntime(plan.GetProperty("runtime"), runtime);
        var id = RuntimeJson.Text(plan, "planId"); var resource = plan.GetProperty("resource");
        RuntimeJson.Shape(resource, "id revision"); var resourceId = RuntimeJson.Text(resource, "id"); var revision = RuntimeJson.Text(resource, "revision");
        return (plan, new PlanIdentity(id, resourceId, revision));
    }

    /// <summary>`^[A-Za-z][A-Za-z0-9_-]{0,127}$` without the pattern matcher. The node id has already passed
    /// <see cref="RuntimeJson.Text(string)"/>, which refuses every control character, so the pattern's end anchor,
    /// which would also accept one trailing line feed, can see only the end of the text.</summary>
    internal static bool IsNodeId(string nodeId)
    {
        static bool Letter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        if (nodeId.Length is 0 or > 128 || !Letter(nodeId[0])) return false;
        for (var index = 1; index < nodeId.Length; index++)
        {
            var c = nodeId[index];
            if (!(Letter(c) || c is >= '0' and <= '9' or '_' or '-')) return false;
        }
        return true;
    }

    /// <summary>The first pass over a discovered file: resolve just enough identity to group it by planId, without
    /// validating the rest of its content. Throws with the file's own error when even this much cannot be resolved.</summary>
    internal static PlanIdentity PeekIdentity(PlanDocument document, RuntimeIdentity runtime) => Identify(document, runtime).Identity;

    /// <summary>A plan records the Forge major it was compiled for and the minor of the build it was compiled against, the
    /// conservative lower bound it needs (never an exact version or game build). It loads on any installed runtime of that major at or above the
    /// minor; a different major is refused by name, with both versions in the sentence.</summary>
    private static void RequireRuntime(JsonElement declared, RuntimeIdentity runtime)
    {
        RuntimeJson.Shape(declared, "id major minMinor");
        var major = (int)RuntimeJson.Integer(declared.GetProperty("major"), 0, 65535);
        var minimum = (int)RuntimeJson.Integer(declared.GetProperty("minMinor"), 0, 65535);
        RuntimeJson.Require(RuntimeJson.Text(declared, "id") == runtime.Id, "runtime-version", "The plan names a different runtime.");
        RuntimeJson.Require(ForgeVersion.TryParse(runtime.Version, out var installed) && ForgeVersion.Meets(installed.Major, installed.Minor, major, minimum),
            "runtime-version", "ForgeEngine " + ForgeVersion.Describe(runtime.Version, major, minimum) + ".");
    }

    /// <summary>The rest of a plan's validation, over the value and identity <see cref="Identify"/> already resolved
    /// from the one read of its file.</summary>
    internal static ResolvedPlan Parse(JsonElement plan, PlanIdentity planIdentity, RuntimeLimits ceiling, RuntimeRegistry registry,
        RuntimeKernel kernel, ParseTable table)
    {
        var (id, resourceId, revision) = (planIdentity.Id, planIdentity.ResourceId, planIdentity.ResourceRevision);
        var domain = RuntimeJson.Text(plan, "domain");
        RuntimeJson.Require(Wire.Domains.Contains(domain), "plan-domain", domain);
        var budget = plan.GetProperty("limits");
        RuntimeJson.Shape(budget, "maxEventsPerTick maxCommandsPerTick maxQueuedEvents maxCausalDepth");
        var limits = ceiling with
        {
            MaxEventsPerTick = (int)RuntimeJson.Integer(budget.GetProperty("maxEventsPerTick"), 1, ceiling.MaxEventsPerTick),
            MaxCommandsPerTick = (int)RuntimeJson.Integer(budget.GetProperty("maxCommandsPerTick"), 1, ceiling.MaxCommandsPerTick),
            MaxQueuedEvents = (int)RuntimeJson.Integer(budget.GetProperty("maxQueuedEvents"), 1, ceiling.MaxQueuedEvents),
            MaxCausalDepth = (int)RuntimeJson.Integer(budget.GetProperty("maxCausalDepth"), 1, ceiling.MaxCausalDepth)
        };
        // The pin table is positional: nodes name their binding by index, never by string.
        var pins = new List<string>();
        var pinIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var pinContracts = new List<BindingContract>();
        var subjectOutputs = new List<int>();
        foreach (var pin in RuntimeJson.Rows(plan, "bindings", 2048))
        {
            // A pin is the binding id alone: the capability, provider, handler and subject output are this runtime's own
            // registration, and what the plan's blocks resolve to is proved by the layout hash below.
            var bindingId = RuntimeJson.Text(pin);
            RuntimeJson.Require(pinIndices.TryAdd(bindingId, pins.Count), "duplicate-binding-pin", bindingId);
            RuntimeJson.Require(registry.Bindings.TryGetValue(bindingId, out var binding) && RuntimeJson.Text(binding, "status") == "implemented", "binding-unavailable", bindingId);
            var registration = table.Binding(bindingId);
            subjectOutputs.Add(registration.SubjectOutput < 0 ? -1 : registration.SubjectOutput);
            pins.Add(bindingId);
            pinContracts.Add(registration);
        }
        RuntimeJson.Require(pins.SequenceEqual(pins.OrderBy(x => x, StringComparer.Ordinal)), "binding-order", "Binding locks must be ordinal sorted.");
        // The handler table of this plan, in pin order: a step's handler position indexes it, so the tick path
        // reaches a handler through an integer instead of a binding-id dictionary lookup.
        var handlerPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < pinContracts.Count; index++) handlerPositions.TryAdd(pinContracts[index].HandlerName, index);
        foreach (var key in new[] { "permissions", "dependencies" })
        {
            var values = RuntimeJson.Strings(plan.GetProperty(key));
            RuntimeJson.Require(values.SequenceEqual(values.OrderBy(x => x, StringComparer.Ordinal)), "plan-order", key);
        }
        var entryRows = RuntimeJson.Rows(plan, "entrypoints", ceiling.MaxEntrypoints);
        var attachments = ParseAttachments(plan, registry, ceiling);
        var entryIds = entryRows.Select(e => RuntimeJson.Text(e, "nodeId")).ToArray();
        RuntimeJson.Require(entryIds.SequenceEqual(entryIds.OrderBy(x => x, StringComparer.Ordinal)), "entrypoint-order", "Entrypoints must be ordinal sorted.");
        var used = new HashSet<string>(StringComparer.Ordinal); var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        using var layoutHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

        int Slot(JsonElement value, int count, string code, string detail)
        {
            RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 && index < count, code, detail);
            return value.GetInt32();
        }
        // kind: "trigger", "action", "control", "query" or "pure" (the last two select a
        // selector/condition/modifier capability, split by whether it touches the world).
        Node Load(JsonElement row, string kind)
        {
            var nodeId = RuntimeJson.Text(row, "nodeId");
            RuntimeJson.Require(IsNodeId(nodeId), "node-id", nodeId);
            RuntimeJson.Require(nodeIds.Add(nodeId), "duplicate-node", nodeId);
            var pin = Slot(row.GetProperty("binding"), pins.Count, "binding-index", nodeId);
            var bindingId = pins[pin]; used.Add(bindingId);
            var registration = pinContracts[pin];
            var binding = registration.Binding; var capability = registration.Capability;
            var capabilityId = RuntimeJson.Text(capability, "id");
            var capabilityKind = RuntimeJson.Text(capability, "kind");
            var matchesKind = kind switch
            {
                "trigger" => capabilityKind == "trigger",
                "action" => capabilityKind == "action",
                "control" => capabilityKind == "control",
                "pure" => capabilityKind is "selector" or "condition" or "modifier",
                // A query node is the on-demand read tier: a selector picks entities, a condition answers about
                // them and a value row (`kind: state`) answers a fact nobody publishes. A `modifier` whose graph
                // reads the world is a value row the catalog categorizes `modifier`, and it is answered here like
                // a state row; the authority check below still refuses a pure modifier, because a pure row's
                // execution is not the query this tier requires.
                "query" => capabilityKind is "selector" or "condition" or "state" or "modifier",
                _ => false
            };
            RuntimeJson.Require(matchesKind && capability.TryGetProperty("graph", out _), "node-kind", nodeId);
            var graph = registration.Graph;
            var executionAuthority = RuntimeJson.Text(graph, "execution");
            // An `action` step is executed on exactly one machine, and which one decides where its handler runs: the
            // advance that owns the world (`host`), the recipient the dispatch addresses (`presentation`), or the one
            // session that holds what the step changes (`owner`). All three are real dispatches this kernel walks —
            // the owner tier resolves the holder and records the intent, and the holder runs the same handler through
            // its own entry point — so a registered `owner` row is admitted here like the other two. A `control` is
            // the kernel's own walker and belongs to no other tier, and a trigger's own tier is not a dispatch
            // decision at all: the entry point it declares is walked by the kernel, so its admission is unchanged.
            var matchesAuthority = kind switch
            {
                "pure" => executionAuthority == "pure",
                "query" => executionAuthority == "query",
                "control" => executionAuthority == "host",
                "action" => executionAuthority is "host" or "presentation" or "owner",
                _ => executionAuthority is "host" or "presentation"
            };
            RuntimeJson.Require(matchesAuthority, "node-domain-authority", nodeId);
            // Rule 142.4 (I-PLAN, §3.2): a graph's domain is a panel default and a trigger host constraint, never a
            // per-node gate. An action, control, query or pure node is placed by what it does — its ports, its
            // explicit recipients, its execution tier and its permissions — while only a trigger has to find its
            // event source on the host, and the domains a capability declares say where its sources live. The
            // website's own `graphAcceptsNodeDomain` asks this same rule the same way, so the editor, the compiler
            // and this loader answer "may this capability be placed here" identically.
            RuntimeJson.Require(kind != "trigger"
                || attachments.Any(attachment => attachment.Kind == "gear-block" && attachment.Port != null
                    ? domain is "weapon" or "tool" or "consumable" && HolderSubject(graph) is { } subject
                        && RuntimeJson.Text(subject, "id") == attachment.Port
                    : RuntimeJson.Strings(graph.GetProperty("domains")).Contains(domain, StringComparer.Ordinal)),
                "node-domain-authority", nodeId);
            string? control = null; var bodyOutput = -1; string? valuePort = null; string? triggerAddress = null;
            EffectOptions? effect = null; CostOptions? cost = null;
            RuntimeJson.Require(graph.TryGetProperty("parameters", out _), "node-parameters", nodeId);
            var definitions = registration.Parameters;
            RuntimeJson.Require(definitions.All(p => RuntimeJson.Text(p, "type") != "recipient-policy"), "unsupported-parameter", nodeId);
            var constants = RuntimeJson.Rows(row, "constants");
            RuntimeJson.Require(constants.Length == definitions.Length, "constant-frame", nodeId);
            // Strictly increasing declaration indices; each promoted value arrives through an input slot after the resolved inputs.
            var promoted = new HashSet<string>(StringComparer.Ordinal); var last = -1;
            foreach (var frame in RuntimeJson.Rows(row, "promoted"))
            {
                var index = Slot(frame, definitions.Length, "promotion-frame", nodeId);
                RuntimeJson.Require(index > last, "promotion-frame", nodeId); last = index;
                promoted.Add(registration.ParameterNames[index]);
            }
            // Positional constants rebuild the keyed bag; null is the compiled spelling of "not authored" or "promoted".
            // The bag is what a step's structural parameters and the resolvable variadic counts are read from; every
            // value parameter is validated once, against its own definition, where the frame builder encodes it.
            var resolvedContract = registration.Resolve(row, constants, promoted, nodeId);
            var parameters = resolvedContract.Parameters;
            // The registered contract resolved from this node's constants is the port type and width authority: the
            // frame is built from it alone. Its canonical layout joins the plan's layout hash in plan order (each
            // entry's trigger, then its steps), so a port the compiler resolved differently, a stale plan or a
            // tampered one refuses the whole plan where the hash is compared.
            layoutHash.AppendData(registration.CapabilityIdUtf8); layoutHash.AppendData(LineFeed);
            layoutHash.AppendData(resolvedContract.InputLayout); layoutHash.AppendData(LineFeed);
            layoutHash.AppendData(resolvedContract.OutputLayout); layoutHash.AppendData(LineFeed);
            var authoritative = resolvedContract.Contract;
            var inputs = resolvedContract.Inputs; var outputs = resolvedContract.Outputs;
            var inputExecution = resolvedContract.InputExecution;
            var outputExecution = resolvedContract.OutputExecution;
            // The one world-port fact both evaluated tiers are judged by.
            var WorldPorts = resolvedContract.WorldPorts;
            switch (kind)
            {
                case "trigger":
                    // A trigger declares no inputs. It may be told which author definition it subscribes to (a
                    // structural resource constant) and which map object it is attached to (a structural map object
                    // address, rule 143.11) — both compile-time constants the loader resolves, never a filter the
                    // dispatch would have to evaluate. `TriggerAddress` is read once here and carried on the entry.
                    RuntimeJson.Require(inputs.Length == 0 && outputExecution.Length == 1, "trigger-shape", nodeId);
                    triggerAddress = TriggerAddress(nodeId, definitions, constants);
                    break;
                case "action":
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Handlers.ContainsKey(bindingId)
                        || StatusContract.IsDispel(capabilityId), "action-handler", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional") && outputExecution.Length <= 1, "action-shape", nodeId);
                    // The kernel-dispatched dispel row is answered here, so the two things the kernel reads off it
                    // are checked here: the recipients port every action declares, and the one status-kind input the
                    // step addresses an instance by. A row that carries none would dispatch an address nobody wrote.
                    if (StatusContract.IsDispel(capabilityId))
                        RuntimeJson.Require(inputs.Count(p => OptionalText(p, "schema") == StatusContract.StatusKindSchema) == 1,
                            RuntimeAbiCodes.StatusDispelShape, nodeId);
                    // The step's own duration options (plan §3.4 动作卡). They are read here, once, because every
                    // one of them is an answer a handler would otherwise have to keep a clock for: the kernel owns
                    // the handle and the timing, and a card may only ask for one where the module that applies the
                    // effect registered the callback that ends it.
                    effect = EffectOptions.Parse(row);
                    if (effect != null)
                    {
                        RuntimeJson.Require(executionAuthority == "host", "effect-tier",
                            "effect belongs to a host action: only the authoritative side may keep a clock a replica has to agree with.");
                        var retains = registry.EffectRestores.ContainsKey(bindingId);
                        // An action that changes persistent native state registers the callback that undoes it, and
                        // only such an action may carry a duration at all. An action that only runs once per period
                        // — damage, healing, stamina — changes nothing that has to be given back, so a period is
                        // exactly what it is allowed to carry without a callback (plan §3.2 I-PLAN 适用范围).
                        RuntimeJson.Require(retains || effect.Interval != null, "effect-unsupported",
                            "This action declares no restore callback, so it may only carry a periodic effect (effect.interval).");
                        RuntimeJson.Require(!effect.Cancel || RuntimeKernel.EffectHandlePort(graph) != null, "effect-handle",
                            "effect.cancel needs an effect handle output on the row.");
                        // Every `end_on` ability is resolved once, here, to the trigger binding that carries it and
                        // to the kinds it can be about: an ability that cannot be about a receiver of this action
                        // could never end it, which is a card worth refusing rather than a field nothing reads.
                        var endsOn = new List<string>(effect.EndsOn.Count);
                        var endTriggers = new List<EffectEndTrigger>(effect.EndsOn.Count);
                        foreach (var ability in effect.EndsOn)
                        {
                            RuntimeJson.Require(registry.Capabilities.TryGetValue(ability, out var endsCapability)
                                && RuntimeJson.Text(endsCapability, "kind") == "trigger", "effect-end-on",
                                "effect.end_on names an ability this registry does not carry as a trigger: " + ability);
                            RuntimeJson.Require(endsCapability.TryGetProperty("graph", out var endsGraph)
                                && RuntimeKernel.EndOnCovers(endsGraph, graph), "effect-end-on",
                                "effect.end_on names a trigger whose subject kinds do not cover this action's receivers: " + ability);
                            var triggerBinding = registry.Bindings.Values.FirstOrDefault(candidate =>
                                RuntimeJson.Text(candidate, "capabilityId") == ability);
                            RuntimeJson.Require(triggerBinding.ValueKind == JsonValueKind.Object, "effect-end-on",
                                "effect.end_on names a trigger nothing binds: " + ability);
                            var triggerBindingId = RuntimeJson.Text(triggerBinding, "id");
                            RuntimeJson.Require(pinIndices.TryGetValue(triggerBindingId, out var triggerPin), "binding-lock", "effect.end_on is absent from the plan binding lock: " + ability);
                            endsOn.Add(triggerBindingId);
                            endTriggers.Add(new EffectEndTrigger(triggerBindingId, subjectOutputs[triggerPin],
                                PortView.Of(RuntimeGraphContracts.SubjectPort(endsGraph))));
                            // The receiver's trigger is a binding this plan consumes, so it belongs to the plan's
                            // binding lock like every row's own binding: the ending is read from a dispatch on it,
                            // and a lock that left it out would let the plan run against a provider it never named.
                            used.Add(triggerBindingId);
                        }
                        effect = effect with { EndOn = endsOn, EndTriggers = endTriggers };
                    }
                    // The step's own cost group (plan §3.2 I-PLAN 结果行): the resources it pays, in declaration
                    // order. Every item is resolved here, once, against the provider that owns its kind — a cost
                    // nobody can charge is a plan that cannot run as written, refused at load rather than
                    // discovered by a step that never pays.
                    cost = CostOptions.Parse(row, nodeId);
                    if (cost != null)
                    {
                        RuntimeJson.Require(executionAuthority == "host", "cost-tier",
                            "cost belongs to a host action: only the authoritative side charges a resource.");
                        foreach (var item in cost.Items)
                        {
                            RuntimeJson.Require(registry.ResourceProviders.TryGetValue(item.ResourceKind, out var owner)
                                && owner.Provider.Cost != null, RuntimeAbiCodes.CostUnsupported,
                                nodeId + "." + item.ResourceKind + " has no owner that keeps a balance to charge.");
                            RuntimeJson.Require(kernel.ResolveCompiledResource(item.ResourceKind, item.ResourceId) != null,
                                RuntimeAbiCodes.StaleResource, nodeId + "." + item.ResourceKind + " " + item.ResourceId);
                        }
                    }
                    break;
                case "control":
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute", "binding-role", nodeId);
                    RuntimeJson.Require(ControlShapes.TryGetValue(RuntimeJson.Text(capability, "id"), out var declaredShape), RuntimeAbiCodes.ControlUnsupported, nodeId);
                    var shape = declaredShape!;
                    RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional"), RuntimeAbiCodes.ControlShape, nodeId);
                    var execIds = outputExecution.Select(p => RuntimeJson.Text(p, "id")).ToArray();
                    // A control's exits are judged three ways: a sequence and the enum fan-out are variadic
                    // (`step_N`, or `case_1`..`case_N` then `otherwise`) and are checked by pattern; a control whose
                    // table entry declares no exits at all is a flow that ends, so it declares no execution output;
                    // every other kind declares its exits by id, in declaration order, with the region output —
                    // when it has one — last.
                    RuntimeJson.Require(ExitsMatch(RuntimeJson.Text(capability, "id"), execIds, shape),
                        RuntimeAbiCodes.ControlShape, nodeId + "." + string.Join(",", execIds));
                    foreach (var required in shape.Inputs)
                    {
                        var port = inputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == required.Id);
                        RuntimeJson.Require(port.ValueKind == JsonValueKind.Object && RuntimeJson.Text(port, "type") == required.Type
                            && OptionalText(port, "unit") == required.Unit && OptionalText(port, "handleKind") == required.HandleKind
                            && OptionalText(port, "lifetime") == required.Lifetime && RuntimeGraphContracts.Many(port) == required.Many,
                            RuntimeAbiCodes.ControlShape, nodeId + "." + required.Id);
                    }
                    foreach (var required in shape.Outputs)
                    {
                        var port = outputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == required.Id);
                        RuntimeJson.Require(port.ValueKind == JsonValueKind.Object && RuntimeJson.Text(port, "type") == required.Type
                            && OptionalText(port, "unit") == required.Unit && OptionalText(port, "handleKind") == required.HandleKind
                            && OptionalText(port, "lifetime") == required.Lifetime && RuntimeGraphContracts.Many(port) == required.Many,
                            RuntimeAbiCodes.ControlShape, nodeId + "." + required.Id);
                    }
                    control = RuntimeJson.Text(capability, "id");
                    bodyOutput = control is Blocks.ControlFlowRepeat.Id or Blocks.ControlFlowForEach.Id ? 1 : -1;
                    if (control == PresentControl) valuePort = PresentValuePort(inputs, outputs, nodeId);
                    break;
                case "pure":
                    // A pure step is read through its evaluator; a planned evaluate binding has none.
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "evaluate", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Evaluators.ContainsKey(bindingId), "missing-evaluator", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 0 && outputExecution.Length == 0, "pure-shape", nodeId);
                    // The step's own ports and its capability's declared reads are one jurisdiction: a declared
                    // read is a read even with no port behind it, so a pure step is refused for either.
                    RuntimeJson.Require(!inputs.Concat(outputs).Any(p => RuntimeGraphContracts.PureWorldPort(RuntimeJson.Text(p, "type")))
                        && registry.Reads(capabilityId).Length == 0, RuntimeAbiCodes.PureWorldRead, nodeId);
                    break;
                case "query":
                    // A query is the same evaluator mechanism as `pure`, with the world reachable through the
                    // kernel's budgeted session: `observe` is its binding role, `evaluate` also accepted because a
                    // world-port observation and a value-only one share one registration table.
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") is "observe" or "evaluate", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Evaluators.ContainsKey(bindingId), "missing-evaluator", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 0 && outputExecution.Length == 0, "query-shape", nodeId);
                    // A world port or a declared read makes the observation a query; a step with neither may not
                    // claim the tier, which is what keeps `query` from becoming a second spelling of `pure`.
                    RuntimeJson.Require(WorldPorts || registry.Reads(capabilityId).Length > 0, RuntimeAbiCodes.QueryAuthority, nodeId);
                    break;
            }
            // Every non-execution port that carries a value across this boundary must be a type this runtime
            // validates: every step's inputs (same as v2), the trigger's own event outputs, and a `pure`/`query`
            // step's outputs — the latter are the frames later steps read through `fromStepSlot`. An `action`'s
            // outputs are judged where one is read rather than here: an output nothing reads crosses no boundary,
            // and the read is what makes it a value this runtime has to move (`MovablePort`). A `handle` is the one
            // exception that is carried but never decoded: it has an implemented frame form of its own and is
            // moved, not validated as a value. A result row is the other: a step's declared result input is a
            // reference to the rows an earlier step wrote, read through that step's own descriptor, so it is judged
            // where it is wired rather than by the value rule. A row output is written by the step's own ABI and
            // never carried in from outside, so it stays refused on every side.
            foreach (var port in kind == "trigger" || kind is "pure" or "query" ? resolvedContract.OutputValues : resolvedContract.InputValues)
            {
                // A row input is one row set, never a collection of rows: `many` stays refused by the same rule every
                // other reference carrier follows.
                var rowInput = kind is "action" or "control" && RuntimeJson.Text(port, "type") == "result"
                    && !RuntimeGraphContracts.Many(port);
                RuntimeJson.Require(rowInput || MovablePort(port), kind == "trigger" ? "unsupported-event-port" : "unsupported-input-port", nodeId + "." + RuntimeJson.Text(port, "id"));
            }
            return new Node(nodeId, kind, bindingId, handlerPositions[registration.HandlerName], graph, parameters, constants, authoritative, promoted, control, bodyOutput, valuePort, triggerAddress, effect, cost)
            { Registration = registration, Resolved = resolvedContract, Inputs = inputs, Outputs = outputs };
        }

        /// <summary>
        /// The map object address an entrypoint's trigger is attached to, or null when it declares none (rule
        /// 143.11: a trigger bound to one object receives only that object's events, an unbound one receives every
        /// event of its binding). The address is a structural parameter of the trigger node and arrives as the node's
        /// own compile-time constant.
        ///
        /// Only the provider-neutral envelope is checked here: non-empty `/`-separated segments. Categories,
        /// segment counts and accepted keys or coordinates belong to the resolving provider; the website renders
        /// that provider's canonical spelling. Encoding any category grammar in the kernel would create a second
        /// owner. A trigger may declare at most one such
        /// parameter: two addresses are not a filter this kernel could evaluate, and silently picking one would mount
        /// the plan on an object nobody named.
        /// </summary>
        static string? TriggerAddress(string nodeId, JsonElement[] definitions, JsonElement[] constants)
        {
            string? address = null;
            for (var i = 0; i < definitions.Length; i++)
            {
                if (RuntimeJson.Text(definitions[i], "type") != "object-address") continue;
                RuntimeJson.Require(RuntimeJson.Text(definitions[i], "role") == "structural",
                    "trigger-address-role", nodeId + "." + RuntimeJson.Text(definitions[i], "id"));
                RuntimeJson.Require(address == null, "trigger-address-count", nodeId);
                if (constants[i].ValueKind == JsonValueKind.Null) continue;
                var text = RuntimeJson.Text(constants[i]);
                var segments = text.Split('/');
                RuntimeJson.Require(segments.All(part => part.Length > 0),
                    "trigger-address", nodeId + "." + RuntimeJson.Text(definitions[i], "id"));
                address = text;
            }
            return address;
        }

        // A custom-message entry names the one message it listens for, and a variable-change entry the one variable.
        // There is no "every message" entry: a name that
        // is missing, empty or not a message identifier refuses the plan, so the subscription can index by name.
        static string? TriggerMessage(Node trigger)
        {
            if (VariableContracts.NameFilterParameter(trigger.BindingId) is not { } parameter) return null;
            RuntimeJson.Require(trigger.Parameters.TryGetProperty(parameter, out var value) && value.ValueKind == JsonValueKind.String
                && VariableContracts.IsNameFilter(trigger.BindingId, value.GetString()!), "message-name", trigger.Id);
            return value.GetString();
        }

        // Resource-addressed facts publish their structural resource identity as ScopeId.
        // Resolve the constant once at load; dispatch compares this identity without a resource lookup.
        static string? TriggerResource(Node trigger)
        {
            string? address = null;
            foreach (var parameter in trigger.Registration.Parameters)
            {
                if (RuntimeJson.Text(parameter, "type") != "resource" || RuntimeJson.Text(parameter, "role") != "structural"
                    || OptionalText(parameter, "resourceKind") is not ("wave" or "chained-puzzle" or "item" or "zone" or "objective")) continue;
                if (!trigger.Parameters.TryGetProperty(RuntimeJson.Text(parameter, "id"), out var value) || value.ValueKind == JsonValueKind.Null) continue;
                RuntimeJson.Require(address == null, "trigger-resource-count", trigger.Id);
                address = RuntimeJson.Text(value, "resourceId");
            }
            return address;
        }

        /// <summary>
        /// The structural enum members a trigger node compiled, keyed by parameter id. A trigger declares no
        /// inputs, so an enum parameter it carries can only be a compile-time subscription key — the `key` a key
        /// trigger polls. Carrying them on the entry is what lets the observing provider read the exact set the
        /// loaded plans asked for, through the kernel's own subscription table, instead of reading every member of
        /// the set. A parameter the plan left unwritten is absent, not defaulted: an optional structural enum's
        /// declaration-order default is the contract's answer and never a subscription this loader invented.
        /// </summary>
        static IReadOnlyDictionary<string, string> TriggerEnumParameters(Node trigger)
        {
            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in trigger.Registration.Parameters)
            {
                if (RuntimeJson.Text(parameter, "type") != "enum" || RuntimeJson.Text(parameter, "role") != "structural") continue;
                var name = RuntimeJson.Text(parameter, "id");
                if (!trigger.Parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                members[name] = RuntimeGraphContracts.EnumMemberName(parameter, value);
            }
            return members;
        }

        /// <summary>
        /// The dispatch filters a trigger's structural enum parameters declare, in parameter order. A structural
        /// enum parameter is a subscription key: it narrows the entry to one member of a closed set, and when the
        /// trigger also declares an output port of the same name that port is what the event must carry the member
        /// on. A parameter whose trigger declares no such output takes part in no filter — it is a compile-time
        /// subscription alone. The member index is resolved here, in the output port's own set, so dispatch
        /// compares the integer the payload carries instead of a member name.
        /// </summary>
        static IReadOnlyList<TriggerEnumFilter> TriggerEnumFilters(Node trigger)
        {
            var filters = new List<TriggerEnumFilter>();
            var outputs = trigger.Outputs;
            foreach (var parameter in trigger.Registration.Parameters)
            {
                if (RuntimeJson.Text(parameter, "type") != "enum" || RuntimeJson.Text(parameter, "role") != "structural") continue;
                var name = RuntimeJson.Text(parameter, "id");
                if (!trigger.Parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                var port = -1;
                for (var index = 0; index < outputs.Length; index++)
                    if (RuntimeJson.Text(outputs[index], "type") == "enum" && RuntimeJson.Text(outputs[index], "id") == name) { port = index; break; }
                if (port < 0) continue;
                var member = RuntimeGraphContracts.EnumMemberName(parameter, value);
                var position = Array.IndexOf(RuntimeGraphContracts.EnumMembers(outputs[port]), member);
                RuntimeJson.Require(position >= 0, "trigger-enum-filter", trigger.Id + "." + name);
                filters.Add(new TriggerEnumFilter(name, position));
            }
            return filters;
        }

        // Resolves one step's `inputs` row set against its own contract's input ports. `eventPorts` are the owning
        // entrypoint's trigger outputs; `nodes` are this entrypoint's already-loaded steps, used for `fromStepSlot`
        // bounds/kind checks. The pure-step index vs. consumer-index ordering, and whether a read of a when-present
        // step's forwarded value sits in that step's region, are checked later, once every step's reads are known.
        List<StepInput> ResolveInputs(JsonElement stepRow, Node node, JsonElement[] eventPorts, Node[] nodes)
        {
            var targets = node.Inputs;
            var inputs = new List<StepInput>(); var previous = -1;
            void Add(StepInput input)
            {
                if (input.View.Type == "resource")
                    input.ResourceResolver = kernel.BindResourceResolver(RuntimeJson.Text(input.Port, "resourceKind"));
                inputs.Add(input);
            }
            foreach (var source in RuntimeJson.Rows(stepRow, "inputs", targets.Length))
            {
                RuntimeJson.Require(source.ValueKind == JsonValueKind.Object, "object-required", node.Id);
                var hasLiteral = source.TryGetProperty("value", out var literalValue);
                var hasEvent = source.TryGetProperty("fromEventSlot", out _);
                var hasStep = source.TryGetProperty("fromStepSlot", out var fromStepValue);
                RuntimeJson.Require((hasLiteral ? 1 : 0) + (hasEvent ? 1 : 0) + (hasStep ? 1 : 0) == 1, "literal-with-event-source", node.Id);
                RuntimeJson.Shape(source, hasLiteral ? "slot value" : hasEvent ? "slot fromEventSlot" : "slot fromStepSlot");
                var slot = Slot(source.GetProperty("slot"), targets.Length, "input-slot", node.Id);
                // Sorted and strictly increasing: one driver per input, one canonical spelling per plan.
                RuntimeJson.Require(slot > previous, "input-order", node.Id); previous = slot;
                var target = targets[slot]; var name = RuntimeJson.Text(target, "id");
                RuntimeJson.Require(RuntimeJson.Text(target, "type") != "execution", "execution-slot", node.Id + "." + name);
                if (hasLiteral)
                {
                    // A literal is a value the plan compiled into its own frame. An entity is a live reference; a
                    // handle exists only once a step or an event produced it; an event and a result row are dispatch
                    // identities, not values; and a resource is the one reference a plan may compile, resolved
                    // through the kind's owner below. Each of the others is refused by its own code.
                    var targetType = RuntimeJson.Text(target, "type");
                    RuntimeJson.Require(targetType != "entity", "literal-wrong-type", node.Id + "." + name);
                    RuntimeJson.Require(targetType != "handle", RuntimeAbiCodes.HandleLiteral, node.Id + "." + name);
                    RuntimeJson.Require(targetType != "event", RuntimeAbiCodes.EventLiteral, node.Id + "." + name);
                    RuntimeJson.Require(targetType != "result", "literal-wrong-type", node.Id + "." + name);
                    if (targetType == "resource" && RuntimeGraphContracts.Many(target))
                    {
                        // A list of resources: every member is resolved through the kind's owner exactly as a single
                        // literal is, and the handler reads the list of canonical runtime references.
                        RuntimeJson.Require(literalValue.ValueKind == JsonValueKind.Array && literalValue.GetArrayLength() <= Wire.MaxSetWidth,
                            "literal-wrong-type", node.Id + "." + name);
                        var members = new List<JsonElement>(literalValue.GetArrayLength());
                        foreach (var member in literalValue.EnumerateArray())
                            members.Add(ResolveResourceLiteral(kernel, member, target, node.Id + "." + name).Value);
                        Add(new StepInput(name, null, literalValue, null, false, target) { RuntimeLiteral = RuntimeJson.From(members.ToArray()) });
                        continue;
                    }
                    if (targetType == "resource")
                    {
                        // A resource literal is the plan's own compiled reference, written in the document's
                        // `{id, revision}` form and resolved here through the kind's owner. The value a handler
                        // reads is the canonical runtime reference, built once here rather than translated at every
                        // dispatch; the compiled literal stays on the input, because the frame's constant pool is
                        // encoded from it.
                        var (reference, resolvedReference) = ResolveResourceLiteral(kernel, literalValue, target, node.Id + "." + name);
                        Add(new StepInput(name, null, literalValue, null, false, target)
                        { RuntimeLiteral = reference, ResolvedLiteral = resolvedReference });
                        continue;
                    }
                    try { RuntimeJson.ValidateValue(literalValue, target); }
                    catch (RuntimeContractException) { throw new RuntimeContractException("literal-wrong-type", node.Id + "." + name); }
                    Add(new StepInput(name, null, literalValue, null, false, target));
                    continue;
                }
                JsonElement origin; string? eventPort = null; StepSlotRef? fromStep = null; var guarded = false;
                // A resource reference and an event row both move like every other value once something produced
                // them: an event payload hands one to a step exactly as a `fromStepSlot` output does. Whether the
                // reference is still there, and whether the event is the one this port declares, is asked where it
                // is read, not where it is carried — `event-port-kind` at the boundary.
                if (hasEvent)
                {
                    var eventSlot = Slot(source.GetProperty("fromEventSlot"), eventPorts.Length, "event-port-missing", node.Id);
                    origin = eventPorts[eventSlot];
                    RuntimeJson.Require(RuntimeJson.Text(origin, "type") != "execution", "execution-slot", node.Id + "." + name);
                    eventPort = RuntimeJson.Text(origin, "id");
                }
                else
                {
                    RuntimeJson.Shape(fromStepValue, "step port");
                    var stepField = fromStepValue.GetProperty("step");
                    // Any strictly earlier step may be read — not only a `pure` one. What a step publishes is the
                    // value frame it wrote: a `query` observation, a `pure` computation, a control's own `index`,
                    // `item`, `timer`, `cancelled` or guarded value, or an `action`'s own result row. Every node
                    // kind a loaded plan can carry publishes such a frame, so the only refusal the slot itself
                    // owes is an index that names no slot of this entry.
                    RuntimeJson.Require(stepField.ValueKind == JsonValueKind.Number && stepField.TryGetInt32(out var sourceStepIndex)
                        && sourceStepIndex >= 0 && sourceStepIndex < nodes.Length, "from-step-slot", node.Id);
                    var sourceStep = stepField.GetInt32();
                    var sourceOutputs = nodes[sourceStep].Outputs;
                    var portField = fromStepValue.GetProperty("port");
                    RuntimeJson.Require(portField.ValueKind == JsonValueKind.Number && portField.TryGetInt32(out var portIndex)
                        && portIndex >= 0 && portIndex < sourceOutputs.Length, "from-step-port", node.Id);
                    var portIndexValue = portField.GetInt32();
                    origin = sourceOutputs[portIndexValue];
                    RuntimeJson.Require(RuntimeJson.Text(origin, "type") != "execution", "from-step-port", node.Id);
                    // An `action`'s outputs kept their v2 treatment only while nothing read them; the read makes the
                    // port one this runtime has to move, so it is judged by the rule every declared input is. A result
                    // row is the one output that is not a value: the read is a reference to the activation's typed row
                    // region, addressed through the descriptor the plan compiles for the port the source's own
                    // recipients declaration names, so that port — and only that port — is readable.
                    var rowRead = RuntimeJson.Text(origin, "type") == "result"
                        && RuntimeGraphContracts.ResultPortName(nodes[sourceStep].Contract) == RuntimeJson.Text(origin, "id");
                    RuntimeJson.Require(nodes[sourceStep].Kind != "action" || rowRead || MovablePort(origin), "unsupported-input-port", node.Id + "." + name);
                    // A when-present step's forwarded value is the one value this runtime treats as there: the read
                    // is refused by name anywhere outside the region its `present` exit reaches (checked below,
                    // after all reads are resolved, because an evaluated step's region follows from its readers).
                    guarded = nodes[sourceStep].Control == PresentControl;
                    fromStep = new StepSlotRef(sourceStep, portIndexValue);
                }
                if (RuntimeGraphContracts.IsCommonResultPort(target))
                {
                    var producer = fromStep is { } rowSource ? nodes[rowSource.Step] : null;
                    RequireCommonResultSource(fromStep != null, producer?.Kind, producer?.Contract ?? default,
                        origin, node.Id + "." + name);
                }
                RuntimeJson.Require(RuntimeGraphContracts.AssignableValue(origin, target), "port-mismatch", node.Id + "." + name);
                // A non-nullable "one" output may wire into a "many" input; dispatch wraps it into a
                // one-element collection. A nullable "one" cannot feed "many", and "many" can never feed "one".
                // Both rules ask about the value as the plan proved it: inside its own `present` region the guarded
                // value is there, which is what the branch check above established, so the declaration's `nullable`
                // is what the read would otherwise have been refused for.
                var nullable = RuntimeJson.Flag(origin, "nullable") && !guarded;
                var wrap = !RuntimeGraphContracts.Many(origin) && RuntimeGraphContracts.Many(target) && !nullable;
                RuntimeJson.Require(RuntimeGraphContracts.Many(origin) == RuntimeGraphContracts.Many(target) || wrap, "port-mismatch", node.Id + "." + name);
                RuntimeJson.Require(!nullable || RuntimeJson.Flag(target, "nullable"), "nullable-port", node.Id + "." + name);
                // An unwired `instigator` states that nobody caused the effect, and a handler reads an event that omitted
                // its optional port exactly as it reads an unwired one, so that wire would silently turn an attacker it
                // could not find into that statement. A when-present step's guarded value is how such a port reaches it.
                RuntimeJson.Require(!RuntimeJson.Flag(origin, "optional")
                    || (RuntimeJson.Flag(target, "optional") && name != "instigator") || RuntimeJson.Flag(target, "nullable"), "optional-event-port", node.Id + "." + name);
                Add(new StepInput(name, eventPort, null, fromStep, wrap, origin));
            }
            // A resource input is satisfied by its literal alone, so it is not required to appear in the wiring.
            foreach (var target in targets.Where(p => RuntimeJson.Text(p, "type") is not ("execution" or "resource") && !RuntimeJson.Flag(p, "optional")))
                RuntimeJson.Require(inputs.Any(i => i.Name == RuntimeJson.Text(target, "id")), "missing-input", node.Id + "." + RuntimeJson.Text(target, "id"));
            return inputs;
        }

        var total = 0;
        // Per-entry step material, kept until every entry has been proved canonical: a frame descriptor cannot be
        // built before the whole plan's step array is known, and the descriptor is what a ResolvedStep carries.
        var entryNodes = new List<Node[]>();
        var entryContracts = new List<EntryContract>();
        var entryStart = new List<int>();
        var entryTrigger = new List<(string NodeId, string BindingId, JsonElement Contract, string? TriggerAddress, IReadOnlyDictionary<string, string> EnumParameters, IReadOnlyList<TriggerEnumFilter> Filters, string? ResourceScope, string? MessageName)>();
        // One entry's trigger options, or null where its card declares none. Parsed here and carried on the
        // entrypoint: the options are a structural property of the entry, so nothing about them is re-read per event.
        var entryGate = new List<TriggerGateOptions?>();
        foreach (var entry in entryRows)
        {
            RuntimeJson.Shape(entry, "nodeId binding constants promoted start steps", "gate");
            // The trigger is resolved first: its own contract is what says whether the fact this entry point is
            // gated on carries a shot identity, which a `shot`-scoped gate is refused without.
            var trigger = Load(entry, "trigger");
            var gate = TriggerGateContract.Parse(entry, attachments, trigger.Contract);
            var eventPorts = trigger.Outputs;
            var stepRows = RuntimeJson.Rows(entry, "steps", ceiling.MaxStepsPerEntrypoint);
            RuntimeJson.Require(stepRows.Length > 0, "empty-entrypoint", trigger.Id); total += stepRows.Length;
            var nodes = new Node[stepRows.Length];
            for (var i = 0; i < stepRows.Length; i++)
            {
                var stepRow = stepRows[i];
                RuntimeJson.Shape(stepRow, "nodeId nodeKind binding constants promoted inputs successors", "effect cost_policy cost");
                var nodeKind = RuntimeJson.Text(stepRow, "nodeKind");
                RuntimeJson.Require(nodeKind is "action" or "control" or "pure" or "query", "node-kind", RuntimeJson.Text(stepRow, "nodeId"));
                nodes[i] = Load(stepRow, nodeKind);
            }
            // The entry's own start is proved before anything reads the walk: the when-present region below is a
            // property of that walk, and a plan whose start is not a step has no region to compute. The website's
            // plan reader validates `start` at the same point, so both sides refuse the same plan for the same
            // reason.
            var startRaw = entry.GetProperty("start");
            var startIndex = -1;
            RuntimeJson.Require(startRaw.ValueKind == JsonValueKind.Number && startRaw.TryGetInt32(out startIndex)
                && startIndex >= 0 && startIndex < stepRows.Length && nodes[startIndex].Kind is "action" or "control", "entry-start", trigger.Id);
            var successors = new int?[stepRows.Length][];
            for (var i = 0; i < stepRows.Length; i++)
            {
                var node = nodes[i];
                var expected = node.Kind is "pure" or "query" ? 0 : node.Outputs.Count(p => RuntimeJson.Text(p, "type") == "execution");
                var raw = RuntimeJson.Rows(stepRows[i], "successors", ceiling.MaxStepsPerEntrypoint);
                RuntimeJson.Require(raw.Length == expected, node.Kind is "pure" or "query" ? "pure-successor" : "successor-shape", node.Id);
                var successorsOfStep = new int?[raw.Length];
                for (var j = 0; j < raw.Length; j++)
                {
                    if (raw[j].ValueKind == JsonValueKind.Null) { successorsOfStep[j] = null; continue; }
                    RuntimeJson.Require(raw[j].ValueKind == JsonValueKind.Number && raw[j].TryGetInt32(out var successor)
                        && successor >= 0 && successor < stepRows.Length, "successor-shape", node.Id);
                    successorsOfStep[j] = raw[j].GetInt32();
                }
                successors[i] = successorsOfStep;
            }
            var stepInputs = new List<StepInput>[stepRows.Length];
            for (var i = 0; i < stepRows.Length; i++)
            {
                stepInputs[i] = ResolveInputs(stepRows[i], nodes[i], eventPorts, nodes);
                // Proof travels with the producer, not with the downstream consumer's desired kind.
                var flowed = RuntimeEntityKindFlow.Resolve(nodes[i].Contract,
                    input => stepInputs[i].FirstOrDefault(value => value.Name == input)?.Port,
                    RuntimeEntityKindFlow.FilterKind(nodes[i].Registration.CapabilityId,
                        nodes[i].Resolved.HandlerParametersFor(nodes[i].Registration.Capability)));
                if (!flowed.Equals(nodes[i].Contract))
                    nodes[i] = nodes[i] with { Contract = flowed, Inputs = RuntimeJson.Rows(flowed, "inputs"), Outputs = RuntimeJson.Rows(flowed, "outputs") };
            }
            // A guarded value is read only inside the region of the step that tested it: another when-present step's
            // region proves that step's value, not this one.
            var presentRegions = PresentRegions(nodes, successors, startIndex, stepInputs);
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var input in stepInputs[i])
                    if (input.FromStep is { } guard && guard.Step < nodes.Length && nodes[guard.Step].Control == PresentControl)
                        RuntimeJson.Require(presentRegions[guard.Step]?[i] == true, "present-branch", nodes[i].Id + "." + input.Name);
            // Edge direction: a successor always names a strictly later step; a pure read always names a strictly earlier one.
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var next in successors[i])
                    if (next is int target) RuntimeJson.Require(target > i && nodes[target].Kind is "action" or "control", "successor-index", nodes[i].Id);
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var input in stepInputs[i])
                    if (input.FromStep is { } from) RuntimeJson.Require(from.Step < i, "from-step-slot", nodes[i].Id);
            // A `repeat` or `for_each` runs in the shape its filled inputs pick, and each shape publishes only its own
            // outputs: the shape is fixed here, once, and a read of the other shape's output is refused by name.
            for (var i = 0; i < stepRows.Length; i++)
                if (nodes[i].Kind == "control") nodes[i] = nodes[i] with { Loop = LoopShapeOf(nodes[i], stepInputs[i]) };
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var input in stepInputs[i])
                    if (input.FromStep is { } from && nodes[from.Step].Kind == "control")
                        RequireLoopOutput(nodes[from.Step], RuntimeJson.Text(nodes[from.Step].Outputs[from.Port], "id"));
            // Canonical order: Kahn's algorithm over both edge kinds, ties broken by nodeId ordinal. The compiled
            // array must equal this order exactly, item by item.
            var indegree = new int[stepRows.Length];
            var dependents = new List<int>[stepRows.Length];
            for (var i = 0; i < stepRows.Length; i++) dependents[i] = new List<int>();
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var next in successors[i]) if (next is int target) { dependents[i].Add(target); indegree[target]++; }
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var input in stepInputs[i]) if (input.FromStep is { } from) { dependents[from.Step].Add(i); indegree[i]++; }
            var visited = new bool[stepRows.Length];
            for (var position = 0; position < stepRows.Length; position++)
            {
                var candidate = -1; string? candidateId = null;
                for (var i = 0; i < stepRows.Length; i++)
                {
                    if (visited[i] || indegree[i] != 0) continue;
                    if (candidate == -1 || string.CompareOrdinal(nodes[i].Id, candidateId) < 0) { candidate = i; candidateId = nodes[i].Id; }
                }
                RuntimeJson.Require(candidate != -1, "step-order", trigger.Id);
                RuntimeJson.Require(candidate == position, "step-order", nodes[candidate].Id);
                visited[candidate] = true;
                foreach (var dependent in dependents[candidate]) indegree[dependent]--;
            }
            // Reachability: every action/control step must be reachable from `start`; every pure step must be read,
            // directly or transitively, by some reachable step. `start` itself was proved where the walk's
            // successors were read, because the when-present region is a property of that same walk.
            var start = startIndex;
            var reachable = new bool[stepRows.Length]; reachable[start] = true;
            bool changed;
            do
            {
                changed = false;
                for (var i = 0; i < stepRows.Length; i++)
                {
                    if (!reachable[i]) continue;
                    foreach (var next in successors[i]) if (next is int target && !reachable[target]) { reachable[target] = true; changed = true; }
                    foreach (var input in stepInputs[i]) if (input.FromStep is { } from && !reachable[from.Step]) { reachable[from.Step] = true; changed = true; }
                }
            } while (changed);
            for (var i = 0; i < stepRows.Length; i++) RuntimeJson.Require(reachable[i], "unreachable-step", nodes[i].Id);
            // Everything a frame descriptor needs about this entry, in the entry's own order: the builder runs once
            // for the whole plan, after this loop, because a plan-wide step index is what `fromStepSlot` addresses.
            entryContracts.Add(new EntryContract(trigger.Id, trigger.Contract, Enumerable.Range(0, stepRows.Length)
                .Select(i => new StepContract(nodes[i].Id, nodes[i].Kind, nodes[i].Graph, nodes[i].Contract, nodes[i].Constants, nodes[i].Promoted,
                    stepInputs[i], successors[i], pinIndices[nodes[i].BindingId], nodes[i].HandlerIndex)).ToArray()));
            entryNodes.Add(nodes);
            entryStart.Add(start);
            entryTrigger.Add((trigger.Id, trigger.BindingId, trigger.Contract, trigger.TriggerAddress, TriggerEnumParameters(trigger), TriggerEnumFilters(trigger), TriggerResource(trigger), TriggerMessage(trigger)));
            entryGate.Add(gate);
        }
        RuntimeJson.Require(entryContracts.Count > 0 && total <= ceiling.MaxTotalSteps, "plan-step-budget", id);
        // The website and this runtime each derive every node's port layout from their own block table; the plan
        // records the website's digest and is refused whole when the two derivations differ.
        Span<byte> derivedLayout = stackalloc byte[32];
        layoutHash.GetHashAndReset(derivedLayout);
        RuntimeJson.Require(plan.GetProperty("layoutHash").ValueKind == JsonValueKind.String
            && plan.GetProperty("layoutHash").ValueEquals(Convert.ToHexString(derivedLayout).ToLowerInvariant()),
            "layout-mismatch", "The plan's blocks resolve to another port layout on this runtime than on the website that compiled it: " + id);
        // Holder mounts must name every entry's canonical single-player subject, never its damage source or gun.
        foreach (var attachment in attachments)
            if (attachment.Port is { } hostPort)
                RuntimeJson.Require(attachment.Kind == "gear-block"
                    ? entryTrigger.All(entry => HolderSubject(entry.Contract) is { } subject && RuntimeJson.Text(subject, "id") == hostPort)
                    : entryTrigger.Any(entry => RuntimeJson.Rows(entry.Contract, "outputs")
                        .Any(output => RuntimeJson.Text(output, "type") == "entity" && RuntimeJson.Text(output, "id") == hostPort)),
                    RuntimeAbiCodes.AttachmentPort, hostPort);
        // Per-trigger command bound: every entrypoint an event can claim contributes its dispatchable steps, and a
        // single event walks at most one path through each of them. A message trigger is bucketed by the message it
        // names; resource/enum filters exclude incompatible listeners before the address capacity is applied.
        foreach (var group in entryContracts.Select((entry, index) => (entry, trigger: entryTrigger[index])).GroupBy(x => (x.trigger.BindingId, x.trigger.MessageName)))
            RuntimeJson.Require(EventCommandBound.Of(group.Select(x => new EventCommandClaim(x.trigger.TriggerAddress,
                x.entry.Steps.Count(step => step.NodeKind is not ("pure" or "query")), x.trigger.ResourceScope, x.trigger.Filters)),
                EventCommandBound.ScopesPerEvent(group.First().trigger.Contract)) <= limits.MaxCommandsPerTick, "event-command-budget", id);
        var closure = registry.Closure(used);
        RuntimeJson.ExactSet(pins, closure, "binding-closure");
        RuntimeJson.ExactSet(RuntimeJson.Strings(plan.GetProperty("dependencies")), registry.Packages(closure), "dependency-lock");
        var permissions = RuntimeJson.Strings(plan.GetProperty("permissions"));
        RuntimeJson.ExactSet(permissions, closure.SelectMany(b => registry.Support[b].RequiredPermissions), "permission-lock");
        // Frame descriptors are the last thing a plan gets: every step's slots, its copy sources and the constant
        // pool are fixed here, once, so dispatch never resolves a port name or a JSON value again. Steps are
        // addressed plan-wide (an entry's steps are contiguous from its own offset), which is what `fromStepSlot` names.
        // The plan's variables: `variables[]` declares the per-scope names. Names are the world's addresses, so a name
        // may appear once in a plan and the store refuses one another loaded plan declares differently.
        var variableRows = RuntimeVariableContract.ParsePlanVariables(plan).ToArray();
        var variableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in variableRows) RuntimeJson.Require(variableNames.Add(row.Name), "duplicate-variable", row.Name);
        // A resource variable's initial choice is resolved here, through the kind's owner, exactly like a resource
        // literal on a step: a plan that starts from a reference nothing owns is refused whole, never started with
        // another resource or none.
        foreach (var row in variableRows)
            if (row.ResourceKind != null && row.Initial.ValueKind != JsonValueKind.Null)
            {
                var initialId = RuntimeJson.Text(row.Initial, "resourceId");
                RuntimeJson.Require(kernel.ResolveCompiledResource(row.ResourceKind, initialId) != null, RuntimeAbiCodes.StaleResource, row.Name);
            }
        var frame = PlanFrames.Build(entryContracts, limits);
        var resolved = new List<ResolvedEntry>(entryContracts.Count);
        var holderMatchers = attachments.Select(attachment =>
        {
            if (attachment.Kind != "gear-block" || attachment.Port == null) return null;
            var holder = registry.AttachmentMatchers[attachment.Kind].Matcher.Holder!(attachment.Reference);
            RuntimeJson.Require(holder != null, RuntimeAbiCodes.AttachmentPort, attachment.Reference);
            return holder;
        }).ToArray();
        var stepOffset = frame.Start;
        for (var entry = 0; entry < entryContracts.Count; entry++)
        {
            var steps = new List<ResolvedStep>(entryContracts[entry].Steps.Count);
            for (var i = 0; i < entryContracts[entry].Steps.Count; i++)
            {
                var step = entryContracts[entry].Steps[i];
                var bindingId = entryNodes[entry][i].BindingId;
                var registration = entryNodes[entry][i].Registration;
                var binding = registration.Binding;
                var capabilityId = registration.CapabilityId;
                var capability = registration.Capability;
                var execution = capability.TryGetProperty("graph", out var graph) ? RuntimeJson.Text(graph, "execution") : "";
                var parameterDefinitions = registration.Parameters;
                var handlerParameters = entryNodes[entry][i].Resolved.HandlerParametersFor(capability);
                steps.Add(new ResolvedStep(step.NodeId, step.NodeKind, bindingId, step.BindingIndex, step.HandlerIndex,
                    entryNodes[entry][i].Parameters, step.Inputs, step.Promoted, step.Successors, step.Contract, frame.Steps[stepOffset + i],
                    parameterDefinitions,
                    registration.ProviderId, capabilityId, execution,
                    "," + RuntimeJson.Quote(id) + "," + RuntimeJson.Quote(step.NodeId) + "]",
                    entryNodes[entry][i].Control, entryNodes[entry][i].ValuePort, entryNodes[entry][i].Effect,
                    entryNodes[entry][i].Cost, entryNodes[entry][i].Loop)
                {
                    Capability = capability,
                    Handler = registry.Handlers.TryGetValue(bindingId, out var handler) ? handler : null,
                    Evaluator = registry.Evaluators.TryGetValue(bindingId, out var evaluator) ? evaluator : null,
                    OwnerTier = execution == "owner" || registry.OwnerSessions.ContainsKey(capabilityId),
                    // Registration refuses a shape that keeps plays on any handler but a presentation one.
                    HoldsPlay = registry.Shapes.TryGetValue(bindingId, out var shape) && shape.KeyedByPlay,
                    LogGate = kernel.PlanLogGate(registration.ProviderId),
                    RequiredActorRoles = registration.RequiredActorRoles,
                    HandlerParameters = handlerParameters,
                    ParameterValues = HandlerParameterValues(entryNodes[entry][i].Parameters, handlerParameters,
                        parameterDefinitions),
                    InputConstantValues = HandlerInputValues(step.Inputs, step.NodeKind),
                    RecipientsInput = graph.ValueKind == JsonValueKind.Object && graph.TryGetProperty("recipients", out var recipients)
                        && recipients.ValueKind == JsonValueKind.Object && OptionalText(recipients, "input") is { } recipientsInput && RuntimeJson.IsText(recipientsInput) ? recipientsInput : null
                });
            }
            for (var index = 0; index < steps.Count; index++)
            {
                var stored = steps[index];
                stored.ControlSuccessors = stored.Successors.Select(value => value ?? -1).ToArray();
                if (stored.NodeKind == "control") stored.ControlInputs = RuntimeKernel.ControlInputSlots(stored);
                if (stored.Control == VariableContracts.LogRecordCapability)
                    stored.RecordValues = RuntimeKernel.AuthorRecordValues(stored.HandlerParameters);
                if (stored.Execution == "presentation")
                {
                    stored.RecipientsInputSlot = Array.IndexOf(stored.HandlerInputs, stored.RecipientsInput);
                    stored.PresentationConstants = new bool[stored.HandlerInputCount];
                    for (var input = 0; input < stored.Inputs.Count; input++)
                        if (stored.InputSlots[input] >= 0 && stored.Inputs[input].HandlerLiteral != null)
                            stored.PresentationConstants[stored.InputSlots[input]] = true;
                }
                if (stored.Control is VariableContracts.ReadCapability or VariableContracts.WriteCapability or Blocks.ControlFlowPresent.Id)
                {
                    if (stored.Control != Blocks.ControlFlowPresent.Id)
                    {
                        var name = RuntimeJson.Text(stored.HandlerParameters, "name");
                        stored.Variable = Array.Find(variableRows, row => row.Name == name);
                        RuntimeJson.Require(stored.Variable != null, "variable-undeclared", name);
                        RuntimeJson.Require(VariableValueTypes.IsPortTypeOf(RuntimeJson.Text(stored.HandlerParameters, "value_type"), stored.Variable!.Type),
                            "variable-type", name);
                    }
                    stored.VariableInput = Array.IndexOf(stored.HandlerInputs, "value");
                    stored.SubjectInput = Array.IndexOf(stored.HandlerInputs, "subject");
                    stored.WeaponSlotInput = Array.IndexOf(stored.HandlerInputs, "slot");
                    var outputs = RuntimeJson.Rows(stored.Contract, "outputs");
                    stored.ValueOutput = Array.FindIndex(outputs, port => RuntimeJson.Text(port, "id") == "value");
                    stored.WrittenOutput = Array.FindIndex(outputs, port => RuntimeJson.Text(port, "id") == "written");
                    stored.PreviousOutput = Array.FindIndex(outputs, port => RuntimeJson.Text(port, "id") == "previous");
                    stored.StoredInputs = new RuntimeKernel.StepInputs(stored);
                    stored.StoredOutputs = new JsonElement[outputs.Length];
                    stored.StoredResolved = new ResourceRef?[outputs.Length];
                }
                // Every step owns one input frame, reset and refilled on each execution instead of a new one per
                // execution: a handler's context stops exposing its inputs when the invocation ends, a presentation
                // request copies its payload out before the step returns, and the two kernel records that outlive
                // a step (an effect layer, an owner capture) keep a snapshot of the frame rather than the frame.
                else stored.StoredInputs = new RuntimeKernel.StepInputs(stored);
                if (stored.Execution == "presentation") stored.BoundaryInputs = new RuntimeKernel.StepInputs(stored);
                if (stored.OutputViews is { } views && stored.Evaluator != null)
                {
                    stored.Evaluated = new EvaluationOutputs(Array.ConvertAll(views, view => view.Id),
                        DeclaredOutputs(registry.Shapes.TryGetValue(stored.BindingId, out var evaluatorShape) ? evaluatorShape : null, views));
                    stored.EvaluatedEntities = new RuntimeKernel.EntityCheck[]?[views.Length];
                }
                if (steps[index].NodeKind == "action" && steps[index].Frames.Row != null)
                    steps[index] = steps[index] with { ResultDerivedSteps = ResultDerivedSteps(steps, index) };
                if (HasResultDecision(steps, index)) steps[index] = steps[index] with { HasResultDecision = true };
                if (!steps[index].ProducesSound) continue;
                var soundPort = Array.FindIndex(RuntimeJson.Rows(steps[index].Contract, "outputs"),
                    port => RuntimeJson.Text(port, "id") == "sound");
                steps[index] = steps[index] with
                {
                    TracksSound = steps.Any(consumer => consumer.Inputs.Any(input =>
                    input.FromStep is { } source && source.Step == index && source.Port == soundPort))
                };
            }
            var executionEdges = steps.Select(step => step.Successors.ToArray()).ToArray();
            var loopBodies = new List<(int Loop, bool[] Body, int Size)>();
            for (var index = 0; index < steps.Count; index++)
            {
                var loop = steps[index];
                if (loop.Control is not (Blocks.ControlFlowRepeat.Id or Blocks.ControlFlowForEach.Id)
                    || (loop.Loop & LoopShape.Scheduled) != 0) continue;
                var body = Reachable(executionEdges, loop.Successors[1] ?? -1, null);
                loop.RoundResetSteps = Enumerable.Range(0, steps.Count)
                    .Where(at => body[at] || steps[at].NodeKind is "pure" or "query").ToArray();
                loopBodies.Add((index, body, body.Count(inside => inside)));
            }
            // A step that holds plays is one play per round of every synchronous loop it runs in: the loops, outermost
            // first, whose round (a candidate's identity, or the index) names the play (RuntimeKernel.PlayIdentity).
            for (var index = 0; index < steps.Count; index++)
                if (steps[index].HoldsPlay)
                    steps[index].PlayLoops = loopBodies.Where(loop => loop.Body[index])
                        .OrderByDescending(loop => loop.Size).Select(loop => loop.Loop).ToArray();
            stepOffset += steps.Count;
            var resolvedEntry = new ResolvedEntry(entryContracts[entry].NodeId, entryTrigger[entry].BindingId, entryStart[entry], steps, entryContracts[entry].Trigger, entryTrigger[entry].TriggerAddress, entryGate[entry], entryTrigger[entry].EnumParameters, entryTrigger[entry].Filters, entryTrigger[entry].ResourceScope, entryTrigger[entry].MessageName);
            resolved.Add(resolvedEntry with
            {
                Attachments = attachments.Select((attachment, index) => new ResolvedAttachment(
                attachment, registry.AttachmentMatchers[attachment.Kind].Matcher,
                resolvedEntry.TriggerOutputs.Where(port => port.Type == "entity" && (attachment.Port == null || port.Id == attachment.Port)).ToArray(),
                holderMatchers[index], attachment.Level == null ? null : registry.AttachmentMatchers["level"].Matcher.Scope,
                table.Binding(resolvedEntry.BindingId).EquipmentAttachment)).ToArray()
            });
        }
        return new ResolvedPlan(id, resourceId, revision, domain, limits, resolved, closure, permissions, attachments, frame, variableRows);
    }

    /// <summary>
    /// One compiled resource reference, resolved once at load through the provider that owns the port's kind. The
    /// reference is the same one the constant pool holds, so a plan that names a resource nothing owns is refused
    /// before any step runs. Only the registry's static declaration is read here: a provider that owns the kind has
    /// the resource, and one that does not — or a kind no package registered at all — leaves the plan with a
    /// reference to a world that has ended, which is `stale-resource`. A provider's own answer cannot be inspected
    /// further here, because a plan is loaded before the world it was compiled for is necessarily the one in front
    /// of it. What the step's consumers read is returned in the canonical runtime spelling the port's own kind and
    /// the literal's own id make, so the two spellings meet in exactly this one place.
    /// </summary>
    private static (JsonElement Value, ResourceRef Resolved) ResolveResourceLiteral(RuntimeKernel kernel, JsonElement literal, JsonElement port, string at)
    {
        var kind = RuntimeJson.Text(port, "resourceKind");
        var id = RuntimeJson.ResourceLiteral(literal);
        var resolved = kernel.ResolveCompiledResource(kind, id);
        if (resolved == null && kernel.ToleratesMissing(kind))
            return (RuntimeJson.ResourceRuntimeValue(literal, port), new ResourceRef(kind, id));
        RuntimeJson.Require(resolved != null, RuntimeAbiCodes.StaleResource, at);
        kernel.RequireResourceRevision(kind, id, RuntimeJson.Text(literal, "revision"), at);
        return (RuntimeJson.ResourceRuntimeValue(literal, port), resolved!);
    }

    /// <summary>
    /// The registered contract resolved for this node's constants and promotions, every port a type this wire
    /// knows and its value-type choice already made. Derive once per binding/constants/promotion.
    /// </summary>
    private static JsonElement AuthoritativeContract(JsonElement contract, string nodeId)
    {
        var fields = contract.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        foreach (var side in new[] { "inputs", "outputs" })
        {
            var ports = RuntimeJson.Rows(contract, side);
            var typed = new JsonElement[ports.Length];
            for (var index = 0; index < ports.Length; index++)
            {
                RuntimeJson.Require(Array.IndexOf(Wire.PortTypes, RuntimeJson.Text(ports[index], "type")) >= 0,
                    "layout-mismatch", nodeId + "." + side + "." + index);
                var port = ports[index].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                // The structural value-type parameter has already selected this concrete member. Keeping its
                // marker would make handler-shape resolution interpret the port as an unresolved choice again.
                port.Remove("valueTypeParameter");
                typed[index] = RuntimeJson.From(port);
            }
            fields[side] = RuntimeJson.From(typed);
        }
        return RuntimeJson.From(fields);
    }

    private static CommandValue[] HandlerParameterValues(JsonElement wireParameters, JsonElement handlerParameters,
        JsonElement[] definitions)
    {
        var values = new CommandValue[definitions.Length];
        if (handlerParameters.ValueKind != JsonValueKind.Object) return values;
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var name = RuntimeJson.Text(definition, "id");
            if (handlerParameters.TryGetProperty(name, out var value))
                values[index] = CommandValue.Read(value, ResolvedStep.ParameterKind(definition), many: false)
                    .WithWire(wireParameters.GetProperty(name));
        }
        return values;
    }

    /// <summary>The step's output index for each output the evaluator's shape declares, in declaration order; null
    /// where the shape declares none, which addresses the outputs in contract order. A declared output this step's
    /// resolved contract does not carry (a mode that publishes fewer ports) is -1, and writing it is refused.</summary>
    private static int[]? DeclaredOutputs(HandlerShape? shape, PortView[] views)
    {
        if (shape == null || shape.OutputPorts.Count == 0) return null;
        var map = new int[shape.OutputPorts.Count];
        for (var i = 0; i < map.Length; i++)
        {
            var id = shape.OutputPorts[i];
            map[i] = Array.FindIndex(views, view => view.Id == id);
        }
        return map;
    }

    private static CommandValue[] HandlerInputValues(IReadOnlyList<StepInput> inputs, string nodeKind)
    {
        var values = new CommandValue[inputs.Count];
        if (nodeKind != "action") return values;
        for (var index = 0; index < inputs.Count; index++)
            // The cell is what the handler reads, so an enum literal is its member name, not the compiled index.
            if (inputs[index].HandlerLiteral is { } literal)
                values[index] = CommandValue.Read(inputs[index].View.HandlerValue(literal), inputs[index].View.HandlerKind,
                    inputs[index].View.Many, inputs[index].ResolvedLiteral, inputs[index].ResourceResolver).WithWire(literal);
        return values;
    }

    /// <summary>A declared port field that may legitimately be absent, read as text only when it is a string.</summary>
    private static string? OptionalText(JsonElement port, string key)
        => port.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Only value computations and control forwarding derive a memo from a result. Another action's
    /// outputs are its own execution snapshot, so dependency invalidation stops at that action boundary.</summary>
    internal static int[] ResultDerivedSteps(IReadOnlyList<ResolvedStep> steps, int producerIndex)
    {
        var derived = new bool[steps.Count];
        var readers = new List<int>();
        for (var index = producerIndex + 1; index < steps.Count; index++)
        {
            if (steps[index].NodeKind is not ("pure" or "query" or "control")) continue;
            foreach (var input in steps[index].Inputs)
            {
                if (input.FromStep is not { } source || source.Step < 0 || source.Step >= index) continue;
                if (!(source.Step == producerIndex && input.ResultPort) && !derived[source.Step]) continue;
                derived[index] = true; readers.Add(index); break;
            }
        }
        return readers.ToArray();
    }

    /// <summary>A direct row decision, or one numeric comparison of the count with a literal, handles an
    /// unsuccessful action. No intermediate action, sequence, delay, loop or arbitrary value chain grants
    /// continuation. The existing present-region proof still governs every use of a forwarded nullable value.</summary>
    internal static bool HasResultDecision(IReadOnlyList<ResolvedStep> steps, int producerIndex)
    {
        var producer = steps[producerIndex];
        if (producer.NodeKind != "action" || producer.Frames.Row == null || producer.Successors.Count != 1
            || producer.Successors[0] is not int decisionIndex) return false;
        var decision = steps[decisionIndex];
        if (decision.NodeKind != "control") return false;
        var decisionInput = decision.Control switch
        {
            Blocks.ControlFlowBranch.Id => "condition",
            Blocks.ControlFlowEnumSwitch.Id or PresentControl => "value",
            _ => null
        };
        if (decisionInput == null) return false;
        var input = decision.Inputs.FirstOrDefault(value => value.Name == decisionInput);
        if (input?.FromStep is not { } source) return false;
        var reader = steps[source.Step];
        if (decision.Control == Blocks.ControlFlowBranch.Id
            && CountDecision(steps, reader, source.Port, producerIndex)) return true;
        if (!ResultReaderOf(reader, producer, producerIndex, "row")) return false;
        var selected = RuntimeJson.Rows(reader.Contract, "outputs")[source.Port];
        var output = RuntimeJson.Text(selected, "id");
        if (decision.Control == Blocks.ControlFlowEnumSwitch.Id) return output == "status";
        if (decision.Control != PresentControl || output != "committed" || decision.Successors.Count != 2) return false;
        // Missing may handle an unknown commit directly. A known bool may end here, but any connected known
        // path must immediately branch on this guard's own forwarded value, not merely on its presence or on
        // another value. The normal present-region proof separately protects every read of that value.
        if (decision.Successors[0] is not int knownIndex) return true;
        var known = steps[knownIndex];
        if (known.NodeKind != "control" || known.Control != Blocks.ControlFlowBranch.Id) return false;
        var condition = known.Inputs.FirstOrDefault(value => value.Name == "condition");
        if (condition?.FromStep is not { } forwarded || forwarded.Step != decisionIndex) return false;
        var valuePort = RuntimeJson.Rows(decision.Contract, "outputs")[forwarded.Port];
        return decision.ValuePort != null && RuntimeJson.Text(valuePort, "id") == decision.ValuePort
            && RuntimeJson.Text(valuePort, "type") == "boolean";
    }

    private static bool ResultReaderOf(ResolvedStep reader, ResolvedStep producer, int producerIndex, string mode)
    {
        if (reader.NodeKind != "query" || reader.CapabilityId != Blocks.QueryResultRead.Id
            || reader.HandlerParameters.ValueKind != JsonValueKind.Object
            || !reader.HandlerParameters.TryGetProperty("mode", out var selected)
            || selected.ValueKind != JsonValueKind.String || selected.GetString() != mode) return false;
        var rows = reader.Inputs.FirstOrDefault(value => value.Name == "result");
        return rows?.FromStep is { } resultSource && resultSource.Step == producerIndex && rows.ResultPort
            && RuntimeGraphContracts.ResultPortName(producer.Contract) == RuntimeJson.Text(rows.Port, "id");
    }

    private static bool CountDecision(IReadOnlyList<ResolvedStep> steps, ResolvedStep comparison, int output, int producerIndex)
    {
        if (comparison.NodeKind is not ("query" or "pure") || comparison.CapabilityId != Blocks.ConditionPredicateCompare.Id
            || RuntimeJson.Text(RuntimeJson.Rows(comparison.Contract, "outputs")[output], "id") != "value") return false;
        if (comparison.Inputs.Any(input => input.Name is not ("left" or "right" or "operator" or "tolerance"))) return false;
        var ports = RuntimeJson.Rows(comparison.Contract, "inputs");
        if (ports.Where(port => RuntimeJson.Text(port, "id") is "left" or "right")
            .Any(port => RuntimeJson.Text(port, "type") is not ("integer" or "number"))) return false;
        var left = comparison.Inputs.FirstOrDefault(value => value.Name == "left");
        var right = comparison.Inputs.FirstOrDefault(value => value.Name == "right");
        var operation = comparison.Inputs.FirstOrDefault(value => value.Name == "operator");
        if (operation?.HandlerLiteral is not { } literalOperator) return false;
        var member = operation.View.HandlerValue(literalOperator);
        if (member.ValueKind != JsonValueKind.String || member.GetString() is not ("eq" or "ne" or "lt" or "lte" or "gt" or "gte")) return false;
        var tolerance = comparison.Inputs.FirstOrDefault(value => value.Name == "tolerance");
        if (tolerance != null && (!NumberLiteral(tolerance, out var toleranceValue) || toleranceValue != 0)) return false;
        return CountSource(left, right) || CountSource(right, left);

        bool CountSource(StepInput? count, StepInput? constant)
        {
            if (count?.FromStep is not { } source || !NumberLiteral(constant, out _)) return false;
            var reader = steps[source.Step];
            return ResultReaderOf(reader, steps[producerIndex], producerIndex, "count")
                && RuntimeJson.Text(RuntimeJson.Rows(reader.Contract, "outputs")[source.Port], "id") == "count";
        }
        static bool NumberLiteral(StepInput? input, out double value)
        {
            value = 0;
            return input?.HandlerLiteral is { ValueKind: JsonValueKind.Number } literal
                && literal.TryGetDouble(out value) && double.IsFinite(value);
        }
    }

    /// <summary>
    /// Whether a port carries a value this runtime can move across a step boundary: every value type plus a
    /// handle — the same set the boundary validator resolves, so a port which passes here is one
    /// <see cref="RuntimeJson.ValidateValue"/> can judge. A collection is one head slot plus its elements, so it is
    /// the narrower set of kinds with an element form: `resource`, `event`, `result` and `policy` are reference or
    /// row carriers with no per-element form and are refused here, before a frame is built, rather than reserved a
    /// width nothing fills. Asked of every declared step input, of a trigger's own event outputs, and of an
    /// `action` output the moment a later step reads it.
    /// </summary>
    private static bool MovablePort(JsonElement port)
    {
        var type = RuntimeJson.Text(port, "type");
        return (Wire.RuntimeValueTypes.Contains(type) || type == "handle")
            && (!RuntimeGraphContracts.Many(port) || RuntimeGraphContracts.Segmented(type));
    }

    /// <summary>
    /// Which steps a when-present step's guarded value may be read in, within one entrypoint: the steps every path
    /// from the entry's `start` reaches through that step's `present` exit. Ruling 158.2 states the rule as
    /// dominance, and it is the website's own rule (`Web/src/forge/control-lowering.ts` `presentRegions`): a step
    /// two paths meet at after the guard has a way in that never tested the value, so reading the value there
    /// would read a possibly-absent one. It is the exit *edge* that is dominated, not the step it enters — a step
    /// another path also enters may run without the test — which is what removing that one edge and asking what is
    /// still reachable states exactly.
    ///
    /// A `pure` or `query` step has no successor: it is evaluated when a step reads it, inside that step's activation.
    /// So it belongs to one exit's region when it has at least one reader and every reader, directly or through
    /// further evaluated steps, is in that same region — then it is only ever evaluated where the tested value is
    /// there. A step read both inside and outside stays outside.
    ///
    /// Each when-present step has its own region, indexed by that step; a step whose `present` exit is not wired, and
    /// every other step, has none. A region proves only the value its own step tested.
    /// </summary>
    private static bool[]?[] PresentRegions(Node[] nodes, int?[][] successors, int start, List<StepInput>[] stepInputs)
    {
        var regions = new bool[]?[nodes.Length];
        var everything = Reachable(successors, start, null);
        for (var step = 0; step < nodes.Length; step++)
        {
            // The exit order is the shape table's: `present` is output 0, which `ExitsMatch` has already proved.
            if (nodes[step].Control != PresentControl || successors[step].Length == 0 || successors[step][0] is not int gate) continue;
            var without = Reachable(successors, start, (step, gate));
            var inside = new bool[nodes.Length];
            for (var at = 0; at < nodes.Length; at++) inside[at] = everything[at] && !without[at];
            for (var grew = true; grew;)
            {
                grew = false;
                for (var at = 0; at < nodes.Length; at++)
                {
                    if (inside[at] || nodes[at].Kind is not ("pure" or "query")) continue;
                    var read = false; var allInside = true;
                    for (var reader = 0; reader < nodes.Length && allInside; reader++)
                        foreach (var input in stepInputs[reader])
                            if (input.FromStep is { } from && from.Step == at) { read = true; if (!inside[reader]) { allInside = false; break; } }
                    if (read && allInside) { inside[at] = true; grew = true; }
                }
            }
            regions[step] = inside;
        }
        return regions;
    }

    /// <summary>The steps an execution walk from <paramref name="start"/> reaches, optionally with one successor
    /// edge taken out of the graph. A step is marked once, so a plan whose successors are not strictly forward
    /// still terminates — in the rejection those successors earn, which is checked right after this walk.</summary>
    private static bool[] Reachable(int?[][] successors, int start, (int From, int To)? without)
    {
        var found = new bool[successors.Length];
        if (start < 0 || start >= successors.Length) return found;
        var pending = new Stack<int>(); found[start] = true; pending.Push(start);
        var skipped = false;
        while (pending.Count > 0)
        {
            var at = pending.Pop();
            foreach (var next in successors[at])
            {
                if (next is not int target) continue;
                // Only the present edge is removed; missing may have the same target.
                if (!skipped && without is { } edge && at == edge.From && target == edge.To)
                { skipped = true; continue; }
                if (!found[target]) { found[target] = true; pending.Push(target); }
            }
        }
        return found;
    }

    /// <summary>
    /// The plan's mount targets. The list is required and non-empty because a plan that hangs on nothing can only
    /// be a mistake; it is ordinal-sorted and duplicate-free so the same plan has one spelling; and every kind it
    /// names must have a matcher a provider registered — whether that matcher wants the event's subject or not —
    /// so a plan can never be accepted and then silently never dispatched.
    /// </summary>
    private static IReadOnlyList<PlanAttachment> ParseAttachments(JsonElement plan, RuntimeRegistry registry, RuntimeLimits limits)
    {
        var rows = RuntimeJson.Rows(plan, "attachments");
        RuntimeJson.Require(rows.Length > 0, RuntimeAbiCodes.AttachmentEmpty, "A plan declares at least one mount target.");
        RuntimeJson.Require(rows.Length <= limits.MaxAttachmentsPerPlan, RuntimeAbiCodes.AttachmentBudget,
            "A plan declares more mount targets than the per-plan budget allows.");
        var attachments = new List<PlanAttachment>(rows.Length);
        foreach (var row in rows)
        {
            RuntimeJson.Shape(row, "kind reference", "category port level");
            var kind = RuntimeJson.Text(row, "kind");
            RuntimeJson.Require(Wire.AttachmentKinds.Contains(kind), RuntimeAbiCodes.AttachmentKind, kind);
            var reference = RuntimeJson.Text(row, "reference");
            var category = row.TryGetProperty("category", out var value) ? RuntimeJson.Text(value) : null;
            // Only a map object is addressed by (category, address); the other kinds name a whole block or type.
            RuntimeJson.Require(kind == "map-object" ? category != null : category == null, RuntimeAbiCodes.AttachmentKind, kind);
            // Gear blocks may opt into the holder relation on this same equipment registration.
            var port = row.TryGetProperty("port", out var portValue) ? RuntimeJson.Text(portValue) : null;
            RuntimeJson.Require(kind == "player" ? port != null && Regex.IsMatch(port, @"^[a-z][a-z0-9_]*$")
                : kind == "gear-block" ? port == null || Regex.IsMatch(port, @"^[a-z][a-z0-9_]*$") : port == null,
                RuntimeAbiCodes.AttachmentPort, kind);
            RuntimeJson.Require(registry.AttachmentMatchers.ContainsKey(kind), RuntimeAbiCodes.AttachmentKind, kind);
            RuntimeJson.Require(kind != "gear-block" || port == null || registry.AttachmentMatchers[kind].Matcher.Holder != null,
                RuntimeAbiCodes.AttachmentPort, kind);
            var scope = row.TryGetProperty("level", out var scopeValue) ? RuntimeJson.Text(scopeValue) : null;
            RuntimeJson.Require(scope == null || kind == "map-object" && Regex.IsMatch(scope, @"^(?:0|[1-9]\d*):[A-E]:(?:0|[1-9]\d*)$"), RuntimeAbiCodes.AttachmentKind, "Invalid object mount level scope.");
            RuntimeJson.Require(scope == null || registry.AttachmentMatchers.TryGetValue("level", out var levelMatcher)
                && levelMatcher.Matcher.Scope != null && levelMatcher.Owner == registry.AttachmentMatchers[kind].Owner,
                RuntimeAbiCodes.AttachmentKind, "Object mount level scope has no matching provider scope matcher.");
            attachments.Add(new PlanAttachment(kind, category, reference, port, scope));
        }
        var sorted = attachments.OrderBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Category ?? "", StringComparer.Ordinal).ThenBy(a => a.Reference, StringComparer.Ordinal)
            .ThenBy(a => a.Port ?? "", StringComparer.Ordinal).ThenBy(a => a.Level ?? "", StringComparer.Ordinal).ToArray();
        for (var index = 0; index < attachments.Count; index++)
        {
            var detail = attachments[index].Kind + "/" + (attachments[index].Category ?? "") + "/" + attachments[index].Reference
                + (attachments[index].Port == null ? "" : "#" + attachments[index].Port);
            RuntimeJson.Require(attachments[index] == sorted[index], RuntimeAbiCodes.AttachmentOrder, detail);
            RuntimeJson.Require(index == 0 || attachments[index] != attachments[index - 1], RuntimeAbiCodes.AttachmentDuplicate, detail);
        }
        return attachments.AsReadOnly();
    }

    private static JsonElement? HolderSubject(JsonElement graph)
    {
        var subject = RuntimeGraphContracts.SubjectPort(graph);
        return subject.ValueKind == JsonValueKind.Object && RuntimeJson.Text(subject, "type") == "entity"
            && !RuntimeGraphContracts.Many(subject) && RuntimeGraphContracts.DeclaredEntityKinds(subject)
                .Contains(EntityKinds.Player, StringComparer.Ordinal) ? subject : null;
    }
}
