using System;
using System.Collections.Generic;
using System.Reflection;
using FX_EffectSystem;
using Gear;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

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
        => mode is EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc or EnergyMode.Disc or EnergyMode.BlackHole;

    internal static Frame Begin(BulletWeapon weapon, bool synced = false)
    {
        var gear = EnergyGearRegistry.DefinitionOf(weapon);
        var frame = Frames.Count == 0 ? new Frame() : Frames.Pop();
        frame.Weapon = weapon; frame.Gear = gear; frame.Previous = _current;
        frame.Hit = null; frame.CastSeen = frame.Resolved = frame.ApplyingHit = false; frame.Synced = synced;
        frame.Power = gear != null && weapon.Owner?.Owner?.IsBot == true
            ? gear.Numbers.BotChargePower : PrototypeController.ReleasedPower(weapon);
        _current = frame;
        return frame;
    }

    internal static void End(Frame? frame, Exception? error)
    {
        if (frame == null) return;
        try
        {
            if (frame.Gear != null && error != null) Plugin.Error("Native energy fire failed; committed hits will not be retried: " + error);
            else if (frame.Gear != null && !frame.CastSeen)
                Plugin.Error("Native energy fire did not call CastWeaponRay: " +
                    new InvalidOperationException("The fire implementation did not provide a shared weapon ray."));
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
        try { PrototypeController.NativeDischarge(frame.Weapon, frame.Gear!, origin, hit.fireDir); }
        catch (Exception error)
        {
            Plugin.Error("Energy discharge stopped; committed hits will not be retried: " + error);
            PrototypeController.StopNativeDischarge(frame.Weapon);
        }
    }
}

// Gate a bot's native shot request before it sets Shoot or spends its native burst count.
// Its synced Fire and PlayerSync still own the actual round, shot count, recoil and animation.
[HarmonyPatch]
internal static class NativeEnergyBotShotReady
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(PlayerBotActionUseFirearm), "StartShooting");
        yield return AccessTools.DeclaredMethod(typeof(PlayerBotActionUseFirearm), "UpdateAutoFire");
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(PlayerBotActionUseFirearm __instance)
    {
        var weapon = __instance.m_bulletWeapon;
        if (!SNet.IsMaster || weapon?.Owner?.Owner?.IsBot != true ||
            EnergyGearRegistry.DefinitionOf(weapon) is not { } gear || !EnergyNativeFire.Delayed(gear.Mode)) return true;
        return PrototypeController.CanLaunch(weapon, gear);
    }
}

[HarmonyPatch]
internal static class NativeEnergyFireEntry
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in new[] { typeof(BulletWeapon), typeof(Shotgun), typeof(BulletWeaponSynced), typeof(ShotgunSynced) })
            yield return AccessTools.DeclaredMethod(type, nameof(BulletWeapon.Fire));
    }

    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static void Prefix(BulletWeapon __instance, MethodBase __originalMethod, out EnergyNativeFire.Frame? __state)
        => __state = EnergyNativeFire.Begin(__instance, __originalMethod.DeclaringType == typeof(BulletWeaponSynced) ||
            __originalMethod.DeclaringType == typeof(ShotgunSynced));

    [HarmonyFinalizer, HarmonyPriority(int.MinValue)]
    private static Exception? Finalizer(EnergyNativeFire.Frame? __state, Exception? __exception)
    {
        EnergyNativeFire.End(__state, __exception);
        return __exception;
    }
}

// Synced Fire casts before preparing its owner/damage; this runs before any shared-ray modifiers.
[HarmonyPatch]
internal static class NativeEnergySyncedRayPreparation
{
    private static MethodBase TargetMethod() => NativeEnergyRay.TargetMethod();

    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static void Prefix(ref Weapon.WeaponHitData __1) => EnergyNativeFire.PrepareSyncedRay(__1);
}

// The three-argument overload delegates to this overload in GameAssembly, so one resolution hook covers both.
[HarmonyPatch]
internal static class NativeEnergyRay
{
    internal static MethodBase TargetMethod() => AccessTools.Method(typeof(Weapon), nameof(Weapon.CastWeaponRay),
        new[] { typeof(Transform), typeof(Weapon.WeaponHitData).MakeByRefType(), typeof(Vector3), typeof(int) });

    [HarmonyPrefix, HarmonyPriority(int.MinValue)]
    private static void Prefix(ref Weapon.WeaponHitData __1, out bool __state)
    {
        __state = false;
        if (!EnergyNativeFire.Matches(__1)) return;
        var frame = EnergyNativeFire.Current!;
        __state = !frame.CastSeen;
        frame.CastSeen = true;
        // The native primary ray is counted once. Recasts cannot add another counted shot or energy effect.
        __1.maxRayDist = __state ? MathF.Max(100f, __1.maxRayDist) : 0f;
    }

    // Shot counters observe the primary cast before its real limb hits are submitted.
    [HarmonyPostfix, HarmonyPriority(int.MinValue)]
    private static void Postfix(ref Weapon.WeaponHitData __1, Vector3 __2, bool __state, ref bool __result)
    {
        if (!EnergyNativeFire.Matches(__1)) return;
        __result = false;
        if (__state) EnergyNativeFire.Resolve(__2, __1);
    }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.BulletHit))]
internal static class NativeEnergyHit
{
    [HarmonyPrefix, HarmonyPriority(int.MinValue)]
    private static bool Prefix(Weapon.WeaponHitData __0)
        => !EnergyNativeFire.Matches(__0) || EnergyNativeFire.Current!.ApplyingHit;
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

[HarmonyPatch(typeof(EX_SpriteMuzzleFlash), nameof(EX_SpriteMuzzleFlash.Play))]
internal static class NativeEnergyMuzzleFlash
{
    [HarmonyPrefix]
    private static bool Prefix() => !EnergyNativeFire.InFireEntry;
}
