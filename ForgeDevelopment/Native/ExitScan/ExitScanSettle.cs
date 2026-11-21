using System;
using System.Collections.Generic;
using ChainedPuzzles;
using Il2CppInterop.Runtime.InteropTypes;
using LevelGeneration;

namespace ForgeDevelopment.Native;

/// <summary>The native side of the exit-scan settle gate: reads the elevator and level-exit scan puzzles of the current
/// floor, builds the gate that stops their player scanners through the vanilla calls, and records the return-boundary
/// lifetime of the captured instances. A package that returns to the menu through the game's own quit path (the game
/// drive steps) owns the call order; this class owns the reads and the gate.</summary>
public static class ExitScanSettle
{
    public const int MaximumWaitFrames = 120;
    private static ExitScanLifetimeCapture? _lifetime;

    /// <summary>Receives every lifetime observation row. The log line is always written; a package that keeps its own
    /// report (the pack test report) subscribes here, and no sink is a valid state.</summary>
    public static Action<IReadOnlyDictionary<string, string>>? LifetimeSink { get; set; }

    public static void BeforeQuit() => _lifetime?.Observe("before-native-quit");
    public static void Cleanup(string phase)
    {
        try { _lifetime?.Observe("cleanup-" + phase); }
        finally { if (phase == "after") _lifetime = null; }
    }
    public static void Stop() => _lifetime = null;

    public static void ObserveCurrent()
    {
        if (Builder.CurrentFloor == null)
            throw new InvalidOperationException("exit-scan.lifetime-level-unavailable");
        if (CaptureScans().Count == 0)
            throw new InvalidOperationException("exit-scan.lifetime-instances-unavailable");
        _lifetime!.Observe("manual-before-return");
    }

    private static List<ExitScanProbe> CaptureScans()
    {
        var probes = new List<ExitScanProbe>();
        _lifetime = new ExitScanLifetimeCapture(ExitScanEnvironment.WorldEpoch);
        Add(probes, "ElevatorShaftLanding.Current.m_puzzle", ElevatorShaftLanding.Current?.m_puzzle);

        var floor = Builder.Current?.m_currentFloor;
        if (floor != null && !floor.WasCollected)
            foreach (var component in ForgeCore.GameFloor.Components(floor.transform))
            {
                if (component == null || component.WasCollected) continue;
                var exit = component.TryCast<LG_LevelExitGeo>();
                if (exit == null || exit.WasCollected) continue;
                Add(probes, "LG_LevelExitGeo.m_puzzle", exit.m_puzzle);
            }
        return probes;
    }

    public static ExitScanSettleGate Begin()
    {
        var probes = CaptureScans();
        var lifetime = _lifetime ?? throw new InvalidOperationException("exit-scan.lifetime-unavailable");
        lifetime.Observe("return-begin");
        return new ExitScanSettleGate(ExitScanEnvironment.WorldEpoch, ExitScanEnvironment.Frame, MaximumWaitFrames, probes, () =>
        {
            // Instance and core are siblings. Let the original end-session path perform cleanup;
            // an early manager cleanup neither retires its sibling cores nor proves callback retirement.
            lifetime.Observe("inactive-before-native-quit");
            ExitScanEnvironment.Info("exit-scans-return-prepared before-floor-cleanup world=" + ExitScanEnvironment.WorldEpoch
                + " frame=" + ExitScanEnvironment.Frame + " observation=recorded");
        });
    }

    private static void Add(List<ExitScanProbe> probes, string source, ChainedPuzzleInstance? puzzle)
    {
        if (puzzle == null || puzzle.WasCollected || puzzle.Pointer == IntPtr.Zero) return;
        var scanners = new List<ExitScanScannerProbe>();
        var cores = puzzle.m_chainedPuzzleCores
            ?? throw new ExitScanSettleException("exit-scan.cores-unavailable", source + " has no core list");
        foreach (var core in cores) AddScanners(scanners, source, core, 0);
        probes.Add(new ExitScanProbe(source, puzzle.Pointer.ToInt64(),
            () => Read(puzzle, source, solved: false),
            () => Read(puzzle, source, solved: true),
            () => puzzle.AttemptInteract(eChainedPuzzleInteraction.Deactivate),
            scanners));
        _lifetime?.Add(source, puzzle);
    }

    /// <summary>Collects every player scanner a puzzle's cores own. A bioscan core owns one scanner; a cluster core
    /// owns child cores. Any other core cannot be shown stopped, so it is refused by name rather than skipped.</summary>
    private static void AddScanners(List<ExitScanScannerProbe> scanners, string source, Il2CppObjectBase? value, int depth)
    {
        if (depth > 4) throw new ExitScanSettleException("exit-scan.core-depth", source + " nests cluster cores too deeply");
        if (value == null || value.WasCollected)
            throw new ExitScanSettleException("exit-scan.core-unavailable", source + " has a missing core");
        var bioscan = value.TryCast<CP_Bioscan_Core>();
        if (bioscan != null)
        {
            var name = "core@0x" + bioscan.Pointer.ToInt64().ToString("X");
            var scanner = bioscan.m_playerScanner?.TryCast<CP_PlayerScanner>()
                ?? throw new ExitScanSettleException("exit-scan.scanner-unavailable", source + "/" + name + " has no player scanner");
            var sync = bioscan.m_sync?.TryCast<CP_Bioscan_Sync>()
                ?? throw new ExitScanSettleException("exit-scan.scanner-unavailable", source + "/" + name + " has no bioscan sync");
            scanners.Add(new ExitScanScannerProbe(name, () => ReadScanner(scanner, sync, source + "/" + name), () =>
            {
                // Vanilla CP_PlayerScanner.StopScan (RVA 0x1732A60): stops m_scanRoutine and
                // m_checkPlayerIsCloseRoutine, clears both and m_scanActive. It is the same call
                // CP_Bioscan_Core.OnSyncStateChange makes for the Disabled status.
                scanner.StopScan();
                ExitScanEnvironment.Info("exit-scan-scanner-stopped " + source + "/" + name
                    + " world=" + ExitScanEnvironment.WorldEpoch + " frame=" + ExitScanEnvironment.Frame);
            }));
            return;
        }
        var cluster = value.TryCast<CP_Cluster_Core>();
        if (cluster != null)
        {
            var children = cluster.m_childCores
                ?? throw new ExitScanSettleException("exit-scan.cores-unavailable", source + " has a cluster core with no children");
            foreach (var child in children) AddScanners(scanners, source, child, depth + 1);
            return;
        }
        throw new ExitScanSettleException("exit-scan.core-unsupported",
            source + " has a core whose scanner cannot be stopped or read: 0x" + value.Pointer.ToInt64().ToString("X"));
    }

    private static ExitScanScannerState ReadScanner(CP_PlayerScanner scanner, CP_Bioscan_Sync sync, string name)
    {
        if (scanner.WasCollected || scanner.Pointer == IntPtr.Zero || sync.WasCollected || sync.Pointer == IntPtr.Zero)
            throw new InvalidOperationException(name + " was collected before its scanner stopped");
        return new ExitScanScannerState(scanner.m_scanActive, scanner.m_scanRoutine != null,
            scanner.m_checkPlayerIsCloseRoutine != null, scanner.m_lastPlayerCount, sync.GetCurrentState().status.ToString());
    }

    private static bool Read(ChainedPuzzleInstance puzzle, string source, bool solved)
    {
        if (puzzle.WasCollected || puzzle.Pointer == IntPtr.Zero)
            throw new InvalidOperationException(source + " was collected before it became inactive");
        return solved ? puzzle.IsSolved : puzzle.IsActive;
    }
}
