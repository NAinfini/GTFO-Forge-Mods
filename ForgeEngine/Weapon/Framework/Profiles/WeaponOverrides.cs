using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>How one named value is changed. The operation travels with the field and not with the request,
/// because ruling 84 merges field by field: a later writer replaces the whole field, and with it the operation
/// it asked for. `set` writes the value; `add` and `subtract` resolve against what the instance's own block held
/// before this package cloned it, so replaying a request onto a rebuilt block means the same thing the first
/// write meant. `multiply`, `minimum` and `maximum` resolve the same way: a multiplier scales the instance's own
/// value, and the two comparisons keep the smaller or the larger of the instance's value and the request's.</summary>
internal enum WeaponOverrideOperation
{
    Set,
    Add,
    Subtract,
    Multiply,
    Minimum,
    Maximum
}

/// <summary>The units a request can contribute to the same native destination.</summary>
internal enum WeaponOverrideUnit
{
    Native,
    ShotsPerSecond
}

/// <summary>One named value an override asks to change on one equipment instance. The name is from
/// <see cref="WeaponOverrideLedger.Fields"/> and nothing else: a name outside that set has no native write point
/// and is refused with <see cref="ResultCode.OverrideFieldUnknown"/> rather than written somewhere close.
/// Values use the native member's unit unless a frequency action explicitly declares shots per second. The
/// field name remains the shared merge identity, so a profile and a rate action replace the same destination.
/// The value is a plain double here because this half is game-independent — the native half is the only place
/// that knows whether a given field is a float, an int or a nested range, and it is also the only place that can
/// resolve an `add` or a `subtract`, because only it holds the instance's original block.</summary>
internal sealed record WeaponOverrideField(string Name, double Value,
    WeaponOverrideOperation Operation = WeaponOverrideOperation.Set,
    WeaponOverrideUnit Unit = WeaponOverrideUnit.Native);

/// <summary>Where one override came from: the identity of the writing node, `(plan, node, sequence)`, which is what
/// ends the writer's own fields later. `Sequence` is the caller's own counter for a behaviour that fires more than
/// once. Several overrides on one instance merge by arrival: the last request wins the fields it names.</summary>
internal sealed record WeaponOverrideSource(string PlanId, string NodeId, long Sequence);

/// <summary>One request to change named values on one equipment instance for a bounded time. `DurationTicks`
/// of zero or less means the override lasts until the equipment life ends; a positive value is turned into an
/// absolute expiry by the ledger when the request is accepted, because a relative duration is only meaningful
/// once there is a tick to measure it from.</summary>
internal sealed record WeaponOverrideRequest(EntityReference Equipment, IReadOnlyList<WeaponOverrideField> Fields,
    WeaponOverrideSource Source, long DurationTicks);

/// <summary>What the ledger currently holds for one equipment instance: the merged fields, the identity of the
/// writer that last set each one, and when each writer expires. `writers` is kept per field rather than per
/// entry so a later request can overwrite one value without discarding the others, which is the only merge order
/// ruling 84 allows — one order, the order of arrival, with no second priority table.</summary>
internal sealed class WeaponOverrideState
{
    internal WeaponOverrideState(EntityReference equipment)
    {
        Equipment = equipment;
        mutable = new List<WeaponOverrideField>();
        writers = new Dictionary<string, WeaponOverrideSource>(StringComparer.Ordinal);
    }

    // The merged field set and its per-field writers are the ledger's own working state, not a second public
    // view: the properties below are copies a caller can read without being able to write into the table the
    // ledger is deciding from.
    internal readonly List<WeaponOverrideField> mutable;
    internal readonly Dictionary<string, WeaponOverrideSource> writers;
    internal readonly Dictionary<WeaponOverrideSource, long> expiries = new();

    /// <summary>The instance this entry is about, as the equipment life's own reference. An override never
    /// invents a reference: a caller that has no current one has nothing to override.</summary>
    public EntityReference Equipment { get; }
    /// <summary>The accepted fields, in the order they were first set. One entry per name.</summary>
    public IReadOnlyList<WeaponOverrideField> Fields => mutable;
    /// <summary>The earliest finite deadline among surviving writers, or 0 when all last until life end.</summary>
    public long ExpiryTick { get; internal set; }

    internal void RefreshExpiry()
    {
        foreach (var writer in expiries.Keys.ToArray())
            if (!writers.ContainsValue(writer)) expiries.Remove(writer);
        ExpiryTick = 0;
        foreach (var tick in expiries.Values)
            if (tick > 0 && (ExpiryTick == 0 || tick < ExpiryTick)) ExpiryTick = tick;
    }
    /// <summary>The source that last wrote each field, keyed by field name.</summary>
    public IReadOnlyDictionary<string, WeaponOverrideSource> Writers => writers;

    /// <summary>The value of one field, or null when this entry does not set it. A caller that needs to combine a
    /// request with what is already applied reads the difference from here instead of guessing a default.</summary>
    public double? Value(string name)
    {
        foreach (var field in mutable)
            if (string.Equals(field.Name, name, StringComparison.Ordinal)) return field.Value;
        return null;
    }
}

/// <summary>What one native write did, which is not a yes-or-no question. `Refused` is a request nothing was
/// written for because the request itself was not acceptable: the instance is exactly as it was found. `Failed` is
/// a write that failed before any native value changed — a candidate that could not be built or a field that could
/// not be copied — so the instance is also exactly as it was found, but the cause is a failure rather than a
/// refusal. `Applied` is a request whose swaps were written and read back off the instance. `Unconfirmed` is a
/// request whose native writes were attempted and could not be read back: whether the instance carries them is not
/// observable from the applier, so it is neither a failure nor a success and no caller may report it as one. The
/// applier has already rolled the instance back to the state it last confirmed and dropped its entry when it
/// answers `Unconfirmed`, or kept that entry reachable when even the rollback could not be read back — either way
/// nothing is left that only this write could reach.</summary>
internal enum WeaponOverrideWrite
{
    Refused,
    Failed,
    Applied,
    Unconfirmed
}

/// <summary>The write-back the native half supplies so a revert can be decided in one place. The ledger owns the
/// decision — what is applied, what expired, which world an entry belongs to — and the applier owns the native
/// objects, so the ledger calls this with a reference and never with a block.
///
/// A revert is asked for on three occasions and no more: one equipment life ending, one world epoch ending, and
/// one entry reaching its expiry tick. The applier restores the equipment's own block from its clone; a
/// reference the applier no longer holds is not an error, because an equipment life that already ended took its
/// clone with it.</summary>
internal interface IWeaponOverrideSink
{
    /// <summary>Writes the merged fields of one accepted request onto the named instance and answers what that
    /// write did. `Failed` and `Unconfirmed` are both "the ledger records nothing for this request": the first
    /// proves the instance was not touched, the second does not. The applier itself is responsible for leaving no
    /// entry and no clone behind, or for keeping an entry reachable when it could not put the instance back.</summary>
    WeaponOverrideWrite Apply(EntityReference equipment, IReadOnlyList<WeaponOverrideField> fields, out string code);
    /// <summary>Restores the instance's own block. Idempotent: restoring an instance that carries no override is
    /// a success with nothing to do, so a second revert of the same reference never fails a caller.</summary>
    bool Restore(EntityReference equipment, out string code);
    /// <summary>Restores every instance this machine has overridden. Returns how many it restored, so a world
    /// boundary can report whether anything was actually written back.</summary>
    int RestoreAll();
}

/// <summary>The ledger's own answer to one request: whether it recorded the request, refused it, reached a native
/// write that failed before touching the instance, or reached one whose outcome nobody could read. The last two
/// states exist because a request the native half did not apply must not be reported as a refusal: a proven
/// pre-write failure is `failed`+`none` at the row, and a write nobody could confirm is `failed`+`unknown`.
/// Mapping either to a refusal would claim the instance was left alone, and mapping either to a success would
/// claim a change nobody observed.</summary>
internal enum WeaponOverrideCommit
{
    Refused,
    Failed,
    Applied,
    Unconfirmed
}

/// <summary>
/// The instance-override ledger: which equipment instance carries which changed values, who wrote them, and when
/// they stop being true. This is ruling 84's instance-level block replacement, decided here and written by the
/// native half.
///
/// What this file deliberately does not do: it never reads or writes an `ArchetypeDataBlock`, never touches the
/// gear pool, and never keeps a second copy of the game's own state. The field names are the one vocabulary the
/// native write points implement (see <see cref="Fields"/> for each name's destination and the evidence file for
/// the offsets), and the ledger holds only what a plan asked for.
///
/// The relationship with the gear-loadout session is a one-way ordering rule and not a shared data structure:
/// loadout narrows the gear pool before an instance exists, an override changes the block of an instance that
/// already exists, and an override never modifies the shared block or the pool array. There is therefore no
/// arbitration between the two — "pool first, then instance" is the whole of the rule.
/// </summary>
internal sealed class WeaponOverrideLedger
{
    /// <summary>The most fields one request may carry. A request that names more is refused rather than
    /// truncated: a caller that asked for seventeen changes did not ask for sixteen of them.</summary>
    public const int MaximumOverrideFieldsPerRequest = 16;
    /// <summary>The most equipment instances one world may override at once. The count is of instances, not of
    /// requests, because the cost being bounded is the number of cloned blocks alive.</summary>
    public const int MaximumOverriddenEquipmentPerWorld = 64;

    /// <summary>
    /// The field vocabulary, in the order the design lists it, each name against the native value it writes.
    /// Every destination is read per shot or per frame by the shipped code, which is what makes an instance
    /// clone effective at all; the evidence file carries the offsets and the read sites.
    ///
    /// Three names are additions to the design's own list rather than restatements of it: `burst_count` is the
    /// burst length the applier writes onto the burst archetypes, and the two spread scales are the movement and
    /// aim multipliers of the `spread` row. One name from the design's list is deliberately absent: no value
    /// behind `spread_pattern` is written by the shipped code, so declaring it would advertise a write point the
    /// game does not have.
    /// </summary>
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        "fire_rate", "burst_count", "spread_cone", "spread_movement_scale", "spread_aim_scale",
        "recoil_horizontal", "recoil_vertical", "recoil_recovery", "recoil_camera_kick", "reload_time", "melee_charge_time"
    };

    /// <summary>The operations the generic `property` row's structural parameter offers, spelled the way its graph
    /// spells them. They are the three that row can carry; the named modifier rows (`fire_rate`) offer
    /// `multiply`, `minimum` and `maximum` on their own parameter, and the parser below is the one table both read.
    /// </summary>
    public static readonly IReadOnlyList<string> Operations = new[] { "set", "add", "subtract" };

    /// <summary>Whether one operation name is one the write path can resolve, and which member it is. A missing
    /// name and an unknown one are the same answer — the row refuses both by name rather than guessing `set`.</summary>
    public static bool TryParseOperation(string? name, out WeaponOverrideOperation operation)
    {
        switch (name)
        {
            case "set": operation = WeaponOverrideOperation.Set; return true;
            case "add": operation = WeaponOverrideOperation.Add; return true;
            case "subtract": operation = WeaponOverrideOperation.Subtract; return true;
            case "multiply": operation = WeaponOverrideOperation.Multiply; return true;
            case "minimum": operation = WeaponOverrideOperation.Minimum; return true;
            case "maximum": operation = WeaponOverrideOperation.Maximum; return true;
            default: operation = WeaponOverrideOperation.Set; return false;
        }
    }

    private readonly Dictionary<string, WeaponOverrideState> _byEquipment = new(StringComparer.Ordinal);
    private readonly IWeaponOverrideSink? _sink;
    private long _world = -1;
    public Action<EntityReference, WeaponOverrideSource, bool, string>? Expired { get; set; }

    /// <summary>A ledger with no native half attached: every decision is still made — merging, budgets, expiry —
    /// but an accepted request cannot be written, so <see cref="Begin"/> reports it as unapplied. The focused
    /// tests use this shape, and so does a build whose native half failed to install.</summary>
    public WeaponOverrideLedger() { }

    /// <summary>A ledger whose accepted requests are written onto the native instances by <paramref name="sink"/>.</summary>
    public WeaponOverrideLedger(IWeaponOverrideSink sink)
        => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>Instances currently carrying an override in this world, expired entries excluded.</summary>
    public int Count
    {
        get
        {
            Sweep();
            return _byEquipment.Count;
        }
    }

    /// <summary>Read-only ownership count for migration capture. Unlike Count this never sweeps or restores.</summary>
    public int RetainedCount => _byEquipment.Count;

    /// <summary>Whether a name is one the native half can write.</summary>
    public static bool IsKnownField(string? name)
    {
        if (name == null) return false;
        foreach (var field in Fields)
            if (string.Equals(field, name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Accepts one request. The whole request is checked before anything is written, so a request that names one
    /// unknown field changes nothing at all rather than its other fields: a half-applied weapon is a state no
    /// native code path produces, and ruling 84 rejects it by name.
    ///
    /// <paramref name="tick"/> is the world's current simulation tick. It is what a relative duration is measured
    /// from, and it is also what makes an already-expired request a refusal instead of a write that is reverted
    /// on the next sweep.
    ///
    /// Nothing is recorded for a request the native half refused or could not confirm: an entry recorded for a
    /// write nobody observed would let this ledger report an override that is not on the weapon, and its expiry
    /// and life-end reverts would then write a block back for a change the applier never confirmed. The
    /// `Unconfirmed` answer therefore leaves the ledger exactly as the refusal does, and the native half is the
    /// one that answers for the clone it may have made.
    /// </summary>
    public WeaponOverrideCommit Begin(WeaponOverrideRequest request, long tick, out string code)
    {
        if (request == null) { code = ResultCode.StaleEntity; return WeaponOverrideCommit.Refused; }
        if (request.Fields == null || request.Fields.Count == 0) { code = ResultCode.StaleEntity; return WeaponOverrideCommit.Refused; }
        if (request.Fields.Count > MaximumOverrideFieldsPerRequest) { code = ResultCode.OverrideBudget; return WeaponOverrideCommit.Refused; }
        foreach (var field in request.Fields)
            if (!IsKnownField(field.Name)) { code = ResultCode.OverrideFieldUnknown; return WeaponOverrideCommit.Refused; }
        if (request.Source == null) { code = ResultCode.StaleEntity; return WeaponOverrideCommit.Refused; }
        if (request.DurationTicks > 0 && request.DurationTicks > long.MaxValue - tick)
        { code = ResultCode.OverrideBudget; return WeaponOverrideCommit.Refused; }

        Sweep();
        var key = request.Equipment.Id ?? "";
        WeaponOverrideState? existing = null;
        var created = !_byEquipment.TryGetValue(key, out existing);
        if (!created && existing!.Equipment != request.Equipment)
        {
            // A stable equipment ID can be reused for a later life. Never merge the previous life's
            // fields or writer precedence into it, and a stale world/life must not retire the current one.
            if (request.Equipment.WorldEpoch != existing.Equipment.WorldEpoch
                || request.Equipment.LifeEpoch <= existing.Equipment.LifeEpoch)
            { code = ResultCode.StaleEntity; return WeaponOverrideCommit.Refused; }
            if (!Revert(existing.Equipment, out code)) return WeaponOverrideCommit.Unconfirmed;
            created = true;
        }
        if (created)
        {
            if (_byEquipment.Count >= MaximumOverriddenEquipmentPerWorld) { code = ResultCode.OverrideBudget; return WeaponOverrideCommit.Refused; }
            existing = new WeaponOverrideState(request.Equipment);
        }

        var merged = Merge(existing!, request, out var won);
        // No native half is attached on this machine: nothing can write the instance now, which is a phase answer
        // every override row declares, never a refusal without a code.
        if (_sink == null) { code = ResultCode.AuthorityOrPhase; return WeaponOverrideCommit.Refused; }
        // The entry is only remembered once the write succeeded: a ledger that recorded a request the native
        // half refused — or one whose outcome it could not read — would report an override that is not on the
        // weapon.
        var write = _sink.Apply(request.Equipment, merged, out code);
        if (write != WeaponOverrideWrite.Applied)
            return write switch
            {
                WeaponOverrideWrite.Unconfirmed => WeaponOverrideCommit.Unconfirmed,
                WeaponOverrideWrite.Failed => WeaponOverrideCommit.Failed,
                _ => WeaponOverrideCommit.Refused
            };
        if (created) _byEquipment[key] = existing!;
        existing!.mutable.Clear();
        existing.mutable.AddRange(merged);
        // Only the fields this request actually won take its identity: a request whose source is older than the
        // writer that holds a field keeps that writer's value, and recording the loser as the writer of a value it
        // did not write would let the loser's own ending drop the winning effect.
        foreach (var name in won) existing.writers[name] = request.Source;
        if (won.Count != 0) existing.expiries[request.Source] = request.DurationTicks > 0 ? tick + request.DurationTicks : 0;
        existing.RefreshExpiry();
        code = "";
        return WeaponOverrideCommit.Applied;
    }

    /// <summary>Ends the override on one instance and asks the native half to put the instance's own block back.
    /// An instance that carries no override is a success: nothing to restore is not a failure to restore.</summary>
    public bool Revert(EntityReference equipment, out string code)
    {
        code = "";
        if (equipment == null || !_byEquipment.TryGetValue(equipment.Id ?? "", out var state)
            || state.Equipment != equipment) return true;
        // The writer owns recovery until the native baseline was actually confirmed. A failed restore
        // must stay reachable for the next attempt, rather than become an apparent no-op success.
        if (_sink != null && !_sink.Restore(equipment, out code)) return false;
        _byEquipment.Remove(equipment.Id ?? "");
        return true;
    }

    /// <summary>
    /// Ends the fields one writer applied to one instance, which is what a kernel-managed effect's ending means: the
    /// card that wrote them is over, and whatever another writer set on the same instance stays true. The fields the
    /// writer set are dropped from the merged set, and the instance is written again from its own original block —
    /// dropping a field is putting that field back, which only a write starting from the original can do. A writer
    /// with no field on this instance is a success: an effect the module already undid has nothing left to undo.
    ///
    /// The writer is matched on plan and node, which is the identity a kernel-managed effect carries; a provider
    /// that names its own writes with the ledger's whole merge key uses the overload below.
    /// </summary>
    public bool RevertWriter(EntityReference equipment, string planId, string nodeId, out string code)
        => RevertWriter(equipment,
            writer => writer.PlanId == planId && writer.NodeId == nodeId, out code);

    /// <summary>
    /// The same revert, matched on the ledger's whole merge identity `(plan, node, sequence)`. One node that
    /// dispatches the same row twice writes twice under its own node id, and each write has to end by itself:
    /// matching the node alone would let the first ending drop the fields of the second write with it, so a
    /// change the plan asked to last until a later tick would end at the earlier one.
    /// </summary>
    public bool RevertWriter(EntityReference equipment, WeaponOverrideSource source, out string code)
    {
        if (source == null) { code = ResultCode.StaleEntity; return false; }
        return RevertWriter(equipment, writer => writer.PlanId == source.PlanId && writer.NodeId == source.NodeId
            && writer.Sequence == source.Sequence, out code);
    }

    public bool HasWriter(EntityReference equipment, WeaponOverrideSource source)
        => _byEquipment.TryGetValue(equipment.Id, out var state) && state.Equipment == equipment
            && state.writers.Values.Contains(source);

    /// <summary>The one revert both identities run through: the fields whose recorded writer the predicate accepts
    /// are dropped, and the rest are written again from the instance's own block.</summary>
    private bool RevertWriter(EntityReference equipment, Func<WeaponOverrideSource, bool> match, out string code)
    {
        code = "";
        if (equipment == null || !_byEquipment.TryGetValue(equipment.Id ?? "", out var state)
            || state.Equipment != equipment) return true;
        var kept = new List<WeaponOverrideField>(state.mutable.Count);
        var keptWriters = new Dictionary<string, WeaponOverrideSource>(StringComparer.Ordinal);
        foreach (var field in state.mutable)
        {
            if (state.writers.TryGetValue(field.Name, out var writer) && match(writer)) continue;
            kept.Add(field);
            if (state.writers.TryGetValue(field.Name, out var keptWriter)) keptWriters[field.Name] = keptWriter;
        }
        if (kept.Count == state.mutable.Count) return true;
        if (kept.Count == 0) return Revert(equipment, out code);
        if (_sink == null) return false;
        // The surviving writers' fields replace the installed merged set in one write: the applier builds the new
        // set from the instance's own block and swaps it in, so there is no window in which the native instance
        // carries neither the old nor the new state. A write the native half could not confirm is reported with
        // its own code and leaves the ledger's fields as they were — the applier has rolled the instance back to
        // the state that was confirmed, and the next revert of this entry retries the cleanup rather than the
        // mutation.
        if (_sink.Apply(equipment, kept, out code) != WeaponOverrideWrite.Applied) return false;
        state.mutable.Clear(); state.mutable.AddRange(kept);
        state.writers.Clear();
        foreach (var pair in keptWriters) state.writers[pair.Key] = pair.Value;
        state.RefreshExpiry();
        code = "";
        return true;
    }

    /// <summary>
    /// The fields one instance is currently overridden with, or an empty list when it carries none. This is what
    /// a gear spawn replays: `OnGearSpawnComplete` is the one place the game rebuilds an archetype, so an
    /// instance whose block was just rebuilt is written again from exactly this answer. Replay is idempotent
    /// because the answer is the merged absolute field set and not a delta.
    /// </summary>
    public IReadOnlyList<WeaponOverrideField> Pending(EntityReference equipment)
    {
        Sweep();
        return _byEquipment.TryGetValue(equipment?.Id ?? "", out var state) && state.Equipment == equipment
            ? new List<WeaponOverrideField>(state.mutable)
            : Array.Empty<WeaponOverrideField>();
    }

    /// <summary>
    /// Starts a world, or returns whether the epoch actually changed. A notification for the epoch already in
    /// force gives nothing up: the instances it would restore are the ones the running world is using, and a
    /// repeated notification is not a world boundary. When the epoch did change, the previous world's entries are
    /// given up first — ruling 84 makes the world boundary an unconditional revert — and the native half restores
    /// every instance it holds. Nothing is carried across, because a reference from the old world is not
    /// resolvable in the new one and an override on it could never be reverted.
    /// </summary>
    public bool BeginWorld(long worldEpoch)
    {
        if (_world == worldEpoch) return false;
        _world = worldEpoch;
        _byEquipment.Clear();
        _sink?.RestoreAll();
        return true;
    }

    /// <summary>Ends the current world: same revert as <see cref="BeginWorld"/>, with no new epoch. Kept separate
    /// so an end that is not followed by a start still reverts, which is what an expedition teardown is.</summary>
    public void EndWorld()
    {
        _world = -1;
        _byEquipment.Clear();
        _sink?.RestoreAll();
    }

    /// <summary>
    /// Gives up every entry and reverts the native instances without ending the world. This is a checkpoint
    /// reload's boundary: the same expedition continues and the epoch does not move, but the level's gear is
    /// rebuilt from the game's own data, so an override this world accepted no longer exists on any instance and
    /// must not be replayed onto the rebuilt ones. It is <see cref="BeginWorld"/>'s revert without its epoch.
    /// </summary>
    public void RestoreAll()
    {
        _byEquipment.Clear();
        _sink?.RestoreAll();
    }

    /// <summary>
    /// Reverts each writer whose deadline passed, leaving other writers on the instance intact. Called from the places that already know the tick —
    /// accept, query and the native sweep — rather than from a timer of its own, so the ledger owns no clock and
    /// an idle world costs nothing.
    /// </summary>
    public void Sweep(long tick)
    {
        List<(EntityReference Equipment, WeaponOverrideSource Writer)>? expired = null;
        foreach (var state in _byEquipment.Values)
        {
            if (state.ExpiryTick <= 0 || state.ExpiryTick > tick) continue;
            foreach (var pair in state.expiries)
                if (pair.Value > 0 && pair.Value <= tick)
                    (expired ??= new()).Add((state.Equipment, pair.Key));
        }
        if (expired == null) return;
        foreach (var (equipment, writer) in expired)
        {
            var restored = RevertWriter(equipment, writer, out var code);
            Expired?.Invoke(equipment, writer, restored, code);
        }
    }

    /// <summary>Whether one instance currently carries an override, for a caller that holds the reference and
    /// wants the ledger's own answer rather than a copy of its table.</summary>
    public bool IsOverridden(EntityReference equipment)
    {
        Sweep();
        return _byEquipment.TryGetValue(equipment?.Id ?? "", out var state) && state.Equipment == equipment;
    }

    /// <summary>Expired entries are dropped on every read, so the table a caller sees is never larger than the
    /// set of overrides that are still true. Sweeping without a tick only drops entries that were already
    /// expired when they were written, which cannot happen — the write path refuses those — so a read with no
    /// clock in hand is the no-op it should be rather than an arbitrary clock reading.</summary>
    private void Sweep() => Sweep(0);

    /// <summary>The merged field set of one request against one instance: the last request to arrive wins every field
    /// it names, whoever wrote the field before, and a field it does not name is left alone. A writer that loses a
    /// field simply holds one field fewer; it ends when it holds none.
    ///
    /// <paramref name="won"/> is the set of fields this request wrote, which is what its identity is recorded against.</summary>
    private static IReadOnlyList<WeaponOverrideField> Merge(WeaponOverrideState state, WeaponOverrideRequest request,
        out List<string> won)
    {
        won = new List<string>(request.Fields.Count);
        var merged = new List<WeaponOverrideField>(state.mutable);
        foreach (var field in request.Fields)
        {
            var existing = Index(merged, field.Name);
            if (existing >= 0) merged[existing] = field;
            else merged.Add(field);
            won.Add(field.Name);
        }
        return merged;
    }

    private static int Index(List<WeaponOverrideField> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
            if (string.Equals(fields[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
