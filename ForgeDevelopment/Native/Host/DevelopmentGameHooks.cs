using System;
using ForgeCore;

namespace ForgeDevelopment.Native;

/// <summary>The game methods Development observes that ForgeCore patches once for every package (<see cref="GameHooks"/>). Development
/// declares no Harmony class for them: it subscribes here and each observation keeps its own rules. A ForgeCore subscription cannot be
/// withdrawn, so the fixed hooks of a collection session call back only while <see cref="Enter"/>'s scope is open (from the moment the
/// session installs its fixed hooks until it removes them), exactly as the patch classes they replace were installed and removed.
/// The opt-in combat sampling is subscribed by <see cref="SubscribeCombat"/>. A subscriber that throws is one ForgeCore error line and the
/// other subscribers still run.</summary>
internal static class DevelopmentGameHooks
{
    private static bool _subscribed, _combatSubscribed, _live;

    /// <summary>Subscribes the fixed hooks (the first time only) and opens the scope in which they call back.</summary>
    internal static IDisposable Enter()
    {
        Subscribe();
        _live = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _live = false;
    }

    /// <summary>The first time the opt-in combat sampling starts; the callbacks stay silent while it is off.</summary>
    internal static void SubscribeCombat()
    {
        if (_combatSubscribed) return;
        _combatSubscribed = true;
        GameHooks.EnemyDamageReceived.Around<float?>(DamageSample.Before, DamageSample.After);
        GameHooks.BulletWeaponWielded.After((weapon, _) => WeaponSample.Wielded(weapon));
    }

    private static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;

        // Level generation diagnostics: the report sees the cleanup before every other patch and, whether the body returned or threw,
        // after them; the per-level door and acoustic observations are cleared with it.
        GameHooks.LevelCleanup.Around<bool>(
            _ =>
            {
                if (!_live) return false;
                RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.Cleanup("before"));
                return true;
            },
            (_, entered, _) => { if (entered) CleanedUp(null); },
            (_, entered, error) => { if (entered) CleanedUp(error); });
        GameHooks.MarkerSpawned.After((call, _) =>
        {
            if (_live) RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.Marker(call.Marker, call.Result));
        });
        GameHooks.PrefabBuilt.After((call, _) =>
        {
            if (_live) RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.Spawner(call.Spawner, call.Built));
        });

        // The capture's snapshot boundaries and the player-list membership records.
        GameHooks.ExpeditionEnded.After((_, _) => { if (_live) CaptureTriggers.LevelEnded.Ended(); });
        GameHooks.PlayerDespawned.Before(player => { if (_live) MembershipPlayerDespawned.Before(player); });
        GameHooks.PlayerDespawned.After((player, _) => { if (_live) MembershipPlayerDespawned.After(player); });
        GameHooks.EnemySyncSpawned.After((call, _) => { if (_live) CaptureEnemySpawned.Spawned(call.Sync); });
        GameHooks.EnemySyncDespawned.Before(sync => { if (_live) CaptureEnemyDespawned.Despawned(sync); });

    }

    private static void CleanedUp(Exception? error)
    {
        RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.Cleanup("after", error));
        RuntimeDiagnostics.Safe(CaptureDoorTopologyNative.Clear);
        RuntimeDiagnostics.Safe(RecAcousticObservation.Reset);
    }
}
