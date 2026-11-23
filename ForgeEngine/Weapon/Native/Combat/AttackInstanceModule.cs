using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using Gear;

namespace ForgeWeapon.Native;

/// <summary>Host-side attack-instance facts: the two ends of a burst sequence and the game's own empty-clip
/// decision. Neither is a Forge tally of a firing: the sequence ends are the weapon archetype's own
/// `OnStartFiring` / `OnStopFiring` and the count is the weapon's own `m_burstMax`, and the empty-clip fact is
/// the game's own refusal path — never a comparison of a clip readback against zero.
///
/// This type never counts a shot, never applies damage and never reads health. Every fact names the equipment
/// life the adapter recorded and the player reference its owner resolves to. A world change drops the per-life
/// bookkeeping without a fact, because a sequence reported in the new world would be attributed to a world it
/// never ran in.</summary>
internal sealed class AttackInstanceModule
{
    /// <summary>One equipment life's attack bookkeeping: the one open burst sequence, its own sequence number and
    /// the dry-fire sequence. Keyed by the equipment entity's own id, so a handle rebuilt by a backpack readback
    /// keeps its sequences.</summary>
    private sealed class Life
    {
        internal bool BurstOpen;
        internal long Bursts;
        internal long DryFires;
    }

    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<IForgeEngine?> _kernel;
    private readonly WeaponLog _log;
    private readonly Dictionary<string, Life> _lives = new(StringComparer.Ordinal);
    // WHY: P5 — the three rows' gates, taken once here and read before any id, scope or payload is built.
    private readonly RuntimeSubscriptionGate _dryFireGate, _burstStartedGate, _burstEndedGate;
    private long _world = -1, _sequence;

    internal AttackInstanceModule(EquipmentNativeAdapter adapter, Func<IForgeEngine?> kernel, WeaponLog log)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dryFireGate = adapter.GateOf(WeaponBurstContract.DryFireBinding);
        _burstStartedGate = adapter.GateOf(WeaponBurstContract.BurstStartedBinding);
        _burstEndedGate = adapter.GateOf(WeaponBurstContract.BurstEndedBinding);
    }

    /// <summary>The weapon ran out of ammunition: `BWA_Burst.OnFireShotEmptyClip` and
    /// `BWA_Auto.OnFireShotEmptyClip` are the game's own empty-clip paths, so the fact is published from the
    /// game's own decision and never from comparing a clip readback against zero. The row's two ports are the
    /// actor and the equipment and it has no scope port, so the fact carries the equipment life's own dry-fire
    /// sequence: one sequence per equipment life, the same identity scheme the burst ends use.</summary>
    internal void DryFire(BulletWeapon? weapon)
    {
        if (weapon == null) return;
        _log.Skipped(WeaponBurstContract.DryFireBinding, "hook-reached");
        var kernel = Ready();
        if (kernel == null) { _log.Skipped(WeaponBurstContract.DryFireBinding, "not-ready-or-not-host"); return; }
        var recorded = _adapter.AttackTarget(weapon);
        if (recorded == null)
        {
            // An empty clip on a life this build has not recorded is a normal gap rather than a refusal: trace only.
            _log.Skipped(WeaponBurstContract.DryFireBinding, "equipment-untracked");
            return;
        }
        var life = LifeOf(recorded.Value.Equipment);
        // Both counters advance whether or not anyone listens, so the ids a plan sees are the same either way.
        var dryFire = ++life.DryFires;
        var sequence = checked(++_sequence);
        if (!_dryFireGate.HasSubscribers) { _log.Skipped(WeaponBurstContract.DryFireBinding, "no-consumer"); return; }
        // The kernel records the fact itself: `trigger.fired` when it queues it, `event.rejected` when it refuses it;
        // a fact no mounted plan claims is answered `ignored`, which only the line below records.
        var id = Id(kernel, sequence);
        var fact = _adapter.Begin(_dryFireGate, kernel.WorldEpoch);
        fact.Values.Entity(Blocks.TriggerCombatDryFire.OutActor, recorded.Value.Source);
        fact.Values.Entity(Blocks.TriggerCombatDryFire.OutEquipment, recorded.Value.Equipment);
        Raise(kernel, _dryFireGate, fact, id, "gtfo.weapon.dry-fire:" + recorded.Value.Equipment.Id + ":" + Number(dryFire), recorded.Value.Equipment.Id);
    }

    /// <summary>A burst sequence opened or closed, counted by the weapon's own `m_burstMax`. `BWA_Burst` and
    /// `BWA_Auto` declare both ends of the sequence, so the count is the weapon's own burst length and never a
    /// Forge tally. A close with no open sequence, or a second open with one already open, publishes nothing:
    /// the game reports a sequence, and this module does not invent the missing end of one.</summary>
    internal void Burst(BulletWeapon? weapon, bool started)
    {
        var kernel = Ready();
        if (kernel == null || weapon == null) return;
        var recorded = _adapter.AttackTarget(weapon);
        if (recorded == null) return;
        var life = LifeOf(recorded.Value.Equipment);
        if (started == life.BurstOpen) return;
        life.BurstOpen = started;
        var bursts = ++life.Bursts;
        var sequence = checked(++_sequence);
        var gate = started ? _burstStartedGate : _burstEndedGate;
        if (!gate.HasSubscribers) return;
        var count = weapon.m_burstMax;
        if (count < 0) count = 0;
        var id = Id(kernel, sequence);
        var fact = _adapter.Begin(gate, kernel.WorldEpoch);
        fact.Values.Entity(started ? Blocks.TriggerCombatBurstStarted.OutSource : Blocks.TriggerCombatBurstEnded.OutSource, recorded.Value.Source);
        fact.Values.Entity(started ? Blocks.TriggerCombatBurstStarted.OutEquipment : Blocks.TriggerCombatBurstEnded.OutEquipment, recorded.Value.Equipment);
        fact.Values.Integer(started ? Blocks.TriggerCombatBurstStarted.OutCount : Blocks.TriggerCombatBurstEnded.OutCount, count);
        Raise(kernel, gate, fact, id, "gtfo.weapon.burst:" + recorded.Value.Equipment.Id + ":" + Number(bursts), recorded.Value.Equipment.Id);
    }

    private Life LifeOf(EntityReference equipment)
    {
        if (!_lives.TryGetValue(equipment.Id, out var life))
        {
            life = new Life();
            _lives[equipment.Id] = life;
        }
        return life;
    }

    /// <summary>The fact id: this module's own sequence, so two facts of one life are two ids; the caller advanced
    /// it and has already read the row's gate.</summary>
    private static string Id(IForgeEngine kernel, long sequence)
        => "gtfo.weapon.attack-fact:" + Number(kernel.WorldEpoch) + ":" + Number(sequence);

    /// <summary>Raises one fact begun under this provider's own registration, the same path the equipment and shot
    /// facts use; Runtime owns ordering, dedupe and causality.</summary>
    private void Raise(IForgeEngine kernel, RuntimeSubscriptionGate gate, EventWriter fact, string id, string scope, string entity)
        => _log.Answered(gate.BindingId, fact.Raise(id, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick), scope), entity);

    /// <summary>The gate every fact here shares: the host's own ready kernel, and a world whose sequences are this
    /// module's. A world change drops every life's sequences without a fact, because an end reported in the new
    /// world would close a sequence that belonged to another one.</summary>
    private IForgeEngine? Ready()
    {
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return null;
        if (kernel.WorldEpoch != _world)
        {
            _lives.Clear();
            _world = kernel.WorldEpoch;
        }
        return _adapter.Authoritative() ? kernel : null;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
