using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using Gear;
using HarmonyLib;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of <see cref="EquipmentEventContract"/>: the three equipment facts this package publishes,
/// each read where the game itself performs the thing the card names. Nothing here writes, and every published
/// reference is resolved through the adapter before the fact leaves.
///
/// <b>Flashlight.</b> `ItemEquippable.SetAttachedFlashlightEnabled(bool)` is the game's own write of the light
/// attached to an item (`catalog/native/native-runtime.json`, type `ItemEquippable`; ledger row
/// `vanilla-player.tool.flashlight` names the same member). The state is the argument the body was entered with,
/// which is why the hook is a postfix on that body and not a sample of a light component. Only a change is
/// published: an item re-told the state it already had is not a toggle. The one parameter is injected by position
/// (`__0`), because neither truth source states its name.
///
/// <b>Throw released.</b> `ItemEquippable.Throw(Vector3 velocity)` is the only native entry of a throw that
/// carries the velocity the card declares; the ledger's glow-stick row names that member and its parameter by
/// name. A `ThrowingWeapon` (fog repeller, grenades) never reaches this entry (A1 game run: the thrown fog
/// repeller fired no hook here), so its throw is published from the frame of its Update that sets `m_hasThrown`
/// (<see cref="ThrowUseChecked"/>), with the weapon's own `CalcThrust()` as the velocity.
///
/// <b>Melee push.</b> The push is the melee weapon's own state object `Gear.MWS_Push`: `Enter` is the push being
/// prepared, its private `SendAction` is the push really being pushed out, and
/// `MeleeWeaponFirstPerson.DoAttackDamage(data, isPush: true)` is the push reaching a target — the exact branch
/// the swing row of <see cref="WeaponMeleeHitFacts"/> skips, so one native call is never two facts. The weapon
/// behind a state object is its base's `m_weapon` (`Gear.MWS_Base`), which is where the actor and the equipment
/// come from. The `target` port is the card's one nullable output (published as an explicit null), so a push that reached something this build
/// cannot name still publishes the phase it really reached.
/// </summary>
internal sealed class EquipmentEventFacts : IDisposable
{
    /// <summary>The live observer, or null before the session is built and after it is disposed.</summary>
    internal static EquipmentEventFacts? Current { get; private set; }

    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<IForgeEngine?> _kernel;
    private readonly WeaponLog _log;
    /// <summary>The flashlight state each item was last told, keyed by the item's own native pointer, so only a
    /// change is published. It is dropped with the world, like every other pointer-keyed table in this package.
    /// </summary>
    private readonly Dictionary<IntPtr, bool> _lights = new();
    // WHY: P5 — the kernel's own refreshed gates, taken once at attach, read before any value is built.
    private readonly RuntimeSubscriptionGate _flashlightGate, _throwGate, _pushGate;
    private long _world = -1, _sequence;
    private bool _disposed;

    internal EquipmentEventFacts(EquipmentNativeAdapter adapter, Func<IForgeEngine?> kernel, WeaponLog log)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _flashlightGate = adapter.GateOf(EquipmentEventContract.FlashlightBinding);
        _throwGate = adapter.GateOf(EquipmentEventContract.ThrowReleasedBinding);
        _pushGate = adapter.GateOf(EquipmentEventContract.MeleePushBinding);
    }

    /// <summary>Installs the observer. Called by the session before its hooks are patched, so no hook can run over
    /// a half-built observer.</summary>
    internal static EquipmentEventFacts Attach(EquipmentNativeAdapter adapter, Func<IForgeEngine?> kernel, WeaponLog log)
    {
        if (Current != null) throw new InvalidOperationException("Equipment event facts are single-instance.");
        var facts = new EquipmentEventFacts(adapter, kernel, log);
        Current = facts;
        return facts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lights.Clear();
        _sequence = 0;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    // -------------------------------------------------------------------------------------------------------
    // Flashlight.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>One item's attached light was set. The equipment is the life this machine records for that item
    /// and the actor is that item's own owner; an item with neither is not a fact this row can name, so nothing is
    /// published and the remembered state is left alone — the next real change still reads as one.</summary>
    internal void FlashlightSet(ItemEquippable? item, bool on)
    {
        if (!Ready() || item == null) return;
        if (_lights.TryGetValue(item.Pointer, out bool was) && was == on) return;
        var equipment = _adapter.CurrentEntityOf(item);
        var actor = _adapter.OwnerOf(item);
        if (equipment == null || actor == null) return;
        _lights[item.Pointer] = on;
        if (Closed(_flashlightGate) || Begin(_flashlightGate, equipment, out var id) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerEquipmentFlashlight.OutActor, actor);
        fact.Values.Entity(Blocks.TriggerEquipmentFlashlight.OutEquipment, equipment);
        fact.Values.Boolean(Blocks.TriggerEquipmentFlashlight.OutOn, on);
        Raise(fact, id, equipment);
    }

    // -------------------------------------------------------------------------------------------------------
    // Throw released.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>One throwable left the hand, with the velocity the native body was entered with. A velocity with a
    /// non-finite component is not a reading, so the fact is refused by name rather than published with a number
    /// the game did not produce.</summary>
    internal void ThrowReleased(ItemEquippable? thrown, Vector3 velocity)
    {
        if (!Ready() || thrown == null) return;
        var item = _adapter.CurrentEntityOf(thrown);
        var actor = _adapter.OwnerOf(thrown);
        if (item == null || actor == null) return;
        if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z))
        {
            _log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.FactUnpublished, "velocity-not-finite", entity: item.Id,
                binding: EquipmentEventContract.ThrowReleasedBinding);
            return;
        }
        if (Closed(_throwGate) || Begin(_throwGate, item, out var id) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerEquipmentThrowReleased.OutActor, actor);
        fact.Values.Entity(Blocks.TriggerEquipmentThrowReleased.OutItem, item);
        fact.Values.Vector(Blocks.TriggerEquipmentThrowReleased.OutVelocity, velocity.x, velocity.y, velocity.z);
        Raise(fact, id, item);
    }

    // -------------------------------------------------------------------------------------------------------
    // Melee push.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>The push being prepared and the push being pushed out, both read from the state object the game
    /// runs them in.</summary>
    internal void PushState(MWS_Push? push, int phase)
    {
        if (!Ready() || push == null) return;
        PublishPush(push.m_weapon, phase, null);
    }

    /// <summary>The push reaching one target: the per-target branch of the first-person hit entry that the swing
    /// row skips. The target is the adapter's own damage-target climb, and a target it cannot name leaves the
    /// card's nullable port out instead of dropping the fact.</summary>
    internal void PushHit(MeleeWeaponFirstPerson? weapon, MeleeWeaponDamageData? data)
    {
        if (!Ready() || weapon == null || data == null) return;
        PublishPush(weapon, EquipmentEventContract.PushHit, _adapter.DamageTarget(data.damageGO, out _));
    }

    /// <summary>A client's push, admitted by <see cref="ClientWeaponReport"/>, on the host's copy of the weapon.</summary>
    internal void PushReported(Item weapon, byte phase, EntityReference? target)
    {
        if (Ready()) PublishPush(weapon, Math.Min((int)phase, EquipmentEventContract.PushHit), target);
    }

    /// <summary>One push fact. The actor is the weapon's own owner and the equipment is the life this machine
    /// records for it; a push whose weapon has neither publishes nothing, because an actorless or equipmentless
    /// push names nothing a plan could act on.</summary>
    private void PublishPush(Item? weapon, int phase, EntityReference? target)
    {
        if (weapon == null) return;
        var equipment = _adapter.CurrentEntityOf(weapon);
        var actor = _adapter.OwnerOf(weapon);
        if (equipment == null || actor == null || Closed(_pushGate) || Begin(_pushGate, equipment, out var id) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerCombatMeleePush.OutActor, actor);
        fact.Values.Entity(Blocks.TriggerCombatMeleePush.OutEquipment, equipment);
        fact.Values.Enum(Blocks.TriggerCombatMeleePush.OutPhase, phase);
        // `target` is the row's one nullable port, not an optional one: a push that reached nothing this build can
        // name answers it with an explicit null, because the kernel refuses a fact that leaves it out.
        fact.Values.Entity(Blocks.TriggerCombatMeleePush.OutTarget, target);
        Raise(fact, id, equipment);
    }

    // -------------------------------------------------------------------------------------------------------
    // Shared plumbing.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>Begins one fact, or answers null while the kernel is not running. Every caller has already read its
    /// row's cached gate, so the event value is never built for a fact nobody subscribes to. A rejection is never
    /// swallowed: the kernel writes it as `event.rejected`, and a queued fact as `trigger.fired`, both under this
    /// provider's binding, so no second line of this package's own is written.</summary>
    private EventWriter? Begin(RuntimeSubscriptionGate gate, EntityReference subject, out string id)
    {
        id = "";
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return null;
        id = "gtfo.weapon." + gate.BindingId + ":" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence));
        return _adapter.Begin(gate, kernel.WorldEpoch);
    }

    private void Raise(EventWriter fact, string id, EntityReference subject)
    {
        var kernel = _kernel()!;
        fact.Raise(id, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick), subject.Id);
    }

    /// <summary>The gate every body above starts with: this session's authority and a kernel that is really
    /// running. The remembered flashlight states are keyed by native pointers of objects in one world, so they are
    /// dropped when the world changes.</summary>
    private bool Ready()
    {
        if (_disposed) return false;
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (kernel.WorldEpoch != _world) { _world = kernel.WorldEpoch; _lights.Clear(); _sequence = 0; }
        return _adapter.Authoritative();
    }


    /// <summary>A declared row nobody listens on.</summary>
    private static bool Closed(RuntimeSubscriptionGate gate) => !gate.HasSubscribers;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

// The game's own write of an item's attached light. The state is the argument the body was entered with; ForgeCore
// patches the setter once (`GameHooks.FlashlightSet`) and this reads after the game applied it and every other patch ran.
internal static class FlashlightToggled
{
    internal static void Subscribe() => ForgeCore.GameHooks.FlashlightSet.After(Set);

    private static void Set((ItemEquippable Item, bool Enabled) call, bool ran)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (session, call) => { if (!session.ClientFacts.Flashlight(call.Item1, call.Item2)) EquipmentEventFacts.Current?.FlashlightSet(call.Item1, call.Item2); }, call);
}

// The one native entry of a throw that carries a velocity. `velocity` is the game's own parameter name for it.
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.Throw))]
internal static class ItemThrown
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemEquippable __instance, Vector3 velocity)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (session, call) => { if (!session.ClientFacts.Throw(call.Item1, call.Item2)) EquipmentEventFacts.Current?.ThrowReleased(call.Item1, call.Item2); }, (__instance, velocity));
}

// The push state object's own two ends, both from `Enter` (ForgeCore's `GameHooks.PushEntered`, subscribed here): the
// state being entered is the push being prepared, and the push really being pushed out is `SendAction`, which `Enter`
// inlines as its last step (the same `PlayerSync.SendGenericInteract(7)`); `SendAction` has no caller of its own in this
// build, so a patch on it never ran. After a body that ran, the prepare fact and then the release fact.
internal static class MeleePushPrepared
{
    internal static void Subscribe() => ForgeCore.GameHooks.PushEntered.After(Prepared);

    private static void Prepared(MWS_Push push, bool ran)
        => WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (session, call) =>
        {
            var (entered, sent) = call;
            if (!session.ClientFacts.Push(entered.m_weapon, 0, null)) EquipmentEventFacts.Current?.PushState(entered, EquipmentEventContract.PushPrepare);
            if (sent && !session.ClientFacts.Push(entered.m_weapon, 1, null)) EquipmentEventFacts.Current?.PushState(entered, EquipmentEventContract.PushRelease);
        }, (push, ran));
}

// The per-target hit entry, entered as a push (ForgeCore's `GameHooks.MeleeAttackDamage`, subscribed here). This is the
// branch `MeleeSwingDamage` explicitly skips, so the two never describe the same call.
internal static class MeleePushDamage
{
    internal static void Subscribe() => ForgeCore.GameHooks.MeleeAttackDamage.Before(Entered);

    private static void Entered((MeleeWeaponFirstPerson Weapon, MeleeWeaponDamageData Data, bool IsPush) call)
    {
        if (!call.IsPush) return;
        WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (session, hit) =>
        {
            if (!session.ClientFacts.Push(hit.Item1, 2, hit.Item2?.damageGO)) EquipmentEventFacts.Current?.PushHit(hit.Item1, hit.Item2);
        }, (call.Weapon, call.Data));
    }
}
