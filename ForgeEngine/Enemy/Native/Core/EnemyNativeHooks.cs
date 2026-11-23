using System;
using System.Collections.Generic;
using System.Linq;
using Agents;
using Enemies;
using HarmonyLib;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The package's native callback classes, grouped by the player-visible function each supports
/// (<see cref="EnemyHookFeatures"/>). Each class is one `[HarmonyPatch]` target with static prefix/postfix/finalizer
/// callbacks; ForgeEngine is the only installer, so this file is where the package hands them over
/// (<see cref="Declare"/>) and nothing in the package patches by itself.
/// `ForgeEnemy/tests/HookInstall` pins that every `[HarmonyPatch]` class in the built assembly belongs to exactly one
/// function.</summary>
internal static class EnemyNativeHooks
{
    /// <summary>Every callback class handed to the Runtime, in declaration order: the classes of every function that is
    /// not withdrawn by evidence.</summary>
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(EnemyHookFeatures.Table
        .Where(feature => feature.Evidence == null).SelectMany(feature => feature.Hooks).ToArray());

    /// <summary>Callback classes held back from the Runtime, each with the reason. They stay in the source and nothing
    /// installs them: the function that owns one is not available until the reason is resolved.</summary>
    internal static IReadOnlyDictionary<Type, string> Pending { get; } = EnemyHookFeatures.Table
        .Where(feature => feature.Evidence != null).SelectMany(feature => feature.Hooks.Select(hook => (hook, feature.Evidence!)))
        .ToDictionary(pair => pair.hook, pair => pair.Item2);

    /// <summary>Hands <see cref="Types"/> to the Runtime in one call and answers which functions stand. A class the
    /// Runtime refuses withdraws only the function that owns it, with the Runtime's reason; so does a refused
    /// damage-kind shared entry (<paramref name="damageKindEntries"/>, null when both stand) and a function withdrawn
    /// by evidence. Every other function keeps working.</summary>
    internal static EnemyHookAvailability Declare(Func<string?> damageKindEntries)
    {
        var direct = new Dictionary<EnemyFeature, string>();
        foreach (var feature in EnemyHookFeatures.Table)
            if (feature.Evidence != null) direct[feature.Id] = "not declared: " + feature.Evidence;
        foreach (var refusal in ForgeEngine.GameBindings.NativeHookRegistration.Declare(EnemyModule.ProviderId, Types))
        {
            var feature = EnemyHookFeatures.Of(refusal.Callbacks) ?? throw new InvalidOperationException("enemy-hook-unowned " + refusal.Callbacks.Name);
            var detail = refusal.Callbacks.Name + "=" + refusal.Reason;
            direct[feature] = direct.TryGetValue(feature, out var earlier) ? earlier + "; " + detail : detail;
        }
        if (damageKindEntries() is { } shared)
            direct[EnemyFeature.DamageKind] = direct.TryGetValue(EnemyFeature.DamageKind, out var earlier) ? earlier + "; " + shared : shared;
        foreach (var (feature, source, subscribe) in CoreSubscriptions)
        {
            if (Subscribed.Contains(source)) continue;
            try { subscribe(); Subscribed.Add(source); }
            catch (Exception error)
            {
                var detail = source + "=" + error.Message;
                direct[feature] = direct.TryGetValue(feature, out var earlier) ? earlier + "; " + detail : detail;
            }
        }
        return EnemyHookFeatures.Resolve(direct);
    }

    /// <summary>The game methods ForgeCore patches once for every package (<see cref="ForgeCore.GameHooks"/>): the function stands on
    /// the subscription like on a declared class, but Runtime installs nothing for it. A bracket has no unsubscribe and its handlers
    /// reach the current session themselves, so each subscribes once per process.</summary>
    private static readonly (EnemyFeature Feature, string Source, Action Subscribe)[] CoreSubscriptions =
    {
        // After runs in reverse subscription order, so the spawn fact (subscribed first) runs after the registration it reads.
        (EnemyFeature.SpawnFact, "GameHooks.EnemySyncSpawned (spawn-fact)", EnemyNodeSpawned.Subscribe),
        (EnemyFeature.Tracking, "GameHooks.EnemySyncSpawned (tracking)", EnemyTracking.SubscribeSpawned),
        (EnemyFeature.Tracking, "GameHooks.EnemySyncDespawned (tracking)", EnemyTracking.SubscribeDespawned),
        (EnemyFeature.Tracking, "GameHooks.EnemyDespawned", EnemyTracking.SubscribeAgentDespawned),
        (EnemyFeature.Tracking, "GameHooks.EnemyDestroyed", EnemyTracking.SubscribeAgentDestroyed),
        (EnemyFeature.Damage, "GameHooks.EnemyDamageReceived", EnemyDamage.Subscribe),
        (EnemyFeature.Death, "GameHooks.EnemyDead", EnemyDeathStarted.Subscribe),
        (EnemyFeature.LimbBreak, "GameHooks.EnemyLimbDestroyed", EnemyLimbBroken.Subscribe),
        (EnemyFeature.CapCost, "GameHooks.EnemyModeChanged", EnemyCapCostAllocation.Subscribe),
        (EnemyFeature.Appearance, "GameHooks.EnemySyncDespawned (appearance)", EnemyAppearanceRecycled.Subscribe),
        (EnemyFeature.ScoutAbilities, "GameHooks.ScoutScreamActivated", EnemyScreamActivateHook.Subscribe),
        (EnemyFeature.ScannerAbilities, "GameHooks.ScoutDetectionEntered", EnemyScannerScoutHook.Subscribe),
        (EnemyFeature.DamageKind, "GameHooks.EnemyExplosionReceived", EnemyReceiveExplosionKind.Subscribe),
        (EnemyFeature.Scream, "GameHooks.EnemyScreamed", EnemyScreamHook.Subscribe),
        (EnemyFeature.Waves, "GameHooks.WaveStopped", EnemyWaveStop.Subscribe),
        (EnemyFeature.Wake, "GameHooks.HibernateWakeActivated", EnemyWakeActivation.Subscribe),
        (EnemyFeature.Behavior, "GameHooks.EnemyTargetInjected", EnemyTargetInjected.Subscribe),
        (EnemyFeature.Noise, "GameHooks.HibernatingNoiseHeard", EnemyHibernatingNoiseHook.Subscribe),
        (EnemyFeature.Look, "GameHooks.HibernateWakeupStarted", EnemyWakeupGlow.Subscribe),
        (EnemyFeature.ScannerAbilities, "GameHooks.HibernateDetectionStarted", EnemyScannerDetectionStartHook.Subscribe),
        (EnemyFeature.ScannerAbilities, "GameHooks.HibernateDetectionEnded", EnemyScannerDetectionEndHook.Subscribe)
    };

    private static readonly HashSet<string> Subscribed = new(StringComparer.Ordinal);
}

internal sealed partial class EnemyModule
{
    private long _damageObservationGeneration = -1;
    private bool _damageObserved;

    internal bool NativeDamageFactsObserved
    {
        get
        {
            if (_damageObservationGeneration == _kernel.SubscriptionGeneration) return _damageObserved;
            _damageObserved = DamageWindowObserved || StaggerObserved;
            _damageObservationGeneration = _kernel.SubscriptionGeneration;
            return _damageObserved;
        }
    }
}
/// <summary>The generic enemy registration, subscribed to the game methods ForgeCore patches once for every package: the sync's spawn
/// (after the body, so the life exists) and the three ways a life leaves the level, each reported before the game tears anything down.
/// Each ends the tracked life through the same path, which reports the end to the kernel; a life already ended finds nothing to end.</summary>
internal static class EnemyTracking
{
    internal static void Spawned(EnemySync instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Tracking) is not { } session) return;
        var sync = instance;
        session.Guard(ref sync, static (EnemyModule module, ref EnemySync value) =>
        {
            if (value.m_agent != null) module.TrackSpawn(value.m_agent);
        });
    }

    internal static void Despawned(EnemySync instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Tracking) is not { } session) return;
        var sync = instance;
        session.Guard(ref sync, static (EnemyModule module, ref EnemySync value) =>
        {
            // Native synchronous boundary: capture the current life before any teardown.
            if (value.m_agent != null)
                module.CompleteDespawn(module.CaptureDespawn(value.m_agent));
        });
    }

    /// <summary>The agent's own despawn (back to the pool) and its destruction.</summary>
    internal static void AgentEnded(EnemyAgent enemy)
    {
        if (EnemyHost.Offer(EnemyFeature.Tracking) is not { } session) return;
        var ended = enemy;
        session.Guard(ref ended, static (EnemyModule module, ref EnemyAgent value) => module.TrackDespawn(value));
    }

    internal static void SubscribeSpawned() => ForgeCore.GameHooks.EnemySyncSpawned.After(static (call, _) => Spawned(call.Sync));
    internal static void SubscribeDespawned() => ForgeCore.GameHooks.EnemySyncDespawned.Before(Despawned);
    internal static void SubscribeAgentDespawned() => ForgeCore.GameHooks.EnemyDespawned.Before(AgentEnded);
    internal static void SubscribeAgentDestroyed() => ForgeCore.GameHooks.EnemyDestroyed.Before(AgentEnded);
}

// `ProcessReceivedDamage` is the busiest hook in this package and the one funnel every committed hit passes
// through, so both windows a received hit opens share this one before/after pair instead of racing two subscriptions
// on the same member. The health window (`BeforeDamage`/`AfterDamage`) publishes the pre-existing
// `damage_applied` (with the part `limbID` named) and `health_changed` facts; the instance window
// (`BeforeDamageInstance`/`AfterDamageInstance`) publishes `forge.trigger.combat.killed`. Both close in the order
// they opened, and each consumes its own token
// exactly once, so a replayed callback cannot publish twice. The closing half also reports
// `forge.trigger.combat.staggered`, the one fact of this call that opens no window: the reaction the hit asked
// for and the receiver's own applied answer are arguments the closing half already carries, and the reaction the enemy
// actually stands on is read back from it.
//
// ForgeCore patches the method once (`GameHooks.EnemyDamageReceived`, prefix Priority.First, postfix Priority.Last) and builds one
// small `EnemyDamageCall` per hit; the damage rule's amount goes back through `call.Damage`, which the patch writes into the
// receiver's own `damage` argument. `hitreact` and `limbID` are parameters of the receiver's own declaration (build 20403457:
// `ProcessReceivedDamage(float, Agent, Vector3, Vector3, ES_HitreactType, bool, int limbID, float,
// DamageNoiseLevel, uint)`, returning `bool`). `limbID` is
// the array position the caller named, not an `m_limbID`; `call.Applied` is the receiver's answer that the
// damage landed, which the receiving methods branch on, so an applied hit is distinguishable from a refused one.
internal static class EnemyDamage
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemyDamageReceived.Around(Before, After);

    internal static State Before(ForgeCore.EnemyDamageCall call)
    {
        using var measured = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyDamagePrefix);
        if (EnemyHost.Offer(EnemyFeature.Damage) is not { } session) return default;
        // `forge.action.combat.damage_rule` is answered here and nowhere else: the rule standing on this receiver
        // changes the amount before the receiver reads it, which is the only point in the game where a hit that
        // has not happened yet can still be changed. A build with no rule in it returns the amount it was handed. A kill
        // (`call.Unruled`, set only by ForgeCore `Combat.Kill`) keeps its amount whatever rule stands on the enemy.
        call.Damage = session.GuardDamageRule(call.Receiver, call.Damage, call.Source, call.Unruled);
        // Refresh the shared observation gate only when Runtime's subscription generation changes. A closed
        // group creates neither a health observation nor a damage-instance observation.
        if (!session.Module.NativeDamageFactsObserved) return default;
        var opening = new Opening { Receiver = call.Receiver, Source = call.Source, LimbId = call.LimbId, Position = call.Position };
        // PERF(F6): a static lambda, which the compiler caches once. Inputs and the two observations travel in
        // `opening` by reference.
        session.Guard(ref opening, static (EnemyModule module, ref Opening window) =>
        {
            window.Captured.Observed = true;
            window.Captured.Health = module.BeforeDamage(window.Receiver, window.LimbId, window.Position);
            window.Captured.Instance = module.BeforeDamageInstance(window.Receiver, window.Source, window.LimbId, window.Position);
        });
        return opening.Captured;
    }

    internal static void After(ForgeCore.EnemyDamageCall call, State state, bool ran)
    {
        if (!ran) return;
        using var measured = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyDamagePostfix);
        if (EnemyHost.Offer(EnemyFeature.Damage) is not { } session) return;
        var closing = new Closing { Receiver = call.Receiver, Hitreact = call.Hitreact, LimbId = call.LimbId, Applied = call.Applied, Window = state };
        // PERF(F6): a static lambda plus state by reference, so the closing half allocates no closure per hit.
        session.Guard(ref closing, static (EnemyModule module, ref Closing call) =>
        {
            if (call.Window.Observed)
            {
                module.AfterDamage(call.Receiver, call.Window.Health);
                module.AfterDamageInstance(call.Receiver, call.Window.Instance);
                // The stagger is the one fact of this window that needs no observation of its own: the reaction asked
                // for and the receiver's applied answer are both arguments of the call being closed, and the reaction
                // the enemy actually stands on is read back here. PERF(F6): its gate is read before the method, whose
                // own first checks reach the host's authority reads.
                if (module.StaggerObserved) module.AfterDamageStaggered(call.Receiver, call.Hitreact, call.Applied);
            }
            // An attachment part's crack stage is the host's reading of the limb this hit named: ReceiveBulletDamage
            // writes that limb's health (0x137EE79) before it calls this receiver, whatever the receiver answers.
            EnemyModelLimbCracks.AfterDamage(module, call.Receiver, call.LimbId);
        });
    }

    /// <summary>The two observations of one native call, carried as one token.</summary>
    internal struct State
    {
        internal bool Observed;
        internal EnemyModule.DamageObservation? Health;
        internal EnemyModule.DamageInstanceObservation? Instance;
    }

    /// <summary>The opening half's inputs and outputs, passed to the closure-free guard by reference.</summary>
    private struct Opening
    {
        internal Dam_EnemyDamageBase Receiver;
        internal Agents.Agent? Source;
        internal int LimbId;
        internal Vector3 Position;
        internal State Captured;
    }

    /// <summary>The closing half's inputs, passed to the closure-free guard by reference.</summary>
    private struct Closing
    {
        internal Dam_EnemyDamageBase Receiver;
        internal ES_HitreactType Hitreact;
        internal int LimbId;
        internal bool Applied;
        internal State Window;
    }
}

// The behaviour facts (awakened, target acquired / lost, scout scream phase, zone changed) are sampled when the game
// reports the change instead of on every awake enemy's every frame: the behaviour machine's and the AI's own entries
// (`StateEntries`: `EnemyBehaviour.ChangeState`, `EnemyAI.SetDetectedAgent`), the three entries below, the scout scream
// state's own update (`EnemyScreamUpdateHook`) and, for the two falling edges the game raises no call for (a lock whose
// target stopped being valid, a tag whose native timer ran out), a once-per-tick look at the lives that currently hold
// one (`EnemyModule.ObserveHoldersInFrame`). The hooks only read native state and publish facts; they never write the
// world. A build with no plan for any of the families pays a bool read per call.
internal static class EnemyBehaviorSampling
{
    /// <summary>Samples the AI's behaviour state, target lock and scout scream phase after one of its entries ran.</summary>
    internal static void Sample(EnemyAI? ai)
    {
        if (ai == null || EnemyHost.Offer(EnemyFeature.Behavior) is not { } session || !session.Module.BehaviorObserved) return;
        using var measured = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyBehaviorTargets);
        var sampled = ai;
        session.Guard(ref sampled, static (EnemyModule module, ref EnemyAI value) => module.ObserveEnemyBehaviorEvent(value.m_enemyAgent));
    }

    /// <summary>Samples the scream phase of one scout after its scream state started (<paramref name="began"/>, which
    /// starts the phase watermark over) or advanced.</summary>
    internal static void SampleScream(EnemyAgent? enemy, bool began)
    {
        if (enemy == null || EnemyHost.Offer(EnemyFeature.Behavior) is not { } session || !session.Module.ScoutScreamObserved) return;
        using var measured = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyBehaviorTargets);
        var sampled = enemy;
        if (began) session.Guard(ref sampled, static (EnemyModule module, ref EnemyAgent value) => module.ScoutScreamBegan(value));
        else session.Guard(ref sampled, static (EnemyModule module, ref EnemyAgent value) => module.ObserveEnemyBehaviorEvent(value));
    }
}
// The AI took (`SetTarget`) or was handed (`InjectPropagatedTarget`) a target. `SetDetectedAgent` is sampled by
// `EnemyDetectedAgentEntry`. ForgeCore patches `InjectPropagatedTarget` once (`GameHooks.EnemyTargetInjected`); this samples after its body.
[HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.SetTarget))]
internal static class EnemyTargetSet
{
    [HarmonyPostfix]
    private static void Postfix(EnemyAI __instance) => EnemyBehaviorSampling.Sample(__instance);
}

internal static class EnemyTargetInjected
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemyTargetInjected.After(static (ai, _) => EnemyBehaviorSampling.Sample(ai));
}

// The enemy's course node changed, which is the only thing `forge.trigger.enemy.zone_changed` compares. The first zone
// a life is seen in is its baseline and publishes nothing; a node that did not change its zone publishes nothing.
[HarmonyPatch(typeof(EnemyAgent), "set_CourseNode")]
internal static class EnemyCourseNodeSet
{
    [HarmonyPostfix]
    private static void Postfix(EnemyAgent __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Behavior) is not { } session || !session.Module.ZoneChangeObserved) return;
        var sampled = __instance;
        session.Guard(ref sampled, static (EnemyModule module, ref EnemyAgent value) => module.ObserveEnemyZoneEvent(value));
    }
}

// Native detection registration for a scout tendril: the game has already accepted the target, so the hook
// reports it with the position the native target carries instead of scanning for one.
[HarmonyPatch(typeof(ES_ScoutDetection), nameof(ES_ScoutDetection.OnTargetRegistered))]
internal static class EnemyScoutDetected
{
    [HarmonyPostfix]
    private static void Postfix(ES_ScoutDetection __instance, AgentTarget target)
    {
        if (EnemyHost.Offer(EnemyFeature.ScoutDetection) is not { } session || !session.Module.ScoutDetectionObserved) return;
        var call = new Call { Detection = __instance, Target = target };
        session.Guard(ref call, static (EnemyModule module, ref Call scout) => module.ObserveScoutDetection(scout.Detection.m_owner, scout.Target));
    }

    private struct Call
    {
        internal ES_ScoutDetection Detection;
        internal AgentTarget Target;
    }
}

internal sealed partial class EnemyModule
{
    internal bool ScoutDetectionObserved => _scoutDetectionGate.HasSubscribers;
}
