using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using LevelGeneration;
using SNetwork;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The capture side's entry point. It owns the snapshot schedule, the change pass and the triggers — the shared
/// native callbacks and Runtime upstream observations — that say when the game changed something worth a snapshot. The core
/// calls <see cref="EnsureStarted"/> once after the session opens and <see cref="SnapshotAll"/> on the snapshot hotkey;
/// everything else here is driven by the game's own calls, so a reason is never invented by a timer that guessed.
/// </summary>
internal static partial class CaptureRegistry
{
    private const string Where = "ForgeDevelopment.Native.CaptureRegistry";

    private static CaptureMonitor? _monitor;
    private static CaptureChangeDetector? _changes;
    private static CaptureChangePass? _changePass;
    private static long _snapshots;
    private static long _changed;
    private static long _lastPeriodicTick;
    private static long _lastChangeTick;
    private static float _periodicAccumulator;
    private static readonly CaptureSnapshotPass SnapshotPass = new();
    private static readonly CaptureWorkBudget FrameBudget = new(CaptureBudgets.ChangeRowsPerFrame, CaptureBudgets.SnapshotMillisecondsPerFrame);
    private static readonly CaptureWorkBudget ChangeBudget = new(CaptureBudgets.ChangeRowsPerFrame, CaptureBudgets.ChangeMillisecondsPerFrame);
    private static readonly Func<IEnumerable<CaptureRow?>> ChangeSource = CaptureWorld.ChangeRows;
    private static readonly Action<CaptureChangeResult, long> ChangeOutput = WriteChanges;
    private static readonly Func<bool> CpuCanContinue = static () => ForgeCore.DiagnosticCpuBudget.Shared.CanContinue;
    private static string _lastReason = CaptureReason.Manual;
    private static int _periodicSeconds = DefaultPeriodicSeconds;
    private static bool _started;

    /// <summary>The low-frequency snapshot cadence. Thirty seconds is the default the task names; the value is
    /// clamped so a configuration mistake cannot turn the snapshot into a frame cost.</summary>
    internal const int DefaultPeriodicSeconds = 30;
    internal const int MinPeriodicSeconds = 5;
    internal const int MaxPeriodicSeconds = 600;

    internal static bool Active => _started && RecSession.Active;
    internal static long Snapshots => _snapshots;
    internal static long Changes => _changed;

    /// <summary>The reasons the capture answers, in the order the game produces them in one expedition. The probe
    /// index publishes this so a reader knows which snapshot reasons a session should contain.</summary>
    internal static readonly string[] Reasons = CaptureReason.All;

    internal static void EnsureStarted(Func<CaptureMonitor> createMonitor, int periodicSeconds)
    {
        ArgumentNullException.ThrowIfNull(createMonitor);
        if (!RecSession.Active || _started) return;
        _periodicSeconds = Math.Clamp(periodicSeconds, MinPeriodicSeconds, MaxPeriodicSeconds);
        _monitor = createMonitor();
        _changes = new CaptureChangeDetector(CaptureBudgets.ChangeRowsPerFrame, CaptureBudgets.ChangeMillisecondsPerFrame, CaptureWorld.ChangeIdentityPresent);
        _changePass = new CaptureChangePass(_changes, CaptureBudgets.ChangeSweepIntervalMilliseconds);
        _monitor.IntervalSeconds = _periodicSeconds;
        _started = true;
        StartObservations();
        // Bootstrap an already running world's enemy index through the independent full-snapshot channel.
        SnapshotAll(CaptureReason.Periodic);
        RecSession.Note("capture_started", json =>
        {
            json.WriteString("periodicSeconds", _periodicSeconds.ToString(CultureInfo.InvariantCulture));
            json.WriteNumber("changeRowsPerFrame", CaptureBudgets.ChangeRowsPerFrame);
            json.WriteNumber("changeMsPerFrame", CaptureBudgets.ChangeMillisecondsPerFrame);
            json.WriteNumber("snapshotMsPerFrame", CaptureBudgets.SnapshotMillisecondsPerFrame);
            json.WriteNumber("changeSweepIntervalMs", CaptureBudgets.ChangeSweepIntervalMilliseconds);
            json.WritePropertyName("reasons");
            json.WriteStartArray();
            foreach (var reason in Reasons) json.WriteStringValue(reason);
            json.WriteEndArray();
        });
    }

    /// <summary>Releases the capture after a startup that unwound. The snapshot and change state is dropped with the
    /// monitor component, so a later <see cref="EnsureStarted"/> starts from the level it is actually in.</summary>
    internal static void Stop()
    {
        if (!_started) return;
        StopObservations();
        RecPopulationBudget.Reset("capture-stop");
        if (_monitor != null) { _monitor.enabled = false; UnityEngine.Object.Destroy(_monitor); }
        _monitor = null;
        _changePass?.Reset(); _changePass = null;
        _changes = null; CaptureWorld.ResetChanges();
        SnapshotPass.Reset();
        CaptureFieldReadbackNative.Reset();
        _started = false;
        _periodicAccumulator = 0f;
    }

    /// <summary>Enqueues a complete read, retaining its reason. Hooks never walk or serialize a world.
    /// Acquisition spans frames and carries its source epoch and both endpoints; it is not an atomic checkpoint.</summary>
    internal static bool SnapshotAll(string reason, Action<CaptureSnapshot?>? completed = null)
    {
        if (!Active) return false;
        _changes?.InvalidateMembership();
        if (reason is CaptureReason.LevelEntered or CaptureReason.LevelEnded or CaptureReason.CheckpointRestored)
        {
            _changePass?.Reset(); CaptureWorld.ResetChanges();
            if (reason is CaptureReason.LevelEnded or CaptureReason.CheckpointRestored) RecPopulationBudget.Reset(reason);
        }
        var snapshot = new CaptureSnapshot(reason, "world", _observationTick) { WorldEpoch = RecTime.WorldEpoch };
        var stream = new CaptureSnapshotStream(snapshot, () => _observationTick, _observationEnvelope);
        IEnumerable<int> Complete(CaptureCompletion result)
        {
            snapshot.CompletedTick = _observationTick;
            snapshot.MainThreadMilliseconds = result.TotalMilliseconds;
            snapshot.PeakSliceMilliseconds = result.PeakMilliseconds;
            snapshot.AllocatedBytes = result.AllocatedBytes;
            snapshot.CaptureFrames = result.Frames;
            snapshot.AcquisitionComplete &= result.Succeeded;
            snapshot.TerminalStatus = snapshot.AcquisitionComplete ? "completed" : result.Succeeded ? "error" : result.Status;
            if (!snapshot.AcquisitionComplete) snapshot.FieldCompleteness = snapshot.CoverageCompleteness = "incomplete";
            if (!result.Succeeded) snapshot.Skipped.Add(result.Status + (result.Error.Length == 0 ? "" : ":" + result.Error));
            snapshot.Notes.Add("acquisition-spans-frames; values are observed at their individual read times");
            snapshot.Notes.Add("change-pipeline " + Describe());
            foreach (var step in stream.Complete()) yield return step;
        }
        var admission = SnapshotPass.Enqueue(reason, snapshot.WorldEpoch, () => SnapshotWork(snapshot, stream).GetEnumerator(), result =>
        {
            snapshot.AcquisitionComplete &= result.Succeeded;
            if (snapshot.AcquisitionComplete) { _snapshots++; _lastReason = reason; }
            completed?.Invoke(snapshot.AcquisitionComplete ? snapshot : null);
        }, finalize: result => Complete(result).GetEnumerator());
        return admission == CaptureEnqueueResult.Accepted;
    }

    private static IEnumerable<int> SnapshotWork(CaptureSnapshot snapshot, CaptureSnapshotStream stream)
    {
        stream.SetContext(RecSession.FreezeContext());
        snapshot.StartedTick = Now();
        var reason = snapshot.Reason;
        var manual = reason == CaptureReason.Manual;
        var options = new CaptureOptions(reason, snapshot.Tick)
        {
            IncludeText = manual || reason is CaptureReason.Periodic or CaptureReason.LevelGenerated,
            IncludeEnvironment = manual || reason is CaptureReason.Periodic,
            IncludeNavMarkers = reason is not CaptureReason.Change,
            IncludeDetail = reason is not CaptureReason.Change,
            IncludeLoadout = manual || reason is CaptureReason.LevelGenerated
        };
        foreach (var step in stream.Collect(CaptureWorld.CollectSlices(options, snapshot))) yield return step;
        foreach (var step in stream.Collect(CaptureFieldReadbackNative.CollectSlices(snapshot))) yield return step;
        // Do not seed a delayed world view over the live incremental baseline.
        yield return 0;
        RecPopulationBudget.Snapshot(reason);
        if (CaptureReason.CapturesDataBlocks(reason))
        {
            var blocks = new CaptureSnapshot(reason, "datablocks", snapshot.Tick) { WorldEpoch = snapshot.WorldEpoch };
            var blockStream = new CaptureSnapshotStream(blocks, Now);
            foreach (var step in blockStream.Collect(CaptureDataBlocks.CollectSlices(blocks, int.MaxValue))) yield return step;
            blocks.CompletedTick = Now();
            snapshot.AcquisitionComplete &= blocks.AcquisitionComplete;
            foreach (var step in blockStream.Complete()) yield return step;
            yield return 0;
            foreach (var step in CaptureEnemyAppearance.SnapshotSlices(reason, snapshot.Tick)) yield return step;
            foreach (var step in CaptureEnemyLimbs.SnapshotSlices(reason, snapshot.Tick)) yield return step;
        }
    }

    /// <summary>An explicit data-block request joins the same budgeted cursor and streaming writer.</summary>
    internal static void SnapshotDataBlocks(string reason, long tick)
    {
        if (!Active) return;
        var snapshot = new CaptureSnapshot(reason, "datablocks", tick) { WorldEpoch = RecTime.WorldEpoch };
        var stream = new CaptureSnapshotStream(snapshot, () => _observationTick, _observationEnvelope);
        IEnumerable<int> Complete(CaptureCompletion result)
        {
            snapshot.CompletedTick = _observationTick;
            snapshot.AcquisitionComplete &= result.Succeeded;
            snapshot.TerminalStatus = snapshot.AcquisitionComplete ? "completed" : result.Succeeded ? "error" : result.Status;
            if (!snapshot.AcquisitionComplete) snapshot.FieldCompleteness = snapshot.CoverageCompleteness = "incomplete";
            if (!result.Succeeded) snapshot.Skipped.Add(result.Status + (result.Error.Length == 0 ? "" : ":" + result.Error));
            foreach (var step in stream.Complete()) yield return step;
        }
        SnapshotPass.Enqueue(reason, snapshot.WorldEpoch, () =>
        {
            stream.SetContext(RecSession.FreezeContext());
            return stream.Collect(CaptureDataBlocks.CollectSlices(snapshot, int.MaxValue)).GetEnumerator();
        }, _ => { }, finalize: result => Complete(result).GetEnumerator());
    }

    /// <summary>Rows per layout record. A zone with more areas continues in further records of the same zone, so a
    /// record stays far under the per-record budget and no area is dropped.</summary>
    internal const int LayoutRowsPerRecord = 32;

    internal static void EnqueueWork(string reason, IEnumerable<int> work)
    {
        if (!Active) return;
        var context = _observationEnvelope;
        SnapshotPass.Enqueue(reason, RecTime.WorldEpoch, () => work.GetEnumerator(), result =>
        {
            if (!result.Succeeded && !RecSession.WriteFrozen("session", "capture_incomplete", json =>
            { json.WriteString("reason", reason); json.WriteString("terminalStatus", result.Status); json.WriteString("error", result.Error); }, RecFrozenBytes.Measure(result) + RecFrozenBytes.Measure(reason), context))
                throw new InvalidOperationException("capture-incomplete-receipt-refused");
        });
    }

    /// <summary>The level layout shares the same frame budget and frozen output path as world snapshots.</summary>
    internal static void SnapshotLayout(string reason)
    {
        if (!Active) return;
        EnqueueWork(reason, LayoutWork(reason, _observationTick));
    }

    private static IEnumerable<int> LayoutWork(string reason, long tick)
    {
        var records = new List<CaptureSnapshot>();
        foreach (var step in CaptureWorld.CollectLayoutSlices(records, reason, tick, LayoutRowsPerRecord)) yield return step;
        foreach (var record in records)
        {
            yield return 0;
            record.CompletedTick = Now(); record.WorldEpoch = RecTime.WorldEpoch;
            foreach (var step in new CaptureSnapshotStream(record).Complete()) yield return step;
        }
    }

    /// <summary>
    /// The per-frame change pass. It is deliberately not a snapshot: it compares a few fields of the objects a change
    /// record is defined over and writes only the rows that moved, with the cost it spent. A frame that finds nothing
    /// writes nothing beyond its periodic counters.
    /// </summary>
    internal static void InvalidateChangeMembership() => _changes?.InvalidateMembership();

    internal static void Tick()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentCapturePump);
        if (!Active || _changePass == null) return;
        var cpu = ForgeCore.DiagnosticCpuBudget.Shared;
        cpu.BeginFrame(RecTime.Frame);
        if (!cpu.TryReserve(out var reservation)) return;
        using var charge = reservation;
        if (!RecObservation.TryEnterReadPass()) return;
        try
        {
            var tick = Now();
            FrameBudget.Restart(CpuCanContinue);
            RefreshObservationContext(tick);
#if FORGE_DEVELOPMENT_TIMING
            using (DevelopmentSlowOperation.Measure("capture.snapshot"))
#endif
            SnapshotPass.Run(RecTime.WorldEpoch, FrameBudget, RecSession.SessionId, _observationGeneration, _observationFrame);
            if (CaptureWorld.ChangesReady && FrameBudget.ElapsedMilliseconds <= CaptureBudgets.SnapshotMillisecondsPerFrame - CaptureBudgets.ChangeMillisecondsPerFrame)
            {
                ChangeBudget.Restart(CpuCanContinue);
#if FORGE_DEVELOPMENT_TIMING
                using (DevelopmentSlowOperation.Measure("capture.changes"))
#endif
                _changePass.Run(ChangeSource, tick, ChangeBudget, ChangeOutput);
            }
            _lastChangeTick = tick;
        }
        catch (Exception error) { Log("capture-pass:" + SnapshotPass.LastReason, error); }
        finally { RecObservation.Exit(); }
    }

    /// <summary>The periodic cadence. It is a tick counter rather than a timer so a paused or unfocused game does not
    /// accumulate missed snapshots and then take several at once. While a pass is still running the next snapshot waits
    /// for it, so a slow snapshot never becomes continuous capture work.</summary>
    internal static bool DueForPeriodic(float deltaSeconds)
    {
        if (!Active) return false;
        _periodicAccumulator += deltaSeconds;
        if (_periodicAccumulator < _periodicSeconds || SnapshotPass.Pending) return false;
        _periodicAccumulator = 0f;
        _lastPeriodicTick = Now();
        return true;
    }

    private static void WriteChanges(CaptureChangeResult result, long tick)
    { CaptureStateWriter.WriteChanges(result, tick, "world"); _changed++; }

    internal static string Describe()
        => "changeTotalMs=" + (_changePass?.LastMilliseconds ?? 0).ToString("F3", CultureInfo.InvariantCulture)
            + " changeLifetimePeakMs=" + (_changePass?.PeakMilliseconds ?? 0).ToString("F3", CultureInfo.InvariantCulture)
            + " changeAllocatedBytes=" + (_changePass?.LastAllocatedBytes ?? 0).ToString(CultureInfo.InvariantCulture)
            + " changeFrames=" + (_changePass?.Frames ?? 0).ToString(CultureInfo.InvariantCulture)
            + " changeWorkSteps=" + (_changePass?.LastSteps ?? 0).ToString(CultureInfo.InvariantCulture)
            + " changeBudgetStops=" + (_changePass?.BudgetStops ?? 0).ToString(CultureInfo.InvariantCulture)
            + " changeIndexReady=" + CaptureWorld.ChangesReady
            + " changeOutputPending=" + (_changePass?.Pending ?? false)
            + " " + "reason=" + _lastReason + " snapshots=" + _snapshots.ToString(CultureInfo.InvariantCulture)
            + " changes=" + _changed.ToString(CultureInfo.InvariantCulture)
            + " changeTick=" + _lastChangeTick.ToString(CultureInfo.InvariantCulture)
            + " periodicTick=" + _lastPeriodicTick.ToString(CultureInfo.InvariantCulture)
            + " " + (_changes?.Describe() ?? "changes=off");

    /// <summary>The frame number a snapshot and its change record are stamped with. It is read here rather than from
    /// the session context so a capture error cannot come from the thing that labels the capture.</summary>
    private static long Now()
    {
        try { return Time.frameCount; }
        catch (Exception) { return -1; } // Il2Cpp read guard: -1 is the documented "no frame" stamp.
    }

    internal static void Log(string what, Exception error)
    {
        try
        {
            RecSession.Write("log", "capture_error", json =>
            {
                json.WriteString("where", Where + ":" + what);
                json.WriteString("error", error.GetType().Name);
                json.WriteString("message", RecReflect.Truncate(error.Message, 200));
            });
        }
        catch (Exception) { } // Last resort: the capture error log itself failed, and there is no other sink to report to.
    }
}

/// <summary>The snapshot clock. It lives on the plugin object and does nothing but count seconds and drive the change
/// pass, so the capture has exactly one place per frame where it spends time.</summary>
internal sealed class CaptureMonitor : MonoBehaviour
{
    internal float IntervalSeconds { get; set; } = CaptureRegistry.DefaultPeriodicSeconds;

    private void Update()
    {
        try
        {
            if (IntervalSeconds > 0f && CaptureRegistry.DueForPeriodic(ForgeCore.Frame.UnscaledDeltaTime)) CaptureRegistry.SnapshotAll(CaptureReason.Periodic);
            CaptureRegistry.Tick();
            EnemyNavigationWatch.Tick();
        }
        catch (Exception error) { CaptureRegistry.Log("monitor", error); }
    }
}

/// <summary>One snapshot reason, declared on the game boundary that produces it: GTFO-API's level events, ForgeCore's
/// checkpoint and host-migration events and the remaining shared native callbacks. Every one defers the work to the scheduler
/// instead of recording inside the native call. The subscription lives as long as the diagnostics collection, not the world
/// capture: FactoryDone also completes the generation report (<see cref="RuntimeDiagnostics.StructureFinished"/>), which the
/// landing gate waits on with capture switched off; the snapshot callbacks return at once while the capture is not running.</summary>
internal static class CaptureTriggers
{
    private static bool _subscribed;

    private static void Fire(string reason)
    {
        try { CaptureRegistry.SnapshotAll(reason); }
        catch (Exception error) { CaptureRegistry.Log("trigger:" + reason, error); }
    }

    private static void LevelEntered() => Fire(CaptureReason.LevelEntered);
    private static void CheckpointStored() => Fire(CaptureReason.CheckpointStored);
    private static void CheckpointRestored() => Fire(CaptureReason.CheckpointRestored);
    private static void HostMigrated() => Fire(CaptureReason.HostMigrated);

    internal static void Subscribe()
    {
        if (_subscribed) throw new InvalidOperationException("capture-upstream-already-subscribed");
        GTFO.API.LevelAPI.OnEnterLevel += LevelEntered;
        GTFO.API.LevelAPI.OnFactoryDone += FactoryFinished;
        ForgeCore.GameEvents.CheckpointReached += CheckpointStored;
        ForgeCore.GameEvents.CheckpointReloaded += CheckpointRestored;
        ForgeCore.GameEvents.MasterMigrated += HostMigrated;
        _subscribed = true;
    }

    internal static void Unsubscribe()
    {
        if (!_subscribed) return;
        GTFO.API.LevelAPI.OnEnterLevel -= LevelEntered;
        GTFO.API.LevelAPI.OnFactoryDone -= FactoryFinished;
        ForgeCore.GameEvents.CheckpointReached -= CheckpointStored;
        ForgeCore.GameEvents.CheckpointReloaded -= CheckpointRestored;
        ForgeCore.GameEvents.MasterMigrated -= HostMigrated;
        _subscribed = false;
    }
    private static void FactoryFinished()
    {
        RuntimeDiagnostics.Safe(RuntimeDiagnostics.StructureFinished);
        Fire(CaptureReason.LevelGenerated);
        // The layout and the process's patch list are read once at this boundary. Both walk far more than a frame
        // may spend, and neither changes until the next level is built.
        CaptureRegistry.SnapshotLayout(CaptureReason.LevelGenerated);
        RecProcessInventory.Refresh("level-generated");
    }

    [HarmonyPatch(typeof(Dimension), nameof(Dimension.NotifyWarpableObjects))]
    internal static class DimensionWarped
    {
        [HarmonyPostfix]
        private static void Postfix() => Fire(CaptureReason.DimensionChanged);
    }

    [HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnPlayerJoinedSessionHub))]
    internal static class PlayerJoined
    {
        [HarmonyPostfix]
        private static void Postfix() => Fire(CaptureReason.PlayerJoined);
    }

    [HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnPlayerLeftSessionHub))]
    internal static class PlayerLeft
    {
        [HarmonyPostfix]
        private static void Postfix() => Fire(CaptureReason.PlayerLeft);
    }

    /// <summary>Subscribed by <see cref="DevelopmentGameHooks"/> to ForgeCore's expedition end.</summary>
    internal static class LevelEnded
    {
        internal static void Ended() => Fire(CaptureReason.LevelEnded);
    }
}


