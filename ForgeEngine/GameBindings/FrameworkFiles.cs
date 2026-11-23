using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

internal static class FrameworkFiles
{
    /// <summary>Cap on one discovered plan file; enforced by <see cref="PlanDiscovery"/> before it reads a candidate's bytes.</summary>
    internal const int MaximumPlanBytes = Wire.PackageLimits.MaximumPlanBytes;
    internal static string Resolve(string root, string relative)
        => PublishedResources.ResolveWithinRoot(root, relative);
}
