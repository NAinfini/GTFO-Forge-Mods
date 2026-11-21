using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ForgeDevelopment.Native;

// An installation attempt is owned before invoking the backend: a backend may throw after changing a target.
// Failed removals remain owned, and a later Stop retries them. This state is independent of callback gating.
internal sealed class ExperimentHookLifetime
{
    private readonly List<MethodInfo> _owned = new();
    private readonly Action<MethodInfo> _install, _remove;
    internal ExperimentHookLifetime(Action<MethodInfo> install, Action<MethodInfo> remove)
    { _install = install; _remove = remove; }
    internal bool Enabled { get; private set; }
    internal int OwnedCount => _owned.Count;
    internal void Start(IReadOnlyList<MethodInfo> targets)
    {
        if (Enabled) return;
        if (_owned.Count != 0) throw new InvalidOperationException("Experiment hook cleanup is incomplete; stop it before rearming.");
        try
        {
            foreach (var target in targets)
            {
                if (_owned.Contains(target)) continue;
                _owned.Add(target);
                _install(target);
            }
            Enabled = true;
        }
        catch (Exception original)
        {
            try { Stop(); }
            catch (Exception cleanup) { throw new AggregateException("Experiment installation failed; cleanup remains incomplete.", original, cleanup); }
            throw;
        }
    }
    internal void Stop()
    {
        Enabled = false;
        var failures = new List<Exception>();
        for (var index = _owned.Count - 1; index >= 0; index--)
        {
            try { _remove(_owned[index]); _owned.RemoveAt(index); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0)
            throw new AggregateException("Experiment cleanup incomplete; " + _owned.Count + " hook target(s) remain owned.", failures);
    }
}

// Explicit command-scoped damage experiment. A void prefix changes the local player's damage argument,
// never suppresses the native call, and has no finalizer that could consume a native exception.
internal static class ExperimentDamage
{
    private static readonly string[] EntryPoints =
    { "BulletDamage", "MeleeDamage", "ExplosionDamage", "FireDamage", "PushDamage", "FallDamage" };
    private const string Consumer = "experiment.damage";
    private static readonly Dictionary<MethodInfo, IDisposable> Leases = new();
    private static readonly DevelopmentNativeHooks.DamageWriter Writer = Apply;
    private static readonly ExperimentHookLifetime Hooks = new(Install, Remove);
    private static float _factor = 1f;
    internal static bool Armed => Hooks.OwnedCount != 0;
    internal static float Factor => _factor;

    internal static string Arm(float factor)
    {
        if (!float.IsFinite(factor) || factor < 0f)
            throw new ArgumentOutOfRangeException(nameof(factor), "The damage factor must be finite and zero or positive.");
        if (!ExperimentRunner.AllowsDamage)
            throw new InvalidOperationException("Damage hooks require an active explicitly started experiment command.");
        DevelopmentDamageHooks.Register(DevelopmentNativeHooks.Shared);
        var targets = EntryPoints.Select(name => typeof(Dam_SyncedDamageBase).GetMethod(name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(typeof(Dam_SyncedDamageBase).FullName, name)).ToArray();
        // Do not publish the multiplier until the complete group is installed.
        Hooks.Start(targets);
        _factor = factor;
        var report = "damage factor " + factor + "; subscribed " + Hooks.OwnedCount + " entry points";
        ExperimentRunner.Log(report, "experiment damage");
        return report;
    }
    private static void Install(MethodInfo target)
    {
        // The shared target's reviewed typed prefix owns the sole ref-float write channel.
        // Ordinary recorder callbacks remain read-only; trace and damage share one native patch set.
        var lease = DevelopmentNativeHooks.Shared.SubscribeDamageWriter(target, Consumer, Writer);
        Leases.Add(target, lease);
    }
    private static void Remove(MethodInfo target)
    {
        if (Leases.TryGetValue(target, out var lease))
        {
            // Retain a failed lease for Stop's explicit retry. The shared backend disables
            // its writer immediately even if the physical target cannot yet be removed.
            lease.Dispose();
            Leases.Remove(target);
        }
        else
        {
            // Admission can refuse before a writer lease exists while a recorder still
            // owns this target. Never retry/remove another consumer's active installation.
            var shared = DevelopmentNativeHooks.Shared;
            if (shared.Inspect().Any(status => status.Target.Equals(target) && status.Consumers != 0)) return;
            if (!shared.RetryCleanup(target))
                throw new InvalidOperationException("Damage hook cleanup remains pending on " + target.Name);
        }
    }
    private static void Apply(object instance, ref float damage) => Prefix((Dam_SyncedDamageBase)instance, ref damage);
    internal static void Stop()
    {
        _factor = 1f;
        Hooks.Stop();
    }
    internal static string Disarm()
    {
        var count = Hooks.OwnedCount;
        Stop();
        var report = "restored " + count + " damage entry points; no damage experiment writers remain";
        ExperimentRunner.Log(report, "experiment damage");
        return report;
    }
    private static void Prefix(Dam_SyncedDamageBase __instance, ref float dam)
    {
        var original = dam;
        try
        {
            if (!Hooks.Enabled || _factor == 1f || dam <= 0f) return;
            var player = ForgeCore.Players.Local;
            if (player?.Damage == null || player.Damage.Pointer != __instance.Pointer) return;
            var scaled = dam * _factor;
            if (!float.IsFinite(scaled)) throw new ArithmeticException("Damage scaling produced a non-finite argument.");
            dam = scaled;
        }
        catch (Exception error)
        {
            dam = original;
            ExperimentRunner.Log("damage argument restored after observer failure: " + error.GetType().Name + ": " + error.Message, "experiment damage");
        }
    }
}
