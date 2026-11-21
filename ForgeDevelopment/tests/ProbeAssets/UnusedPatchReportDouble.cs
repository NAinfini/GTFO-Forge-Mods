namespace ForgeDevelopment.Native;

// RecTracer.cs also defines the report formatter, whose optional method list refers
// to this native patch type. The parser test never creates a patch or calls that
// formatter; no Harmony/native behaviour is represented by this compile-only type.
internal sealed class RecPatch
{
    internal string Subject => throw new System.NotSupportedException("ProbeAssets only parses configuration");
    internal RecTraceEntry Entry => throw new System.NotSupportedException();
    internal System.Reflection.MethodBase Method => throw new System.NotSupportedException();
    internal long Id => throw new System.NotSupportedException();
    internal bool Installed => throw new System.NotSupportedException();
    internal long Installation => throw new System.NotSupportedException();
    internal string ObservationScope => throw new System.NotSupportedException();
    internal string? NativeReason => throw new System.NotSupportedException();
    internal string? Removed => throw new System.NotSupportedException();
    internal long Failures => throw new System.NotSupportedException();
    internal bool CleanupPending => throw new System.NotSupportedException();
    internal long RateDropped => throw new System.NotSupportedException();
    internal long BudgetDropped => throw new System.NotSupportedException();
}
