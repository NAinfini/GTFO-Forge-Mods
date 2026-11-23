using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using HarmonyLib;
using UnityEngine;
using ForgeCore;

namespace ForgeWeapon.Native;

/// <summary>
/// The three deployed-device facts of <see cref="WeaponDeployableFactsContract"/>, read from the bodies that
/// really produce them and published the way this package's other facts are.
///
/// A sentry shot is the firing component's own master update, and the fact is the ammunition that update moved:
/// the device core's own <c>Ammo</c> read before and after the body. A drop is one shot; a value that reached zero
/// is the depletion. Neither is inferred from a clip this package tracks, and the instance's own
/// <c>OnBulletFired</c>/<c>OnAmmoDepleated</c> callbacks are deliberately not hooked — they are presentation
/// actions the instance assigns to itself, while the ammunition is the fact worth reading.
///
/// A glue-gun shot is the hand-held tool's own launch bodies, deduped per frame so a burst of glue reads as the
/// one trigger pull it was. That is the half of `e-glue` this package owns; the enemy that ends up stuck is the
/// enemy domain's own fact.
///
/// A detonation is the mine's own trigger, and the position is the device's own transform rather than the trigger's
/// parameter, which is a detection range and not a place.
///
/// Every hook is inert unless the session is live, exactly like the package's other observers: the bodies below
/// only choose when to read.
/// </summary>
internal sealed class WeaponDeployableFacts : IDisposable
{
    /// <summary>The live observer, or null before the session is built and after it is disposed.</summary>
    internal static WeaponDeployableFacts? Current { get; private set; }

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Func<IntPtr, EntityReference?> _deployedOf;
    private readonly Func<Item?, EntityReference?> _equipmentOf;
    private readonly Func<Item?, EntityReference?> _ownerOf;
    private readonly Func<EntityReference?, bool> _isCurrent;
    private readonly EquipmentIdentitySession _identity;
    // WHY: P5 — the three rows' gates, taken once at attach.
    private readonly RuntimeSubscriptionGate _firedGate, _depletedGate, _detonatedGate;
    private readonly WeaponLog _log;
    // An enum port travels as the index of its member in the port's whole set (the kernel refuses a member name as
    // `invalid-enum`), so the three members this observer publishes are resolved once from the SDK's own sets.
    private static readonly int SentryGunKind = Member("equipment_kind", "sentry_gun");
    private static readonly int GlueGunKind = Member("equipment_kind", "glue_gun");
    private static readonly int PrimaryAction = Member("equipment_action", "primary");
    private readonly Dictionary<IntPtr, long> _glueFrame = new();
    // The devices whose missing identity was already reported in this world.
    private readonly HashSet<IntPtr> _unidentified = new();
    private long _world = -1, _sequence;
    private bool _disposed;

    internal WeaponDeployableFacts(IForgeEngine kernel, Func<bool> canExecute, Func<IntPtr, EntityReference?> deployedOf,
        Func<Item?, EntityReference?> equipmentOf, Func<Item?, EntityReference?> ownerOf, Func<EntityReference?, bool> isCurrent,
        EquipmentIdentitySession identity, WeaponLog log)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _deployedOf = deployedOf ?? throw new ArgumentNullException(nameof(deployedOf));
        _equipmentOf = equipmentOf ?? throw new ArgumentNullException(nameof(equipmentOf));
        _ownerOf = ownerOf ?? throw new ArgumentNullException(nameof(ownerOf));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _firedGate = identity.GateOf(WeaponDeployableFactsContract.FiredBinding);
        _depletedGate = identity.GateOf(WeaponDeployableFactsContract.AmmoDepletedBinding);
        _detonatedGate = identity.GateOf(WeaponDeployableFactsContract.DetonatedBinding);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Installs the observer. Called by the session before its hooks are patched, so no hook can run
    /// over a half-built observer.</summary>
    internal static WeaponDeployableFacts Attach(IForgeEngine kernel, Func<bool> canExecute,
        Func<IntPtr, EntityReference?> deployedOf, Func<Item?, EntityReference?> equipmentOf,
        Func<Item?, EntityReference?> ownerOf, Func<EntityReference?, bool> isCurrent, EquipmentIdentitySession identity,
        WeaponLog log)
    {
        if (Current != null) throw new InvalidOperationException("Deployed-device facts are single-instance.");
        var facts = new WeaponDeployableFacts(kernel, canExecute, deployedOf, equipmentOf, ownerOf, isCurrent, identity, log);
        Current = facts;
        return facts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _glueFrame.Clear();
        _unidentified.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private bool _sentryAsked;

    /// <summary>
    /// Puts on the sentry's per-frame firing hook the first tick something reads what it reports: a plan listening to
    /// the sentry's shot or depletion row, or to a shot/hit/damage fact (<paramref name="shotsObserved"/>), because
    /// the hook's depth count is also what keeps a sentry's bullets out of a player's open shot. Called on every
    /// advanced tick: one boolean until asked once (a gate with no subscriber table entry is open).
    /// </summary>
    internal void EnsureSentryHook(bool shotsObserved)
    {
        if (_sentryAsked || !shotsObserved && Closed(_firedGate) && Closed(_depletedGate)) return;
        _sentryAsked = true;
        WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.SentryFire);
    }

    /// <summary>
    /// One sentry firing update, read as the ammunition the body moved. <paramref name="before"/> is what the prefix
    /// read and carried in Harmony's own state, so no table is kept between the two halves. A drop is one shot, and
    /// the shot's own `ammo` port is the value after it. A body that ended at zero publishes both facts, in that
    /// order: the shot happened, and it was the last one the device had.
    /// </summary>
    internal void SentryFired(SentryGunInstance_Firing_Bullets firing, float before)
    {
        SyncWorld();
        if (!_canExecute() || !_kernel.Lifecycle.IsReadyHost) return;
        var core = firing.m_core;
        var after = core == null ? 0f : core.Ammo;
        if (after >= before) return;
        var deployed = Placed(core == null ? IntPtr.Zero : core.Pointer, WeaponDeployableFactsContract.FiredBinding);
        if (deployed == null) return;
        // WHY: F9, a payload is built only for a row somebody listens on; the Placed refusal above is logged either way.
        // A sentry fires on its own: no player pulled a trigger, so the nullable actor port is present and null. The
        // row's shape has every declared port, so a missing key is refused as `missing-field`.
        if (!Closed(_firedGate))
        {
            var fired = Begin(_firedGate, out var id, out var world);
            fired.Values.Entity(Blocks.TriggerEquipmentFired.OutDevice, deployed);
            fired.Values.Null(Blocks.TriggerEquipmentFired.OutActor);
            fired.Values.Enum(Blocks.TriggerEquipmentFired.OutEquipmentKind, SentryGunKind);
            fired.Values.Enum(Blocks.TriggerEquipmentFired.OutEquipmentAction, PrimaryAction);
            fired.Values.Integer(Blocks.TriggerEquipmentFired.OutAmmo, (int)MathF.Max(0f, after));
            Raise(fired, id, world);
        }
        if (after <= 0f && !Closed(_depletedGate))
        {
            var depleted = Begin(_depletedGate, out var id, out var world);
            depleted.Values.Entity(Blocks.TriggerEquipmentAmmoDepleted.OutDevice, deployed);
            depleted.Values.Null(Blocks.TriggerEquipmentAmmoDepleted.OutActor);
            Raise(depleted, id, world);
        }
    }

    /// <summary>The ammunition the firing component's device holds, or zero when its core is gone: the prefix half.</summary>
    internal static float AmmoOf(SentryGunInstance_Firing_Bullets firing)
    {
        var core = firing.m_core;
        return core == null ? 0f : core.Ammo;
    }

    /// <summary>One glue-gun launch. A burst fires several projectiles in one trigger pull, so a second launch
    /// inside the same frame is the same shot and is not published again.</summary>
    internal void GlueFired(Item? tool)
    {
        if (tool == null) return;
        SyncWorld();
        if (!_canExecute() || !_kernel.Lifecycle.IsReadyHost) return;
        var pointer = tool.Pointer;
        var frame = Time.frameCount;
        if (_glueFrame.TryGetValue(pointer, out var last) && last == frame) return;
        _glueFrame[pointer] = frame;
        // The hand-held glue gun is an equipment life, not a placement: its device port is the tool the launch
        // came from, and the actor is that tool's own owner.
        if (Closed(_firedGate)) return;
        var device = EquipmentOf(tool);
        if (device == null) return;
        var actor = _ownerOf(tool);
        var fact = Begin(_firedGate, out var id, out var world);
        fact.Values.Entity(Blocks.TriggerEquipmentFired.OutDevice, device);
        fact.Values.Entity(Blocks.TriggerEquipmentFired.OutActor, actor);
        fact.Values.Enum(Blocks.TriggerEquipmentFired.OutEquipmentKind, GlueGunKind);
        fact.Values.Enum(Blocks.TriggerEquipmentFired.OutEquipmentAction, PrimaryAction);
        // The glue gun's charge is not read by this observer, so the nullable port is present and null.
        fact.Values.Null(Blocks.TriggerEquipmentFired.OutAmmo);
        Raise(fact, id, world);
    }

    /// <summary>One mine detonation, at the device's own position. The trigger component is not the item: the
    /// instance the placement table tracks is reached through the component's own core, and a component whose core
    /// is already gone publishes nothing.</summary>
    internal void Detonated(MineDeployerInstance_Detonate_Explosive? trigger)
    {
        var device = trigger == null ? null : trigger.m_core?.TryCast<Item>();
        if (device == null) return;
        SyncWorld();
        if (!_canExecute() || !_kernel.Lifecycle.IsReadyHost) return;
        var position = EquipmentNativeAdapter.Position(device);
        if (position == null) return;
        var deployed = Placed(device.Pointer, WeaponDeployableFactsContract.DetonatedBinding);
        if (deployed == null || Closed(_detonatedGate)) return;
        var fact = Begin(_detonatedGate, out var id, out var world);
        fact.Values.Entity(Blocks.TriggerEquipmentDetonated.OutDevice, deployed);
        fact.Values.Vector(Blocks.TriggerEquipmentDetonated.OutPosition, position[0], position[1], position[2]);
        Raise(fact, id, world);
    }

    /// <summary>The equipment life a hand-held tool is, asked of the same slot table the placement facts use: the
    /// materialized item's own slot, which the identity session already recorded. A tool this build has not
    /// recorded answers null and its fact is not published, because the row's `device` port is required.</summary>
    private EntityReference? EquipmentOf(Item tool) => _equipmentOf(tool);

    /// <summary>The deployed instance a native object is, or null. Only a reference the session's own resolver
    /// still answers for is returned, so a device whose life already ended can never reach a fact.</summary>
    private EntityReference? Placed(IntPtr device, string binding)
    {
        var reference = _deployedOf(device);
        if (reference != null && _isCurrent(reference)) return reference;
        // A sentry another mod stood off the tool slot (a summoned turret) has no identity on purpose (see Placed in the adapter):
        // no fact for it, and nothing went wrong.
        if (ForgeCore.Gear.IsToolSlotKept(device)) return null;
        // A standing device with no current identity fires every shot it has; the refusal is the same for all of
        // them, so it is reported once per device and world, with the device named, instead of once per bullet.
        if (_unidentified.Add(device) && _log.Logs(RuntimeLogLevel.Error))
            _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.FactUnpublished, new RuntimeLogFacts
            {
                Binding = binding,
                Values = new[] { RuntimeLogValue.Of("reason", "device-not-current"),
                    RuntimeLogValue.Of("device", "0x" + device.ToInt64().ToString("x", CultureInfo.InvariantCulture)) }
            });
        return null;
    }

    /// <summary>Begins one fact. Every caller has already read its row's cached gate, so a row nobody listens on
    /// never builds an event value.</summary>
    private EventWriter Begin(RuntimeSubscriptionGate gate, out string id, out long world)
    {
        world = _kernel.WorldEpoch;
        id = "gtfo.equipment.fired:" + Number(world) + ":" + Number(checked(++_sequence));
        return _identity.Begin(gate, world);
    }

    /// <summary>The kernel records the outcome itself (`trigger.fired` / `event.rejected`), so a sentry firing with
    /// nothing refused writes no line of this package's own at any level.</summary>
    private void Raise(EventWriter fact, string id, long world)
        => fact.Raise(id, world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(world));

    /// <summary>The world this observer's facts belong to, read from the kernel on every use so a fact can never
    /// be stamped with an epoch the kernel has already left. The tables are keyed by native pointers of objects in
    /// one world, so they are dropped with it rather than carried into the next one.</summary>
    private void SyncWorld()
    {
        var world = _kernel.WorldEpoch;
        if (world != _world) { _world = world; _glueFrame.Clear(); _unidentified.Clear(); _sequence = 0; }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A declared row nobody listens on.</summary>
    private static bool Closed(RuntimeSubscriptionGate gate) => !gate.HasSubscribers;

    private static int Member(string set, string member) => WeaponEnumMembers.Index(set, member);
}

// The sentry's own firing update, read as the pair of ammunition values around it: the prefix reads what the
// device had and hands it to the postfix through Harmony's own `__state`, the postfix reads what it has and
// publishes the difference as one shot. The update is the master's own body — the client copy runs
// `UpdateFireClient` — so the fact is published where the ammunition really moves. NaN is "the prefix did not
// read", which the postfix answers by doing nothing, exactly as a missing remembered value did before.
[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireMaster))]
internal static class SentryFired
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(SentryGunInstance_Firing_Bullets __instance, out float __state)
    {
        // Entered before anything can return, and left by the finalizer below on every exit path.
        WeaponCombatObserver.DeployableFiringDepth++;
        using var measured = HookCounters.Measure(HookSection.SentryFireMaster);
        __state = float.NaN;
        if (WeaponNativeSession.Offer(WeaponFeature.Sentry) is not { } session || WeaponDeployableFacts.Current == null) return;
        __state = session.Guard(static (_, firing) => WeaponDeployableFacts.AmmoOf(firing), __instance, float.NaN);
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Finalizer()
    {
        if (WeaponCombatObserver.DeployableFiringDepth > 0) WeaponCombatObserver.DeployableFiringDepth--;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunInstance_Firing_Bullets __instance, float __state)
    {
        using var measured = HookCounters.Measure(HookSection.SentryFireMaster);
        if (float.IsNaN(__state)) return;
        // WHY: P5 - the common frame fires nothing; `SentryFired` returns at this same comparison, so the guard is only entered for a drop.
        if (WeaponDeployableFacts.AmmoOf(__instance) >= __state) return;
        WeaponNativeSession.Offer(WeaponFeature.Sentry)?.Guard(static (_, call) => WeaponDeployableFacts.Current?.SentryFired(call.Item1, call.Item2),
            (__instance, __state));
    }
}

// The glue gun's two launch bodies. Either one is a trigger pull; the observer dedupes a burst's several
// projectiles into the one shot they were. ForgeCore patches the burst once (`GameHooks.GlueBurstFired`); it is read
// after every other patch of it.
internal static class GlueGunBurst
{
    internal static void Subscribe() => ForgeCore.GameHooks.GlueBurstFired.After(Fired);

    private static void Fired(GlueGun gun, bool ran)
        => WeaponNativeSession.Offer(WeaponFeature.Devices)?.Guard(static (session, gun) => { if (!session.ClientFacts.Glue(gun)) WeaponDeployableFacts.Current?.GlueFired(gun); }, gun);
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.FireSingle))]
internal static class GlueGunSingle
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(GlueGun __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Devices)?.Guard(static (session, gun) => { if (!session.ClientFacts.Glue(gun)) WeaponDeployableFacts.Current?.GlueFired(gun); }, __instance);
}

// The mine's own trigger. `DoExplode` is the damage half and runs from this one, so hooking the trigger publishes
// once per detonation rather than once per damage application.
[HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), nameof(MineDeployerInstance_Detonate_Explosive.TriggerDetonate))]
internal static class MineDetonated
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MineDeployerInstance_Detonate_Explosive __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Devices)?.Guard(static (_, trigger) => WeaponDeployableFacts.Current?.Detonated(trigger), __instance);
}
