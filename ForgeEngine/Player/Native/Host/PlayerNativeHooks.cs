using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.GameBindings;
using Player;

namespace ForgePlayer.Native;

/// <summary>One Player feature and the native targets it stands on. A refused target withdraws exactly the bindings
/// and services of the features it supports; every other feature keeps working.</summary>
internal sealed record PlayerHookFeature(string Name, Type[] Hooks, string[] Bindings)
{
    /// <summary>The services and device writers this feature publishes besides its bindings.</summary>
    internal PlayerHookService Services { get; init; }
}

[Flags]
internal enum PlayerHookService
{
    None = 0,
    IncomingDamage = 1,
    FlashlightWriter = 4,
    MapWriter = 8
}

/// <summary>The outcome of one declaration pass: the refused targets with Runtime's reasons, and the features
/// they take down.</summary>
internal sealed class PlayerHookAvailability
{
    internal static readonly PlayerHookAvailability Complete = new(new Dictionary<Type, string>());
    internal IReadOnlyDictionary<Type, string> Refused { get; }
    internal IReadOnlyList<PlayerHookFeature> Unavailable { get; }
    internal PlayerHookAvailability(IReadOnlyDictionary<Type, string> refused)
    {
        Refused = refused;
        Unavailable = PlayerNativeHooks.Features.Where(feature => feature.Hooks.Any(refused.ContainsKey)).ToArray();
    }
    internal bool Offers(PlayerHookService service) => !Unavailable.Any(feature => (feature.Services & service) != 0);
    internal IReadOnlyList<string> WithdrawnBindings => Unavailable.SelectMany(feature => feature.Bindings)
        .Distinct(StringComparer.Ordinal).ToArray();
    internal string Describe(PlayerHookFeature feature) => "feature=" + feature.Name
        + " bindings=" + (feature.Bindings.Length == 0 ? "none" : string.Join(",", feature.Bindings))
        + " services=" + feature.Services
        + " refused=" + string.Join(",", feature.Hooks.Where(Refused.ContainsKey).Select(hook => hook.Name + "=" + Refused[hook]));
}

/// <summary>Player callback declarations. Runtime resolves and admits every target and is the only physical installer.
/// Installations last for the process; disposing a session withdraws only the Player state they access.</summary>
internal static class PlayerNativeHooks
{
    private const string Binding = ModuleDefinition.ProviderId + ".binding.";

    private static readonly Type[] DamageKinds =
    {
        typeof(PlayerDamageKindReceive), typeof(PlayerFireDamageKind),
        typeof(PlayerStickyDamageKind), typeof(PlayerParasiteDamageKind),
        typeof(PlayerLocalParasiteDamageKind), typeof(PlayerLocalNoAirDamageKind), typeof(PlayerPushDamageKind),
        typeof(PlayerTankGrabAttackKind), typeof(PlayerTrapGrabAttackKind)
    };

    internal static readonly IReadOnlyList<PlayerHookFeature> Features = Array.AsReadOnly(new[]
    {
        new PlayerHookFeature("downed", new[] { typeof(DownedStateReadback), typeof(SyncedDownedStateReadback) },
            new[] { Binding + "player.downed" }),
        new PlayerHookFeature("revived", new[] { typeof(RevivedStateReadback) }, new[] { Binding + "player.revived" }),
        new PlayerHookFeature("revive-interaction", new[] { typeof(ReviveInteractionReadback) },
            new[] { Binding + "player.revive_started", Binding + "player.revive_cancelled" }),
        new PlayerHookFeature("died", new[] { typeof(PlayerDiedReadback), typeof(LocalPlayerDiedReadback) },
            new[] { Binding + "player.died" }),
        new PlayerHookFeature("teleported", new[] { typeof(PlayerWarpedReadback) }, new[] { Binding + "player.teleported" }),
        // Damage facts and incoming-damage rules read the kind every kind entry names; one missing kind would
        // mislabel that damage, so the whole damage feature is withdrawn instead.
        new PlayerHookFeature("damage", new[] { typeof(PlayerDamageAccepted) }.Concat(DamageKinds).ToArray(),
            new[] { Binding + "trigger.combat.damage_applied" }) { Services = PlayerHookService.IncomingDamage },
        // A hit, a heal and a set-health are the three health writes; health facts need all three.
        new PlayerHookFeature("health", new[] { typeof(PlayerDamageAccepted), typeof(PlayerHealthAdded), typeof(PlayerHealthSet) },
            new[] { Binding + "trigger.combat.health_changed", Binding + "trigger.player.low_health" }),
        new PlayerHookFeature("infection", new[] { typeof(PlayerInfectionWritten) },
            new[] { Binding + "trigger.player.infection_changed" }),
        new PlayerHookFeature("backpack", new[] { typeof(PlayerBackpackAdded), typeof(PlayerBackpackRemoved), typeof(PlayerBackpackStatusChanged) },
            new[] { PlayerBackpackContract.ChangedBinding }),
        new PlayerHookFeature("ping", new[] { typeof(PlayerPingMarkerSet) }, new[] { Binding + "trigger.input.ping" }),
        new PlayerHookFeature("movement-state", new[] { typeof(PlayerLocomotionStateChanged) },
            new[] { Binding + "trigger.player.movement_state_changed" }),
        new PlayerHookFeature("input-held", new[] { typeof(TimedInteractionCompleted) }, new[] { Binding + "trigger.input.held" }),
        // A held flashlight constrains only its call argument; the native synchronization body still runs.
        new PlayerHookFeature("flashlight-hold", new[] { typeof(FlashlightHoldPrefix) }, Array.Empty<string>())
            { Services = PlayerHookService.FlashlightWriter },
        new PlayerHookFeature("map-hold", new[] { typeof(MapVisualHoldPrefix), typeof(MapNoticeHoldPrefix), typeof(MapRefreshHoldPostfix) }, Array.Empty<string>())
            { Services = PlayerHookService.MapWriter }
    });

    /// <summary>The observers and subscriptions on the game methods ForgeCore patches for everyone (<see cref="ForgeCore.PlayerHits"/>,
    /// <see cref="ForgeCore.GameHooks"/>): features stand on them like on a declared target, but Runtime installs nothing for them.</summary>
    private static readonly (Type Hook, string Source, Action Subscribe)[] CoreOwned =
    {
        (typeof(PlayerDamageAccepted), "ForgeCore.PlayerHits", () => PlayerDamageAccepted.Observe(ModuleDefinition.ProviderId)),
        (typeof(PlayerInfectionWritten), "ForgeCore.PlayerHits", () => PlayerInfectionWritten.Observe(ModuleDefinition.ProviderId)),
        (typeof(PlayerDamageKindReceive), "ForgeCore.GameHooks.PlayerReceived", PlayerDamageKindReceive.Subscribe),
        (typeof(PlayerHealthSet), "ForgeCore.GameHooks.HealthSetReceived", PlayerHealthSet.Subscribe),
        (typeof(RevivedStateReadback), "ForgeCore.GameHooks.PlayerRevived", RevivedStateReadback.Subscribe),
        (typeof(LocalPlayerDiedReadback), "ForgeCore.GameHooks.LocalPlayerDied", LocalPlayerDiedReadback.Subscribe)
    };

    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(Features.SelectMany(feature => feature.Hooks).Distinct()
        .Where(hook => CoreOwned.All(owned => owned.Hook != hook)).ToArray());

    /// <summary>Every target is declared, so one run names every target that could not be proven; each refusal is
    /// returned with the features it takes down. Player identity stands on Runtime's membership entries instead
    /// (<see cref="PlayerMembershipReadback"/>).</summary>
    internal static PlayerHookAvailability Declare()
    {
        var refused = ForgeEngine.GameBindings.NativeHookRegistration.Declare(ModuleDefinition.ProviderId, Types)
            .ToDictionary(refusal => refusal.Callbacks, refusal => refusal.Reason);
        foreach (var (hook, source, subscribe) in CoreOwned)
        {
            try { subscribe(); }
            catch (Exception error) { refused[hook] = source + ": " + error.Message; }
        }
        return refused.Count == 0 ? PlayerHookAvailability.Complete : new PlayerHookAvailability(refused);
    }
}

/// <summary>Player identity: every surface resolves a player through these two readbacks, so a refused membership
/// lease is the one refusal that stops the whole package. Both read after the native body returned: identity comes
/// from PlayerManager.PlayerAgentsInLevel and each agent's linked SNet_Player. Runtime owns the replication entries
/// (PlayerReplicationManager.OnSpawn / OnDeSpawn) as shared installs, so Player is a read-only subscriber and no
/// package can occupy them first. In the reviewed GameAssembly PlayerManager.OnPlayerSpawned / OnPlayerDespawned
/// each have exactly one direct caller, the last call before OnSpawn / OnDeSpawn return, so these readbacks read the
/// same list at the same point for local, remote and bot players; targets, RVAs and call edges are frozen in
/// evidence/player-hooks.json.</summary>
internal static class PlayerMembershipReadback
{
    internal const string Consumer = "forge.player.identity";
    internal static DomainWorldObservation Subscribe(ForgeEngine.Framework.RuntimeModuleHandle owner, ForgeEngine.Framework.IForgeEngine kernel)
        => new("Player", owner, kernel, null,
            static epoch => GamePlayerMembership.Subscribe(Consumer, epoch, GamePlayerMembershipEntry.ReplicationSpawn,
                GameEntryPhase.Returned, static call => Spawned(call.SpawnData)),
            static epoch => GamePlayerMembership.Subscribe(Consumer, epoch, GamePlayerMembershipEntry.ReplicationDespawn,
                GameEntryPhase.Returned, static _ => Despawned()));

    // The respawn fact asks whether the player already had a life before the identity records this spawn, and it
    // reconciles the identity itself; the plain reconcile after it covers a session with no life half.
    internal static void Spawned(pPlayerSpawnData spawnData)
    {
        PlayerHost.Session?.GuardLifeFacts(facts => facts.Respawned(spawnData));
        PlayerHost.Session?.Guard(static module => { module.Reconcile(); module.NoteLifeChange(); });
    }
    internal static void Despawned() => PlayerHost.Session?.Guard(static module => { module.Reconcile(); module.NoteLifeChange(); });
}
