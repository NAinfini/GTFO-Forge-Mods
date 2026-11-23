using System;
using System.Collections.Generic;
using AssetShards;
using HarmonyLib;

namespace ForgeWeapon.Native;

internal static partial class WeaponNativeHooks
{
    static partial void NativeResourceLifetimeHookTypes(List<Type> types)
    {
        types.Add(typeof(ShardDependencyResetObserved));
    }

    static partial void NativeResourceLifetimeSubscriptions() => SentryResourceLifetime.Subscribe();
}

// Build 20403457: the unregistered branch of AssetShardManager.LoadShardAsync synchronously calls this exact
// method once, and ResetShardDependencyCount returns only after setting the named shard's count to one. The
// postfix observes every normally returned reset, including another caller's reset between our Load and its
// callback. The void prefix makes Harmony 2.10.1 provide __runOriginal and cannot suppress, repeat or replace the
// native body. A different prefix suppressing the original leaves __runOriginal false and creates no receipt.
[HarmonyPatch(typeof(AssetShardManager), nameof(AssetShardManager.ResetShardDependencyCount))]
internal static class ShardDependencyResetObserved
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(string shardName) { }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(string shardName, bool __runOriginal)
    {
        if (!__runOriginal) return;
        WeaponNativeSession.Offer(WeaponFeature.Lifetime)?.Guard(
            static (_, shard) => ShardDependencyResetReceipts.Observe(shard), shardName);
    }
}

/// <summary>Process-wide observation of the game's own reset calls. The tokens make a receipt fail closed: a
/// caller must see exactly one completed reset in its synchronous Load call, and that exact token must still be
/// current when the callback and later release run. Only the named shard is compared; resets of other shards do
/// not invalidate it.</summary>
internal static class ShardDependencyResetReceipts
{
    private sealed class State
    {
        internal object Current = new();
        internal object? Previous;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, State> States = new(StringComparer.Ordinal);

    internal static object Snapshot(string shard)
    {
        lock (Sync) return Get(shard).Current;
    }

    internal static bool ExactNext(string shard, object before, out object receipt)
    {
        lock (Sync)
        {
            var state = Get(shard);
            receipt = state.Current;
            return ReferenceEquals(state.Previous, before);
        }
    }

    internal static bool IsCurrent(string shard, object receipt)
    {
        lock (Sync) return ReferenceEquals(Get(shard).Current, receipt);
    }

    internal static void Observe(string shard)
    {
        lock (Sync)
        {
            var state = Get(shard);
            state.Previous = state.Current;
            state.Current = new object();
        }
    }

    private static State Get(string shard)
    {
        if (!States.TryGetValue(shard, out var state)) States.Add(shard, state = new());
        return state;
    }
}

// Build 20403457: SentryGunInstance.OnDestroy (0x1417a80) runs native cleanup but does not
// release Forge's extra-part receipts. Capture the exact holder before that body runs; ForgeCore's
// one sentry-lifecycle patch brackets the call, so neither the capture nor either completion
// suppresses or repeats the native body, and the game still throws its own exception.
internal static class SentryResourceLifetime
{
    internal static void Subscribe() => ForgeCore.GameHooks.SentryLifecycle.Around(Capture, Retire, RetireAfterFault);

    private static Action? Capture(ForgeCore.SentryCall call)
        => call.Step == ForgeCore.SentryStep.Destroy ? CaptureHolder(call.Sentry) : null;

    private static Action? CaptureHolder(SentryGunInstance sentry)
    {
        Action? retirement = null;
        GearPartExtraAssets? owner = null;
        try
        {
            WeaponNativeSession.Offer(WeaponFeature.Sentry)?.Guard(session =>
            {
                if (session.GearParts?.ExtraSpawner is { } extras)
                {
                    owner = extras;
                    try { retirement = extras.CaptureHolderRetirement(sentry.GearPartHolder); }
                    catch (Exception error) { extras.ReportHolderRetirementFailure(error); }
                }
            });
        }
        catch (Exception error) { owner?.ReportHolderRetirementFailure(error); }
        return retirement;
    }

    // The continuation is one-shot, contains its own cleanup errors, and runs whether the native body
    // returned, was skipped by another prefix or threw, so stale callbacks are cancelled in every case.
    private static void Retire(ForgeCore.SentryCall call, Action? retirement, bool ran) => retirement?.Invoke();

    private static void RetireAfterFault(ForgeCore.SentryCall call, Action? retirement, Exception error) => retirement?.Invoke();
}
