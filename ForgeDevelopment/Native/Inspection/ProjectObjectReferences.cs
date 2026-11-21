using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeDevelopment.Native;

internal enum ProjectScanStatus { NotRequested, Pending, Complete, Partial, Cancelled, Rejected }
internal enum ProjectSourceVerification { NotProvided, Pending, Matched, Mismatch, Unavailable, Cancelled }
internal enum ProjectReferenceStatus { Matched, Ambiguous, Missing, Inactive, Unverified }
internal enum ProjectReferenceReason
{
    Unique, MultipleCandidates, NoCandidate, LayoutInactive, ScanPartial, ScanCancelled,
    SourceMismatch, SourceUnavailable, SourcePending, UnsupportedDimension, ZoneUnresolved,
    CreationContextUnverified, AreaMappingIncomplete, ObservationBudgetExceeded, WorldEpochUnavailable
}

// These receipts are diagnostic identities, not executable EntityReference values.
public sealed record DiagnosticNativeObject
{
    public string Kind { get; }
    public int InstanceId { get; }

    public DiagnosticNativeObject(string kind, int instanceId)
    {
        if (kind is not ("zone" or "geomorph" or "area")) throw new ArgumentOutOfRangeException(nameof(kind));
        if (instanceId == 0) throw new ArgumentOutOfRangeException(nameof(instanceId));
        Kind = kind;
        InstanceId = instanceId;
    }
}

internal sealed record ProjectResourcePin(string Id, string Revision);
internal abstract record ProjectLocator;
internal sealed record ProjectZoneLocator(uint LayoutId, int Dimension, int Layer, int LocalIndex) : ProjectLocator;
/// <summary>One authored room's locator: the zone it belongs to in both spellings — the author's zone id and that
/// zone's native coordinates — plus the room definition and the native prefab it is generated from. The native
/// coordinates are required because the resolver searches one zone: the authored id alone is not a zone the level
/// can be asked about.</summary>
internal sealed record ProjectRoomLocator(string ZoneAuthorId, ProjectRoomScope Zone, ProjectResourcePin Room, string SourcePrefab,
    string? InstanceId = null) : ProjectLocator;
internal sealed record ProjectObjectDeclaration(string ExpeditionId, string AuthorId, ProjectLocator Locator)
{
    internal string Kind => Locator is ProjectZoneLocator ? "zone" : "room";
    internal object LocatorDocument => Locator switch
    {
        ProjectZoneLocator zone => new
        {
            kind = "zone",
            layoutId = zone.LayoutId,
            dimension = zone.Dimension,
            layer = zone.Layer,
            localIndex = zone.LocalIndex
        },
        ProjectRoomLocator { InstanceId: not null } room => new
        {
            kind = "authored-geomorph-in-zone",
            zoneAuthorId = room.ZoneAuthorId,
            dimension = room.Zone.Dimension,
            layer = room.Zone.Layer,
            localIndex = room.Zone.LocalZoneIndex,
            instanceId = room.InstanceId,
            room = new { id = room.Room.Id, revision = room.Room.Revision },
            sourcePrefab = room.SourcePrefab
        },
        ProjectRoomLocator room => new
        {
            kind = "unique-geomorph-in-zone",
            zoneAuthorId = room.ZoneAuthorId,
            dimension = room.Zone.Dimension,
            layer = room.Zone.Layer,
            localIndex = room.Zone.LocalZoneIndex,
            room = new { id = room.Room.Id, revision = room.Room.Revision },
            sourcePrefab = room.SourcePrefab
        },
        _ => throw new InvalidOperationException("Unsupported project locator.")
    };
}

internal sealed record ProjectLayoutKey(uint LayoutId, int Dimension, int Layer);
internal sealed record ProjectZoneCandidate(int InstanceId, uint LayoutId, int Dimension, int Layer, int LocalIndex)
{
    internal ProjectZoneLocator Locator => new(LayoutId, Dimension, Layer, LocalIndex);
    internal object Document => new
    {
        kind = "zone",
        instanceId = InstanceId,
        layoutId = LayoutId,
        dimension = Dimension,
        layer = Layer,
        localIndex = LocalIndex
    };
}

internal sealed record ProjectAreaCandidate(int InstanceId, int Uid);
// The areas one generated geomorph owns, observed where the inspection already walks the level. The
// geomorph's source identity is deliberately not observed here: which authored reference a geomorph answers is
// the one shared resolver's answer, and a copy of that rule kept by this scan would be a second answer to it.
internal sealed record ProjectGeomorphAreas(int InstanceId, int ZoneInstanceId, IReadOnlyList<ProjectAreaCandidate> Areas);
// One room the shared resolver named for an authored reference, as the candidate document the report contract
// carries to the site.
internal sealed record ProjectGeomorphCandidate(int InstanceId, int ZoneInstanceId, string SourcePrefab,
    IReadOnlyList<ProjectAreaCandidate> Areas)
{
    internal object Document(int maximumAreas) => new
    {
        kind = "geomorph",
        instanceId = InstanceId,
        zoneInstanceId = ZoneInstanceId,
        sourcePrefab = SourcePrefab,
        areas = Array.AsReadOnly(Areas.Take(maximumAreas).Select(area => new { instanceId = area.InstanceId, uid = area.Uid }).ToArray())
    };
}

/// <summary>One zone's three coordinates: the scope one authored room search runs in, and the same three numbers a
/// room's locator carries for the zone it belongs to.</summary>
internal readonly record struct ProjectRoomScope(int Dimension, int Layer, int LocalZoneIndex);

/// <summary>One generated room the level answered with: the geomorph the level built and the zone it stands
/// in. Native instance ids, so this scan can join them to the areas it observed itself.</summary>
internal readonly record struct ProjectRoomHit(int GeomorphInstanceId, int ZoneInstanceId);

/// <summary>
/// What one room question answered: the rooms the level holds for the reference inside the zone the locator names,
/// or the one reason the question could not be answered. A refusal is never a candidate to pick from, and it keeps
/// the resolver's own reason instead of being flattened into "unverified": no room, several rooms and a zone the
/// level does not have are three different things an author fixes differently.
/// </summary>
internal readonly record struct ProjectRoomAnswer(IReadOnlyList<ProjectRoomHit>? Rooms, ProjectReferenceReason? Refusal)
{
    internal static ProjectRoomAnswer Answered(IReadOnlyList<ProjectRoomHit> rooms) => new(rooms, null);
    internal static ProjectRoomAnswer Refused(ProjectReferenceReason reason) => new(null, reason);
}

/// <summary>
/// The one question this diagnostics scan asks about an authored room: which generated room does this complete
/// locator name inside its zone. A refusal is never a candidate to pick from.
/// </summary>
internal delegate ProjectRoomAnswer ProjectRoomResolver(long worldEpoch, ProjectRoomLocator locator);

internal sealed record ProjectReferenceOverflow(long DroppedNativeObjects, long DroppedCandidates, long DroppedAreas)
{
    internal object Document => new
    {
        droppedNativeObjects = DroppedNativeObjects,
        droppedCandidates = DroppedCandidates,
        droppedAreas = DroppedAreas
    };
}

internal sealed record ProjectReferenceGroup(ProjectObjectDeclaration Declaration, ProjectReferenceStatus Status,
    ProjectReferenceReason ReasonCode, int ObservedCandidateCount, bool CandidatesTruncated, IReadOnlyList<object> Candidates)
{
    internal object Document => new
    {
        expeditionId = Declaration.ExpeditionId,
        kind = Declaration.Kind,
        authorId = Declaration.AuthorId,
        locator = Declaration.LocatorDocument,
        status = ProjectObjectReferences.Wire(Status),
        reasonCode = ProjectObjectReferences.Wire(ReasonCode),
        observedCandidateCount = ObservedCandidateCount,
        candidatesTruncated = CandidatesTruncated,
        candidates = Candidates
    };
}

internal sealed record ProjectReferenceSnapshot(long WorldEpoch, long? SimulationTick, ProjectScanStatus ScanStatus,
    ProjectSourceVerification SourceVerification, IReadOnlyList<ProjectReferenceGroup> Groups, ProjectReferenceOverflow Overflow)
{
    internal object Document => new
    {
        worldEpoch = WorldEpoch,
        simulationTick = SimulationTick,
        scope = "generated-floor",
        scanStatus = ProjectObjectReferences.Wire(ScanStatus),
        sourceVerification = ProjectObjectReferences.Wire(SourceVerification),
        groups = Array.AsReadOnly(Groups.Select(group => group.Document).ToArray()),
        overflow = Overflow.Document
    };
}

internal static class ProjectObjectReferences
{
    internal const string ProjectFormat = "gtfo-forge-project";
    internal const string ReportFormat = "gtfo-forge-diagnostics-report";
    internal const int MaximumDeclarations = 4096, MaximumExpeditions = 256;
    internal const int MaximumZonesPerExpedition = 128, MaximumRoomsPerExpedition = 512, MaximumActiveLayouts = 128;
    internal const int MaximumCandidatesPerGroup = 2, MaximumCandidates = 8192;
    internal const int MaximumAreas = 16384, MaximumNativeObjects = 32768;
    // The project manifest and sampled sources keep the 256 MiB bound of Forge content reads.
    internal const int MaximumManifestBytes = 256 * 1024 * 1024, MaximumReportBytes = 16 * 1024 * 1024;
    internal const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Wire(Enum value)
    {
        if (!Enum.IsDefined(value.GetType(), value)) throw new ArgumentOutOfRangeException(nameof(value));
        var name = value.ToString();
        var result = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) result.Append('_');
            result.Append(char.ToLowerInvariant(name[i]));
        }
        return result.ToString();
    }

    internal static void RequireObject(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object.");
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException($"Unknown or duplicate field: {property.Name}.");
        if (remaining.Count != 0) throw new InvalidDataException($"Missing field: {remaining.First()}.");
    }

    internal static string ReadText(JsonElement value, int maximumChars, int maximumUtf8Bytes)
    {
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected a string.");
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > maximumChars || text.Any(char.IsControl))
            throw new InvalidDataException("String is empty, too long, or contains control characters.");
        try
        {
            if (StrictUtf8.GetByteCount(text) > maximumUtf8Bytes) throw new InvalidDataException("String byte budget exceeded.");
        }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid Unicode string.", error); }
        return text;
    }

    internal static string ReadAuthorId(JsonElement value)
    {
        var id = ReadText(value, 64, 64);
        if (!AsciiAlphanumeric(id[0]) || id.Any(character => !AsciiAlphanumeric(character) && character is not ('.' or '_' or ':' or '-')))
            throw new InvalidDataException("Invalid author ID.");
        return id;
    }

    internal static string ReadSha256(JsonElement value)
    {
        var hash = ReadText(value, 64, 64);
        if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Expected a lowercase SHA-256 value.");
        return hash;
    }

    internal static string ReadSourcePrefab(JsonElement value) => ValidateSourcePrefab(ReadText(value, 1024, 4096));

    internal static string ValidateSourcePrefab(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Any(char.IsControl) || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Expected a complete, unchanged native Assets/ prefab identity.");
        try
        {
            if (StrictUtf8.GetByteCount(path) > 4096) throw new InvalidDataException("Prefab byte budget exceeded.");
        }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid prefab Unicode.", error); }
        return path;
    }

    internal static long ReadInteger(JsonElement value, long minimum, long maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number != Math.Truncate(number) || number < minimum || number > maximum)
            throw new InvalidDataException("Integer is outside its permitted range.");
        return checked((long)number);
    }

    internal static void ValidateEpoch(long worldEpoch, long? simulationTick)
    {
        if (worldEpoch < 0 || worldEpoch > MaximumSafeInteger) throw new ArgumentOutOfRangeException(nameof(worldEpoch));
        if (simulationTick.HasValue && (simulationTick.Value < 0 || simulationTick.Value > MaximumSafeInteger))
            throw new ArgumentOutOfRangeException(nameof(simulationTick));
    }

    internal static IReadOnlyList<ProjectObjectDeclaration> Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumDeclarations)
            throw new InvalidDataException("Invalid objectReferences array or declaration budget exceeded.");
        var result = new List<ProjectObjectDeclaration>(value.GetArrayLength());
        var identities = new HashSet<(string Expedition, string Kind, string Author)>();
        var zoneTargets = new HashSet<(string Expedition, ProjectZoneLocator Zone)>();
        var declaredZones = new Dictionary<(string Expedition, string Author), ProjectZoneLocator>();
        var counts = new Dictionary<string, (int Zones, int Rooms)>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateArray())
        {
            RequireObject(entry, "expeditionId", "kind", "authorId", "locator");
            var expedition = ReadAuthorId(entry.GetProperty("expeditionId"));
            var author = ReadAuthorId(entry.GetProperty("authorId"));
            var kind = ReadText(entry.GetProperty("kind"), 4, 4);
            var locator = entry.GetProperty("locator");
            ProjectLocator parsed;
            if (kind == "zone")
            {
                RequireObject(locator, "kind", "layoutId", "dimension", "layer", "localIndex");
                if (ReadText(locator.GetProperty("kind"), 4, 4) != "zone") throw new InvalidDataException("Zone locator kind mismatch.");
                var zone = new ProjectZoneLocator((uint)ReadInteger(locator.GetProperty("layoutId"), 1, uint.MaxValue),
                    (int)ReadInteger(locator.GetProperty("dimension"), 0, int.MaxValue),
                    (int)ReadInteger(locator.GetProperty("layer"), 0, 2),
                    (int)ReadInteger(locator.GetProperty("localIndex"), 0, int.MaxValue));
                if (!zoneTargets.Add((expedition, zone))) throw new InvalidDataException("Native zone tuple has more than one author in the expedition.");
                declaredZones[(expedition, author)] = zone;
                parsed = zone;
            }
            else if (kind == "room")
            {
                var locatorKind = ReadText(locator.GetProperty("kind"), 32, 32);
                if (locatorKind is not ("unique-geomorph-in-zone" or "authored-geomorph-in-zone"))
                    throw new InvalidDataException("Unsupported room locator.");
                var authored = locatorKind == "authored-geomorph-in-zone";
                if (authored) RequireObject(locator, "kind", "zoneAuthorId", "dimension", "layer", "localIndex", "instanceId", "room", "sourcePrefab");
                else RequireObject(locator, "kind", "zoneAuthorId", "dimension", "layer", "localIndex", "room", "sourcePrefab");
                var instanceId = authored ? ReadAuthorId(locator.GetProperty("instanceId")) : null;
                if (authored && instanceId != author) throw new InvalidDataException("The room instance must equal its author identity.");
                var pin = locator.GetProperty("room");
                RequireObject(pin, "id", "revision");
                var id = ReadText(pin.GetProperty("id"), 512, 2048);
                if (id.Any(character => char.IsWhiteSpace(character) || character is '/' or '\\'))
                    throw new InvalidDataException("Invalid room resource ID.");
                var room = new ProjectRoomLocator(ReadAuthorId(locator.GetProperty("zoneAuthorId")),
                    new ProjectRoomScope((int)ReadInteger(locator.GetProperty("dimension"), 0, int.MaxValue),
                        (int)ReadInteger(locator.GetProperty("layer"), 0, 2),
                        (int)ReadInteger(locator.GetProperty("localIndex"), 0, int.MaxValue)),
                    new ProjectResourcePin(id, ReadSha256(pin.GetProperty("revision"))), ReadSourcePrefab(locator.GetProperty("sourcePrefab")), instanceId);
                parsed = room;
            }
            else throw new InvalidDataException("Unsupported object reference kind.");
            if (!identities.Add((expedition, kind, author))) throw new InvalidDataException("Duplicate author identity.");
            counts.TryGetValue(expedition, out var count);
            count = kind == "zone" ? (count.Zones + 1, count.Rooms) : (count.Zones, count.Rooms + 1);
            counts[expedition] = count;
            if (counts.Count > MaximumExpeditions || count.Zones > MaximumZonesPerExpedition || count.Rooms > MaximumRoomsPerExpedition)
                throw new InvalidDataException("Expedition object reference budget exceeded.");
            result.Add(new ProjectObjectDeclaration(expedition, author, parsed));
        }
        foreach (var declaration in result)
            if (declaration.Locator is ProjectRoomLocator room)
            {
                if (!declaredZones.TryGetValue((declaration.ExpeditionId, room.ZoneAuthorId), out var zone))
                    throw new InvalidDataException("Room zoneAuthorId must name a declared zone in the same expedition.");
                // 房间引用带的原生定位必须就是那条区域引用自己的定位：两处说的是同一件事，写两份就必须相等。
                if (zone.Dimension != room.Zone.Dimension || zone.Layer != room.Zone.Layer || zone.LocalIndex != room.Zone.LocalZoneIndex)
                    throw new InvalidDataException("A room locator must carry the native coordinates of its declared zone.");
            }
        return result.AsReadOnly();
    }

    private static bool AsciiAlphanumeric(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}

internal sealed class ProjectObjectReferenceScan
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<ProjectObjectDeclaration> _declarations;
    private readonly Dictionary<(string Expedition, string Author), ProjectZoneLocator> _declaredZones;
    private readonly long _worldEpoch;
    private readonly Dictionary<int, ProjectZoneCandidate> _zones = new();
    private readonly Dictionary<int, ProjectGeomorphAreas> _geomorphs = new();
    private readonly ProjectRoomResolver? _rooms;
    private readonly Dictionary<int, (string Kind, int Owner)> _nativeObjects = new();
    private readonly HashSet<ProjectLayoutKey> _activeLayouts = new();
    private readonly HashSet<int> _supportedDimensions = new();
    private ProjectScanStatus _status;
    private ProjectSourceVerification _sourceVerification;
    private long? _simulationTick;
    private bool _started, _observationFault, _budgetExceeded;
    private int _areaCount;
    private long _droppedNativeObjects, _droppedAreas;
    // Counts every call that can change the snapshot. The report that carries this scan compares it between
    // exports, so a scan that moved is exported again and one that did not is not.
    private long _revision;
    internal long Revision { get { lock (_gate) return _revision; } }

    /// <param name="rooms">The one room resolver installed in this build, or null when this build installed
    /// none. A scan without one refuses every authored room instead of matching a prefab name against a
    /// second copy of the identity rule that lives with the generated level.</param>
    internal ProjectObjectReferenceScan(IReadOnlyList<ProjectObjectDeclaration> declarations, long worldEpoch,
        long? simulationTick, ProjectSourceVerification sourceVerification, ProjectRoomResolver? rooms = null)
    {
        ProjectObjectReferences.ValidateEpoch(worldEpoch, simulationTick);
        if (!Enum.IsDefined(typeof(ProjectSourceVerification), sourceVerification))
            throw new ArgumentOutOfRangeException(nameof(sourceVerification));
        if (declarations.Count > ProjectObjectReferences.MaximumDeclarations) throw new ArgumentOutOfRangeException(nameof(declarations));
        _declarations = Array.AsReadOnly(declarations.ToArray());
        _declaredZones = _declarations.Where(declaration => declaration.Locator is ProjectZoneLocator)
            .ToDictionary(declaration => (declaration.ExpeditionId, declaration.AuthorId), declaration => (ProjectZoneLocator)declaration.Locator);
        _worldEpoch = worldEpoch;
        _simulationTick = simulationTick;
        _sourceVerification = sourceVerification;
        _rooms = rooms;
        _status = worldEpoch == 0 ? ProjectScanStatus.Rejected : ProjectScanStatus.NotRequested;
    }

    internal void SetSourceVerification(ProjectSourceVerification value)
    {
        if (!Enum.IsDefined(typeof(ProjectSourceVerification), value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_gate)
        {
            _revision++;
            if (_status is ProjectScanStatus.Cancelled or ProjectScanStatus.Rejected) return;
            // A late hash worker cannot revive a cancelled or terminal source result.
            if (_sourceVerification == ProjectSourceVerification.NotProvided && value == ProjectSourceVerification.Pending)
                _sourceVerification = value;
            else if (_sourceVerification == ProjectSourceVerification.Pending && value != ProjectSourceVerification.NotProvided)
                _sourceVerification = value;
        }
    }

    internal void Start(IReadOnlyList<ProjectLayoutKey> activeLayouts, long? simulationTick)
    {
        ProjectObjectReferences.ValidateEpoch(_worldEpoch, simulationTick);
        lock (_gate)
        {
            _revision++;
            if (_started || _status is ProjectScanStatus.Cancelled or ProjectScanStatus.Rejected) return;
            _started = true;
            UpdateTick(simulationTick);
            if (activeLayouts.Count > ProjectObjectReferences.MaximumActiveLayouts)
            {
                _budgetExceeded = true;
                _status = ProjectScanStatus.Rejected;
                return;
            }
            foreach (var layout in activeLayouts)
            {
                if (layout.LayoutId == 0 || layout.Dimension < 0 || layout.Layer is < 0 or > 2)
                {
                    _observationFault = true;
                    _status = ProjectScanStatus.Rejected;
                    return;
                }
                _activeLayouts.Add(layout);
                _supportedDimensions.Add(layout.Dimension);
            }
            _status = ProjectScanStatus.Pending;
        }
    }

    internal void ObserveZone(ProjectZoneCandidate zone)
    {
        lock (_gate)
        {
            _revision++;
            if (_status != ProjectScanStatus.Pending) return;
            if (zone.InstanceId == 0 || zone.LayoutId == 0 || zone.Dimension < 0 || zone.Layer is < 0 or > 2 || zone.LocalIndex < 0)
            { _observationFault = true; return; }
            if (_zones.TryGetValue(zone.InstanceId, out var existing))
            { if (existing != zone) _observationFault = true; return; }
            if (!AddNative(zone.InstanceId, "zone", 0)) return;
            _zones.Add(zone.InstanceId, zone);
        }
    }

    /// <summary>One generated geomorph's own areas, as the inspection walked them inside its zone. The witness
    /// itself is not observed as a candidate here: which authored reference names it is the one room resolver's
    /// answer, and this scan only supplies the areas a report names the answer by.</summary>
    internal void ObserveAreas(ProjectGeomorphAreas observation)
    {
        lock (_gate)
        {
            _revision++;
            if (_status != ProjectScanStatus.Pending) return;
            if (observation.InstanceId == 0 || observation.ZoneInstanceId == 0 || observation.Areas == null)
            { _observationFault = true; return; }
            // Inspect Count before enumeration, so one malformed giant collection is bounded.
            if (observation.Areas.Count > ProjectObjectReferences.MaximumAreas)
            {
                Add(ref _droppedAreas, observation.Areas.Count);
                _budgetExceeded = true;
                return;
            }
            var uniqueAreas = new Dictionary<int, ProjectAreaCandidate>();
            foreach (var area in observation.Areas)
            {
                if (area == null || area.InstanceId == 0) { _observationFault = true; continue; }
                if (uniqueAreas.TryGetValue(area.InstanceId, out var oldArea))
                { if (oldArea != area) _observationFault = true; }
                else uniqueAreas.Add(area.InstanceId, area);
            }
            var areas = uniqueAreas.Values.OrderBy(area => area.InstanceId).ToArray();
            if (_geomorphs.TryGetValue(observation.InstanceId, out var existing))
            {
                if (existing.ZoneInstanceId != observation.ZoneInstanceId || !existing.Areas.SequenceEqual(areas))
                    _observationFault = true;
                return;
            }
            if (!AddNative(observation.InstanceId, "geomorph", observation.ZoneInstanceId)) return;
            var accepted = new List<ProjectAreaCandidate>(areas.Length);
            if (areas.Length > ProjectObjectReferences.MaximumAreas - _areaCount)
            {
                Add(ref _droppedAreas, areas.Length);
                _budgetExceeded = true;
            }
            else
            {
                foreach (var area in areas)
                {
                    if (AddNative(area.InstanceId, "area", observation.InstanceId)) { accepted.Add(area); _areaCount++; }
                    else Add(ref _droppedAreas, 1);
                }
            }
            _geomorphs.Add(observation.InstanceId, new ProjectGeomorphAreas(observation.InstanceId,
                observation.ZoneInstanceId, accepted.AsReadOnly()));
        }
    }

    internal void MarkPartial()
    {
        lock (_gate)
        {
            _revision++;
            if (_status == ProjectScanStatus.Pending) _observationFault = true;
        }
    }

    internal void Complete(long? simulationTick)
    {
        ProjectObjectReferences.ValidateEpoch(_worldEpoch, simulationTick);
        lock (_gate)
        {
            _revision++;
            if (_status != ProjectScanStatus.Pending) return;
            UpdateTick(simulationTick);
            if (_geomorphs.Values.Any(geomorph => !_zones.ContainsKey(geomorph.ZoneInstanceId))) _observationFault = true;
            _status = _observationFault || _budgetExceeded ? ProjectScanStatus.Partial : ProjectScanStatus.Complete;
        }
    }

    internal void Cancel(long? simulationTick)
    {
        ProjectObjectReferences.ValidateEpoch(_worldEpoch, simulationTick);
        lock (_gate)
        {
            _revision++;
            if (_status is ProjectScanStatus.Cancelled or ProjectScanStatus.Rejected) return;
            UpdateTick(simulationTick);
            _started = true;
            _status = ProjectScanStatus.Cancelled;
            if (_sourceVerification == ProjectSourceVerification.Pending) _sourceVerification = ProjectSourceVerification.Cancelled;
        }
    }

    internal void Reject()
    {
        lock (_gate)
        {
            _revision++;
            if (_started || _status == ProjectScanStatus.Cancelled) return;
            _started = true;
            _status = ProjectScanStatus.Rejected;
            if (_sourceVerification == ProjectSourceVerification.Pending) _sourceVerification = ProjectSourceVerification.Unavailable;
        }
    }

    internal ProjectReferenceSnapshot Snapshot()
    {
        lock (_gate)
        {
            // Index once. Matching does not rescan all native objects for every declaration.
            var zones = _zones.Values.OrderBy(zone => zone.InstanceId).GroupBy(zone => zone.Locator)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var restriction = Restriction();
            // Layout IDs alone do not identify an expedition when authors reuse them.
            // Require the complete declared layout-key set, never a matching subset.
            var activeExpeditions = _declaredZones.GroupBy(pair => pair.Key.Expedition)
                .Where(group => group.Select(pair => new ProjectLayoutKey(pair.Value.LayoutId,
                    pair.Value.Dimension, pair.Value.Layer)).ToHashSet().SetEquals(_activeLayouts))
                .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);

            Resolution ResolveZone(ProjectZoneLocator locator)
            {
                var candidates = zones.GetValueOrDefault(locator) ?? Array.Empty<ProjectZoneCandidate>();
                var examples = candidates.Take(ProjectObjectReferences.MaximumCandidatesPerGroup).Select(candidate => (object)candidate).ToArray();
                if (restriction.HasValue) return new(ProjectReferenceStatus.Unverified, restriction.Value, candidates.Length, examples);
                if (!_supportedDimensions.Contains(locator.Dimension))
                    return new(ProjectReferenceStatus.Unverified, ProjectReferenceReason.UnsupportedDimension, candidates.Length, examples);
                if (!_activeLayouts.Contains(new ProjectLayoutKey(locator.LayoutId, locator.Dimension, locator.Layer)))
                    return new(ProjectReferenceStatus.Inactive, ProjectReferenceReason.LayoutInactive, candidates.Length, examples);
                return ByCount(candidates.Length, examples);
            }

            Resolution ResolveRoom(ProjectObjectDeclaration declaration, ProjectRoomLocator locator)
            {
                var zoneLocator = _declaredZones[(declaration.ExpeditionId, locator.ZoneAuthorId)];
                var parent = ResolveZone(zoneLocator);
                var parentCandidates = zones.GetValueOrDefault(zoneLocator) ?? Array.Empty<ProjectZoneCandidate>();
                if (restriction.HasValue) return new(ProjectReferenceStatus.Unverified, restriction.Value, 0, Array.Empty<object>());
                if (parent.Status == ProjectReferenceStatus.Inactive)
                    return new(ProjectReferenceStatus.Inactive, ProjectReferenceReason.LayoutInactive, 0, Array.Empty<object>());
                if (parent.Status != ProjectReferenceStatus.Matched)
                    return new(ProjectReferenceStatus.Unverified,
                        parent.Reason == ProjectReferenceReason.UnsupportedDimension ? parent.Reason : ProjectReferenceReason.ZoneUnresolved,
                        0, Array.Empty<object>());
                // One resolver answers which generated room this authored reference names, asked in the zone the
                // locator itself carries — the same coordinates its declared zone reference carries, which the
                // parser already required them to equal. The resolver's own refusal reason is kept: a zone the
                // level does not have, a zone with no such room and a build with no resolver are different things.
                var zone = parentCandidates[0];
                ProjectRoomAnswer answer;
                try
                {
                    answer = _rooms?.Invoke(_worldEpoch, locator)
                        ?? ProjectRoomAnswer.Refused(ProjectReferenceReason.CreationContextUnverified);
                }
                catch (Exception) { answer = ProjectRoomAnswer.Refused(ProjectReferenceReason.CreationContextUnverified); } // The explicit named refusal CreationContextUnverified is the result.
                // A refusal that observed no room is that refusal. A refusal that observed several — the same
                // prefab placed twice in the zone — carries them, and the cardinality rule below turns them into
                // the ambiguous group an author reads the candidates from.
                if (answer.Rooms is not { } found)
                    return new(ProjectReferenceStatus.Unverified, answer.Refusal!.Value, 0, Array.Empty<object>());
                var names = found.OrderBy(hit => hit.GeomorphInstanceId).ToArray();
                var candidates = new ProjectGeomorphCandidate[names.Length];
                var ownZone = true;
                for (var i = 0; i != names.Length; i++)
                {
                    if (names[i].ZoneInstanceId != zone.InstanceId) ownZone = false;
                    candidates[i] = new ProjectGeomorphCandidate(names[i].GeomorphInstanceId, names[i].ZoneInstanceId,
                        locator.SourcePrefab, _geomorphs.TryGetValue(names[i].GeomorphInstanceId, out var observed)
                            ? observed.Areas : Array.Empty<ProjectAreaCandidate>());
                }
                var examples = candidates.Take(ProjectObjectReferences.MaximumCandidatesPerGroup).Select(candidate => (object)candidate).ToArray();
                // A room the reference claims for one zone but the level built in another is not that zone's room.
                if (!ownZone) return new(ProjectReferenceStatus.Unverified, ProjectReferenceReason.ZoneUnresolved, candidates.Length, examples);
                if (candidates.Length == 1 && candidates[0].Areas.Count == 0)
                    return new(ProjectReferenceStatus.Unverified, ProjectReferenceReason.AreaMappingIncomplete, 1, examples);
                return ByCount(candidates.Length, examples);
            }

            var groups = new List<ProjectReferenceGroup>(_declarations.Count);
            var remainingCandidates = ProjectObjectReferences.MaximumCandidates;
            var remainingAreas = ProjectObjectReferences.MaximumAreas;
            long droppedCandidates = 0;
            var droppedAreas = _droppedAreas;
            foreach (var declaration in _declarations)
            {
                Resolution result;
                if (!restriction.HasValue && !activeExpeditions.Contains(declaration.ExpeditionId))
                    result = new(ProjectReferenceStatus.Inactive, ProjectReferenceReason.LayoutInactive, 0, Array.Empty<object>());
                else if (!restriction.HasValue && activeExpeditions.Count > 1)
                    result = new(ProjectReferenceStatus.Unverified, ProjectReferenceReason.ZoneUnresolved, 0, Array.Empty<object>());
                else
                    result = declaration.Locator is ProjectZoneLocator zone ? ResolveZone(zone) : ResolveRoom(declaration, (ProjectRoomLocator)declaration.Locator);
                var examples = result.Examples.Take(remainingCandidates).ToArray();
                remainingCandidates -= examples.Length;
                if (examples.Length != result.Examples.Length)
                {
                    Add(ref droppedCandidates, result.Examples.Length - examples.Length);
                    result = result with { Status = ProjectReferenceStatus.Unverified, Reason = ProjectReferenceReason.ObservationBudgetExceeded };
                }
                var documents = new List<object>(examples.Length);
                foreach (var example in examples)
                {
                    if (example is ProjectZoneCandidate zoneCandidate) documents.Add(zoneCandidate.Document);
                    else if (example is ProjectGeomorphCandidate geomorph)
                    {
                        var areaCount = Math.Min(remainingAreas, geomorph.Areas.Count);
                        remainingAreas -= areaCount;
                        documents.Add(geomorph.Document(areaCount));
                        if (areaCount != geomorph.Areas.Count)
                        {
                            Add(ref droppedAreas, geomorph.Areas.Count - areaCount);
                            result = result with { Status = ProjectReferenceStatus.Unverified, Reason = ProjectReferenceReason.ObservationBudgetExceeded };
                        }
                    }
                    else throw new InvalidOperationException("Unexpected native candidate type.");
                }
                groups.Add(new ProjectReferenceGroup(declaration, result.Status, result.Reason, result.Count,
                    result.Count > examples.Length, documents.AsReadOnly()));
            }
            var snapshotStatus = _status == ProjectScanStatus.Complete && (droppedCandidates > 0 || droppedAreas > _droppedAreas)
                ? ProjectScanStatus.Partial : _status;
            return new ProjectReferenceSnapshot(_worldEpoch, _simulationTick, snapshotStatus, _sourceVerification, groups.AsReadOnly(),
                new ProjectReferenceOverflow(_droppedNativeObjects, droppedCandidates, droppedAreas));
        }
    }

    private ProjectReferenceReason? Restriction()
    {
        if (_worldEpoch == 0) return ProjectReferenceReason.WorldEpochUnavailable;
        if (_status == ProjectScanStatus.Cancelled) return ProjectReferenceReason.ScanCancelled;
        if (_budgetExceeded) return ProjectReferenceReason.ObservationBudgetExceeded;
        if (_sourceVerification == ProjectSourceVerification.Mismatch) return ProjectReferenceReason.SourceMismatch;
        if (_sourceVerification == ProjectSourceVerification.Pending) return ProjectReferenceReason.SourcePending;
        if (_sourceVerification != ProjectSourceVerification.Matched) return ProjectReferenceReason.SourceUnavailable;
        if (_status != ProjectScanStatus.Complete || _observationFault) return ProjectReferenceReason.ScanPartial;
        return null;
    }

    private bool AddNative(int instanceId, string kind, int owner)
    {
        if (_nativeObjects.TryGetValue(instanceId, out var existing))
        {
            if (existing != (kind, owner)) { _observationFault = true; return false; }
            return true;
        }
        if (_nativeObjects.Count >= ProjectObjectReferences.MaximumNativeObjects)
        {
            Add(ref _droppedNativeObjects, 1);
            _budgetExceeded = true;
            return false;
        }
        _nativeObjects.Add(instanceId, (kind, owner));
        return true;
    }

    private void UpdateTick(long? tick)
    {
        if (tick.HasValue && _simulationTick.HasValue && tick.Value < _simulationTick.Value) _observationFault = true;
        _simulationTick = tick;
    }

    private static Resolution ByCount(int count, object[] examples) => count switch
    {
        0 => new(ProjectReferenceStatus.Missing, ProjectReferenceReason.NoCandidate, count, examples),
        1 => new(ProjectReferenceStatus.Matched, ProjectReferenceReason.Unique, count, examples),
        _ => new(ProjectReferenceStatus.Ambiguous, ProjectReferenceReason.MultipleCandidates, count, examples)
    };

    private static void Add(ref long value, long count) => value = Math.Min(ProjectObjectReferences.MaximumSafeInteger,
        value + Math.Min(Math.Max(0, count), ProjectObjectReferences.MaximumSafeInteger - value));

    private sealed record Resolution(ProjectReferenceStatus Status, ProjectReferenceReason Reason, int Count, object[] Examples);
}
