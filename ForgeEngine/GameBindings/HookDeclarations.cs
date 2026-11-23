using System;
using System.Reflection;

namespace ForgeEngine.GameBindings;

/// <summary>Harmony ordering carried by a Runtime-owned declaration. Arrays are copied and normalized at admission.</summary>
public sealed record NativeHookOrder(
    int PrefixPriority = 800, int PostfixPriority = 800, int FinalizerPriority = 0,
    string[]? PrefixBefore = null, string[]? PrefixAfter = null,
    string[]? PostfixBefore = null, string[]? PostfixAfter = null,
    string[]? FinalizerBefore = null, string[]? FinalizerAfter = null);

public static partial class NativeHookRegistration
{
    private static Func<string, MethodInfo, MethodInfo?, MethodInfo?, MethodInfo?, NativeHookOrder, bool, IDisposable>? _declareManaged = null;
    private static Action<string, MethodInfo>? _releaseManaged = null;

    static NativeHookRegistration() => InitializeManagedHooks();
    static partial void InitializeManagedHooks();

    /// <summary>Declare a Runtime-installed managed hook. <paramref name="ilWrapper"/> selects the reviewed
    /// HarmonyManipulator/ILHook wrapper shape; no caller-supplied installer is accepted.</summary>
    public static IDisposable DeclareManaged(string writer, MethodInfo target, MethodInfo? prefix = null,
        MethodInfo? postfix = null, MethodInfo? finalizer = null, int prefixPriority = 800,
        int postfixPriority = 800, int finalizerPriority = 0, bool ilWrapper = false)
        => DeclareManaged(writer, target, prefix, postfix, finalizer,
            new(prefixPriority, postfixPriority, finalizerPriority), ilWrapper);

    /// <summary>Declare a Runtime-installed managed hook with explicit phase ordering.</summary>
    public static IDisposable DeclareManaged(string writer, MethodInfo target, MethodInfo? prefix,
        MethodInfo? postfix, MethodInfo? finalizer, NativeHookOrder order, bool ilWrapper = false)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(order);
        return (_declareManaged ?? throw new NotSupportedException("managed-hook-backend-unavailable"))
            (writer, target, prefix, postfix, finalizer, order, ilWrapper);
    }

    /// <summary>Retry cleanup retained after a managed declaration failed or its last lease could not be released.
    /// No retained record is an idempotent success; active leases are never revoked by this recovery entry.</summary>
    public static void ReleaseManaged(string writer, MethodInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        (_releaseManaged ?? throw new NotSupportedException("managed-hook-backend-unavailable"))(writer, target);
    }
}
