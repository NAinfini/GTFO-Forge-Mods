using System.Linq;

namespace ForgeEngine.GameBindings;

/// <summary>Cold, read-only authoring diagnostics of the host's already admitted plan bytes.
/// Never discovers files, reloads a plan or runs a second scheduler.</summary>
internal static class RuntimePerformancePlans
{
    /// <summary>Call only on the simulation thread, outside a measured hot hook. Null means not ready.</summary>
    public static RuntimePerformancePlanSnapshot? Read() => GameRuntimeBridge.PerformancePlans();
    /// <summary>Flush the kernel's existing aggregated refusals at a measurement boundary;
    /// changes neither the logging level nor gameplay. Call on the simulation thread.</summary>
    public static void FlushDiagnostics() => GameRuntimeBridge.FlushPerformanceDiagnostics();
}

internal static partial class GameRuntimeBridge
{
    internal static void FlushPerformanceDiagnostics() => Kernel?.FlushDiagnosticSummaries();
    internal static RuntimePerformancePlanSnapshot? PerformancePlans()
    {
        if (Kernel == null || _admittedPlans == null || !CanExecute) return null;
        return RuntimePerformancePlanFacts.Read(_admittedPlans.Select(candidate =>
            (candidate.Document!.Plan(), candidate.Document!.Digest)), Kernel.SubscriptionGeneration, Kernel.BindingCapability);
    }
}
