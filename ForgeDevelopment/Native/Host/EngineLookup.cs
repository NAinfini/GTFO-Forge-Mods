using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using BepInEx.Unity.IL2CPP;

namespace ForgeDevelopment.Native;

/// <summary>Forge Engine is optional. Its author reads of Forge maps (<c>ForgeEngine.Authoring.AuthorReads</c>) are reached by
/// name at run time, the same way the console's <c>call static:</c> reaches any type, never through a compile-time reference.
/// Without the Engine every read answers <see cref="Missing"/>.</summary>
internal static class EngineLookup
{
    internal const string Missing = "forge-engine-not-installed";
    private const string EngineGuid = "NAinfini.ForgeEngine", Reads = "ForgeEngine.Authoring.AuthorReads";

    /// <summary>The installed Forge Engine's version, or null when it is not installed.</summary>
    internal static string? Version
        => IL2CPPChainloader.Instance.Plugins.TryGetValue(EngineGuid, out var info) ? info.Metadata.Version.ToString() : null;

    internal static bool Installed => Version != null;

    /// <summary>Calls one public static read; the read's own exception (its refusal code) comes back unwrapped.</summary>
    internal static T Call<T>(string name, params object?[] arguments)
    {
        var type = (Installed ? ExperimentTypes.Find(Reads) : null) ?? throw new InvalidOperationException(Missing);
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("forge-engine-read-missing:" + name);
        try { return (T)method.Invoke(null, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    /// <summary>The configured Engine mode, or "absent" when the Engine is not installed.</summary>
    internal static string Mode => Installed ? Call<string>("Mode") : "absent";
}
