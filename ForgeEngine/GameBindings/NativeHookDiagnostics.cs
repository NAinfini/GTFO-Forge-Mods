using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

public static partial class NativeHookRegistration
{
    internal const string SharedHookCountersCode = "native-hook.shared-counters";
    internal static void LogSharedDiagnostics(RuntimeKernel? kernel, string boundary)
    {
        if (kernel == null || !kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        foreach (var row in NativeHookRegistration.ReadSharedDiagnostics())
            LogSharedDiagnostic(kernel, row, boundary);
    }
    /// <summary>On-demand snapshot for in-level evidence: writes every current shared-counter row once with
    /// boundary=on-demand through the same Info writer and returns the row count. Only an explicit Development command
    /// calls it; nothing polls it per frame. A missing kernel or closed Info level is an explicit failure.</summary>
    internal static int LogSharedCountersNow()
    {
        var kernel = GameRuntimeBridge.Kernel ?? throw new InvalidOperationException("runtime-not-started");
        if (!kernel.RuntimeLogs(RuntimeLogLevel.Info)) throw new InvalidOperationException("runtime-info-logging-closed");
        var rows = ReadSharedDiagnostics();
        foreach (var row in rows) LogSharedDiagnostic(kernel, row, "on-demand");
        return rows.Count;
    }
    internal static void LogSharedDiagnostic(RuntimeKernel? kernel, NativeHookDiagnostic row, string boundary)
    {
        if (kernel == null || !kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        var values = new List<RuntimeLogValue>(13)
        {
            RuntimeLogValue.Of("entry", row.Target.DeclaringType!.FullName + "." + row.Target.Name),
            RuntimeLogValue.Of("module", row.Target.Module.Name), RuntimeLogValue.Of("methodToken", row.Target.MetadataToken),
            RuntimeLogValue.Of("writer", row.Writer), RuntimeLogValue.Of("phase", row.Phase),
            RuntimeLogValue.Of("consumer", row.Consumer), RuntimeLogValue.Of("leaseEpoch", row.WorldEpoch),
            RuntimeLogValue.Of("status", row.Status), RuntimeLogValue.Of("installationAttempts", row.InstallationAttempts),
            RuntimeLogValue.Of("installations", row.Installations), RuntimeLogValue.Of("callbacks", row.Callbacks),
            RuntimeLogValue.Of("boundary", boundary)
        };
        if (row.ProviderCallbacks is { } providerCallbacks) values.Add(RuntimeLogValue.Of("providerCallbacks", providerCallbacks));
        kernel.LogRuntime(RuntimeLogLevel.Info, SharedHookCountersCode, new RuntimeLogFacts { Values = values });
    }
}
