using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of `forge.action.session.fail_rule`: the ledger of the failure policies this provider has
/// recorded for the world being played, the one handler that writes it, and the decision the game's own failure
/// check is handed back.
///
/// <b>Why a ledger and not a native write.</b> The game's failure verdict is immediate — it is recomputed from the
/// living players on every frame and there is no writable rule state anywhere in the game — so a rule cannot be
/// set on the game. It is held here and applied where the game decides: <see cref="Apply"/> is called from the
/// postfix on `WardenObjectiveManager.CheckExpeditionFailed`, the static method `GS_InLevel.Update` calls every
/// frame, and it hands the verdict back. The failure itself therefore still runs through the game's own path — the
/// countdown and the session command the same body already makes — and nothing here ends a level.
///
/// <b>The shape of the frame path.</b> The check runs on every frame of the level, so the no-rule case is the
/// first field this half reads: <see cref="Apply"/> returns on a single false <see cref="Ledger.Active"/> before
/// it touches a subject, and neither path allocates. Work only happens while a rule is in force, which is the
/// author's own choice.
///
/// <b>The ledger's life.</b> It is one entry per policy plus the world epoch it was written in; the half holds a
/// lifecycle subscription on the Map registration and clears the whole ledger when the world changes, so a rule of
/// a level that is gone cannot answer for the level standing now. The subscription is also why the half belongs to
/// the session: the hook and the read both have to reach the same table.
/// </summary>
internal sealed class FailRuleActions : IDisposable
{
    private readonly Func<long> _world;
    private readonly Func<EntityReference, RuntimePlayerLife?> _lives;
    private readonly Action<string> _report;
    private readonly Action<string> _note;
    private Ledger _ledger;
    private RuntimeLifecycleSubscription? _lifecycle;
    private DomainStateChannel.NativeStateSource? _state;
    private bool _disposed;

    internal FailRuleActions(RuntimeModuleHandle registration, IForgeEngine kernel,
        Func<EntityReference, RuntimePlayerLife?> lives, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(kernel);
        _world = () => kernel.WorldEpoch;
        _lives = lives ?? throw new ArgumentNullException(nameof(lives));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _note = Report;
        _ledger = new Ledger();
        // The rule belongs to the world it was written in and to nothing after it. A world change and a session
        // teardown both clear the table; the kernel's own epoch already invalidates the events, this releases the
        // entries of a level that no longer exists.
        _state = DomainStateChannel.RegisterNativeState(kernel, "forge.map.fail-rule", Capture, Restore);
        try
        {
            _lifecycle = registration.ObserveLifecycle(value =>
            {
                if (value.IsMigration && value.PreviousWorldEpoch is { } from) { _ledger.Follow(from, value.Current.WorldEpoch); return; }
                if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped
                    || value.Kind == RuntimeLifecycleKind.WorldChanged && _ledger.World != value.Current.WorldEpoch)
                    BeginWorld();
            });
        }
        catch { _state.Dispose(); _state = null; throw; }
    }

    /// <summary>Whether any rule of the current world changes the game's verdict. The hook reads this before it
    /// reads anything else.</summary>
    internal bool Active => _ledger.Active;

    // ------------------------------------------------------------------ the handler

    /// <summary>One `forge.action.session.fail_rule` request: the policy it names, the switch, and the subjects
    /// the policy is declared for. Every subject is checked before the ledger is touched, so a request that cannot
    /// be served as asked writes nothing at all.</summary>
    internal CommandResult Handle(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsHost) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string? policy = context.Text(FailRuleContract.Policy);
        if (policy == null || Array.IndexOf(FailRuleContract.Policies, policy) < 0)
            return Outcome.Rejected(ResultCode.FailRulePolicyUnknown);
        int count = context.Count(FailRuleContract.Subject);
        if (count < 0) return Outcome.Rejected(ResultCode.MissingField);
        if (count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        var targets = new EntityReference[count];
        for (int index = 0; index < count; index++)
            if (!context.TryEntity(FailRuleContract.Subject, index, out targets[index]))
                return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (context.Boolean(FailRuleContract.Enabled) is not { } enabled)
            return Outcome.Rejected(ResultCode.FailRuleEnabledRequired);
        foreach (var target in targets)
        {
            if (!EntityKinds.Is(target, EntityKinds.Player))
                return Outcome.Rejected(ResultCode.UnsupportedRecipient);
            if (target.WorldEpoch != context.WorldEpoch)
                return Outcome.Rejected(ResultCode.StaleEntity);
            if (_lives(target) == null) return Outcome.Rejected(ResultCode.FailRuleSubjectMissing);
        }

        // The verdict hook goes on with the first rule; a level that never writes one does not carry it.
        if (MapLazyHooks.Ensure(MapLazyHooks.Group.FailRules) != null)
            return Outcome.Rejected(ResultCode.FailRuleUnavailable);
        _ledger.Record(context.WorldEpoch, policy, enabled, targets);
        _state?.Changed();
        // One row per subject, in the recipient set's own order: the shared success pairing and the one code that
        // says what was recorded.
        var rows = context.Rows;
        for (int index = 0; index < targets.Length; index++)
            rows.Begin(index, targets[index]).Integer(Blocks.ActionSessionFailRule.RowTargetCount, targets.Length)
                .End(Status.Succeeded, Commit.Confirmed, ResultCode.FailRuleSet);
        return rows.Conclude();
    }

    /// <summary>The ledger's own readback: whether the policy is recorded for the given world, or null when this
    /// world has no rule for it. The world is compared here as well as cleared by
    /// the lifecycle, so a read that races a world change answers nothing rather than the old world's rule.</summary>
    internal bool? InForce(string policy, long worldEpoch) => _ledger.InForce(policy, worldEpoch);

    internal JsonElement Capture() => _ledger.Capture(_world());

    internal void Restore(JsonElement data)
    {
        Shape(data, "version", "worldEpoch", "policies");
        if (data.GetProperty("version").ValueKind != JsonValueKind.String
            || data.GetProperty("version").GetString() != "1.0.0")
            throw new FormatException("fail-rule-state-version");
        long world = Epoch(data.GetProperty("worldEpoch"));
        if (world != _world()) throw new FormatException("fail-rule-state-world");
        var policies = data.GetProperty("policies");
        if (policies.ValueKind != JsonValueKind.Array || policies.GetArrayLength() > FailRuleContract.Policies.Length)
            throw new FormatException("fail-rule-state-policies");
        var restored = new Ledger(world);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in policies.EnumerateArray())
        {
            Shape(row, "policy", "enabled", "subjects");
            var policyValue = row.GetProperty("policy");
            string? policy = policyValue.ValueKind == JsonValueKind.String ? policyValue.GetString() : null;
            if (policy == null || Array.IndexOf(FailRuleContract.Policies, policy) < 0 || !seen.Add(policy))
                throw new FormatException("fail-rule-state-policy");
            var enabled = row.GetProperty("enabled");
            if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("fail-rule-state-enabled");
            var subjects = row.GetProperty("subjects");
            if (subjects.ValueKind != JsonValueKind.Array || subjects.GetArrayLength() is < 1 or > CommandResult.MaximumFacts)
                throw new FormatException("fail-rule-state-subjects");
            var targets = new EntityReference[subjects.GetArrayLength()];
            var uniqueTargets = new HashSet<EntityReference>();
            for (int index = 0; index < targets.Length; index++)
            {
                var subject = subjects[index];
                Shape(subject, "id", "worldEpoch", "lifeEpoch");
                EntityReference target;
                try { target = RuntimeJson.Entity(subject); }
                catch (Exception error) when (error is RuntimeContractException or InvalidOperationException or FormatException)
                { throw new FormatException("fail-rule-state-subject", error); }
                if (!EntityKinds.Is(target, EntityKinds.Player) || target.Id.Length <= EntityKinds.Player.Length + 1
                    || !uniqueTargets.Add(target))
                    throw new FormatException("fail-rule-state-subject");
                if (target.WorldEpoch != world) throw new FormatException("fail-rule-state-subject-world");
                targets[index] = target;
            }
            restored.Record(world, policy, enabled.GetBoolean(), targets);
        }
        // The whole document was validated before touching the active ledger. Restoring is receipt replacement,
        // never a native verdict call or a new host write to broadcast back to the sender.
        if (restored.Active && MapLazyHooks.Ensure(MapLazyHooks.Group.FailRules) is { } refusal)
            throw new FormatException("fail-rule-hook-unavailable: " + refusal);
        _ledger = restored;
    }

    private static void Shape(JsonElement data, params string[] fields)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("fail-rule-state-object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in data.EnumerateObject())
            if (Array.IndexOf(fields, field.Name) < 0 || !seen.Add(field.Name))
                throw new FormatException("fail-rule-state-field");
        if (seen.Count != fields.Length) throw new FormatException("fail-rule-state-field");
    }

    private static long Epoch(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long epoch)
            || epoch < 0 || epoch > RuntimeJson.MaxSafeInteger)
            throw new FormatException("fail-rule-state-epoch");
        return epoch;
    }

    // ------------------------------------------------------------ the game's verdict

    /// <summary>Applies the recorded rules to the game's own verdict. The first statement is the whole no-rule
    /// path: one field, no allocation, no subject read. The forced policies are asked first because "this counts
    /// as a failure" is an addition to the game's verdict; suppressing the wipe only clears a verdict nothing else
    /// forced.</summary>
    internal void Apply(ref bool failed)
    {
        if (!_ledger.Active) return;
        // The lifecycle clears the table on a world change; this is the second half of the same rule, so a frame
        // that runs before the notification is not answered from the level that is gone.
        if (_ledger.World != _world()) { BeginWorld(); return; }
        if (_ledger.AnyPlayerForces(_lives)) { failed = true; return; }
        if (_ledger.GroupForces(_lives, _note)) { failed = true; return; }
        if (_ledger.TeamWipeSuppressed) failed = false;
    }

    // ------------------------------------------------------------------ the ledger

    /// <summary>One policy's recorded rule: whether it was written in this world, which side of the switch it was
    /// written with, and the subjects it was declared for.</summary>
    private sealed class Entry
    {
        internal bool Recorded;
        internal bool Enabled;
        internal EntityReference[] Subjects = Array.Empty<EntityReference>();
        internal string[] MissingNotes = Array.Empty<string>();
        internal bool[] MissingReported = Array.Empty<bool>();
    }

    /// <summary>The per-world table of the three policies. Only the write path allocates; the frame path reads
    /// <see cref="Active"/> and then walks arrays that were built when the rule was written.</summary>
    private sealed class Ledger
    {
        private readonly Entry[] _entries;
        private readonly Entry _teamWipe = new();
        private readonly Entry _anyPlayerDown = new();
        private readonly Entry _boundGroupWiped = new();

        internal Ledger(long world = -1)
        {
            World = world;
            _entries = new[] { _teamWipe, _anyPlayerDown, _boundGroupWiped };
        }

        internal long World { get; private set; } = -1;
        internal bool Active { get; private set; }

        /// <summary>Whether `team-wipe` is recorded as off: the one rule that removes a native verdict.</summary>
        internal bool TeamWipeSuppressed => _teamWipe is { Recorded: true, Enabled: false };

        private Entry? EntryOf(string policy) => policy switch
        {
            FailRuleContract.TeamWipePolicy => _teamWipe,
            FailRuleContract.AnyPlayerDownPolicy => _anyPlayerDown,
            FailRuleContract.BoundGroupWipedPolicy => _boundGroupWiped,
            _ => null
        };

        internal JsonElement Capture(long world) => RuntimeJson.From(new
        {
            version = "1.0.0",
            worldEpoch = world,
            policies = FailRuleContract.Policies.Where(policy => World == world && EntryOf(policy)!.Recorded)
                .Select(policy => new { policy, enabled = EntryOf(policy)!.Enabled, subjects = EntryOf(policy)!.Subjects }).ToArray()
        });

        /// <summary>A host migration keeps the level and what the rules recorded in it: they move to the new world number.</summary>
        internal void Follow(long from, long to)
        {
            if (World != from) return;
            World = to;
            foreach (var entry in _entries)
                for (var index = 0; index < entry.Subjects.Length; index++)
                    entry.Subjects[index] = new EntityReference(entry.Subjects[index].Id, to, entry.Subjects[index].LifeEpoch);
        }

        internal void BeginWorld()
        {
            // Nothing was recorded since the last reset: every entry is already clear.
            if (World == -1 && !Active) return;
            foreach (var entry in _entries)
            {
                entry.Recorded = false; entry.Enabled = false; entry.Subjects = Array.Empty<EntityReference>();
                entry.MissingNotes = Array.Empty<string>(); entry.MissingReported = Array.Empty<bool>();
            }
            World = -1;
            Active = false;
        }

        internal void Record(long world, string policy, bool enabled, EntityReference[] subjects)
        {
            if (World != world) BeginWorld();
            World = world;
            var entry = EntryOf(policy) ?? throw new InvalidOperationException(ResultCode.FailRulePolicyUnknown);
            entry.Recorded = true;
            entry.Enabled = enabled;
            entry.Subjects = subjects;
            if (ReferenceEquals(entry, _boundGroupWiped))
            {
                entry.MissingNotes = subjects.Select(subject => "map.fail-rule: the bound group has a player this world no longer holds ("
                    + subject.Id + "); the group-wipe rule forced nothing").ToArray();
                entry.MissingReported = new bool[subjects.Length];
            }
            Recompute();
        }

        internal bool? InForce(string policy, long worldEpoch)
        {
            if (World != worldEpoch || EntryOf(policy) is not { Recorded: true } entry) return null;
            return entry.Enabled;
        }

        private void Recompute()
        {
            Active = TeamWipeSuppressed || Forces(_anyPlayerDown) || Forces(_boundGroupWiped);
        }

        /// <summary>`any-player-down`: one named player down is the failure.</summary>
        internal bool AnyPlayerForces(Func<EntityReference, RuntimePlayerLife?> lives)
        {
            var entry = _anyPlayerDown;
            if (!Forces(entry)) return false;
            foreach (var subject in entry.Subjects)
                if (lives(subject) is { } life && Down(life)) return true;
            return false;
        }

        /// <summary>`bound-group-wiped`: every named player of the group down is the failure. A subject this
        /// process can no longer read is not a player who is down, so the rule waits and says so once rather than
        /// failing the level from a life that is not there.</summary>
        internal bool GroupForces(Func<EntityReference, RuntimePlayerLife?> lives, Action<string> note)
        {
            var entry = _boundGroupWiped;
            if (!Forces(entry)) return false;
            for (int index = 0; index < entry.Subjects.Length; index++)
            {
                var subject = entry.Subjects[index];
                if (lives(subject) is not { } life)
                {
                    if (!entry.MissingReported[index])
                    {
                        entry.MissingReported[index] = true;
                        note(entry.MissingNotes[index]);
                    }
                    return false;
                }
                if (!Down(life)) return false;
            }
            return true;
        }

        private static bool Forces(Entry entry)
            => entry.Recorded && entry.Enabled && entry.Subjects.Length != 0;

        /// <summary>What "down" is for a forced rule: the locomotion machine's own downed state, or a life that is
        /// no longer alive. A dead player cannot be revived, so a wipe that treated death as standing would clear a
        /// level the team has already lost.</summary>
        private static bool Down(RuntimePlayerLife life) => life.Downed || !life.Alive;
    }

    // ------------------------------------------------------------------ plumbing

    internal void BeginWorld()
    {
        _ledger.BeginWorld();
    }

    private void Report(string message)
    {
        _report(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _state?.Dispose();
        _state = null;
        _lifecycle?.Dispose();
        _lifecycle = null;
        BeginWorld();
        _disposed = true;
    }
}

/// <summary>The game's own failure decision, handed to the ledger. `GS_InLevel.Update` calls this static method
/// every frame of the level and branches on its answer; the subscriber takes the answer back after the body, so the
/// level's own countdown and session command still run with the rule applied. ForgeCore patches the method once
/// (`GameHooks.ExpeditionFailedChecked`); the subscription is made the first time authored rules need it
/// (<see cref="MapLazyHooks"/>), so a level without rules never pays for the per-frame call.</summary>
internal static class ExpeditionFailedDecision
{
    private static bool _subscribed;

    internal static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        ForgeCore.GameHooks.ExpeditionFailedChecked.After(static (call, _) => Decided(call));
    }

    private static void Decided(ForgeCore.ExpeditionFailedCheck call)
    {
        try
        {
            var session = MapHost.Session;
            if (session == null) return;
            // The native decision has already completed. Session admission (including its thread and
            // runtime reads) belongs inside this boundary too; publish only a fully observed rule result.
            bool decision = call.Result;
            session.ApplyFailRules(ref decision);
            call.Result = decision;
        }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.InstanceRefused,
                "Native expedition failure decision was kept because authored rule observation failed.",
                RuntimeLogValue.Quote("exception", error is RuntimeContractException refusal
                    ? refusal.Code : error.GetType().Name));
        }
    }
}
