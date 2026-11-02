using System;
using Player;

namespace ForgeOrdnance;

/// <summary>A summoned turret is a vanilla sentry instance owned by the summoner, and the game's sentry code treats every
/// sentry instance as the one the owner carries: it marks the owner's class slot deployed. The HUD then shows "Deploy"
/// and the carried tool, whatever it is, cannot be used. The turret is meant to be an independent world object, so every
/// step of its own life (spawn, pick-up, despawn, cleanup) runs inside <see cref="Guard"/>, which refuses the class slot's
/// deployed write, and the slot is read before and after to prove it came out unchanged. Runs on every peer.</summary>
internal static class SentrySlotGuard
{
    internal static readonly ToolSlotGuard Guard = new();
    private const InventorySlot Slot = InventorySlot.GearClass;

    internal sealed class Watch
    {
        internal PlayerBackpack? Backpack;
        internal ToolSlotState Before;
        internal string Owner = "";
        internal string Step = "";
    }

    internal static Watch Begin(PlayerBackpack? backpack, string owner, string step)
    {
        Guard.Enter();
        var watch = new Watch { Backpack = backpack, Owner = owner, Step = step };
        if (backpack != null && backpack.Pointer != IntPtr.Zero) watch.Before = Read(backpack);
        else watch.Backpack = null;
        return watch;
    }

    internal static void End(Watch? watch)
    {
        if (watch == null) return;
        Guard.Exit();
        var backpack = watch.Backpack;
        if (backpack == null || backpack.Pointer == IntPtr.Zero) return;
        try
        {
            var after = Read(backpack);
            if (after == watch.Before) return;
            Plugin.Error("A summoned turret changed the tool slot of " + watch.Owner + " during " + watch.Step + " (before " + watch.Before.Describe() + ", after " + after.Describe() + "); restoring it.");
            backpack.SetDeployed(Slot, watch.Before.Deployed);
            backpack.UpdateItemStatus(Slot, (eInventoryItemStatus)watch.Before.Status);
        }
        catch (Exception error) { Plugin.Error("Could not verify the tool slot after a summoned turret " + watch.Step + ": " + error); }
    }

    internal static ToolSlotState Read(PlayerBackpack backpack)
    {
        var deployed = backpack.IsDeployed(Slot);
        if (!backpack.TryGetBackpackItem(Slot, out var item) || item == null) return new ToolSlotState("(empty)", deployed, 0);
        return new ToolSlotState(item.Name ?? "(unnamed)", deployed, (int)item.Status);
    }

    internal static PlayerBackpack? BackpackOf(SNetwork.SNet_Player? player)
        => player != null && PlayerBackpackManager.TryGetBackpack(player, out var backpack) ? backpack : null;
}

internal static class SummonedTurretDeployWrite
{
    internal static void Subscribe() => ForgeCore.GameHooks.BackpackDeployed.Before(call =>
    {
        if (!SentrySlotGuard.Guard.AllowsDeployChange(call.Slot == InventorySlot.GearClass)) call.Skip();
    });
}

/// <summary>The spawn: the owner comes from the replicated spawn data, the sentry is not bound to a backpack yet.</summary>
internal static class SummonedTurretSpawnGuard
{
    internal static void Subscribe() => ForgeCore.GameHooks.SentrySpawned.Around(
        call => Begin(call.Data),
        (_, watch, _) => SentrySlotGuard.End(watch),
        (_, watch, _) => SentrySlotGuard.End(watch));

    private static SentrySlotGuard.Watch? Begin(pGearSpawnData spawnData)
    {
        try
        {
            if (!SentryIdentity.TryMarker(spawnData.gearIDRange.publicName.data, out _, out _)) return null;
            spawnData.owner.TryGetPlayer(out var player);
            return SentrySlotGuard.Begin(SentrySlotGuard.BackpackOf(player), player?.NickName ?? "(unknown)", "spawn");
        }
        catch (Exception error) { Plugin.Error("Could not guard the tool slot for a summoned turret spawn: " + error); return null; }
    }
}

/// <summary>Everything else a sentry does to its owner's backpack: spawn completion, pick-up, despawn, checkpoint cleanup
/// (ForgeCore's <see cref="ForgeCore.GameHooks.SentryLifecycle"/>).</summary>
internal static class SummonedTurretLifecycleGuard
{
    internal static void Subscribe() => ForgeCore.GameHooks.SentryLifecycle.Around(
        call => Begin(call.Sentry, call.Step),
        (_, watch, _) => SentrySlotGuard.End(watch),
        (_, watch, _) => SentrySlotGuard.End(watch));

    private static SentrySlotGuard.Watch? Begin(SentryGunInstance sentry, ForgeCore.SentryStep step)
    {
        try
        {
            if (!GearPayload.TryIdentity(sentry, out _)) return null;
            var backpack = sentry.m_belongsToBackpack;
            return SentrySlotGuard.Begin(backpack, backpack?.Owner?.NickName ?? "(unknown)", step.ToString());
        }
        catch (Exception error) { Plugin.Error("Could not guard the tool slot for a summoned turret: " + error); return null; }
    }
}
/// <summary>Host: a summoned sentry takes its requested strength once its components exist.</summary>
internal static class SummonedTurretStrength
{
    internal static void Subscribe() => ForgeCore.GameHooks.SentryGearSpawned.After((sentry, _) => Spawned(sentry));

    private static void Spawned(SentryGunInstance sentry)
    {
        try { GearPayload.ApplyStrength(sentry); }
        catch (Exception error) { Plugin.Error("Could not apply the requested strength to a summoned sentry: " + error); }
    }
}
