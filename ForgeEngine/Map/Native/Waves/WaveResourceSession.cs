using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using HarmonyLib;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Registers the allocated EventID before SurvivalWave.OnSpawn publishes its first fact.</summary>
internal sealed class WaveResourceSession : IDisposable
{
    private static readonly PublishedTextCodes ReadCodes = new("wave-resource-path", "wave-resource-unreadable", "wave-resource-file-size",
        "wave-resource-json", "wave-resource-utf8");
    internal static WaveResourceSession? Current { get; private set; }
    internal WaveResourceRegistry Registry { get; private set; } = new();
    /// <summary>The run and member whose native start is running, pinned for exactly that one native request.</summary>
    internal (WaveResourceRegistry.Run Run, int Member)? Beginning { get; set; }
    internal NativeWaveLifetime? StartingNativeWave { get; set; }
    internal long WorldEpoch => _runtime.WorldEpoch;
    private readonly IForgeEngine _runtime;
    private bool _disposed;
    // Host reference frames and Forge-owned runs are facts of one world; both are dropped when the epoch moves.
    private readonly Dictionary<WaveHostProgram, WaveHostFrame> _hostFrames = new(ReferenceEqualityComparer.Instance);
    private readonly List<NativeWaveLifetime> _owned = new();
    private long _frameEpoch = long.MinValue;
    private RuntimeLifecycleSubscription? _lifecycle;
    private DomainStateChannel.NativeStateSource? _relayState;
    private readonly Func<double> _now;
    // Entries a restored checkpoint owes a start, waiting for the world to be ready (and, for a non-scan host, for its reference frame).
    private readonly List<(WaveRelayStart Start, double Deadline)> _owed = new();
    private bool _relayPending;
    private long _restoredWorld;
    /// <summary>The run each published handle names, by list entry. A restored start of the same entry takes over that run, so the
    /// handle an author's behavior kept before the checkpoint still stops it.</summary>
    internal Dictionary<string, NativeWaveLifetime> Handled { get; } = new(StringComparer.Ordinal);
    /// <summary>A checkpoint restore has entries it still has to start: handles of the lost timeline are kept until it did.</summary>
    internal bool RestorePending => _relayPending || _owed.Count > 0;
    internal const double RelayReferenceWaitSeconds = 30;
    /// <summary>The wave clock the relay record and its guards are timed on.</summary>
    internal double Now() => _now();
    /// <summary>What each host list entry had reached (started, exhausted, cleared); stored with the vanilla checkpoint.</summary>
    internal WaveRelayLedger Relay { get; } = new();
    private WaveResourceSession(IForgeEngine runtime, Func<double> now) { _runtime = runtime; _now = now; }

    internal static WaveResourceSession Start(IForgeEngine runtime, RuntimeModuleHandle registration, Action<string> report,
        WaveResourceRegistry? prepared = null, Func<double>? now = null)
    {
        var session = new WaveResourceSession(runtime, now ?? (() => Time.time));
        if (prepared != null) session.AdmitRegistry(prepared);
        else foreach (var file in ContentPackages.Read(Paths.BepInExRootPath, ContentPackages.EngineReader, "forge", "wave-resources.forge", ReadCodes, report))
        {
            if (!PublishedResources.TryRead(file, PublishedResources.MaximumFileBytes, ReadCodes, report, out var json)) continue;
            try { session.Registry.AddDocument(json!); }
            catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
            {
                report(PublishedResources.Diagnostic(file.Relative, "wave-resource-invalid", error.Message));
                session.Record(null, 0, "wave-resource-invalid", file.Relative, error.Message);
            }
        }
        session._relayState = DomainStateChannel.RegisterNativeState(runtime, WaveRelayLedger.StateKey,
            () => session.Relay.Capture(session._now()), session.RestoreRelay);
        session._lifecycle = registration.ObserveLifecycle(value =>
        {
            if (value.IsMigration) { session.Registry.FollowEpoch(runtime.WorldEpoch); return; }
            if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored)
                session.Registry.ChangeEpoch(runtime.WorldEpoch);
            // A new world forgets the record; a checkpoint restore has just put the stored one back.
            // The restored record is carried into the one world change that follows its restore; a later world
            // (the level entered again before the owed starts were planned) starts from an empty record.
            if (value.Kind == RuntimeLifecycleKind.WorldChanged
                && !(session._relayPending && runtime.WorldEpoch == session._restoredWorld + 1))
            { session.Relay.Clear(); session._owed.Clear(); session.Handled.Clear(); session._relayPending = false; }
        }, replayCurrent: false);
        Current = session;
        return session;
    }

    /// <summary>Publishes the registry already built during package preparation. Registered services retain
    /// this session and read its current registry rather than closing over the empty startup registry.</summary>
    internal void AdmitRegistry(WaveResourceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
        Beginning = null;
        StartingNativeWave = null;
        _hostFrames.Clear(); _owned.Clear(); Handled.Clear();
        Relay.Clear(); _owed.Clear(); _relayPending = false;
    }

    private void RecordStarted(string address)
    {
        if (!Relay.Record(address, WaveStage.Started, _now())) { Record(Registry.Resolve(address), 0, "wave-relay-full"); return; }
        _relayState?.Changed();
    }

    /// <summary>Enemy reports a wave's exhausted and cleared facts here (the started fact is this session's own registration).</summary>
    internal void ReportStage(long epoch, string address, int stage)
    {
        if (_disposed || epoch != _runtime.WorldEpoch || stage is not (2 or 3)) return;
        if (!Relay.Record(address, (WaveStage)stage, _now())) { Record(Registry.Resolve(address), 0, "wave-relay-full"); return; }
        _relayState?.Changed();
    }

    private void RestoreRelay(System.Text.Json.JsonElement data)
    {
        if (_disposed) return;
        Relay.Restore(data, _now(), address => Registry.Resolve(address)?.After != null);
        _owed.Clear();
        _relayPending = true;
        _restoredWorld = _runtime.WorldEpoch;
    }

    /// <summary>Called from the in-level frame. After a checkpoint restore, starts the later list entries the stored record owes:
    /// the earlier entry had reached its anchor and the entry had not started. Each goes through the same native wave start
    /// as <c>wave_start</c>, with the delay that was left; an entry whose host reference is not known yet is retried for a while.</summary>
    internal void RelayFrame()
    {
        if (_disposed) return;
        // A client only keeps the record the host's increments carry (a migrated host resumes from it); it never plans or starts a wave.
        if (!ForgeCore.Session.IsHost) { _relayPending = false; _owed.Clear(); return; }
        if (_relayPending)
        {
            if (Mastermind.Current is not { WasCollected: false } || LevelIdentity.Read()?.ToString() is not { } level) return;
            _relayPending = false;
            var now = _now();
            foreach (var start in Relay.Plan(Registry.Hosts.Where(host => host.Level == level), now)) _owed.Add((start, now + RelayReferenceWaitSeconds));
        }
        for (int index = 0; index < _owed.Count;)
        {
            var (start, deadline) = _owed[index];
            string? refusal = AlarmWaveActions.StartRestored(this, start.Resource, (float)start.DelaySeconds);
            if (refusal == ResultCode.WaveHostReferenceUnavailable && _now() < deadline) { index++; continue; }
            _owed.RemoveAt(index);
            if (refusal == null) RecordResumed(start);
            else Record(start.Resource, 0, refusal);
        }
    }

    private void RecordResumed(WaveRelayStart start)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Write(RuntimeLogLevel.Info, WaveLogCodes.RelayResumed, new RuntimeLogFacts
        {
            Binding = AlarmWaveContract.Binding(Blocks.ActionMapWaveStart.Id),
            Values = new[] { RuntimeLogValue.Of("address", MapLog.SafeDetail(start.Resource.Address)),
                RuntimeLogValue.Of("delaySeconds", start.DelaySeconds),
                RuntimeLogValue.Of("after", start.Resource.After is { } after ? after.When : ""),
                RuntimeLogValue.Of("afterOrdinal", (long)(start.Resource.After?.Ordinal ?? -1)) }
        });
    }

    /// <summary>Cancels one wave of a world epoch (Enemy calls it when the game stops the wave).</summary>
    internal void Cancel(long epoch, ushort eventId)
    {
        if (!_disposed) Registry.Cancel(epoch, eventId);
    }

    /// <summary>A spawned wave's identity: its resource address, instance, enemies still pending and whether it was cancelled.</summary>
    internal (string Address, long Instance, int Pending, bool Cancelled)? ReadIdentity(long epoch, ushort eventId)
        => !_disposed && epoch == _runtime.WorldEpoch ? Registry.ReadIdentity(epoch, eventId) : null;
    internal (string Address, long Instance, int Pending, bool Cancelled)? ReadInstanceIdentity(long epoch, long instance)
        => !_disposed && epoch == _runtime.WorldEpoch ? Registry.ReadInstanceIdentity(epoch, instance) : null;
    internal WaveResource? Resolve(string address) => Registry.Resolve(address, LevelIdentity.Read()?.ToString());

    /// <summary>The native Spawn prefix. A pinned Forge start registers its member; otherwise the exported
    /// origin of the native carrier is looked up, its host's reference frame is captured once per world, and the
    /// member joins the newest open run of that resource.</summary>
    internal bool Register(ushort settings, ushort population, ushort eventId, WaveHostFrame? frame = null)
    {
        EnsureFrameEpoch();
        if (Beginning is { } pinned && (StartingNativeWave?.AcceptsRegistration(settings, population)
            ?? MemberMatches(pinned.Run, pinned.Member, settings, population)))
        {
            bool accepted = Registry.RegisterRuntime(_runtime.WorldEpoch, pinned.Run, pinned.Member, eventId, out var refusal);
            if (accepted) { StartingNativeWave?.Registered(eventId); RecordStarted(pinned.Run.PinnedResource.Address); }
            Record(pinned.Run.PinnedResource, eventId, accepted ? "registered" : refusal, run: pinned.Run, member: pinned.Member);
            return accepted;
        }
        var level = LevelIdentity.Read()?.ToString();
        var resource = Registry.NativeResource(level, settings, population);
        // The carrier's own request is the host's reference even when its readiness keeps the carrier itself
        // from running: later Forge starts of the same host spawn from where the host itself would.
        if (resource?.Host is { } host && frame is { } captured && !_hostFrames.ContainsKey(host))
        {
            _hostFrames.Add(host, captured);
            RecordHostFrame(resource, eventId, captured);
        }
        if (!Registry.Register(_runtime.WorldEpoch, level, settings, population, eventId, out var reason, out var run))
        {
            // Vanilla waves outside the exported registry keep their normal path. Authored but refused origins never run a carrier block.
            if (reason == "wave-origin-unregistered") return true;
            Record(resource, eventId, reason);
            return false;
        }
        Record(resource, eventId, "registered", run: run, member: MemberOf(run!, eventId));
        RecordStarted(run!.PinnedResource.Address);
        return true;
    }

    private static bool MemberMatches(WaveResourceRegistry.Run run, int member, ushort settings, ushort population)
    {
        var members = run.PinnedResource.NativeMembers;
        return member >= 0 && member < members.Count && members[member].SettingsId == settings
            && members[member].PopulationId == population;
    }

    private static int MemberOf(WaveResourceRegistry.Run run, ushort eventId)
    {
        for (int index = 0; index < run.Members; index++) if (run.EventOf(index) == eventId) return index;
        return -1;
    }

    /// <summary>The reference frame the host's first native wave of this world was requested with.</summary>
    internal WaveHostFrame? HostFrame(WaveHostProgram host)
    {
        EnsureFrameEpoch();
        return _hostFrames.TryGetValue(host, out var frame) ? frame : null;
    }

    /// <summary>A published Forge start. Ended owners are pruned here, so the list holds only runs that can still stop.</summary>
    internal void Own(NativeWaveLifetime lifetime)
    {
        EnsureFrameEpoch();
        for (int index = _owned.Count - 1; index >= 0; index--)
            if (!Alive(_owned[index])) _owned.RemoveAt(index);
        _owned.Add(lifetime);
    }

    private bool Alive(NativeWaveLifetime lifetime)
    {
        try { return lifetime.Epoch == _runtime.WorldEpoch && lifetime.IsLive(); }
        catch (Exception error) { MapLog.Contained("alarm-wave.owned-read", error); return true; }
    }

    /// <summary>The puzzle's own block turns its waves off on completion. Every Forge-started run hosted by this
    /// puzzle (whole alarm or one of its scans) that is still live is cancelled through the original StopEvent of
    /// each member. Cancellation is not a clear, so no relay that waits for exhausted or cleared starts after it.
    /// Called after the native state callback returned.</summary>
    internal int StopCompletedHost(uint puzzleId, string code)
    {
        EnsureFrameEpoch();
        int stopped = 0, failed = 0;
        for (int index = _owned.Count - 1; index >= 0; index--)
        {
            var lifetime = _owned[index];
            if (lifetime.Resource.Host is not { } host || !IsPuzzleHost(host, puzzleId)) continue;
            _owned.RemoveAt(index);
            if (!Alive(lifetime)) continue;
            try { lifetime.Cancel(code); stopped++; }
            catch (Exception error) { failed++; MapLog.Contained("alarm-wave.host-stop", error); }
        }
        if (stopped + failed > 0 && MapLog.Logs(failed > 0 ? RuntimeLogLevel.Error : RuntimeLogLevel.Info))
            MapLog.Write(failed > 0 ? RuntimeLogLevel.Error : RuntimeLogLevel.Info, WaveLogCodes.HostStopped, new RuntimeLogFacts
            {
                Binding = AlarmWaveContract.Binding(Blocks.ActionMapWaveStop.Id),
                Values = new[] { RuntimeLogValue.Of("reason", code), RuntimeLogValue.Of("hostId", (long)puzzleId),
                    RuntimeLogValue.Of("stopped", (long)stopped), RuntimeLogValue.Of("failed", (long)failed) }
            });
        return stopped;
    }

    internal static bool IsPuzzleHost(WaveHostProgram host, uint puzzleId)
        => host.Host.TryGetProperty("kind", out var kind) && kind.GetString() is "scan" or "chained-puzzle"
            && host.Host.TryGetProperty("id", out var id) && id.TryGetUInt32(out var value) && value == puzzleId;

    private void EnsureFrameEpoch()
    {
        if (_frameEpoch == _runtime.WorldEpoch) return;
        _hostFrames.Clear(); _owned.Clear();
        _frameEpoch = _runtime.WorldEpoch;
    }

    private void RecordHostFrame(WaveResource resource, ushort eventId, WaveHostFrame frame)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        var host = resource.Host!;
        MapLog.Write(RuntimeLogLevel.Info, WaveLogCodes.HostReference, new RuntimeLogFacts
        {
            Binding = AlarmWaveContract.Binding(Blocks.ActionMapWaveStart.Id),
            EventId = eventId == 0 ? null : "native-wave/" + eventId,
            Values = new[] { RuntimeLogValue.Of("address", MapLog.SafeDetail(resource.Address)),
                RuntimeLogValue.Of("hostKind", host.Host.GetProperty("kind").GetString() ?? ""),
                RuntimeLogValue.Of("hostId", (long)host.Host.GetProperty("id").GetUInt32()),
                RuntimeLogValue.Of("spawnType", (long)frame.SpawnType), RuntimeLogValue.Of("areaDistance", frame.AreaDistance),
                RuntimeLogValue.Of("node", frame.Node == null ? "" : frame.Node.Pointer.ToString()),
                RuntimeLogValue.Of("filter", MapLog.SafeDetail(frame.WorldEventFilter)) }
        });
    }

    internal void Record(WaveResource? resource, ushort eventId, string reason, string? path = null, string? detail = null,
        WaveResourceRegistry.Run? run = null, int member = -1)
    {
        bool refused = reason != "registered";
        var logLevel = refused ? RuntimeLogLevel.Error : RuntimeLogLevel.Info;
        if (!MapLog.Logs(logLevel)) return;
        var host = resource == null ? null : Registry.HostOf(resource);
        var hostText = host?.Host.GetRawText() ?? "";
        MapLog.Write(logLevel, refused ? WaveLogCodes.Refused : WaveLogCodes.Registered, new RuntimeLogFacts
        {
            Binding = AlarmWaveContract.Binding(Blocks.ActionMapWaveStart.Id),
            EventId = eventId == 0 ? null : "native-wave/" + eventId,
            Detail = detail == null ? null : MapLog.SafeDetail(detail),
            Values = new[] { RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Quote("path", path ?? ""),
                RuntimeLogValue.Of("address", MapLog.SafeDetail(resource?.Address ?? "")),
                RuntimeLogValue.Of("addressHash", Digest(resource?.Address ?? "")),
                RuntimeLogValue.Quote("host", hostText), RuntimeLogValue.Of("hostHash", Digest(hostText)),
                RuntimeLogValue.Of("hostKind", host?.Host.GetProperty("kind").GetString() ?? ""),
                RuntimeLogValue.Quote("hostType", host?.Host.GetProperty("type").GetString() ?? ""),
                RuntimeLogValue.Of("hostId", host?.Host.GetProperty("id").GetUInt32() is { } id ? (long)id : -1),
                RuntimeLogValue.Of("eventId", (long)eventId),
                RuntimeLogValue.Of("position", (long)(resource?.Position ?? -1)),
                RuntimeLogValue.Of("revision", resource?.Revision ?? ""),
                RuntimeLogValue.Of("readiness", host?.Readiness ?? "native"),
                RuntimeLogValue.Of("scanIndex", host?.Host.TryGetProperty("scanIndex", out var index) == true ? index.GetInt32() : -1),
                // Whole-wave membership: which member this event is and how
                // many members of its run are still unregistered.
                RuntimeLogValue.Of("member", (long)member),
                RuntimeLogValue.Of("pending", (long)(run?.Pending ?? -1)) }
        });
    }

    private static string Digest(string text)
    {
        if (text.Length == 0) return "";
        var hash = ForgeCore.Hash.Sha256Hex(text);
        return string.Join("-", Enumerable.Range(0, 8).Select(index => hash.Substring(index * 8, 8)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose(); _lifecycle = null;
        _relayState?.Dispose(); _relayState = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }
}

/// <summary>The complete native request a host's wave was started with, minus its delay and data blocks: where
/// Forge starts a later wave of the same host from.</summary>
internal sealed record WaveHostFrame(AIG_CourseNode? Node, SurvivalWaveSpawnType SpawnType, float AreaDistance,
    Vector3 Position, bool CustomDirection, Vector3 Direction, string WorldEventFilter, uint SettingsId, uint PopulationId)
{
    /// <summary>The caller's frame of the TriggerSurvivalWave request in progress, captured before an anchor rewrite
    /// and consumed by the Spawn prefix of that same request.</summary>
    [ThreadStatic] internal static WaveHostFrame? Caller;
}

[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.Spawn), new Type[] { typeof(float), typeof(float), typeof(SurvivalWaveSpawnType),
    typeof(AIG_CourseNode), typeof(Vector3), typeof(ushort), typeof(ushort), typeof(ushort), typeof(bool), typeof(bool), typeof(Vector3), typeof(string) })]
internal static class SurvivalWaveResourceRegistration
{
    /// <summary>Registers the wave: the request frame is captured for the host reference, then the event is registered,
    /// and a refused authored wave does not spawn (the native Spawn is skipped).
    /// The caller's frame from before any authored anchor rewrite is preferred, so a later wave of the same host
    /// spawns from the host's own reference rather than from another wave's anchor.</summary>
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(float areaDistance, SurvivalWaveSpawnType spawnType, AIG_CourseNode courseNode,
        Vector3 position, ushort settingsID, ushort populationDataID, ushort eventID, bool customSpawnDirection,
        Vector3 spawnDirection, string overrideWorldEventFilter)
    {
        var caller = WaveHostFrame.Caller;
        WaveHostFrame.Caller = null;
        if (WaveResourceSession.Current is not { } session) return true;
        var frame = caller != null && caller.SettingsId == settingsID && caller.PopulationId == populationDataID
            ? caller
            : new WaveHostFrame(courseNode, spawnType, areaDistance, position, customSpawnDirection, spawnDirection,
                overrideWorldEventFilter ?? "", settingsID, populationDataID);
        return session.Register(settingsID, populationDataID, eventID, frame);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Normal)]
    internal static void Postfix(ushort eventID)
        => WaveResourceSession.Current?.StartingNativeWave?.ObserveCreated(eventID);
}
