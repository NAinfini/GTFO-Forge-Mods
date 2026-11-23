using System;
using System.Collections.Generic;
using System.Globalization;
using Enemies;
using ForgeEngine.Framework;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// The one row of <see cref="WeaponMeleeHitContract"/>, read from the swing's own per-target hit entry on the
/// machine that performs the swing. Two hooks carry it here, and they never describe the same hit on the same
/// machine:
/// <list type="bullet">
/// <item><b>First person</b> — `MeleeWeaponFirstPerson.DoAttackDamage` is this machine's own player swinging. The
/// attacker is the weapon's own `Owner`, the target is the damageable the hit data resolved, the damage is the
/// weapon's own `m_damageToDeal` — the value `SetNextDamageToDeal(eMeleeWeaponDamage, float)` fixed for this hit
/// and never a number this package computes — and the charge is the weapon's own current state: a swing the game
/// labelled as one of its two charge-hit states is charged, one of the two plain hit states is not, and any other
/// state leaves the port absent rather than guessed.</item>
/// <item><b>Third person</b> — `MeleeWeaponThirdPerson.DoAttackDamage` is a swing this machine replays for
/// somebody else, which in this build is the bot melee action's strike: the damage arrives as the body's own
/// argument and the attacker is the weapon's `Owner`. A third-person swing has no charge flag this build can
/// read — the light/heavy choice is made by the wielder's own first-person weapon — so the port stays absent.</item>
/// </list>
/// A shove is not a swing: the first-person body is entered with `isPush` set for the push damage and that hit is
/// left to the push path, so one row does not carry two actions.
///
/// The row's third source is a remote player's swing, which the host observes from the replicated melee damage;
/// that half is <see cref="WeaponMeleeRemoteFacts"/> and it publishes through <see cref="RemoteHit"/> on this
/// observer, so both halves share one ledger and one host gate. The gate is the adapter's own, so a client
/// publishes nothing and its swing leaves the host only as that replicated
/// damage.
///
/// Nothing here applies damage, reads health or counts a swing: every published value is a read of the call the
/// game is already making (the remote half's damage is the health its own receive body took).
/// </summary>
internal sealed class WeaponMeleeHitFacts : IDisposable
{
    /// <summary>The live observer, or null before the session is built and after it is disposed.</summary>
    internal static WeaponMeleeHitFacts? Current { get; private set; }

    /// <summary>Whether the remote receive window has a live reader before it touches native health.</summary>
    internal bool ObservesRemote => !_disposed && !Closed(_gate) && _adapter.Authoritative();

    private readonly EquipmentNativeAdapter _adapter;
    private readonly IForgeEngine _kernel;
    private readonly WeaponLog _log;
    /// <summary>The one ledger both native sources claim through, so one landed hit is published once however many
    /// bodies observed it. `WeaponMeleeRemoteFacts` is the other source.</summary>
    private readonly MeleeHitLedger _ledger = new();
    // WHY: P5 — the melee-hit row's gate, taken once at attach instead of a string lookup per hit.
    private readonly RuntimeSubscriptionGate _gate;
    private long _world = -1, _sequence;
    private bool _disposed;

    internal WeaponMeleeHitFacts(EquipmentNativeAdapter adapter, IForgeEngine kernel, WeaponLog log)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gate = adapter.GateOf(WeaponMeleeHitContract.MeleeHitBinding);
    }

    /// <summary>Installs the observer. Called by the session before its hooks are patched, so no hook can run
    /// over a half-built observer.</summary>
    internal static WeaponMeleeHitFacts Attach(EquipmentNativeAdapter adapter, IForgeEngine kernel, WeaponLog log)
    {
        if (Current != null) throw new InvalidOperationException("Melee hit facts are single-instance.");
        var facts = new WeaponMeleeHitFacts(adapter, kernel, log);
        Current = facts;
        return facts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>One target of the local player's own swing. The damage is the weapon's `m_damageToDeal`, which is
    /// the value the swing fixed before the hit entry ran; a hit entered as a push is the push path and publishes
    /// nothing here.</summary>
    internal void SwingHit(MeleeWeaponFirstPerson? weapon, MeleeWeaponDamageData? data, bool isPush)
    {
        if (weapon == null || data == null || isPush) return;
        _log.Skipped(WeaponMeleeHitContract.MeleeHitBinding, "hook-reached", "first-person");
        if (ClientWeaponReport.Reporting)
        {
            if (Charge(weapon.CurrentStateName) is { } charged) ClientWeaponReport.Current?.MeleeCharged(weapon, charged);
            return;
        }
        Publish(weapon, data.damageGO, weapon.m_damageToDeal, Charge(weapon.CurrentStateName), "first-person");
    }

    /// <summary>One target of a swing this machine runs for somebody else: the damage is the body's own argument,
    /// and the charge is the one port a third-person swing cannot answer.</summary>
    internal void ThirdPersonHit(MeleeWeaponThirdPerson? weapon, GameObject? damageGO, float damage, bool isPush)
    {
        if (weapon == null || isPush) return;
        _log.Skipped(WeaponMeleeHitContract.MeleeHitBinding, "hook-reached", "third-person");
        Publish(weapon, damageGO, damage, null, "third-person");
    }

    /// <summary>The row's second native source: a remote player's swing, observed on the host from the replicated
    /// melee damage. The attacker and the limb come from the packet, the target is the enemy life the receiver
    /// belongs to, and the damage is the health the receive body really took — so the charge is the one port this
    /// source cannot answer and stays absent.
    ///
    /// The gate is the ledger's own rule: a hit whose attacker is this machine's own player or a bot is already
    /// published by the hit entry that performed it, so this half publishes neither. A claim already held for the
    /// same attacker, target and limb is the same hit observed twice and is skipped.</summary>
    internal void RemoteHit(PlayerAgent? attacker, EnemyAgent? enemy, int limb, float damage, uint category = 0)
    {
        if (attacker == null || enemy == null) return;
        SyncWorld();
        if (!_adapter.Authoritative()) return;
        var owner = attacker.Owner;
        if (owner == null) return;
        bool isLocal, isBot;
        try { isLocal = owner.IsLocal; isBot = owner.IsBot; }
        catch (Exception failure) { NativeCallFailure.Note("WeaponMeleeHitFacts.RemoteHit", failure); return; }
        if (!MeleeHitLedger.PublishesRemote(isLocal, isBot)) return;
        var source = _adapter.OwnerOf(attacker);
        if (source == null)
        {
            Unpublished("owner-unresolved", "remote-receive");
            return;
        }
        var target = _kernel.ResolveEntityInstance(EntityKinds.Enemy, enemy);
        if (target == null)
        {
            Unpublished("target-unresolved", "remote-receive", "enemy", enemy.GlobalID);
            return;
        }
        // The packet names an attacker and a category: the held item that matches is the equipment, and the charge is that client's report.
        var report = ClientWeaponReport.Current;
        PublishResolved(source, target, limb, damage, report?.TakeMeleeCharged(attacker), report?.HeldEquipment(attacker, category), "remote-receive");
    }

    /// <summary>Publishes one hit, once per target. Every port is required except the two the game really leaves
    /// open: an unresolvable attacker or target is not a hit this row can name and publishes nothing at all, while
    /// a target that is not a body part and a swing with no charge state answer their port with an explicit null — the
    /// row declares both nullable, not optional, and the kernel refuses a fact that leaves such a port out.</summary>
    private void Publish(ItemEquippable weapon, GameObject? damageGO, float damage, bool? charged, string via)
    {
        SyncWorld();
        if (!_adapter.Authoritative()) { _log.Skipped(WeaponMeleeHitContract.MeleeHitBinding, "not-authoritative", via); return; }
        var source = _adapter.OwnerOf(weapon);
        if (source == null)
        {
            Unpublished("owner-unresolved", via);
            return;
        }
        if (!float.IsFinite(damage) || damage < 0f)
        {
            Unpublished("damage-not-finite", via);
            return;
        }
        var target = TargetOf(damageGO, out var limb);
        if (target == null)
        {
            // A damageable that is no agent (a door, a terminal, world geometry) is not a thing this row names: it is
            // a swing the row does not describe, so it leaves a trace record only. An agent the climb found but the
            // kernel cannot name is this package's own unresolved target and stays an error.
            var kind = AgentKind(damageGO, out var agentId);
            if (kind == null) Skipped("not-an-agent", via);
            else Unpublished("target-unresolved", via, kind, agentId);
            return;
        }
        PublishResolved(source, target, limb, damage, charged, _adapter.CurrentEntityOf(weapon), via);
    }

    /// <summary>Which kind of agent the hit's damage limb belongs to, and that agent's native id when it is an enemy
    /// (`-1` when none is readable), or null when the game object carries no agent limb at all. It reads
    /// <see cref="EquipmentNativeAdapter.DamageTarget"/>'s climb (<see cref="EquipmentNativeAdapter.Climb"/>) on the failure path only, to tell a thing the row
    /// never names from an agent the kernel does not know.</summary>
    private static string? AgentKind(GameObject? damageGO, out int agentId)
    {
        agentId = -1;
        var climb = EquipmentNativeAdapter.Climb(damageGO);
        if (climb.Kind == EquipmentNativeAdapter.LimbKind.Enemy)
        {
            if (climb.Enemy is { } enemy) agentId = enemy.GlobalID;
            return "enemy";
        }
        return climb.Kind == EquipmentNativeAdapter.LimbKind.Player ? "player" : null;
    }

    /// <summary>Publishes one already-resolved hit, and the one place the two native sources meet: the ledger
    /// decides whether this observation is a hit nobody has published yet. A claim another source already holds for
    /// the same attacker, target and limb is the same hit seen twice, so it is reported and dropped.</summary>
    private void PublishResolved(EntityReference source, EntityReference target, int limb, float damage, bool? charged,
        EntityReference? equipment, string via)
    {
        var world = _kernel.WorldEpoch;
        if (!_ledger.TryClaim(source, target, limb, _kernel.CurrentTick, world))
        {
            if (_log.Logs(RuntimeLogLevel.Trace))
                _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.FactSkipped, new RuntimeLogFacts
                {
                    Binding = WeaponMeleeHitContract.MeleeHitBinding,
                    Entity = target.Id,
                    Values = new[] { RuntimeLogValue.Of("reason", "deduplicated"), RuntimeLogValue.Of("via", via),
                        RuntimeLogValue.Of("source", source.Id), RuntimeLogValue.Of("limb", limb) }
                });
            return;
        }
        // Nothing is listening on the melee-hit row: the kernel would answer `no-consumer` for the event this body
        // is about to build, so the event value is never built.
        if (Closed(_gate)) { _log.Skipped(WeaponMeleeHitContract.MeleeHitBinding, "no-consumer", via); return; }
        var id = "gtfo.weapon.melee:" + Number(world) + ":" + Number(checked(++_sequence));
        var fact = _adapter.Begin(_gate, world);
        fact.Values.Entity(Blocks.TriggerCombatMeleeHit.OutSource, source);
        fact.Values.Entity(Blocks.TriggerCombatMeleeHit.OutTarget, target);
        fact.Values.Number(Blocks.TriggerCombatMeleeHit.OutDamage, damage);
        if (limb >= 0) fact.Values.Integer(Blocks.TriggerCombatMeleeHit.OutLimb, limb);
        else fact.Values.Null(Blocks.TriggerCombatMeleeHit.OutLimb);
        if (charged is { } value) fact.Values.Boolean(Blocks.TriggerCombatMeleeHit.OutCharged, value);
        else fact.Values.Null(Blocks.TriggerCombatMeleeHit.OutCharged);
        fact.Values.Entity(Blocks.TriggerCombatMeleeHit.OutEquipment, equipment); // null is explicitly unavailable, never the actor or held item.
        // The kernel records the outcome itself (`trigger.fired` / `event.rejected`), so no second line is written.
        var result = fact.Raise(id, world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(world));
        _log.Answered(WeaponMeleeHitContract.MeleeHitBinding, result, target.Id);
    }

    /// <summary>The entity a melee hit landed on, which is the adapter's own damage-target climb: the damage limb
    /// the game handed the hit names its own base agent, that agent's type decides which domain is asked, and the
    /// limb index is the part's own `m_limbID`. It is read from there rather than repeated here because the push
    /// row observes the same bodies and must name the same targets.</summary>
    private EntityReference? TargetOf(GameObject? damageGO, out int limb)
        => _adapter.DamageTarget(damageGO, out limb);

    /// <summary>The charge a swing carries, read from the weapon's own state. The game's set has exactly two
    /// charge-hit states and two plain hit states; every other member is a state this row does not describe, which
    /// is an absent port rather than a false one.</summary>
    private static bool? Charge(eMeleeWeaponState state) => state switch
    {
        eMeleeWeaponState.AttackChargeHitLeft or eMeleeWeaponState.AttackChargeHitRight => true,
        eMeleeWeaponState.AttackHitLeft or eMeleeWeaponState.AttackHitRight => false,
        _ => null
    };

    /// <summary>The host's own world, read on every use so a fact can never be stamped with an epoch the kernel
    /// has already left. The adapter's authority gate is the one every package fact shares; the sequence is this
    /// observer's own per-world counter and is reset with the world it belongs to.</summary>
    private void SyncWorld()
    {
        var world = _kernel.WorldEpoch;
        if (world != _world) { _world = world; _sequence = 0; }
    }

    /// <summary>A swing this row could not name: a refusal of this package's own, so it is an error-level record.</summary>
    private void Unpublished(string reason, string via, string? kind = null, int agentId = -1)
    {
        if (!_log.Logs(RuntimeLogLevel.Error)) return;
        var values = new List<RuntimeLogValue> { RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("via", via) };
        if (kind != null) values.Add(RuntimeLogValue.Of("kind", kind));
        if (agentId >= 0) values.Add(RuntimeLogValue.Of("agent", agentId));
        _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.FactUnpublished, new RuntimeLogFacts
        {
            Binding = WeaponMeleeHitContract.MeleeHitBinding,
            Values = values.ToArray()
        });
    }

    /// <summary>A swing this row does not describe, so nothing is published and nothing is wrong.</summary>
    private void Skipped(string reason, string via)
    {
        if (!_log.Logs(RuntimeLogLevel.Trace)) return;
        _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.FactSkipped, new RuntimeLogFacts
        {
            Binding = WeaponMeleeHitContract.MeleeHitBinding,
            Values = new[] { RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("via", via) }
        });
    }

    /// <summary>A declared row nobody listens on.</summary>
    private static bool Closed(RuntimeSubscriptionGate gate) => !gate.HasSubscribers;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

// The first-person per-target hit entry: the local player's own swing, once per entry of `HitsForDamage`. ForgeCore patches
// the body once (`GameHooks.MeleeAttackDamage`); this reads only what the body was handed and what the weapon already
// fixed for this hit, before every other patch of it, and leaves the attribution when the body returned or threw.
internal static class MeleeSwingDamage
{
    internal static void Subscribe() => ForgeCore.GameHooks.MeleeAttackDamage.Around(Enter,
        static (_, state, _) => WeaponCombatObserver.LeaveAttribution(state),
        static (_, state, _) => WeaponCombatObserver.LeaveAttribution(state));

    private static WeaponCombatObserver.Attribution? Enter((MeleeWeaponFirstPerson Weapon, MeleeWeaponDamageData Data, bool IsPush) call)
    {
        var state = WeaponNativeHooks.ObserveAttribution(WeaponFeature.Melee)?.Guard(static (session, weapon) => session.Combat.Attribute(weapon),
            call.Weapon, default(WeaponCombatObserver.Attribution?));
        WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (_, hit) => WeaponMeleeHitFacts.Current?.SwingHit(hit.Weapon, hit.Data, hit.Push),
            (Weapon: call.Weapon, Data: call.Data, Push: call.IsPush));
        return state;
    }
}
// The third-person per-target hit entry: a swing this machine runs for somebody else, which in this build is the
// bot melee action's strike state. The damage is the body's own argument, so no readback is involved.
[HarmonyPatch(typeof(MeleeWeaponThirdPerson), nameof(MeleeWeaponThirdPerson.DoAttackDamage))]
internal static class MeleeThirdPersonDamage
{
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Finalizer(WeaponCombatObserver.Attribution? __state)
        => WeaponCombatObserver.LeaveAttribution(__state);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(MeleeWeaponThirdPerson __instance, GameObject damageGO, float damage, bool isPush, out WeaponCombatObserver.Attribution? __state)
    {
        __state = WeaponNativeHooks.ObserveAttribution(WeaponFeature.Melee)?.Guard(static (session, weapon) => session.Combat.Attribute(weapon),
            __instance, default(WeaponCombatObserver.Attribution?));
        WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (_, call) => WeaponMeleeHitFacts.Current?.ThirdPersonHit(call.Weapon, call.Target, call.Damage, call.Push),
            (Weapon: __instance, Target: damageGO, Damage: damage, Push: isPush));
    }
}
