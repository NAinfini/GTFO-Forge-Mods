using System.Reflection;
using System.Text.Json;
using ForgeEngine.Framework;

/// <summary>
/// The typed output side of an action step (plan §3.2 I-PLAN): a handler that writes its rows through
/// <see cref="RowWriter"/> and one that returns the same rows as JSON are one contract. The same rows render the same
/// receipt byte for byte, the same three broken inputs — a row count that is not the recipient count, a row about
/// another recipient, a status and commit that do not pair — are refused with the same code on both paths, a later
/// step reads typed rows that were never rendered, an event written by position is the event its JSON payload is,
/// and the generated <c>Row…</c> constants are the columns the runtime compiles. Nothing here claims game, multiplayer or performance behaviour.
/// </summary>
internal static class TypedOutputTests
{
    private const string Id = "example.rows";
    // The fixture row's own columns after the four fixed ones (Fixture.ResultFields).
    private const int Amount = 4, TargetCount = 5;

    internal static int Run() => Equivalence() + Refusals() + Downstream() + Events() + Constants()
        + SkeletonRows() + Facts() + DeferredResults() + OwnerRows() + Handles() + RecipientRead() + Prediction();

    private static EntityReference Recipient(Scenario scenario, int index = 1) => new(Id + ":" + index, scenario.World, scenario.Life);

    private static JsonElement Row(EntityReference target, string status, string committed, string code) => RuntimeJson.From(new
    {
        target,
        status,
        committed,
        code,
        amount = 5,
        target_count = 1
    });

    private static CommandResult Run(CommandHandler handler, string plan, string eventId)
    {
        var scenario = new Scenario();
        var module = scenario.Register(Id, handler: handler);
        scenario.Plan(plan, Id);
        var tick = scenario.Kernel.CurrentTick + 1;
        var dispatched = scenario.Raise(module, eventId, Id, tick);
        if (dispatched.Status != "queued") throw new Exception("FAIL: the event was not accepted: " + dispatched.Code);
        return scenario.Kernel.Advance(tick, true).Commands.Single().Result;
    }

    private static readonly TargetStep<int> SkeletonStep = static (ref int amount, EntityReference target, int index, RowWriter rows)
        => rows.Number(Amount, amount).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);

    /// <summary>The same rows written both ways render the same receipt: the status, the commit state, the code and
    /// every byte of the outputs.</summary>
    private static int Equivalence()
    {
        var checks = 0;
        var world = new Scenario();
        var target = Recipient(world);
        foreach (var (name, legacy, typed) in new (string, CommandHandler, CommandHandler)[]
        {
            ("a committed row",
                _ => CommandResult.Succeeded(RuntimeJson.From(new { result = Row(target, "succeeded", "confirmed", "committed") })),
                context =>
                {
                    var rows = context.Rows;
                    rows.Begin(0, target).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
                    return rows.Conclude();
                }),
            ("a rejected row",
                _ => CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, ResultCode.StaleEntity, "",
                    RuntimeJson.From(new { result = Row(target, "rejected", "none", "stale-entity") })),
                context =>
                {
                    var rows = context.Rows;
                    rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Rejected, Commit.None, ResultCode.StaleEntity);
                    return rows.Conclude();
                }),
            ("a row written by the action skeleton",
                _ => CommandResult.Succeeded(RuntimeJson.From(new { result = Row(target, "succeeded", "confirmed", "committed") })),
                context => { var amount = 5; return context.EachTarget(ref amount, SkeletonStep); }),
            ("a conclusion that carries a detail",
                _ => CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, ResultCode.StaleEntity, "the recipient left",
                    RuntimeJson.From(new { result = Row(target, "rejected", "none", "stale-entity") })),
                context =>
                {
                    var rows = context.Rows;
                    rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Rejected, Commit.None, ResultCode.StaleEntity);
                    return rows.Conclude().WithDetail("the recipient left");
                }),
        })
        {
            var json = Run(legacy, "rows", "typed-equal-json");
            var region = Run(typed, "rows", "typed-equal-region");
            if (json.Detail != region.Detail)
                throw new Exception($"FAIL: {name} carries the detail '{region.Detail}' through the typed writer and '{json.Detail}' as JSON");
            if (json.Status != region.Status || json.CommitState != region.CommitState || json.Code != region.Code)
                throw new Exception($"FAIL: {name} concludes {region.Status}/{region.CommitState}/{region.Code} through the typed writer and {json.Status}/{json.CommitState}/{json.Code} as JSON");
            if (json.Outputs.GetRawText() != region.Outputs.GetRawText())
                throw new Exception($"FAIL: {name} renders {region.Outputs.GetRawText()} through the typed writer and {json.Outputs.GetRawText()} as JSON");
            if (!json.Outputs.TryGetProperty("result", out var rendered) || rendered.GetProperty("amount").GetDouble() != 5)
                throw new Exception("FAIL: " + name + " rendered no row to compare");
            checks++;
        }
        return checks;
    }

    /// <summary>The three broken inputs, written both ways: each is the command's `failed + unknown` with the row
    /// ABI's own code, the same code on both paths, and neither republishes the refused rows.</summary>
    private static int Refusals()
    {
        var checks = 0;
        var world = new Scenario();
        var target = Recipient(world); var other = Recipient(world, 2);
        foreach (var (name, code, legacy, typed) in new (string, string, CommandHandler, CommandHandler)[]
        {
            ("a row count that is not the recipient count", RuntimeAbiCodes.ResultRowIncomplete,
                _ => CommandResult.Succeeded(RuntimeJson.From(new { result = new[] { Row(target, "succeeded", "confirmed", "committed"), Row(target, "succeeded", "confirmed", "committed") } })),
                context =>
                {
                    var rows = context.Rows;
                    rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
                    rows.Begin(1).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
                    return rows.Conclude();
                }),
            ("no row for the one recipient", RuntimeAbiCodes.ResultRowIncomplete,
                _ => CommandResult.Succeeded(RuntimeJson.From(new { result = Array.Empty<object>() })),
                context => context.Rows.Conclude()),
            ("a row about another recipient", RuntimeAbiCodes.ResultRowIncomplete,
                _ => CommandResult.Succeeded(RuntimeJson.From(new { result = Row(other, "succeeded", "confirmed", "committed") })),
                context =>
                {
                    var rows = context.Rows;
                    rows.Begin(0, other).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
                    return rows.Conclude();
                }),
            ("a status and commit that do not pair", RuntimeAbiCodes.ResultRowStatus,
                _ => CommandResult.Succeeded(RuntimeJson.From(new { result = Row(target, "succeeded", "none", "committed") })),
                context =>
                {
                    var rows = context.Rows;
                    rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.None, ResultCode.Committed);
                    return Outcome.Succeeded();
                }),
        })
        {
            foreach (var (path, result) in new[] { ("as JSON", Run(legacy, "rows", "typed-refused-json")), ("through the typed writer", Run(typed, "rows", "typed-refused-region")) })
            {
                if (result.Status != CommandStatuses.Failed || result.CommitState != CommitStates.Unknown || result.Code != code)
                    throw new Exception($"FAIL: {name} {path} is {result.Status}/{result.CommitState}/{result.Code}, expected failed/unknown/{code}");
                if (result.Outputs.ValueKind == JsonValueKind.Object && result.Outputs.TryGetProperty("result", out _))
                    throw new Exception($"FAIL: {name} {path} republishes the refused rows");
                checks++;
            }
        }
        return checks;
    }

    /// <summary>A later step reads rows the typed writer wrote on an advance that renders no receipt: the rows it is
    /// handed are the region's own, with every column the writer stored.</summary>
    private static int Downstream()
    {
        var seen = new List<JsonElement>();
        var scenario = new Scenario();
        var module = scenario.Register(Id, handler: context =>
        {
            if (context.Inputs.TryGetProperty(Fixture.RowInput, out var carried)) seen.Add(carried.Clone());
            var rows = context.Rows;
            rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
            return rows.Conclude();
        }, resultInput: true);
        scenario.Plan("downstream", Id, steps: 2, resultInput: true);
        var tick = scenario.Kernel.CurrentTick + 1;
        var dispatched = scenario.Raise(module, "typed-downstream", Id, tick);
        if (dispatched.Status != "queued") throw new Exception("FAIL: the event was not accepted: " + dispatched.Code);
        scenario.Kernel.Advance(tick, true, receipts: false);
        if (seen.Count != 1) throw new Exception("FAIL: the downstream step ran " + seen.Count + " times with a row input");
        var row = seen[0];
        if (row.ValueKind != JsonValueKind.Object || row.GetProperty("status").GetString() != CommandStatuses.Succeeded
            || row.GetProperty("committed").GetString() != CommitStates.Confirmed || row.GetProperty("code").GetString() != "committed"
            || row.GetProperty("target").GetProperty("id").GetString() != Id + ":1" || row.GetProperty("amount").GetDouble() != 5
            || row.GetProperty("target_count").GetInt64() != 1)
            throw new Exception("FAIL: the downstream step read " + row.GetRawText() + " from rows that were never rendered");
        return 1;
    }

    /// <summary>An event written by output position is the event its document is — the form a migration image and an
    /// owner's fact off the network arrive in, admitted through the kernel's own entry: the step it reaches is handed
    /// the same inputs and answers the same receipts byte for byte, the replay ledger takes either form of one event
    /// as a repeat of the other, and a value of the wrong kind is refused at publish with the same code.</summary>
    private static int Events()
    {
        const int Target = 1; // the fixture trigger's outputs are `next`, `target`
        (DispatchResult Dispatched, CommandResult? Result, string Seen, DispatchResult? Repeat) Through(bool writer, bool broken)
        {
            var seen = "";
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                seen += context.Inputs.GetRawText();
                var rows = context.Rows;
                rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
                return rows.Conclude();
            });
            scenario.Plan("rows", Id);
            var tick = scenario.Kernel.CurrentTick + 1;
            DispatchResult Publish(bool byPosition)
            {
                if (!byPosition) return scenario.Kernel.Publish(module, new RuntimeEvent("typed-event", Fixture.Trigger(Id), scenario.World, tick, "shared-scope",
                    RuntimeJson.From(new Dictionary<string, object> { ["target"] = broken ? 5 : Recipient(scenario) })));
                var raised = module.BeginEvent(module.SubscriptionGate(Fixture.Trigger(Id)));
                if (broken) raised.Values.Number(Target, 5); else raised.Values.Entity(Target, Recipient(scenario));
                return raised.Raise("typed-event", scenario.World, tick, "shared-scope");
            }
            var dispatched = Publish(writer);
            if (dispatched.Status != "queued") return (dispatched, null, seen, null);
            // The same event in the other form, before the first is walked: the ledger compares the two payload texts.
            var repeat = Publish(!writer);
            var advanced = scenario.Kernel.Advance(tick, true);
            seen += " events " + string.Join(",", advanced.Events);
            return (dispatched, advanced.Commands.Single().Result, seen, repeat);
        }

        var json = Through(writer: false, broken: false); var written = Through(writer: true, broken: false);
        if (json.Result == null || written.Result == null || json.Result.Code != written.Result.Code
            || json.Result.Outputs.GetRawText() != written.Result.Outputs.GetRawText())
            throw new Exception("FAIL: an event written by position answers " + (written.Result?.Outputs.GetRawText() ?? written.Dispatched.Code)
                + " and its JSON payload " + (json.Result?.Outputs.GetRawText() ?? json.Dispatched.Code));
        if (json.Seen != written.Seen || !written.Seen.Contains(Id + ":1", StringComparison.Ordinal))
            throw new Exception("FAIL: an event written by position hands its step " + written.Seen + " and its JSON payload " + json.Seen);
        if (json.Repeat?.Code != "duplicate-event" || written.Repeat?.Code != "duplicate-event")
            throw new Exception($"FAIL: one event repeated in its other form is {written.Repeat?.Code} after the written form and {json.Repeat?.Code} after the JSON form, expected duplicate-event");
        var refusedJson = Through(writer: false, broken: true); var refusedWritten = Through(writer: true, broken: true);
        if (refusedJson.Dispatched.Status != "rejected" || refusedWritten.Dispatched.Status != "rejected"
            || refusedJson.Dispatched.Code != refusedWritten.Dispatched.Code)
            throw new Exception($"FAIL: a number in the target port is {refusedWritten.Dispatched.Status}/{refusedWritten.Dispatched.Code} by position and {refusedJson.Dispatched.Status}/{refusedJson.Dispatched.Code} as JSON");
        return 4;
    }

    private static readonly TargetStep<int> CountedStep = static (ref int amount, EntityReference target, int index, RowWriter rows)
        => rows.Number(Amount, amount).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
    private static readonly TargetStep<(Status Status, Commit Commit)> EndedStep = static (ref (Status Status, Commit Commit) ending, EntityReference target, int index, RowWriter rows)
        => rows.Number(Amount, 5).Integer(TargetCount, 1).End(ending.Status, ending.Commit, ending.Status == Status.Succeeded ? ResultCode.Committed : ResultCode.StaleEntity);
    private static readonly TargetStep<int> ThrowingStep = static (ref int amount, EntityReference target, int index, RowWriter rows)
        => throw new InvalidOperationException("the fixture's native call threw");

    /// <summary>The rows the action skeleton ends itself: with a count column named, a row it ends carries the
    /// recipient count exactly as a row the step ended does; and under the presentation rule a step that throws
    /// leaves its row `failed + none`, with the conclusion the presentation aggregation gives the same row.</summary>
    private static int SkeletonRows()
    {
        var world = new Scenario();
        var target = Recipient(world);
        foreach (var (name, step, status) in new[] { ("a row the step ended", CountedStep, CommandStatuses.Succeeded), ("a row the skeleton ended", ThrowingStep, CommandStatuses.Failed) })
        {
            var counted = Run(context => { var amount = 5; return context.EachTarget(ref amount, step, exceptionCode: ResultCode.StaleEntity, countColumn: TargetCount); }, "rows", "typed-counted");
            if (counted.Status != status || !counted.Outputs.TryGetProperty("result", out var row)
                || !row.TryGetProperty("target_count", out var count) || count.ValueKind != JsonValueKind.Number || count.GetInt64() != 1)
                throw new Exception($"FAIL: {name} is {counted.Status}/{counted.Code} and renders {counted.Outputs.GetRawText()}, expected {status} with target_count 1");
        }
        var legacyRows = new[] { (Status: CommandStatuses.Failed, Code: ResultCode.StaleEntity) };
        var json = Run(_ => ResultAggregation.Presented(legacyRows, static row => row.Status, static row => row.Code,
            RuntimeJson.From(new { result = Row(target, "failed", "none", "stale-entity") })), "rows", "typed-presented-json");
        var typed = Run(context => { var amount = 5; return context.EachTargetPresented(ref amount, ThrowingStep, exceptionCode: ResultCode.StaleEntity); }, "rows", "typed-presented-region");
        if (json.Status != typed.Status || json.CommitState != typed.CommitState || json.Code != typed.Code || typed.CommitState != CommitStates.None
            || typed.Outputs.GetProperty("result").GetProperty("committed").GetString() != CommitStates.None)
            throw new Exception($"FAIL: a presented step that throws is {typed.Status}/{typed.CommitState}/{typed.Code} {typed.Outputs.GetRawText()} through the skeleton and {json.Status}/{json.CommitState}/{json.Code} as JSON");
        return 3;
    }

    /// <summary>A committed fact written by output position is the fact its document is — the form an owner's result
    /// carries it in off the network: the plan mounted on its trigger runs the same number of times with the same
    /// recipient, a command whose rows are refused publishes neither, and the written fact's wire form is the text
    /// it always was.</summary>
    private static int Facts()
    {
        List<string> Through(bool writer, bool refused)
        {
            var seen = new List<string>();
            var scenario = new Scenario();
            RuntimeSubscriptionGate? gate = null;
            var module = scenario.Register(Id, handler: context =>
            {
                var rows = context.Rows;
                rows.TryTarget(0, out var target);
                seen.Add(context.CausalDepth + ":" + target.Id);
                rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, refused ? Commit.None : Commit.Confirmed, ResultCode.Committed);
                if (context.CausalDepth > 0) return rows.Conclude();
                if (!writer) return CommandResult.Succeeded(RuntimeJson.EmptyObject, new RuntimeFact(Fixture.Trigger(Id), RuntimeJson.From(new Dictionary<string, object> { ["target"] = target })));
                var fact = context.BeginFact(gate!);
                fact.Values.Entity(1, target);
                fact.Add();
                return Outcome.Succeeded();
            });
            gate = module.SubscriptionGate(Fixture.Trigger(Id));
            scenario.Plan("rows", Id);
            var tick = scenario.Kernel.CurrentTick + 1;
            scenario.Raise(module, "typed-fact", Id, tick);
            var facts = scenario.Kernel.Advance(tick, true).Commands.First().Result.Facts;
            scenario.Kernel.Advance(tick + 1, true);
            // The wire form of a result's facts, as the owner channel and a migration image write it.
            if (!refused) seen.Add(RuntimeJson.From(new { facts }).GetRawText());
            return seen;
        }

        var json = Through(writer: false, refused: false); var written = Through(writer: true, refused: false);
        var world = new Scenario();
        var wire = "{\"facts\":[{\"bindingId\":\"" + Fixture.Trigger(Id) + "\",\"outputs\":{\"target\":{\"id\":\"" + Id + ":1\",\"worldEpoch\":" + world.World + ",\"lifeEpoch\":" + world.Life + "}}}]}";
        if (written.Count != 3 || written[1] != "1:" + Id + ":1" || written[2] != wire || !json.SequenceEqual(written))
            throw new Exception("FAIL: a fact written by position ran [" + string.Join(",", written) + "] and its document [" + string.Join(",", json) + "], expected the wire form " + wire);
        foreach (var writer in new[] { false, true })
            if (Through(writer, refused: true).Count != 1)
                throw new Exception("FAIL: a command whose rows are refused published its fact " + (writer ? "written by position" : "as JSON"));
        return 3;
    }

    /// <summary>A deferred result completed through the typed row writer is the receipt its JSON row set is, byte for
    /// byte; a second completion of the same target answers false; and a terminal row whose status and commit do not
    /// pair is refused with the row ABI's code and completes nothing.</summary>
    private static int DeferredResults()
    {
        (CommandResult Result, bool Again) Through(Func<RuntimeModuleHandle, DeferredCommandToken, bool> complete)
        {
            DeferredCommandToken? token = null;
            var scenario = new Scenario();
            var module = scenario.Register(Id, handler: context =>
            {
                var rows = context.Rows;
                rows.TryTarget(0, out var target);
                token = context.BeginDeferredResult(target);
                rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Partial, Commit.Confirmed, ResultCode.Partial);
                return new Outcome(Status.Partial, Commit.Confirmed, ResultCode.Partial);
            });
            scenario.Plan("rows", Id);
            var tick = scenario.Kernel.CurrentTick + 1;
            scenario.Raise(module, "typed-deferred", Id, tick);
            scenario.Kernel.Advance(tick, true);
            if (token == null) throw new Exception("FAIL: the deferring step did not run");
            if (!complete(module, token)) throw new Exception("FAIL: the first completion of a pending target answered false");
            var again = complete(module, token);
            return (scenario.Kernel.Advance(tick + 1, true).Commands.Single().Result, again);
        }

        var json = Through((module, token) => module.CompleteDeferredResult(token, CommandResult.Create(CommandStatuses.Succeeded, CommitStates.Confirmed, ResultCode.Committed, "",
            RuntimeJson.From(new { result = Row(token.Target, "succeeded", "confirmed", "committed") }))));
        var typed = Through((module, token) => { var amount = 5; return module.CompleteDeferredResult(token, ref amount, SkeletonStep); });
        if (json.Again || typed.Again) throw new Exception("FAIL: a target that already reached its terminal result was completed again");
        if (json.Result.Status != typed.Result.Status || json.Result.CommitState != typed.Result.CommitState || json.Result.Code != typed.Result.Code
            || json.Result.Outputs.GetRawText() != typed.Result.Outputs.GetRawText() || !typed.Result.Outputs.TryGetProperty("result", out _))
            throw new Exception($"FAIL: a deferred result is {typed.Result.Status}/{typed.Result.Code} {typed.Result.Outputs.GetRawText()} through the typed writer and {json.Result.Status}/{json.Result.Code} {json.Result.Outputs.GetRawText()} as JSON");

        // The broken input: the refusal is thrown at the completion, and the target is still pending afterwards.
        var attempts = 0;
        var refused = Through((module, token) =>
        {
            var ending = (Status.Failed, Commit.Unknown);
            if (attempts++ > 0) return module.CompleteDeferredResult(token, ref ending, EndedStep);
            var unpaired = (Status.Succeeded, Commit.None);
            try { module.CompleteDeferredResult(token, ref unpaired, EndedStep); }
            catch (RuntimeContractException error) when (error.Code == RuntimeAbiCodes.ResultRowStatus)
            { return module.CompleteDeferredResult(token, ref ending, EndedStep); }
            throw new Exception("FAIL: a terminal row whose status and commit do not pair was accepted");
        });
        if (refused.Again || refused.Result.Status != CommandStatuses.Failed || refused.Result.CommitState != CommitStates.Unknown)
            throw new Exception($"FAIL: after a refused terminal row the target completed as {refused.Result.Status}/{refused.Result.CommitState}/{refused.Result.Code}");
        return 3;
    }

    private static readonly OwnerSettlement<int> OwnerStep = static (ref int amount, bool expired, RowWriter rows) =>
    {
        if (!expired) return null;
        if (amount >= 0) rows.Begin(0).Number(Amount, amount).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return rows.Conclude();
    };

    /// <summary>An owner command that answers later writes its rows through the same writer, against the recipients
    /// its step had: while it waits nothing is settled, the rows it then writes render the result the same rows
    /// written in place do, byte for byte, and a settlement that concludes without its row is refused with the row
    /// ABI's code.</summary>
    private static int OwnerRows()
    {
        (CommandResult? Waiting, CommandResult? Settled, CommandResult InPlace) Through(int amount)
        {
            var scenario = new Scenario();
            var deferral = new OwnerDeferralRequest(1, new OwnerSettlementCall<int>(amount, OwnerStep));
            var kept = false;
            var module = scenario.Register(Id, handler: context =>
            {
                var rows = context.Rows;
                kept = scenario.Kernel.KeepOwnerDeferralRows(deferral);
                rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
                return rows.Conclude();
            });
            scenario.Plan("rows", Id);
            var tick = scenario.Kernel.CurrentTick + 1;
            scenario.Raise(module, "typed-owner-rows", Id, tick);
            var inPlace = scenario.Kernel.Advance(tick, true).Commands.Single().Result;
            if (!kept) throw new Exception("FAIL: the recipients of a step with entity recipients were not kept for a later settlement");
            return (scenario.Kernel.SettleOwnerRows(deferral, expired: false), scenario.Kernel.SettleOwnerRows(deferral, expired: true), inPlace);
        }

        var written = Through(5);
        if (written.Waiting != null) throw new Exception("FAIL: a settlement that asked to keep waiting settled as " + written.Waiting.Code);
        if (written.Settled == null || written.Settled.Status != written.InPlace.Status || written.Settled.CommitState != written.InPlace.CommitState
            || written.Settled.Code != written.InPlace.Code || written.Settled.Outputs.GetRawText() != written.InPlace.Outputs.GetRawText()
            || !written.Settled.Outputs.TryGetProperty("result", out _))
            throw new Exception($"FAIL: rows settled later are {written.Settled?.Status}/{written.Settled?.Code} {written.Settled?.Outputs.GetRawText()} and in place {written.InPlace.Status}/{written.InPlace.Code} {written.InPlace.Outputs.GetRawText()}");
        // The broken input: a conclusion with no row for the recipient.
        var missing = Through(-1).Settled;
        if (missing == null || missing.Status != CommandStatuses.Failed || missing.CommitState != CommitStates.Unknown || missing.Code != RuntimeAbiCodes.ResultRowIncomplete)
            throw new Exception($"FAIL: a later settlement without its row is {missing?.Status}/{missing?.CommitState}/{missing?.Code}");
        return 3;
    }

    /// <summary>A handle is a value: the one a module casts writes the four-component wire form and reads back from
    /// it equal and with the same hash, the module's native object, liveness and release answer the value, a release
    /// against a port of another kind is refused by name, and the three things that are no handle of this module —
    /// the default value, a spent handle and a document missing a component — are not live and read as no handle.</summary>
    private static int Handles()
    {
        var scenario = new Scenario();
        var module = scenario.Register(Id);
        var port = new HandlePort("request", "session");
        var handle = module.CastRequestHandle("session");
        var wire = handle.ToJson();
        var expected = "{\"worldEpoch\":" + handle.WorldEpoch + ",\"lifeEpoch\":" + handle.LifeEpoch + ",\"local\":" + handle.Local + ",\"provider\":" + handle.Provider + "}";
        if (!handle.IsCast || wire.GetRawText() != expected || !RuntimeHandle.TryRead(wire, out var read) || read != handle || read.GetHashCode() != handle.GetHashCode()
            || handle.InWorld(handle.WorldEpoch + 1) == handle || handle.InWorld(handle.WorldEpoch + 1).InWorld(handle.WorldEpoch) != handle)
            throw new Exception("FAIL: a cast handle does not write and read back its wire form as the same value: " + wire.GetRawText());
        var native = new object();
        module.RegisterNative(handle, native, _ => true);
        if (!module.IsLive(handle) || !module.TryNative(handle, out var byValue) || !ReferenceEquals(byValue, native))
            throw new Exception("FAIL: a live handle does not answer its native object by its value");
        if (module.IsLive(default) || RuntimeHandle.TryRead(RuntimeJson.From(new { worldEpoch = handle.WorldEpoch, lifeEpoch = handle.LifeEpoch, local = handle.Local }), out _)
            || RuntimeHandle.TryRead(RuntimeJson.From(new { worldEpoch = 0.5, lifeEpoch = 1, local = 0, provider = 0 }), out _))
            throw new Exception("FAIL: the default value was live, or a document that is no handle was read as one");
        var refused = "";
        try { module.ReleaseEndedHandle(handle, new HandlePort("effect", "session"), "typed-handle"); }
        catch (RuntimeContractException error) { refused = error.Code; }
        if (refused != RuntimeAbiCodes.HandleKind || !module.IsLive(handle))
            throw new Exception("FAIL: a release against a port of another kind was not refused by name: " + refused);
        if (!module.ReleaseEndedHandle(handle, port, "typed-handle") || module.IsLive(handle) || module.TryNative(handle, out _))
            throw new Exception("FAIL: a released handle is still live");
        return 4;
    }

    /// <summary>A recipient read of a presentation step (the read a retained presentation is replayed through) hands
    /// the step's handler a context with the row region a recipient command has: one row per recipient, written and
    /// concluded as usual. A step whose recipients are real but have no screen (the session resolver answers an empty
    /// list, as the player directory does for bots) is sent to nobody and finishes succeeded with
    /// `presentation-no-viewer`, never as a refusal and never as `no-targets`.</summary>
    private static int RecipientRead()
    {
        TickResult Walk(IReadOnlyList<string> sessions, string eventId)
        {
            var scenario = new Scenario();
            var module = Fixture.Module(Id, _ => CommandResult.Succeeded(RuntimeJson.EmptyObject));
            var seed = System.Text.Json.Nodes.JsonNode.Parse(module.Declaration.Registry.GetRawText())!;
            seed["capabilities"]!.AsArray()[1]!["graph"]!["execution"] = "presentation";
            var handle = scenario.Kernel.RegisterModule(ForgeEngine.Testing.TestProviders.Change(module, registry: seed.ToJsonString()) with
            {
                EntityResolvers = scenario.Resolvers(Id),
                PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>> { [Id] = _ => sessions }
            }, RuntimeLogLevel.Off);
            // The compiled plan is the fixture's own: where a step runs is the registered block's fact, not the plan's.
            var compiled = new Scenario();
            compiled.Register(Id);
            scenario.Kernel.LoadPlan(Fixture.Plan(compiled.Kernel, "rows", Id));
            var tick = scenario.Kernel.CurrentTick + 1;
            scenario.Raise(handle, eventId, Id, tick);
            var advanced = scenario.Kernel.Advance(tick, true);
            if (sessions.Count == 0) return advanced;
            var request = advanced.Presentations.Single();
            var count = -1; CommandResult? concluded = null;
            scenario.Kernel.WithRecipientContext(request, context =>
            {
                var rows = context.Rows;
                count = rows.Count;
                rows.Begin(0).Number(Amount, 5).Integer(TargetCount, 1).End(Status.Partial, Commit.None, ResultCode.Partial);
                concluded = rows.ConcludePresented();
            });
            if (count != 1 || concluded == null || concluded.Status != CommandStatuses.Partial || concluded.CommitState != CommitStates.None)
                throw new Exception($"FAIL: a recipient read hands its handler {count} rows and concludes {concluded?.Status}/{concluded?.CommitState}/{concluded?.Code}");
            return advanced;
        }

        Walk(new[] { "2" }, "typed-recipient-read");
        var unseen = Walk(Array.Empty<string>(), "typed-recipient-no-viewer");
        var result = unseen.Commands.Single().Result;
        if (unseen.Presentations.Count != 0 || result.Status != CommandStatuses.Succeeded || result.Code != RuntimeAbiCodes.PresentationNoViewer)
            throw new Exception($"FAIL: a step addressed only to players without a screen sent {unseen.Presentations.Count} requests and finished {result.Status}/{result.Code}, expected none and succeeded/{RuntimeAbiCodes.PresentationNoViewer}");
        return 2;
    }

    /// <summary>A client's own early copy of a presentation step. A kernel that is not the host admits the event its
    /// own player reported, claims the entry and runs the step outside any advance, once; the host's order for the
    /// same token, plan and step is answered with the kept result and closes it, so the step is not run again. A step
    /// addressed to another player is not predicted, a prediction the host never orders is closed after the confirm
    /// time, the open set is bounded, and the host itself predicts nothing.</summary>
    private static int Prediction()
    {
        // `hostOnly` gives the trigger one more required port (`equipment`) that this machine never writes: a value only
        // the host can name. Its plan is the same graph compiled against that declaration (framework-prediction/host-only).
        (Scenario Scenario, Func<long, int, int> Predict, Func<int> Shown, RuntimeModuleHandle Handle) Machine(bool host, Func<long> clock,
            bool hostOnly = false)
        {
            var scenario = new Scenario();
            var shown = 0;
            var module = Fixture.Module(Id, _ =>
            {
                shown++;
                return CommandResult.Partial(RuntimeJson.EmptyObject, CommitStates.None, Array.Empty<RuntimeFact>());
            });
            var seed = System.Text.Json.Nodes.JsonNode.Parse(module.Declaration.Registry.GetRawText())!;
            seed["capabilities"]!.AsArray()[1]!["graph"]!["execution"] = "presentation";
            if (hostOnly)
                seed["capabilities"]!.AsArray()[0]!["graph"]!["outputs"]!.AsArray().Add(
                    System.Text.Json.Nodes.JsonNode.Parse("""{"id":"equipment","type":"entity"}"""));
            var handle = scenario.Kernel.RegisterModule(ForgeEngine.Testing.TestProviders.Change(module, registry: seed.ToJsonString()) with
            {
                EntityResolvers = scenario.Resolvers(Id),
                PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>
                {
                    [Id] = recipients => recipients?.Select(each => each.Id == Id + ":1" ? "me" : "other").ToArray()
                }
            }, RuntimeLogLevel.Off);
            var compiled = new Scenario();
            compiled.Register(Id);
            scenario.Kernel.LoadPlan(hostOnly ? ForgeEngine.Testing.TestRuntime.ReadPlan("framework-prediction", "host-only") : Fixture.Plan(compiled.Kernel, "rows", Id));
            scenario.Kernel.LocalSession = static () => "me";
            scenario.Kernel.PredictionClock = clock;
            scenario.Kernel.Advance(0, host);
            int Predict(long token, int player)
            {
                var gate = handle.SubscriptionGate(Fixture.Trigger(Id));
                var raised = handle.BeginEvent(gate);
                // Player 0 writes no target: the one value the fixture's step reads is then unknown on this machine.
                if (player != 0) raised.Values.Entity(Array.IndexOf(gate.Binding!.OutputIds, "target"), Recipient(scenario, player));
                return raised.Predict(scenario.World, 0, "shared-scope", token);
            }
            return (scenario, Predict, () => shown, handle);
        }

        var now = 0L;
        var client = Machine(false, () => now);
        var kernel = client.Scenario.Kernel;
        if (client.Predict(8, 1) != 1 || client.Shown() != 1)
            throw new Exception($"FAIL: a client did not play its own reported event's presentation step at once (shown {client.Shown()})");
        var kept = kernel.TakePrediction(8, "rows", 0);
        if (kept == null || kernel.TakePrediction(8, "rows", 0) != null || client.Shown() != 1)
            throw new Exception("FAIL: the host's order for a predicted step was not answered from the kept result exactly once");
        if (kernel.TakePrediction(9, "rows", 0) != null || client.Predict(16, 2) != 0 || client.Shown() != 1)
            throw new Exception("FAIL: another token was answered, or a step addressed to another player was predicted");
        if (client.Predict(24, 1) != 1) throw new Exception("FAIL: a second prediction was not played");
        now += RuntimeKernel.PredictionConfirmMilliseconds;
        kernel.Advance(1, false);
        if (kernel.TakePrediction(24, "rows", 0) != null)
            throw new Exception("FAIL: a prediction the host never ordered was still open after the confirm time");
        for (var token = 1; token <= RuntimeKernel.MaximumOpenPredictions; token++)
            if (client.Predict(1000 + token, 1) != 1) throw new Exception("FAIL: an open prediction inside the bound was not played");
        var before = client.Shown();
        if (client.Predict(5000, 1) != 0 || client.Shown() != before)
            throw new Exception("FAIL: a prediction past the bound was played");
        // A required value only the host can name (a shot's equipment) stays unwritten: a step that reads none of it is
        // played, a step whose own input is unwritten waits for the host.
        var partial = Machine(false, () => now, hostOnly: true);
        if (partial.Predict(8, 1) != 1 || partial.Shown() != 1)
            throw new Exception("FAIL: a step that reads nothing the client left unwritten was not predicted");
        if (partial.Predict(9, 0) != 0 || partial.Shown() != 1)
            throw new Exception("FAIL: a step whose input the client did not write was predicted");
        var host = Machine(true, () => now);
        if (host.Predict(8, 1) != 0 || host.Shown() != 0) throw new Exception("FAIL: the host predicted its own event");
        // The host's half: the fact it publishes for the reporter carries the reporter's token to that recipient's order.
        var reported = host.Handle.BeginEvent(host.Handle.SubscriptionGate(Fixture.Trigger(Id)));
        reported.Values.Entity(Array.IndexOf(host.Handle.SubscriptionGate(Fixture.Trigger(Id)).Binding!.OutputIds, "target"), Recipient(host.Scenario));
        reported.Raise("typed-prediction-host", host.Scenario.World, 1, "shared-scope", Recipient(host.Scenario), 8);
        var ordered = host.Scenario.Kernel.Advance(1, true).Presentations.Single();
        if (ordered.PredictedBy != "me" || ordered.PredictionToken != 8 || ordered.StepIndex != 0 || ordered.PlanId != "rows")
            throw new Exception($"FAIL: the host's order does not name the reporter's prediction: {ordered.PredictedBy}/{ordered.PredictionToken}");
        return 8;
    }

    /// <summary>The generated row constants are the runtime's columns: for every block that declares a result row,
    /// each <c>Row…</c> constant is the position of the column the descriptor compiles under that name, and no column
    /// is without one. A generator that drifted from the row layout fails here, not in a handler.</summary>
    private static int Constants()
    {
        var checks = 0;
        foreach (var block in typeof(Blocks).GetNestedTypes(BindingFlags.Public))
        {
            if (block.GetField("Id")?.GetRawConstantValue() is not string id) continue;
            var constants = block.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(int) && field.Name.StartsWith("Row", StringComparison.Ordinal))
                .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!);
            var graph = PrimitiveContracts.Graph(id);
            var named = RuntimeGraphContracts.ResultPortName(graph);
            if (named.Length == 0)
            {
                if (constants.Count != 0) throw new Exception("FAIL: " + id + " has row constants and declares no result row");
                continue;
            }
            var port = graph.GetProperty("outputs").EnumerateArray().Single(candidate => RuntimeJson.Text(candidate, "id") == named);
            var row = ResultRowAbi.Compile(port, graph, 0, 1);
            if (constants.Count != row.Columns.Length)
                throw new Exception($"FAIL: {id} has {constants.Count} row constants for {row.Columns.Length} columns");
            for (var column = 0; column < row.Columns.Length; column++)
            {
                var name = "Row" + string.Concat(row.Columns[column].Id.Split('.', '_', '-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
                if (!constants.TryGetValue(name, out var position) || position != column)
                    throw new Exception($"FAIL: {id} column {row.Columns[column].Id} is at {column} and its constant {name} says {(constants.ContainsKey(name) ? position : -1)}");
            }
            checks++;
        }
        if (checks == 0) throw new Exception("FAIL: no block's row constants were checked");
        return 1;
    }
}
