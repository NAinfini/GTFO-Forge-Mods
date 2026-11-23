using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;
using ForgeEngine.Testing;

/// <summary>
/// The typed result-row ABI (plan §3.2 I-PLAN 结果行): the registration rule that types a row's `target` by the
/// catalog's own recipient declaration, the descriptor a plan compiles from it, the bounded writer and validator the
/// action boundary writes through, and the typed read a downstream step makes.
///
/// The four halves are asserted separately because they fail separately: a registration that disagrees with its
/// recipient contract never reaches a plan, a descriptor that lays a row out wrongly is caught by the writer's own
/// bounds, a handler's row that is not the recipient at its position is caught at the boundary, and a downstream read
/// that went back through the receipt instead of the typed region is caught by the step that consumes it. Nothing
/// here claims game, multiplayer or performance behaviour.
/// </summary>
internal static class ResultRowAbiTests
{
    private const string Id = "example.rows";
    private const string Kind = "item";
    private const string Item = "example.item.one";

    private const string EntityPort = """{ "id": "target", "type": "entity" }""";
    private const string ResourcePort = """{ "id": "item", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" }""";
    private const string PlayerPort = """{ "id": "target", "type": "entity", "entityKinds": ["gtfo.player"] }""";
    private const string HandlePort = """{ "id": "handles", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }""";
    /// <summary>The nullable entity column the canonical `forge.action.map.item_spawn` row declares after the four
    /// fixed columns, written exactly as the contract writes it.</summary>
    private const string SpawnedPort = """{ "id": "spawned", "type": "entity", "nullable": true }""";

    internal static int Run()
    {
        var checks = 0;
        checks += Registration();
        checks += CommonInput();
        checks += ConditionalReader();
        checks += DecisionSources();
        checks += Writer();
        checks += Catalog();
        checks += Boundary();
        checks += Downstream();
        return checks;
    }

    /// <summary>
    /// The registration rule: a row's `target` column declares one of the three world-reference kinds with exactly
    /// the identity keys that kind is validated by, and a capability whose row declares a different identity than
    /// its own recipient port is refused. A column that declares no identity of its own is refused too: the
    /// metadata-absent tolerance is gone, so a bare `entity` column paired with a resource or a kinded entity
    /// recipient cannot restate that recipient as another kind — the one shape left where a bare column is the
    /// declaration is an entity recipient that itself declares no kinds.
    /// </summary>
    private static int Registration()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != RuntimeAbiCodes.ResultSchema)
                    throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {RuntimeAbiCodes.ResultSchema}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }
        void Accept(string name, string recipient, string target, string? later = null)
        {
            RuntimeGraphContracts.ValidateCapability("action", Graph(recipient, target, later), Id + "." + name);
            Check(true, "result row registers: " + name);
        }

        Accept("resource", ResourcePort, """{ "id": "target", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" }""");
        Accept("handle", HandlePort, """{ "id": "target", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }""");
        Accept("entity", PlayerPort, """{ "id": "target", "type": "entity", "entityKinds": ["gtfo.player"] }""");
        Accept("entity-plain", EntityPort, """{ "id": "target", "type": "entity" }""");

        // The removed generator shape: a bare `entity` column paired with a resource recipient declares no identity
        // of its own, so accepting it would let the recipient's declared kind be restated as an entity. The
        // rejection is asserted rather than the placeholder being accepted.
        Reject(() => RuntimeGraphContracts.ValidateCapability("action",
            Graph(ResourcePort, """{ "id": "target", "type": "entity" }"""), Id),
            "a bare entity target column paired with a resource recipient");

        return checks;
    }

    /// <summary>The common input is a reference to an action's concrete row region, not another row schema.
    /// These registration and frame checks deliberately make no assertion about row-selection or commit-value
    /// semantics, which belong to the reader rather than to the type interface.</summary>
    private static int CommonInput()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void Reject(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }
        var common = RuntimeJson.Parse("""{ "id": "result", "type": "result", "schema": "forge.result.common" }""");
        var indexPort = RuntimeJson.Parse("""{ "id": "index", "type": "integer" }""");
        JsonElement Query(JsonElement input, bool output = false) => RuntimeJson.From(new
        {
            domains = new[] { "logic" },
            execution = "query",
            reads = new[] { "kernel" },
            inputs = output ? Array.Empty<JsonElement>() : new[] { input, indexPort },
            outputs = output ? new[] { input } : new[] { RuntimeJson.Parse("""{ "id": "value", "type": "boolean" }""") },
            parameters = Array.Empty<object>()
        });
        RuntimeGraphContracts.ValidateCapability("state", Query(common), Id + ".common");
        Check(RuntimeGraphContracts.IsCommonResultPort(common), "the common result interface is explicitly marked");
        var (kind, width) = RuntimeGraphContracts.FramePort(common);
        Check(kind == ValueKind.Result && width == 1, "a common input occupies one opaque reference slot");
        Reject(() => RuntimeGraphContracts.ValidateCapability("state", Query(common, output: true), Id),
            RuntimeAbiCodes.ResultSchema, "the common interface cannot be declared as an output");
        // Register the real query mechanism and resolve the handler's physical offsets. The evaluator is only
        // an ABI probe; it neither chooses a row nor defines the production reader's value semantics.
        var shape = new HandlerShape().Inputs("result", "index").Outputs("value");
        var query = Query(common);
        var declaration = new ProviderDeclaration(Id + ".common", "1.0.0") { Kind = "extension" }
            .Declare(new
            {
                id = Id + ".common.read",
                owner = Id + ".common",
                kind = "state",
                label = "Common row input",
                version = "1.0.0",
                parameters = new { },
                graph = query
            })
            .Bind(new ProviderBinding(Id + ".common.binding.read", Id + ".common.read", Id + ".common.handler.read",
                "observe", Array.Empty<string>(), shape));
        var kernel = new RuntimeKernel(Fixture.Identity);
        kernel.BeginWorld(1);
        kernel.RegisterModule(declaration.Module(RuntimeApi.Fingerprint, evaluators:
            new Dictionary<string, EvaluatorHandler> { [Id + ".common.handler.read"] = (_, o) => o.Boolean(0, true) }),
            RuntimeLogLevel.Off);
        Check(shape.Input("result") == 0 && shape.Input("index") == 1,
            "query registration keeps the common input and the following value at distinct physical offsets");

        foreach (var (recipient, target) in new[]
        {
            (EntityPort, """{ "id": "target", "type": "entity" }"""),
            (PlayerPort, """{ "id": "target", "type": "entity", "entityKinds": ["gtfo.player"] }"""),
            (ResourcePort, """{ "id": "target", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" }"""),
            (HandlePort, """{ "id": "target", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }""")
        })
        {
            var graph = Graph(recipient, target);
            var output = graph.GetProperty("outputs")[1];
            RuntimeGraphContracts.ValidateCapability("action", graph, Id);
            Check(RuntimeGraphContracts.AssignableValue(output, common), "a validated concrete target kind feeds the common input: " + target);
            Check(!RuntimeGraphContracts.AssignableValue(common, output), "the common input cannot produce a concrete target kind: " + target);
            RuntimePlan.RequireCommonResultSource(true, "action", graph, output, Id);
            Check(true, "the loader source gate admits the action's declared result port");
            foreach (var sourceKind in new[] { "query" })
                Reject(() => RuntimePlan.RequireCommonResultSource(true, sourceKind, graph, output, Id),
                    "port-mismatch", "a " + sourceKind + " cannot produce a common action result");

            var renamed = JsonNode.Parse(output.GetRawText())!;
            renamed["id"] = "other_result";

        }
        Check(!RuntimeGraphContracts.AssignableValue(common, common), "common-to-common is not a result-producing edge");
        Check(!RuntimeGraphContracts.AssignableValue(indexPort, common), "an ordinary value cannot feed the common interface");
        Check(!RuntimeGraphContracts.AssignableValue(RuntimeJson.Parse(
            """{ "id": "result", "type": "result", "schema": "example.result.missing" }"""), common),
            "a named result schema without validated columns cannot feed the common interface");
        var malformed = JsonNode.Parse(Graph(EntityPort, """{ "id": "target", "type": "entity" }""")
            .GetProperty("outputs")[1].GetRawText())!;
        malformed["fields"]![1]!["schema"] = "commit_state";
        Check(!RuntimeGraphContracts.AssignableValue(RuntimeJson.Parse(malformed.ToJsonString()), common),
            "the common interface still refuses a noncanonical status field");

        // The actual frame builder keeps the producer's full row descriptor and reserves only a reference for
        // the consumer. This tests the in-memory ABI directly, not a hand-authored serialized execution plan.
        var producer = Graph(EntityPort, """{ "id": "target", "type": "entity" }""", SpawnedPort);
        var rowPort = producer.GetProperty("outputs")[1];
        var targetPort = RuntimeJson.Parse(EntityPort);
        var input = new StepInput("result", null, null, new StepSlotRef(0, 1), false, rowPort);
        var sourceStep = new StepContract("producer", "action", producer, producer, Array.Empty<JsonElement>(),
            new HashSet<string>(), new[] { new StepInput("target", "target", null, null, false, targetPort) },
            Array.Empty<int?>(), 0, 0);
        var queryStep = new StepContract("consumer", "query", query, query, Array.Empty<JsonElement>(),
            new HashSet<string>(), new[] { input, new StepInput("index", null, RuntimeJson.From(0), null, false, indexPort) },
            Array.Empty<int?>(), 1, 1);
        var frames = PlanFrames.Build(new[] { new EntryContract("entry", RuntimeJson.From(new { outputs = new[] { targetPort } }),
            new[] { sourceStep, queryStep }) }, new RuntimeLimits());
        Check(frames.Steps[0].Row is { Columns.Length: 5, TargetKind: ValueKind.Entity }
            && frames.Steps[1].Row == null && frames.Steps[1].Inputs[0].Kind == ValueKind.Result
            && frames.Steps[1].Inputs[0].Width == 1 && frames.Steps[1].Inputs[0].SourceStep == 0
            && frames.Steps[1].Inputs[0].SourceSlot == 1,
            "a common consumer keeps a source reference while the producer retains its complete five-column descriptor");
        Check(input.ResultPort && input.Port.GetProperty("schema").GetString() == "example.result.rows"
            && input.Port.GetProperty("fields").GetArrayLength() == 5,
            "the loaded input retains the concrete producer contract instead of a synthetic common row");
        return checks;
    }

    /// <summary>The production reader's two resolved shapes and evaluator. Count mode never selects a row;
    /// row mode preserves a selected row's actual outcome, including an unknown commit.</summary>
    private static int ConditionalReader()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void Reject(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }
        var graph = PrimitiveContracts.Graph(ForgeEngine.Trigger.Targeting.ObservedResultDeclarations.CapabilityId);
        RuntimeGraphContracts.ValidateCapability("state", graph, Id + ".reader");
        var count = RuntimeGraphContracts.Resolve(graph, RuntimeJson.From(new { mode = 0 }));
        var row = RuntimeGraphContracts.Resolve(graph, RuntimeJson.From(new { mode = 1 }));
        Check(RuntimeJson.Rows(count, "inputs").Select(port => RuntimeJson.Text(port, "id")).SequenceEqual(new[] { "result" })
            && RuntimeJson.Rows(count, "outputs").Select(port => RuntimeJson.Text(port, "id")).SequenceEqual(new[] { "count" }),
            "count mode exposes only the source result and its row count");
        Check(RuntimeJson.Rows(row, "inputs").Select(port => RuntimeJson.Text(port, "id")).SequenceEqual(new[] { "result", "row_index" })
            && RuntimeJson.Rows(row, "outputs").Select(port => RuntimeJson.Text(port, "id")).SequenceEqual(new[] { "count", "status", "committed", "code" }),
            "row mode requires an explicit index and exposes the selected row's values");
        Check(!RuntimeJson.Flag(RuntimeJson.Rows(row, "inputs")[1], "optional")
            && RuntimeJson.Rows(row, "outputs").Where(port => RuntimeJson.Flag(port, "nullable"))
                .Select(port => RuntimeJson.Text(port, "id")).SequenceEqual(new[] { "committed" }),
            "only committed is nullable, and row selection is required");
        Check(new[] { count, row }.All(contract => RuntimeJson.Rows(contract, "inputs").Concat(RuntimeJson.Rows(contract, "outputs"))
            .All(port => !port.TryGetProperty("when", out _))), "resolved ports carry no authoring condition metadata");
        Reject(() => RuntimeGraphContracts.Resolve(graph, RuntimeJson.EmptyObject), "missing-constant", "mode cannot be omitted");
        Reject(() => RuntimeGraphContracts.Resolve(graph, RuntimeJson.From(new { mode = 2 })), "invalid-enum", "an unknown mode cannot select a shape");

        var changed = JsonNode.Parse(graph.GetRawText())!;
        changed["inputs"]![1]!["when"]!["parameter"] = "absent";
        Reject(() => RuntimeGraphContracts.ValidateCapability("state", RuntimeJson.Parse(changed.ToJsonString()), Id),
            "port-condition", "a port condition naming an absent parameter is refused");
        var plain = Graph(EntityPort, """{ "id": "target", "type": "entity" }""");
        Check(RuntimeGraphContracts.Resolve(plain, RuntimeJson.EmptyObject).GetRawText() == RuntimeJson.From(plain).GetRawText(),
            "an ordinary contract with no conditional ports retains its resolved bytes");

        var evaluator = ForgeEngine.Trigger.Targeting.ObservedResultDeclarations.Family.Nodes.Single().Evaluate;
        JsonElement Read(string mode, JsonElement rows, long? index = null)
        {
            var inputs = new Dictionary<string, JsonElement> { ["result"] = rows };
            if (index != null) inputs["row_index"] = RuntimeJson.From(index.Value);
            return ForgeEngine.Testing.TestContexts.Read(evaluator, new EvaluationContext("reader", RuntimeJson.From(new { mode }), RuntimeJson.From(inputs),
                default, null!, null!, 0, _ => null), "count", "status", "committed", "code");
        }
        var values = RuntimeJson.From(new[]
        {
            Row("gtfo.player:1", CommandStatuses.Succeeded, CommitStates.Confirmed, "committed"),
            Row("gtfo.player:2", CommandStatuses.Rejected, CommitStates.None, RuntimeAbiCodes.InsufficientCost),
            Row("gtfo.player:3", CommandStatuses.Failed, CommitStates.Unknown, "native-unconfirmed")
        });
        var counted = Read("count", values);
        Check(counted.GetProperty("count").GetInt32() == 3 && counted.EnumerateObject().Count() == 1,
            "counting several results emits no implicit first row");
        Check(Read("count", RuntimeJson.From(Array.Empty<object>())).GetProperty("count").GetInt32() == 0,
            "a legitimate empty result set has zero rows without selecting row zero");
        var first = Read("row", values, 0);
        var second = Read("row", values, 1);
        var third = Read("row", values, 2);
        var statuses = RuntimeEnumSets.Members("execution_outcome").ToArray();
        Check(first.GetProperty("status").GetInt32() == Array.IndexOf(statuses, CommandStatuses.Succeeded)
            && first.GetProperty("committed").ValueKind == JsonValueKind.True, "confirmed maps to true with its actual status");
        Check(second.GetProperty("status").GetInt32() == Array.IndexOf(statuses, CommandStatuses.Rejected)
            && second.GetProperty("committed").ValueKind == JsonValueKind.False
            && second.GetProperty("code").GetString() == RuntimeAbiCodes.InsufficientCost,
            "explicit index one selects the rejection rather than the first row");
        Check(third.GetProperty("status").GetInt32() == Array.IndexOf(statuses, CommandStatuses.Failed)
            && third.GetProperty("committed").ValueKind == JsonValueKind.Null
            && third.GetProperty("code").GetString() == "native-unconfirmed",
            "an unknown commit stays null and retains its failure code");
        Check(Read("row", values[0], 0).GetProperty("count").GetInt32() == 1,
            "a single-object row still requires explicit index zero");
        Reject(() => Read("row", values), "missing-input", "a row read never chooses an omitted index");
        Reject(() => Read("row", values, -1), RuntimeAbiCodes.ResultIndexOutOfRange, "a negative index is refused");

        Reject(() => ForgeEngine.Testing.TestContexts.Read(evaluator, new EvaluationContext("reader", RuntimeJson.From(new { mode = "count" }), RuntimeJson.EmptyObject,
            default, null!, null!, 0, _ => null), "count"), "from-step-value", "an unavailable producer is not an empty result set");
        return checks;
    }

    /// <summary>The loader's single recovery classifier over resolved metadata. This is an in-memory unit check;
    /// the retained Weapon suite separately loads genuine compiler plans and executes the resulting decisions.</summary>
    private static int DecisionSources()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        var producer = Graph(EntityPort, """{ "id": "target", "type": "entity" }""");
        var readerGraph = PrimitiveContracts.Graph(ForgeEngine.Trigger.Targeting.ObservedResultDeclarations.CapabilityId);
        var reader = RuntimeGraphContracts.Resolve(readerGraph, RuntimeJson.From(new { mode = 0 }));
        var compareGraph = PrimitiveContracts.Graph(Blocks.ConditionPredicateCompare.Id);
        var compare = RuntimeGraphContracts.Resolve(compareGraph, RuntimeJson.From(new { value_type = 1 }));
        var branch = PrimitiveContracts.Graph(Blocks.ControlFlowBranch.Id);
        JsonElement Port(JsonElement contract, string side, string id) => RuntimeJson.Rows(contract, side)
            .Single(port => RuntimeJson.Text(port, "id") == id);
        StepInput Literal(string name, object value) => new(name, null, RuntimeJson.From(value), null, false, Port(compare, "inputs", name));
        var rows = new StepInput("result", null, null, new StepSlotRef(0, 1), false, Port(producer, "outputs", "result"));
        var count = new StepInput("left", null, null, new StepSlotRef(1, 0), false, Port(reader, "outputs", "count"));
        var original = new[]
        {
            new StepContract("producer", "action", producer, producer, Array.Empty<JsonElement>(), new HashSet<string>(),
                new[] { new StepInput("target", "target", null, null, false, RuntimeJson.Parse(EntityPort)) }, new int?[] { 3 }, 0, 0),
            new StepContract("reader", "query", readerGraph, reader, new[] { RuntimeJson.From(0) }, new HashSet<string>(),
                new[] { rows }, Array.Empty<int?>(), 1, 1),
            new StepContract("compare", "query", compareGraph, compare, new[] { RuntimeJson.From(1), RuntimeJson.From((object?)null) },
                new HashSet<string>(), new[] { count, Literal("right", 0), Literal("operator", 0) }, Array.Empty<int?>(), 2, 2),
            new StepContract("branch", "control", branch, branch, Array.Empty<JsonElement>(), new HashSet<string>(),
                new[] { new StepInput("condition", null, null, new StepSlotRef(2, 0), false, Port(compare, "outputs", "value")) },
                new int?[] { null, null }, 3, 3)
        };
        var frames = PlanFrames.Build(new[] { new EntryContract("entry",
            RuntimeJson.From(new { outputs = new[] { RuntimeJson.Parse(EntityPort) } }), original) }, new RuntimeLimits());
        var ids = new[] { "example.action", Blocks.QueryResultRead.Id, Blocks.ConditionPredicateCompare.Id, Blocks.ControlFlowBranch.Id };
        var parameters = new[] { RuntimeJson.EmptyObject, RuntimeJson.From(new { mode = "count" }),
            RuntimeJson.From(new { value_type = "integer" }), RuntimeJson.EmptyObject };
        var steps = original.Select((step, index) => new ResolvedStep(step.NodeId, step.NodeKind, "example.binding." + index,
            index, index, RuntimeJson.EmptyObject, step.Inputs, step.Promoted, step.Successors, step.Contract, frames.Steps[index],
            RuntimeJson.Rows(step.Graph, "parameters"), "example", ids[index], step.NodeKind is "action" or "control" ? "host" : "query", "[]",
            Control: index == 3 ? Blocks.ControlFlowBranch.Id : null)
        { HandlerParameters = parameters[index] }).ToArray();
        Check(RuntimePlan.HasResultDecision(steps, 0), "a direct count==0 comparison explicitly handles this producer");
        Check(RuntimePlan.ResultDerivedSteps(steps, 0).SequenceEqual(new[] { 1, 2, 3 }),
            "result invalidation includes the reader, numeric comparison and dependent decision frame");
        var independent = steps.Concat(new[] { steps[2] with { NodeId = "independent-query",
            Inputs = new[] { Literal("left", 1), Literal("right", 0), Literal("operator", 0) } } }).ToArray();
        Check(RuntimePlan.ResultDerivedSteps(independent, 0).SequenceEqual(new[] { 1, 2, 3 }),
            "an unrelated query remains outside result reentry invalidation");
        var snapshots = steps.Concat(new[]
        {
            steps[0] with { NodeId = "another-action", Inputs = new[] { rows } },
            steps[1] with { NodeId = "another-action-reader", Inputs = new[] { rows with { FromStep = new StepSlotRef(4, 1) } } }
        }).ToArray();
        Check(RuntimePlan.ResultDerivedSteps(snapshots, 0).SequenceEqual(new[] { 1, 2, 3 }),
            "invalidation stops at another action's independently executed result snapshot");
        ResolvedStep[] ChangeComparison(params StepInput[] inputs)
        {
            var changed = steps.ToArray(); changed[2] = changed[2] with { Inputs = inputs }; return changed;
        }
        Check(RuntimePlan.HasResultDecision(ChangeComparison(count, Literal("right", 1), Literal("operator", 5)), 0),
            "count>=1 is an allowed literal comparison, not an invented zero-threshold restriction");
        Check(RuntimePlan.HasResultDecision(ChangeComparison(Literal("left", 1), count with { Name = "right" }, Literal("operator", 3)), 0),
            "a literal on the left and count on the right has the same bounded dependency");
        Check(RuntimePlan.HasResultDecision(ChangeComparison(count, Literal("right", 0), Literal("operator", 0), Literal("tolerance", 0)), 0),
            "an explicit zero tolerance adds no dynamic dependency");
        Check(!RuntimePlan.HasResultDecision(ChangeComparison(count, count with { Name = "right" }, Literal("operator", 0)), 0),
            "a dynamic second operand cannot grant recovery");
        Check(!RuntimePlan.HasResultDecision(ChangeComparison(count, Literal("right", 0),
            count with { Name = "operator" }), 0), "a dynamic operator cannot grant recovery");
        Check(!RuntimePlan.HasResultDecision(ChangeComparison(count, Literal("right", 0), Literal("operator", 0), Literal("tolerance", 1)), 0),
            "a nonzero tolerance cannot grant recovery");
        Check(!RuntimePlan.HasResultDecision(ChangeComparison(count, Literal("right", 0), Literal("operator", 0),
            count with { Name = "tolerance" }), 0), "a dynamic tolerance cannot grant recovery");
        var another = steps.Concat(new[] { steps[0] with { NodeId = "another-producer" } }).ToArray();
        another[1] = another[1] with { Inputs = new[] { rows with { FromStep = new StepSlotRef(4, 1) } } };
        Check(!RuntimePlan.HasResultDecision(another, 0), "another action's result cannot handle this producer's failure");
        var indirect = steps.ToArray(); indirect[1] = indirect[1] with { CapabilityId = "forge.modifier.value.calculate" };
        Check(!RuntimePlan.HasResultDecision(indirect, 0), "an arithmetic or arbitrary query chain cannot grant recovery");
        var unrelated = steps.ToArray(); unrelated[0] = unrelated[0] with { Successors = new int?[] { 2 } };
        Check(!RuntimePlan.HasResultDecision(unrelated, 0), "an immediate non-decision successor cannot grant recovery");

        // A nullable commit first branches on presence. The known side must immediately test that same
        // present node's guarded bool; merely connecting an arbitrary action to the known exit is not enough.
        var rowReader = RuntimeGraphContracts.Resolve(readerGraph, RuntimeJson.From(new { mode = 1 }));
        var presentGraph = PrimitiveContracts.Graph(Blocks.ControlFlowPresent.Id);
        var present = RuntimeGraphContracts.Resolve(presentGraph, RuntimeJson.From(new { value_type = 2 }));
        var rowOriginal = new[]
        {
            new StepContract("producer", "action", producer, producer, Array.Empty<JsonElement>(), new HashSet<string>(),
                original[0].Inputs, new int?[] { 2 }, 0, 0),
            new StepContract("row-reader", "query", readerGraph, rowReader, new[] { RuntimeJson.From(1) }, new HashSet<string>(),
                new[] { rows, new StepInput("row_index", null, RuntimeJson.From(0), null, false, Port(rowReader, "inputs", "row_index")) },
                Array.Empty<int?>(), 1, 1),
            new StepContract("present", "control", presentGraph, present, new[] { RuntimeJson.From(2), RuntimeJson.From(0) }, new HashSet<string>(),
                new[] { new StepInput("value", null, null, new StepSlotRef(1, 2), false, Port(rowReader, "outputs", "committed")) },
                new int?[] { 3, null }, 2, 2),
            new StepContract("branch", "control", branch, branch, Array.Empty<JsonElement>(), new HashSet<string>(),
                new[] { new StepInput("condition", null, null, new StepSlotRef(2, 2), false, Port(present, "outputs", "value")) },
                new int?[] { null, null }, 3, 3)
        };
        var rowFrames = PlanFrames.Build(new[] { new EntryContract("entry",
            RuntimeJson.From(new { outputs = new[] { RuntimeJson.Parse(EntityPort) } }), rowOriginal) }, new RuntimeLimits());
        var rowIds = new[] { "example.action", Blocks.QueryResultRead.Id, Blocks.ControlFlowPresent.Id, Blocks.ControlFlowBranch.Id };
        var rowSteps = rowOriginal.Select((step, index) => new ResolvedStep(step.NodeId, step.NodeKind, "example.row.binding." + index,
            index, index, RuntimeJson.EmptyObject, step.Inputs, step.Promoted, step.Successors, step.Contract, rowFrames.Steps[index],
            RuntimeJson.Rows(step.Graph, "parameters"), "example", rowIds[index], step.NodeKind is "action" or "control" ? "host" : "query", "[]",
            Control: index == 2 ? Blocks.ControlFlowPresent.Id : index == 3 ? Blocks.ControlFlowBranch.Id : null,
            ValuePort: index == 2 ? "value" : null)
        { HandlerParameters = index == 1 ? RuntimeJson.From(new { mode = "row" }) : RuntimeJson.EmptyObject }).ToArray();
        var evaluate = ForgeEngine.Trigger.Targeting.ObservedResultDeclarations.Family.Nodes.Single().Evaluate;
        Check(RuntimePlan.ResultDerivedSteps(rowSteps, 0).SequenceEqual(new[] { 1, 2, 3 }),
            "a row result invalidates its reader, present forwarding frame and guarded branch");
        foreach (var confirmed in new[] { false, true })
        {
            var answer = ForgeEngine.Testing.TestContexts.Read(evaluate, new EvaluationContext("reader", RuntimeJson.From(new { mode = "row" }), RuntimeJson.From(new
            {
                result = Row("gtfo.player:1", confirmed ? CommandStatuses.Succeeded : CommandStatuses.Rejected,
                    confirmed ? CommitStates.Confirmed : CommitStates.None, confirmed ? "committed" : RuntimeAbiCodes.InsufficientCost),
                row_index = 0
            }), default, null!, null!, 0, _ => null), "count", "status", "committed", "code");
            Check(RuntimePlan.HasResultDecision(rowSteps, 0) && answer.GetProperty("committed").GetBoolean() == confirmed,
                "known " + confirmed + " is admitted only through present's own guarded boolean branch");
        }
        var unknownOnly = rowSteps.Concat(new[] { rowSteps[0] with { NodeId = "unknown-handler" } }).ToArray();
        unknownOnly[2] = unknownOnly[2] with { Successors = new int?[] { null, 4 } };
        Check(RuntimePlan.HasResultDecision(unknownOnly, 0), "an unconnected known path permits direct handling on present's missing exit");
        var directAction = rowSteps.Concat(new[] { rowSteps[0] with { NodeId = "ordinary-action" } }).ToArray();
        directAction[2] = directAction[2] with { Successors = new int?[] { 4, null } };
        Check(!RuntimePlan.HasResultDecision(directAction, 0), "present's known exit cannot directly authorize an ordinary action");
        var wrongValue = rowSteps.ToArray(); wrongValue[3] = wrongValue[3] with
        {
            Inputs = new[]
        {
            new StepInput("condition", null, RuntimeJson.From(true), null, false, Port(branch, "inputs", "condition"))
        }
        };
        Check(!RuntimePlan.HasResultDecision(wrongValue, 0), "the known branch cannot test an unrelated boolean value");
        var unguarded = rowSteps.ToArray(); unguarded[3] = unguarded[3] with
        {
            Inputs = new[]
        {
            new StepInput("condition", null, null, new StepSlotRef(1, 2), false, Port(rowReader, "outputs", "committed"))
        }
        };
        Check(!RuntimePlan.HasResultDecision(unguarded, 0), "the known branch must read the guard's value, not the reader's nullable original");
        var otherPresent = rowSteps.Concat(new[] { rowSteps[2] with { NodeId = "another-present" } }).ToArray();
        otherPresent[3] = otherPresent[3] with
        {
            Inputs = new[]
        {
            new StepInput("condition", null, null, new StepSlotRef(4, 2), false, Port(present, "outputs", "value"))
        }
        };
        Check(!RuntimePlan.HasResultDecision(otherPresent, 0), "the known branch cannot read another present node's forwarded value");
        return checks;
    }

    /// <summary>
    /// The compiled descriptor and its writer: one row per recipient at the descriptor's own offsets, the target's
    /// complete identity of the declared kind, and the ABI's own code for every value the descriptor does not admit.
    /// </summary>
    private static int Writer()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void Reject(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }

        // entity: the reference's own id and its two epochs, written at the row's own offsets. An entity column is
        // three slots because a row keeps the identity the handler named, not a component triple that would have to
        // be re-spelled on the way out.
        {
            var row = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", """{ "id": "amount", "type": "number", "unit": "hp" }""");
            var frame = Write(row, 0,
                """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed", "amount": 4.5 }""",
                Entity("gtfo.player:7", 3, 9));
            Check(frame.String(0) == "gtfo.player:7" && frame.Integer(1) == 3 && frame.Integer(2) == 9,
                "an entity row's target is the recipient's own id and both epochs");
            Check(frame.EnumIndex(3) == 0 && frame.EnumIndex(4) == 1 && frame.String(5) == "committed" && frame.Number(6) == 4.5,
                "an entity row's later columns are written at the descriptor's own offsets");
        }

        // resource: the kind's index in the shared table plus the id, which is the same two-slot value every other
        // resource in a frame occupies.
        {
            var row = Descriptor(ResourcePort, """{ "id": "target", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" }""");
            Check(row.Stride == 5 && row.Span == 20, "a resource target widens its row by the value's own two slots");
            var frame = Write(row, 0,
                """{ "target": {"resourceKind":"item","resourceId":"example.item.one"}, "status": "succeeded", "committed": "confirmed", "code": "committed" }""",
                Resource(Kind, Item));
            var written = frame.Resource(0);
            Check(written.KindIndex == Array.IndexOf(Wire.ResourceKinds, Kind) && written.Id == Item,
                "a resource row's target is the recipient's own kind index and id");
        }

        // handle: the identity triple plus the creating provider's registration index.
        {
            var row = Descriptor(HandlePort, """{ "id": "target", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }""");
            var frame = Write(row, 0,
                """{ "target": {"worldEpoch":3,"lifeEpoch":11,"local":2,"provider":4}, "status": "succeeded", "committed": "confirmed", "code": "committed" }""",
                Handle(3, 11, 2, 4));
            var handle = frame.Handle(0, out var provider);
            Check(handle.WorldEpoch == 3 && handle.LifeEpoch == 11 && handle.Local == 2 && provider == 4,
                "a handle row's target is the recipient's identity and its creating provider");
        }

        // A row whose target is not the recipient at its position is refused whatever kind it is: the row's target
        // is that recipient, so naming somebody else is a retarget rather than a result.
        {
            var entity = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""");
            Reject(() => Write(entity, 0, """{ "target": {"id":"gtfo.player:8","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed" }""", Entity("gtfo.player:7", 3, 9)),
                RuntimeAbiCodes.ResultRowIncomplete, "a row that names another entity than its recipient");

            var resource = Descriptor(ResourcePort, """{ "id": "target", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" }""");

            var handle = Descriptor(HandlePort, """{ "id": "target", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }""");

        }

        // The row's own columns: a missing one, an unknown one, a wrong kind, an enum member outside its set and a
        // code the port does not declare are all the row's failure and never a coerced value.
        {
            var row = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", """{ "id": "outcome", "type": "enum", "schema": "commit_state" }""");

            // The target column is the row's own identity, so a row that leaves it out is incomplete rather than a
            // row whose target the kernel would fill in from the recipient: the thirteen non-entity producers still
            // write their rows without it and are refused by exactly this rule until they migrate.

            Reject(() => Write(row, 0, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "committed", "committed": "confirmed", "code": "committed", "outcome": "none" }""", Entity("gtfo.player:7", 3, 9)),
                RuntimeAbiCodes.ResultRowStatus, "a commit word used as a row status");

            // A declared code list admits its own codes, the shared words the runtime writes, and a registered ABI
            // code — and nothing else.
            var declared = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", codes: """["example-refused"]""");
            Write(declared, 0, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "rejected", "committed": "none", "code": "example-refused" }""", Entity("gtfo.player:7", 3, 9));
            Write(declared, 0, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed" }""", Entity("gtfo.player:7", 3, 9));

            Check(true, "a declared code list admits its own codes and the shared words");

            // An empty list means the port cannot fail, not that its rows go unchecked: it still carries the shared words,
            // and a domain code on it, or a code the declared list does not name, is the row's failure.
            var undeclared = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", codes: "[]");
            Write(undeclared, 0, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed" }""", Entity("gtfo.player:7", 3, 9));
            Write(undeclared, 0, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "rejected", "committed": "none", "code": "stale-entity" }""", Entity("gtfo.player:7", 3, 9));
            Reject(() => Write(undeclared, 0, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "rejected", "committed": "none", "code": "example-refused" }""", Entity("gtfo.player:7", 3, 9)),
                RuntimeAbiCodes.ResultRowStatus, "a domain code on a port that declares none");
            Reject(() => Write(declared, 0, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "rejected", "committed": "none", "code": "example-other" }""", Entity("gtfo.player:7", 3, 9)),
                RuntimeAbiCodes.ResultRowStatus, "a code the port does not declare");
        }

        // The writer's bounds: a row index outside the declared recipient count and a string the runtime's own
        // budget refuses are both the row budget, raised before anything is overwritten.
        {
            var row = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", """{ "id": "note", "type": "string" }""", capacity: 1);
            Reject(() => Write(row, 1, """{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed", "note": "x" }""", Entity("gtfo.player:7", 3, 9)),
                RuntimeAbiCodes.ResultRowBudget, "a row index beyond the port's declared recipient count");
            var note = new string('x', Wire.PlanJson.MaxStringValueLength + 1);

        }

        // The render is the descriptor's own: the typed row round-trips to the JSON a receipt reads, and a column
        // nothing wrote — a synthesized refusal's domain columns — is omitted rather than reported as null.
        {
            var row = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", """{ "id": "amount", "type": "number", "unit": "hp", "nullable": true }""");
            var arena = new FrameSpace(256, 65536);
            var writer = new FrameWriter(arena.Slots, 0, row.Span, arena.Strings);
            ResultRowAbi.Write(ref writer, row, 0,
                RuntimeJson.Parse("""{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed", "amount": null }"""),
                Entity("gtfo.player:7", 3, 9));
            var rendered = Rendered(new Frame(arena.Slots, 0, arena.Strings.Memory), row, 0, settled: false);
            Check(rendered.GetProperty("target").GetProperty("id").GetString() == "gtfo.player:7"
                && rendered.GetProperty("status").GetString() == "succeeded"
                && rendered.GetProperty("committed").GetString() == "confirmed"
                && rendered.GetProperty("code").GetString() == "committed"
                && rendered.GetProperty("amount").ValueKind == JsonValueKind.Null,
                "a typed row renders back as the row a receipt reads");

            var refusal = new FrameWriter(arena.Slots, 32, row.Span, arena.Strings);
            ResultRowAbi.WriteRefusal(ref refusal, row, 0, Entity("gtfo.player:7", 3, 9), CommandStatuses.Rejected, CommitStates.None, "example-refused");
            var refused = Rendered(new Frame(arena.Slots, 32, arena.Strings.Memory), row, 0, settled: false);
            Check(refused.GetProperty("target").GetProperty("id").GetString() == "gtfo.player:7"
                && refused.GetProperty("status").GetString() == "rejected"
                && refused.GetProperty("committed").GetString() == "none"
                && refused.GetProperty("code").GetString() == "example-refused"
                && !refused.TryGetProperty("amount", out _),
                "a synthesized refusal row carries the recipient, the refusal's own code, and no invented value");

            // The synthesized words are the command's own conclusion when the row vocabulary admits that pair, so
            // the command-level outcome stays derivable from the complete row set; a conclusion the vocabulary does
            // not admit is written as the conservative `rejected + none` rather than as an illegal pairing.
            var cancelled = new FrameWriter(arena.Slots, 64, row.Span, arena.Strings);
            ResultRowAbi.WriteRefusal(ref cancelled, row, 0, Entity("gtfo.player:7", 3, 9), CommandStatuses.Cancelled, CommitStates.None, "example-cancelled");
            var carried = Rendered(new Frame(arena.Slots, 64, arena.Strings.Memory), row, 0, settled: false);
            Check(carried.GetProperty("status").GetString() == CommandStatuses.Cancelled
                && carried.GetProperty("committed").GetString() == CommitStates.None
                && carried.GetProperty("code").GetString() == "example-cancelled",
                "a synthesized row carries the command's own conclusion when that pairing is canonical");

            var illegal = new FrameWriter(arena.Slots, 96, row.Span, arena.Strings);
            ResultRowAbi.WriteRefusal(ref illegal, row, 0, Entity("gtfo.player:7", 3, 9), CommandStatuses.Succeeded, CommitStates.None, "example-illegal");
            var conservative = Rendered(new Frame(arena.Slots, 96, arena.Strings.Memory), row, 0, settled: false);
            Check(conservative.GetProperty("status").GetString() == CommandStatuses.Rejected
                && conservative.GetProperty("committed").GetString() == CommitStates.None
                && conservative.GetProperty("code").GetString() == "example-illegal",
                "a synthesized row falls back to rejected + none for a conclusion the row vocabulary does not admit");

            // A synthesized row's target is the recipient at its position whatever kind it declares: a resource row
            // reports the recipient's own kind index and id, and a handle row its identity and creating provider —
            // neither is coerced to an entity shape or to null. The render takes no boundary value at all, so the
            // identity a reader sees can only be the one the slots hold.
            var resourceRow = Descriptor(ResourcePort, """{ "id": "target", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" }""");
            var resourceRefusal = new FrameWriter(arena.Slots, 128, resourceRow.Span, arena.Strings);
            ResultRowAbi.WriteRefusal(ref resourceRefusal, resourceRow, 0, Resource(Kind, Item), CommandStatuses.Rejected, CommitStates.None, "example-refused");
            var refusedResource = Rendered(new Frame(arena.Slots, 128, arena.Strings.Memory), resourceRow, 0, settled: false);
            Check(refusedResource.GetProperty("target").GetProperty("resourceId").GetString() == Item
                && refusedResource.GetProperty("target").GetProperty("resourceKind").GetString() == Kind
                && refusedResource.GetProperty("status").GetString() == CommandStatuses.Rejected
                && new Frame(arena.Slots, 128, arena.Strings.Memory).Resource(0).Id == Item,
                "a synthesized resource row carries the recipient's own kind and id from the typed slots");

            var handleRow = Descriptor(HandlePort, """{ "id": "target", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }""");
            var handleRefusal = new FrameWriter(arena.Slots, 160, handleRow.Span, arena.Strings);
            ResultRowAbi.WriteRefusal(ref handleRefusal, handleRow, 0, Handle(3, 11, 2, 4), CommandStatuses.Cancelled, CommitStates.None, "example-cancelled");
            var refusedHandle = Rendered(new Frame(arena.Slots, 160, arena.Strings.Memory), handleRow, 0, settled: false);
            Check(refusedHandle.GetProperty("target").GetProperty("provider").GetInt32() == 4
                && refusedHandle.GetProperty("target").GetProperty("local").GetInt32() == 2
                && refusedHandle.GetProperty("status").GetString() == CommandStatuses.Cancelled
                && new Frame(arena.Slots, 160, arena.Strings.Memory).Handle(0, out var storedProvider).WorldEpoch == 3
                && storedProvider == 4,
                "a synthesized handle row carries the recipient's own identity and provider from the typed slots");
        }

        // A later entity column is the same value form as an entity target, and it is the shape the canonical
        // `forge.action.map.item_spawn` row declares: `{ "id": "spawned", "type": "entity", "nullable": true }` after
        // the four fixed columns. The row keeps the reference the handler named — its id text and both epochs — so a
        // reader sees exactly that reference and never a spelling composed from the frame's own components.
        {
            var row = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", SpawnedPort);
            // The one recipient these rows are about: a row's target is the recipient at its position, and the
            // spawned reference is the row's own later column.
            var recipient = new EntityReference("gtfo.player:7", 3, 9);
            Check(row.Stride == 9 && row.Columns[4].Offset == 6 && row.Columns[4].Width == RuntimeGraphContracts.ResultEntityWidth,
                "an entity column is three slots — the id and both epochs — at its own offset");
            var arena = new FrameSpace(256, 65536);
            var writer = new FrameWriter(arena.Slots, 0, row.Span, arena.Strings);
            ResultRowAbi.Write(ref writer, row, 0, SpawnedValue(recipient, "gtfo.item:12", 5, 6), Entity("gtfo.player:7", 3, 9));
            var frame = new Frame(arena.Slots, 0, arena.Strings.Memory);
            Check(frame.String(6) == "gtfo.item:12" && frame.Integer(7) == 5 && frame.Integer(8) == 6,
                "a later entity column stores its own id and both epochs");
            var spawned = Rendered(frame, row, 0, settled: false).GetProperty("spawned");
            Check(spawned.GetProperty("id").GetString() == "gtfo.item:12"
                && spawned.GetProperty("worldEpoch").GetInt64() == 5
                && spawned.GetProperty("lifeEpoch").GetInt64() == 6
                && spawned.GetProperty("id").GetString() != "e:5:6:12",
                "a later entity column round-trips the exact reference, never a synthesized e:<world>:<life>:<local>");

            // Null stays the null the row declared, in the same slots.
            var nullWriter = new FrameWriter(arena.Slots, 32, row.Span, arena.Strings);
            ResultRowAbi.Write(ref nullWriter, row, 0, SpawnedValue(recipient, null, 0, 0), Entity("gtfo.player:7", 3, 9));
            var nullFrame = new Frame(arena.Slots, 32, arena.Strings.Memory);
            Check(nullFrame.Kind(6) == ValueKind.Null
                && Rendered(nullFrame, row, 0, settled: false).GetProperty("spawned").ValueKind == JsonValueKind.Null,
                "a null entity column stays the null the row declared");

            // The contract's own bound is the only bound: the largest safe integer an epoch may carry round-trips
            // exactly, and a value past it is refused rather than truncated.
            var boundary = RuntimeJson.MaxSafeInteger;
            var boundaryWriter = new FrameWriter(arena.Slots, 64, row.Span, arena.Strings);
            ResultRowAbi.Write(ref boundaryWriter, row, 0, SpawnedValue(recipient, "gtfo.item:13", boundary, boundary - 1), Entity("gtfo.player:7", 3, 9));
            var boundaryFrame = new Frame(arena.Slots, 64, arena.Strings.Memory);
            Check(boundaryFrame.Integer(7) == boundary && boundaryFrame.Integer(8) == boundary - 1,
                "an epoch at the safe-integer boundary is stored whole");
            var atBoundary = Rendered(boundaryFrame, row, 0, settled: false).GetProperty("spawned");
            Check(atBoundary.GetProperty("worldEpoch").GetInt64() == boundary
                && atBoundary.GetProperty("lifeEpoch").GetInt64() == boundary - 1,
                "an epoch at the safe-integer boundary round-trips exactly");

            // An entity target is the same value form, so its epochs are the contract's too: the largest safe
            // integer round-trips and nothing is narrowed to a 32-bit component.
            var targetRow = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""");
            var targetArena = new FrameSpace(256, 65536);
            var targetWriter = new FrameWriter(targetArena.Slots, 0, targetRow.Span, targetArena.Strings);
            var wide = Entity("gtfo.item:15", boundary, boundary - 1);
            ResultRowAbi.Write(ref targetWriter, targetRow, 0, RuntimeJson.Parse("""{ "target": {"id":"gtfo.item:15","worldEpoch":%W%,"lifeEpoch":%L%}, "status": "succeeded", "committed": "confirmed", "code": "committed" }"""
                .Replace("%W%", boundary.ToString()).Replace("%L%", (boundary - 1).ToString())), wide);
            var wideFrame = new Frame(targetArena.Slots, 0, targetArena.Strings.Memory);
            var wideTarget = Rendered(wideFrame, targetRow, 0, settled: false).GetProperty("target");
            Check(wideTarget.GetProperty("id").GetString() == "gtfo.item:15"
                && wideTarget.GetProperty("worldEpoch").GetInt64() == boundary
                && wideTarget.GetProperty("lifeEpoch").GetInt64() == boundary - 1,
                "an entity target keeps the id it was written for and both epochs without narrowing");
        }
        return checks;
    }

    /// <summary>
    /// The production path: a plan whose handler writes a row set the descriptor refuses. The command is
    /// conservatively `failed + unknown` with the ABI's own code, no committed fact is published, and the row the
    /// handler wrote is not republished as an output.
    /// </summary>
    private static int Boundary()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void Reject(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }

        // A malformed row set is the command's own failure, and the fact it tried to publish is not published.
        foreach (var (rows, name) in new (Func<JsonElement>[], string)[]
        {
            (new Func<JsonElement>[] { () => RuntimeJson.From(new { result = new[] { Row("example.rows:1", "committed", "confirmed", "committed") } }) }, "a commit word as the row status"),
            (new Func<JsonElement>[] { () => RuntimeJson.From(new { result = new[] { RuntimeJson.From(new { target = new EntityReference("example.rows:1", 1, 1), status = "succeeded", committed = "confirmed", amount = 5, target_count = 1 }) } }) }, "a row without its code column"),
        })
        {
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: _ => CommandResult.Succeeded(rows[0](),
                new RuntimeFact(Fixture.Trigger(Id), RuntimeJson.EmptyObject)));
            scenario.Plan("rows", Id);
            var tick = Beat(scenario, module, "row-" + name);
            var result = tick.Commands.Single().Result;
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown,
                "a malformed row set is a conservative failed + unknown [" + name + "]");
            Check(result.Code is RuntimeAbiCodes.ResultRowIncomplete or RuntimeAbiCodes.ResultRowStatus,
                "a malformed row set reports the ABI's own code [" + name + ":" + result.Code + "]");
            Check(!result.Outputs.TryGetProperty("result", out _), "a refused row set is not republished as an output [" + name + "]");
            Check(tick.Events.All(receipt => !receipt.EventId.StartsWith("fact:", StringComparison.Ordinal)),
                "a refused row set publishes no committed fact [" + name + "]");
        }

        // The row count is the recipient count: a handler that wrote one row too many is refused.
        {
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: _ => CommandResult.Succeeded(RuntimeJson.From(new
            {
                result = new[] { Row("example.rows:1", "succeeded", "confirmed", "committed"), Row("example.rows:1", "succeeded", "confirmed", "committed") }
            })));
            scenario.Plan("count", Id);
            var result = Beat(scenario, module, "row-count").Commands.Single().Result;
            Check(result.Status == CommandStatuses.Failed && result.Code == RuntimeAbiCodes.ResultRowIncomplete,
                "a row set wider than the explicit recipient set is refused");
        }

        // A resolved empty recipient set is the kernel's own answer: succeeded + confirmed `no-targets` with no rows,
        // and the handler is never invoked.
        {
            const string set = "example.alpha";
            var scenario = new Scenario();
            var invoked = 0;
            var source = Fixture.Module(set, handler: _ => { invoked++; return CommandResult.Rejected("example-refused"); },
                effectPort: true, restore: _ => { }, endOn: true, recipientSet: true);
            var registry = JsonNode.Parse(source.Declaration.Registry.GetRawText())!;
            var action = registry["capabilities"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == Fixture.ActionCapability(set))!["graph"]!.AsObject();
            var input = action["inputs"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == "target")!.AsObject();
            input["id"] = "targets"; input["cardinality"] = "many";
            action["recipients"]!["input"] = "targets"; action["recipients"]!["cardinality"] = "many";
            var module = scenario.Kernel.RegisterModule(TestProviders.Change(source, registry: registry.ToJsonString(),
                shapes: new Dictionary<string, HandlerShape> { [Fixture.Handler(set)] = Fixture.Shape(set, new[] { "targets" }) })
                with
            { EntityResolvers = scenario.Resolvers(set) }, RuntimeLogLevel.Off);
            scenario.Kernel.LoadPlan(TestRuntime.ReadPlan("framework-completion", "end-recipient-set"));
            Fixture.Raise(module, Fixture.Trigger(set), "empty-set", 1, 0, ("target", scenario.Player(set, 1)), ("targets", Array.Empty<EntityReference>()));
            var result = scenario.Kernel.Advance(0, true).Commands.Single().Result;
            Check(invoked == 0 && result.Status == CommandStatuses.Succeeded && result.CommitState == CommitStates.Confirmed
                    && result.Code == "no-targets" && result.Outputs.GetProperty("result").GetArrayLength() == 0,
                "a resolved empty recipient set is answered no-targets without invoking the handler");
        }

        // A recipient set as wide as a set may be (which is also the per-command fact budget) is walked whole by the
        // action skeleton: one row for every recipient, none dropped and the command not refused.
        {
            const string set = "example.alpha";
            const int recipients = Wire.MaxSetWidth;
            var scenario = new Scenario();
            TargetStep<int> step = static (ref int amount, EntityReference target, int index, RowWriter rows)
                => rows.Number(4, amount).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
            var source = Fixture.Module(set, handler: context => { var amount = 5; return context.EachTarget(ref amount, step, countColumn: 5); },
                effectPort: true, restore: _ => { }, endOn: true, recipientSet: true);
            var registry = JsonNode.Parse(source.Declaration.Registry.GetRawText())!;
            var action = registry["capabilities"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == Fixture.ActionCapability(set))!["graph"]!.AsObject();
            var input = action["inputs"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == "target")!.AsObject();
            input["id"] = "targets"; input["cardinality"] = "many";
            action["recipients"]!["input"] = "targets"; action["recipients"]!["cardinality"] = "many";
            var module = scenario.Kernel.RegisterModule(TestProviders.Change(source, registry: registry.ToJsonString(),
                shapes: new Dictionary<string, HandlerShape> { [Fixture.Handler(set)] = Fixture.Shape(set, new[] { "targets" }) })
                with
            { EntityResolvers = scenario.Resolvers(set) }, RuntimeLogLevel.Off);
            scenario.Kernel.LoadPlan(TestRuntime.ReadPlan("framework-completion", "end-recipient-set"));
            var targets = Enumerable.Range(1, recipients).Select(index => scenario.Player(set, index)).ToArray();
            Fixture.Raise(module, Fixture.Trigger(set), "wide-set", 1, 0, ("target", scenario.Player(set, 1)), ("targets", targets));
            var result = scenario.Kernel.Advance(0, true).Commands.Single().Result;
            var rows = result.Outputs.GetProperty("result");
            Check(result.Status == CommandStatuses.Succeeded && rows.GetArrayLength() == recipients
                    && rows[recipients - 1].GetProperty("target").GetProperty("id").GetString() == set + ":" + recipients
                    && rows[recipients - 1].GetProperty("target_count").GetInt64() == recipients,
                "a full-width recipient set is walked whole: one row per recipient, none dropped and none refused [" + result.Status + ":" + result.Code + "]");
        }

        // One row per recipient, in the recipients' own order: each row occupies its own stride of the region, the
        // row at position one is about the second recipient, and a row written with the other recipient's identity
        // is refused rather than moved.
        {
            var row = Descriptor(EntityPort, """{ "id": "target", "type": "entity" }""", capacity: 2);
            var arena = new FrameSpace(512, 65536);
            var writer = new FrameWriter(arena.Slots, 0, row.Span, arena.Strings);
            var first = Entity("gtfo.player:7", 3, 9); var second = Entity("gtfo.player:8", 3, 9);
            ResultRowAbi.Write(ref writer, row, 0, RuntimeJson.Parse("""{ "target": {"id":"gtfo.player:7","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed" }"""), first);
            ResultRowAbi.Write(ref writer, row, 1, RuntimeJson.Parse("""{ "target": {"id":"gtfo.player:8","worldEpoch":3,"lifeEpoch":9}, "status": "rejected", "committed": "none", "code": "committed" }"""), second);
            var frame = new Frame(arena.Slots, 0, arena.Strings.Memory);
            Check(frame.String(row.RowSlot(0)) == "gtfo.player:7" && frame.String(row.RowSlot(1)) == "gtfo.player:8"
                && frame.EnumIndex(row.RowSlot(0) + 3) == 0 && frame.EnumIndex(row.RowSlot(1) + 3) == 2,
                "each row keeps its own stride and the recipients' own order");
            Reject(() => Write(row, 0, """{ "target": {"id":"gtfo.player:8","worldEpoch":3,"lifeEpoch":9}, "status": "succeeded", "committed": "confirmed", "code": "committed" }""", first),
                RuntimeAbiCodes.ResultRowIncomplete, "a first row that names the second recipient");
        }

        // A pre-invocation refusal still reports the explicit recipient, one rejected row, with the code the kernel
        // refused by — the same descriptor the handler's rows go through.
        {
            var scenario = new Scenario();
            var module = scenario.Register(Id);
            scenario.Plan("stale", Id);
            var tick = scenario.Kernel.CurrentTick + 1;
            var dispatched = scenario.Raise(module, "row-stale", Id, tick, new EntityReference(Id + ":1", scenario.World, 1));
            if (dispatched.Status != "queued") throw new Exception("FAIL: the event was not accepted: " + dispatched.Code);
            scenario.Life = 2;
            var result = scenario.Kernel.Advance(tick, true).Commands.Single().Result;
            Check(result.Code == "stale-entity", "the stale recipient is refused before the handler runs");
            var rows = result.Outputs.GetProperty("result");
            Check(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == 1
                && rows[0].GetProperty("status").GetString() == CommandStatuses.Cancelled
                && rows[0].GetProperty("committed").GetString() == CommitStates.None
                && rows[0].GetProperty("code").GetString() == "stale-entity"
                && rows[0].GetProperty("target").GetProperty("id").GetString() == Id + ":1"
                && !rows[0].TryGetProperty("amount", out _),
                "a kernel refusal writes one row per explicit recipient with the refusal's own code");
        }

        // The region a row is written into belongs to the activation, and the same step's region is reused by the
        // next dispatch: a column the refused row never wrote must not surface the value an earlier command's row
        // left in the same slots.
        {
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: _ => CommandResult.Succeeded(RuntimeJson.From(new
            {
                result = new
                {
                    target = new EntityReference(Id + ":1", scenario.World, scenario.Life),
                    status = "succeeded",
                    committed = "confirmed",
                    code = "committed",
                    amount = 5,
                    target_count = 1
                }
            })));
            scenario.Plan("reuse", Id);
            var first = Beat(scenario, module, "row-reuse-first").Commands.Single().Result;
            Check(first.Outputs.GetProperty("result").GetProperty("amount").GetDouble() == 5,
                "the first command writes its own row into the step's region");
            var tick = scenario.Kernel.CurrentTick + 1;
            var dispatched = scenario.Raise(module, "row-reuse-refused", Id, tick, new EntityReference(Id + ":1", scenario.World, 1));
            if (dispatched.Status != "queued") throw new Exception("FAIL: the event was not accepted: " + dispatched.Code);
            scenario.Life = 2;
            var refused = scenario.Kernel.Advance(tick, true).Commands.Single().Result;
            var rows = refused.Outputs.GetProperty("result");
            Check(rows[0].GetProperty("code").GetString() == "stale-entity"
                && !rows[0].TryGetProperty("amount", out _) && !rows[0].TryGetProperty("target_count", out _),
                "a refused row reports no value an earlier command left in the same slots");
        }
        return checks;
    }

    /// <summary>
    /// The typed read a downstream step makes: a step wired to an earlier step's result-row output resolves its rows
    /// from the activation's typed region through the compiled descriptor — one row per recipient, in recipient
    /// order — and never from the receipt the command published. The receipt is the boundary's own rendering, so it
    /// can be changed or omitted without changing what a later step reads, and a row set the ABI refused or never
    /// wrote leaves nothing behind to read.
    /// </summary>
    private static int Downstream()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        JsonElement Row(Scenario scenario) => RuntimeJson.From(new
        {
            target = new EntityReference(Id + ":1", scenario.World, scenario.Life),
            status = "succeeded",
            committed = "confirmed",
            code = "committed",
            amount = 5,
            target_count = 1
        });

        // A two-step plan whose second step reads the first step's result row. The consumer records what it was
        // handed, so the assertions are about the value a handler really received and not about an internal table.
        {
            var seen = new List<JsonElement>();
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
                return CommandResult.Succeeded(RuntimeJson.From(new { result = Row(scenario) }));
            }, resultInput: true);
            scenario.Plan("downstream", Id, steps: 2, resultInput: true);
            var tick = Beat(scenario, module, "row-downstream");
            Check(seen.Count == 1, "the downstream step ran once with its row input");
            var received = seen[0];
            Check(received.ValueKind == JsonValueKind.Object
                && received.GetProperty("status").GetString() == CommandStatuses.Succeeded
                && received.GetProperty("committed").GetString() == CommitStates.Confirmed
                && received.GetProperty("code").GetString() == "committed"
                && received.GetProperty("target").GetProperty("id").GetString() == Id + ":1"
                && received.GetProperty("amount").GetDouble() == 5,
                "a downstream step receives the row its source wrote, with every column the writer stored");

            // The receipt is observational. The activation's own step frame carries no row port at all — so nothing
            // downstream could have read the receipt's rendering — while a caller outside the kernel still receives
            // its own rendered receipt.
            var frame = scenario.Kernel.TestStepFrame(0)!.Value;
            Check(frame.ValueKind == JsonValueKind.Object && !frame.TryGetProperty("result", out _),
                "the internal step frame carries no rendered row port");
            var receipt = tick.Commands[0].Result.Outputs.GetProperty("result");
            Check(receipt.GetProperty("code").GetString() == "committed"
                && receipt.GetProperty("target").GetProperty("id").GetString() == Id + ":1",
                "the receipt still renders the rows for a caller outside the kernel");
            Check(!received.Equals(receipt),
                "the downstream value is the typed region's own rendering, not the receipt's element");
        }

        // A malformed row set is the source command's own failure and stops the walk: the consumer never runs, so no
        // partial row reaches it. The activation that follows reads only the rows its own source wrote.
        {
            var seen = new List<JsonElement>();
            var arrivals = 0;
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
                arrivals++;
                // The first arrival writes a row without its code column: the descriptor refuses the whole set.
                return arrivals == 1
                    ? CommandResult.Succeeded(RuntimeJson.From(new
                    {
                        result = RuntimeJson.From(new
                        {
                            target = new EntityReference(Id + ":1", scenario.World, scenario.Life),
                            status = "succeeded",
                            committed = "confirmed",
                            amount = 5,
                            target_count = 1
                        })
                    }))
                    : CommandResult.Succeeded(RuntimeJson.From(new { result = Row(scenario) }));
            }, resultInput: true);
            scenario.Plan("malformed", Id, steps: 2, resultInput: true);
            var refused = Beat(scenario, module, "row-malformed").Commands.Single().Result;
            Check(refused.Status == CommandStatuses.Failed && refused.CommitState == CommitStates.Unknown
                && refused.Code == RuntimeAbiCodes.ResultRowIncomplete,
                "a row set the descriptor refuses is the source command's own failure");
            Check(seen.Count == 0, "a malformed row set reaches no downstream step");
            var next = Beat(scenario, module, "row-malformed-next");
            Check(seen.Count == 1 && next.Commands.Count == 2
                && seen[0].GetProperty("code").GetString() == "committed",
                "the next activation's consumer reads only the rows that activation wrote");
        }

        // A step that wrote no rows at all — a handler that published none — has none to read: the consumer's read is
        // refused by name and its handler never runs, rather than being answered with another step's or an earlier
        // activation's rows.
        {
            var seen = new List<JsonElement>();
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
                return CommandResult.Succeeded(RuntimeJson.EmptyObject);
            }, resultInput: true);
            scenario.Plan("unwritten", Id, steps: 2, resultInput: true);
            var tick = Beat(scenario, module, "row-unwritten");
            Check(tick.Commands.Count == 2 && tick.Commands[1].Result.Code == "from-step-value",
                "a step that wrote no rows is refused by name where it is read");
            Check(seen.Count == 0, "no row reaches the consumer of a step that wrote none");
        }

        // A row crosses a suspension with the flow that wrote it: a `delay` or a `g-wait` between the writer and the
        // step reading its row restores exactly the rows that flow wrote, text columns included, so the read after
        // the pause is answered with the source command's row and not refused from-step-value. A flow whose source
        // wrote no rows still has none after the pause: the restored rows are the captured ones, nothing more.
        foreach (var plan in new[] { "downstream-delay", "downstream-wait" })
        {
            var seen = new List<JsonElement>();
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
                return CommandResult.Succeeded(RuntimeJson.From(new { result = Row(scenario) }));
            }, resultInput: true);
            scenario.Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
            scenario.Kernel.RegisterModule(VariableContracts.Module(), RuntimeLogLevel.Off);
            scenario.Plan(plan, Id, steps: 2, resultInput: true);
            var first = Beat(scenario, module, plan);
            Check(first.Commands.Count == 1 && seen.Count == 0, plan + ": only the writer runs before the pause");
            var after = scenario.Kernel.Advance(scenario.Kernel.CurrentTick + 3, true);
            Check(after.Commands.Count == 1 && after.Commands[0].Result.Status == CommandStatuses.Succeeded && seen.Count == 1
                && seen[0].GetProperty("code").GetString() == "committed"
                && seen[0].GetProperty("target").GetProperty("id").GetString() == Id + ":1"
                && seen[0].GetProperty("amount").GetDouble() == 5,
                plan + ": the reader after the pause is handed the row its source wrote");
        }
        foreach (var plan in new[] { "downstream-delay", "downstream-wait" })
        {
            var seen = new List<JsonElement>();
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
                return CommandResult.Succeeded(RuntimeJson.EmptyObject);
            }, resultInput: true);
            scenario.Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
            scenario.Kernel.RegisterModule(VariableContracts.Module(), RuntimeLogLevel.Off);
            scenario.Plan(plan, Id, steps: 2, resultInput: true);
            Beat(scenario, module, plan + "-unwritten");
            var after = scenario.Kernel.Advance(scenario.Kernel.CurrentTick + 3, true);
            Check(after.Commands.Count == 1 && after.Commands[0].Result.Code == "from-step-value" && seen.Count == 0,
                plan + ": a row nobody wrote is still refused by name after the pause");
        }

        // The canonical item-spawn shape through the whole path: a later nullable entity column keeps the reference
        // the handler wrote, so a downstream reader receives exactly that reference — its own id and both epochs —
        // and never a spelling composed from the frame's components.
        {
            var seen = new List<JsonElement>();
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
                return CommandResult.Succeeded(RuntimeJson.From(new
                {
                    result = SpawnedValue(new EntityReference(Id + ":1", scenario.World, scenario.Life), "gtfo.item:7", 5, 6)
                }));
            }, resultInput: true, spawnedRow: true);
            scenario.Plan("spawned", Id, steps: 2, resultInput: true);
            var tick = Beat(scenario, module, "row-spawned");
            Check(seen.Count == 1 && tick.Commands.Count == 2, "the consumer of a spawned row ran once");
            var spawned = seen[0].GetProperty("spawned");
            Check(spawned.GetProperty("id").GetString() == "gtfo.item:7"
                && spawned.GetProperty("worldEpoch").GetInt64() == 5
                && spawned.GetProperty("lifeEpoch").GetInt64() == 6
                && spawned.GetProperty("id").GetString() != "e:5:6:7",
                "a downstream reader receives the exact entity a later column carried [" + spawned.GetRawText() + "]");
            var receipt = tick.Commands[0].Result.Outputs.GetProperty("result").GetProperty("spawned");
            Check(receipt.GetProperty("id").GetString() == "gtfo.item:7" && receipt.GetProperty("worldEpoch").GetInt64() == 5,
                "the receipt renders the same stored reference the downstream reader reads");
        }

        // The same shape with a null later column: the absence the handler wrote stays the absence a reader sees, in
        // the receipt and downstream alike.
        {
            var seen = new List<JsonElement>();
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
                return CommandResult.Succeeded(RuntimeJson.From(new
                {
                    result = SpawnedValue(new EntityReference(Id + ":1", scenario.World, scenario.Life), null, 0, 0)
                }));
            }, resultInput: true, spawnedRow: true);
            scenario.Plan("spawned-null", Id, steps: 2, resultInput: true);
            var tick = Beat(scenario, module, "row-spawned-null");
            Check(seen.Count == 1 && seen[0].GetProperty("spawned").ValueKind == JsonValueKind.Null,
                "a null later entity column reaches a downstream reader as null");
            Check(tick.Commands[0].Result.Outputs.GetProperty("result").GetProperty("spawned").ValueKind == JsonValueKind.Null,
                "a null later entity column stays null in the receipt");
        }
        return checks;
    }

    /// <summary>
    /// Every action the reviewed Primitive projection declares with a recipient set, taken through the row writer with
    /// a representative row built from its own declared columns and a recipient id of the kind a snapshot publishes.
    /// A recipient input that declares only a subkind (`gtfo.map_object.door`, `gtfo.map_object.terminal`) is the case
    /// that matters: ids carry the parent kind (`gtfo.map_object:door/...`), so a writer that compared the id's kind
    /// with the declared subkind refused every row of `door_lock`, `door_open`, `foaming` over doors and the terminal
    /// actions as `result-row-incomplete` even though the native write had landed.
    /// </summary>
    private static int Catalog()
    {
        var checks = 0;
        foreach (var primitive in new[] { (Name: Blocks.ActionMapDoorOpen.Id, Value: PrimitiveContracts.Graph(Blocks.ActionMapDoorOpen.Id)) })
        {
            var graph = primitive.Value;
            if (!graph.TryGetProperty("recipients", out var recipients)) continue;
            var name = RuntimeJson.Text(recipients, "result");
            var port = graph.GetProperty("outputs").EnumerateArray().Single(candidate => RuntimeJson.Text(candidate, "id") == name);
            var row = ResultRowAbi.Compile(port, graph, 0, 2);
            // The recipient input's first declared kind, as an id spells it: the first two segments of the kind
            // (`gtfo.map_object.door` is carried by `gtfo.map_object:door/...`).
            var inputKinds = RuntimeGraphContracts.DeclaredEntityKinds(
                RuntimeGraphContracts.PortOf(graph, "inputs", RuntimeJson.Text(recipients, "input"))!.Value);
            var recipient = row.TargetKind switch
            {
                ValueKind.Entity => Entity(string.Join('.', (inputKinds.Length > 0 ? inputKinds[0] : EntityKinds.Player).Split('.').Take(2)) + ":catalog/1", 1, 1),
                ValueKind.Resource => Resource(Wire.ResourceKinds[row.TargetResourceKind], "catalog.resource"),
                _ => Handle(1, 1, 1, 0)
            };
            var columns = new Dictionary<string, object?>();
            for (var column = 0; column < row.Columns.Length; column++)
            {
                var declared = row.Columns[column];
                if (declared.Settlement) continue;
                columns[declared.Id] = column switch
                {
                    0 => recipient,
                    1 => "succeeded",
                    2 => "confirmed",
                    3 => "committed",
                    _ => declared.Kind switch
                    {
                        ValueKind.Boolean => true,
                        ValueKind.Integer => 1,
                        ValueKind.Number => 1.5,
                        ValueKind.String => "catalog",
                        ValueKind.Vector3 => new[] { 0.0, 0.0, 0.0 },
                        ValueKind.Entity => new EntityReference(EntityKinds.Player + ":catalog/2", 1, 1),
                        ValueKind.Enum => declared.Members![0],
                        _ => throw new Exception("FAIL: " + primitive.Name + " declares the unrepresentable column " + declared.Id)
                    }
                };
            }
            try { Write(row, 0, RuntimeJson.From(columns), recipient); }
            catch (RuntimeContractException error)
            { throw new Exception("FAIL: " + primitive.Name + " refuses its own representative row: " + error.Code + " " + error.Message); }
            checks++;
        }
        if (checks != 1) throw new Exception("FAIL: the canonical door result schema was not checked");

        // A subkind-only input still refuses an id of another namespace: the published kind of `gtfo.map_object.door`
        // is `gtfo.map_object`, not every kind.
        var door = PrimitiveContracts.Graph(Blocks.ActionMapDoorLock.Id);
        var doorRow = ResultRowAbi.Compile(door.GetProperty("outputs").EnumerateArray().Single(candidate => RuntimeJson.Text(candidate, "id") == "result"), door, 0, 2);
        var player = Entity("gtfo.player:7", 1, 1);
        try
        {
            Write(doorRow, 0, RuntimeJson.From(new { target = player, status = "succeeded", committed = "confirmed", code = "committed", target_count = 1 }), player);
            throw new Exception("FAIL: a door recipient input accepted a player id");
        }
        catch (RuntimeContractException error) when (error.Code == RuntimeAbiCodes.ResultRowIncomplete) { checks++; }
        return checks;
    }

    // ---- fixtures ----------------------------------------------------------------------------------------------

    /// <summary>One action capability whose recipient port and row are the pair under test.</summary>
    private static JsonElement Graph(string recipientPort, string target, string? later = null)
    {
        var port = RuntimeJson.Parse(recipientPort);
        var fields = "[ " + target
            + """, { "id": "status", "type": "enum", "schema": "execution_outcome" }"""
            + """, { "id": "committed", "type": "enum", "schema": "commit_state" }"""
            + """, { "id": "code", "type": "string" }"""
            + (later == null ? "" : ", " + later) + " ]";
        var json = """
        {
          "domains": ["map"],
          "execution": "host",
          "inputs": [ { "id": "in", "type": "execution" }, %PORT% ],
          "outputs": [ { "id": "next", "type": "execution" },
            { "id": "result", "type": "result", "schema": "example.result.rows", "fields": %FIELDS% } ],
          "parameters": [],
          "recipients": { "input": "%INPUT%", "target": "%TARGET%", "cardinality": "one", "requires": [], "result": "result" }
        }
        """;
        return RuntimeJson.Parse(json
            .Replace("%PORT%", recipientPort).Replace("%FIELDS%", fields)
            .Replace("%INPUT%", RuntimeJson.Text(port, "id")).Replace("%TARGET%", RuntimeJson.Text(port, "type")));
    }

    /// <summary>The descriptor one contract compiles to, at the region's own slot.</summary>
    private static ResultRowDescriptor Descriptor(string recipientPort, string target, string? later = null, int capacity = 4, string? codes = null)
    {
        var fields = "[ " + target
            + """, { "id": "status", "type": "enum", "schema": "execution_outcome" }"""
            + """, { "id": "committed", "type": "enum", "schema": "commit_state" }"""
            + """, { "id": "code", "type": "string" }"""
            + (later == null ? "" : ", " + later) + " ]";
        var port = RuntimeJson.Parse("""{ "id": "result", "type": "result", "schema": "example.result.rows", "fields": """
            + fields + """, "codes": """ + (codes ?? """["example-refused"]""") + " }");
        return ResultRowAbi.Compile(port, Graph(recipientPort, target, later), 0, capacity);
    }

    /// <summary>One row written into its own arena, returning the frame it was written into.</summary>
    private static Frame Write(ResultRowDescriptor row, int index, string value, JsonElement recipient)
        => Write(row, index, RuntimeJson.Parse(value), recipient);

    private static Frame Write(ResultRowDescriptor row, int index, JsonElement value, JsonElement recipient)
    {
        var arena = new FrameSpace(512, 65536);
        var writer = new FrameWriter(arena.Slots, 0, row.Span, arena.Strings);
        ResultRowAbi.Write(ref writer, row, index, value, recipient);
        ResultRowAbi.ValidatePairing(row, index, value);
        return new Frame(arena.Slots, 0, arena.Strings.Memory);
    }

    /// <summary>One written row as the JSON a reader sees: the ABI's own renderer, the one the receipt and a
    /// downstream read both use. A single row renders as the object the handler's own set carried. It takes no
    /// boundary value: every column, the target included, is decoded from the slots the writer filled.</summary>
    private static JsonElement Rendered(Frame frame, ResultRowDescriptor row, int index, bool settled)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        ResultRowAbi.WriteSet(writer, frame, row, 1, set: false, settled);
        writer.Flush();
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The fixture row's own values, one row per recipient.</summary>
    private static JsonElement Row(string target, string status, string committed, string code) => RuntimeJson.From(new
    {
        target = new EntityReference(target, 1, 1),
        status,
        committed,
        code,
        amount = 5,
        target_count = 1
    });

    /// <summary>One row of the canonical item-spawn shape: the four fixed columns and the nullable `spawned` entity
    /// the row reports, which is the later-entity column under test. Its target is the caller's, because a row's
    /// target is the recipient at its position.</summary>
    private static JsonElement SpawnedValue(EntityReference target, string? spawnedId, long world, long life) => RuntimeJson.From(new
    {
        target,
        status = "succeeded",
        committed = "confirmed",
        code = "committed",
        spawned = spawnedId == null ? (EntityReference?)null : new EntityReference(spawnedId, world, life)
    });

    private static JsonElement Entity(string id, long world, long life) => RuntimeJson.From(new EntityReference(id, world, life));
    private static JsonElement Resource(string kind, string id) => RuntimeJson.From(new { resourceKind = kind, resourceId = id });
    private static JsonElement Handle(long world, long life, long local, long provider)
        => RuntimeJson.From(new { worldEpoch = world, lifeEpoch = life, local, provider });

    /// <summary>One arrival at the provider's own trigger about the fixture's one recipient.</summary>
    private static TickResult Beat(Scenario scenario, RuntimeModuleHandle module, string id)
    {
        var tick = scenario.Kernel.CurrentTick + 1;
        var dispatched = scenario.Raise(module, id, Id, tick);
        if (dispatched.Status != "queued") throw new Exception("FAIL: the event was not accepted: " + dispatched.Code);
        return scenario.Kernel.Advance(tick, true);
    }
}
