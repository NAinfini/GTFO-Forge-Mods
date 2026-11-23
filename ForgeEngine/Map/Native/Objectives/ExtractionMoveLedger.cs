using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;
using ChainedPuzzles;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// `forge.action.map.extraction_move`: the level's exit scan moved to a new point while the level runs. The game has no
/// entry that moves a built scan, so this is the minimal domain adapter: each machine moves its own scan object, its
/// course-node registration, its nav marker target and the objective's zone text. The record of what moved travels the
/// Runtime's native-state path, which is what carries it to clients, a late joiner, a checkpoint restore and a host
/// migration; a level no plan moved has an empty record and this class touches nothing.
///
/// The endpoint's address stays the zone it was built in, because the address is its identity (`m_exitArea` and the
/// landing's own area are untouched). What is moved is the scan core; the native win condition keeps reading that same
/// core and puzzle. The native exit wave does not read the core: the scan's progress callback hands
/// `WardenObjectiveManager.OnExitScanProgressUpdate` the course node the endpoint was built in (its `SpawnNode`), which
/// `ExtractionWaveNode` swaps for the scan's new node before the native body starts the wave from it. Every machine
/// swaps it (the record is on every machine); the wave itself is spawned by the host's Mastermind, so the host decides.
///
/// Refusals, decided on the host before anything moves: a scan players have started or finished is not moved
/// (`extraction-scan-running`, `extraction-scan-finished`): moving it would drop their progress or move a scan that
/// already counted. An exit puzzle that is not one plain bioscan is refused (`extraction-scan-unsupported`), as is a
/// point with no course node in the endpoint's own dimension (`extraction-position-off-level`).
///
/// A checkpoint recall keeps built objects, so a restore whose record no longer names an endpoint puts that endpoint
/// back where this machine first found it.
/// </summary>
internal sealed class ExtractionMoveLedger : IDisposable
{
    internal const string StateKey = "forge.map.extraction-move";
    private const int MaximumEntries = 8;
    /// <summary>How far from the point the course-node search reaches: an entity's position can sit just off the navmesh.</summary>
    private const float NodeSampleMetres = 2f;

    private const string PendingCode = "extraction-move-endpoint-unresolved";

    internal sealed record Entry(string Address, float X, float Y, float Z, int Revision);

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;
    private readonly DomainStateChannel.NativeStateSource _state;
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, int> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector3> _originals = new(StringComparer.Ordinal);
    /// <summary>By endpoint address: the node the game hands the exit wave (where the endpoint was built) and the node the scan moved to.</summary>
    private readonly Dictionary<string, (IntPtr Built, AIGraph.AIG_CourseNode Moved)> _waves = new(StringComparer.Ordinal);
    // Endpoints a restored record dropped, gathered before they are put back so the table is not changed while read.
    private readonly List<string> _dropped = new();
    // Frames an unbuilt endpoint waits before the next attempt (the zone-light ledger's pace); a restore resets it.
    private const int RetryFrames = 15;
    private int _revision, _retryIn;
    private bool _pending, _unresolvedReported, _disposed;

    internal ExtractionMoveLedger(IForgeEngine kernel, Func<bool> canExecute, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _state = DomainStateChannel.RegisterNativeState(kernel, StateKey, Capture, Restore);
    }

    internal CommandResult Handle(CommandContext context)
    {
        if (!_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!context.TryEntity(ObjectiveActionContract.MoveExtraction, out _))
            return Outcome.Rejected(ResultCode.ExtractionTargetMissing);
        if (!GameVectors.TryUnity(context.Vector(ObjectiveActionContract.MovePosition), out var position))
            return Outcome.Rejected(ResultCode.ExtractionPositionRequired);
        var request = new MoveRequest(this, position);
        return context.EachTarget(ref request, MoveStep, report: _reportException ??= ReportException,
            countColumn: Blocks.ActionMapExtractionMove.RowTargetCount);
    }

    /// <summary>What the endpoint's step needs from the request.</summary>
    private readonly record struct MoveRequest(ExtractionMoveLedger Self, Vector3 Position);

    private static readonly TargetStep<MoveRequest> MoveStep = Move;
    private Action<EntityReference, Exception>? _reportException;

    private void ReportException(EntityReference target, Exception error)
        => Report("map.extraction-move-exception: " + error.GetType().Name + ": " + error.Message);

    /// <summary>The one endpoint: checked, moved on this machine and recorded for the others.</summary>
    private static void Move(ref MoveRequest request, EntityReference target, int index, RowWriter rows)
    {
        var self = request.Self;
        var position = request.Position;
        var endpoint = ExtractionObservation.Resolve(target, target.WorldEpoch);
        if (endpoint == null || ExtractionObservation.Address(endpoint) is not { } address)
        { rows.End(Status.Rejected, Commit.None, ResultCode.ExtractionTargetMissing); return; }
        if (Scan(endpoint) is not { } scan) { rows.End(Status.Rejected, Commit.None, ResultCode.ExtractionScanUnsupported); return; }
        var status = scan.State.status;
        if (status == eBioscanStatus.Finished) { rows.End(Status.Rejected, Commit.None, ResultCode.ExtractionScanFinished); return; }
        if (status is eBioscanStatus.Scanning or eBioscanStatus.TimedOut) { rows.End(Status.Rejected, Commit.None, ResultCode.ExtractionScanRunning); return; }
        if (Node(address, position) == null) { rows.End(Status.Rejected, Commit.None, ResultCode.ExtractionPositionOffLevel); return; }
        if (!self.Apply(address, position)) { rows.End(Status.Failed, Commit.Unknown, ResultCode.ExtractionMoveUnconfirmed); return; }
        var entry = new Entry(address, position.x, position.y, position.z, ++self._revision);
        self._entries.RemoveAll(row => row.Address == address);
        self._entries.Add(entry);
        self._applied[address] = entry.Revision;
        self._state.Changed();
        if ((scan.transform.position - position).sqrMagnitude < 0.0001f) rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.ExtractionMoved);
        else rows.End(Status.Failed, Commit.Unknown, ResultCode.ExtractionMoveUnconfirmed);
    }

    /// <summary>Applies every record this machine has not applied, and undoes every move a restored record dropped.
    /// An endpoint not built yet stays pending, reported once and retried next frame.</summary>
    internal void Frame()
    {
        if (_disposed || !_pending) return;
        if (_retryIn > 0) { _retryIn--; return; }
        bool complete = true;
        _dropped.Clear();
        foreach (var original in _originals)
            if (!Recorded(original.Key)) _dropped.Add(original.Key);
        foreach (var address in _dropped)
        {
            if (Attempt(address, _originals[address])) { _originals.Remove(address); _applied.Remove(address); _waves.Remove(address); }
            else complete = false;
        }
        _dropped.Clear();
        for (var index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            if (_applied.TryGetValue(entry.Address, out var done) && done == entry.Revision) continue;
            if (Attempt(entry.Address, new Vector3(entry.X, entry.Y, entry.Z))) _applied[entry.Address] = entry.Revision;
            else complete = false;
        }
        _pending = !complete;
        if (!complete) _retryIn = RetryFrames;
        if (!complete && !_unresolvedReported) { _unresolvedReported = true; Report("map." + PendingCode); }
    }

    private bool Recorded(string address)
    {
        foreach (var row in _entries)
            if (row.Address == address) return true;
        return false;
    }

    /// <summary>The way back for a check: drops the record of the endpoint and puts its scan where this machine first found it.
    /// False when the endpoint was never moved or the scan could not be put back.</summary>
    internal bool Undo(string address)
    {
        if (_disposed || !_originals.TryGetValue(address, out var original)) return false;
        _entries.RemoveAll(row => row.Address == address);
        if (!Attempt(address, original)) return false;
        _originals.Remove(address); _applied.Remove(address); _waves.Remove(address);
        _state.Changed();
        return true;
    }

    /// <summary>The node the exit wave starts from: the scan's new node when `built` is the node of an endpoint whose scan was moved, else null.</summary>
    internal AIGraph.AIG_CourseNode? WaveNode(AIGraph.AIG_CourseNode? built)
    {
        if (_disposed || built == null || _waves.Count == 0) return null;
        foreach (var wave in _waves.Values)
            if (wave.Built == built.Pointer && !wave.Moved.WasCollected) return wave.Moved;
        return null;
    }

    private bool Attempt(string address, Vector3 position)
    {
        try { return Apply(address, position); }
        catch (Exception error)
        {
            Report("map.extraction-move-exception: " + error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    private bool Apply(string address, Vector3 position)
    {
        if (LevelEndpointAddress.Parse(address) is not { } parsed) return false;
        var endpoint = ExtractionObservation.Resolve(LevelEndpointAddress.Reference(parsed, _kernel.WorldEpoch), _kernel.WorldEpoch);
        if (endpoint == null || Scan(endpoint) is not { } scan || Node(parsed, position) is not { } node) return false;
        _originals.TryAdd(address, scan.transform.position);
        if (ExtractionObservation.BuiltNode(endpoint) is { } built) _waves[address] = (built.Pointer, node);
        scan.transform.position = position;
        // A disabled scan is not registered on a node: its own Activate registers it on `m_courseNode`.
        bool live = scan.State.status != eBioscanStatus.Disabled;
        if (live) scan.m_courseNode?.UnregisterBioscan(scan);
        scan.m_courseNode = node;
        if (live)
        {
            node.RegisterBioscan(scan);
            // The scanner's HUD reads the position when the scan starts.
            var scanner = scan.PlayerScanner?.TryCast<CP_PlayerScanner>();
            scanner?.StopScan();
            scanner?.StartScan();
        }
        Marker(endpoint)?.SetTrackingObject(scan.gameObject);
        ZoneText(node);
        return true;
    }

    /// <summary>The one movable shape: the endpoint's puzzle is a single plain bioscan.</summary>
    internal static CP_Bioscan_Core? Scan(Component endpoint)
    {
        var puzzle = endpoint switch
        {
            ElevatorShaftLanding landing => landing.m_puzzle,
            LG_LevelExitGeo exit => exit.m_puzzle,
            _ => null
        };
        if (puzzle == null || puzzle.WasCollected || puzzle.NRofPuzzles() != 1) return null;
        return puzzle.GetPuzzle(0)?.TryCast<CP_Bioscan_Core>();
    }

    private static NavMarker? Marker(Component endpoint) => endpoint switch
    {
        ElevatorShaftLanding landing => landing.m_marker,
        LG_LevelExitGeo exit => exit.m_marker,
        _ => null
    };

    private static AIGraph.AIG_CourseNode? Node(string address, Vector3 position)
        => LevelEndpointAddress.Parse(address) is { } parsed ? Node(parsed, position) : null;

    private static AIGraph.AIG_CourseNode? Node(MapObjectReference parsed, Vector3 position)
        => int.TryParse(parsed.Dimension, out var dimension)
            && ForgeCore.Level.CourseNodeIn(dimension, position, NodeSampleMetres, out var node) ? node : null;

    /// <summary>The objective's [EXTRACTION_ZONE] text names the zone the scan stands in now.</summary>
    private static void ZoneText(AIGraph.AIG_CourseNode node)
    {
        var zone = node.m_zone;
        if (zone == null) return;
        var text = zone.NavInfo.GetFormattedText(LG_NavInfoFormat.Full_And_Number_With_Space);
        foreach (var layer in new[] { LG_LayerType.MainLayer, LG_LayerType.SecondaryLayer, LG_LayerType.ThirdLayer })
        {
            if (!WardenObjectiveManager.HasWardenObjectiveDataForLayer(layer)) continue;
            WardenObjectiveManager.SetObjectiveTextFragment(layer, WardenObjectiveManager.GetCurrentChainIndex(layer), eWardenTextFragment.EXTRACTION_ZONE, text);
            WardenObjectiveManager.UpdateObjectiveGUIWithCurrentState(layer, false, false, true);
        }
    }

    private JsonElement Capture() => JsonSerializer.SerializeToElement(new { schemaVersion = FormatVersion.Current, revision = _revision, entries = _entries });

    private void Restore(JsonElement data)
    {
        if (_disposed) return;
        if (data.ValueKind != JsonValueKind.Object || data.GetProperty("schemaVersion").GetString() != FormatVersion.Current)
            throw new FormatException("extraction-move-version");
        var array = data.GetProperty("entries");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumEntries) throw new FormatException("extraction-move-entries");
        var entries = new List<Entry>();
        foreach (var item in array.EnumerateArray())
        {
            var entry = new Entry(item.GetProperty("Address").GetString() ?? "", item.GetProperty("X").GetSingle(), item.GetProperty("Y").GetSingle(),
                item.GetProperty("Z").GetSingle(), item.GetProperty("Revision").GetInt32());
            if (LevelEndpointAddress.Parse(entry.Address) == null || !float.IsFinite(entry.X) || !float.IsFinite(entry.Y) || !float.IsFinite(entry.Z))
                throw new FormatException("extraction-move-entry");
            entries.Add(entry);
        }
        _entries.Clear(); _entries.AddRange(entries);
        _revision = Math.Max(_revision, data.GetProperty("revision").GetInt32());
        // A recall may have rewound this machine's scan state, so every record is applied again.
        _applied.Clear();
        _pending = true; _unresolvedReported = false; _retryIn = 0;
        Frame();
    }

    /// <summary>A world change or a stopped runtime ends the record: the scans it names are gone.</summary>
    internal void Clear()
    {
        _entries.Clear(); _applied.Clear(); _originals.Clear(); _waves.Clear(); _revision = 0; _retryIn = 0; _pending = false; _unresolvedReported = false;
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("extraction-move.report", reportError); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _state.Dispose();
    }
}

/// <summary>The exit scan's progress callback hands the native body the node the endpoint was built in; a moved scan hands
/// it the scan's node instead, so the exit wave starts where the scan stands. A level no plan moved changes nothing.</summary>
[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.OnExitScanProgressUpdate))]
internal static class ExtractionWaveNode
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ref AIGraph.AIG_CourseNode sourceNode)
    {
        if (MapHost.Session?.ExtractionMoves?.WaveNode(sourceNode) is { } moved) sourceNode = moved;
    }
}
