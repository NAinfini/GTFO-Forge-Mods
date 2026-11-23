using System.Text.Json.Nodes;
using ForgeEngine.Framework;
using ForgeEngine.Testing;
using System.Text.Json;

internal static class FrameworkSuite
{
    internal static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "bench") return TypedOutputBench.Run(args[1..]);
        var checks = PlanAbiTests.Run() + ResultRowAbiTests.Run() + TypedOutputTests.Run() + PathChecks() + LeaseChecks() + ContentHeaderChecks() + ContentIdCollisionChecks()
            + GenerationChecks();
        Console.WriteLine($"Framework checks: {checks} passed.");
        return 0;
    }

    /// <summary>The content generation transaction (contract I-RELOAD): startup commits the empty generation into generation 1, an
    /// unchanged participant carries its leases, a refused preparation and a failed commit both leave the current generation
    /// exactly as it was (the committed participants reverted newest first), and a lease revokes once.</summary>
    private static int GenerationChecks()
    {
        var checks = 0;
        void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); checks++; }
        var journal = new List<string>();
        var game = new List<string>();
        var a = new FakeParticipant("a", journal, game);
        var b = new FakeParticipant("b", journal, game);
        var generations = new ForgeEngine.GameBindings.ContentGenerations();
        generations.Register(a); generations.Register(b);
        var changes = new List<ForgeEngine.GameBindings.ContentChanged>();
        generations.Changed += changes.Add;
        a.Input = "a1"; b.Input = "b1";
        var first = generations.Commit(generations.Discover("root", startup: true));
        Check(first.Number == 1 && game.SequenceEqual(new[] { "a1", "b1" }) && journal.Take(4).SequenceEqual(new[] { "read a", "read b", "commit a", "commit b" })
            && changes.Single() is { From: 0, To: 1 }, "startup commits the empty generation into generation 1 in registration order");
        journal.Clear(); changes.Clear();
        b.Input = "b2";
        var second = generations.Commit(generations.Discover("root", startup: false));
        Check(second.Number == 2 && ReferenceEquals(second.Leases("a"), first.Leases("a")) && game.SequenceEqual(new[] { "a1", "b2" })
            && changes.Single().Changed.SequenceEqual(new[] { "b" }) && journal.Contains("release b"),
            "an unchanged participant carries its leases; a changed one replaces and releases what it replaced");
        journal.Clear(); changes.Clear();
        a.Input = "a3"; b.Input = "b3"; b.RefusePrepare = true;
        try { generations.Commit(generations.Discover("root", startup: false)); throw new Exception("FAIL: a refused preparation throws"); }
        catch (ForgeEngine.GameBindings.ContentGenerationRefusal refusal)
        {
            Check(refusal.Participant == "b" && ReferenceEquals(generations.Current, second) && game.SequenceEqual(new[] { "a1", "b2" })
                && journal.Contains("discard a") && !journal.Any(line => line.StartsWith("commit", StringComparison.Ordinal)) && changes.Count == 0,
                "a refused preparation writes nothing and discards what the others prepared");
        }
        journal.Clear(); b.RefusePrepare = false; b.FailCommit = true;
        try { generations.Commit(generations.Discover("root", startup: false)); throw new Exception("FAIL: a failed commit throws"); }
        catch (ForgeEngine.GameBindings.ContentGenerationRefusal refusal)
        {
            Check(refusal.Code == ForgeEngine.GameBindings.ContentGenerationRefusal.CommitFailed && ReferenceEquals(generations.Current, second)
                && game.SequenceEqual(new[] { "a1", "b2" })
                && journal.SkipWhile(line => line != "commit a").SequenceEqual(new[] { "commit a", "commit b", "revert b", "revert a" }),
                "a failed commit reverts the committed participants newest first and keeps the current generation");
        }
        int revoked = 0;
        var lease = new ForgeEngine.GameBindings.ContentLease(ForgeEngine.GameBindings.ContentLeaseKind.KernelContent, "test", () => revoked++);
        lease.Revoke(); lease.Revoke();
        Check(revoked == 1 && lease.Revoked, "a lease revokes exactly once");
        return checks;
    }

    /// <summary>A participant whose content is one string in a list standing for the game; it swaps its own entry in place.</summary>
    private sealed class FakeParticipant : ForgeEngine.GameBindings.IContentParticipant
    {
        private readonly List<string> _journal, _game;
        internal FakeParticipant(string id, List<string> journal, List<string> game) { Id = id; _journal = journal; _game = game; }
        public string Id { get; }
        internal string Input = "";
        internal bool RefusePrepare, FailCommit;
        public void Read(ForgeEngine.GameBindings.ContentDiskSnapshot snapshot) { _journal.Add("read " + Id); snapshot.Put(Id, Input); }
        public ForgeEngine.GameBindings.ContentPart Admit(ForgeEngine.GameBindings.ContentDiskSnapshot snapshot, ForgeEngine.GameBindings.ContentGeneration current)
        {
            var input = snapshot.Get<string>(Id);
            return new(Id, input, input);
        }
        public ForgeEngine.GameBindings.PreparedContent Prepare(ForgeEngine.GameBindings.ContentChange change)
        {
            if (RefusePrepare) throw new ForgeEngine.GameBindings.ContentGenerationRefusal(ForgeEngine.GameBindings.ContentGenerationRefusal.Unsupported, Id, "refused");
            if (change.Unchanged) return ForgeEngine.GameBindings.PreparedContent.Carry(change.CurrentLeases!);
            return new Swap(this, (string)change.Part.Value!);
        }

        private sealed class Swap : ForgeEngine.GameBindings.PreparedContent
        {
            private readonly FakeParticipant _owner;
            private readonly string _value;
            private int _slot = -1;
            private string? _old;
            internal Swap(FakeParticipant owner, string value) { _owner = owner; _value = value; }
            internal override void Commit(ForgeEngine.GameBindings.ContentGeneration next)
            {
                _owner._journal.Add("commit " + _owner.Id);
                _slot = _owner._game.FindIndex(entry => entry.StartsWith(_owner.Id, StringComparison.Ordinal));
                if (_slot < 0) { _owner._game.Add(_value); _slot = _owner._game.Count - 1; } else { _old = _owner._game[_slot]; _owner._game[_slot] = _value; }
                if (_owner.FailCommit) throw new InvalidOperationException("native write failed");
            }
            internal override void Release(ForgeEngine.GameBindings.ContentLeases? previous) => _owner._journal.Add("release " + _owner.Id);
            internal override void Revert()
            {
                _owner._journal.Add("revert " + _owner.Id);
                if (_slot < 0) return;
                if (_old == null) _owner._game.RemoveAt(_slot); else _owner._game[_slot] = _old;
            }
            internal override void Discard() => _owner._journal.Add("discard " + _owner.Id);
        }
    }

    /// <summary>The native block header rule discovery shares with the website's `contentEntryDataBlockIdentities`: keyed
    /// children and several children of one type are accepted; a bad key, a repeated id or a missing primary is refused.</summary>
    private static int ContentHeaderChecks()
    {
        var checks = 0;
        const string Entry = "Team-Pack:boss";
        IReadOnlyList<ForgeEngine.Framework.ContentDataBlockId> Read(string blocks) =>
            ForgeEngine.GameBindings.ContentPackDiscovery.NativeIdentities(new("Team-Pack:boss", "enemy", "Boss", Array.Empty<string>(), "{\"blocks\":[" + blocks + "]}", ""));
        string Block(string id, string type) => "{\"id\":\"" + id + "\",\"type\":\"" + type + "\"}";
        var accepted = Read(Block(Entry, "Enemy") + "," + Block(Entry + "#EnemyGroup", "EnemyGroup") + "," + Block(Entry + "#EnemyGroup:wave-1", "EnemyGroup")
            + "," + Block(Entry + "#EnemyGroup:wave_2", "EnemyGroup"));
        if (accepted.Count != 4) throw new Exception("FAIL: keyed children of one type are accepted");
        checks++;
        foreach (var (blocks, what) in new[]
        {
            (Block(Entry, "Enemy") + "," + Block(Entry + "#EnemyGroup:Wave", "EnemyGroup"), "an upper-case key"),
            (Block(Entry, "Enemy") + "," + Block(Entry + "#EnemyGroup:", "EnemyGroup"), "an empty key"),
            (Block(Entry, "Enemy") + "," + Block(Entry + "#EnemyGroup:-a", "EnemyGroup"), "a key starting with a dash"),
            (Block(Entry, "Enemy") + "," + Block(Entry + "#EnemyGroup:" + new string('a', 65), "EnemyGroup"), "a key over 64 characters"),
            (Block(Entry, "Enemy") + "," + Block(Entry + "#Enemy:a", "EnemyGroup"), "a key under another type"),
            (Block(Entry, "Enemy") + "," + Block(Entry + "#EnemyGroup:a", "EnemyGroup") + "," + Block(Entry + "#EnemyGroup:a", "EnemyGroup"), "a repeated id"),
            (Block(Entry + "#EnemyGroup", "EnemyGroup"), "no primary block"),
            (Block(Entry, "Enemy") + "," + Block(Entry, "Enemy"), "two primary blocks"),
        })
        {
            try { Read(blocks); }
            catch (InvalidDataException) { checks++; continue; }
            throw new Exception("FAIL: a native block header with " + what + " is refused");
        }
        return checks;
    }

    /// <summary>The collision rule discovery shares with the website's `validateContentPacks`, on the case table both sides
    /// answer: a site number refuses the colliding entries, a local-band number refuses every colliding package whole.</summary>
    private static int ContentIdCollisionChecks()
    {
        var checks = 0;
        var table = TestRuntime.FindInCheckout(Path.Combine("Tests", "Forge", "fixtures", "content-id-collision-cases.json"))
            ?? throw new Exception("FAIL: Tests/Forge/fixtures/content-id-collision-cases.json is not in the checkout");
        using var cases = JsonDocument.Parse(File.ReadAllBytes(table));
        foreach (var row in cases.RootElement.EnumerateArray())
        {
            var packs = row.GetProperty("packs").EnumerateArray().Select(pack =>
            {
                string name = pack.GetProperty("package").GetString()!;
                var ids = pack.GetProperty("ids").EnumerateObject().ToDictionary(id => id.Name, id => id.Value.GetUInt32(), StringComparer.Ordinal);
                var entries = ids.Keys.Select(id => new ForgeEngine.GameBindings.ContentEntry(id, "behavior", id, Array.Empty<string>(), "{}", id)).ToArray();
                return new ForgeEngine.GameBindings.ContentPack(name, "1.0.0", Array.Empty<string>(), entries, name) { Ids = ids };
            }).ToArray();
            var rejected = ForgeEngine.GameBindings.ContentPackDiscovery.Validate(packs).Rejections
                .ToDictionary(rejection => rejection.Entry, rejection => rejection.Code, StringComparer.Ordinal);
            var expected = row.GetProperty("rejected").EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!, StringComparer.Ordinal);
            if (rejected.Count != expected.Count || expected.Any(entry => !rejected.TryGetValue(entry.Key, out var code) || code != entry.Value))
                throw new Exception("FAIL: " + row.GetProperty("case").GetString() + ": got " + JsonSerializer.Serialize(rejected));
            checks++;
        }
        if (checks == 0) throw new Exception("FAIL: the shared content id collision table is empty");
        return checks;
    }

    /// <summary>A shared entry's subscription never outlives its world or reaches a subscriber after it failed, and its
    /// subscribers are walked by descending priority with ties in subscription order.</summary>
    private static int LeaseChecks()
    {
        var checks = 0;
        void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); checks++; }
        void Refused(Action action, string reason)
        {
            try { action(); }
            catch (InvalidOperationException error) { Check(error.Message == reason, "refuses " + reason); return; }
            throw new Exception("FAIL: expected " + reason);
        }
        long world = 1;
        var failures = new List<string>();
        var list = new ForgeEngine.GameBindings.NativeHookObservers<Action<int>>(() => world, (consumer, _) => failures.Add(consumer), 3);
        var received = new List<string>();
        void Publish(int value)
        {
            foreach (var entry in list.Items)
            {
                if (!entry.Lease.IsActive) continue;
                try { entry.Observer(value); } catch (Exception error) { list.Failed(entry.Lease, error); }
            }
        }
        var old = list.Subscribe("old", 1, 0, value => received.Add("old" + value), reason => reason);
        Refused(() => list.Subscribe("old", 1, 0, _ => { }, reason => reason), "native-hook-consumer-duplicate");
        var bad = list.Subscribe("bad", 1, 0, _ => throw new InvalidOperationException(), reason => reason);
        list.Subscribe("first", 1, 800, value => received.Add("first" + value), reason => reason);
        Refused(() => list.Subscribe("fourth", 1, 0, _ => { }, reason => reason), "native-hook-subscriber-budget");
        Publish(1);
        Check(bad.Status == "observer-failed" && failures.SequenceEqual(new[] { "bad" }) && received.SequenceEqual(new[] { "first1", "old1" }),
            "a failing subscriber is ended and reported once, the others still run, a higher priority runs first");
        world = 2;
        Check(!old.IsActive && old.Status == "world-epoch-ended", "a lease of an old world ends by itself");
        Refused(() => list.Subscribe("late", 1, 0, _ => { }, reason => reason), "native-hook-world-epoch-stale");
        var next = list.Subscribe("old", 2, 0, value => received.Add("new" + value), reason => reason);
        received.Clear();
        Publish(2);
        Check(received.SequenceEqual(new[] { "new2" }) && next.IsActive, "the new world's subscriber receives only new calls");
        next.Dispose();
        Check(next.Status == "released" && list.Items.Length == 0, "a released lease leaves the list");
        return checks;
    }

    /// <summary>The one relative-path rule (ForgeCore.PosixPath) and the BepInEx resolution built on it: a `..` step, a
    /// drive, an absolute or UNC path, an empty or `.` segment and a name Windows would alias are refused outright, even a
    /// `..` that would land back inside the root. The rule's cases are the table the website's posix-path.ts answers too.</summary>
    private static int PathChecks()
    {
        var checks = 0;
        var table = TestRuntime.FindInCheckout(Path.Combine("Tests", "Forge", "fixtures", "posix-path-cases.json"))
            ?? throw new Exception("FAIL: Tests/Forge/fixtures/posix-path-cases.json is not in the checkout");
        using var cases = JsonDocument.Parse(File.ReadAllBytes(table));
        foreach (var row in cases.RootElement.EnumerateArray())
        {
            var path = row.GetProperty("path").GetString()!;
            if (ForgeCore.PosixPath.IsRelative(path) != row.GetProperty("isRelative").GetBoolean()
                || ForgeCore.PosixPath.IsRelativeFile(path) != row.GetProperty("isRelativeFile").GetBoolean())
                throw new Exception("FAIL: the relative-path rule misjudged " + JsonSerializer.Serialize(path));
            checks++;
        }
        if (checks == 0) throw new Exception("FAIL: the shared relative-path case table is empty");
        var root = Path.Combine(Path.GetTempPath(), "forge-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var plain in new[] { "plugins/pack/a.json", "plugins\\pack/a.json" })
            {
                if (!PublishedResources.ResolveWithinRoot(root, plain).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("FAIL: the plain relative path " + plain + " did not resolve under the root");
                checks++;
            }
            foreach (var name in new[] { "plugins/../a.json", "plugins\\..\\a.json", "C:/Windows/a.json", "C:a.json", "/a.json",
                "\\\\host\\share\\a.json", "plugins/./a.json", "plugins//a.json", "plugins/con" })
            {
                try { PublishedResources.ResolveWithinRoot(root, name); }
                catch (InvalidDataException) { checks++; continue; }
                throw new Exception("FAIL: the path " + name + " was accepted");
            }
            return checks;
        }
        finally { Directory.Delete(root, true); }
    }
}
sealed class Scenario
{
    public readonly RuntimeKernel Kernel;
    public readonly List<string> Applied = new();
    public long World = 1, Life = 1;
    /// <summary>The mount owner is registered first, because a plan's mount kind has to be owned before the plan
    /// that declares it loads; the scenarios here register one or two behaviour providers on top of it.
    /// <paramref name="subjectMounts"/> adds the mount kind that accepts an entity, which a per-instance trigger
    /// option needs before a plan may scope anything to one.</summary>
    public Scenario(RuntimeLimits? limits = null, bool subjectMounts = false, IRuntimeLogSink? logSink = null)
    {
        Kernel = logSink == null ? new RuntimeKernel(Fixture.Identity, limits)
            : new RuntimeKernel(Fixture.Identity, limits ?? new RuntimeLimits(), logSink, RuntimeLogLevel.Error);
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(subjectMounts ? Fixture.MountOwnerWithSubjects() : Fixture.MountOwner(), RuntimeLogLevel.Off);
    }
    /// <summary>The kinds this scenario answers for: every entity of the provider's own kind. The index after the
    /// colon is what tells a per-player option's players apart — that clock is charged against the entity the event's
    /// own `instigator` port names, and an instigator that does not resolve is refused before any gate sees the
    /// event — while every other case here names the one entity its provider owns.</summary>
    public Dictionary<string, Func<EntityReference, bool>> Resolvers(string id)
    {
        var prefix = id + ":";
        bool Entity(EntityReference r) => r.WorldEpoch == World && r.LifeEpoch == Life
            && r.Id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(r.Id.AsSpan(prefix.Length), out _);
        return new() { [id] = Entity };
    }
    /// <summary>The one player of a scenario's provider kind: the entity a per-player clock is charged against.</summary>
    public EntityReference Player(string id, int index) => new(id + ":" + index, World, Life);
    public RuntimeModuleHandle Register(string id, CommandHandler? handler = null, bool amountPort = false, bool instigatorPort = false,
        bool effectPort = false, EffectRestoreHandler? restore = null, bool endOn = false, bool manyPort = false,
        bool statusEnded = false, bool recipientSet = false, bool resultInput = false, bool spawnedRow = false,
        bool equipmentPort = false, RuntimeLogLevel logLevel = RuntimeLogLevel.Off)
        => Kernel.RegisterModule(Fixture.Module(id,
        handler ?? (ctx =>
        {
            Applied.Add(id + ":" + ctx.Parameters.GetProperty("amount").GetDouble()); return CommandResult.Succeeded(RuntimeJson.From(new { actual = ctx.Parameters.GetProperty("amount").GetDouble() }));
        }), amountPort, instigatorPort, effectPort, restore, endOn, manyPort, statusEnded, recipientSet,
        resultInput: resultInput, spawnedRow: spawnedRow, equipmentPort: equipmentPort) with
        { EntityResolvers = Resolvers(id) }, logLevel);
    public void Plan(string plan, string provider, int steps = 1, bool amountPort = false, bool instigatorPort = false,
        bool effectPort = false, string? effect = null, bool resultInput = false)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, steps, null, null, amountPort, instigatorPort, effectPort, effect, resultInput: resultInput));
    /// <summary>A plan whose one action step declares the cost group the test wrote, as the wire JSON a compiler
    /// emits: the step pays the resources it names before anything of it is written. The duration block is
    /// independent of the charge, so a test that needs both writes both.</summary>
    public void CostPlan(string plan, string provider, string cost, bool effectPort = false, string? effect = null)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, 1, null, null, false, false, effectPort, effect, cost));
    /// <summary>A plan whose entry point declares a trigger option block, written as the JSON a compiler emits.</summary>
    public void Gated(string plan, string provider, string gate, bool amountPort = false, bool
instigatorPort = false)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, 1, null, gate, amountPort, instigatorPort));
    /// <summary>The same gated entry point mounted on a target that accepts an entity: what a per-instance trigger
    /// option books its counters against is the entity this mount matched.</summary>
    public void GatedOnSubject(string plan, string provider, string gate)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, 1, Fixture.SubjectAttachments, gate));
    /// <summary>An effect card that names the receiver's own event in `effect.end_on`. The event's trigger is a
    /// binding of the same module, and the card mounts no entry point on it: an ending is read from the dispatch
    /// itself, so nothing has to listen on the receiver's trigger for the effect to end.</summary>
    public void EndOnPlan(string plan, string provider, string effect, bool effectPort = true)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, 1, null, null, false, false, effectPort, effect));
    /// <summary>One event of a provider's fixture trigger about one entity (the provider's first by default),
    /// published the way a provider publishes: written by position.</summary>
    public DispatchResult Raise(RuntimeModuleHandle module, string id, string provider, long tick = 1, EntityReference? target = null)
        => Fixture.Raise(module, Fixture.Trigger(provider), id, World, tick, ("target", target ?? new EntityReference(provider + ":1", World, Life)));
}
static class Fixture
{
    /// <summary>Publishes one event of a module's own trigger: each named value is written at its output's position
    /// on the binding's sheet, as the kind its port declares. A refusal thrown before the publish is answered as
    /// the rejected dispatch a publish answers.</summary>
    internal static DispatchResult Raise(RuntimeModuleHandle module, string bindingId, string eventId, long world, long tick,
        params (string Port, object? Value)[] values)
    {
        try
        {
            var gate = module.SubscriptionGate(bindingId);
            var raised = module.BeginEvent(gate);
            var binding = gate.Binding!;
            foreach (var (port, value) in values)
            {
                var position = Array.IndexOf(binding.OutputIds, port);
                var cell = position < 0 ? -1 : binding.SheetCells[position];
                var kind = cell < 0 ? PortView.Kinds.Other : binding.Outputs[cell].Kind;
                var sheet = raised.Values;
                switch (value)
                {
                    case null: sheet.Null(position); break;
                    case EntityReference entity: sheet.Entity(position, entity); break;
                    case EntityReference[] entities: sheet.Entities(position, entities); break;
                    case string text: sheet.Text(position, text); break;
                    case bool flag: sheet.Boolean(position, flag); break;
                    case int or long or double when kind == PortView.Kinds.Integer: sheet.Integer(position, Convert.ToInt64(value)); break;
                    case int or long or double when kind == PortView.Kinds.Enum: sheet.Enum(position, Convert.ToInt32(value)); break;
                    case int or long or double: sheet.Number(position, Convert.ToDouble(value)); break;
                    default: throw new ArgumentException("No typed setter for " + value.GetType().Name, nameof(values));
                }
            }
            return raised.Raise(eventId, world, tick, "shared-scope");
        }
        catch (RuntimeContractException error) { return new DispatchResult("rejected", error.Code, eventId); }
    }
    public static readonly RuntimeIdentity Identity = new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457");
    public static readonly string[] Permissions = { "gtfo.player.health.write" };
    /// <summary>An enum port's frame carries the declaration index of its member set; the table itself is the SDK's,
    /// so the fixture reads it rather than restating the order and drifting from it.</summary>
    internal static int EnumSetIndex(string schema) => Array.IndexOf(RuntimeGraphContracts.EnumSetNames, schema);
    /// <summary>One of the shared port tables — handle kinds, handle lifetimes — read by name, so a fixture port that
    /// declares one writes the same dense index the runtime's own layout does instead of a restatement of the order.</summary>
    internal static int TableIndex(string table, string name) => Array.IndexOf(table switch
    {
        "HandleKinds" => Wire.HandleKinds,
        "HandleLifetimes" => Wire.HandleLifetimes,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown runtime value table.")
    }, name);
    public static string Trigger(string id) => id + ".binding.trigger";
    public static string TriggerCapability(string id) => id + ".trigger";
    /// <summary>A second entity port on the fixture trigger, so one event can carry two entities a subject-matching
    /// mount accepts: what a per-instance option has to refuse, because one activation is not one instance.</summary>
    public static readonly string ManyPort = "targets";
    /// <summary>The one input port a fixture action reads another step's result row through, declared only by a
    /// module a test asked for it on. It names the same result schema the row's own output does, so the two ports are
    /// one contract and the wiring is the rule every other port follows.</summary>
    public static readonly string RowInput = "catch";
    /// <summary>The second trigger a card's `effect.end_on` names: the receiver's own event, which is an ability of
    /// its own row rather than of the entry point that applied the effect.</summary>
    public static string EndTrigger(string id) => id + ".binding.ended";
    public static string EndTriggerCapability(string id) => id + ".ended";
    public static string ActionCapability(string id) => id + ".apply";
    public static string Handler(string id) => id + ".handler.apply";
    /// <summary>The port one effect handle is published through, spelled as the capability declares it: the same
    /// three facts a module hands the kernel's own cancel entry point when it stops an effect itself.</summary>
    public static JsonElement EffectPort() => RuntimeJson.Parse("""{"type":"handle","handleKind":"effect","lifetime":"entity_life"}""");
    /// <summary>The fixture action handler's own ports. A test that widens the fixture capability declares the
    /// extra names it made the handler read, so every shape in this file still describes its handler exactly.</summary>
    public static HandlerShape Shape(string id, IReadOnlyList<string>? inputs = null, IReadOnlyList<string>? parameters = null)
        => new HandlerShape().Inputs((inputs ?? new[] { "target" }).ToArray()).Outputs("result")
            .Parameters((parameters ?? new[] { "amount" }).ToArray());
    /// <summary>The fixture result schema's row: the four fixed columns in their fixed order, then the actual
    /// amount and the number of affected targets, exactly as the contract's result-row rule spells them.</summary>
    public static readonly object[] ResultFields = {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" },
        new { id = "amount", type = "number", unit = "hp" },
        new { id = "target_count", type = "integer" }
    };
    /// <summary>The canonical item-spawn row's fields, exactly as `forge.action.map.item_spawn` declares them: the
    /// four fixed columns and the nullable `spawned` entity the row reports. It is the shape a later entity column
    /// has, and the one a fixture needs to prove such a column keeps the identity the handler wrote.</summary>
    public static readonly object[] SpawnedFields = {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" },
        new { id = "spawned", type = "entity", nullable = true }
    };
    /// <summary>The row a step's cost is settled on: the four fixed columns, then the two amounts the charge writes
    /// after `code`, then the fixture's own values. The two columns are declared, which is what gives them an offset
    /// the settlement writes at; a row that declares none is a row no charge can report on.</summary>
    public static readonly object[] CostResultFields = {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" },
        new { id = "consumed", type = "integer" },
        new { id = "released", type = "integer" },
        new { id = "amount", type = "number", unit = "hp" },
        new { id = "target_count", type = "integer" }
    };
    /// <summary>Every fixture plan declares one mount target. A plan hangs on something, and a mount kind is only
    /// loadable while the provider that owns it has registered its matcher; the fixture provider in these files
    /// claims this kind.</summary>
    /// <summary>The mount kind the fixture plans declare and the provider that owns it. No kind belongs to the
    /// kernel any more, so a fixture that loads a plan has to stand its owner up.</summary>
    public static readonly string MountProvider = "example.mounts";
    public static readonly string MountReference = "fixture-level";
    /// <summary>Every fixture plan declares one mount target, written in the reference the mount owner answers.</summary>
    public static readonly object[] Attachments = { new { kind = "level", reference = MountReference } };
    /// <summary>The mount a per-instance trigger option needs: one that accepts an entity, so the plan has an
    /// instance to count.</summary>
    public static readonly object[] SubjectAttachments = { new { kind = "map-object", category = "fixture", reference = "any" } };
    /// <summary>The graph fixture's pure compare handler: two numbers in, one boolean out.</summary>
    public static HandlerShape CompareShape() => new HandlerShape().Inputs("left", "right").Outputs("value");
    public static BindingSupport[] Support(string id, IReadOnlyList<string>? permissions = null, bool endOn = false)
    {
        var support = new List<BindingSupport>
        {
            new(Trigger(id), "implementation-only", Array.Empty<string>()),
            new(id + ".binding.apply", "implementation-only", permissions ?? Permissions)
        };
        // The receiver's own event is a binding of this provider too, and a binding is supported or the module
        // does not register: it is declared only by the tests that give a card an `end_on` ability.
        if (endOn) support.Add(new BindingSupport(EndTrigger(id), "implementation-only", Array.Empty<string>()));
        return support.ToArray();
    }
    /// <summary>`amountPort` and `instigatorPort` widen the fixture trigger with the optional output ports a test
    /// needs the matched event to be able to carry: a number a trigger option accumulates, and the player a
    /// per-player option is charged against. Both are off by default because a declared port is part of the
    /// trigger's shape — an event may only carry the ports its capability declares — while a test that does not need
    /// one would otherwise have to carry a value for it in every event it publishes.</summary>
    public static RuntimeModule Module(string id, CommandHandler? handler = null, bool amountPort = false, bool instigatorPort = false,
        bool effectPort = false, EffectRestoreHandler? restore = null, bool endOn = false, bool manyPort = false,
        bool statusEnded = false, bool recipientSet = false, bool costRow = false, bool resultInput = false,
        bool spawnedRow = false, bool equipmentPort = false)
    {
        object Port(string name, string type) => new { id = name, type };
        var domains = new[] { "enemy", "weapon", "room", "map", "tool", "consumable", "logic" };
        object[] TriggerPorts()
        {
            // The application trigger's historical declared port type is retained by its existing compiled
            // fixtures. It is not an end_on subject unless a focused test explicitly declares one; the separate
            // receiver-event fixture uses the same ANY kind bound as the action's actual recipient input.
            var ports = new List<object> { Port("next", "execution"), new { id = "target", type = "entity", entityKinds = new[] { "gtfo.player" } } };
            if (instigatorPort) ports.Add(new { id = "instigator", type = "entity", optional = true });
            if (amountPort) ports.Add(new { id = "amount", type = "number", optional = true });
            if (equipmentPort) ports.Add(new { id = "equipment", type = "entity", entityKinds = new[] { EntityKinds.Equipment }, optional = true });
            // The optional set is a widener: most events of the same trigger carry one subject. A recipients port
            // that is required — every action row's own recipients input — can only be fed by a set the row may
            // rely on, which is what the second declaration is for.
            if (manyPort) ports.Add(new { id = ManyPort, type = "entity", cardinality = "many", optional = true });
            if (recipientSet) ports.Add(new { id = ManyPort, type = "entity", cardinality = "many" });
            return ports.ToArray();
        }
        // The action row's own ports. A test that asks for an effect handle gets the port the kernel publishes
        // into — and the recipients contract that names it — because a handle with no declared output would be a
        // duration nothing could ever hold. `spawnedRow` swaps the fixture's own columns for the canonical
        // item-spawn shape, whose later nullable entity column is the one a reader must see exactly as it was
        // written.
        object[] RowFields() => spawnedRow ? SpawnedFields : costRow ? CostResultFields : ResultFields;
        object[] ActionPorts(bool handle)
        {
            var ports = new List<object> { Port("next", "execution"), new { id = "result", type = "result", schema = "example.result.apply", fields = RowFields() } };
            if (handle) ports.Add(new { id = "effect", type = "handle", handleKind = "effect", lifetime = "entity_life" });
            return ports.ToArray();
        }
        object Recipients(bool handle) => handle
            ? new { input = "target", target = "entity", cardinality = "one", requires = new[] { "health.current" }, result = "result", handle = "effect" }
            : (object)new { input = "target", target = "entity", cardinality = "one", requires = new[] { "health.current" }, result = "result" };
        // The action row's own inputs. `resultInput` adds the one port a step reads another step's result row through:
        // it names the same result schema the row's own output declares, which is what makes the two ports one
        // contract, and it is optional because the step that writes the rows has none to read.
        object[] ActionInputs()
        {
            var ports = new List<object> { Port("in", "execution"), Port("target", "entity") };
            if (resultInput) ports.Add(new { id = RowInput, type = "result", schema = "example.result.apply", fields = RowFields(), optional = true });
            return ports.ToArray();
        }
        var json = RuntimeJson.From(new
        {
            providers = new[] { new { id, kind = id.StartsWith("forge.") ? "native" : "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = endOn
                ? new object[] { TriggerRow(id), EndTriggerRow(id), ActionRow(id, effectPort) }
                : new object[] { TriggerRow(id), ActionRow(id, effectPort) },
            bindings = endOn
                ? new[] { TriggerBinding(id), EndTriggerBinding(id), ActionBinding(id) }
                : new[] { TriggerBinding(id), ActionBinding(id) }
        });
        object TriggerRow(string owner) => new { id = TriggerCapability(owner), owner, kind = "trigger", label = "Observed event", version = "1.0.0", parameters = new { }, graph = new { domains = domains, execution = "host", inputs = Array.Empty<object>(), outputs = TriggerPorts(), parameters = Array.Empty<object>() } };
        // This fixture's receivers use the provider's example.* kind, not gtfo.player. Both the action recipient
        // and this explicitly selected subject are therefore ANY; separate tests exercise narrow kind coverage.
        object EndTriggerRow(string owner) => new { id = EndTriggerCapability(owner), owner, kind = "trigger", label = "Receiver event", version = "1.0.0", parameters = new { }, graph = new { domains = domains, execution = "host", inputs = Array.Empty<object>(), outputs = new object[] { Port("next", "execution"), Port("target", "entity") }, parameters = Array.Empty<object>(), subject = "target" } };
        object ActionRow(string owner, bool handle) => new { id = ActionCapability(owner), owner, kind = "action", label = "Effect", version = "1.0.0", parameters = new { }, graph = new { domains = domains, execution = "host", inputs = ActionInputs(), outputs = ActionPorts(handle), parameters = new[] { new { id = "amount", type = "number", role = "value", required = true, minimum = 1, maximum = 100 } }, recipients = Recipients(handle) } };
        object TriggerBinding(string owner) => new { id = Trigger(owner), capabilityId = TriggerCapability(owner), providerId = owner, handler = owner + ".handler.trigger", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
        object EndTriggerBinding(string owner) => new { id = EndTrigger(owner), capabilityId = EndTriggerCapability(owner), providerId = owner, handler = owner + ".handler.ended", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
        object ActionBinding(string owner) => new { id = owner + ".binding.apply", capabilityId = ActionCapability(owner), providerId = owner, handler = Handler(owner), role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
        if (statusEnded)
        {
            // A provider that carries the kernel's own status-ended row: the entry point a plan mounts on it is how
            // a test sees the fact, and the binding is the provider's own subscription to that one row — the same
            // binding id the fixture trigger always uses, so the fixture's plan builder reaches it unchanged.
            var watcherJson = RuntimeJson.From(new
            {
                providers = new[] { new { id, kind = id.StartsWith("forge.") ? "native" : "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = new object[] { StatusEndedRow(id), WatcherRow(id) },
                bindings = new[] { StatusEndedBinding(id), WatcherBinding(id) }
            });
            return new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeApi.Fingerprint, ForgeEngine.Testing.TestProviders.Declaration(watcherJson.GetRawText(), Support(id), shapes: new Dictionary<string, HandlerShape>
            {
                [Handler(id)] = new HandlerShape().Inputs("target", "status", "reason").Outputs("result").Parameters("amount")
            }), new Dictionary<string, CommandHandler> { [Handler(id)] = handler ?? (_ => CommandResult.Succeeded(RuntimeJson.EmptyObject)) })
            {
                EffectRestores = restore == null ? null : new Dictionary<string, EffectRestoreHandler> { [Handler(id)] = restore }
            };
        }
        return new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeApi.Fingerprint, ForgeEngine.Testing.TestProviders.Declaration(json.GetRawText(), Support(id, endOn: endOn), shapes: new Dictionary<string, HandlerShape> { [Handler(id)] = Shape(id, resultInput ? new[] { "target", RowInput } : null) }), new Dictionary<string, CommandHandler> { [Handler(id)] = handler ?? (_ => CommandResult.Succeeded(RuntimeJson.EmptyObject)) })
        {
            // One restore callback per handler name, exactly as the handler table is keyed: the fixture's effect
            // row is only loadable when a test registered the callback that ends it.
            EffectRestores = restore == null ? null : new Dictionary<string, EffectRestoreHandler> { [Handler(id)] = restore }
        };
        // The row the kernel answers for itself, declared here with the shape the kernel reads off it, so a test
        // drives the production dispatch path rather than a private one.
        object StatusEndedRow(string owner) => new { id = StatusContract.EndedCapability, owner, kind = "trigger", label = "Status ended", version = "1.0.0", parameters = new { }, graph = new { domains = domains, execution = "host", inputs = Array.Empty<object>(), outputs = new object[] { Port("next", "execution"), new { id = "target", type = "entity", entityKinds = new[] { "gtfo.player" } }, new { id = "status", type = "enum", schema = "status_kind" }, new { id = "reason", type = "enum", schema = "status_end_reason" } }, parameters = Array.Empty<object>() } };
        // The subscription to that row: one binding per provider that hears about endings, declared in the role a
        // trigger always takes. No handler is registered for it because a trigger is not a command — what runs is
        // the plan an author mounted on it, exactly as for every other observed event.
        object StatusEndedBinding(string owner) => new { id = Trigger(owner), capabilityId = StatusContract.EndedCapability, providerId = owner, handler = owner + ".handler.ended", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
        // The card such a provider mounts: one that reads the whole fact — the receiver, the status kind and the
        // reason — so a test sees what the kernel published rather than only that something ran.
        object WatcherRow(string owner) => new { id = ActionCapability(owner), owner, kind = "action", label = "Status watcher", version = "1.0.0", parameters = new { }, graph = new { domains = domains, execution = "host", inputs = new object[] { Port("in", "execution"), Port("target", "entity"), new { id = "status", type = "enum", schema = "status_kind" }, new { id = "reason", type = "enum", schema = "status_end_reason" } }, outputs = ActionPorts(effectPort), parameters = new[] { new { id = "amount", type = "number", role = "value", required = true, minimum = 1, maximum = 100 } }, recipients = Recipients(effectPort) } };
        object WatcherBinding(string owner) => new { id = owner + ".binding.apply", capabilityId = ActionCapability(owner), providerId = owner, handler = Handler(owner), role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
    }
    /// <summary>The provider that owns the fixture plans' own mount kind. It declares a scope matcher — no event
    /// subject is needed — and answers only the references it was given.</summary>
    public static RuntimeModule MountOwner() => MountOwner(new[] { MountReference });

    /// <summary>The same owner carrying both mount kinds the fixture's plans use: the scope matcher above, and a
    /// subject matcher that accepts every entity a trigger payload carries. A per-instance trigger option is booked
    /// against exactly that accepted entity, so it is the kind a plan scope `instance` has to mount on.</summary>
    public static RuntimeModule MountOwnerWithSubjects() => MountOwner() with
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == MountReference),
            ["map-object"] = AttachmentMatcherRegistration.BySubject((category, reference, subject) => !string.IsNullOrEmpty(subject.Id))
        }
    };

    /// <summary>The same owner scripted with another set of levels: a fixture that loads plans from a foreign
    /// source names the levels those plans mount on, so the double answers exactly those and nothing else.</summary>
    public static RuntimeModule MountOwner(IReadOnlyCollection<string> references) => new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeApi.Fingerprint, ForgeEngine.Testing.TestProviders.Declaration(RuntimeJson.From(new
    {
        providers = new[] { new { id = MountProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(),
        bindings = Array.Empty<object>()
    }).GetRawText(), Array.Empty<BindingSupport>()), new Dictionary<string, CommandHandler>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                category == null && references.Contains(reference))
        }
    };
    /// <summary>Reads the TypeScript compiler's checked-in plan for this exact contract and option set.</summary>
    public static string Plan(RuntimeKernel kernel, string planId, string provider, int stepCount = 1, object[]? attachments = null, string? gate = null, bool amountPort = false, bool instigatorPort = false, bool effectPort = false, string? effect = null, string? cost = null, bool resultInput = false)
    {
        if (planId is "downstream" or "downstream-delay" or "downstream-wait" or "malformed" or "unwritten" or "spawned" or "spawned-null" or "ended" or "kept")
            return TestRuntime.ReadPlan("framework-completion", planId);

        var manifest = RuntimeJson.Parse(kernel.ExportManifest());
        var registry = manifest.GetProperty("registry");
        var providerBindings = registry.GetProperty("bindings").EnumerateArray()
            .Where(binding => binding.GetProperty("providerId").GetString() == provider).ToArray();
        var bindingIds = providerBindings.Select(binding => binding.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var capabilityIds = providerBindings.Select(binding => binding.GetProperty("capabilityId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var arguments = new
        {
            planId,
            provider,
            stepCount,
            attachments = attachments ?? Attachments,
            gate = gate is null ? null : JsonNode.Parse(gate),
            effect = effect is null ? null : JsonNode.Parse(effect),
            cost = cost is null ? null : JsonNode.Parse(cost),
            resultInput,
            limits = new
            {
                maxEventsPerTick = kernel.Limits.MaxEventsPerTick,
                maxCommandsPerTick = kernel.Limits.MaxCommandsPerTick,
                maxQueuedEvents = kernel.Limits.MaxQueuedEvents,
                maxCausalDepth = kernel.Limits.MaxCausalDepth
            },
            contract = new
            {
                provider = registry.GetProperty("providers").EnumerateArray()
                    .Single(row => row.GetProperty("id").GetString() == provider),
                capabilities = registry.GetProperty("capabilities").EnumerateArray()
                    .Where(row => capabilityIds.Contains(row.GetProperty("id").GetString()!)).ToArray(),
                bindings = providerBindings,
                bindingSupport = manifest.GetProperty("bindingSupport").EnumerateArray()
                    .Where(row => bindingIds.Contains(row.GetProperty("bindingId").GetString()!)).ToArray()
            }
        };
        var key = TestRuntime.VariantKey(arguments);
        var compiled = TestRuntime.ReadPlan("framework-program", key);
        if (gate == null && effect == null && cost == null) return compiled;

        // Rejection cases keep their one malformed option as a precise mutation of a legal TS baseline. A valid
        // option is already in the compiler output and never passes through this test-only mutation path.
        var compiledDocument = JsonNode.Parse(compiled)!;
        var entrypoint = compiledDocument["entrypoints"]![0]!;
        var step = entrypoint["steps"]![0]!;
        if (gate != null && !Same(entrypoint["gate"], gate)) entrypoint["gate"] = JsonNode.Parse(gate);
        if (effect != null && !Same(step["effect"], effect)) step["effect"] = JsonNode.Parse(effect);
        if (cost != null)
            foreach (var field in JsonNode.Parse(cost)!.AsObject())
                if (!Same(step[field.Key], field.Value!.ToJsonString()))
                    step[field.Key] = JsonNode.Parse(field.Value!.ToJsonString());
        return compiledDocument.ToJsonString();

        static bool Same(JsonNode? actual, string expected)
            => actual != null && RuntimeJson.StableText(RuntimeJson.Parse(actual.ToJsonString()))
                == RuntimeJson.StableText(RuntimeJson.Parse(expected));
    }
}
