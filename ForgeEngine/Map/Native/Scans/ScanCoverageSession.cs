using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChainedPuzzles;
using ForgeEngine.Framework;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic.List<Player.PlayerAgent>;

namespace ForgeMap.Native;

/// <summary>
/// The authored scan coverage of one level, applied once per level when the level's own generation finished, and
/// again for every instance that appears afterwards.
///
/// §4.2.9 fixes the key as the ChainedPuzzle block and not the host: the author clones the block into this
/// project's own block, so after generation every machine can read the block's own `persistentID` back off each
/// `ChainedPuzzleInstance.Data` and apply a row to every instance carrying it. That is what makes the level's own
/// instance list the one collection this half reads, and it is also why a route row is refused when its block id
/// is carried by more than one instance: the website's export already refuses that, and the runtime fails closed
/// rather than routing every copy of a shared block along one author's path.
///
/// Which half runs where is §4.2.9's own split. A `radius` row is applied on every machine, because the radius
/// and the ring are local presentation and the game replicates neither. A `room` or `zone` row's membership is a
/// host decision and only the host's counting entry publishes it; the host also enlarges that scan's own distance
/// gate so the counting entry keeps being called while its members stand anywhere in the owned rooms, while the
/// scan's ring is left at the preset size because the ring is not the member rule. A `route` row is applied on
/// every machine, because the game replicates only the interpolation phase and the pause flag — each peer computes
/// the scan's position from its own `ScanPositions`, so a route written on the host alone would put the ring in a
/// different place on every other machine. A `preset` coverage leaves the native circle intact while still applying
/// the row's independent player requirement, colour or movement inputs.
///
/// Nothing falls back. A row whose block, component, ring, room, zone or route point this level cannot answer is
/// refused with its own code and left out, and a row whose member set cannot be represented in the replicated
/// state — more than four players or more than eight required items — is refused whole rather than published
/// truncated. Every refusal is reported once per level, which is what the in-game check of a row reads.
/// </summary>
internal sealed class ScanCoverageSession : IDisposable
{
    /// <summary>The one live session, which is what the three Harmony callbacks answer through. A callback is
    /// static, so the session installs itself here before the first level finishes generating and clears itself
    /// when it is released; a callback that fires before it exists applies nothing rather than throwing through a
    /// patch.</summary>
    internal static ScanCoverageSession? Current { get; private set; }

    private readonly IReadOnlyList<ScanCoverageRow> _rows;
    private readonly Func<long> _worldEpoch;
    private readonly Func<MapLevelReference?> _level;
    private readonly Action<string> _report;
    // Every peer retains the row target, but only the machine that is master at a native scan beat installs or
    // temporarily remaps its speed input. This makes a promoted client authoritative without a second scheduler.
    private readonly Dictionary<IntPtr, ScanCoverageTarget> _authorityScanners = new();
    private readonly Dictionary<IntPtr, ScanCoverageTarget> _shapes = new();
    private readonly Dictionary<IntPtr, ScanOwnedInputs> _owned = new();
    private readonly Dictionary<IntPtr, (long Epoch, int Instance)> _graphicsReady = new();

    /// <summary>A host migration keeps the level and its scans: the coverage already applied stands under the new world number.</summary>
    internal void FollowWorld(long from, long to)
    {
        if (_appliedEpoch == from) _appliedEpoch = to;
        foreach (var entry in _graphicsReady.ToArray())
            if (entry.Value.Epoch == from) _graphicsReady[entry.Key] = (to, entry.Value.Instance);
    }
    private readonly Dictionary<IntPtr, (IntPtr Core, ScanGraphicsState State)> _graphicsTargets = new();
    private readonly Dictionary<(IntPtr Pointer, int Instance, long Epoch), int> _graphicsSetups = new();
    internal long GraphicsSetupEpoch => _worldEpoch();
    private readonly Dictionary<IntPtr, List<IntPtr>> _instanceCores = new();
    private ScanPlacementHosts? _placements;
    private IDisposable? _placementBatchDone;
    /// <summary>The membership plan of every room- or zone-covered scan core of this level, keyed by the core's
    /// own pointer. Every peer keeps it for a possible authority change; only the current host's counting entry
    /// publishes it. A core the authored rows do not name is left exactly as the preset made it.</summary>
    private readonly Dictionary<IntPtr, Plan> _plans = new();
    /// <summary>Every scan core this level's rows have already been written to. The level's own pass and the
    /// per-instance hook can both reach one instance, and a radius scaled or a speed multiplied twice would be a
    /// different scan than the author wrote, so a core is written once and never again inside one level.</summary>
    private readonly HashSet<IntPtr> _written = new();
    /// <summary>Where each authored round of an instance ended, for the next round's guide line.</summary>
    private readonly Dictionary<(IntPtr Instance, int Round), Vector3> _roundEnds = new();
    private readonly OnceReporter _once = new();
    private long _appliedEpoch = -1;
    private MapLevelReference? _appliedLevel;
    private bool _passRan;
    private bool _faulted;
    /// <summary>A callback failure stops this half for the level it happened in; the next world applies again.</summary>
    private readonly ForgeEngine.GameBindings.WorldFault _fault = new();
    private bool Down => _faulted || _fault.Holds(_worldEpoch());
    private bool _disposed;

    /// <summary>One covered core's row, member rule and authority gate, retained on every peer.</summary>
    private readonly record struct Plan(ScanCoverageRow Row, ScanMembership Membership, float GateRadius,
        float GateRadiusSquared);

    /// <summary>One resolved scan component of one instance: the core the row names and the scanner it counts
    /// with. A component that resolved to anything else never reaches this list.</summary>
    private readonly record struct Target(CP_Bioscan_Core Core, CP_PlayerScanner Scanner);

    private const string RadiusValueCode = "scan-coverage-radius";
    private const string TeamSpeedsValueCode = "scan-coverage-team-speeds";
    private const string RouteValueCode = "scan-coverage-route";

    private ScanCoverageSession(IReadOnlyList<ScanCoverageRow> rows, Func<long> worldEpoch,
        Func<MapLevelReference?> level, Action<string> report)
    {
        _rows = rows;
        _worldEpoch = worldEpoch;
        _level = level;
        _report = report;
    }

    /// <summary>Prepares scans from the package admission snapshot before the first native Rundown write.
    /// The activation callback selects only successfully published packages before Runtime becomes ready.</summary>
    internal static ScanCoverageSession Start(Func<long> worldEpoch, Func<MapLevelReference?> level,
        Action<string> report, MapPreparedContent prepared, out Action<MapPreparedContent> activate)
    {
        ArgumentNullException.ThrowIfNull(worldEpoch);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(prepared);
        // Do not orphan a prior session's failed withdrawal receipts during reload.
        if (Current is { } previous)
        {
            previous.Dispose();
            if (ReferenceEquals(Current, previous))
                throw new InvalidOperationException("The previous scan session still has pending ownership cleanup.");
        }
        var rows = new List<ScanCoverageRow>();
        var session = new ScanCoverageSession(rows.AsReadOnly(), worldEpoch, level, report);
        // The full set has passed package duplicate checks. Construct once before publication as well, so
        // the survivor subset cannot introduce a new refusal, conversion or filesystem read afterwards.
        _ = new ScanPlacementHosts(prepared.ScanPlacements,
            failure => throw new InvalidOperationException("Prepared scan placement invariant failed: " + failure));
        bool activated = false;
        activate = accepted =>
        {
            if (activated) throw new InvalidOperationException("Scan package activation is startup-only.");
            activated = true;
            var placements = new ScanPlacementHosts(accepted.ScanPlacements, report);
            rows.Clear();
            rows.AddRange(Load(accepted));
            session._placements = placements;
            if (accepted.ScanPlacements.Count != 0)
            {
                session._placementBatchDone = ForgeCore.FactoryEvents.OnBatchDone(session.PlacementBatchDone);
            }
        };
        Current = session;
        return session;
    }

    /// <summary>How many rows this install's documents gave, for the plugin's one load record.</summary>
    internal int Rows => _rows.Count;
    private void PlacementBatchDone(LG_Factory.BatchName batch)
    {
        if (_disposed || Down || (int)batch is not (0 or 45 or 46 or 47)) return;
        try
        {
            if (_level() is { } level) _placements?.BatchDone(LG_Factory.Current, batch, _worldEpoch(), level);
        }
        catch (Exception error)
        {
            MapLog.Contained("scan-coverage.placement-batch-done", error);
            try { _report("Map scan placement callback failed: " + error.GetType().Name + ": " + error.Message); }
            catch (Exception reportError) { MapLog.Contained("scan-coverage.placement-batch-done-report", reportError); }
        }
    }

    /// <summary>Whether this install authored any scan coverage at all. A session with nothing to apply answers
    /// false, and the hook that calls it then does no work.</summary>
    internal bool Pending => !_disposed && !Down && _rows.Count != 0;

    /// <summary>
    /// Applies this level's rows to every instance the level's own list already holds, once per level: a level is
    /// applied when its own identity is read and the world it belongs to is the one that asked. A second call for
    /// the same level in the same world applies nothing — a checkpoint restore reloads state rather than the
    /// level, and a radius scaled or a speed multiplied twice would be a different scan than the author wrote.
    /// </summary>
    internal void Apply()
    {
        try
        {
            if (!_disposed && !Down && _level() is { } placementLevel)
                _placements?.Validate(LG_Factory.Current, _worldEpoch(), placementLevel);
        }
        catch (Exception error)
        {
            MapLog.Contained("scan-coverage.placement-validate", error);
            try { _report("Map scan placement validation failed: " + error.GetType().Name + ": " + error.Message); }
            catch (Exception reportError) { MapLog.Contained("scan-coverage.placement-validate-report", reportError); }
        }
        if (!Pending) return;
        try
        {
            var epoch = _worldEpoch();
            if (epoch == 0) return;
            if (_level() is not { } level) return;
            if (_passRan && _appliedEpoch == epoch && _appliedLevel == level) return;
            _appliedEpoch = epoch;
            _appliedLevel = level;
            ReleaseAll();
            foreach (var entry in _graphicsReady.ToArray()) if (entry.Value.Epoch != epoch) _graphicsReady.Remove(entry.Key);
            _once.Reset();
            var rows = ScanCoverage.RowsForLevel(_rows, level);
            var applied = 0;
            var pending = 0;
            foreach (var row in rows)
            {
                var instances = ScanInstanceResolver.Instances(row.ChainedPuzzleId);
                if (instances.Count == 0 && LaterObjectiveScan(row.ChainedPuzzleId)) { pending++; continue; }
                if (ApplyRow(row, epoch, instances)) applied++;
            }
            _passRan = true;
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ScanCoverageLevel, "level=" + level + " applied=" + applied.ToString(CultureInfo.InvariantCulture) + "/"
                + rows.Count.ToString(CultureInfo.InvariantCulture) + " pending=" + pending.ToString(CultureInfo.InvariantCulture)
                + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                + (Session.IsHost ? " host" : " client"), RuntimeLogValue.Of("applied", (long)applied), RuntimeLogValue.Of("rows", (long)rows.Count),
                RuntimeLogValue.Of("host", Session.IsHost), RuntimeLogValue.Of("pending", (long)pending));
        }
        catch (Exception error) { Fault(error); }
    }

    private static bool LaterObjectiveScan(uint block)
    {
        // Chained objectives construct these instances when the corresponding native objective starts/exits.
        // Only a real level reference can wait; an unreferenced/missing instance still fails below.
        for (var layerIndex = 0; layerIndex < 3; layerIndex++)
        {
            var layer = (LG_LayerType)layerIndex;
            var count = WardenObjectiveManager.GetWardenObjectiveCountForLayer(layer);
            for (var chain = 0; chain < count; chain++)
                if (WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(layer, chain, out var data)
                    && data != null && (data.ChainedPuzzleToActive == block || data.ChainedPuzzleAtExit == block)) return true;
        }
        return false;
    }

    /// <summary>
    /// Applies this level's rows to one instance that appeared after the level's own pass ran — a host that sets
    /// its scan up on an event, or any other instance the factory did not build with the level. The hook that
    /// calls this fires for every instance the game builds, including the ones built during generation, so the
    /// pass's own work is never repeated here: before the pass has run an instance is the pass's, and afterwards
    /// the full instance set of the row's block id is re-read so a route row whose block id turned out to be
    /// shared is still refused instead of being routed onto the one instance that just appeared.
    /// </summary>
    internal void ApplyInstance(ChainedPuzzleInstance? instance)
    {
        if (!Pending || instance == null || instance.WasCollected) return;
        if (!_passRan) return;
        try
        {
            var epoch = _worldEpoch();
            if (epoch == 0 || epoch != _appliedEpoch) return;
            if (_level() is not { } level || _appliedLevel != level) return;
            var data = instance.Data;
            if (data == null || data.WasCollected) return;
            var instances = ScanInstanceResolver.Instances(data.persistentID);
            if (instances.Count == 0) return;
            foreach (var row in ScanCoverage.RowsForLevel(_rows, level))
                if (row.ChainedPuzzleId == data.persistentID) ApplyRow(row, epoch, instances);
        }
        catch (Exception error) { Fault(error); }
    }

    /// <summary>
    /// One row, from its block id to the writes it asks for. Every instance carrying the block id is resolved
    /// first and the row is refused as a whole if any of them cannot be answered: a row is one authored scan, so
    /// half of it is not a state the author wrote, and a row that reached some of a shared block's instances
    /// would be exactly the silent divergence §4.2.9 forbids.
    /// </summary>
    private bool ApplyRow(ScanCoverageRow row, long epoch, IReadOnlyList<ChainedPuzzleInstance> instances)
    {
        if (instances.Count == 0) { Refuse(row, ScanCoverage.UnresolvedCode, Detail(row, instances)); return false; }
        var targets = new List<Target>(instances.Count);
        var nativeExitDefaults = 0;
        string? refusal = null;
        foreach (var instance in instances)
        {
            var components = ScanInstanceResolver.Components(instance);
            var core = row.Component < components.Count ? components[row.Component].TryCast<CP_Bioscan_Core>() : null;
            var isBioscan = core != null && !core.WasCollected;
            var scanner = isBioscan ? core!.PlayerScanner?.TryCast<CP_PlayerScanner>() : null;
            var isScanner = scanner != null && !scanner.WasCollected;
            var movable = isBioscan && core!.IsMovable && core.m_movingComp != null && !core.m_movingComp.WasCollected;
            var exit = isScanner && (core!.IsExitPuzzle || scanner!.IsExitScan);
            // A preset request identical to the live native exit default is already satisfied.
            // Do not write its scanner or completion state. Real exit overrides still use the
            // explicit exit-scan refusal below; a preset is not permission to rewrite expedition end.
            if (exit && row.Coverage == ScanCoverageMode.Preset && row.Radius == null && row.Route == null
                && row.TeamSpeeds == null && row.Movement == null && row.Colors == null && row.Text == null && row.Shape == null
                && !NativeSetupFields(row)
                && (row.Players == null || row.Players.Rule == ScanPlayerRule.Any
                    && row.Players.NativeRequirement == (int)scanner!.m_playerRequirement))
            {
                nativeExitDefaults++;
                continue;
            }
            // The extraction point's own scan may take only its radius and colours (contract §4.5 M13): its
            // completion is the expedition's end and every other coverage field would rewrite that.
            var exitOverride = exit && !(row.Coverage is ScanCoverageMode.Radius or ScanCoverageMode.Preset && (row.Radius != null || row.Colors != null)
                && row.Route == null && row.Movement == null && row.TeamSpeeds == null && row.Shape == null && row.Text == null && !NativeSetupFields(row)
                && row.Players is null or { Rule: ScanPlayerRule.Any });
            var code = ScanCoverage.Refusal(instances.Count, components.Count, row.Component, isBioscan, exitOverride,
                movable, row.Route != null, row.Movement != null);
            if (code != null) { refusal = code; break; }
            if (!isScanner) { refusal = ScanCoverage.NoScannerCode; break; }
            if (row.Points != null && core!.m_owner?.TryCast<CP_Cluster_Core>() == null) { refusal = ScanCoverage.PointsNotGroupCode; break; }
            targets.Add(new Target(core!, scanner!));
            if (!_instanceCores.TryGetValue(instance.Pointer, out var ownedCores))
                _instanceCores.Add(instance.Pointer, ownedCores = new List<IntPtr>());
            if (!ownedCores.Contains(core!.Pointer)) ownedCores.Add(core.Pointer);
        }
        if (refusal != null)
        {
            Refuse(row, refusal, Detail(row, instances));
            return false;
        }
        if (nativeExitDefaults != 0 && _once.ShouldReport("native-exit-unchanged:" + row.Describe()))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ScanCoverageRow,
                "Exit preset already matches the native scanner; no override or completion write was made.",
                RuntimeLogValue.Of("chainedPuzzleId", (long)row.ChainedPuzzleId),
                RuntimeLogValue.Of("component", (long)row.Component), RuntimeLogValue.Of("coverage", "preset"),
                RuntimeLogValue.Of("outcome", "native-exit-unchanged"), RuntimeLogValue.Of("nativeInstances", (long)nativeExitDefaults));
        // A route is one path for the whole row, so its room-local points are composed into world coordinates once
        // and every instance of the block id travels the same path.
        IReadOnlyList<Vector3>? route = null;
        if (row.Route != null && !TryRoutePoints(row, epoch, out route)) return false;
        IReadOnlyList<iWardenObjectiveItem>? requiredItems = null;
        if (row.RequiredItems != null && !TryRequiredItems(row, out requiredItems)) return false;
        var written = 0;
        foreach (var target in targets)
            if (!_written.Contains(target.Core.Pointer) && !ApplyTarget(row, target, route, write: false)) return false;
        foreach (var target in targets)
        {
            if (_written.Contains(target.Core.Pointer)) continue;
            // Before the membership plan is read: it counts one flag per item the core holds.
            if (requiredItems != null) target.Core.AddRequiredItems(new Il2CppReferenceArray<iWardenObjectiveItem>(requiredItems.ToArray()));
            if (!ApplyTarget(row, target, route)) return false;
            _written.Add(target.Core.Pointer);
            written++;
        }
        if (written == 0) return true;
        RowApplied(row, row.Describe() + " coverage=" + row.Coverage
            + (row.TeamSpeeds == null ? "" : " teamSpeeds=" + string.Join(",", row.TeamSpeeds.Select(value => value.ToString("R", CultureInfo.InvariantCulture))))
            + (row.Movement == null ? "" : " movement=" + row.Movement.Value)
            + " instances="
            + instances.Count.ToString(CultureInfo.InvariantCulture) + " written="
            + written.ToString(CultureInfo.InvariantCulture) + (Session.IsHost ? " host" : " client"));
        return true;
    }

    /// <summary>Precomputes every native Single before this scan's first write. A later speed or ring overflow
    /// must refuse the whole application rather than leave coverage, movement or an earlier speed cell changed.</summary>
    private bool ApplyTarget(ScanCoverageRow row, Target target, IReadOnlyList<Vector3>? route, bool write = true)
    {
        CP_Bioscan_Graphics? graphics = null;
        GameObject? zone = null;
        var metres = 0f;
        var radiusSquared = 0f;
        var scaled = default(Vector3);
        if (row.Coverage == ScanCoverageMode.Radius || row.Colors != null || row.Text != null)
        {
            graphics = target.Core.m_graphicsComp?.TryCast<CP_Bioscan_Graphics>();
            zone = graphics == null || graphics.WasCollected ? null : graphics.m_gfxScannerZone;
            if (graphics == null || graphics.WasCollected || zone == null || zone.WasCollected)
            {
                Refuse(row, ScanCoverage.NoRingCode,
                    "the scan core holds no bioscan graphics ring, so the radius cannot be shown and is not written");
                return false;
            }
        }
        if (row.Coverage == ScanCoverageMode.Radius)
        {
            metres = (float)row.Radius!.Value;
            radiusSquared = metres * metres;
            if (row.Shape != null)
            {
                // Native MasterScan uses a strict float squared-distance gate. Round that coarse gate outward;
                // the shape filter still checks the author's <= sphere in double precision, including its edge.
                radiusSquared = (float)(row.Shape.Radius * row.Shape.Radius);
                for (var i = 0; i < 3; i++) radiusSquared = MathF.BitIncrement(radiusSquared);
            }
            var original = target.Scanner.m_scanRadius;
            var factor = original > 0 ? metres / original : 1f;
            var start = graphics!.m_startScale;
            // The ring widens horizontally and keeps its height, including the scale restored on visibility changes.
            scaled = new Vector3(start.x * factor, start.y, start.z * factor);
            if (!float.IsFinite(metres) || !float.IsFinite(radiusSquared)
                || !float.IsFinite(scaled.x) || !float.IsFinite(scaled.y) || !float.IsFinite(scaled.z))
            {
                Refuse(row, RadiusValueCode, "the derived radius, squared radius and ring scale must be finite Singles");
                return false;
            }
        }
        MeshFilter? shapeFilter = null;
        double shapeRoomYaw = 0;
        if ((row.Colors != null || row.Text != null) && (!_graphicsReady.TryGetValue(graphics!.Pointer, out var graphicsEpoch) || graphicsEpoch.Epoch != _worldEpoch() || graphicsEpoch.Instance != graphics.GetInstanceID()))
        { Warn(row.Describe(), "scan-coverage-colors-setup-pending", "the native graphics Setup has not completed; colours and ring text await that instance boundary"); return false; }
        if (row.Colors != null && (graphics!.m_colors == null || graphics.m_colorsByMode == null))
        { Refuse(row, "scan-coverage-colors-unreadable", "the native colour mode table is unreadable"); return false; }
        if (row.Shape != null)
        {
            shapeFilter = zone!.GetComponent<MeshFilter>();
            var geo = target.Core.m_courseNode?.m_area?.m_geomorph;
            if (shapeFilter == null || shapeFilter.WasCollected || geo == null || geo.WasCollected)
            { Refuse(row, "scan-coverage-shape-unresolved", "the native ring mesh or room orientation is unreadable"); return false; }
            shapeRoomYaw = geo.transform.eulerAngles.y * Math.PI / 180;
            scaled = new Vector3(metres, scaled.y, metres);
        }
        // The exit scan keeps the player requirement and speeds its native preset carries; only radius and colours are written.
        var players = target.Core.IsExitPuzzle || target.Scanner.IsExitScan ? null : row.Players;
        var factors = row.TeamSpeeds;
        var writeSpeeds = factors != null || players != null || row.Shape != null || row.RequiredItems != null;
        var speeds = writeSpeeds ? target.Scanner.m_scanSpeeds : null;
        Span<float> teamSpeeds = stackalloc float[writeSpeeds ? 4 : 0];
        if (writeSpeeds)
        {
            if (speeds == null || speeds.Length != 4)
            {
                Refuse(row, ScanCoverage.TeamSpeedsCode, "the scanner's speed table has "
                    + (speeds == null ? "no" : speeds.Length.ToString(CultureInfo.InvariantCulture)) + " entries; the row carries "
                    + "4");
                return false;
            }
            for (var i = 0; i < 4; i++)
            {
                teamSpeeds[i] = players?.Allows(i + 1) == false ? 0 : speeds[i] * (float)(factors?[i] ?? 1);
                if (!float.IsFinite(teamSpeeds[i]))
                {
                    Refuse(row, TeamSpeedsValueCode, "every derived team-size scan speed must be a finite Single");
                    return false;
                }
            }
        }
        var movementSpeed = row.Route == null ? 0f : target.Core.m_movingComp!.m_movementSpeed * (float)row.Route.Speed;
        if (row.Route != null && !float.IsFinite(movementSpeed))
        {
            Refuse(row, RouteValueCode, "the derived movement speed must be a finite Single");
            return false;
        }

        if (!write) return row.Coverage is ScanCoverageMode.Room or ScanCoverageMode.Zone ? ApplyOwnership(row, target, write: false) : true;
        // Unity destroys a level's native objects at its teardown, and the next level's pass releases this
        // level's receipts afterwards. Every withdrawal asks first whether its object is still there.
        var scanner = target.Scanner;
        bool scannerGone() => scanner.WasCollected || scanner == null;
        bool graphicsGone() => graphics == null || graphics.WasCollected;
        bool zoneGone() => zone == null || zone.WasCollected;
        var owned = new ScanOwnedInputs();
        _owned.Add(target.Core.Pointer, owned);
        if (row.Coverage == ScanCoverageMode.Radius)
        {
            owned.Write(() => target.Scanner.m_scanRadius, value => target.Scanner.m_scanRadius = value, metres, scannerGone);
            owned.Write(() => target.Scanner.m_scanRadiusSqr, value => target.Scanner.m_scanRadiusSqr = value, radiusSquared, scannerGone);
            owned.Write(() => graphics!.m_radius, value => graphics!.m_radius = value, metres, graphicsGone);
            owned.Write(() => graphics!.m_startScale, value => graphics!.m_startScale = value, scaled, graphicsGone);
            owned.Write(() => zone!.transform.localScale, value => zone!.transform.localScale = value, scaled, zoneGone);
        }
        else if (row.Coverage != ScanCoverageMode.Preset && !ApplyOwnership(row, target))
        {
            _owned.Remove(target.Core.Pointer); owned.Dispose(); return false;
        }
        else if (row.Coverage is ScanCoverageMode.Room or ScanCoverageMode.Zone && Session.IsHost
            && _plans.TryGetValue(target.Core.Pointer, out var authorityPlan))
        {
            owned.Write(() => target.Scanner.m_scanRadius, value => target.Scanner.m_scanRadius = value, authorityPlan.GateRadius, scannerGone);
            owned.Write(() => target.Scanner.m_scanRadiusSqr, value => target.Scanner.m_scanRadiusSqr = value, authorityPlan.GateRadiusSquared, scannerGone);
        }
        if (row.ReduceSpeed is { } reduce)
        {
            // On every machine: the HUD reads the flag, and a promoted host runs the loss from these same fields.
            owned.Write(() => target.Scanner.m_reduceWhenNoPlayer, value => target.Scanner.m_reduceWhenNoPlayer = value, reduce > 0, scannerGone);
            owned.Write(() => target.Scanner.m_reduceSpeed, value => target.Scanner.m_reduceSpeed = value, (float)reduce, scannerGone);
        }
        if (players != null) owned.Write(() => target.Scanner.m_playerRequirement, value => target.Scanner.m_playerRequirement = value, (PlayerRequirement)players.NativeRequirement, scannerGone);
        if (Session.IsHost && writeSpeeds) owned.Speeds(target.Scanner, teamSpeeds.ToArray());
        if (row.Colors != null || row.Text != null)
        {
            var look = new ScanGraphicsState(graphics!, row.Colors, row.Text);
            owned.Track(look.Dispose);
            _graphicsTargets.Add(graphics!.Pointer, (target.Core.Pointer, look));
            look.Apply();
        }
        ScanCoverageTarget? counting = null;
        if (row.Shape != null)
        {
            var mesh = ScanCoverageTarget.BuildMesh(row.Shape);
            // Reverse withdrawal detaches our mesh before destroying it, also at the native Setup boundary.
            var originalMesh = shapeFilter!.sharedMesh;
            owned.Track(() =>
            {
                if (!shapeFilter.WasCollected && shapeFilter != null && shapeFilter.sharedMesh?.Pointer == mesh.Pointer) shapeFilter.sharedMesh = originalMesh;
                if (!mesh.WasCollected) UnityEngine.Object.Destroy(mesh);
            });
            shapeFilter.sharedMesh = mesh;
            var yaw = shapeRoomYaw + row.Shape.Yaw;
            owned.Write(() => zone!.transform.rotation, value => zone!.transform.rotation = value,
                Quaternion.Euler(0, (float)(Math.Atan2(Math.Sin(yaw), Math.Cos(yaw)) * 180 / Math.PI), 0), zoneGone);
            counting = new ScanCoverageTarget(row, target.Core, target.Scanner, shapeRoomYaw, authoredSpeeds: owned.SpeedInput,
                authorityRadius: metres, authorityRadiusSquared: radiusSquared);
            _shapes[target.Core.Pointer] = counting;
        }
        else if (_plans.TryGetValue(target.Core.Pointer, out var membershipPlan))
            counting = new ScanCoverageTarget(row, target.Core, target.Scanner, 0, membershipPlan.Membership, owned.SpeedInput,
                membershipPlan.GateRadius, membershipPlan.GateRadiusSquared);
        else if (writeSpeeds || row.Coverage == ScanCoverageMode.Radius)
            counting = new ScanCoverageTarget(row, target.Core, target.Scanner, 0, authoredSpeeds: owned.SpeedInput,
                authorityRadius: row.Coverage == ScanCoverageMode.Radius ? metres : null,
                authorityRadiusSquared: row.Coverage == ScanCoverageMode.Radius ? radiusSquared : null);
        if (counting != null) _authorityScanners[target.Scanner.Pointer] = counting;
        if (row.Movement != null) target.Core.m_movingComp!.m_typeOfMovement = (MovementType)(int)row.Movement.Value;
        if (row.Route == null) return true;
        var movable = target.Core.m_movingComp!;
        movable.ScanPositions = Positions(route!);
        movable.m_amountOfPositions = route!.Count;
        movable.m_movementSpeed = movementSpeed;
        movable.m_useStaticBioscanPoints = false;
        return true;
    }

    /// <summary>
    /// One `room` or `zone` row. The owned member set is resolved and checked on every machine — a row whose
    /// members this level cannot name is refused everywhere and not only on the host. Every peer retains the
    /// membership for promotion, while only the current host writes the coarse gate and publishes it. The ring is never touched here:
    /// the preset ring stays the visual, and the member rule is the ownership the document names.
    /// </summary>
    private bool ApplyOwnership(ScanCoverageRow row, Target target, bool write = true)
    {
        var room = row.Coverage == ScanCoverageMode.Room;
        var resolved = room
            ? ScanMembership.TryRoom(target.Core, out var membership, out var reason)
            : ScanMembership.TryZone(target.Core, out membership, out reason);
        if (!resolved)
        {
            Refuse(row, room ? ScanCoverage.RoomUnresolvedCode : ScanCoverage.ZoneUnresolvedCode, reason);
            return false;
        }
        if (!membership!.TryPlayers(out var players, out reason))
        {
            Refuse(row, ScanCoverage.MemberUnreadableCode, reason);
            return false;
        }
        if (players.Count > ScanCoverage.PlayerSlotLimit)
        {
            Refuse(row, ScanCoverage.MemberLimitCode, players.Count + " players stand in this scan; the replicated "
                + "state carries " + ScanCoverage.PlayerSlotLimit + " slots");
            return false;
        }
        if (!membership.TryRequiredItems(target.Core, out var required, out reason))
        {
            Refuse(row, ScanCoverage.MemberUnreadableCode, reason);
            return false;
        }
        if (required.Length > ScanCoverage.RequiredItemLimit)
        {
            Refuse(row, ScanCoverage.MemberLimitCode, "the scan requires " + required.Length + " items; the "
                + "replicated state carries " + ScanCoverage.RequiredItemLimit + " bits");
            return false;
        }
        if (!membership.TryGateRadius(target.Core, out var gate, out reason))
        {
            Refuse(row, ScanCoverage.GateUnresolvedCode, reason);
            return false;
        }
        var metres = (float)gate;
        var radiusSquared = metres * metres;
        if (!float.IsFinite(metres) || !float.IsFinite(radiusSquared))
        {
            Refuse(row, ScanCoverage.GateUnresolvedCode, "the derived membership gate and squared radius must be finite Singles");
            return false;
        }
        if (!write) return true;
        _plans[target.Core.Pointer] = new Plan(row, membership, metres, radiusSquared);
        if (!Session.IsHost)
        {
            RowApplied(row, row.Describe() + " coverage=" + row.Coverage + " members="
                + players.Count.ToString(CultureInfo.InvariantCulture) + " host-only");
            return true;
        }
        RowApplied(row, row.Describe() + " coverage=" + row.Coverage + " gate=" + Show(metres)
            + " ring=preset nodes=" + membership.Detail);
        return true;
    }

    /// <summary>
    /// One route row's points, composed through the same room resolver a fixed placement uses. The room-local
    /// points are resolved once per row, because every instance of the block id travels the same authored path.
    /// </summary>
    private bool TryRoutePoints(ScanCoverageRow row, long epoch, out IReadOnlyList<Vector3>? positions)
    {
        positions = null;
        var points = new List<Vector3>(row.Route!.Points.Count);
        foreach (var point in row.Route.Points)
        {
            if (!RoomResolver.TryPlace(epoch, point.Locator, out var room, out var roomRefusal))
            {
                Refuse(row, ScanCoverage.RoutePointCode, roomRefusal);
                return false;
            }
            var world = point.WorldPoint(room!.Pose);
            if (_level() is not { } level || _placements?.RoutePointAllowed(level, row.ChainedPuzzleId, point.Locator) == false)
            { Refuse(row, ScanCoverage.RoutePointCode, "the placed scan route must stay in its own zone"); return false; }
            var position = new Vector3((float)world[0], (float)world[1], (float)world[2]);
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
            {
                Refuse(row, ScanCoverage.RoutePointCode, "every resolved route coordinate must be a finite Single");
                return false;
            }
            points.Add(position);
        }
        positions = points.AsReadOnly();
        return true;
    }

    private static bool NativeSetupFields(ScanCoverageRow row)
        => row.Position != null || row.Points != null || row.RequiredItems != null || row.ReduceSpeed != null;

    /// <summary>The row's required items as the native entry takes them. Fixed items are placed on every machine.</summary>
    private bool TryRequiredItems(ScanCoverageRow row, out IReadOnlyList<iWardenObjectiveItem>? items)
    {
        items = null;
        var found = new List<iWardenObjectiveItem>(row.RequiredItems!.Count);
        foreach (var address in row.RequiredItems)
        {
            if (MapHost.Session?.Placements?.WardenItem(address) is { } item) { found.Add(item); continue; }
            Refuse(row, ScanCoverage.RequiredItemCode, "`" + address + "` is no carryable fixed item of this level");
            return false;
        }
        items = found.AsReadOnly();
        return true;
    }

    /// <summary>The instance a Setup argument belongs to, and how many scans the rounds before this one expanded to,
    /// so a Setup answers the same ordinal the finished instance's walk does.</summary>
    private static int FirstOfRound(ChainedPuzzleInstance instance, int round)
    {
        var first = 0;
        for (var index = 0; index != round; index++)
        {
            var core = instance.GetPuzzle(index);
            first += core?.TryCast<CP_Cluster_Core>() is { m_childCores: { } children } ? children.Length : 1;
        }
        return first;
    }

    private ScanCoverageRow? SetupRow(ChainedPuzzleInstance instance, int component)
    {
        if (_level() is not { } level || instance.Data is not { WasCollected: false } data) return null;
        foreach (var row in _rows)
            if (row.Level == level && row.ChainedPuzzleId == data.persistentID && row.Component == component) return row;
        return null;
    }

    /// <summary>One authored point as the world position and area native Setup reads. An unresolved point is refused
    /// and the scan keeps the place the game chose.</summary>
    private bool TryPoint(ScanCoverageRow row, ScanRoutePoint point, out Vector3 world, out LG_Area? area)
    {
        world = default;
        area = null;
        ZoneIndex.Reset();
        try
        {
            if (!RoomResolver.TryPlace(_worldEpoch(), point.Locator, out var room, out var refusal))
            { Refuse(row, ScanCoverage.PositionCode, refusal); return false; }
            var at = point.WorldPoint(room!.Pose);
            world = new Vector3((float)at[0], (float)at[1], (float)at[2]);
            if (!float.IsFinite(world.x) || !float.IsFinite(world.y) || !float.IsFinite(world.z))
            { Refuse(row, ScanCoverage.PositionCode, "the resolved coordinate must be a finite Single"); return false; }
            // A hanging point stands in no node: it keeps the scan's own area rather than guessing another.
            if (RoomResolver.TryCourseNode(point.Locator.Dimension, at, null, out var node)) area = node?.m_area;
            return true;
        }
        finally { ZoneIndex.Reset(); }
    }

    /// <summary>A round's guide line starts where the previous round ended, which native Setup only knows as a local.</summary>
    private void ContinueGuideLine(ChainedPuzzleInstance instance, int round, ref Vector3 previous)
    {
        if (_roundEnds.TryGetValue((instance.Pointer, round - 1), out var end)) previous = end;
    }

    internal void BeforeBioscanSetup(CP_Bioscan_Core core, int index, iChainedPuzzleOwner? owner, ref LG_Area sourceArea, ref Vector3 previous)
    {
        if (!Pending) return;
        try
        {
            var cluster = owner?.TryCast<CP_Cluster_Core>();
            var round = cluster?.m_puzzleIndex ?? index;
            var instance = cluster != null ? cluster.m_owner?.TryCast<ChainedPuzzleInstance>() : owner?.TryCast<ChainedPuzzleInstance>();
            if (instance == null || instance.WasCollected) return;
            var first = FirstOfRound(instance, round);
            if (cluster == null) ContinueGuideLine(instance, round, ref previous);
            if (SetupRow(instance, first + (cluster != null ? index : 0)) is not { Position: { } point, Route: null } row
                || !TryPoint(row, point, out var world, out var area)) return;
            core.transform.position = world;
            if (area != null) sourceArea = area;
            if (cluster == null) _roundEnds[(instance.Pointer, round)] = world;
        }
        catch (Exception error) { Fault(error); }
    }

    internal void BeforeClusterSetup(CP_Cluster_Core cluster, int round, iChainedPuzzleOwner? owner, ref LG_Area sourceArea, ref Vector3 previous)
    {
        if (!Pending) return;
        try
        {
            if (owner?.TryCast<ChainedPuzzleInstance>() is not { WasCollected: false } instance) return;
            var first = FirstOfRound(instance, round);
            ContinueGuideLine(instance, round, ref previous);
            if (SetupRow(instance, first)?.Points is { } points) cluster.m_amountOfPuzzles = points;
            // The group's centre is the middle of its authored scans, so native placement and every guide line start there.
            var sum = Vector3.zero;
            var count = 0;
            LG_Area? centreArea = null;
            for (var child = 0; child != cluster.m_amountOfPuzzles; child++)
            {
                if (SetupRow(instance, first + child) is not { Position: { } point, Route: null } row
                    || !TryPoint(row, point, out var world, out var area)) continue;
                sum += world;
                count++;
                centreArea ??= area;
            }
            if (count == 0) return;
            var centre = sum / count;
            cluster.transform.position = centre;
            if (centreArea != null) sourceArea = centreArea;
            _roundEnds[(instance.Pointer, round)] = centre;
        }
        catch (Exception error) { Fault(error); }
    }

    /// <summary>
    /// Rewrites one covered scan's own counting arguments, on the host, inside the scan core's own counting
    /// entry: the players standing in the owned node set, the count that entry treats as its own maximum, and one
    /// required-item flag per item. Everything else — progress accumulation, the replicated state, completion, the
    /// HUD — stays the game's own.
    ///
    /// A member set that cannot be read or has grown past what the replicated state carries is answered with no
    /// members at all rather than a partial set that would complete on the wrong ones. The refusal is reported with
    /// the row identity and leaves the original scan beat running at zero speed.
    /// </summary>
    internal void Inject(CP_Bioscan_Core? core, Il2CppList? playersInScan, ref int inScanMax,
        Il2CppStructArray<bool>? reqObjsInScan)
    {
        if (_disposed || Down || core == null || core.WasCollected) return;
        ScanCoverageRow? memberRow = null;
        ScanCoverageTarget? authorityTarget = null;
        try
        {
            if (!Session.IsHost) return;
            if (_shapes.TryGetValue(core.Pointer, out var shape))
            {
                memberRow = shape.Row; authorityTarget = shape;
                if (playersInScan == null)
                {
                    RejectMembers(shape.Row, ScanCoverage.MemberUnreadableCode, "the native player member list does not read", null,
                    ref inScanMax, reqObjsInScan, shape); return;
                }
                if (!shape.TryFilterMembers(playersInScan, reqObjsInScan, out var playerCount, out var requiredCount,
                    out var refusalCode, out var refusalDetail))
                { RejectMembers(shape.Row, refusalCode!, refusalDetail, playersInScan, ref inScanMax, reqObjsInScan, shape); return; }
                playersInScan.Clear();
                for (var i = 0; i < playerCount; i++) playersInScan.Add(shape.FilteredPlayer(i));
                if (core.m_reqItemsEnabled && reqObjsInScan != null)
                    for (var i = 0; i < requiredCount; i++) reqObjsInScan[i] = shape.FilteredRequired(i);
                shape.RestoreRequirementForHud();
                return;
            }
            if (!_plans.TryGetValue(core.Pointer, out var plan))
            {
                // No member rewrite of its own: the frame only hands the HUD its real requirement back.
                if (core.PlayerScanner?.TryCast<CP_PlayerScanner>() is { } plain && _authorityScanners.TryGetValue(plain.Pointer, out var plainTarget))
                    plainTarget.RestoreRequirementForHud();
                return;
            }
            memberRow = plan.Row;
            if (core.PlayerScanner?.TryCast<CP_PlayerScanner>() is { } scanner
                && _authorityScanners.TryGetValue(scanner.Pointer, out authorityTarget)
                && authorityTarget.TryCurrentRefusal(out var currentCode, out var currentDetail))
            { RejectMembers(plan.Row, currentCode!, currentDetail, playersInScan, ref inScanMax, reqObjsInScan, authorityTarget); return; }
            if (playersInScan == null)
            {
                RejectMembers(plan.Row, ScanCoverage.MemberUnreadableCode, "the native player member list does not read", null,
                ref inScanMax, reqObjsInScan, authorityTarget); return;
            }
            if (!plan.Membership.TryPlayers(out var players, out var reason))
            {
                RejectMembers(plan.Row, ScanCoverage.MemberUnreadableCode, reason, playersInScan, ref inScanMax, reqObjsInScan, authorityTarget);
                return;
            }
            if (players.Count > ScanCoverage.PlayerSlotLimit)
            {
                RejectMembers(plan.Row, ScanCoverage.MemberLimitCode, players.Count + " players stand in this scan; the "
                    + "replicated state carries " + ScanCoverage.PlayerSlotLimit + " slots", playersInScan, ref inScanMax,
                    reqObjsInScan, authorityTarget);
                return;
            }
            if (!plan.Membership.TryRequiredItems(core, out var required, out reason))
            {
                RejectMembers(plan.Row, ScanCoverage.MemberUnreadableCode, reason, playersInScan, ref inScanMax, reqObjsInScan, authorityTarget);
                return;
            }
            if (required.Length > ScanCoverage.RequiredItemLimit)
            {
                RejectMembers(plan.Row, ScanCoverage.MemberLimitCode, "the scan requires " + required.Length + " items; the "
                    + "replicated state carries " + ScanCoverage.RequiredItemLimit + " bits", playersInScan, ref inScanMax,
                    reqObjsInScan, authorityTarget);
                return;
            }
            if ((core.m_reqItemsEnabled && reqObjsInScan == null)
                || (reqObjsInScan != null && reqObjsInScan.Length != required.Length))
            {
                RejectMembers(plan.Row, "scan-coverage-required-item-mismatch", "the scan publishes "
                    + (reqObjsInScan == null ? "no" : reqObjsInScan.Length.ToString(CultureInfo.InvariantCulture))
                    + " required-item flag(s) for " + required.Length + " required item(s)", playersInScan, ref inScanMax,
                    reqObjsInScan, authorityTarget);
                return;
            }
            playersInScan.Clear();
            foreach (var player in players) playersInScan.Add(player);
            inScanMax = players.Count;
            if (reqObjsInScan != null)
                for (var index = 0; index != required.Length; index++) reqObjsInScan[index] = required[index];
            authorityTarget?.RestoreRequirementForHud();
        }
        catch (Exception error)
        {
            if (memberRow != null)
                RejectMembers(memberRow, ScanCoverage.MemberUnreadableCode, error.GetType().Name + ": " + error.Message,
                    playersInScan, ref inScanMax, reqObjsInScan, authorityTarget);
            else Fault(error);
        }
    }

    private void RejectMembers(ScanCoverageRow row, string code, string? detail, Il2CppList? playersInScan,
        ref int inScanMax, Il2CppStructArray<bool>? reqObjsInScan, ScanCoverageTarget? target)
    {
        inScanMax = 0;
        if (reqObjsInScan != null) for (var i = 0; i < reqObjsInScan.Length; i++) reqObjsInScan[i] = false;
        try { playersInScan?.Clear(); }
        catch (Exception error) { detail = (detail == null ? "" : detail + "; ") + "member cleanup: " + error.GetType().Name + ": " + error.Message; }
        try { target?.RestoreRequirementForHud(); }
        catch (Exception error) { detail = (detail == null ? "" : detail + "; ") + "requirement restore: " + error.GetType().Name + ": " + error.Message; }
        Refuse(row, code, detail);
    }

    /// <summary>One row this session left out, named by its own block id and component and the code that says
    /// why. It is reported once per level, because application runs once per level, as one record.</summary>
    private void Refuse(ScanCoverageRow row, string code, string? detail)
    {
        if (!_once.ShouldReport("refused:" + row.Describe() + ":" + code)) return;
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ScanCoverageRefused, detail ?? code, RuntimeLogValue.Of("chainedPuzzleId", (long)row.ChainedPuzzleId), RuntimeLogValue.Of("component", (long)row.Component), RuntimeLogValue.Of("reason", code));
    }

    /// <summary>One thing about a row that is applied but has to be said, reported once per level.</summary>
    private void Warn(string name, string code, string detail)
    {
        if (!_once.ShouldReport("warning:" + name + ":" + code)) return;
        _report("scan-coverage-warning " + name + " code=" + code + ": " + detail);
    }

    /// <summary>One failure inside a native callback. It disables this half for the rest of the level and is reported once,
    /// which is the contract every other native callback of this package keeps.</summary>
    private void Fault(Exception error)
    {
        if (Down) return;
        _fault.Mark(_worldEpoch());
        var detail = error.ToString();
        try { ReleaseAll(); } catch (Exception cleanup) { detail += "; release: " + cleanup.ToString(); }
        if (MapLog.Logs(RuntimeLogLevel.Error))
        {
            // The writer truncates message previews; ordered values preserve the full exception.
            var exception = MapLog.SafeDetail(detail);
            var parts = (exception.Length + 255) / 256;
            for (var part = 0; part < parts; part++)
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.NativeReport,
                    "Map scan coverage callback failed; join values.exception in part order.",
                    RuntimeLogValue.Of("phase", "scan-coverage-exception"),
                    RuntimeLogValue.Of("part", part + 1), RuntimeLogValue.Of("parts", parts),
                    RuntimeLogValue.Of("exception", exception.Substring(part * 256, Math.Min(256, exception.Length - part * 256))));
        }
        _report("Map scan coverage disabled for this level after native callback failure: " + detail);
    }

    private static string Show(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>One row written onto its scan, as one record the provider's log keeps.</summary>
    private static void RowApplied(ScanCoverageRow row, string detail)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ScanCoverageRow, detail, RuntimeLogValue.Of("chainedPuzzleId", (long)row.ChainedPuzzleId),
            RuntimeLogValue.Of("component", (long)row.Component), RuntimeLogValue.Of("coverage", row.Coverage.ToString()));
    }

    /// <summary>One refusal's own sentence: the block id, how many instances carried it and what the first
    /// instance's component walk answered, so an author reading the log can tell a missing block from a wrong
    /// ordinal without a second read of the level.</summary>
    private static string Detail(ScanCoverageRow row, IReadOnlyList<ChainedPuzzleInstance> instances)
    {
        var components = instances.Count == 0 ? 0 : ScanInstanceResolver.Components(instances[0]).Count;
        return "block " + row.ChainedPuzzleId.ToString(CultureInfo.InvariantCulture) + " matches "
            + instances.Count.ToString(CultureInfo.InvariantCulture) + " instance(s); the first holds "
            + components.ToString(CultureInfo.InvariantCulture) + " component(s)";
    }

    /// <summary>One route as the movement component's own position list. Every core gets its own list, because the
    /// movement routine owns and may rewrite the list it was handed.</summary>
    private static Il2CppSystem.Collections.Generic.List<Vector3> Positions(IReadOnlyList<Vector3> points)
    {
        var list = new Il2CppSystem.Collections.Generic.List<Vector3>();
        foreach (var point in points) list.Add(point);
        return list;
    }

    /// <summary>The same frozen rows prepared for the package transaction; no independent disk discovery.</summary>
    private static IReadOnlyList<ScanCoverageRow> Load(MapPreparedContent prepared) => prepared.ScanCoverage;


    internal ScanGraphicsState? BeforeGraphicsSetup(CP_Bioscan_Graphics graphics, long setupEpoch)
    {
        if (_disposed || Down) return null;
        try
        {
            _graphicsReady.Remove(graphics.Pointer);
            var key = (graphics.Pointer, graphics.GetInstanceID(), setupEpoch);
            _graphicsSetups.TryGetValue(key, out var depth); _graphicsSetups[key] = depth + 1;
            if (!_graphicsTargets.TryGetValue(graphics.Pointer, out var target)) return null;
            target.State.BeforeSetup(); return target.State;
        }
        catch (Exception error) { Fault(error); return null; }
    }
    internal void AfterGraphicsSetup(CP_Bioscan_Graphics graphics, ScanGraphicsState? state, long? setupEpoch = null)
    {
        if (_disposed || Down || graphics.WasCollected) return;
        try
        {
            var outer = EndGraphicsSetup(graphics, setupEpoch ?? _worldEpoch());
            if (setupEpoch is { } epoch && epoch != _worldEpoch()) { state?.FailedSetup(); return; }
            state?.AfterSetup();
            if (!outer) return;
            _graphicsReady[graphics.Pointer] = (_worldEpoch(), graphics.GetInstanceID());
            if (state != null) return;
            // A graphics component may finish Setup after the level pass. Reuse the existing instance row
            // application instead of inventing a second selector or applying a multiplier twice.
            if (!_passRan) return;
            foreach (var instance in ScanInstanceResolver.Instances())
                foreach (var component in ScanInstanceResolver.Components(instance))
                    if (component.TryCast<CP_Bioscan_Core>() is { } core && core.m_graphicsComp?.TryCast<CP_Bioscan_Graphics>() is { } candidate
                        && candidate.Pointer == graphics.Pointer) { ApplyInstance(instance); break; }
        }
        catch (Exception error) { Fault(error); }
    }

    private bool EndGraphicsSetup(CP_Bioscan_Graphics graphics, long epoch)
    {
        var key = (graphics.Pointer, graphics.GetInstanceID(), epoch);
        if (!_graphicsSetups.TryGetValue(key, out var depth) || depth <= 1) { _graphicsSetups.Remove(key); return true; }
        _graphicsSetups[key] = depth - 1; return false;
    }

    /// <summary>A scan HUD has just written its localized name into its world text. When that text is the ring label of
    /// a row that authored one, the label is written again.</summary>
    internal void AfterHudText(CP_Bioscan_Hud hud)
    {
        if (_disposed || Down || _graphicsTargets.Count == 0) return;
        try
        {
            var written = hud.m_bioscanWorldText;
            if (written == null) return;
            foreach (var target in _graphicsTargets.Values)
                if (target.State.LabelRenderer == written.Pointer) target.State.WriteText();
        }
        catch (Exception error) { Fault(error); }
    }

    internal void FailedGraphicsSetup(CP_Bioscan_Graphics graphics, ScanGraphicsState? state, long setupEpoch)
    {
        try
        {
            if (_graphicsReady.TryGetValue(graphics.Pointer, out var ready) && ready.Epoch == setupEpoch && ready.Instance == graphics.GetInstanceID())
                _graphicsReady.Remove(graphics.Pointer);
            EndGraphicsSetup(graphics, setupEpoch); state?.FailedSetup();
        }
        catch (Exception error) { Fault(error); }
    }

    internal void BeforeInstanceSetup(ChainedPuzzleInstance instance)
    {
        try
        {
            foreach (var key in _roundEnds.Keys.ToArray()) if (key.Instance == instance.Pointer) _roundEnds.Remove(key);
            if (!_instanceCores.Remove(instance.Pointer, out var cores)) return;
            foreach (var core in cores) Release(core);
        }
        catch (Exception error) { Fault(error); }
    }

    private void Release(IntPtr core)
    {
        // Temporary frames must unwind before the authored table. A failed receipt remains reachable for retry.
        Exception? failure = null;
        foreach (var pair in _authorityScanners.ToArray())
            if (pair.Value.CorePointer == core)
            {
                try { pair.Value.ReleaseNativeInputs(); _authorityScanners.Remove(pair.Key); }
                catch (Exception error) { failure ??= error; }
            }
        if (failure != null) throw failure;
        if (_owned.TryGetValue(core, out var owned))
        {
            owned.Dispose();
            _owned.Remove(core);
        }
        foreach (var pair in _graphicsTargets.ToArray()) if (pair.Value.Core == core) _graphicsTargets.Remove(pair.Key);
        _shapes.Remove(core); _plans.Remove(core); _written.Remove(core);
    }
    private void ReleaseAll()
    {
        Exception? failure = null;
        foreach (var core in _owned.Keys.ToArray()) try { Release(core); } catch (Exception error) { failure ??= error; }
        if (failure != null) throw failure;
        _instanceCores.Clear(); _plans.Clear(); _authorityScanners.Clear(); _shapes.Clear(); _written.Clear(); _graphicsTargets.Clear();
    }
    public void Dispose()
    {
        if (_disposed && _owned.Count == 0 && _authorityScanners.Count == 0 && _placementBatchDone == null) return;
        _disposed = true;
        try
        {
            _placementBatchDone?.Dispose();
            _placementBatchDone = null;
            _placements?.Forget();
            ReleaseAll();
        }
        catch (Exception error)
        {
            _faulted = true;
            _report("Map scan cleanup retained pending ownership after failure: " + error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            if (_owned.Count == 0 && _authorityScanners.Count == 0 && _placementBatchDone == null)
            {
                _graphicsReady.Clear(); _graphicsSetups.Clear();
                if (ReferenceEquals(Current, this)) Current = null;
            }
        }
    }

    internal ScanNativeBeat? BeforeNativeBeat(CP_PlayerScanner? scanner)
    {
        if (_disposed || Down || !Session.IsHost || _worldEpoch() != _appliedEpoch || scanner == null || scanner.WasCollected) return null;
        try
        {
            if (!_authorityScanners.TryGetValue(scanner.Pointer, out var target)) return null;
            if (target.FiltersMembers)
            {
                var beat = target.BeforeNativeBeat(out var refusalCode, out var refusalDetail);
                if (refusalCode != null) Refuse(target.Row, refusalCode, refusalDetail);
                return beat;
            }
            target.EnsureAuthorityInputs(); return null;
        }
        catch (Exception error) { Fault(error); return null; }
    }
}
