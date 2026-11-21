using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeDevelopment.Native;

internal sealed record ObservationAdmission(string RequestId, string Status);

internal static partial class CaptureRegistry
{
    private static IReadOnlyDictionary<string, RecObservationSpec> _observationSpecs = new Dictionary<string, RecObservationSpec>();
    private static readonly Dictionary<string, TargetBinding> ArmedObservations = new(StringComparer.Ordinal);
    private static long _observationGeneration, _observationFrame, _observationTick;
    private static int _observationThread;
    private static RecFrozenContext? _observationEnvelope;
    // The frame-start context, kept as its parts. The record is built only when an observation is admitted, so a
    // frame that admits none allocates nothing for it.
    private static string _contextSession = "";
    private static long _contextEpoch = -1;
    private static double _contextMilliseconds;
    private static bool _contextActive;
    private static RecReadContext ObservationContext
        => new(_observationThread, _contextSession, _contextEpoch, _observationGeneration, _observationFrame, _contextMilliseconds, _contextActive);
    internal static string LastObservation { get; private set; } = "no observation requested";

    private static void StartObservations()
    {
        _observationGeneration++; _observationThread = Environment.CurrentManagedThreadId;
        SnapshotPass.Active = true;
        RefreshObservationContext(Now());
        CaptureFieldReadbackNative.Prepare();
        CapturePlayerMembership.Start();
        var directory = Path.GetDirectoryName(typeof(CaptureRegistry).Assembly.Location)
            ?? throw new InvalidOperationException("observation package directory unavailable");
        try
        {
            _observationSpecs = RecObservationSpec.Parse(File.ReadAllText(Path.Combine(directory, "probes", "capture-readbacks.forge")));
            RecTracerRuntime.ObservationReturned = ObserveTrace;
            LastObservation = "observation readers ready: " + _observationSpecs.Count;
        }
        catch (Exception error)
        {
            _observationSpecs = new Dictionary<string, RecObservationSpec>();
            LastObservation = "observation catalog rejected: " + error.GetType().Name + ": " + error.Message;
            RecSession.Note("observation_catalog_rejected", json => json.WriteString("reason", LastObservation));
        }
    }

    private static void StopObservations()
    {
        RecTracerRuntime.ObservationReturned = null;
        CapturePlayerMembership.Stop();
        ArmedObservations.Clear(); _observationGeneration++;
        _contextActive = false;
        SnapshotPass.Active = false;
        // The existing CaptureRegistry.Stop subsequently drains each request's terminal callback before RecSession.Stop.
    }

    private static void RefreshObservationContext(long tick)
    {
        _observationFrame = RecTime.Frame; _observationTick = tick;
        _contextSession = RecSession.SessionId; _contextEpoch = RecTime.WorldEpoch;
        _contextMilliseconds = SnapshotPass.Now; _contextActive = Active;
        if (ArmedObservations.Count != 0 || _observationEnvelope == null
            || _observationEnvelope.Session != RecSession.SessionId)
            _observationEnvelope = RecSession.FreezeContext();
    }

    private static RecReadContext ReadObservationContext(string requestId)
        => new(_observationThread, RecSession.SessionId, RecTime.WorldEpoch, _observationGeneration,
            _observationFrame, SnapshotPass.Now, Active && SnapshotPass.IsPending(requestId), !Active ? "disabled" : "cancelled");

    internal static ObservationAdmission Observe(string observationId, TargetBinding target)
    {
        // Manual commands run outside hooks and freeze their exact source context before admission.
        if (Environment.CurrentManagedThreadId != _observationThread) return QueueObservation(observationId, target, null);
        RefreshObservationContext(Now()); _observationEnvelope = RecSession.FreezeContext();
        return QueueObservation(observationId, target, null);
    }

    internal static string ArmObservation(string observationId, TargetBinding target)
    {
        if (!Active) return "disabled";
        if (Environment.CurrentManagedThreadId != _observationThread || !_observationSpecs.TryGetValue(observationId, out var spec)
            || spec.Trigger != "after-returned" || !RecUnityProjectionAdapter.SupportsBinding(target)) return "invalid";
        ArmedObservations[observationId] = target;
        RefreshObservationContext(Now());
        return "armed";
    }

    private static void ObserveTrace(RecTraceInvocation invocation)
    {
        // Only managed lookups and admission. No instance, args, enumerator, native context or getter enters this path.
        foreach (var spec in _observationSpecs.Values)
            if (spec.Trigger == "after-returned" && spec.Profile == invocation.Profile && spec.Entry == invocation.EntryId
                && ArmedObservations.TryGetValue(spec.Id, out var target)) QueueObservation(spec.Id, target, invocation);
    }

    private static ObservationAdmission QueueObservation(string observationId, TargetBinding target, RecTraceInvocation? invocation)
    {
        var requestId = Guid.NewGuid().ToString("N");
        if (!_observationSpecs.TryGetValue(observationId, out var spec)) return new(requestId, "invalid");
        var context = ObservationContext;
        var envelope = _observationEnvelope;
        if (envelope == null) return new(requestId, "disabled");
        var ticket = new ObservationTicket(requestId, context.Session, spec.Id, spec.Hash, context.WorldEpoch,
            context.Generation, invocation?.EntryId ?? "", invocation?.InstallationId ?? "", invocation?.Signature ?? "",
            invocation?.InvocationId ?? 0, context.Frame, _observationTick, context.Frame + 120, SnapshotPass.Now + 5000, target);
        var snapshot = new CaptureSnapshot("observation", "observation/" + spec.Id, _observationTick)
        { WorldEpoch = context.WorldEpoch, StartedTick = _observationTick, Observation = ticket };
        var stream = new CaptureSnapshotStream(snapshot, () => _observationTick, envelope);
        IEnumerable<int> Complete(CaptureCompletion result) => CompleteSnapshot(snapshot, stream, result);
        void Completed(CaptureCompletion result) => SnapshotCompleted(snapshot, result);
        if (Environment.CurrentManagedThreadId != _observationThread || !RecUnityProjectionAdapter.SupportsBinding(target)
            || invocation == null && spec.Trigger != "manual" || invocation != null && spec.Trigger != "after-returned")
        {
            var result = new CaptureCompletion(requestId, "invalid", 0, 0, 0, 0, 0);
            using (var receipt = Complete(result).GetEnumerator()) receipt.MoveNext();
            Completed(result);
            return new(requestId, "invalid");
        }
        var admission = SnapshotPass.Enqueue("observation/" + spec.Id, ticket.SourceWorldEpoch, () =>
        {
            var adapter = new RecUnityProjectionAdapter(spec.Fields, spec.Hierarchy, spec.Materials, spec.ShaderProperties);
            return stream.Collect(new RecProjectionCursor(adapter, new ScopedReadLease(ticket, () => ReadObservationContext(ticket.RequestId),
                RecTracerRuntime.IsObservationInstallationCurrent, () => SnapshotPass.CanReadField), snapshot, RecUnityProjectionAdapter.RootOperation,
                spec.MaximumRows, spec.MaximumDepth, spec.MaximumSteps, spec.MaximumBytes)).GetEnumerator();
        }, Completed, ticket, result => Complete(result).GetEnumerator());
        return new(requestId, admission == CaptureEnqueueResult.Accepted ? "accepted" : admission == CaptureEnqueueResult.QueueFull
            ? "queue-full" : admission == CaptureEnqueueResult.Inactive ? "disabled" : "invalid");
    }

    internal static void ObserveLocalModel()
    {
        if (!Active) { LastObservation = "disabled"; return; }
        var player = ForgeCore.Players.Local;
        var life = NativeLife.Of(player);
        if (life == null) { LastObservation = "target-unresolved: local player owner has no current life"; return; }
        var receipt = Observe("model-structure", new(RecUnityProjectionAdapter.Owner, RecUnityProjectionAdapter.PlayerToken + life.Pointer, life.Instance, life.WorldEpoch));
        LastObservation = receipt.RequestId + ": " + receipt.Status;
    }

    /// <summary>The terminal receipt of an observation snapshot: its timing, its terminal status
    /// (a successful pass over incomplete fields or coverage is an error) and the stream's final write.</summary>
    private static IEnumerable<int> CompleteSnapshot(CaptureSnapshot snapshot, CaptureSnapshotStream stream, CaptureCompletion result)
    {
        snapshot.CompletedTick = _observationTick;
        snapshot.MainThreadMilliseconds = result.TotalMilliseconds; snapshot.PeakSliceMilliseconds = result.PeakMilliseconds;
        snapshot.AllocatedBytes = result.AllocatedBytes; snapshot.CaptureFrames = result.Frames;
        snapshot.TerminalStatus = result.Succeeded && (snapshot.FieldCompleteness != "complete"
            || snapshot.CoverageCompleteness != "complete") ? "error" : result.Status;
        snapshot.AcquisitionComplete = snapshot.TerminalStatus == "completed";
        if (!snapshot.AcquisitionComplete)
        {
            if (!result.Succeeded) snapshot.FieldCompleteness = snapshot.CoverageCompleteness = "incomplete";
            snapshot.Skipped.Add(snapshot.TerminalStatus + (result.Error.Length == 0 ? "" : ":" + result.Error));
        }
        foreach (var step in stream.Complete()) yield return step;
    }

    private static void SnapshotCompleted(CaptureSnapshot snapshot, CaptureCompletion result)
    {
        if (!result.Succeeded) snapshot.TerminalStatus = result.Status;
        LastObservation = result.RequestId + ": " + snapshot.TerminalStatus;
    }
}
