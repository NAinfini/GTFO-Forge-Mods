using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeEngine.Framework;

/// <summary>
/// The cost of one action step through the kernel, measured on this machine: an event that reaches a one-step plan
/// whose action writes one result row, advanced with receipts off (the host's per-frame advance). It reports the
/// thread's allocated bytes and the wall time per event. The numbers are an offline kernel measurement — not a game
/// frame — and are only comparable between runs of this same scenario on the same machine.
/// Run with: ForgeEngine.EngineCore.Tests Framework bench [iterations]
/// </summary>
internal static class TypedOutputBench
{
    private const string Id = "example.rows";
    private const string Detail = "{\"entry\":\"bench\"}";

    /// <summary>The row a domain handler builds today: its own record, serialized by reflection.</summary>
    private sealed record LegacyRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code, double Amount,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>The shape every domain handler has before its migration: one record per recipient, the row set
    /// framed with <see cref="ResultRows.Of(object)"/>, the conclusion taken by the one aggregation rule.</summary>
    private static CommandResult Legacy(CommandContext context)
    {
        var rows = new List<LegacyRow>(1)
        {
            new(context.GetEntityInput("target"), CommandStatuses.Succeeded, CommitStates.Confirmed, ResultCode.Committed, 5, 1)
        };
        return ResultAggregation.Conclude(rows, static row => row.CommitState, static row => row.Code, ResultRows.Of(rows));
    }

    /// <summary>The same row through the typed writer: the columns by position, the conclusion as a value.</summary>
    private static CommandResult Typed(CommandContext context)
    {
        var rows = context.Rows;
        rows.Begin(0).Number(4, 5).Integer(5, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return rows.Conclude();
    }

    /// <summary>One measured case: the action handler and what the provider does after the advance. Every case's
    /// event is published the way a provider publishes it: written by position.</summary>
    private sealed record Case(string Name, CommandHandler Handler, Action<Scenario, RuntimeModuleHandle>? After = null);

    private static readonly CommandResult Nothing = CommandResult.Succeeded(RuntimeJson.EmptyObject);
    private static DeferredCommandToken? pendingToken;

    private static Case[] Cases() => new Case[]
    {
        new("legacy-json-row", Legacy),
        new("typed-row", Typed),
        // The floor: the same event and step with a handler that writes nothing, so what the other cases cost
        // beyond it is the output side alone.
        new("no-output", static _ => Nothing),
        // A conclusion that carries a diagnostic detail: built per call, and as a value.
        new("detail-create", static context =>
        {
            CommandResult concluded = Typed(context);
            return CommandResult.Create(concluded.Status, concluded.CommitState, concluded.Code, Detail, RuntimeJson.EmptyObject);
        }),
        new("detail-outcome", static context =>
        {
            var rows = context.Rows;
            rows.Begin(0).Number(4, 5).Integer(5, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
            return rows.Conclude().WithDetail(Detail);
        }),
        // A committed fact beside the row, written by position. The fact is published on the fixture's own trigger,
        // so the step runs once more for it (without a fact).
        new("fact-writer", static context =>
        {
            var outcome = Typed(context);
            if (context.CausalDepth > 0) return outcome;
            var fact = context.BeginFact(factGate!);
            if (context.Rows.TryTarget(0, out var target)) fact.Values.Entity(1, target);
            fact.Add();
            return outcome;
        }),
        // The trigger side alone: the payload written by position.
        new("event-writer", static _ => Nothing),
        // A result that ends later: the terminal row as a JSON row set, and through the typed row writer.
        new("deferred-json", Deferring, After: static (_, module) =>
        {
            var token = pendingToken!; pendingToken = null;
            module.CompleteDeferredResult(token, CommandResult.Create(CommandStatuses.Succeeded, CommitStates.Confirmed, ResultCode.Committed, "",
                ResultRows.Of(new[] { new LegacyRow(token.Target, CommandStatuses.Succeeded, CommitStates.Confirmed, ResultCode.Committed, 5, 1) })));
        }),
        new("deferred-writer", Deferring, After: static (_, module) =>
        {
            var token = pendingToken!; pendingToken = null;
            var amount = 5;
            module.CompleteDeferredResult(token, ref amount, DeferredStep);
        }),
    };

    private static RuntimeSubscriptionGate? eventGate, factGate;
    private static EntityReference? benchTarget;

    private static readonly TargetStep<int> DeferredStep = static (ref int amount, EntityReference target, int index, RowWriter rows)
        => rows.Number(4, amount).Integer(5, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);

    private static CommandResult Deferring(CommandContext context)
    {
        var rows = context.Rows;
        rows.TryTarget(0, out var target);
        pendingToken = context.BeginDeferredResult(target);
        rows.Begin(0).Number(4, 5).Integer(5, 1).End(Status.Partial, Commit.Confirmed, ResultCode.Partial);
        return new Outcome(Status.Partial, Commit.Confirmed, ResultCode.Partial);
    }

    internal static int Run(string[] args)
    {
        var iterations = args.Length > 0 ? int.Parse(args[0]) : 20000;
        foreach (var item in Cases())
        {
            // Three runs of the same case: the first also pays the JIT, so the last is the one to read.
            for (var run = 0; run < 3; run++)
            {
                var (nanoseconds, bytes) = Measure(item, iterations);
                Console.WriteLine($"bench {item.Name} run={run} iterations={iterations} ns/event={nanoseconds:F0} bytes/event={bytes:F0}");
            }
        }
        return 0;
    }

    private static (double Nanoseconds, double Bytes) Measure(Case item, int iterations)
    {
        var scenario = new Scenario();
        var calls = 0;
        var module = scenario.Register(Id, handler: context => { calls++; return item.Handler(context); });
        scenario.Plan("rows", Id);
        eventGate = null; benchTarget = null; pendingToken = null;
        factGate = module.SubscriptionGate(Fixture.Trigger(Id));
        var payload = RuntimeJson.From(new Dictionary<string, object> { ["target"] = new EntityReference(Id + ":1", scenario.World, scenario.Life) });
        const int warmup = 2000;
        var first = scenario.Kernel.CurrentTick + 1;
        var events = new RuntimeEvent[warmup + iterations];
        for (var index = 0; index < events.Length; index++)
            events[index] = new RuntimeEvent("bench-" + index, Fixture.Trigger(Id), scenario.World, first + index, "shared-scope", payload);
        for (var index = 0; index < warmup; index++) Step(scenario, module, item, events[index]);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        for (var index = warmup; index < events.Length; index++) Step(scenario, module, item, events[index]);
        clock.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (calls < events.Length) throw new Exception("FAIL: the bench step ran " + calls + " times for " + events.Length + " events");
        return (clock.Elapsed.TotalMilliseconds * 1_000_000 / iterations, (double)allocated / iterations);
    }

    /// <summary>The trigger of every case: the payload written by position.</summary>
    private static DispatchResult Raise(Scenario scenario, RuntimeModuleHandle module, RuntimeEvent value)
    {
        var raised = module.BeginEvent(eventGate ??= module.SubscriptionGate(Fixture.Trigger(Id)));
        raised.Values.Entity(1, benchTarget ??= new EntityReference(Id + ":1", scenario.World, scenario.Life));
        return raised.Raise(value.EventId, value.WorldEpoch, value.SimulationTick, value.ScopeId);
    }

    private static void Step(Scenario scenario, RuntimeModuleHandle module, Case item, RuntimeEvent value)
    {
        var dispatched = Raise(scenario, module, value);
        if (dispatched.Status != "queued") throw new Exception("FAIL: the bench event was not accepted: " + dispatched.Code);
        scenario.Kernel.Advance(value.SimulationTick, true, receipts: false);
        item.After?.Invoke(scenario, module);
    }
}
