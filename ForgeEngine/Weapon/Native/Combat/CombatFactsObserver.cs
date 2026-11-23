using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using Gear;
using Player;

namespace ForgeWeapon.Native;

/// <summary>
/// The weapon-state facts of this batch and the impact records the hit-context read answers from. Every body
/// here reads the state a native object really holds and publishes it where the state really changes; nothing
/// here writes, and every published reference is checked through the adapter before it leaves.
///
/// <b>Charge.</b> The two native charge families are read where each one runs. A melee charge is the
/// <c>MWS_ChargeUp</c> state object: `Enter` is the charge starting, `Update` is it running, `Exit` is it
/// ending, and `OnChargeupRelease` is the charged attack really being swung. A ranged charge is the archetype's
/// own update, whose <c>m_inChargeup</c> flag is the state and whose <c>m_chargeupTimer</c> against
/// <c>ChargeupDelay()</c> is the proportion. The proportion is only published while the native threshold is a
/// positive number this machine can read: a threshold of zero means "this weapon has no charge threshold", which
/// is not a ratio of one.
///
/// <b>Aim.</b> The holder's own <c>ItemAimTrigger</c> is the weapon state and not a key: a weapon whose sights
/// take time to come up, or one an EMP folded away, is entered and left by the state machine while the key that
/// asked for it stays held. Only the transition is published, so no plan ever sees a repeat of a state it
/// already knows.
///
/// <b>Shot resolution.</b> One `Fire` body is one shot: the prefix opens it, the shared hit routine counts what
/// it reached, and the postfix publishes the outcome — `hit` when at least one impact reached a damageable
/// target, `world` when the shot reached geometry only, and `miss` when the body resolved with no impact at all.
/// A miss therefore exists as a fact and can never be doubled by a later hit, which is exactly what a plan that
/// counts consecutive misses needs. The unit is the shot and not the pellet: a shotgun's several pellets are one
/// shot with a `hit_count`, because one native body produced them.
/// </summary>
internal sealed class CombatFactsObserver
{
    /// <summary>The live observer, or null before the session is built and after it is disposed.</summary>
    internal static CombatFactsObserver? Current { get; private set; }

    private static readonly int ChargeStart = ChargeMember(WeaponStateTriggerContract.ChargeStart);
    private static readonly int ChargeProgress = ChargeMember(WeaponStateTriggerContract.ChargeProgress);
    private static readonly int ChargeEnd = ChargeMember(WeaponStateTriggerContract.ChargeEnd);
    private static readonly int ChargeSwing = ChargeMember(WeaponStateTriggerContract.ChargeSwing);
    // The aim row's `phase` port is an enum of the `aim_phase` set, so it travels as the member's index.
    private static readonly int AimEnterMember = WeaponEnumMembers.Index("aim_phase", WeaponStateTriggerContract.AimEnter);
    private static readonly int AimExitMember = WeaponEnumMembers.Index("aim_phase", WeaponStateTriggerContract.AimExit);

    private readonly EquipmentNativeAdapter _adapter;
    private readonly WeaponCombatObserver _shots;
    private readonly IForgeEngine _kernel;
    private readonly Func<Item?, EntityReference?> _equipmentOf;
    private readonly Func<PlayerAgent?, EntityReference?> _ownerOf;
    private readonly Dictionary<IntPtr, MeleeCharge> _melee = new();
    private readonly HashSet<IntPtr> _aiming = new();
    private readonly RuntimeSubscriptionGate _chargeGate, _aimGate, _shotResolvedGate;
    private long _world = -1, _sequence;
    private bool _disposed, _chargeAsked, _aimAsked, _aimSeedPending;

    // WHY: P5 — a value in the table, so tracking a charge's edges allocates nothing per swing when no plan listens.
    private readonly record struct MeleeCharge(MeleeWeaponFirstPerson Weapon, bool Swung);

    internal CombatFactsObserver(EquipmentNativeAdapter adapter, WeaponCombatObserver shots, Func<IForgeEngine?> kernel,
        Func<Item?, EntityReference?> equipmentOf, Func<PlayerAgent?, EntityReference?> ownerOf)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _shots = shots ?? throw new ArgumentNullException(nameof(shots));
        _kernel = (kernel ?? throw new ArgumentNullException(nameof(kernel)))()
            ?? throw new ArgumentException("The Runtime provider returned no live host.", nameof(kernel));
        _equipmentOf = equipmentOf ?? throw new ArgumentNullException(nameof(equipmentOf));
        _ownerOf = ownerOf ?? throw new ArgumentNullException(nameof(ownerOf));
        // WHY: the gates are the kernel's own refreshed instances, taken once, so hooks read a bool rather than a string lookup.
        _chargeGate = adapter.GateOf(WeaponStateTriggerContract.ChargeStateBinding);
        _aimGate = adapter.GateOf(WeaponStateTriggerContract.AimStateBinding);
        _shotResolvedGate = adapter.GateOf(CombatPrimitiveContract.ShotResolvedBinding);
    }

    /// <summary>Installs the observer. Called by the session before its hooks are patched, so no hook can run
    /// over a half-built observer.</summary>
    internal static CombatFactsObserver Attach(EquipmentNativeAdapter adapter, WeaponCombatObserver shots,
        Func<IForgeEngine?> kernel, Func<Item?, EntityReference?> equipmentOf,
        Func<PlayerAgent?, EntityReference?> ownerOf)
    {
        if (Current != null) throw new InvalidOperationException("Combat facts are single-instance.");
        var facts = new CombatFactsObserver(adapter, shots, kernel, equipmentOf, ownerOf);
        Current = facts;
        return facts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>
    /// Puts on the per-frame charge and aim hooks the first tick a plan listens to their state. Called on every advanced tick: two booleans
    /// until each unit is asked for once. The first aim sample after the hook goes on only records the holder's state,
    /// so an aim already up when the plan subscribed is not reported as a new entry, as when the hook ran all along.
    /// </summary>
    internal void EnsureStateHooks()
    {
        if (!_chargeAsked && !Closed(_chargeGate))
        {
            _chargeAsked = true;
            WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.ArchetypeUpdate);
        }
        if (!_aimAsked && !Closed(_aimGate))
        {
            _aimAsked = true;
            _aimSeedPending = WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.Aim) == null;
        }
    }

    private void Clear()
    {
        _melee.Clear(); _aiming.Clear(); _sequence = 0;
    }

    // -------------------------------------------------------------------------------------------------------
    // Charge: the melee state object.
    // -------------------------------------------------------------------------------------------------------

    internal void MeleeEntered(MWS_ChargeUp charge)
    {
        if (!Ready() || charge == null) return;
        var weapon = charge.m_weapon;
        if (weapon == null) return;
        _melee[charge.Pointer] = new MeleeCharge(weapon, false);
        PublishCharge(weapon, ChargeStart, null);
    }

    internal void MeleeRan(MWS_ChargeUp charge)
    {
        if (Closed(_chargeGate) || !Ready() || charge == null) return; // WHY: F9, progress frames cost nothing when unsubscribed.
        if (!_melee.TryGetValue(charge.Pointer, out var state)) return;
        PublishCharge(state.Weapon, ChargeProgress, MeleeRatio(charge));
    }

    /// <summary>The charge ended. A charge whose release the game announced is the charged attack being swung;
    /// one that ends without it is the charge being abandoned, which is the path the weapon's own
    /// `OnMeleeChargeCancel` names.</summary>
    internal void MeleeEnded(MWS_ChargeUp charge)
    {
        if (!Ready() || charge == null) return;
        if (!_melee.Remove(charge.Pointer, out var state)) return;
        var ratio = MeleeRatio(charge);
        PublishCharge(state.Weapon, state.Swung ? ChargeSwing : ChargeEnd, ratio);
    }

    /// <summary>The charged attack was released. The release is remembered on the charge it belongs to rather
    /// than published here, because the state object's own `Exit` is what ends the charge and the two events must
    /// not both be reported as its ending.</summary>
    internal void MeleeReleased(MWS_ChargeUp charge)
    {
        if (!Ready() || charge == null) return;
        if (_melee.TryGetValue(charge.Pointer, out var state)) _melee[charge.Pointer] = state with { Swung = true };
    }

    private static double? MeleeRatio(MWS_ChargeUp charge)
    {
        var full = charge.m_maxDamageTime;
        if (!float.IsFinite(full) || full <= 0f) return null;
        var elapsed = charge.m_elapsed;
        if (!float.IsFinite(elapsed) || elapsed < 0f) return null;
        return Math.Clamp(elapsed / full, 0d, 1d);
    }

    // -------------------------------------------------------------------------------------------------------
    // Charge: the ranged archetype.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>The flag before one archetype update, so a charge that starts and ends inside that update is
    /// still seen as both an entry and an ending.</summary>
    internal static bool RangedCharging(BulletWeaponArchetype archetype)
        => archetype != null && archetype.m_inChargeup;

    internal void RangedRan(BulletWeaponArchetype archetype, bool before)
    {
        if (archetype == null) return;
        var now = archetype.m_inChargeup;
        // WHY: F8/F9, an archetype neither charging nor just stopped has nothing to publish, so nothing else is read.
        if (!now && !before) return;
        if (Closed(_chargeGate) || !Ready()) return;
        var weapon = archetype.m_weapon;
        if (weapon == null) return;
        var ratio = RangedRatio(archetype);
        if (now && !before) PublishCharge(weapon, ChargeStart, ratio);
        else if (now) PublishCharge(weapon, ChargeProgress, ratio);
        else if (before) PublishCharge(weapon, ChargeEnd, ratio);
    }

    private static double? RangedRatio(BulletWeaponArchetype archetype)
    {
        var full = ChargeupDelay(archetype); // WHY: F8, no closure.
        var elapsed = archetype.m_chargeupTimer;
        if (!float.IsFinite(full) || full <= 0f || !float.IsFinite(elapsed) || elapsed < 0f) return null;
        return Math.Clamp(elapsed / full, 0d, 1d);
    }

    // -------------------------------------------------------------------------------------------------------
    // Aim.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One holder update, with the sights' state read from the holder's own trigger. The wielded item is what
    /// the fact is about; a holder with nothing in hand, or one whose item this machine has no current equipment
    /// reference for, publishes nothing — an aim fact without its weapon names no equipment to act on.
    /// </summary>
    internal void AimSampled(FirstPersonItemHolder holder)
    {
        if (!Ready() || holder == null) return;
        var pointer = holder.Pointer;
        var was = _aiming.Contains(pointer);
        var trigger = AimTrigger(holder);
        if (_aimSeedPending)
        {
            _aimSeedPending = false;
            if (trigger && holder.WieldedItem != null) _aiming.Add(pointer);
            else _aiming.Remove(pointer);
            return;
        }
        if (!trigger && !was) return;
        // WieldedItem is part of the native watermark: while the trigger stays true, a missing item clears the
        // state and an item returning produces a new enter edge. Reading it here preserves that same-frame path.
        var item = holder.WieldedItem;
        var aiming = item != null && trigger;
        if (aiming == was) return;
        if (aiming) _aiming.Add(pointer);
        else _aiming.Remove(pointer);
        if (item == null) return;
        // WHY: the transition is recorded above either way, so enter/exit edges stay exact when a plan subscribes later.
        if (Closed(_aimGate) || ClientWeaponReport.Current?.Aim(item, aiming) == true) return;
        var equipment = _equipmentOf(item);
        if (equipment == null) return;
        PublishAim(equipment, _ownerOf(OwnerAgent(holder)), aiming);
    }

    // -------------------------------------------------------------------------------------------------------
    // Shot resolution.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One shot's own ending: what the shot reached, read from the impacts the shared hit routine counted while
    /// the body was running. An impact the hit routine could not name a damageable target for is a world hit, so
    /// the outcome is decided by whether any impact reached a target rather than by whether one happened at all.
    /// </summary>
    internal void ShotResolved(WeaponCombatObserver.Shot shot)
    {
        if (!Ready() || ClientWeaponReport.Reporting) return;
        if (Closed(_shotResolvedGate)) return; // WHY: F9, the tally read and payload are skipped when unsubscribed.
        // A suppressed shot's rays were answered as misses: its bullet never flew, so reporting a miss would describe
        // a trajectory that did not happen (and hide the projectile that replaces it).
        if (WeaponShotRules.Current is { ShotReplaced: true }) return;
        var tally = _shots.HitsOf(shot);
        var hits = tally.Targets + tally.Worlds;
        var outcome = tally.Targets > 0 ? CombatPrimitiveContract.Hits
            : tally.Worlds > 0 ? CombatPrimitiveContract.World : CombatPrimitiveContract.Misses;
        var source = _shots.Owner(shot);
        if (Begin(_shotResolvedGate, shot.Equipment, out var id) is not { } fact) return;
        ShotResolvedOutputs(fact.Values, source, shot.Equipment, outcome, tally.Target, hits,
            tally.PositionX, tally.PositionY, tally.PositionZ, tally.NormalX, tally.NormalY, tally.NormalZ);
        Raise(fact, id, shot.Equipment);
    }

    /// <summary>The shot-resolution row's ports: the place and the surface normal are written only when the shot
    /// reached something, and are an explicit null otherwise.</summary>
    internal static void ShotResolvedOutputs(EvaluationOutputs values, EntityReference? source, EntityReference? equipment,
        int outcome, EntityReference? target, int hits, double x, double y, double z, double nx, double ny, double nz)
    {
        values.Entity(Blocks.TriggerCombatShotResolved.OutSource, source);
        // Null only on the shooter's own machine (ShotPredicted): the cell stays unwritten there, not written as nobody.
        if (equipment != null) values.Entity(Blocks.TriggerCombatShotResolved.OutEquipment, equipment);
        values.Enum(Blocks.TriggerCombatShotResolved.OutOutcome, outcome);
        values.Entity(Blocks.TriggerCombatShotResolved.OutTarget, target);
        if (hits > 0)
        {
            values.Vector(Blocks.TriggerCombatShotResolved.OutPosition, x, y, z);
            values.Vector(Blocks.TriggerCombatShotResolved.OutNormal, nx, ny, nz);
        }
        else
        {
            values.Null(Blocks.TriggerCombatShotResolved.OutPosition);
            values.Null(Blocks.TriggerCombatShotResolved.OutNormal);
        }
        values.Integer(Blocks.TriggerCombatShotResolved.OutHitCount, hits);
    }

    /// <summary>A client's own shot, admitted by <see cref="ClientWeaponReport"/>: the same row with the shooter's reading of what it
    /// reached. <paramref name="outcome"/> is 2 for a hit, 1 for geometry only and 0 for a miss. False when nothing listens.</summary>
    internal bool ShotReported(EntityReference source, EntityReference equipment, byte outcome, EntityReference? target, int hits,
        UnityEngine.Vector3 point, UnityEngine.Vector3 normal, long token)
    {
        if (!Ready() || Closed(_shotResolvedGate)) return false;
        if (Begin(_shotResolvedGate, equipment, out var id) is not { } fact) return true;
        ShotResolvedOutputs(fact.Values, source, equipment, ReportedOutcome(outcome),
            target, hits, point.x, point.y, point.z, normal.x, normal.y, normal.z);
        // The shooter's machine may already have shown what this shot shows it (ShotPredicted): the token says which.
        fact.Raise(id, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), equipment.Id, source, token);
        return true;
    }

    private static int ReportedOutcome(byte outcome)
        => outcome == 2 ? CombatPrimitiveContract.Hits : outcome == 1 ? CombatPrimitiveContract.World : CombatPrimitiveContract.Misses;

    /// <summary>On the shooter's own machine (a client), the same row as <see cref="ShotReported"/> written from what the
    /// shot report carries, without the equipment, which only the host names: the presentation steps the shot shows this
    /// player that read none of what is missing are played now and kept under <paramref name="token"/>.</summary>
    internal void ShotPredicted(EntityReference source, byte outcome, EntityReference target, int hits,
        UnityEngine.Vector3 point, UnityEngine.Vector3 normal, long token)
    {
        if (_disposed || Closed(_shotResolvedGate) || _kernel.StartupState != RuntimeStartupState.Ready) return;
        // The shot is already reported and the game's own firing is in progress: nothing thrown here may reach either.
        try
        {
            var fact = _adapter.BeginPredicted(_shotResolvedGate);
            ShotResolvedOutputs(fact.Values, source, null, ReportedOutcome(outcome),
                target, hits, point.x, point.y, point.z, normal.x, normal.y, normal.z);
            fact.Predict(_kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "", token);
        }
        catch (Exception error) { _adapter.PredictionFailed(_shotResolvedGate, error); }
    }

    /// <summary>A client's sights going up or down, admitted by <see cref="ClientWeaponReport"/>: the same row, on the host's copy of the item.</summary>
    internal void AimReported(Item item, bool aiming)
    {
        if (!Ready() || Closed(_aimGate) || _equipmentOf(item) is not { } equipment) return;
        PublishAim(equipment, _adapter.OwnerOf(item), aiming);
    }

    private void PublishAim(EntityReference equipment, EntityReference? actor, bool aiming)
    {
        if (Begin(_aimGate, equipment, out var id) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerCombatAimState.OutEquipment, equipment);
        fact.Values.Entity(Blocks.TriggerCombatAimState.OutActor, actor);
        fact.Values.Enum(Blocks.TriggerCombatAimState.OutPhase, aiming ? AimEnterMember : AimExitMember);
        Raise(fact, id, equipment);
    }

    // -------------------------------------------------------------------------------------------------------
    // Shared plumbing.
    // -------------------------------------------------------------------------------------------------------

    private void PublishCharge(MeleeWeaponFirstPerson weapon, int phase, double? ratio)
        => PublishCharge((Item)weapon, phase, ratio);

    private void PublishCharge(BulletWeapon weapon, int phase, double? ratio)
        => PublishCharge((Item)weapon, phase, ratio);

    private void PublishCharge(Item weapon, int phase, double? ratio)
    {
        if (ClientWeaponReport.Current?.Charge(weapon, phase, ratio, phase == ChargeProgress) == true) return;
        if (phase == ChargeProgress && !ProgressDue(weapon.Pointer)) return;
        ChargePublished(weapon, phase, ratio);
    }

    // A weapon this machine publishes for itself (the host's own and its bots') reports charge progress at the same pace a
    // client's report reaches the host, so the card fires as often for every player. A fixed table: no allocation per frame.
    private readonly IntPtr[] _progressWeapons = new IntPtr[8];
    private readonly float[] _progressAt = new float[8];

    private bool ProgressDue(IntPtr weapon)
    {
        var now = Clock.Time;
        var free = -1;
        for (var index = 0; index < _progressWeapons.Length; index++)
        {
            if (_progressWeapons[index] == weapon)
            {
                if (now < _progressAt[index]) return false;
                _progressAt[index] = now + ClientWeaponReport.ProgressSeconds;
                return true;
            }
            if (free < 0 && (_progressWeapons[index] == IntPtr.Zero || now >= _progressAt[index])) free = index;
        }
        if (free >= 0) { _progressWeapons[free] = weapon; _progressAt[free] = now + ClientWeaponReport.ProgressSeconds; }
        return true;
    }

    internal void ChargePublished(Item weapon, int phase, double? ratio)
    {
        if (Closed(_chargeGate)) return; // WHY: F9, no reads or payload for a charge row nobody listens on.
        var equipment = _equipmentOf(weapon);
        if (equipment == null) return;
        var actor = _adapter.OwnerOf(weapon);
        if (Begin(_chargeGate, equipment, out var id) is not { } fact) return;
        // Enum outputs carry their canonical member index. A ratio not read yet is absent, not JSON null.
        fact.Values.Entity(Blocks.TriggerWeaponChargeState.OutEquipment, equipment);
        fact.Values.Enum(Blocks.TriggerWeaponChargeState.OutPhase, phase);
        if (ratio is { } value) fact.Values.Number(Blocks.TriggerWeaponChargeState.OutRatio, value);
        fact.Values.Entity(Blocks.TriggerWeaponChargeState.OutActor, actor);
        Raise(fact, id, equipment);
    }

    private static int ChargeMember(string member) => WeaponEnumMembers.Index("charge_phase", member);

    /// <summary>
    /// Begins one fact, or answers null while the kernel is not running. Every caller has already read its row's
    /// cached gate before building the value, so a fact nobody subscribes to never reaches here. A rejection is never
    /// swallowed: the kernel writes it as `event.rejected`, because a payload this provider cannot validate is a
    /// defect here rather than a game condition.
    /// </summary>
    private EventWriter? Begin(RuntimeSubscriptionGate gate, EntityReference subject, out string id)
    {
        id = "";
        if (subject == null) return null;
        var kernel = _kernel;
        if (kernel.StartupState != RuntimeStartupState.Ready) return null;
        id = "gtfo.weapon." + gate.BindingId + ":" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence));
        return _adapter.Begin(gate, kernel.WorldEpoch);
    }

    /// <summary>The kernel records the outcome itself: `trigger.fired` when it queues the fact, `event.rejected` when
    /// it refuses it, both under this provider's binding, so no second line of this package's own is written.</summary>
    private void Raise(EventWriter fact, string id, EntityReference subject)
        => fact.Raise(id, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), subject.Id);

    /// <summary>The gate every body above starts with: this session's authority (the host's own advance and the
    /// gameplay gate it was started with) and a kernel that is really running. The tables are dropped when the
    /// world changes, because every one of them is keyed by native pointers of objects in one world.</summary>
    private bool Ready()
    {
        if (_disposed) return false;
        var kernel = _kernel;
        if (kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (kernel.WorldEpoch != _world) { _world = kernel.WorldEpoch; Clear(); }
        // A client keeps the same state tables: its edges leave through the report at the publishing line.
        return _adapter.Authoritative() || ClientWeaponReport.Reporting;
    }

    // WHY: static helpers replace the per-call Safe closures (F8); the fallbacks are the same values as before.
    private static float ChargeupDelay(BulletWeaponArchetype archetype)
    {
        try { return archetype.ChargeupDelay(); }
        catch (Exception failure) { NativeCallFailure.Note("CombatFactsObserver.ChargeupDelay", failure); return 0f; }
    }

    private static bool AimTrigger(FirstPersonItemHolder holder)
    {
        try { return holder.ItemAimTrigger; }
        catch (Exception failure) { NativeCallFailure.Note("CombatFactsObserver.AimTrigger", failure); return false; }
    }

    private static PlayerAgent? OwnerAgent(FirstPersonItemHolder holder)
    {
        try { return holder.m_owner; }
        catch (Exception failure) { NativeCallFailure.Note("CombatFactsObserver.OwnerAgent", failure); return null; }
    }

    /// <summary>A declared row nobody listens on.</summary>
    private static bool Closed(RuntimeSubscriptionGate gate) => !gate.HasSubscribers;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
