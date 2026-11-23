using ForgeCore;
using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ForgeMap;

/// <summary>
/// What one authored wave anchor pins a wave's spawn position to, which is the one thing the anchor chooses: the
/// zone the wave spawns anywhere inside, one named area of that zone, one point of one room inside it, or one
/// vanilla world-event spawn-point name. Each is one branch of the game's own spawn-point scorer, so the kind is
/// a native spawn type and not a behaviour this layer interprets.
/// </summary>
internal enum WaveAnchorKind { Zone, Area, Point, Named }

/// <summary>One zone of the generated level an anchor is written against: the same dimension, layer and local
/// index every authored room locator and every map-object address already spells. The coordinates constrain an
/// authored Area receipt to the zone it was generated in.</summary>
internal readonly record struct WaveAnchorZone(int Dimension, int Layer, int LocalIndex);

/// <summary>The author identities of one generated Area. The room id is the stable MapRoom instance, while the
/// Area id is resolved through that room's locked generation receipt.</summary>
internal readonly record struct WaveAnchorArea(string RoomId, string AreaId);

/// <summary>
/// One authored anchor: the wave settings block it applies to, the level it belongs to, and what it pins the
/// wave's spawn position to. The settings id is the cloned `SurvivalWaveSettings.persistentID` the website wrote
/// and pointed the wave at, so the anchor is keyed by the block the native entry really loads — never by an
/// authored wave name. A point anchor carries the room locator and the room-local point of the fixed-placement
/// family, so an anchor is placed in the one coordinate grammar the rest of this module uses.
///
/// A row names exactly what its kind needs: a zone anchor names its zone, an area anchor its author room and Area,
/// and a point anchor its room and its point inside it. The zone is part of every row's identity
/// even where the locator already carries the same three coordinates, and the reader refuses a row whose two
/// spellings disagree rather than picking one.
/// </summary>
internal sealed class WaveAnchor
{
    internal WaveAnchor(uint settingsId, MapLevelReference level, WaveAnchorKind kind, WaveAnchorZone zone,
        WaveAnchorArea? area, RoomLocator? locator, IReadOnlyList<double>? position, string? name)
    {
        SettingsId = settingsId;
        Level = level;
        Kind = kind;
        Zone = zone;
        Area = area;
        Locator = locator;
        Position = position;
        Name = name;
    }

    /// <summary>The cloned wave settings block's own `persistentID`, which is the key the wave entry is read
    /// back with. It is a 16-bit id because that is what the wave object and its replication data carry.</summary>
    public uint SettingsId { get; }
    /// <summary>The level this row anchors. A row of another level is not this world's row.</summary>
    public MapLevelReference Level { get; }
    public WaveAnchorKind Kind { get; }
    /// <summary>The zone the anchor is searched in. A point row whose document carries no separate `zone` takes
    /// the locator's own coordinates, so the value is always the one zone the anchor belongs to.</summary>
    public WaveAnchorZone Zone { get; }
    /// <summary>The author room instance and Area id this row names, or null for another kind.</summary>
    public WaveAnchorArea? Area { get; }
    /// <summary>The room a point anchor stands in, or null when the kind is not `point`.</summary>
    public RoomLocator? Locator { get; }
    /// <summary>A point anchor's position in the room's prefab-root local coordinates, or null for another
    /// kind.</summary>
    public IReadOnlyList<double>? Position { get; }
    /// <summary>The exact vanilla world-event object key a named anchor filters, or null for another kind.</summary>
    public string? Name { get; }

    /// <summary>
    /// The authored point in the world: the room's own world pose composed with the point this row carries in
    /// the room's prefab-root frame. It is the same conversion a fixed item or a fixed enemy is placed by, so a
    /// point anchor lands where a placement at the same point would. A row of another kind carries no point and
    /// answers no position, which is a refusal at the caller and never a position at the origin.
    /// </summary>
    public double[]? WorldPosition(RoomPose room)
        => Position is { } local ? room.Point(local) : null;

    public override string ToString()
        => Level + "/wave:" + SettingsId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The anchors one document answered: the rows it could read, in the author's order, and the rows it
/// refused, in the same order.</summary>
internal sealed class WaveAnchorDocument
{
    internal WaveAnchorDocument(IReadOnlyList<WaveAnchor> anchors, IReadOnlyList<FixedPlacementRefusal> refusals)
    {
        Anchors = anchors;
        Refusals = refusals;
    }

    public IReadOnlyList<WaveAnchor> Anchors { get; }
    public IReadOnlyList<FixedPlacementRefusal> Refusals { get; }
}

/// <summary>
/// The package document that carries a level's wave anchors:
/// `plugins/&lt;package&gt;/forge/wave-anchors.forge`, the same one-level package directory rule the fixed
/// placement and trigger-zone documents use. A row is `settingsId`, `level`, `kind` and the fields that kind
/// needs, and the settings id is unique inside one level because it is the key the native wave entry is read
/// back with: two rows claiming one id would make the anchor ambiguous, and one of them would silently never be
/// used.
///
/// The document is read row by row rather than all-or-nothing: a row this build cannot read is refused with its
/// own code and left out, because the anchors of one level are independent of each other and an author fixing one
/// row must not have to fix every other row of the file first. The document's own shape — its version, its array
/// and the fields at its root — is still all-or-nothing, because a root that does not read has no rows to keep.
/// Reading the file off disk is the native half's job; this half is its one grammar.
/// </summary>
internal static class WaveAnchorManifest
{
    /// <summary>The greatest settings id a wave anchor may carry: the wave object and its replication data keep
    /// the settings id in 16 bits, so an id above this would be truncated into another block's.</summary>
    public const int MaximumSettingsId = 65535;
    /// <summary>The room-document Area identity limit; these are stable hierarchy paths and may contain `/`.</summary>
    public const int MaximumAreaIdChars = 2048;
    /// <summary>The game's FixedString64 has two length bytes and 62 UTF-8 payload bytes. Its constructor passes
    /// 0x3e to the encoder (RVA 0x11a6137); there is no reserved terminator byte.</summary>
    public const int MaximumNamedFilterBytes = 62;
    private const string Prefix = "wave-anchor";
    private const string FileName = "wave-anchors.forge";

    private static readonly string[] RowFields = { "settingsId", "level", "kind", "zone", "area", "point", "name" };

    /// <summary>The document's one file name, which is what a package scan looks for.</summary>
    public static string FileNameOnly => FileName;

    /// <summary>Reads one anchor document. A false answer is the document's own refusal — unreadable JSON,
    /// another schema version, a root that is not this shape — and a true answer carries every row plus every
    /// row-level refusal, so a caller reports the refusals and anchors the rest.</summary>
    public static bool TryParse(string text, out WaveAnchorDocument? document, out string? code, out string? reason)
    {
        document = null;
        code = null;
        reason = null;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(text); }
        catch (JsonException error) { code = Prefix + "-json"; reason = error.Message; return false; }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refuse(Prefix + "-json", "The document's root is not an object.", out code, out reason);
            if (!DocumentReading.Known(root, out var unknown, "schemaVersion", "anchors"))
                return Refuse(Prefix + "-field", "Unknown field `" + unknown + "`.", out code, out reason);
            if (!FormatVersion.Matches(root))
                return Refuse(Prefix + "-schema", "The document must declare schemaVersion " + FormatVersion.Current + ".", out code, out reason);
            if (!root.TryGetProperty("anchors", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return Refuse(Prefix + "-rows", "The document carries no `anchors` array.", out code, out reason);
            if (rows.GetArrayLength() > FixedPlacement.MaximumRows)
                return Refuse(Prefix + "-limit",
                    "The document carries " + rows.GetArrayLength() + " rows; the cap is " + FixedPlacement.MaximumRows + ".",
                    out code, out reason);
            var anchors = new List<WaveAnchor>();
            var refusals = new List<FixedPlacementRefusal>();
            var seen = new HashSet<(MapLevelReference Level, uint SettingsId)>();
            var index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                index++;
                if (!TryAnchor(row, out var anchor, out var rowCode, out var rowReason))
                {
                    refusals.Add(new FixedPlacementRefusal(rowCode!, Row(index, rowReason!)));
                    continue;
                }
                if (!seen.Add((anchor!.Level, anchor.SettingsId)))
                {
                    refusals.Add(new FixedPlacementRefusal(Prefix + "-duplicate", Row(index,
                        "Wave settings id `" + anchor.SettingsId + "` is already claimed for level `" + anchor.Level
                        + "`; the later row is left out.")));
                    continue;
                }
                anchors.Add(anchor);
            }
            document = new WaveAnchorDocument(Array.AsReadOnly(anchors.ToArray()), Array.AsReadOnly(refusals.ToArray()));
            return true;
        }
    }

    /// <summary>
    /// One anchor row. The kind decides which of the three field groups the row must carry and which it must
    /// not, so a row that carries an area on a zone anchor or a point on an area anchor is refused by name
    /// instead of being read as whichever field the reader happened to look at first. The zone is required for
    /// the two kinds that are searched in a zone; a point anchor's locator already names its own zone, and a row
    /// that writes both is accepted only while the two agree.
    /// </summary>
    private static bool TryAnchor(JsonElement row, out WaveAnchor? anchor, out string? code, out string? reason)
    {
        anchor = null;
        code = Prefix + "-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "An anchor row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, RowFields))
        { code = Prefix + "-field"; reason = "Unknown anchor field `" + unknown + "`."; return false; }
        if (!RuntimeJson.TryInteger(row, "settingsId", 1, MaximumSettingsId, out var settingsId))
        {
            code = Prefix + "-settings";
            reason = "A `settingsId` is the wave settings block's own id: a whole number in [1, " + MaximumSettingsId + "].";
            return false;
        }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = Prefix + "-level"; reason = "An anchor needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!DocumentReading.Text(row, "kind", 16, out var kindText) || !TryKind(kindText!, out var kind))
        { code = Prefix + "-kind"; reason = "A `kind` is `zone`, `area`, `point` or `named`."; return false; }

        var hasZone = row.TryGetProperty("zone", out _);
        var hasArea = row.TryGetProperty("area", out _);
        var hasPoint = row.TryGetProperty("point", out _);
        var hasName = row.TryGetProperty("name", out _);
        if ((kind == WaveAnchorKind.Zone || kind == WaveAnchorKind.Area) && !hasZone)
        {
            code = Prefix + "-zone";
            reason = "A `" + kindText + "` anchor needs the zone it is searched in as `{dimension, layer, localIndex}`.";
            return false;
        }
        WaveAnchorZone zone = default;
        if (hasZone && !TryZone(row, out zone, out reason)) { code = Prefix + "-zone"; return false; }

        WaveAnchorArea? area = null;
        RoomLocator? locator = null;
        IReadOnlyList<double>? position = null;
        string? name = null;
        switch (kind)
        {
            case WaveAnchorKind.Zone:
                if (hasArea || hasPoint || hasName)
                { code = Prefix + "-shape"; reason = "A `zone` anchor carries none of `area`, `point` or `name`."; return false; }
                break;
            case WaveAnchorKind.Area:
                if (hasPoint || hasName)
                { code = Prefix + "-shape"; reason = "An `area` anchor carries neither `point` nor `name`."; return false; }
                if (!TryArea(row, out var parsedArea, out reason)) { code = Prefix + "-area"; return false; }
                area = parsedArea;
                break;
            case WaveAnchorKind.Point:
                if (hasArea || hasName)
                { code = Prefix + "-shape"; reason = "A `point` anchor carries neither `area` nor `name`."; return false; }
                if (!TryPoint(row, out locator, out position, out reason)) { code = Prefix + "-point"; return false; }
                // The locator carries the same three coordinates the row's `zone` spells, so the two are one
                // fact written twice. A row whose copies disagree names no single zone, and picking either one
                // would place the wave somewhere the author did not write.
                if (hasZone && (locator!.Dimension != zone.Dimension || locator.Layer != zone.Layer
                    || locator.LocalIndex != zone.LocalIndex))
                {
                    code = Prefix + "-zone";
                    reason = "The row's `zone` and its `locator` name different zones; a point anchor has one zone.";
                    return false;
                }
                if (!hasZone) zone = new WaveAnchorZone(locator!.Dimension, locator.Layer, locator.LocalIndex);
                break;
            case WaveAnchorKind.Named:
                if (hasZone || hasArea || hasPoint)
                { code = Prefix + "-shape"; reason = "A `named` anchor carries only its vanilla spawn-point `name`."; return false; }
                if (!row.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                {
                    code = Prefix + "-name";
                    reason = "A `named` anchor needs the exact vanilla spawn-point `name`.";
                    return false;
                }
                name = nameElement.GetString();
                if (string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > MaximumNamedFilterBytes)
                {
                    code = Prefix + "-name";
                    reason = "A `named` anchor needs the exact vanilla spawn-point `name`, at most "
                        + MaximumNamedFilterBytes + " UTF-8 bytes.";
                    return false;
                }
                break;
        }
        anchor = new WaveAnchor((uint)settingsId, level, kind, zone, area, locator, position, name);
        return true;
    }

    /// <summary>The row's zone as the level's own three coordinates. All three are required: a zone addressed
    /// by two of them names no single zone, and a guessed coordinate would search the wrong one.</summary>
    private static bool TryZone(JsonElement row, out WaveAnchorZone zone, out string? reason)
    {
        zone = default;
        reason = null;
        if (!row.TryGetProperty("zone", out var element) || element.ValueKind != JsonValueKind.Object)
        { reason = "A `zone` is an object."; return false; }
        if (!DocumentReading.Known(element, out var unknown, "dimension", "layer", "localIndex"))
        { reason = "Unknown zone field `" + unknown + "`."; return false; }
        if (!DocumentReading.Coordinate(element, "dimension", 0, int.MaxValue, out var dimension)
            || !DocumentReading.Coordinate(element, "layer", 0, RoomLocator.MaximumLayer, out var layer)
            || !DocumentReading.Coordinate(element, "localIndex", 0, int.MaxValue, out var localIndex))
        { reason = "A `zone` carries `dimension`, `layer` and `localIndex`, all three."; return false; }
        zone = new WaveAnchorZone(dimension, layer, localIndex);
        return true;
    }

    /// <summary>The author room instance and Area id. Both remain author identities; native letters, display
    /// names and list positions cannot substitute for either one.</summary>
    private static bool TryArea(JsonElement row, out WaveAnchorArea area, out string? reason)
    {
        area = default;
        reason = null;
        if (!row.TryGetProperty("area", out var element) || element.ValueKind != JsonValueKind.Object)
        { reason = "An `area` is an object."; return false; }
        if (!DocumentReading.Known(element, out var unknown, "roomId", "areaId"))
        { reason = "Unknown area field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(element, "roomId", 64, out var roomId) || !DocumentReading.AuthorId(roomId!))
        { reason = "An `area` carries the authored room instance as `roomId`."; return false; }
        if (!DocumentReading.Text(element, "areaId", MaximumAreaIdChars, out var areaId))
        { reason = "An `area` carries the authored Area identity as `areaId`."; return false; }
        area = new WaveAnchorArea(roomId!, areaId!);
        return true;
    }

    /// <summary>The room and the room-local point a point anchor stands at, in the fixed-placement family's own
    /// locator grammar, so an authored point means the same thing wherever it is written.</summary>
    private static bool TryPoint(JsonElement row, out RoomLocator? locator, out IReadOnlyList<double>? position, out string? reason)
    {
        locator = null;
        position = null;
        reason = null;
        if (!row.TryGetProperty("point", out var element) || element.ValueKind != JsonValueKind.Object)
        { reason = "A `point` is an object."; return false; }
        if (!DocumentReading.Known(element, out var unknown, "locator", "p_u"))
        { reason = "Unknown point field `" + unknown + "`."; return false; }
        if (!element.TryGetProperty("locator", out var locatorRow))
        { reason = "A `point` carries the room it stands in as `locator`."; return false; }
        if (!RoomLocator.TryParse(locatorRow, out locator, out _, out var locatorReason))
        { reason = "A `point` carries the room it stands in: " + (locatorReason ?? "the locator does not read."); return false; }
        if (!DocumentReading.Numbers(element, "p_u", 3, -FixedPlacement.MaximumCoordinate, FixedPlacement.MaximumCoordinate,
            out var local))
        { reason = "A `point` carries `p_u`: three finite metre coordinates in the room's prefab-root frame."; return false; }
        position = local;
        return true;
    }

    private static bool TryKind(string text, out WaveAnchorKind kind)
    {
        switch (text)
        {
            case "zone": kind = WaveAnchorKind.Zone; return true;
            case "area": kind = WaveAnchorKind.Area; return true;
            case "point": kind = WaveAnchorKind.Point; return true;
            case "named": kind = WaveAnchorKind.Named; return true;
            default: kind = WaveAnchorKind.Zone; return false;
        }
    }

    private static bool Refuse(string refusal, string text, out string? code, out string? reason)
    {
        code = refusal;
        reason = text;
        return false;
    }

    private static string Row(int index, string reason)
        => "row " + index.ToString(CultureInfo.InvariantCulture) + ": " + reason;
}
