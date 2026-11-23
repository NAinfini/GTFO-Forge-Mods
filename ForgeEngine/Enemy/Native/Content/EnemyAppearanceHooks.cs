using System;
using HarmonyLib;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

// Appearance is assembled before native initialization on the live instance; recycle releases only our writes.
// Assemble on the live object before vanilla creates its glow animator from the material groups.
// Register added single-material renderers directly so they participate in the same native glow setup.
[HarmonyPatch(typeof(EnemyAppearance), nameof(EnemyAppearance.Setup))]
internal static class EnemyAppearanceInitializing
{
    [HarmonyPrefix]
    private static void Prefix(EnemyAgent owner, out EnemyAppearanceCall __state)
    {
        __state = EnemyAppearanceCall.Capture(owner);
        if (__state.Owner is not { } module) return;
        EnemyHost.Offer(EnemyFeature.Appearance)?.Guard(ref owner, static (EnemyModule m, ref EnemyAgent enemy) => m.InitializeInstanceAppearance(enemy));
    }

    [HarmonyFinalizer]
    private static void Finalizer(Exception? __exception, EnemyAppearanceCall __state)
    {
        if (__exception != null) __state.Release();
    }
}

internal readonly record struct EnemyAppearanceCall(EnemyModule? Owner, int InstanceId)
{
    internal static EnemyAppearanceCall Capture(EnemyAgent? enemy)
    {
        try
        {
            if (EnemyHost.Offer(EnemyFeature.Appearance) is not { } session || enemy?.gameObject is not { } instance) return default;
            return new(session.Module, instance.GetInstanceID());
        }
        catch (Exception error) { NativeFault.Note("enemy.appearance.capture", error); return default; }
    }

    internal void Release() => Owner?.ReleaseInstanceAppearance(InstanceId);
}

internal static class EnemyAppearanceRecycled
{
    // ForgeCore patches `EnemySync.OnDespawn` once for every package. The after and the faulted end both run after the original
    // and every other postfix, on the normal and the exceptional path, so together they are the one release of the life's appearance.
    internal static void Subscribe() => ForgeCore.GameHooks.EnemySyncDespawned.Around(Capture, Release, Release);

    internal static EnemyAppearanceCall Capture(EnemySync sync) => EnemyAppearanceCall.Capture(sync.m_agent);
    private static void Release(EnemySync sync, EnemyAppearanceCall state, bool ran) => state.Release();
    private static void Release(EnemySync sync, EnemyAppearanceCall state, Exception error) => state.Release();
}
