using System;
using System.Collections.Generic;
using System.Reflection;
using FX_EffectSystem;
using Gear;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

// Fire remains native, including replacement prefixes. Only its shared ray/hit path changes.
internal static class EnergyNativeFire
{
    internal sealed class Frame
    {
        internal BulletWeapon Weapon = null!;
        internal EnergyGear? Gear;
        internal Weapon.WeaponHitData? Hit;
        internal Frame? Previous;
        internal bool CastSeen, Resolved, ApplyingHit, Synced;
        internal float Power;
    }

    private static readonly Stack<Frame> Frames = new(new[] { new Frame(), new Frame(), new Frame(), new Frame() });
    private static Frame? _current;
    internal static Frame? Current => _current?.Gear != null ? _current : null;
    internal static bool InFireEntry => Current != null;
    internal static bool InFireCall => _current != null;
    internal static bool Delayed(EnergyMode mode)
        => mode is EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc or EnergyMode.Disc or EnergyMode.BlackHole or EnergyMode.Blast;

    internal static Frame Begin(BulletWeapon weapon, bool synced = false)
    {
        var gear = EnergyGearRegistry.DefinitionOf(weapon);
        var frame = Frames.Count == 0 ? new Frame() : Frames.Pop();
        frame.Weapon = weapon; frame.Gear = gear; frame.Previous = _current;
        frame.Hit = null; frame.CastSeen = frame.Resolved = frame.ApplyingHit = false; frame.Synced = synced;
        frame.Power = gear != null && weapon.Owner?.Owner?.IsBot == true
            ? gear.Numbers.BotChargePower : EnergyController.ReleasedPower(weapon);
        _current = frame;
        return frame;
    }

    internal static void End(Frame? frame, Exception? error)
    {
        if (frame == null) return;
        try
        {
            if (frame.Gear != null && error != null) Plugin.Error("Native energy fire failed; committed hits will not be retried: " + error);
        }
        finally
        {
            _current = frame.Previous;
            frame.Weapon = null!; frame.Gear = null; frame.Hit = null; frame.Previous = null;
            Frames.Push(frame);
        }
    }

    internal static bool Matches(Weapon.WeaponHitData hit)
        => Current != null && hit.owner != null && Current.Weapon.Owner != null &&
            hit.owner.Pointer == Current.Weapon.Owner.Pointer;

    internal static void PrepareSyncedRay(Weapon.WeaponHitData hit)
    {
        var frame = Current;
        if (frame == null || !frame.Synced || hit.owner != null ||
            Weapon.s_weaponRayData?.Pointer != hit.Pointer) return;
        var weapon = frame.Weapon;
        var owner = weapon.Owner ?? throw new InvalidOperationException("Synced energy fire has no shooter.");
        var archetype = weapon.ArchetypeData ?? throw new InvalidOperationException("Synced energy fire has no archetype.");
        var item = weapon.ItemDataBlock ?? throw new InvalidOperationException("Synced energy fire has no item data.");
        // Both synced Fire bodies fill these only after a successful cast. Prepare the same native values
        // before ray observers run, so misses also resolve and lower-layer modifiers see the actual shooter.
        hit.owner = owner;
        hit.damage = archetype.GetDamageWithBoosterEffect(owner, item.inventorySlot);
        hit.staggerMulti = archetype.StaggerDamageMulti;
        hit.precisionMulti = archetype.PrecisionDamageMulti;
        hit.damageFalloff = archetype.DamageFalloff;
    }

    internal static void Resolve(Vector3 origin, Weapon.WeaponHitData hit)
    {
        var frame = Current!;
        if (frame.Resolved) return;
        frame.Resolved = true;
        frame.Hit = hit;
        var owner = frame.Weapon.Owner?.Owner;
        // Synced human fire is presentation only. Bots deal their native hits on the host.
        if (owner == null || frame.Synced && !owner.IsBot || (owner.IsBot ? !SNet.IsMaster : !owner.IsLocal)) return;
        try { EnergyController.NativeDischarge(frame.Weapon, frame.Gear!, origin, hit.fireDir); }
        catch (BudgetExhausted error)
        {
            // This shot found no room (a busy projectile slot); the gun is fine and only this shot is dropped.
            Plugin.Error("An energy shot was dropped: " + error);
        }
        catch (Exception error)
        {
            Plugin.Error("Energy discharge stopped; committed hits will not be retried: " + error);
            EnergyController.StopNativeDischarge(frame.Weapon);
        }
    }
}

// Gate a bot's native shot request before it sets Shoot or spends its native burst count.
// Its synced Fire and PlayerSync still own the actual round, shot count, recoil and animation.
// Installed by EnergyLazyHooks the first time an energy gun is wielded.
internal static class NativeEnergyBotShotReady
{
    internal static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(NativeEnergyBotShotReady), nameof(Prefix))) { priority = Priority.First };
        harmony.Patch(AccessTools.DeclaredMethod(typeof(PlayerBotActionUseFirearm), "StartShooting"), prefix);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(PlayerBotActionUseFirearm), "UpdateAutoFire"), prefix);
    }

    private static bool Prefix(PlayerBotActionUseFirearm __instance)
    {
        var weapon = __instance.m_bulletWeapon;
        if (!SNet.IsMaster || weapon?.Owner?.Owner?.IsBot != true ||
            EnergyGearRegistry.DefinitionOf(weapon) is not { } gear || !EnergyNativeFire.Delayed(gear.Mode)) return true;
        return EnergyController.CanLaunch(weapon, gear);
    }
}

// The four firing bodies run inside one frame each (ForgeCore's brackets open at the call and close once the game's body and every other
// patch of it are done, or the body threw).
internal static class NativeEnergyFireEntry
{
    internal static void Subscribe()
    {
        Watch(ForgeCore.GameHooks.Fire.BulletWeapon, synced: false);
        Watch(ForgeCore.GameHooks.Fire.Shotgun, synced: false);
        Watch(ForgeCore.GameHooks.Fire.BulletWeaponSynced, synced: true);
        Watch(ForgeCore.GameHooks.Fire.ShotgunSynced, synced: true);
    }

    private static void Watch<TWeapon>(ForgeCore.Bracket<TWeapon> bracket, bool synced) where TWeapon : BulletWeapon
        => bracket.Around(weapon => EnergyNativeFire.Begin(weapon, synced),
            (_, frame, _) => EnergyNativeFire.End(frame, null),
            (_, frame, error) => EnergyNativeFire.End(frame, error));
}

// The ray of the energy shot. The three-argument overload delegates to this overload in GameAssembly, so one hook covers both.
internal static class NativeEnergyRay
{
    internal static void Subscribe() => ForgeCore.GameHooks.AlignedRay.Around<bool>(Enter, Exit);

    private static bool Enter(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin)
    {
        // Synced Fire casts before preparing its owner and damage; fill them before anything reads the ray.
        EnergyNativeFire.PrepareSyncedRay(data);
        if (!EnergyNativeFire.Matches(data)) return false;
        var frame = EnergyNativeFire.Current!;
        var primary = !frame.CastSeen;
        frame.CastSeen = true;
        // The native primary ray is counted once. Recasts cannot add another counted shot or energy effect.
        data.maxRayDist = primary ? MathF.Max(100f, data.maxRayDist) : 0f;
        return primary;
    }

    // Shot counters observe the primary cast before its real limb hits are submitted.
    private static void Exit(bool primary, Transform transform, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result, bool ran)
    {
        if (!EnergyNativeFire.Matches(data)) return;
        result = false;
        if (primary) EnergyNativeFire.Resolve(origin, data);
    }
}
// The energy shot's own primary ray is not a bullet: the hit entry answers a miss for it (ForgeCore's one patch of
// `BulletWeapon.BulletHit`, skipped from its before), unless the shot is applying its real hits.
internal static class NativeEnergyHit
{
    internal static void Subscribe() => ForgeCore.GameHooks.BulletHit.Before(Entered);

    private static void Entered(ForgeCore.BulletHitCall call)
    {
        if (EnergyNativeFire.Matches(call.Data) && !EnergyNativeFire.Current!.ApplyingHit) call.Skip(false);
    }
}

[HarmonyPatch]
internal static class NativeEnergyTracer
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Synced guns acquire the pooled wrapper; its child tracer content can have no pool of its own.
        foreach (var type in new[] { typeof(FX_Effect), typeof(FX_EffectContent) })
            yield return AccessTools.DeclaredMethod(type, nameof(FX_EffectContent.Play),
                new[] { typeof(FX_Trigger), typeof(Vector3), typeof(Quaternion) });
    }

    [HarmonyPrefix]
    private static bool Prefix(FX_EffectBase_Poolable __instance)
    {
        if (!EnergyNativeFire.InFireEntry || __instance.m_pool == null || BulletWeapon.s_tracerPool == null ||
            __instance.m_pool.Pointer != BulletWeapon.s_tracerPool.Pointer) return true;
        __instance.ReturnToPool();
        return false;
    }
}

[HarmonyPatch(typeof(BulletWeaponSynced), "WhizByTest")]
internal static class NativeEnergyBulletWhiz
{
    [HarmonyPrefix]
    private static bool Prefix() => !EnergyNativeFire.InFireEntry;
}
