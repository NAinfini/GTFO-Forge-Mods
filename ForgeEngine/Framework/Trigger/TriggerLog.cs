using ForgeEngine.Framework;

namespace ForgeEngine.Trigger;

/// <summary>The trigger provider's diagnostic codes in the Forge Engine log (contract §3.2 I-DIAG). Each has one
/// fixed level.</summary>
internal static partial class TriggerLogCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/TriggerLogCodes.g.cs).
}

/// <summary>
/// The trigger provider's way into the Forge Engine log: its registration's own <see cref="RuntimeModuleHandle.Log"/>.
/// The evaluators are declared by the managed module before the registration exists, so the host hands the
/// registration over once it does; with none attached (every offline suite) nothing is written. Callers ask
/// <see cref="Logs"/> before they measure or build anything, so a closed level costs one comparison.
/// </summary>
internal static class TriggerLog
{
    private static RuntimeModuleHandle? _handle;

    /// <summary>Hands the provider's registration over. Called once, right after the registration exists.</summary>
    public static void Attach(RuntimeModuleHandle handle) => _handle = handle ?? throw new System.ArgumentNullException(nameof(handle));

    /// <summary>Withdraws only this registration after startup fails.</summary>
    public static void Detach(RuntimeModuleHandle handle)
    {
        if (ReferenceEquals(_handle, handle)) _handle = null;
    }

    public static bool Logs(RuntimeLogLevel level) => _handle is { } handle && handle.Logs(level);

    public static void Write(RuntimeLogLevel level, string code, in RuntimeLogFacts facts) => _handle?.Log(level, code, in facts);
}
