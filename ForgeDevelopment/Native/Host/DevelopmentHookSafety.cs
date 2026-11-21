using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ForgeDevelopment.Native;

internal static class DevelopmentHookSafety
{
    internal sealed record Hook(MethodBase Target, Type[] PatchTypes, string? Refusal);

    // Resolve and inspect all targets before the recorder starts. A refused
    // observation is a coverage gap, not a failure of the diagnostic service.
    internal static Hook[] Validate(Assembly assembly)
        => Targets(assembly).Select(h => h with { Refusal = RecNativePatchSafety.RejectionReason(h.Target) }).ToArray();

    internal static Hook[] Targets(Assembly assembly)
        => Resolve(assembly.GetTypes().Where(t => t.IsDefined(typeof(HarmonyPatch), false)));

    private static Hook[] Resolve(IEnumerable<Type> patchTypes)
    {
        var hooks = new Dictionary<MethodBase, List<Type>>();
        foreach (var type in patchTypes)
        {
            // TargetMethods is optional. AccessTools emits a warning for a
            // missing optional member, so use ordinary metadata lookup here.
            var custom = type.GetMethod("TargetMethods", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            IEnumerable<MethodBase> targets;
            if (custom != null)
                targets = custom.Invoke(null, null) as IEnumerable<MethodBase>
                    ?? throw new InvalidOperationException("Development hook targets unavailable: " + type.FullName);
            else
            {
                var info = HarmonyMethodExtensions.GetMergedFromType(type);
                if (info.declaringType == null || info.methodName == null
                    || info.methodType is not (null or MethodType.Normal))
                    throw new InvalidOperationException("Unverified Development hook declaration: " + type.FullName);
                targets = new[] { AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes)
                    ?? throw new MissingMethodException(info.declaringType.FullName, info.methodName) };
            }
            foreach (var target in targets)
            {
                if (!hooks.TryGetValue(target, out var patches)) hooks.Add(target, patches = new List<Type>());
                patches.Add(type);
            }
        }
        return hooks.Select(h => new Hook(h.Key, h.Value.ToArray(), null)).ToArray();
    }

    private static readonly List<Batch> Batches = new();

    // Every subscription enters the same native owner. An installation failure rolls
    // back this batch only; failed removals remain in Batches for StopAll to retry.
    internal static IDisposable Install(Hook[] hooks, DevelopmentNativeHooks? backend = null)
    {
        if (hooks == null) throw new ArgumentNullException(nameof(hooks));
        const string consumer = "fixed-hooks";
        var batch = new Batch();
        Batches.Add(batch);
        var installed = 0;
        var refused = 0;
        try
        {
            foreach (var hook in hooks)
            {
                if (hook.Refusal != null)
                {
                    NoteRefusal(consumer, hook, hook.Refusal);
                    refused++;
                    continue;
                }
                var callbacks = Callbacks(hook);
                var before = new List<string>();
                var after = new List<string>();
                foreach (var callback in callbacks)
                {
                    var info = HarmonyMethodExtensions.GetMergedFromType(callback.Method.DeclaringType!).Merge(new HarmonyMethod(callback.Method));
                    if (info.before != null) before.AddRange(info.before);
                    if (info.after != null) after.AddRange(info.after);
                }
                // Approved observation points: earliest readonly postfix for fixed
                // capture, finalizer for trace. Keep owner constraints explicit.
                var order = DevelopmentNativeHooks.Order.Shared with
                {
                    Before = before.Distinct(StringComparer.Ordinal).ToArray(),
                    After = after.Distinct(StringComparer.Ordinal).ToArray()
                };
                try
                {
                    batch.Leases.Add((backend ?? DevelopmentNativeHooks.Shared).Subscribe(hook.Target, consumer,
                        callbacks, order, DevelopmentNativeHooks.Lane.EarlyPostfix, DevelopmentTypedHooks.For(hook.Target)));
                }
                catch (Exception error)
                {
                    try { NoteRefusal(consumer, hook, error.GetBaseException().Message); }
                    catch { /* Preserve the installation failure, even if its recorder is unavailable. */ }
                    if (error.GetBaseException() is NotSupportedException
                        && !(backend ?? DevelopmentNativeHooks.Shared).Inspect().Any(status => status.Target.Equals(hook.Target) && status.CleanupPending))
                    { refused++; continue; }
                    throw;
                }
                installed++;
            }
            Plugin.PluginLog.LogInfo($"development.hooks-ready profile={consumer} installed={installed} refused={refused} coverage={(refused == 0 ? "complete" : "partial")} observation=early-readonly-postfix");
            RecTraceCoverage.Publish(consumer);
            return batch;
        }
        catch (Exception original)
        {
            try { batch.Dispose(); }
            catch (Exception cleanup)
            {
                try { original.Data["ForgeDevelopment.HookCleanupFailure"] = cleanup; }
                catch { /* A cleanup failure cannot replace the original installation error. */ }
            }
            throw;
        }
    }

    internal static DevelopmentHookCallbacks.Callback[] Callbacks(Hook hook)
    {
        var callbacks = new List<DevelopmentHookCallbacks.Callback>();
        foreach (var type in hook.PatchTypes)
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsDefined(typeof(HarmonyPrefix), false)) callbacks.Add(new(DevelopmentHookCallbacks.Phase.Prefix, method));
                if (method.IsDefined(typeof(HarmonyPostfix), false)) callbacks.Add(new(DevelopmentHookCallbacks.Phase.Postfix, method));
                if (method.IsDefined(typeof(HarmonyFinalizer), false)) callbacks.Add(new(DevelopmentHookCallbacks.Phase.Finalizer, method));
            }
        if (callbacks.Count == 0) throw new InvalidOperationException("Development hook has no explicit phase callbacks: " + hook.Target);
        return callbacks.ToArray();
    }

    internal static IDisposable SubscribeMarker(MethodInfo target, MethodInfo callback)
        => DevelopmentNativeHooks.Shared.Subscribe(target, "performance-marker:" + callback.Name,
            new[] { new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Postfix, callback) },
            DevelopmentNativeHooks.Order.Shared, DevelopmentNativeHooks.Lane.FinalizerReturned, DevelopmentTypedHooks.For(target));

    internal static void StopAll(DevelopmentNativeHooks? backend = null)
    {
        var failures = new List<Exception>();
        foreach (var batch in Enumerable.Reverse(Batches.ToArray()))
            try { batch.Dispose(); } catch (Exception error) { failures.Add(error); }
        // Also clear a failed Acquire that never returned a lease; its native owner
        // keeps that pending installation in its own recovery ledger.
        try { (backend ?? DevelopmentNativeHooks.Shared).StopAll(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Development shared hook cleanup incomplete.", failures);
    }

    private sealed class Batch : IDisposable
    {
        internal readonly List<IDisposable> Leases = new();
        public void Dispose()
        {
            var failures = new List<Exception>();
            for (var index = Leases.Count - 1; index >= 0; index--)
            {
                try { Leases[index].Dispose(); Leases.RemoveAt(index); }
                catch (Exception error) { failures.Add(error); }
            }
            if (Leases.Count == 0) Batches.Remove(this);
            if (failures.Count != 0) throw new AggregateException("Development hook batch cleanup incomplete.", failures);
        }
    }

    private static void NoteRefusal(string consumer, Hook hook, string reason)
    {
        RecTraceCoverage.Note(consumer, hook.Target, reason);
        RecSession.Write("session", "trace_refused", json =>
        {
            json.WriteString("profile", consumer);
            json.WriteString("type", hook.Target.DeclaringType?.FullName);
            json.WriteString("method", RecTraceCoverage.Signature(hook.Target));
            json.WriteString("reason", reason);
            json.WriteString("observationScope", "unavailable");
        });
    }
}
