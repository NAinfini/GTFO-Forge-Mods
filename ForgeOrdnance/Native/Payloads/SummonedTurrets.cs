using System;

namespace ForgeOrdnance;

/// <summary>A summoned turret is a vanilla sentry instance owned by the summoner, and the game's sentry code treats every sentry
/// instance as the one the owner carries: it marks the owner's class slot deployed, so the HUD shows "Deploy" and the carried tool,
/// whatever it is, cannot be used. The turret is meant to be an independent world object, so ForgeCore keeps every step of its life
/// off the owner's tool slot (<see cref="ForgeCore.Gear.KeepToolSlot"/>), recognising it by the marker in its public gear name
/// (<see cref="SentryIdentity.Marker"/>). Runs on every peer.</summary>
internal static class SummonedTurretToolSlot
{
    internal static void Subscribe() => ForgeCore.Gear.KeepToolSlot(name => SentryIdentity.TryMarker(name, out _, out _));
}

/// <summary>Host: a summoned sentry takes its requested strength once its components exist.</summary>
internal static class SummonedTurretStrength
{
    internal static void Subscribe() => ForgeCore.GameHooks.SentryGearSpawned.After((sentry, _) => Spawned(sentry));

    private static void Spawned(SentryGunInstance sentry)
    {
        try { GearPayload.ApplyStrength(sentry); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Could not apply the requested strength to a summoned sentry: " + error); }
    }
}
