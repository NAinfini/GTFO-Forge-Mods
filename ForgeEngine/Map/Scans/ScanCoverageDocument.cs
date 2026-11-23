using ForgeCore;
using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

/// <summary>How one authored scan decides who counts as standing in it. The three spellings are the document's
/// own wire words; the runtime half reads each one as the member rule it names and never as a placement.
///
/// `Radius` is the preset scan's own circle: the game's own distance gate decides, and the authored number is the
/// radius in metres. `Room` and `Zone` are the ownership rules of §4.2.9: the scan's own generated course node
/// owns the members, or the zone that node stands in does. Neither carries a radius — an author who wrote one
/// would be choosing a room the scan is not in, which is exactly what the contract forbids. `Preset` leaves the
/// preset's own member rule and circle alone. It may still carry C10's player requirement and colour overrides,
/// or a movement route; those fields change their own native inputs without replacing the preset circle.</summary>
internal enum ScanCoverageMode
{
    Radius,
    Room,
    Zone,
    Preset
}

/// <summary>How a moving scan travels: the game's own `ChainedPuzzles.MovementType` values (dump.cs 610266), written
/// as they are into the scan's `CP_BasicMovable.m_typeOfMovement`. The document spells them with the game's own
/// member names. Only a scan whose preset carries a movement component can take one.</summary>
internal enum ScanMovement
{
    None = 0,
    Directional = 1,
    Circular = 2
}

/// <summary>
/// One point of an authored movement route: the room it stands in and its position in that room's prefab-root
/// local frame, which is the same locator and the same local point every placed object of this family carries.
/// A route point is not a world coordinate: the level the game generated is the only thing that can say where the
/// room ended up, so the document carries the reference and the native half composes the pose.
/// </summary>
internal sealed class ScanRoutePoint
{
    internal ScanRoutePoint(RoomLocator locator, double[] position)
    {
        Locator = locator;
        Position = Array.AsReadOnly(position);
    }

    /// <summary>The room the point stands in, in the one locator spelling every authored room reference uses.</summary>
    public RoomLocator Locator { get; }
    /// <summary>The point in the room's prefab-root local coordinates, in metres.</summary>
    public IReadOnlyList<double> Position { get; }

    /// <summary>This point in the world, through the room's own world pose. It is the same one conversion a
    /// placed object uses, so a route point and a fixed placement cannot land two different ways.</summary>
    internal double[] WorldPoint(RoomPose room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return room.Point(Position);
    }
}

/// <summary>
/// One authored movement route: the world points the scan travels through, in order, and the multiplier its own
/// speed is scaled by. The route belongs to the row and is applied on every machine, because the game replicates
/// only the interpolation phase and the pause flag — each peer computes the scan's position from its own
/// `ScanPositions`, so a route applied on the host alone would put the ring in a different place on every other
/// machine.
/// </summary>
internal sealed class ScanRoute
{
    internal ScanRoute(IReadOnlyList<ScanRoutePoint> points, double speed)
    {
        Points = points;
        Speed = speed;
    }

    /// <summary>The route's points in the author's own order. At least two, so a route is a path and not a point.</summary>
    public IReadOnlyList<ScanRoutePoint> Points { get; }
    /// <summary>The factor the scan's own `m_movementSpeed` is multiplied by.</summary>
    public double Speed { get; }
}

/// <summary>
/// One authored scan row: the level it belongs to, the ChainedPuzzle block it names, the component ordinal inside
/// that block's runtime instances, what the scan's coverage is, and the optional movement route.
///
/// §4.2.9 fixes the row's identity as the block and not the host: the author clones the ChainedPuzzle block into
/// this project's own block and the host points at the clone, so the block's own `persistentID` is what every
/// machine can read back after generation. The row is applied to every runtime `ChainedPuzzleInstance` whose own
/// `Data.persistentID` is that block — a shared preset reached from two hosts is two instances and the row is
/// written to both, which is why a route row has to be reachable from exactly one host at export time.
///
/// The component ordinal counts an instance's own components in the game's own order with every cluster's child
/// scans expanded in place, which is the spelling §4.2.9 fixes: a cluster is one component that contains several
/// scans, and an author points at the scan and not at the container.</summary>
internal sealed class ScanCoverageRow
{
    internal ScanCoverageRow(MapLevelReference level, uint chainedPuzzleId, int component,
        ScanCoverageMode coverage, double? radius, ScanRoute? route, IReadOnlyList<double>? teamSpeeds = null,
        ScanMovement? movement = null, ScanPlayers? players = null, IReadOnlyList<ScanColor>? colors = null, ScanShape? shape = null,
        ScanRoutePoint? position = null, int? points = null, IReadOnlyList<string>? requiredItems = null, double? reduceSpeed = null,
        string? text = null)
    {
        Text = text;
        Level = level;
        ChainedPuzzleId = chainedPuzzleId;
        Component = component;
        Coverage = coverage;
        Radius = radius;
        Route = route;
        TeamSpeeds = teamSpeeds;
        Movement = movement;
        Players = players;
        Colors = colors;
        Shape = shape;
        Position = position;
        Points = points;
        RequiredItems = requiredItems;
        ReduceSpeed = reduceSpeed;
    }

    /// <summary>The level this row belongs to. A row of another level is not this world's row.</summary>
    public MapLevelReference Level { get; }
    /// <summary>The native `ChainedPuzzleDataBlock.persistentID` this row is written against. Every runtime
    /// instance carrying it is an instance this row applies to.</summary>
    public uint ChainedPuzzleId { get; }
    /// <summary>The scan's ordinal inside an instance, with cluster child scans expanded in the game's own order.</summary>
    public int Component { get; }
    /// <summary>How the scan decides who is in it.</summary>
    public ScanCoverageMode Coverage { get; }
    /// <summary>The authored radius in metres, present exactly when <see cref="Coverage"/> is
    /// <see cref="ScanCoverageMode.Radius"/>.</summary>
    public double? Radius { get; }
    /// <summary>The authored movement route, or null when the row leaves the preset route alone.</summary>
    public ScanRoute? Route { get; }
    /// <summary>The team-size scan multipliers (EOS `scan.Speed`, which TeamSize was merged into): one factor per
    /// number of players in the scan, 1 to <see cref="ScanCoverage.PlayerSlotLimit"/>, multiplied into the scanner's
    /// own `m_scanSpeeds`; null when the row leaves the preset speeds alone.</summary>
    public IReadOnlyList<double>? TeamSpeeds { get; }
    /// <summary>The moving scan's movement type written into `CP_BasicMovable.m_typeOfMovement`; null when the row
    /// leaves the preset's own movement alone.</summary>
    public ScanMovement? Movement { get; }
    public ScanPlayers? Players { get; }
    public IReadOnlyList<ScanColor>? Colors { get; }
    public ScanShape? Shape { get; }
    /// <summary>Where this scan stands, written into the core's transform before its native Setup reads it, so the
    /// game's own position, course node, sound and guide line all follow one authored point. A route row travels its
    /// route instead. Null keeps the position the game chose.</summary>
    public ScanRoutePoint? Position { get; }
    /// <summary>On a group's first scan: how many scans the group has (`CP_Cluster_Core.m_amountOfPuzzles`, read only
    /// during the group's native Setup). Every scan of a group runs at once and the group passes when all finish.</summary>
    public int? Points { get; }
    /// <summary>The fixed items that must stand in this scan: `item/...` addresses, handed to the native
    /// `CP_Bioscan_Core.AddRequiredItems`. An item counts while its holder or the item itself is inside.</summary>
    public IReadOnlyList<string>? RequiredItems { get; }
    /// <summary>The share of the scan lost per second while it cannot advance (`CP_PlayerScanner.m_reduceSpeed`). Zero
    /// switches the loss off; null keeps the preset.</summary>
    public double? ReduceSpeed { get; }
    /// <summary>The ring label, written through the graphics' own `SetText` after its Setup; null keeps the preset's
    /// own label.</summary>
    public string? Text { get; }

    /// <summary>The row as a diagnostic names it: the block id and the component ordinal are the row's whole
    /// identity, because the level and the block together are what an author wrote and a report has to name.</summary>
    public string Describe()
        => "chainedPuzzleId=" + ChainedPuzzleId.ToString(CultureInfo.InvariantCulture) + " component="
            + Component.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => Describe();
}

/// <summary>One row a document refused, with the code that names what was wrong and the sentence that names the
/// first thing that could not be read. A refused row is left out of the table; the rows around it are kept,
/// because one mistyped row must not take a level's whole scan coverage away.</summary>
internal readonly record struct ScanCoverageRefusal(string Code, string Reason, uint? ChainedPuzzleId = null, int? Component = null, MapLevelReference? Level = null);

/// <summary>The rows one document answered: the rows it could read, in the author's order, and the rows it
/// refused, in the same order.</summary>
internal sealed class ScanCoverageDocument
{
    internal ScanCoverageDocument(IReadOnlyList<ScanCoverageRow> rows, IReadOnlyList<ScanCoverageRefusal> refusals)
    {
        Rows = rows;
        Refusals = refusals;
    }

    public IReadOnlyList<ScanCoverageRow> Rows { get; }
    public IReadOnlyList<ScanCoverageRefusal> Refusals { get; }
}

/// <summary>
/// The bounds every scan-coverage document is read inside, the one filter that answers a level's own rows, and the
/// one place a row's runtime refusal code is decided.
///
/// The coordinate bound is the fixed-placement family's own, because a route point is the same room-local point an
/// item or an enemy is placed at; the member bounds are the replicated state's own limits — four player slots and
/// eight required-item bits — because a scan whose members cannot be represented has to be refused instead of
/// published truncated.
///
/// <see cref="Refusal"/> is deliberately game-independent: it is the decision the native half reaches after it has
/// read the level, written once here so a diagnostic's code and the test that pins it cannot drift. The native
/// half reads the facts and calls this; it never decides a code itself.
/// </summary>
internal static class ScanCoverage
{
    /// <summary>The greatest room-local coordinate a route point may carry, in metres, matching the fixed
    /// placement family's own bound.</summary>
    public const double MaximumCoordinate = float.MaxValue;
    /// <summary>The native Single range. The reader also checks the scanner's squared-radius representation.</summary>
    public const double MaximumRadiusMeters = float.MaxValue;
    /// <summary>The greatest speed multiplier one row may write.</summary>
    public const double MaximumSpeedMultiplier = float.MaxValue;
    /// <summary>The greatest team-size scan multiplier one row may write.</summary>
    public const double MaximumTeamSpeedMultiplier = float.MaxValue;
    /// <summary>The code of a `teamSpeeds` row whose scanner carries no per-team-size speed table of the game's own
    /// length, so the factors cannot be multiplied in.</summary>
    public const string TeamSpeedsCode = "scan-coverage-team-speeds-unreadable";
    /// <summary>CP_BasicMovable.IsMoveConfigured requires more than one position (RVA 0x1861A60).</summary>
    public const int MinimumRoutePoints = 2;
    /// <summary>Rows one package document may carry.</summary>
    public const int MaximumRows = 512;
    /// <summary>The player slots the replicated bioscan state carries. A scan whose member set is larger cannot
    /// be published, so it is refused rather than truncated.</summary>
    public const int PlayerSlotLimit = 4;
    /// <summary>The required-item bits the replicated bioscan state carries, for the same reason.</summary>
    public const int RequiredItemLimit = 8;
    /// <summary>The greatest scan count one group may ask for; the native generation finds the room for them.</summary>
    public const int MaximumGroupPoints = 64;
    /// <summary>The longest ring label one row may write, matching the website's own limit.</summary>
    public const int MaximumTextLength = 200;

    /// <summary>The code of a row whose block id no runtime instance of this level carries.</summary>
    public const string UnresolvedCode = "scan-coverage-unresolved";
    /// <summary>The code of a row whose component ordinal is past the instance's own last component.</summary>
    public const string ComponentOutOfRangeCode = "scan-coverage-component-out-of-range";
    /// <summary>The code of a row whose component is not a bioscan core.</summary>
    public const string ComponentKindCode = "scan-coverage-component-kind";
    /// <summary>The code of a row that carries a route while its block id is carried by more than one instance.
    /// The website's export already refuses this; the runtime still fails closed rather than routing every copy of
    /// a shared block along one author's path.</summary>
    public const string RouteAmbiguousCode = "scan-coverage-route-ambiguous";
    /// <summary>The code of a row that carries a route on a scan that does not move.</summary>
    public const string RouteNotMovableCode = "scan-coverage-route-not-movable";
    /// <summary>The code of a row that carries a movement type on a scan that has no movement component to write.</summary>
    public const string MovementNotMovableCode = "scan-coverage-movement-not-movable";
    /// <summary>The code of a row written against an exit scan, whose compatibility is an explicit fail-closed
    /// gate until it is resolved.</summary>
    public const string ExitScanCode = "scan-coverage-exit-scan";

    /// <summary>The code of a row whose resolved component holds no player scanner, so its own distance gate and
    /// its ring cannot be written at all.</summary>
    public const string NoScannerCode = "scan-coverage-no-scanner";
    /// <summary>The code of a `radius` row whose core holds no bioscan graphics ring. The radius and the ring are
    /// written together, so a ring this build cannot scale refuses the row instead of leaving a ring that lies
    /// about the scan.</summary>
    public const string NoRingCode = "scan-coverage-no-ring";
    /// <summary>The code of a `room` row whose scan core carries no course node to own.</summary>
    public const string RoomUnresolvedCode = "scan-coverage-room-unresolved";
    /// <summary>The code of a `zone` row whose scan core's zone holds no readable course node.</summary>
    public const string ZoneUnresolvedCode = "scan-coverage-zone-unresolved";
    /// <summary>The code of a row whose owned members could not be read at all — a player table that does not
    /// read, or a required item whose own node cannot be read.</summary>
    public const string MemberUnreadableCode = "scan-coverage-member-unreadable";
    /// <summary>The code of a row whose member set is larger than the replicated state carries: more than four
    /// players or more than eight required items. The row is refused whole, never published truncated.</summary>
    public const string MemberLimitCode = "scan-coverage-member-limit";
    /// <summary>The code of a `room` or `zone` row whose owned node set carries no measurable area, so the host's
    /// coarse gate cannot be enlarged to keep the counting entry called.</summary>
    public const string GateUnresolvedCode = "scan-coverage-gate-unresolved";
    /// <summary>The code of a route row whose room-local point this level cannot compose into a world coordinate.</summary>
    public const string RoutePointCode = "scan-coverage-route-point-unresolved";
    /// <summary>The code of a `position` row whose room-local point this level cannot compose into a world coordinate.</summary>
    public const string PositionCode = "scan-coverage-position-unresolved";
    /// <summary>The code of a `points` row whose scan is not part of a group.</summary>
    public const string PointsNotGroupCode = "scan-coverage-points-not-group";
    /// <summary>The code of a required item this level holds no carryable fixed item for.</summary>
    public const string RequiredItemCode = "scan-coverage-required-item-unresolved";

    /// <summary>
    /// The one refusal a row's native resolution produces, or null when the row applies. The order is the order the
    /// row is read in — the block id, then the ordinal, then the component's kind, then the row-wide gates — so a
    /// diagnostic names the first thing that was wrong and never a later consequence of it.
    ///
    /// `instanceCount` is how many instances of this level carry the row's block id, and the other four facts are
    /// the instance currently being resolved. An exit scan is refused before the route gates because its
    /// completion is the expedition's own end, which no authored coverage may rewrite.
    /// </summary>
    public static string? Refusal(int instanceCount, int componentCount, int component, bool isBioscan,
        bool isExitScan, bool isMovable, bool hasRoute, bool hasMovement = false)
    {
        if (instanceCount <= 0) return UnresolvedCode;
        if (component < 0 || component >= componentCount) return ComponentOutOfRangeCode;
        if (!isBioscan) return ComponentKindCode;
        if (isExitScan) return ExitScanCode;
        if (hasRoute && instanceCount > 1) return RouteAmbiguousCode;
        if (hasRoute && !isMovable) return RouteNotMovableCode;
        if (hasMovement && !isMovable) return MovementNotMovableCode;
        return null;
    }

    /// <summary>The rows of one document that belong to one level, in the document's own order. The order is the
    /// document's because it is the same on every machine, which is what makes the applied sequence itself
    /// deterministic.</summary>
    public static IReadOnlyList<ScanCoverageRow> RowsForLevel(IReadOnlyList<ScanCoverageRow> rows, MapLevelReference level)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var owned = new List<ScanCoverageRow>();
        foreach (var row in rows) if (row.Level == level) owned.Add(row);
        return Array.AsReadOnly(owned.ToArray());
    }
}

/// <summary>
/// The one package document that carries a level's authored scan coverage:
/// `plugins/&lt;package&gt;/forge/scan-coverage.forge`, the same one-level package directory rule every authored
/// content file of this module uses. Each row is the ChainedPuzzle block and component ordinal that identify a
/// scan, the coverage that decides its members, and the optional movement route; the level each row belongs to is
/// the row's own, because one package document carries every level's rows exactly as the fixed-placement documents
/// do, and a block id is only unique inside one level.
///
/// The document is read row by row rather than all-or-nothing: a row this build cannot read is refused with its
/// own code and left out, because the rows of one level are independent of each other and an author fixing one row
/// must not have to fix every other row of the file first. The document's own shape — its version, its array and
/// the fields at its root — is still all-or-nothing, because a document whose root does not read has no rows to
/// keep. Reading the file off disk is the native half's job; this half is its one grammar.
/// </summary>
internal static class ScanCoverageManifest
{
    private const string FileName = "scan-coverage.forge";

    /// <summary>The document's one file name, which is what a package scan looks for.</summary>
    public static string FileNameOnly => FileName;

    /// <summary>
    /// I-MANIFEST: the Forge scan block fields this document carries and `ScanCoverageSession` applies. A row's
    /// `chainedPuzzleId` is the scan block it is written against (`Scope.AllowedPersistentIDs.ID`), its `zone`
    /// coverage is the zone-instead-of-room rule (`Scope.UseZoneInsteadOfNode`), its `teamSpeeds` are the per-team-size
    /// multipliers whose full-team cell is the old full-team multiplier (`TeamSize.PerTeamSizeScanMultis`), and its
    /// route's `speed` is the moving scan's speed factor (`MovingScan.TMoveSpeedMulti`), and its `movement` is
    /// the moving scan's movement type (`vanilla.scan.moving.MovementType`). Route points remain readable in
    /// existing documents, but the authoring catalog defers `MovingScan.TPositions` until its canvas control
    /// is projected, so it is not advertised as a released definition field.
    /// </summary>
    public static IReadOnlyList<string> DefinitionFields { get; } = Array.AsReadOnly(new[]
    {
        "map.forge.ecc.MovingScan.TMoveSpeedMulti",
        "map.forge.ecc.RequiredItems.ConcurrentCluster",
        "map.forge.ecc.RequiredItems.RequiredItemsIndices",
        "map.forge.ecc.ScanPosition.Position",
        "map.forge.scan.Scope.AllowedPersistentIDs.ID",
        "map.forge.scan.Scope.UseZoneInsteadOfNode",
        "map.forge.scan.TeamSize.PerTeamSizeScanMultis",
        "map.forge.vanilla.scan.bioscan.ReduceSpeed",
        "map.forge.vanilla.scan.moving.MovementType"
    });

    /// <summary>Reads one scan-coverage document. A false answer is the document's own refusal — unreadable JSON,
    /// another schema version, a root that is not this shape — and a true answer carries every row plus every
    /// row-level refusal, so a caller reports the refusals and applies the rest.</summary>
    public static bool TryParse(string text, out ScanCoverageDocument? document, out string? code, out string? reason)
    {
        document = null;
        using var parsed = Read(text, out code, out reason);
        if (parsed == null) return false;
        var rows = new List<ScanCoverageRow>();
        var refusals = new List<ScanCoverageRefusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in parsed.RootElement.GetProperty("scans").EnumerateArray())
        {
            index++;
            if (!TryRow(row, out var scan, out var rowCode, out var rowReason))
            {
                uint? puzzle = null; int? component = null; MapLevelReference? rowLevel = null;
                if (row.ValueKind == JsonValueKind.Object)
                {
                    if (DocumentReading.Text(row, "level", out var rowLevelText)) rowLevel = MapLevelReference.TryParse(rowLevelText);
                    if (row.TryGetProperty("chainedPuzzleId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out var nativeId)) puzzle = nativeId;
                    if (row.TryGetProperty("component", out var ordinal) && ordinal.ValueKind == JsonValueKind.Number && ordinal.TryGetInt32(out var nativeOrdinal)) component = nativeOrdinal;
                }
                var identity = puzzle is { } block
                    ? "chainedPuzzleId=" + block.ToString(CultureInfo.InvariantCulture)
                        + (component is { } componentIndex ? " component=" + componentIndex.ToString(CultureInfo.InvariantCulture) : "") + ": "
                    : component is { } loneComponent ? "component=" + loneComponent.ToString(CultureInfo.InvariantCulture) + ": " : "";
                refusals.Add(new ScanCoverageRefusal(rowCode!, Row(index, identity + rowReason!), puzzle, component, rowLevel));
                continue;
            }
            // §4.2.9 fixes the key as the block and the component inside one level: two rows sharing both would
            // apply two different coverages to one scan, and the later row is the one the author can see is extra.
            var key = scan!.Level.ToString() + "/" + scan.ChainedPuzzleId.ToString(CultureInfo.InvariantCulture) + "/"
                + scan.Component.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(key))
            {
                refusals.Add(new ScanCoverageRefusal("scan-coverage-duplicate",
                    Row(index, "`" + scan.Describe() + "` of level " + scan.Level
                        + " is claimed more than once; the later row is left out."), scan.ChainedPuzzleId, scan.Component, scan.Level));
                continue;
            }
            rows.Add(scan);
        }
        document = new ScanCoverageDocument(Array.AsReadOnly(rows.ToArray()), Array.AsReadOnly(refusals.ToArray()));
        return true;
    }

    /// <summary>Reads one document's root and its row array: the version, the one array field, and the cap every
    /// document of this family carries. Everything here is all-or-nothing, because a root that does not read has
    /// no rows whose own refusals could be told apart. The document is handed back to the caller, which reads the
    /// rows while it is alive and disposes it when it is done.</summary>
    private static JsonDocument? Read(string text, out string? code, out string? reason)
    {
        code = null;
        reason = null;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "scan-coverage-json"; reason = error.Message; return null; }
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        { code = "scan-coverage-json"; reason = "The document's root is not an object."; parsed.Dispose(); return null; }
        if (!DocumentReading.Known(root, out var unknown, "schemaVersion", "scans"))
        { code = "scan-coverage-field"; reason = "Unknown field `" + unknown + "`."; parsed.Dispose(); return null; }
        if (!FormatVersion.Matches(root))
        { code = "scan-coverage-schema"; reason = "The document must declare schemaVersion " + FormatVersion.Current + "."; parsed.Dispose(); return null; }
        if (!root.TryGetProperty("scans", out var rows) || rows.ValueKind != JsonValueKind.Array)
        { code = "scan-coverage-rows"; reason = "The document carries no `scans` array."; parsed.Dispose(); return null; }
        if (rows.GetArrayLength() > ScanCoverage.MaximumRows)
        {
            code = "scan-coverage-limit";
            reason = "The document carries " + rows.GetArrayLength() + " rows; the cap is " + ScanCoverage.MaximumRows + ".";
            parsed.Dispose();
            return null;
        }
        return parsed;
    }

    private static bool TryRow(JsonElement row, out ScanCoverageRow? scan, out string? code, out string? reason)
    {
        scan = null;
        code = "scan-coverage-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "A scan row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "level", "chainedPuzzleId", "component", "coverage", "radius", "route", "teamSpeeds", "movement", "players", "colors", "shape", "position", "points", "requiredItems", "reduceSpeed", "text"))
        { code = "scan-coverage-field"; reason = "Unknown scan field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "scan-coverage-level"; reason = "A scan needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!RuntimeJson.TryInteger(row, "chainedPuzzleId", 1, uint.MaxValue, out var puzzleId))
        { code = "scan-coverage-chained-puzzle"; reason = "A `chainedPuzzleId` is a native ChainedPuzzle block id: a whole number in [1, " + uint.MaxValue + "]."; return false; }
        if (!RuntimeJson.TryInteger(row, "component", 0, int.MaxValue, out var component))
        { code = "scan-coverage-component"; reason = "A component ordinal is a whole number in [0, " + int.MaxValue + "]."; return false; }
        if (!DocumentReading.Text(row, "coverage", 16, out var coverageText) || !TryMode(coverageText!, out var coverage))
        { code = "scan-coverage-coverage"; reason = "A coverage is `radius`, `room`, `zone` or `preset`."; return false; }
        var hasRadius = row.TryGetProperty("radius", out _);
        if (coverage == ScanCoverageMode.Radius && !hasRadius)
        { code = "scan-coverage-radius"; reason = "A `radius` coverage carries the radius in metres."; return false; }
        if (coverage != ScanCoverageMode.Radius && hasRadius)
        { code = "scan-coverage-radius"; reason = "A radius is written only for a `radius` coverage; a `room` or `zone` scan covers its own node set and a `preset` scan keeps its own circle."; return false; }
        double? radius = null;
        if (hasRadius)
        {
            if (!DocumentReading.Number(row, "radius", -ScanCoverage.MaximumRadiusMeters, ScanCoverage.MaximumRadiusMeters, out var metres)
                || !float.IsFinite((float)metres * (float)metres))
            { code = "scan-coverage-radius"; reason = "A radius and its native squared radius must be finite Singles."; return false; }
            radius = metres;
        }
        ScanRoute? route = null;
        if (row.TryGetProperty("route", out var routeElement) && !TryRoute(routeElement, out route, out reason))
        { code = "scan-coverage-route"; return false; }
        IReadOnlyList<double>? teamSpeeds = null;
        if (row.TryGetProperty("teamSpeeds", out _))
        {
            if (!DocumentReading.Numbers(row, "teamSpeeds", ScanCoverage.PlayerSlotLimit, -ScanCoverage.MaximumTeamSpeedMultiplier, ScanCoverage.MaximumTeamSpeedMultiplier, out var speeds))
            { code = "scan-coverage-team-speeds"; reason = "`teamSpeeds` carries " + ScanCoverage.PlayerSlotLimit + " finite Single factors, one per team size."; return false; }
            teamSpeeds = Array.AsReadOnly(speeds!);
        }
        ScanMovement? movement = null;
        if (row.TryGetProperty("movement", out _))
        {
            if (!DocumentReading.Text(row, "movement", 16, out var movementText) || !TryMovement(movementText!, out var parsedMovement))
            { code = "scan-coverage-movement"; reason = "A `movement` is `None`, `Directional` or `Circular`."; return false; }
            movement = parsedMovement;
        }
        ScanRoutePoint? position = null;
        if (row.TryGetProperty("position", out var positionElement) && !TryPoint(positionElement, "The position", out position, out reason))
        { code = "scan-coverage-position"; return false; }
        int? groupPoints = null;
        if (row.TryGetProperty("points", out _))
        {
            if (!RuntimeJson.TryInteger(row, "points", 1, ScanCoverage.MaximumGroupPoints, out var count))
            { code = "scan-coverage-points"; reason = "`points` is a whole number in [1, " + ScanCoverage.MaximumGroupPoints + "]."; return false; }
            groupPoints = (int)count;
        }
        IReadOnlyList<string>? requiredItems = null;
        if (row.TryGetProperty("requiredItems", out var itemsElement))
        {
            if (itemsElement.ValueKind != JsonValueKind.Array || itemsElement.GetArrayLength() is 0 or > ScanCoverage.RequiredItemLimit
                || itemsElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString())))
            { code = "scan-coverage-required-items"; reason = "`requiredItems` carries 1 to " + ScanCoverage.RequiredItemLimit + " item addresses."; return false; }
            requiredItems = Array.AsReadOnly(itemsElement.EnumerateArray().Select(item => item.GetString()!).ToArray());
        }
        double? reduceSpeed = null;
        if (row.TryGetProperty("reduceSpeed", out _))
        {
            if (!DocumentReading.Number(row, "reduceSpeed", 0, float.MaxValue, out var reduce))
            { code = "scan-coverage-reduce-speed"; reason = "`reduceSpeed` is a finite share per second, zero or more."; return false; }
            reduceSpeed = reduce;
        }
        string? text = null;
        if (row.TryGetProperty("text", out _) && !DocumentReading.Text(row, "text", ScanCoverage.MaximumTextLength, out text))
        { code = "scan-coverage-text"; reason = "`text` is one line of ring text, 1 to " + ScanCoverage.MaximumTextLength + " characters, not padded."; return false; }
        if (!ScanAppearanceReading.TryRead(row, coverage, radius, out var players, out var colors, out var shape, out code, out reason)) return false;
        scan = new ScanCoverageRow(level, (uint)puzzleId, (int)component, coverage, radius, route, teamSpeeds, movement, players, colors, shape,
            position, groupPoints, requiredItems, reduceSpeed, text);
        return true;
    }

    /// <summary>Reads one route: the points the scan travels through and the factor its own speed is scaled by.
    /// Every point is a room locator and a room-local point, so a route is resolved by the level and not by the
    /// document, and a point whose room this level cannot name refuses the row instead of placing the scan at a
    /// guessed coordinate.</summary>
    private static bool TryRoute(JsonElement route, out ScanRoute? parsed, out string? reason)
    {
        parsed = null;
        reason = null;
        if (route.ValueKind != JsonValueKind.Object) { reason = "A route is an object."; return false; }
        if (!DocumentReading.Known(route, out var unknown, "points", "speed"))
        { reason = "Unknown route field `" + unknown + "`."; return false; }
        if (!route.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
        { reason = "A route carries the points it travels through."; return false; }
        var count = points.GetArrayLength();
        if (count < ScanCoverage.MinimumRoutePoints)
        {
            reason = "A route carries at least " + ScanCoverage.MinimumRoutePoints
            + " points; this one carries " + count + "."; return false;
        }
        var parsedPoints = new List<ScanRoutePoint>(count);
        var index = 0;
        foreach (var point in points.EnumerateArray())
        {
            index++;
            if (!TryPoint(point, "Point " + index, out var parsedPoint, out reason)) return false;
            parsedPoints.Add(parsedPoint!);
        }
        if (!DocumentReading.Number(route, "speed", -ScanCoverage.MaximumSpeedMultiplier, ScanCoverage.MaximumSpeedMultiplier, out var speed))
        { reason = "A route's speed is a finite Single factor."; return false; }
        parsed = new ScanRoute(Array.AsReadOnly(parsedPoints.ToArray()), speed);
        return true;
    }

    /// <summary>One authored point: the room it stands in and its room-local position.</summary>
    private static bool TryPoint(JsonElement point, string name, out ScanRoutePoint? parsed, out string? reason)
    {
        parsed = null;
        reason = null;
        if (point.ValueKind != JsonValueKind.Object) { reason = name + " is an object."; return false; }
        if (!DocumentReading.Known(point, out var unknown, "locator", "p_u"))
        { reason = "Unknown point field `" + unknown + "`."; return false; }
        if (!point.TryGetProperty("locator", out var locatorElement))
        { reason = name + " carries the room it stands in; a room-local point without a room cannot be placed."; return false; }
        if (!RoomLocator.TryParse(locatorElement, out var locator, out _, out var locatorReason))
        { reason = name + ": " + locatorReason; return false; }
        if (!DocumentReading.Numbers(point, "p_u", 3, -ScanCoverage.MaximumCoordinate, ScanCoverage.MaximumCoordinate, out var position))
        { reason = name + " carries three finite metre coordinates in the room's prefab-root frame."; return false; }
        parsed = new ScanRoutePoint(locator!, position!);
        return true;
    }

    private static bool TryMode(string text, out ScanCoverageMode mode)
    {
        switch (text)
        {
            case "radius": mode = ScanCoverageMode.Radius; return true;
            case "room": mode = ScanCoverageMode.Room; return true;
            case "zone": mode = ScanCoverageMode.Zone; return true;
            case "preset": mode = ScanCoverageMode.Preset; return true;
            default: mode = ScanCoverageMode.Radius; return false;
        }
    }

    private static bool TryMovement(string text, out ScanMovement movement)
    {
        switch (text)
        {
            case "None": movement = ScanMovement.None; return true;
            case "Directional": movement = ScanMovement.Directional; return true;
            case "Circular": movement = ScanMovement.Circular; return true;
            default: movement = ScanMovement.None; return false;
        }
    }

    private static string Row(int index, string reason)
        => "row " + index.ToString(CultureInfo.InvariantCulture) + ": " + reason;
}
