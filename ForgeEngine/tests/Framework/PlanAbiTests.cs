using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;
using ForgeEngine.Testing;

/// <summary>
/// Plan-ABI checks that need a registry of their own: the `query` tier, the seven first-step controls and their
/// continuations, handles, mount targets and the result-row declaration. Every case builds its plan the way the
/// compiler does — bindings pinned by index, dense port slots, successors in execution-output order — and asserts
/// both the positive path (what ran, in what order) and the refusal code when the shape is wrong.
/// </summary>
internal static class PlanAbiTests
{
    private static readonly HashSet<string> CompiledPlans = new(StringComparer.Ordinal)
    {
        "action-output", "action-output-delay", "branch-false", "branch-true", "cancel", "cancel-stale", "count", "delay", "delay-world",
        "emit-null.send", "enum-present-compare", "enum-switch", "enum-switch-absent", "enum-switch-event", "enum-switch-narrow", "for-each",
        "for-each-budget", "for-each-position", "for-each-position-repeat", "for-each-position-short",
        "for-each-position-stop", "interval", "interval-unbounded", "latch", "latch-other", "lifecycle", "mount-filter",
        "mount-held", "mount-level", "mount-level-elsewhere", "mount-mixed", "mount-mixed-world", "mount-ok", "own-guard",
        "partial-continues", "present-branch", "query-budget", "query-chain", "query-in-present", "read-required",
        "recycle.send", "repeat", "repeat-budget", "repeat-delay", "repeat-pulse-index", "restart", "reuse", "rows",
        "scope-open", "sequence", "sequence-delay", "stale", "step-budget", "stop-for-each", "stop-repeat",
        "stop-sequence", "wait-deferred.send", "wait-race.send", "wait-scope.send",
        "message-claim.send", "message-claim.ping", "message-claim.pong", "log-record",
        "group-join", "group-timeout-retry", "group-cancel"
    };
    private static readonly HashSet<string> CompletionPlans = new(StringComparer.Ordinal)
    {
        "emit-null.wait", "for-each-interval-delay", "for-each-interval-wait", "lifecycle.dep", "lifecycle.reload", "ramp", "ramp-exponent",
        "ramp-release", "ramp-stepped", "ramp-world", "ramp-zero-exponent", "recycle.wait", "scope-bound",
        "sequence-wait", "wait-after-unload", "wait-budget", "wait-consumer", "wait-deferred.after",
        "wait-deferred.wait", "wait-handle", "wait-race", "wait-race.after", "wait-scope-timeout",
        "wait-scope.after", "wait-scope.wait", "wait-unload", "wait-unregister"
    };
    private const string Id = "example.abi";
    private static readonly string[] NoPermissions = Array.Empty<string>();

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }

        // ObserveTrigger is a read-only consumer: it opens the provider's gate, sees admitted events whether or not a
        // plan consumes them, never changes dispatch, and a throwing callback is dropped without touching the publish.
        {
            var h = new Harness();
            var gate = h.Module.SubscriptionGate(h.TriggerBinding);
            var seen = new List<string>();
            Check(!gate.HasSubscribers, "no plan and no observer leaves the gate closed");
            RejectCode(() => h.Module.ObserveTrigger(h.TriggerBinding + ".unknown", _ => { }), "not-trigger", "an unknown binding cannot be observed");
            var subscription = h.Module.ObserveTrigger(h.TriggerBinding, view => seen.Add(view.EventId + ":" + view.Outputs.GetProperty("amount").GetDouble()));
            var unconsumed = h.Publish("observed-1");
            Check(gate.HasSubscribers && subscription.IsActive, "an observer counts as a consumer in the gate");
            Check(unconsumed is { Status: "ignored", Code: "no-consumer" } && seen.SequenceEqual(new[] { "observed-1:5" }),
                "an event no plan consumes is still observed, and the publisher still hears no-consumer");
            subscription.Dispose();
            h.Publish("observed-2");
            Check(!gate.HasSubscribers && !subscription.IsActive && seen.Count == 1, "a disposed observer closes the gate and sees nothing");
            h.Load(h.Plan("own-guard"));
            using var beside = h.Module.ObserveTrigger(h.TriggerBinding, view => seen.Add(view.EventId));
            using var faulty = h.Module.ObserveTrigger(h.TriggerBinding, _ => throw new InvalidOperationException("observer fault"));
            var dispatched = h.Publish("observed-3");
            h.Kernel.Advance(1, true);
            Check(dispatched.Status == "queued" && h.Applied.Count > 0 && seen.SequenceEqual(new[] { "observed-1:5", "observed-3" }),
                "an observer beside a mounted plan sees the event and leaves the dispatch alone");
            Check(!faulty.IsActive && beside.IsActive, "a throwing callback is removed and its neighbour is kept");
        }
        // Multiple installed copies of the same compiled mount share one executable plan.
        {
            var h = new Harness();
            var json = h.Plan("mount-level");
            var loaded = h.Kernel.LoadPlans(new[]
            {
                PlanCandidate.Loaded("first/forge/plans/mount-level.plan.forge", json),
                PlanCandidate.Loaded("second/forge/plans/mount-level.plan.forge", json)
            });
            Check(loaded.Count == 2 && loaded.All(row => row.Loaded), "identical installed mounts each report success");
            Check(h.Kernel.PlanIdentities.Count == 1, "identical installed mounts execute once");
        }
        // Deduplication is exact JSON equality within one discovery batch, not semantic normalization.
        // Both documents are individually valid; a trailing newline deliberately changes only the file text.
        {
            var h = new Harness();
            var json = h.Plan("mount-level");
            var loaded = h.Kernel.LoadPlans(new[]
            {
                PlanCandidate.Loaded("first/forge/plans/mount-level.plan.forge", json),
                PlanCandidate.Loaded("second/forge/plans/mount-level.plan.forge", json + "\n")
            });
            Check(loaded.Count == 2 && loaded.All(row => !row.Loaded && row.Code == "plan-conflict"),
                "same planId with different JSON text rejects every member even when only whitespace differs");
            Check(h.Kernel.PlanIdentities.Count == 0, "a conflicting duplicate group installs no plan");
        }
        // query: a world read feeds an action through the query step's own frame.
        {
            var h = new Harness();
            h.Load(h.Plan("query-chain"));
            h.Publish("q1");
            var tick = h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_apply:" + Entity(1) + ":1" }), "a query step's observation reaches the action that reads it [" + string.Join("|", h.Applied) + "]");
            Check(h.Queries == 1 && tick.Commands.All(c => c.Result.Status == "succeeded"), "the query step is evaluated once per activation");
        }
        // query: an exhausted read budget rejects the step instead of answering with a short set.
        {
            var h = new Harness();
            h.Load(h.Plan("query-budget"));
            h.Publish("query-budget", targets: Enumerable.Range(1, RuntimeKernel.MaximumEntityQueriesPerTick + 1).Select(Entity).ToArray());
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected"
                && tick.Commands[0].Result.Code == RuntimeAbiCodes.QueryBudget,
                "a query past the tick's read budget is refused as query-budget ["
                + string.Join("|", tick.Commands.Select(c => c.Result.Status + ":" + c.Result.Code)) + "]");
            Check(h.Applied.Count == 0, "the consumer of a refused query never sees a shorter candidate set");
        }
        // query: the tier itself is a registration fact, not a plan one.
        {
            RejectCode(() => Probe("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"entity\"}"),
                RuntimeAbiCodes.PureWorldPort, "a pure observation cannot declare an entity port");
            RejectCode(() => Probe("query", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"boolean\"}"),
                RuntimeAbiCodes.QueryAuthority, "a value-only observation cannot claim the query tier");
            RejectCode(() => Probe("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"execution\"}"),
                "pure-execution", "a pure observation carries no execution port");
            RejectCode(() => Probe("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"boolean\",\"actorRole\":\"instigator\"}"),
                "context-role-port", "only a single-entity port can declare an actor role");
            Probe("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"boolean\"}");
            Check(true, "a value-only observation registers as pure");
            Probe("query", "{\"id\":\"target\",\"type\":\"entity\"}", "{\"id\":\"value\",\"type\":\"entity\"}");
            Check(true, "an observation with a world port registers as query");
        }
        // candidates: the per-query ceiling is held against what a zone or a range leaves of a kind, not against
        // the whole level. A roster past the ceiling is refused whole; narrowed below it, it is answered complete;
        // narrowed to a set that is still past it, it is refused again, never cut to fit.
        {
            const string kind = "example.mob";
            const int total = RuntimeKernel.MaximumEntityCandidatesPerQuery + 44;
            var roster = Enumerable.Range(0, total).Select(i => new EntityReference(kind + ":" + i.ToString("D3"), 1, 1)).ToArray();
            var near = new EntityReference(EntityKinds.Zone + ":near", 1, 0);
            var far = new EntityReference(EntityKinds.Zone + ":far", 1, 0);
            static int Index(EntityReference reference) => int.Parse(reference.Id[^3..]);
            var kernel = new RuntimeKernel(Fixture.Identity);
            kernel.BeginWorld(1);
            kernel.RegisterModule(ProbeModule("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"boolean\"}") with
            {
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [kind] = _ => true },
                EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>> { [kind] = () => roster },
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { [kind] = _ => null },
                // Entity i stands i metres along x; the first forty stand in the near zone.
                EntityPoseObservers = new Dictionary<string, EntityPoseObserver>
                { [kind] = (EntityReference entity, out EntityPose pose) => { pose = new EntityPose(Index(entity), 0, 0, "alive"); return true; } },
                EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>> { [kind] = entity => Index(entity) < 40 ? near : far }
            }, RuntimeLogLevel.Off);
            kernel.StartRuntime(() => { });
            var whole = kernel.EnumerateEntityCandidates(kind);
            Check(whole.Status == "rejected" && whole.Code == "entity-query-budget" && whole.Items.Count == 0,
                "a whole-kind roster past the ceiling is refused, not truncated [" + whole.Status + ":" + whole.Code + "]");
            var zoned = kernel.EnumerateEntityCandidates(kind, new EntityCandidateScope { Zone = near });
            Check(zoned.Status == "complete" && zoned.Items.Count == 40 && zoned.Items.All(item => Index(item.Reference) < 40),
                "a zone narrows the roster before it is counted [" + zoned.Status + ":" + zoned.Code + ":" + zoned.Items.Count + "]");
            var ranged = kernel.EnumerateEntityCandidates(kind, new EntityCandidateScope { Within = true, X = 100, Radius = 10 });
            Check(ranged.Status == "complete" && ranged.Items.Select(item => Index(item.Reference)).SequenceEqual(Enumerable.Range(90, 21)),
                "a range narrows the roster before it is counted, in the roster's own order [" + ranged.Status + ":" + ranged.Code + ":" + ranged.Items.Count + "]");
            var both = kernel.EnumerateEntityCandidates(kind, new EntityCandidateScope { Zone = near, Within = true, X = 0, Radius = 9.5 });
            Check(both.Status == "complete" && both.Items.Count == 10, "a zone and a range together keep what is inside both");
            var wide = kernel.EnumerateEntityCandidates(kind, new EntityCandidateScope { Zone = far });
            Check(wide.Status == "rejected" && wide.Code == "entity-query-budget" && wide.Items.Count == 0,
                "a narrowed set still past the ceiling is refused whole [" + wide.Status + ":" + wide.Code + ":" + wide.Requested + "]");
        }
        // query: a plan cannot run an observation outside its tier.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("wrong-tier")), "node-domain-authority", "a plan step's nodeKind must be the capability's own tier");
        }
        // The plan records the digest of the port layout its blocks resolved to on the website; this runtime derives
        // the same layout from its own registrations and refuses the whole plan when the two differ.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("layout-hash")), "layout-mismatch", "a plan whose layout hash is not the one this runtime derives is refused whole");
            Check(h.Kernel.PlanIdentities.Count == 0, "a plan refused for its layout hash installs nothing");
        }
        // action: a declared output is a value a later step reads, the same way a control's is. The action's own
        // result row stays a row: it is readable only by a port that declares the same result schema, so wiring it
        // into an entity input is a port mismatch rather than a row carried as a value.
        {
            var h = new Harness();
            h.Load(h.Plan("action-output"));
            h.Publish("action-output");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S0_publish:" + Entity(1) + ":-", "S1_read:" + Entity(1) + ":7" }),
                "an action's declared output reaches the step that reads it [" + string.Join("|", h.Applied) + "]");
        }
        // action: the declared output survives a `delay` between the producer and its reader, the way the effect
        // handle an equipped trigger starts is still there for the `effect_cancel` that follows the wait.
        {
            var h = new Harness();
            h.Load(h.Plan("action-output-delay"));
            h.Publish("action-output-delay");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S0_publish:" + Entity(1) + ":-" }),
                "the producer runs before the delay [" + string.Join("|", h.Applied) + "]");
            var tick = h.Kernel.Advance(3, true);
            Check(h.Applied.SequenceEqual(new[] { "S0_publish:" + Entity(1) + ":-", "S1_read:" + Entity(1) + ":7" }),
                "an action's declared output reaches a reader after a delay [" + string.Join("|", h.Applied) + "]");
            Check(tick.Events.All(e => e.Status != "rejected"),
                "the reader after the delay is not refused from-step-slot [" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)) + "]");
        }
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("reads-action-row")), "port-mismatch", "an action's result row is not a value a later step reads");
        }
        // A target filter's `kind` narrows what it forwards, as the website's type check does: a kill's player-or-enemy
        // target filtered to enemies feeds an enemy-only input, and the same wire through an `any` filter still does not.
        {
            var wide = RuntimeJson.Parse("""{ "id": "target", "type": "entity", "entityKinds": ["gtfo.player", "gtfo.enemy"] }""");
            var enemyOnly = RuntimeJson.Rows(PrimitiveContracts.Graph(Blocks.QueryEnemyType.Id), "inputs").Single(p => RuntimeJson.Text(p, "id") == "enemy");
            JsonElement Targets(string kind)
            {
                var parameters = RuntimeJson.From(new { kind, empty = "emit-empty" });
                var filter = RuntimeGraphContracts.Resolve(PrimitiveContracts.Graph(Blocks.SelectorTargetFilter.Id), parameters);
                var flowed = RuntimeEntityKindFlow.Resolve(filter, _ => wide, RuntimeEntityKindFlow.FilterKind(Blocks.SelectorTargetFilter.Id, parameters));
                return RuntimeJson.Rows(flowed, "outputs").Single(p => RuntimeJson.Text(p, "id") == "targets");
            }
            Check(RuntimeGraphContracts.AssignableValue(Targets("enemy"), enemyOnly), "an enemy filter's targets feed an enemy-only input");
            Check(!RuntimeGraphContracts.AssignableValue(Targets("any"), enemyOnly), "an any filter forwards its candidates' kinds, which an enemy-only input refuses");
        }

        // present: one value, two ways out, and the value itself is a value only where the step proved it is there.
        {
            var h = new Harness();
            h.Load(h.Plan("present-branch"));
            h.Publish("present-missing");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S2_missing:" + Entity(1) + ":-" }),
                "a value that is not there leaves through missing [" + string.Join("|", h.Applied) + "]");
            h.Applied.Clear();
            h.Publish("present-value", 2, null, maybe: 4);
            h.Kernel.Advance(2, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_guarded:" + Entity(1) + ":4" }),
                "the present branch reads the value the step tested [" + string.Join("|", h.Applied) + "]");
        }
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("guarded-outside")), "present-branch", "a guarded value is refused outside the branch that proved it is there");
        }
        // A nullable enum remains a wire index between control steps. Two simultaneous invocations carry different
        // equipment_kind members through present -> delay -> present without sharing a continuation snapshot; the
        // compare evaluator receives member text only at its handler boundary.
        {
            var h = new Harness(triggerOperators: true, equipmentEnum: true);
            h.Load(h.Plan("enum-present-compare"));
            Check(h.Publish("enum-present-sentry", maybeEnum: 0).Status == "queued"
                && h.Publish("enum-present-mine", maybeEnum: 1).Status == "queued",
                "valid equipment_kind indices enter the enum-present plan");
            var suspended = h.Kernel.Advance(1, true);
            Check(suspended.Events.All(row => row.Status != "rejected") && h.Applied.Count == 0,
                "both enum invocations suspend at the delay without losing their values");
            var resumed = h.Kernel.Advance(2, true);
            var routed = h.Applied.ToHashSet(StringComparer.Ordinal);
            Check(resumed.Events.All(row => row.Status != "rejected")
                && routed.SetEquals(new[] { "S_sentry:" + Entity(1) + ":-", "S_mine:" + Entity(1) + ":-" }),
                "sentry_gun compares eq false/ne true while mine compares eq true/ne false after two presence guards ["
                    + string.Join("|", h.Applied) + "] events="
                    + string.Join("|", resumed.Events.Select(row => row.Status + ":" + row.Code)) + " commands="
                    + string.Join("|", resumed.Commands.Select(row => row.NodeId + ":" + row.Result.Status + ":" + row.Result.Code)));
            h.Applied.Clear();
            Check(h.Publish("enum-present-null", tick: 3, maybeEnum: null).Status == "queued",
                "an explicit nullable equipment_kind enters the presence guard");
            var missing = h.Kernel.Advance(3, true);
            Check(missing.Events.All(row => row.Status != "rejected")
                && h.Applied.SequenceEqual(new[] { "S_missing_first:" + Entity(1) + ":-" }),
                "a null equipment_kind exits through missing before the delay [" + string.Join("|", h.Applied) + "]");
            var invalid = h.Publish("enum-present-invalid", tick: 4, maybeEnum: 3);
            Check(invalid.Status == "rejected" && invalid.Code == "invalid-enum",
                "an out-of-range equipment_kind wire index is rejected at publication");
        }
        {
            var h = new Harness(triggerOperators: true, equipmentEnum: true);
            h.Load(h.Plan("enum-present-compare"));
            h.Publish("enum-present-migration-sentry", maybeEnum: 0);
            h.Publish("enum-present-migration-mine", maybeEnum: 1);
            h.Kernel.Advance(1, true);
            var snapshot = h.Kernel.CaptureHostMigration();
            h.Kernel.BeginHostMigration();
            var corrupted = JsonNode.Parse(snapshot)!;
            var storedEnum = corrupted["schedules"]!.AsArray()[0]!["resume"]!["flow"]!["storedValues"]!.AsArray()[0]!["ports"]!
                .AsArray().Single(cell => cell!["port"]!.GetValue<int>() == 2)!;
            storedEnum["value"] = 999;
            RejectCode(() => h.Kernel.RestoreHostMigration(corrupted.ToJsonString(), 2, true), "invalid-enum",
                "migration rejects a stored enum wire index outside the loaded output contract");
            Check(h.Kernel.WorldEpoch == 1 && h.Kernel.CaptureHostMigration() == snapshot,
                "a rejected stored enum snapshot switches neither the world nor its pending continuations");
            h.Kernel.RestoreHostMigration(snapshot, 2, true);
            h.World = 2;
            var resumed = h.Kernel.Advance(2, true);
            var routed = h.Applied.ToHashSet(StringComparer.Ordinal);
            Check(resumed.Events.All(row => row.Status != "rejected")
                && routed.SetEquals(new[] { "S_sentry:" + Entity(1) + ":-", "S_mine:" + Entity(1) + ":-" }),
                "a migrated delay retains each invocation's enum wire snapshot [" + string.Join("|", h.Applied)
                    + "] events=" + string.Join("|", resumed.Events.Select(row => row.Status + ":" + row.Code)));
        }
        // Ruling 158.2: the region is dominance, not forward reachability. Two paths that meet again after the
        // guard leave a way in that never tested the value, so the step they meet at may not read it.
        {
            var h = new Harness();

        }
        // A query step is evaluated where a step reads it, so it sits in a `present` region when every step reading it
        // does: it may then read the guarded value, and a single guarded entity fills its set input. One reader
        // outside the region puts it outside too.
        {
            var h = new Harness();
            var guardEntity = h.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = 4 }));
            h.Load(h.Plan("query-in-present"));
            h.Publish("query-in-present");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S2_guarded:" + Entity(1) + ":1" }),
                "a query read only inside the present region reads the guarded value [" + string.Join("|", h.Applied) + "]");
            // A reference whose life ended before the step is not there: `present` answers `missing` instead of
            // cancelling the entry, and nothing in its region reads the gone value.
            var gone = new Harness();
            var traces = new List<RuntimeBehaviorTraceRecord>();
            using (gone.Kernel.ObserveBehaviorTrace(traces.Add))
            {
                gone.Load(gone.Plan("query-in-present"));
                gone.Publish("query-in-present-gone");
                gone.IsLive = _ => false;
                gone.Kernel.Advance(1, true);
            }
            var after = traces.Where(row => row.Scope == "behavior-node" && row.Phase == "after").ToArray();
            Check(after.Any(row => row.Step == "S0_present" && row.Result is { Status: CommandStatuses.Succeeded, Reason: "routed" })
                && after.Any(row => row.Step == "S3_missing") && after.All(row => row.Step != "S2_guarded"),
                "a gone reference leaves present through missing [" + string.Join("|", after.Select(row => row.Step + ":" + row.Result?.Status + ":" + row.Result?.Reason)) + "]");
            var outside = new Harness();
            var outsideGuard = outside.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = 4 }));

        }
        // Inside a `present` region only the step's own forwarded value is there: the event port it tested, read
        // straight into a required input beside the guarded read, is still a possibly missing value.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("tested-source-direct")), "nullable-port", "the tested event port read directly inside the present region is refused");
            var unguarded = new Harness(optionalInstigator: true);
            RejectCode(() => unguarded.Load(unguarded.InvalidPlan("omitted-actor-as-instigator")), "optional-event-port", "an event port the event may omit cannot stand in for a wired instigator");
        }
        // Each when-present step's region proves only the value that step tested: a second guard on the first one's
        // `missing` exit has a region of its own, where its own value is read and the first one's is refused.
        {
            var own = new Harness();
            own.Load(own.Plan("own-guard"));
            own.Publish("own-guard");
            own.Kernel.Advance(1, true);
            Check(own.Applied.SequenceEqual(new[] { "S4_second_missing:" + Entity(1) + ":-" }),
                "a second guard's region reads its own value [" + string.Join("|", own.Applied) + "]");
            var other = new Harness();

        }
        // Ruling 158.4: a handle member spells both halves of a handle's contract, so a guarded wave handle
        // resolves to the same `effect`/`encounter` pair the action that stops the wave declares — and the pair is
        // the kind's own, never one the member leaves open (rule 142.1).
        {
            var h = new Harness();
            var guardedHandle = h.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = "handle:effect" }));
            var tested = RuntimeJson.Rows(guardedHandle, "inputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            var forwarded = RuntimeJson.Rows(guardedHandle, "outputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            Check(RuntimeJson.Text(tested, "type") == "handle" && RuntimeJson.Text(tested, "handleKind") == "effect"
                && RuntimeJson.Text(tested, "lifetime") == "encounter" && RuntimeJson.Flag(tested, "nullable"),
                "a guarded handle member carries its kind, its lifetime and the fact it may be absent");
            Check(RuntimeGraphContracts.ValueTypeMatches(tested, forwarded) && RuntimeJson.Flag(forwarded, "nullable"),
                "the forwarded handle is the value that was tested");
            Check(RuntimeGraphContracts.IsValueTypeMember("handle:effect") && !RuntimeGraphContracts.IsValueTypeMember("handle:audio")
                && !RuntimeGraphContracts.IsValueTypeMember("audio"),
                "only the handle kinds a value may be missing in are guard members");
        }
        // A Zone is a resource, not an entity cast. Both wire spellings of the structural member
        // resolve the existing guard to the same complete frame type without changing earlier indices.
        {
            var h = new Harness();
            var guardedZone = h.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = "resource:zone" }));
            var tested = RuntimeJson.Rows(guardedZone, "inputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            var forwarded = RuntimeJson.Rows(guardedZone, "outputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            Check(RuntimeJson.Text(tested, "type") == "resource" && RuntimeJson.Text(tested, "resourceKind") == "zone"
                && RuntimeJson.Text(tested, "schema") == "forge.resource.zone" && RuntimeJson.Flag(tested, "nullable"),
                "the Zone guard carries the resource kind, schema and missing-value contract");
            Check(RuntimeGraphContracts.ValueTypeMatches(tested, forwarded) && RuntimeJson.Flag(forwarded, "nullable")
                && !forwarded.TryGetProperty("entityKindsFrom", out _),
                "the guarded resource is forwarded without entity-kind metadata or a nullability cast");
            var frame = RuntimeGraphContracts.FramePort(tested);
            Check(frame.Kind == ValueKind.Resource && frame.Width == RuntimeFrames.ResourceWidth,
                "the guarded resource uses the normal resource frame layout");
            var parameter = RuntimeJson.Rows(guardedZone, "parameters").Single(p => RuntimeJson.Text(p, "id") == "value_type");
            var members = RuntimeJson.Strings(parameter.GetProperty("values"));
            Check(members.SequenceEqual(new[] { "number", "integer", "boolean", "string", "entity", "vector3",
                "handle:timer", "handle:subscription", "handle:effect", "handle:sound", "resource:zone", "handle:effect:entity_life", "enum",
                "resource:enemy", "resource:weapon", "resource:tool", "resource:chained-puzzle" }),
                "named enum guards and chosen-resource guards are appended without changing Zone, effect lifetimes or earlier member indices");
            var enemyGuard = h.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = "resource:enemy" }));
            var enemyTested = RuntimeJson.Rows(enemyGuard, "inputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            Check(RuntimeJson.Text(enemyTested, "type") == "resource" && RuntimeJson.Text(enemyTested, "resourceKind") == "enemy"
                && RuntimeJson.Text(enemyTested, "schema") == "forge.resource.enemy",
                "the chosen-enemy guard carries the enemy resource kind and schema so it wires straight into enemy.spawn");
            var enumGuard = h.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = "enum", enum_set = "damage_kind" }));
            var enumInput = RuntimeJson.Rows(enumGuard, "inputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            var enumOutput = RuntimeJson.Rows(enumGuard, "outputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            Check(RuntimeJson.Text(enumInput, "schema") == "damage_kind" && RuntimeGraphContracts.ValueTypeMatches(enumInput, enumOutput),
                "the presence guard preserves the named enum set on both sides");
            var indexed = h.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = 10 }));
            var indexedValue = RuntimeJson.Rows(indexed, "inputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            Check(RuntimeGraphContracts.ValueTypeMatches(tested, indexedValue),
                "the compiled enum index and authored Zone member name resolve identically");
            foreach (var member in new[] { "zone" })
                RejectCode(() => h.Contract(Blocks.ControlFlowPresent.Id, RuntimeJson.From(new { value_type = member })),
                    "port-type", "the Zone guard refuses an unoffered or untyped resource member: " + member);
        }
        // branch: one execution output per outcome, in declaration order.
        foreach (var condition in new[] { true })
        {
            var h = new Harness();
            h.Load(h.Plan("branch-" + condition));
            h.Publish("branch");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { condition ? "S1_then:" + Entity(1) + ":-" : "S2_else:" + Entity(1) + ":-" }), "branch follows the condition's own output");
        }

        // delay: a new world drops every continuation it held.
        {
            var h = new Harness();
            h.Load(h.Plan("delay-world"));
            h.Publish("delay-world");
            h.Kernel.Advance(1, true);
            h.Kernel.BeginWorld(2); h.World = 2;
            h.Kernel.Advance(3, true);
            Check(h.Applied.Count == 0, "BeginWorld clears every continuation");
            // A world must advance its epoch; only a restarted count takes a lower one, and only once.
            RejectCode(() => h.Kernel.BeginWorld(1), "world-epoch", "a lower epoch is refused while the count was not restarted");
            h.Kernel.RestartWorldCount(); h.Kernel.BeginWorld(1); h.World = 1;
            Check(h.Kernel.WorldEpoch == 1, "a restarted count accepts a lower epoch");
            h.Kernel.BeginWorld(3); h.World = 3;
            RejectCode(() => h.Kernel.BeginWorld(2), "world-epoch", "the restart is spent by the world it began");
            // A client that built its level before it heard its host's number keeps the world and takes that number.
            h.Publish("delay-world", tick: 4);
            h.Kernel.Advance(4, true);
            RejectCode(() => h.Kernel.FollowWorld(2), "world-epoch", "a kept world follows no lower epoch while the count was not restarted");
            h.Kernel.RestartWorldCount(); h.Kernel.FollowWorld(2); h.World = 2;
            Check(h.Kernel.WorldEpoch == 2 && !h.Kernel.HostMigrationPaused, "a kept world follows its host's lower epoch after a restart");
            RejectCode(() => h.Kernel.BeginWorld(1), "world-epoch", "the restart is spent by the world that followed");
        }
        // repeat with an interval: next runs now, every pulse re-enters the body region, and the count ends the schedule.
        {
            var h = new Harness();
            h.Load(h.Plan("interval"));
            h.Publish("interval");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_next:" + Entity(1) + ":-", "S3_pulse:" + Entity(1) + ":1" }) && h.Queries == 1,
                "a repeat with an interval enters next immediately and runs the first pulse in the same tick");
            h.Kernel.Advance(2, true);
            Check(h.Queries == 2 && h.Applied.Count == 3, "every pulse re-evaluates the query step");
            h.Kernel.Advance(3, true);
            Check(h.Applied.Count == 3 && h.Kernel.Advance(9, true).CommandsExecuted == 0, "a repeat with an interval stops after its declared pulse count");
        }

        // ramp: the envelope republishes its own value on every pulse, so the region after the step runs once per
        // tick of the envelope; the last ramp pulse is exactly `to`.
        {
            var h = new Harness();
            h.Load(h.Plan("ramp"));
            h.Publish("ramp");
            h.Kernel.Advance(1, true); h.Kernel.Advance(2, true); h.Kernel.Advance(3, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_next:" + Entity(1) + ":-=1", "S1_next:" + Entity(1) + ":-=2", "S1_next:" + Entity(1) + ":-=3" }),
                "a ramp publishes the value its envelope holds on every tick [" + string.Join("|", h.Applied) + "]");
            Check(h.Kernel.Advance(9, true).CommandsExecuted == 0, "a ramp stops when its whole envelope is spent");
        }

        // ramp: the two refusals — a leg past the iteration ceiling, and a missing input or a curve that is not one.
        {
            var h = new Harness();
            // The website refuses this sum before export, so the plan is the legal ramp with its legs changed here.
            h.Load(h.InvalidPlan("ramp-budget"));
            h.Publish("ramp-budget");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.IterationBudget),
                "a ramp past the iteration ceiling is refused by name [" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)) + "]");
            Check(h.Applied.Count == 0, "a refused ramp runs none of its pulses");
        }
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("ramp-missing-input")), "missing-input", "a ramp with an unwired input is refused at load rather than run on a default");
        }
        {
            var h = new Harness();
            h.Load(h.Plan("ramp-zero-exponent"));
            h.Publish("ramp-zero-exponent");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == "ramp-exponent"),
                "a ramp exponent that is not a curve is refused [" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)) + "]");
        }

        // repeat: the round ceiling is a budget, not a truncation.
        {
            var h = new Harness(new RuntimeLimits { MaxControlIterations = 2 });
            h.Load(h.Plan("repeat-budget"));
            h.Publish("repeat-budget");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.IterationBudget),
                "a loop past its round ceiling is refused by name");
            Check(h.Applied.Count == 0, "a loop past its round ceiling is refused whole, not truncated to the ceiling");
        }

        // for_each: a candidate set larger than the declared budget is refused, never truncated.
        {
            var h = new Harness();
            h.Load(h.Plan("for-each-budget"));
            h.Publish("for-each-budget", targets: new[] { Entity(1), Entity(2) });
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.IterationBudget),
                "a candidate set above its budget is refused");
            Check(h.Applied.Count == 0, "no round runs off a partially read candidate set");
        }
        // for_each into a presentation step: each round's command names its play by the round's candidate, not by its
        // place in the list, so when a target leaves mid-way the targets that remain keep their plays (a held effect
        // keeps following the same enemy). The play's tick is the tick the step ran on.
        {
            var h = new Harness(presentation: true);
            h.Load(h.Plan("for-each"));
            Dictionary<string, (long Play, long Tick)> Plays(long tick, params int[] targets)
            {
                h.Publish("held-" + tick, tick, targets: targets.Select(Entity).ToArray());
                return h.Kernel.Advance(tick, true).Presentations.Where(sent => sent.NodeId == "S3_body").ToDictionary(
                    sent => sent.Inputs.GetProperty("target").GetProperty("id").GetString()!,
                    sent => (sent.Inputs.GetProperty("$play")[0].GetInt64(), sent.Inputs.GetProperty("$play")[1].GetInt64()));
            }
            var before = Plays(1, 1, 2, 3);
            var after = Plays(2, 1, 3);
            Check(before.Count == 3 && after.Count == 2 && before.Values.Select(play => play.Play).Distinct().Count() == 3,
                "every round of the walk is a play of its own [" + string.Join("|", before.Select(pair => pair.Key + "=" + pair.Value.Play)) + "]");
            Check(after[Entity(1)].Play == before[Entity(1)].Play && after[Entity(3)].Play == before[Entity(3)].Play,
                "a target leaving the list mid-way leaves the remaining targets' plays unchanged ["
                + string.Join("|", after.Select(pair => pair.Key + "=" + pair.Value.Play)) + "]");
            Check(before.Values.All(play => play.Tick == 1) && after.Values.All(play => play.Tick == 2),
                "each play carries the tick its step ran on");
        }
        // An interval series is one play per series: the same trigger fired twice before the first series ended runs two
        // series side by side, each pulse of a series names that series' play, and the two series never share one.
        {
            var h = new Harness(presentation: true);
            h.Load(h.Plan("interval"));
            h.Publish("series-a");
            h.Publish("series-b");
            long[] Pulses(long tick) => h.Kernel.Advance(tick, true).Presentations.Where(sent => sent.NodeId == "S3_pulse")
                .Select(sent => sent.Inputs.GetProperty("$play")[0].GetInt64()).ToArray();
            var first = Pulses(1);
            var second = Pulses(2);
            Check(first.Length == 2 && first.Distinct().Count() == 2,
                "two interleaved series of one trigger are two plays [" + string.Join("|", first) + "]");
            Check(second.Length == 2 && second.OrderBy(play => play).SequenceEqual(first.OrderBy(play => play)),
                "every pulse of a series names that series' play [" + string.Join("|", second) + "]");
        }
        // Outside every loop a play is about the trigger's subject: two events in one scope about two different enemies
        // are two plays, and a step that holds no plays carries no `$play` at all.
        {
            var h = new Harness(presentation: true);
            h.Load(h.Plan("sequence"));
            h.Publish("subject-1", targets: new[] { Entity(1) });
            h.Publish("subject-2", targets: new[] { Entity(2) });
            var sent = h.Kernel.Advance(1, true).Presentations;
            var bySubject = sent.GroupBy(command => command.NodeId).ToArray();
            Check(bySubject.Length > 0 && bySubject.All(step => step.Count() == 2 && step
                    .Select(command => command.Inputs.GetProperty("$play")[0].GetInt64()).Distinct().Count() == 2),
                "two subjects in one scope are two plays");
            var plain = new Harness(presentation: true, plays: false);
            plain.Load(plain.Plan("sequence"));
            plain.Publish("subject-1", targets: new[] { Entity(1) });
            var unkeyed = plain.Kernel.Advance(1, true).Presentations;
            Check(unkeyed.Count > 0 && unkeyed.All(command => !command.Inputs.TryGetProperty("$play", out _)),
                "a step whose handler keeps nothing per play is sent without a play");
            // A trigger that names no subject says nothing about whom its event is about, so each event is a play of its
            // own; with a subject, two events about the same enemy are that enemy's one play.
            long[] Twice(Harness harness)
            {
                harness.Load(harness.Plan("sequence"));
                harness.Publish("same-1", targets: new[] { Entity(1) });
                harness.Publish("same-2", targets: new[] { Entity(1) });
                var node = sent[0].NodeId;
                return harness.Kernel.Advance(1, true).Presentations.Where(command => command.NodeId == node)
                    .Select(command => command.Inputs.GetProperty("$play")[0].GetInt64()).ToArray();
            }
            var subjectless = Twice(new Harness(presentation: true, subject: false));
            var subjected = Twice(new Harness(presentation: true));
            Check(subjectless.Length == 2 && subjectless[0] != subjectless[1],
                "two events of a trigger with no subject are two plays [" + string.Join("|", subjectless) + "]");
            Check(subjected.Length == 2 && subjected[0] == subjected[1],
                "two events about one subject are one play [" + string.Join("|", subjected) + "]");
        }
        // An interval series inside a for_each: every pulse runs inside the round that started it, and a step after a
        // suspension in the pulse (a delay, a g-wait's timeout) still holds that round — its target is the round's
        // candidate and its play is the play the same round names on every pulse, across a host migration too.
        foreach (var variant in new[] { "for-each-interval-delay", "for-each-interval-wait" })
        {
            (string Target, long Play)[] Run(bool migrate)
            {
                var h = new Harness(presentation: true);
                h.Load(h.Plan(variant));
                h.Publish(variant, targets: new[] { Entity(1), Entity(2) });
                var seen = new List<(string, long)>();
                for (long tick = 1; tick <= 6; tick++)
                {
                    if (migrate && tick == 2)
                    {
                        var image = h.Kernel.CaptureHostMigration();
                        h.Kernel.BeginHostMigration();
                        h.World = 2;
                        h.Kernel.RestoreHostMigration(image, 2, true);
                    }
                    var result = h.Kernel.Advance(tick, true);
                    Check(!result.Events.Any(e => e.Code == "presentation-step"), variant + ": no pulse loses its round");
                    seen.AddRange(result.Presentations.Where(command => command.NodeId == "S4_held").Select(command =>
                        (command.Inputs.GetProperty("target").GetProperty("id").GetString()!, command.Inputs.GetProperty("$play")[0].GetInt64())));
                }
                return seen.OrderBy(row => row.Item1).ThenBy(row => row.Item2).ToArray();
            }
            var rows = Run(false);
            var byTarget = rows.GroupBy(row => row.Target).ToDictionary(group => group.Key, group => group.Select(row => row.Play).Distinct().ToArray());
            Check(rows.Length == 4 && byTarget.Count == 2 && byTarget.Values.All(plays => plays.Length == 1)
                    && byTarget[Entity(1)][0] != byTarget[Entity(2)][0],
                variant + ": each round's pulses resume as that round's one play [" + string.Join("|", rows.Select(row => row.Target + "=" + row.Play)) + "]");
            var moved = Run(true);
            Check(moved.SequenceEqual(rows),
                variant + ": a host migration keeps every suspended pulse's round and play [" + string.Join("|", moved.Select(row => row.Target + "=" + row.Play)) + "]");
        }
        // for_each over positions: one round per point, in list order, with `index` published for the body. The budget
        // bounds the rounds, so a table exactly that long is walked once.
        {
            var h = new Harness();
            h.Load(h.Plan("for-each-position"));
            h.Publish("for-each-position", targets: new[] { Entity(1), Entity(2) });
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S3_body:" + Entity(1) + ":0", "S3_body:" + Entity(1) + ":1", "S2_next:" + Entity(1) + ":-" }),
                "for_each over positions runs one body per position and then enters next [" + string.Join("|", h.Applied) + "]");
        }

        // for_each over positions: a table longer than its budget is refused, never cut short — the rule the
        // candidate walk follows too.
        {
            var h = new Harness();
            h.Load(h.Plan("for-each-position-short"));
            h.Publish("for-each-position-short", targets: new[] { Entity(1), Entity(2) });
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.IterationBudget) && h.Applied.Count == 0,
                "a position table longer than its budget is refused rather than truncated [" + string.Join("|", h.Applied) + "]");
        }
        // for_each: the shape is fixed at load. A walk wires exactly one of its two lists, and each list publishes
        // only its own current value, so reading `item` off a position walk is refused before anything runs.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("for-each-both-lists")), RuntimeAbiCodes.ControlShape, "a for_each wiring both lists");
            var g = new Harness();

        }
        // repeat with an interval: every pulse publishes its own round, counted from 0, beside the timer.
        {
            var h = new Harness();
            h.Load(h.Plan("repeat-pulse-index"));
            h.Publish("repeat-pulse-index");
            for (var tick = 1; tick <= 8; tick++) h.Kernel.Advance(tick, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_body:" + Entity(1) + ":0", "S1_body:" + Entity(1) + ":1", "S1_body:" + Entity(1) + ":2" }),
                "each pulse of a repeat series publishes its round [" + string.Join("|", h.Applied) + "]");
        }
        // enum_switch: the member the set lists at index i is the exit `case_(i+1)`, and only that exit runs.
        {
            var h = new Harness();
            h.Load(h.Plan("enum-switch"));
            h.Publish("enum-switch");
            var tick = h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S3_case_3:" + Entity(1) + ":-" }),
                "enum_switch enters case_(index+1) for the member the set lists at that position [" + string.Join("|", h.Applied) + "]"
                    + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)));
        }

        // enum_switch: optional event values route by member, with null or an omitted port going to otherwise.
        foreach (var (value, omit, expected) in new (int?, bool, string)[]
        { (1, false, "S2_case_2") })
        {
            var h = new Harness(optionalEnum: true);
            h.Load(h.Plan("enum-switch-event"));
            h.Publish("enum-switch-event", maybeEnum: value, omitEnum: omit);
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.All(e => e.Status != "rejected") && h.Applied.SequenceEqual(new[] { expected + ":" + Entity(1) + ":-" }),
                "enum_switch routes a present, null or omitted event value [" + string.Join("|", h.Applied) + "]");
        }
        // cancel: the count is what actually happened, and the cancelled pulse never fires.
        {
            var h = new Harness();
            h.Load(h.Plan("cancel"));
            h.Publish("cancel");
            var tick = h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S2_report:" + Entity(1) + ":1" }), "cancel outputs the count it actually cancelled [" + string.Join("|", h.Applied) + "]" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)));
            Check(tick.Events.All(e => e.Status != "rejected") && h.Kernel.Advance(9, true).CommandsExecuted == 0
                && h.Applied.Count == 1, "the cancelled continuation never fires");
        }
        // cancel: a handle that already died is refused by name, with a zero count.
        {
            var h = new Harness();
            h.Load(h.Plan("cancel-stale"));
            h.Publish("cancel-stale");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.StaleHandle), "a dead handle is refused as stale");
            Check(h.Applied.Count == 0, "the step after the refused cancel never runs");
        }
        // restart: the rolling window. A live timer is re-armed from now and the handle it published keeps
        // working, so the same plan re-runs its window without holding the handle anywhere. The interval's first
        // pulse is `after_interval` (constant 1), so the window the restart moves is still ahead of both calls.
        {
            var h = new Harness();
            h.Load(h.Plan("restart"));
            h.Publish("restart");
            // The interval's own first pulse is at 1 + 5 = 6; the restart at tick 1 puts it at 1 + 5 = 6 as well,
            // so the case reads the re-armed window off the two advances: nothing runs between them, the step after
            // the restart runs now, and exactly one pulse lands on the new tick.
            var activation = h.Kernel.Advance(1, true);
            Check(activation.CommandsExecuted == 1 && h.Applied.SequenceEqual(new[] { "S2_report:" + Entity(1) + ":-" }),
                "a live handle restarts and the flow after it runs [" + string.Join("|", h.Applied) + "]"
                + string.Join("|", activation.Events.Select(e => e.Status + ":" + e.Code))
                + " commands=" + string.Join("|", activation.Commands.Select(c => c.NodeId + ":" + c.Result.Status + ":" + c.Result.Code)));
            Check(activation.Events.All(e => e.Status != "rejected"), "restarting a live timer is not a refusal");
            Check(h.Kernel.Advance(5, true).CommandsExecuted == 0, "the restarted window does not fire before its new interval elapses");
            Check(h.Kernel.Advance(6, true).CommandsExecuted == 1 && h.Applied.Count == 2,
                "the re-armed timer fires one interval after the restart, on the same handle");
            Check(h.Kernel.Advance(11, true).CommandsExecuted == 1 && h.Applied.Count == 3,
                "the re-armed schedule keeps its own cadence for the pulses it has left");
        }
        // restart: a handle no live schedule answers for cannot be reached from a plan. The only handle a later
        // activation can read is the one its resumed control step republishes, and that one is live by
        // construction — so the refusal the kernel raises for a dead handle is proved where it is reachable, by
        // the two cancels of the case above, and this case proves the positive half only.
        // cancel: a handle has no literal spelling, in any plan.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.InvalidPlan("handle-literal")), RuntimeAbiCodes.HandleLiteral, "a handle cannot be written as a literal");
        }
        // one activation may execute only so many steps; a loop round or a resumption is its own activation.
        {
            var h = new Harness(new RuntimeLimits { MaxStepExecutionsPerDispatch = 3 });
            h.Load(h.Plan("step-budget"));
            h.Publish("step-budget");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.DispatchStepBudget),
                "an activation past its step ceiling is refused by name [" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)) + "] applied=" + string.Join("|", h.Applied) + " executed=" + tick.CommandsExecuted);
            Check(h.Applied.SequenceEqual(new[] { "S1_first:" + Entity(1) + ":-", "S2_second:" + Entity(1) + ":-" }),
                "the refused step never runs [" + string.Join("|", h.Applied) + "]");
        }
        // attachments: the list is required, sorted, duplicate-free, of known kinds, and within budget.
        {
            var h = new Harness(mounts: true);
            RejectCode(() => h.Load(h.InvalidPlan("mount-empty")),
                RuntimeAbiCodes.AttachmentEmpty, "a plan without a mount target is refused");
            RejectCode(() => h.Load(h.InvalidPlan("mount-order")),
                RuntimeAbiCodes.AttachmentOrder, "mount targets are ordinal sorted");
            RejectCode(() => h.Load(h.InvalidPlan("mount-duplicate")),
                RuntimeAbiCodes.AttachmentDuplicate, "a repeated mount target is refused");
            RejectCode(() => h.Load(h.InvalidPlan("mount-kind")),
                RuntimeAbiCodes.AttachmentKind, "a kind no provider registered a matcher for is refused at load");

            var budget = new Harness(new RuntimeLimits { MaxAttachmentsPerPlan = 2 }, mounts: true);
            RejectCode(() => budget.Load(budget.InvalidPlan("mount-budget")),
                RuntimeAbiCodes.AttachmentBudget, "a plan above the mount budget is refused");
            budget.Load(budget.Plan("mount-ok"));
            Check(true, "a sorted, duplicate-free mount list within budget loads");
        }
        // attachments: dispatch follows the plan's own mount targets.
        {
            var h = new Harness(mounts: true);
            h.Load(h.Plan("mount-filter"));
            h.Kernel.Advance(1, true);
            Check(h.Applied.Count == 0, "an idle world dispatches nothing");
            var inside = h.Publish("mount-inside");
            h.Kernel.Advance(2, true);
            Check(inside.Status == "queued" && h.Applied.SequenceEqual(new[] { "Action0:" + Entity(1) + ":-" }),
                "an event whose subject is the mounted zone starts the plan");
            var outside = h.Publish("mount-outside", targets: new[] { Entity(2) });
            var tick = h.Kernel.Advance(3, true);
            Check(outside.Status == "ignored" && outside.Code == "attachment-mismatch" && tick.CommandsExecuted == 0,
                "an event about another object never reaches the plan");
        }
        // attachments: a kind whose target names no event subject is judged from the target alone. The world
        // trigger carries no entity port at all, so nothing but the scope matcher can claim its events — and it
        // does, while the same plan under a target the matcher does not answer is never dispatched.
        {
            var world = new Harness();
            world.Load(world.Plan("mount-level"));
            var claimed = world.PublishWorld("mount-level-world");
            Check(claimed.Status == "queued", "a subject-free mount claims an event that carries no subject ["
                + claimed.Status + ":" + claimed.Code + "]");
            var elsewhere = new Harness();
            elsewhere.Load(elsewhere.Plan("mount-level-elsewhere"));
            var miss = elsewhere.PublishWorld("mount-level-miss");
            Check(miss.Status == "ignored" && miss.Code == "attachment-mismatch",
                "a subject-free mount that does not answer the target never reaches the plan [" + miss.Status + ":" + miss.Code + "]");
        }

        // result rows: the shared columns come first, in order, and every later column is a declared value.
        {
            RejectCode(() => ResultProbe("null"), RuntimeAbiCodes.ResultSchema, "a result schema without a declared row shape");
            ResultProbe(null);
            Check(true, "the four shared columns plus real values register");
        }

        return checks;
    }

    /// <summary>One observation capability on its own kernel: enough to prove which tier a port shape forces,
    /// and nothing else, so a rejection can only come from the tier rules.</summary>
    private static void Probe(string execution, string input, string output)
    {
        var kernel = new RuntimeKernel(Fixture.Identity);
        kernel.BeginWorld(1);
        kernel.RegisterModule(ProbeModule(execution, input, output), RuntimeLogLevel.Off);
    }

    private static RuntimeModule ProbeModule(string execution, string input, string output)
    {
        var seed = RuntimeJson.From(new
        {
            providers = new[] { new { id = Id, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new object[] { new {
                id = Id + ".condition.probe", owner = Id, kind = "condition", label = "Probe", version = "1.0.0", parameters = new { },
                graph = new { domains = new[] { "enemy" }, execution, inputs = new[] { RuntimeJson.Parse(input) }, outputs = new[] { RuntimeJson.Parse(output) }, parameters = Array.Empty<object>() } } },
            bindings = new object[] { new {
                id = Id + ".binding.probe", capabilityId = Id + ".condition.probe", providerId = Id, handler = Id + ".handler.probe",
                role = "evaluate", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
        });
        return new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeApi.Fingerprint, ForgeEngine.Testing.TestProviders.Declaration(seed.GetRawText(), new[] { new BindingSupport(Id + ".binding.probe", "implementation-only", NoPermissions) }, shapes: new Dictionary<string, HandlerShape> { [Id + ".handler.probe"] = new HandlerShape().Outputs("value") }), new Dictionary<string, CommandHandler>())
        {
            Evaluators = new Dictionary<string, EvaluatorHandler> { [Id + ".handler.probe"] = (_, o) => o.Boolean(0, true) }
        };
    }

    /// <summary>The same probe for a result schema: the action's own row shape is the only variable. A null
    /// argument leaves the fixture's declared shape alone — that is the case that must register.</summary>
    private static void ResultProbe(string? fields)
    {
        var kernel = new RuntimeKernel(Fixture.Identity);
        kernel.BeginWorld(1);
        var seed = JsonNode.Parse(Fixture.Module(Id).Declaration.Registry.GetRawText())!;
        var result = seed["capabilities"]!.AsArray()[1]!["graph"]!["outputs"]!.AsArray()
            .Single(p => p!["type"]!.GetValue<string>() == "result")!.AsObject();
        if (fields != null) result["fields"] = JsonNode.Parse(fields);
        kernel.RegisterModule(ForgeEngine.Testing.TestProviders.Change(Fixture.Module(Id), registry: seed.ToJsonString()), RuntimeLogLevel.Off);
    }

    internal static string Entity(int index) => EntityKinds.Enemy + ":" + index;
    internal static object FromStep(int slot, int step, int port) => new { slot, fromStepSlot = new { step, port } };
    internal static object FromEvent(int slot, int port) => new { slot, fromEventSlot = port };

    /// <summary>One provider with the four capabilities these cases need: a recorded event, a recorded action that
    /// also reports the value a control step published, a budgeted observation that echoes what it read, and mount
    /// matching for `map-object`. Plan-side helpers speak in port ids; the harness resolves them to wire slots.
    /// Internal rather than private because `ControlFlowTests` walks the same provider for the control walk's own
    /// suspension and resume rules instead of standing a second fixture up beside it.</summary>
    internal sealed class Harness
    {
        internal readonly RuntimeKernel Kernel;
        internal readonly RuntimeModuleHandle Module;
        internal readonly RuntimeSubscriptionGate ZoneGate;
        internal readonly RuntimeSubscriptionGate MessageGate;
        internal readonly List<string> Applied = new();
        internal readonly string TriggerBinding = Id + ".binding.trigger";
        /// <summary>The trigger whose events carry no entity at all: a world, timer or pulse event.</summary>
        internal readonly string WorldTriggerBinding = Id + ".binding.world";
        /// <summary>The second provider's trigger binding, where one was registered: a plan mounted on it is
        /// dispatched by that provider's events.</summary>
        internal readonly string SecondTriggerBinding = Fixture.Trigger(SecondId);
        internal readonly string ObserveBinding = Id + ".binding.observe";
        /// <summary>The kernel's own variable read, resolved for one declared boolean variable — the one read whose
        /// value port lets a case prove the row answers non-null.</summary>
        internal readonly JsonElement ReadFlagContract;
        internal long World = 1;
        internal int Queries;
        internal Func<EntityReference, bool>? IsLive;
        /// <summary>The one variable the variable cases declare in the plan header. A level-scope flag with an
        /// authored initial value, so a read of it answers without any step having written it.</summary>
        internal const string ReadVariable = "level.flag";
        /// <summary>The fixture action's two sentinel amounts: the one that answers with a rejected result and the
        /// one that answers with a confirmed-commit partial. A control region's stop rule needs a command the kernel
        /// really stops on, and the two outcomes are the pair that rule distinguishes.</summary>
        internal const int RefusedAmount = 13;
        internal const int PartialAmount = 17;
        /// <summary>A second provider of the same fixture shape, registered only where a case asks for it: a handle
        /// cast by a provider other than the one a case unregisters is what proves a recycled pool slot is not
        /// released by a stale wait, and a plan mounted on its trigger can outlive the first provider's removal.</summary>
        internal const string SecondId = "example.second";
        /// <summary>The mount target the second provider answers for, of a kind the first provider does not own, so a
        /// plan can still load after the first provider is gone.</summary>
        internal const string SecondMountReference = "example.second.type";
        internal RuntimeModuleHandle? Second;

        internal Harness(RuntimeLimits? ceiling = null, bool mounts = false, bool secondProvider = false, bool optionalEnum = false,
            IRuntimeLogSink? log = null, bool triggerOperators = false, bool equipmentEnum = false,
            RuntimeLogLevel logLevel = RuntimeLogLevel.Error, bool presentation = false, bool plays = true, bool subject = true, bool optionalInstigator = false)
        {
            Kernel = log == null ? new RuntimeKernel(Fixture.Identity, ceiling)
                : new RuntimeKernel(Fixture.Identity, ceiling ?? new RuntimeLimits(), log, logLevel);
            Kernel.BeginWorld(1);
            Kernel.RegisterModule(ControlContracts.Module(), log == null ? RuntimeLogLevel.Off : RuntimeLogLevel.Error);
            if (triggerOperators) Kernel.RegisterBuiltinModule(ForgeEngine.Trigger.ModuleDefinition.Create());
            MessageGate = Kernel.RegisterModule(VariableContracts.Module(), RuntimeLogLevel.Off)
                .SubscriptionGate(VariableContracts.MessageReceivedBinding);
            // The wait fixture names this map event. Register its provider so the test reads the same cached
            // subscription gate a real publisher would hold while a g-wait is pending.
            const string mapId = "forge.module.gtfo.map";
            const string zoneBinding = mapId + ".binding.trigger_map_zone_entered";
            var map = Fixture.Module(mapId);
            var mapSeed = JsonNode.Parse(map.Declaration.Registry.GetRawText())!;
            mapSeed["bindings"]!.AsArray()[0]!["id"] = zoneBinding;
            mapSeed["bindings"]!.AsArray()[0]!["waitable"] = true;
            var mapSupport = map.BindingSupport.ToArray();
            mapSupport[0] = new BindingSupport(zoneBinding, "implementation-only", Array.Empty<string>());
            ZoneGate = Kernel.RegisterModule(ForgeEngine.Testing.TestProviders.Change(map, registry: mapSeed.ToJsonString(), support: mapSupport),
                RuntimeLogLevel.Off).SubscriptionGate(zoneBinding);
            var module = Fixture.Module(Id, context =>
            {
                // The fixture action records which step ran, on what, and the value a preceding control step
                // published into `steps` — that is how a case asserts the frame a loop or a cancel wrote.
                var target = context.Inputs.TryGetProperty("target", out var entity) ? RuntimeJson.Entity(entity).Id : "-";
                var steps = context.Inputs.TryGetProperty("steps", out var value) && value.ValueKind == JsonValueKind.Number ? ((long)value.GetDouble()).ToString() : "-";
                // A vector a preceding control step published — a position table's own position and direction — is
                // logged only when the plan wired the optional port, so every assertion written before it existed
                // still reads the same string.
                var mark = context.Inputs.TryGetProperty("mark", out var point) && point.ValueKind == JsonValueKind.Array
                    ? ":" + string.Join("/", point.EnumerateArray().Select(component => component.GetDouble())) : "";
                var turn = context.Inputs.TryGetProperty("turn", out var facing) && facing.ValueKind == JsonValueKind.Array
                    ? ":" + string.Join("/", facing.EnumerateArray().Select(component => component.GetDouble())) : "";
                // The one `number` input, logged the same way and only when the plan wired it: an integer port cannot
                // carry a fractional value, so a case about a value row (a ramp's own output) reads it here.
                var numeric = context.Inputs.TryGetProperty("number_value", out var number) && number.ValueKind == JsonValueKind.Number
                    ? "=" + number.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                Applied.Add($"{context.NodeId}:{target}:{steps}{mark}{turn}{numeric}");
                // Two amounts are the fixture's own outcomes, because a control region's stop rule can only be
                // tested with a command the kernel really stops on: the refusal is the rejected result, and the
                // confirmed-commit partial is the one non-succeeded result that keeps an entrypoint walking. Every
                // other amount is the ordinary success the rest of the cases read.
                var amount = context.Parameters.GetProperty("amount").GetDouble();
                if (amount == RefusedAmount) return CommandResult.Rejected("fixture-refused", "The fixture refuses this amount.");
                if (amount == PartialAmount) return CommandResult.Partial(RuntimeJson.From(new { actual = 0, count = 7 }), CommitStates.Confirmed, Array.Empty<RuntimeFact>());
                // `count` is the movable output the action-publishes-a-value cases read: a declared column of this
                // capability's own result row, read back by a later step through `fromStepSlot`.
                return CommandResult.Succeeded(RuntimeJson.From(new { actual = 1, count = 7 }));
            });
            var seed = JsonNode.Parse(module.Declaration.Registry.GetRawText())!;
            seed["capabilities"]!.AsArray()[0]!["graph"]!["outputs"] = JsonNode.Parse(
                "[{\"id\":\"next\",\"type\":\"execution\"},{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"targets\",\"type\":\"entity\",\"cardinality\":\"many\"},{\"id\":\"amount\",\"type\":\"number\"},{\"id\":\"maybe\",\"type\":\"integer\",\"nullable\":true},{\"id\":\"maybe_enum\",\"type\":\"enum\",\"schema\":\"execution_outcome\",\"nullable\":true}]");
            if (optionalEnum) seed["capabilities"]![0]!["graph"]!["outputs"]![5]!["optional"] = true;
            if (equipmentEnum) seed["capabilities"]![0]!["graph"]!["outputs"]![5]!["schema"] = "equipment_kind";
            // The action's declared outputs: the result row it publishes (a row is not a value a step hands on),
            // plus one movable integer a later step reads through `fromStepSlot`.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["outputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"count\",\"type\":\"integer\"}"));
            // The same action as a `presentation` row, where a case is about what the host's walk hands a recipient.
            if (presentation) seed["capabilities"]!.AsArray()[1]!["graph"]!["execution"] = "presentation";
            // Outside every loop a held effect's play is about the trigger's own subject, so the presentation cases'
            // trigger names its `target` as that subject.
            if (presentation && subject) seed["capabilities"]!.AsArray()[0]!["graph"]!["subject"] = "target";
            // A second trigger with no entity port at all: a world, timer or pulse event carries no subject, and a
            // case that publishes one proves a subject-free mount is judged for exactly those events.
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Id + ".trigger.world",
                owner = Id,
                kind = "trigger",
                label = "World event",
                version = "1.0.0",
                parameters = new { },
                graph = new
                {
                    domains = new[] { "enemy" },
                    execution = "host",
                    inputs = Array.Empty<object>(),
                    outputs = new object[] { new { id = "next", type = "execution" } },
                    parameters = Array.Empty<object>()
                }
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = WorldTriggerBinding,
                capabilityId = Id + ".trigger.world",
                providerId = Id,
                handler = Id + ".handler.world",
                role = "observe",
                status = "implemented",
                dependencies = Array.Empty<string>(),
                requires = Array.Empty<string>()
            }).GetRawText())!);
            // `steps` is the fixture's one movable value input and stays an integer: a control step publishes the
            // count a case asserts (`cancel`'s cancelled, a loop's index), and the action records it as the number
            // its own log line carries.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["inputs"] = JsonNode.Parse(
                "[{\"id\":\"in\",\"type\":\"execution\"},{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"steps\",\"type\":\"integer\",\"optional\":true},"
                + "{\"id\":\"mark\",\"type\":\"vector3\",\"unit\":\"m\",\"optional\":true,\"nullable\":true},"
                + "{\"id\":\"turn\",\"type\":\"vector3\",\"unit\":\"deg\",\"optional\":true,\"nullable\":true},"
                + "{\"id\":\"number_value\",\"type\":\"number\",\"optional\":true,\"nullable\":true}]");
            if (optionalInstigator)
            {
                seed["capabilities"]![0]!["graph"]!["outputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"actor\",\"type\":\"entity\",\"optional\":true}"));
                seed["capabilities"]![1]!["graph"]!["inputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"instigator\",\"type\":\"entity\",\"optional\":true}"));
            }
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Id + ".observe",
                owner = Id,
                kind = "selector",
                label = "Observed entities",
                version = "1.0.0",
                parameters = new { },
                graph = new
                {
                    domains = new[] { "enemy" },
                    execution = "query",
                    inputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } },
                    outputs = new object[] { new { id = "seen", type = "entity" }, new { id = "seen_many", type = "entity", cardinality = "many" }, new { id = "hits", type = "integer" },
                        new { id = "points", type = "vector3", cardinality = "many" },
                        new { id = "look_dirs", type = "vector3", cardinality = "many" },
                        new { id = "points_extra", type = "vector3", cardinality = "many" } },
                    parameters = Array.Empty<object>()
                }
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = ObserveBinding,
                capabilityId = Id + ".observe",
                providerId = Id,
                handler = Id + ".handler.observe",
                role = "observe",
                status = "implemented",
                dependencies = Array.Empty<string>(),
                requires = Array.Empty<string>()
            }).GetRawText())!);
            Module = Kernel.RegisterModule(ForgeEngine.Testing.TestProviders.Change(module, registry: seed.ToJsonString(), shapes: new Dictionary<string, HandlerShape>
            {
                [Fixture.Handler(Id)] = presentation && plays ? Fixture.Shape(Id, new[] { "target", "steps", "mark", "turn" }).KeysByPlay() : Fixture.Shape(Id, new[] { "target", "steps", "mark", "turn" }),
                [Id + ".handler.observe"] = new HandlerShape().Inputs("targets").Outputs("seen", "seen_many", "hits", "points", "look_dirs", "points_extra")
            }, support: new[] { Fixture.Support(Id)[0], Fixture.Support(Id)[1],
                    new BindingSupport(WorldTriggerBinding, "implementation-only", NoPermissions),
                    new BindingSupport(ObserveBinding, "implementation-only", NoPermissions) }) with
            {
                Evaluators = new Dictionary<string, EvaluatorHandler> { [Id + ".handler.observe"] = Observe },
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [EntityKinds.Enemy] = reference => reference.Id.StartsWith(EntityKinds.Enemy + ":", StringComparison.Ordinal) && reference.WorldEpoch == World && reference.LifeEpoch == 1 && (IsLive?.Invoke(reference) ?? true) },
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { [EntityKinds.Enemy] = reference => new RuntimeEntitySnapshot(reference, EntityKinds.Enemy, null, "alive", new[] { "tag" }, Array.Empty<string>(), new[] { 0d, 0d, 0d }) },
                AttachmentMatchers = Mounts(mounts),
                PresentationSessions = presentation ? new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>
                {
                    [Id] = _ => new[] { "2" }
                } : null
            }, log == null ? RuntimeLogLevel.Off : RuntimeLogLevel.Error);
            // The optional second provider, registered before startup like every module. It brings its own
            // id-prefixed capabilities and bindings, so it shares nothing with the first provider but the kernel: a
            // plan may depend on the first through one step and be mounted on the second's trigger, which is how a
            // case can remove a plan while the provider that sourced its wait is still registered.
            if (secondProvider)
                Second = Kernel.RegisterModule(Fixture.Module(SecondId, _ => CommandResult.Succeeded(RuntimeJson.EmptyObject)) with
                {
                    EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                    {
                        [SecondId] = reference => reference.Id.StartsWith(SecondId + ":", StringComparison.Ordinal)
                            && reference.WorldEpoch == World
                    },
                    // Its own mount kind, so a plan it carries keeps loading after the first provider is gone: one
                    // provider owns a kind, and this one is not the kind the first provider answers for.
                    AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
                    {
                        ["enemy-type"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == SecondMountReference)
                    }
                }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            // The kernel's variable module: `value_type` is a compiled member index, so the read is resolved the way
            // the website compiles it — `1` is `boolean` in the row's own member list.
            ReadFlagContract = Contract(VariableContracts.ReadCapability, RuntimeJson.From(new { name = ReadVariable, value_type = 1 }));
        }

        internal JsonElement Contract(string capabilityId, JsonElement parameters)
            => Kernel.ResolveGraphContract(capabilityId, "1.0.0", parameters);

        /// <summary>The fixture provider's mount kinds. Every plan here mounts the whole level, and no kind belongs
        /// to the kernel any more, so the scope-matched kind is always claimed — without it no fixture plan would
        /// load. The subject-matched kind is claimed only where a case is about the subject path.</summary>
        private static IReadOnlyDictionary<string, AttachmentMatcherRegistration> Mounts(bool subjectMounts)
        {
            var matchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
            {
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == Fixture.MountReference)
            };
            if (subjectMounts)
                matchers["map-object"] = AttachmentMatcherRegistration.BySubject(
                    (category, reference, subject) => category == "zone" && reference == "z1" && subject.Id == EntityKinds.Enemy + ":1");
            return matchers;
        }

        private void Observe(EvaluationContext context, EvaluationOutputs outputs)
        {
            var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            var seen = new List<EntityReference>();
            foreach (var target in targets)
            {
                Queries++;
                if (!context.Query.TrySnapshot(target, out var snapshot, out var code)) throw new RuntimeContractException(code, code);
                seen.Add(snapshot!.Ref);
            }
            ForgeEngine.Testing.TestContexts.Answer(outputs, new
            {
                seen = seen[0],
                seen_many = seen.ToArray(),
                hits = seen.Count,
                // The fixture's one position list: `for_each` walks what a step published, so the list a
                // case checks is produced by a query step exactly the way the candidate set is. The directions are
                // the table's second half, one per position.
                points = new[] { new[] { 1d, 2d, 3d }, new[] { 4d, 5d, 6d } },
                look_dirs = new[] { new[] { 7d, 8d, 9d }, new[] { 10d, 11d, 12d } },
                // A second table of the same kind and a different length, so a case can wire halves that disagree.
                points_extra = new[] { new[] { 1d, 1d, 1d }, new[] { 2d, 2d, 2d }, new[] { 3d, 3d, 3d } }
            });
        }

        internal string Plan(string planId)
        {
            var variant = planId switch
            {
                "branch-False" => "branch-false",
                "branch-True" => "branch-true",
                "latch:other" => "latch-other",
                _ => planId
            };
            if (CompletionPlans.Contains(variant))
                return TestRuntime.ReadPlan("framework-completion", variant);
            if (CompiledPlans.Contains(variant))
                return TestRuntime.ReadPlan("framework-plan-abi", variant);
            throw new InvalidOperationException("No compiled plan fixture for " + planId + ".");
        }

        /// <summary>Builds a refusal case by changing only the malformed field on a legal TS-compiled plan.</summary>
        internal string InvalidPlan(string planId)
        {
            var baseline = planId switch
            {
                "wrong-tier" => "query-chain",
                "reads-action-row" => "action-output",
                "guarded-outside" or "tested-source-direct" or "omitted-actor-as-instigator" => "present-branch",
                "guarded-after-merge" => "guarded-after-merge-baseline",
                "query-read-outside" => "query-in-present",
                "other-guard" => "own-guard",
                "ramp-missing-input" or "ramp-budget" => "ramp",
                "for-each-both-lists" => "for-each",
                "for-each-position-item" => "for-each-position",
                "repeat-no-count" => "repeat",
                "repeat-counted-timer" => "repeat-counted-timer-baseline",
                "handle-literal" => "cancel",
                "layout-hash" => "query-chain",
                "mount-empty" or "mount-order" or "mount-duplicate" or "mount-kind" or "mount-category"
                    or "mount-kindname" or "mount-budget" => "mount-ok",
                _ => throw new ArgumentOutOfRangeException(nameof(planId), planId, "No compiled negative baseline.")
            };
            var plan = JsonNode.Parse(planId is "guarded-after-merge" or "repeat-counted-timer"
                ? TestRuntime.ReadPlan("framework-completion", baseline)
                : Plan(baseline))!;
            plan["planId"] = planId;
            var steps = plan["entrypoints"]![0]!["steps"]!.AsArray();
            JsonObject Input(int step, int slot) => steps[step]!["inputs"]!.AsArray()
                .Single(row => row!["slot"]!.GetValue<int>() == slot)!.AsObject();

            switch (planId)
            {
                case "layout-hash":
                    plan["layoutHash"] = new string('0', 64);
                    break;
                case "wrong-tier":
                    steps[0]!["nodeKind"] = "pure";
                    break;
                case "reads-action-row":
                    Input(1, 2)["fromStepSlot"]!["port"] = 1;
                    break;
                case "guarded-outside":
                    steps[2]!["inputs"]!.AsArray().Add(JsonNode.Parse(Input(1, 2).ToJsonString()));
                    break;
                case "guarded-after-merge":
                    steps.Single(step => step!["nodeId"]!.GetValue<string>() == "Merged")!["inputs"]!.AsArray().Add(JsonNode.Parse(
                        "{\"slot\":2,\"fromStepSlot\":{\"step\":0,\"port\":2}}"));
                    break;
                case "query-read-outside":
                    steps[3]!["inputs"]!.AsArray().Add(JsonNode.Parse(Input(2, 2).ToJsonString()));
                    break;
                case "tested-source-direct":
                    var direct = Input(1, 2);
                    direct.Remove("fromStepSlot");
                    direct["fromEventSlot"] = 4;
                    break;
                case "omitted-actor-as-instigator":
                    steps[1]!["inputs"]!.AsArray().Add(JsonNode.Parse("{\"slot\":6,\"fromEventSlot\":6}"));
                    break;
                case "other-guard":
                    Input(3, 2)["fromStepSlot"]!["step"] = 0;
                    break;
                case "ramp-missing-input":
                    steps[0]!["inputs"]!.AsArray().Remove(Input(0, 2));
                    break;
                case "ramp-budget":
                    // Each leg is inside the ceiling; the duration (slot 3) and the release (slot 5) together are one past it.
                    Input(0, 3)["value"] = 1024;
                    Input(0, 5)["value"] = 1;
                    break;
                case "for-each-both-lists":
                    steps[1]!["inputs"]!.AsArray().Insert(1, JsonNode.Parse(
                        "{\"slot\":2,\"fromStepSlot\":{\"step\":0,\"port\":3}}"));
                    break;
                case "for-each-position-item":
                    var item = Input(3, 1);
                    item.Remove("fromEventSlot");
                    item["fromStepSlot"] = JsonNode.Parse("{\"step\":1,\"port\":2}");
                    break;
                case "repeat-no-count":
                    steps[0]!["inputs"]!.AsArray().Clear();
                    break;
                case "repeat-counted-timer":
                    steps[0]!["inputs"]!.AsArray().RemoveAt(0);
                    break;
                case "handle-literal":
                    var handle = Input(1, 1);
                    handle.Remove("fromStepSlot");
                    handle["value"] = JsonNode.Parse("{\"worldEpoch\":1,\"lifeEpoch\":1,\"local\":0,\"provider\":0}");
                    break;
                case "mount-empty":
                    plan["attachments"] = new JsonArray();
                    break;
                case "mount-order":
                    plan["attachments"] = JsonNode.Parse(
                        "[{\"kind\":\"map-object\",\"category\":\"zone\",\"reference\":\"z1\"},{\"kind\":\"level\",\"reference\":\"l\"}]");
                    break;
                case "mount-duplicate":
                    plan["attachments"] = JsonNode.Parse(
                        "[{\"kind\":\"map-object\",\"category\":\"zone\",\"reference\":\"z1\"},{\"kind\":\"map-object\",\"category\":\"zone\",\"reference\":\"z1\"}]");
                    break;
                case "mount-kind":
                    plan["attachments"] = JsonNode.Parse("[{\"kind\":\"enemy-type\",\"reference\":\"e1\"}]");
                    break;
                case "mount-category":
                    plan["attachments"] = JsonNode.Parse("[{\"kind\":\"level\",\"category\":\"zone\",\"reference\":\"l\"}]");
                    break;
                case "mount-kindname":
                    plan["attachments"] = JsonNode.Parse("[{\"kind\":\"sector\",\"reference\":\"s1\"}]");
                    break;
                case "mount-budget":
                    plan["attachments"]!.AsArray().Add(JsonNode.Parse(
                        "{\"kind\":\"map-object\",\"category\":\"zone\",\"reference\":\"z3\"}"));
                    break;
            }
            return plan.ToJsonString();
        }

        /// <summary>Loads one plan, re-throwing with the code in the message: a failing case must name the rule it
        /// broke, not just the node it broke on.</summary>
        internal void Load(string json)
        {
            try { Kernel.LoadPlan(json); }
            catch (RuntimeContractException error) { throw new RuntimeContractException(error.Code, error.Code + ": " + error.Message); }
        }

        internal DispatchResult Publish(string eventId, long tick = 1, string[]? targets = null, long? maybe = null, int? maybeEnum = null, bool omitEnum = false)
        {
            // The event's subject is the first of its targets: an event about another object must carry that
            // object in `target` too, or a mount matcher could never tell the two apart. `maybe` is the one
            // possibly-absent payload value the when-present cases test, so it is always present in the payload and
            // null when the case means "no value"; `maybe_enum` can also be omitted to test an optional event port.
            var all = targets ?? new[] { EntityKinds.Enemy + ":1" };
            var values = new List<(string, object?)>
            {
                ("target", new EntityReference(all[0], World, 1)),
                ("targets", all.Select(id => new EntityReference(id, World, 1)).ToArray()),
                ("amount", 5d),
                ("maybe", maybe)
            };
            if (!omitEnum) values.Add(("maybe_enum", maybeEnum));
            return Fixture.Raise(Module, TriggerBinding, eventId, World, tick, values.ToArray());
        }

        /// <summary>One event of the world trigger, which carries no entity port at all: a world, timer or pulse
        /// event has no subject, so no mount matcher that reads subjects has anything to read.</summary>
        internal DispatchResult PublishWorld(string eventId, long tick = 1)
            => Fixture.Raise(Module, WorldTriggerBinding, eventId, World, tick);

        /// <summary>One event of the second provider's trigger, about one of that provider's own entities: the
        /// dispatch a plan mounted on that trigger receives, and therefore the source a wait it enters is booked
        /// against.</summary>
        internal DispatchResult PublishSecond(string eventId, long tick = 1)
            => Fixture.Raise(Second!, SecondTriggerBinding, eventId, World, tick, ("target", new EntityReference(SecondId + ":1", World, 1)));
    }
}
