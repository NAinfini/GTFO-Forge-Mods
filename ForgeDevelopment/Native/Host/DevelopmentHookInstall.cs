using System;
using System.Reflection;
using HarmonyLib;

namespace ForgeDevelopment.Native;

/// <summary>Development's hooks go on through ForgeCore's one hook layer (<see cref="ForgeCore.Hooks"/>): a native entry gets a
/// read-only observer beside its owner, a managed method this package owns is its owned set, an interop wrapper an IL hook.
/// Disposing the result takes exactly that hook off.</summary>
// patch-targets: run time (each caller's target is read at its DevelopmentHookInstall.Managed call)
internal static class DevelopmentHookInstall
{
    internal static HarmonyMethod? Method(MethodInfo? callback, int priority, string[]? before = null, string[]? after = null)
        => callback == null ? null : new HarmonyMethod(callback)
        { priority = priority, before = before ?? Array.Empty<string>(), after = after ?? Array.Empty<string>() };

    internal static IDisposable Observe(string writer, MethodInfo target, MethodInfo? prefix, MethodInfo? postfix, MethodInfo? finalizer,
        int prefixPriority = Priority.Normal, int postfixPriority = Priority.Normal, int finalizerPriority = Priority.Normal,
        string[]? prefixBefore = null, string[]? prefixAfter = null, string[]? postfixBefore = null, string[]? postfixAfter = null,
        string[]? finalizerBefore = null, string[]? finalizerAfter = null)
    {
        using (ForgeCore.StartupOperationTiming.Measure(ForgeCore.StartupOperationTiming.Operation.PatchInstallation))
            return ForgeCore.Hooks.Observe(writer, target, new ForgeCore.HookPart(Method(prefix, prefixPriority, prefixBefore, prefixAfter),
                Method(postfix, postfixPriority, postfixBefore, postfixAfter), Method(finalizer, finalizerPriority, finalizerBefore, finalizerAfter)));
    }

    /// <summary>A managed method: an interop wrapper (<paramref name="ilWrapper"/>) gets an IL hook, any other managed method is this
    /// writer's owned set.</summary>
    internal static IDisposable Managed(string writer, MethodInfo target, MethodInfo? prefix = null, MethodInfo? postfix = null,
        MethodInfo? finalizer = null, int prefixPriority = Priority.First, int postfixPriority = Priority.First,
        int finalizerPriority = Priority.Last, bool ilWrapper = false)
    {
        var part = new ForgeCore.HookPart(Method(prefix, prefixPriority), Method(postfix, postfixPriority), Method(finalizer, finalizerPriority));
        using (ForgeCore.StartupOperationTiming.Measure(ForgeCore.StartupOperationTiming.Operation.PatchInstallation))
        {
            if (ilWrapper) return ForgeCore.Hooks.Wrap(writer, target, part);
            ForgeCore.Hooks.Own(writer, target, new[] { part });
        }
        return new Owned(writer, target);
    }

    private sealed class Owned : IDisposable
    {
        private string? _writer;
        private readonly MethodInfo _target;
        internal Owned(string writer, MethodInfo target) { _writer = writer; _target = target; }
        public void Dispose()
        {
            var writer = _writer;
            _writer = null;
            if (writer != null) ForgeCore.Hooks.Disown(writer, _target);
        }
    }
}
