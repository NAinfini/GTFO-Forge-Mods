using System;
using BepInEx.Configuration;
using UnityEngine;

namespace ForgeDevelopment.Native;

internal static partial class Settings
{
    internal static ConfigEntry<bool> PerformanceLogging = null!;
    internal static ConfigEntry<float> PerformanceSummarySeconds = null!;
    internal static ConfigEntry<KeyCode> PerformanceSnapshotKey = null!;
    internal static void Bind(ConfigFile config)
    {
        PerformanceLogging = config.Bind("Performance Diagnostics", "EnablePerformanceLogging", false,
            "Collect original game performance. Restart required.");
        PerformanceSummarySeconds = config.Bind("Performance Diagnostics", "SummaryIntervalSeconds", 1f,
            new ConfigDescription("Seconds between background log summaries.", new AcceptableValueRange<float>(0.25f, 10f)));
        PerformanceSnapshotKey = config.Bind("Performance Diagnostics", "ManualSnapshotKey", KeyCode.F9,
            "Write a checkpoint and flush the current summary. No scene or asset scan.");
    }
}

public sealed class PerformanceMonitor : MonoBehaviour
{
    public PerformanceMonitor(IntPtr pointer) : base(pointer) { }
    public void Start()
    {
        using var cpu = ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired();
        try { PerformanceDiagnostics.Start(gameObject.scene); }
        catch (Exception error) { enabled = false; Plugin.PluginLog.LogError("Performance collection failed: " + error); }
    }
    public void Update() => RuntimeDiagnostics.Budgeted(PerformanceDiagnostics.Tick);
    public void OnApplicationQuit() => PerformanceDiagnostics.Stop();
    public void OnDestroy() => PerformanceDiagnostics.Stop();
}
