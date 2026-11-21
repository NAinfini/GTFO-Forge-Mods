using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ForgeDevelopment.Native;

// Logical capture ownership only: all native targets share the one Development backend.
internal sealed class CaptureHarmonyGroup
{
    private readonly string _owner;
    private readonly DevelopmentNativeHooks.Lane _lane;
    private readonly DevelopmentNativeHooks? _backend;
    private readonly CaptureHookLifecycle _lifecycle = new();
    private readonly Dictionary<MethodBase, int> _registrations = new();
    private DevelopmentNativeHooks Backend => _backend ?? DevelopmentNativeHooks.Shared;
    internal CaptureHarmonyGroup(string owner, DevelopmentNativeHooks.Lane lane = DevelopmentNativeHooks.Lane.EarlyPostfix,
        DevelopmentNativeHooks? backend = null)
    { _owner = owner; _lane = lane; _backend = backend; }
    internal bool Installed => _lifecycle.Installed;
    internal bool HasPending => _lifecycle.HasPending;
    internal string? Fault => _lifecycle.Fault;
    internal void RegisterAdapter(DevelopmentNativeHooks.ReviewedAdapter adapter) => Backend.RegisterAdapter(adapter);
    internal void Install(Action install) => _lifecycle.Install(install);
    internal bool Stop()
    {
        if (!_lifecycle.Stop()) return false;
        _registrations.Clear();
        return true;
    }
    internal static string? RejectionReason(MethodBase method)
    {
        var backend = DevelopmentNativeHooks.Shared;
        return backend.Preflight(method);
    }
    internal void Patch(MethodBase method, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? finalizer = null)
    {
        var callbacks = new List<DevelopmentHookCallbacks.Callback>();
        void Add(HarmonyMethod? patch, DevelopmentHookCallbacks.Phase phase)
        { if (patch != null) callbacks.Add(new(phase, patch.method ?? throw new InvalidOperationException("Capture callback method unavailable."))); }
        Add(prefix, DevelopmentHookCallbacks.Phase.Prefix); Add(postfix, DevelopmentHookCallbacks.Phase.Postfix);
        Add(finalizer, DevelopmentHookCallbacks.Phase.Finalizer);
        var patches = new[] { prefix, postfix, finalizer }.Where(patch => patch != null).Select(patch => patch!).ToArray();
        var order = new DevelopmentNativeHooks.Order(Priority.First, Priority.First, Priority.Last,
            patches.SelectMany(patch => patch.before ?? Array.Empty<string>()).Distinct().ToArray(),
            patches.SelectMany(patch => patch.after ?? Array.Empty<string>()).Distinct().ToArray());
        var registration = _registrations.GetValueOrDefault(method);
        _registrations[method] = registration + 1;
        var consumer = registration == 0 ? _owner : _owner + "/" + registration;
        IDisposable? lease = null;
        _lifecycle.AttemptInstall(method.ToString() ?? method.Name,
            () => lease = Backend.Subscribe(method, consumer, callbacks, order, _lane),
            () =>
            {
                if (lease != null) { lease.Dispose(); return; }
                var status = Backend.Inspect().FirstOrDefault(row => row.Target.Equals(method));
                if (status is { CleanupPending: true, Consumers: 0, Fault: not null } && !Backend.RetryCleanup(method))
                    throw new InvalidOperationException("Capture native cleanup remains pending.", status.Fault);
            });
    }
}
