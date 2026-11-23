using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Uses the existing Runtime entity wire contract; never normalizes an ID.</summary>
internal static class RuntimeEntityReferences
{
    public static EntityReference Validate(EntityReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RuntimeJson.Text(reference.Id);
        RuntimeJson.Integer(reference.WorldEpoch); RuntimeJson.Integer(reference.LifeEpoch);
        return reference;
    }
}

/// <summary>
/// The two readings a per-frame spatial reader needs — where an entity stands and whether it lives — without the
/// full <see cref="RuntimeEntitySnapshot"/>. A value type filled by the kind's own pose observer, so a read
/// allocates nothing. <see cref="LifeState"/> is one of the snapshot's three words (<c>alive</c>, <c>downed</c>,
/// <c>dead</c>), and it is null only in the default value the kernel writes for an entity it could not observe.
/// </summary>
public readonly struct EntityPose
{
    public EntityPose(double x, double y, double z, string lifeState) { X = x; Y = y; Z = z; LifeState = lifeState; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public string? LifeState { get; }
    public bool Observed => LifeState != null;
    public bool Alive => LifeState == "alive";
}

/// <summary>The cheap spatial narrowing one whole-kind enumeration applies before its answer is counted
/// (<see cref="RuntimeKernel.EnumerateEntityCandidates(string, EntityCandidateScope)"/>): the zone the entities stand
/// in, a sphere they stand inside, or both. The default value narrows nothing.</summary>
public readonly struct EntityCandidateScope
{
    public EntityReference? Zone { get; init; }
    /// <summary>Whether the sphere below is part of the scope.</summary>
    public bool Within { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Radius { get; init; }
}

/// <summary>The one point measurement every spatial read keeps or drops an entity by: the scoped enumeration's sphere
/// and the shape selectors' volumes measure with these, so a sphere handed to the enumeration keeps exactly what the
/// same sphere keeps in `forge.selector.target.shape_overlap`, boundary included. The length is scaled before it is
/// squared, so no finite position overflows on the way; a difference or length that does not fit a double is
/// refused (`spatial-overflow`) rather than compared as infinity.</summary>
internal static class PointMeasure
{
    internal static double Distance(double ax, double ay, double az, double bx, double by, double bz)
        => Magnitude(Delta(ax, bx), Delta(ay, by), Delta(az, bz));

    internal static double Delta(double a, double b)
    {
        var value = a - b;
        if (!double.IsFinite(value)) throw new RuntimeContractException("spatial-overflow", "Position difference overflowed.");
        return Math.Abs(value);
    }

    internal static double Magnitude(double x, double y, double z)
    {
        var scale = Math.Max(x, Math.Max(y, z));
        if (scale == 0) return 0;
        x /= scale; y /= scale; z /= scale;
        var length = scale * Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length)) throw new RuntimeContractException("spatial-overflow", "Distance overflowed.");
        return length;
    }
}
/// <summary>A kind's pose read: the same entity its <see cref="RuntimeEntitySnapshot"/> observer answers for, with
/// the same position and life state, written into <paramref name="pose"/>. False where the entity cannot be observed
/// right now, as a null snapshot would say. It must not allocate: it is called per entity per frame.</summary>
public delegate bool EntityPoseObserver(EntityReference entity, out EntityPose pose);

/// <summary>Frozen targeting observation, not authority, world coverage or a native handle. The first seven
/// members are what every observer answers; the seven after them are the readings an observer that watches them
/// publishes, and each answers null where its package does not read it. Null is "unknown", never a zero, an
/// empty state or a false: a reader that needs one of them refuses instead of working with a substituted value,
/// which is why <c>FromJson</c> accepts both an absent key and an explicit null for all seven.</summary>
public sealed class RuntimeEntitySnapshot
{
    public EntityReference Ref { get; }
    public string Kind { get; }
    public string? Faction { get; }
    public string LifeState { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> Receives { get; }
    public IReadOnlyList<double> Position { get; }
    public double? Health { get; }
    public double? HealthMaximum { get; }
    /// <summary>The direction the entity faces, as the three components of a forward vector in world space. A
    /// forward vector and not a quaternion: a vector3 is the only orientation three slots can carry without one
    /// of the four components being invented.</summary>
    public IReadOnlyList<double>? Rotation { get; }
    /// <summary>A member name of the shared `ai_state` set, or null where the observer cannot read the state.</summary>
    public string? AiState { get; }
    public double? Speed { get; }
    /// <summary>The entity this one hangs from, or null for one with no parent. The kernel indexes the reverse
    /// direction from this field, so no observer publishes a `children` field of its own.</summary>
    public EntityReference? Parent { get; }
    /// <summary>Where the entity's eyes are in world space: the game's own <c>Agent.EyePosition</c>, published by
    /// the player and enemy observers and null for a kind without eyes. The point vanilla judges fog infection at.</summary>
    public IReadOnlyList<double>? EyePosition { get; }

    /// <summary>What every observer answers. The optional readings below it are appended here so an observer that
    /// does not publish them keeps its own call shape and leaves them unknown.</summary>
    public RuntimeEntitySnapshot(EntityReference reference, string kind, string? faction,
        string lifeState, IReadOnlyList<string> tags, IReadOnlyList<string> receives,
        IReadOnlyList<double> position, double? health = null, double? healthMaximum = null,
        IReadOnlyList<double>? rotation = null, string? aiState = null, double? speed = null,
        EntityReference? parent = null, IReadOnlyList<double>? eyePosition = null)
    {
        Ref = RuntimeEntityReferences.Validate(reference); Kind = RuntimeJson.Text(kind);
        RuntimeJson.Require(EntityKinds.All.Contains(Kind) && Ref.Id.StartsWith(Kind + ":", StringComparison.Ordinal),
            "entity-kind", "A snapshot kind is an EntityKinds member and prefixes its own reference.");
        Faction = faction == null ? null : RuntimeJson.Text(faction);
        RuntimeJson.Require(lifeState is "alive" or "downed" or "dead", "entity-life-state", "Unknown life state.");
        LifeState = lifeState; Tags = CopyLabels(tags); Receives = CopyLabels(receives);
        RuntimeJson.Require(position != null && position.Count == 3, "entity-position", "Position requires three finite coordinates.");
        var coordinates = new double[3];
        for (int i = 0; i < 3; i++)
        {
            coordinates[i] = position![i];
            RuntimeJson.Require(double.IsFinite(coordinates[i]), "entity-position", "Non-finite coordinate.");
        }
        Position = Array.AsReadOnly(coordinates);
        // The optional readings a domain observer may or may not publish, each validated here by the one function that
        // checks a reading of its kind, so no caller has a second rule to keep in step.
        Health = Measurement(health, "entity-health");
        HealthMaximum = Measurement(healthMaximum, "entity-health-maximum");
        RuntimeJson.Require(Health == null || HealthMaximum == null || Health <= HealthMaximum,
            "entity-health-range", "Health cannot exceed its own maximum.");
        Rotation = rotation == null ? null : Vector(rotation, "entity-rotation");
        AiState = Word(aiState);
        Speed = Measurement(speed, "entity-speed");
        Parent = parent == null ? null : RuntimeEntityReferences.Validate(parent);
        EyePosition = eyePosition == null ? null : Vector(eyePosition, "entity-eye-position");
    }
    /// <summary>
    /// One optional measurement: absent stays absent, a present one is finite and not negative. A health or a speed
    /// of `-1` is not a reading any observer can make, so neither is stored as a value a reader would interpret.
    /// </summary>
    private static double? Measurement(double? reading, string code)
    {
        if (reading is not { } number) return null;
        RuntimeJson.Require(double.IsFinite(number), code, "A reading is absent or finite, never non-finite.");
        RuntimeJson.Require(number >= 0, code, "A reading is absent or a quantity that cannot be negative.");
        return number;
    }
    /// <summary>One optional member name of the `ai_state` set: absent stays absent, a present one is a member.</summary>
    private static string? Word(string? aiState)
    {
        if (aiState == null) return null;
        RuntimeJson.Require(RuntimeGraphContracts.IsEnumMember("ai_state", aiState), "entity-ai-state", aiState);
        return aiState;
    }
    /// <summary>The same three finite components a position has, for an optional vector reading (the direction an
    /// entity faces, where its eyes are), refused under the reading's own code.</summary>
    private static IReadOnlyList<double> Vector(IReadOnlyList<double> reading, string code)
    {
        RuntimeJson.Require(reading.Count == 3, code, "A vector reading requires three finite components.");
        var components = new double[3];
        for (int i = 0; i < 3; i++)
        {
            components[i] = reading[i];
            RuntimeJson.Require(double.IsFinite(components[i]), code, "Non-finite vector component.");
        }
        return Array.AsReadOnly(components);
    }
    internal static IReadOnlyList<string> CopyLabels(IReadOnlyList<string> labels)
    {
        RuntimeJson.Require(labels != null && labels.Count <= 128, "entity-label-budget", "At most 128 labels are allowed.");
        if (labels!.Count == 0) return Array.Empty<string>();
        var copy = new string[labels.Count];
        // A singleton cannot contain a duplicate. Still validate and copy its text: the caller's
        // collection can change after this frozen observation has been published.
        if (copy.Length == 1)
        {
            copy[0] = RuntimeJson.Text(labels[0]);
            return Array.AsReadOnly(copy);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = RuntimeJson.Text(labels[i]);
            RuntimeJson.Require(seen.Add(copy[i]), "duplicate-value", "Duplicate observation label.");
        }
        return Array.AsReadOnly(copy);
    }

    public static RuntimeEntitySnapshot FromJson(JsonElement input)
    {
        var value = RuntimeJson.Parse(input.GetRawText());
        RuntimeJson.Shape(value, "ref kind faction lifeState tags receives position",
            "health healthMaximum rotation aiState speed parent eyePosition");
        return new(RuntimeJson.Entity(value.GetProperty("ref")), RuntimeJson.Text(value, "kind"),
            value.GetProperty("faction").ValueKind == JsonValueKind.Null ? null : RuntimeJson.Text(value, "faction"),
            RuntimeJson.Text(value, "lifeState"), RuntimeJson.Strings(value.GetProperty("tags")),
            RuntimeJson.Strings(value.GetProperty("receives")), Components(value.GetProperty("position")),
            WireReading(value, "health"), WireReading(value, "healthMaximum"),
            value.TryGetProperty("rotation", out var rotation) && rotation.ValueKind != JsonValueKind.Null
                ? Components(rotation) : null,
            value.TryGetProperty("aiState", out var aiState) && aiState.ValueKind != JsonValueKind.Null
                ? RuntimeJson.Text(value, "aiState") : null,
            WireReading(value, "speed"),
            value.TryGetProperty("parent", out var parent) && parent.ValueKind != JsonValueKind.Null
                ? RuntimeJson.Entity(parent) : null,
            value.TryGetProperty("eyePosition", out var eye) && eye.ValueKind != JsonValueKind.Null
                ? Components(eye) : null);
    }
    /// <summary>One optional scalar reading of a snapshot object: absent and explicit null are both "unknown",
    /// because nothing can read a number that is not there and a zero would be a value the observer never made.
    /// The constructor applies the reading's own rule (finite, not negative) to whatever comes back.</summary>
    private static double? WireReading(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var reading) || reading.ValueKind == JsonValueKind.Null) return null;
        var code = key == "speed" ? "entity-speed" : "entity-health";
        RuntimeJson.Require(reading.ValueKind == JsonValueKind.Number, code, key);
        return reading.GetDouble();
    }
    /// <summary>The three finite components of a position or an orientation, read the same way a position is.</summary>
    private static double[] Components(JsonElement array)
    {
        RuntimeJson.Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == 3,
            "entity-position", "Position requires three finite coordinates.");
        var coordinates = new double[3];
        for (int i = 0; i < 3; i++)
            RuntimeJson.Require(array[i].ValueKind == JsonValueKind.Number && array[i].TryGetDouble(out coordinates[i])
                && double.IsFinite(coordinates[i]), "entity-position", "Non-finite coordinate.");
        return coordinates;
    }
}

public sealed record RuntimeEntityInspection(EntityReference Reference,
    RuntimeEntitySnapshot? Snapshot, string Code);

/// <summary>
/// One zone read's answer: the zone the entity stands in, or the reason the read could not be made. It is a
/// resolution and not an inspection, because a zone is a place and not an entity with a snapshot of its own — the
/// level's own coordinates name it, and one provider's responder is what puts an entity of its kind in it.
///
/// One read has exactly two outcomes and <see cref="Answered"/> tells them apart: a provider that placed the
/// entity answered with its zone, and a read nobody could make is refused with a code. A responder that answers
/// nothing has placed nothing — that is the unknown the kind's own contract names — so it is refused like any
/// other unreadable entity and never reported as an entity standing outside every zone.
/// </summary>
public sealed record EntityZoneResolution
{
    /// <summary>The code of a read that answered with the zone the entity stands in.</summary>
    public const string InZoneCode = "entity-zone";
    /// <summary>The kernel's refusal for a responder that answered no zone: a provider that cannot place an
    /// entity it owns right now has not placed it outside every zone, it has not placed it at all.</summary>
    public const string UnknownCode = "entity-zone-unknown";
    /// <summary>The kernel's refusal for a kind whose owner registered no zone responder.</summary>
    public const string UnavailableCode = "entity-zone-unavailable";
    /// <summary>The kernel's refusal for a responder that failed without naming a reason of its own.</summary>
    public const string FailedCode = "entity-zone-failed";
    /// <summary>The kernel's refusal for a responder that answered a reference which is not a zone.</summary>
    public const string KindCode = "entity-zone-kind";

    private EntityZoneResolution(EntityReference? zone, string code, bool answered)
    { Zone = zone; Code = code; Answered = answered; }

    /// <summary>The entity stands in this zone.</summary>
    public static EntityZoneResolution InZone(EntityReference zone) => new(zone, InZoneCode, true);
    /// <summary>The read could not be made. The code is the kernel's own or the responder's, by name.</summary>
    public static EntityZoneResolution Refused(string code) => new(null, code, false);

    /// <summary>The zone the entity stands in.</summary>
    public EntityReference? Zone { get; }
    /// <summary>The answer's own code: <see cref="InZoneCode"/> when the read answered,
    /// and the refusal's code when it did not.</summary>
    public string Code { get; }
    /// <summary>Whether a provider answered where the entity stands. False means <see cref="Code"/> says why the
    /// read could not be made.</summary>
    public bool Answered { get; }
}

/// <summary>
/// One area read's answer: the index of the area the entity stands in inside its own zone, or the reason the read
/// could not be made. It is the second half of <see cref="EntityZoneResolution"/> — a zone is a place, an area is
/// one part of that place — and the same provider answers both, because the course node that places an entity is
/// the one fact behind either answer.
///
/// One read has exactly two outcomes and <see cref="Answered"/> tells them apart: a provider that placed the
/// entity answered with the index the level's zone lists the area under, and a read nobody could make is refused
/// with a code. A responder that answers nothing has placed nothing, so it is refused like any other unreadable
/// entity and never reported as area zero.
/// </summary>
public sealed record EntityAreaResolution
{
    /// <summary>The code of a read that answered with the area the entity stands in.</summary>
    public const string InAreaCode = "entity-zone-area";
    /// <summary>The kernel's refusal for a responder that answered no area: a provider that cannot place an
    /// entity it owns right now has not placed it in area zero.</summary>
    public const string UnknownCode = "entity-zone-area-unknown";
    /// <summary>The kernel's refusal for a kind whose owner registered no area responder.</summary>
    public const string UnavailableCode = "entity-zone-area-unavailable";
    /// <summary>The kernel's refusal for a responder that failed without naming a reason of its own.</summary>
    public const string FailedCode = "entity-zone-area-failed";
    /// <summary>The kernel's refusal for a responder that answered an index no zone has: an area index is a
    /// position in the level's own list, so a negative one is not one.</summary>
    public const string IndexCode = "entity-zone-area-index";

    private EntityAreaResolution(int? area, string code, bool answered)
    { Area = area; Code = code; Answered = answered; }

    /// <summary>The entity stands in this area of its zone.</summary>
    public static EntityAreaResolution InArea(int area) => new(area, InAreaCode, true);
    /// <summary>The read could not be made. The code is the kernel's own or the responder's, by name.</summary>
    public static EntityAreaResolution Refused(string code) => new(null, code, false);

    /// <summary>The area index the entity stands in, inside its own zone.</summary>
    public int? Area { get; }
    /// <summary>The answer's own code: <see cref="InAreaCode"/> when the read answered, and the refusal's code
    /// when it did not.</summary>
    public string Code { get; }
    /// <summary>Whether a provider answered which area the entity stands in. False means <see cref="Code"/> says
    /// why the read could not be made.</summary>
    public bool Answered { get; }
}

/// <summary>
/// Zone identity itself: the level's own three coordinates, formatted the one way every package spells them, so
/// two providers that each resolve their own kind of entity can be compared by the same reader. The zone kind is
/// the namespace those references are routed by, and a zone resource id is the same text, which is what lets the
/// `zone` resource kind and the `gtfo.zone` entity kind name one place. Nothing here reads a level: a package
/// formats the coordinates it already has.
/// </summary>
internal static class RuntimeZones
{
    /// <summary>The resource kind's own name in the shared resource-kind table.</summary>
    public const string ResourceKind = "zone";

    /// <summary>One level zone's reference, from the three coordinates the level assigns it: dimension, layer and
    /// the zone's index inside that layer. The text is stable for as long as the level is, and it is the same text
    /// the `zone` resource kind answers with, so a selector can hold the reference a resource port carried.</summary>
    public static EntityReference Reference(long worldEpoch, int dimension, int layer, int zone)
        => new(Id(dimension, layer, zone), worldEpoch, 0);

    /// <summary>The id half of <see cref="Reference"/>, for a caller that has coordinates and no world.</summary>
    public static string Id(int dimension, int layer, int zone)
        => EntityKinds.Zone + ":" + dimension.ToString(CultureInfo.InvariantCulture) + ":"
            + layer.ToString(CultureInfo.InvariantCulture) + ":" + zone.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Complete covers only requested references, never all entities in a world or area.</summary>
public sealed class RuntimeEntityQueryResult
{
    public string Status { get; }
    public string Code { get; }
    public RuntimeLifecycleSnapshot Context { get; }
    public int Requested { get; }
    public int Distinct { get; }
    public IReadOnlyList<RuntimeEntityInspection> Items { get; }
    public bool IsComplete => Status == "complete";
    internal RuntimeEntityQueryResult(string status, string code, RuntimeLifecycleSnapshot context,
        int requested, int distinct, RuntimeEntityInspection[] items)
    {
        Status = status; Code = code; Context = context; Requested = requested; Distinct = distinct;
        // Only freshly owned result arrays enter here. The kernel's scratch buffers never escape.
        Items = Array.AsReadOnly(items);
    }
    public IReadOnlyList<RuntimeEntitySnapshot> RequireComplete()
    {
        RuntimeJson.Require(IsComplete, Code, "Entity observation is not complete; no implicit partial selection is allowed.");
        return Array.AsReadOnly(Items.Select(item => item.Snapshot!).ToArray());
    }
}
