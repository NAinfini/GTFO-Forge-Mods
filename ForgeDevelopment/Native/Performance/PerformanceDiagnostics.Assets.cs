namespace ForgeDevelopment.Native;

internal static partial class PerformanceDiagnostics
{
    // Performance collection no longer walks the world or inventories assets.
    // Preserve the existing cleanup interface used by RuntimeDiagnostics; there
    // is no scan to cancel and its state is truthfully always inactive.
    internal static bool WorldInspectionActive => false;
    internal static void CancelWorldInspection() { }
}
