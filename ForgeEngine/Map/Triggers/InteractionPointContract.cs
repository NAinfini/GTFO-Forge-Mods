using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

internal sealed record InteractionPointAppearance(string PlacedModelId);

internal sealed class InteractionPoint
{
    internal const float DefaultColliderRadius = .5f * .8660254f;
    internal InteractionPoint(string id, MapLevelReference level, RoomLocator room, double[] position,
        double[] rotation, int duration, string prompt, bool placed, InteractionPointAppearance? appearance = null,
        double extraReach = 0, bool enabled = true, string? group = null)
    {
        Enabled = enabled;
        Group = group;
        Id = id;
        Level = level;
        Room = room;
        Position = Array.AsReadOnly(position);
        Rotation = Array.AsReadOnly(rotation);
        Duration = duration;
        Prompt = prompt;
        Placed = placed;
        Appearance = appearance;
        ExtraReach = extraReach;
    }

    public string Id { get; }
    public MapLevelReference Level { get; }
    public RoomLocator Room { get; }
    public IReadOnlyList<double> Position { get; }
    public IReadOnlyList<double> Rotation { get; }
    public int Duration { get; }
    public string Prompt { get; }
    public bool Placed { get; }
    public InteractionPointAppearance? Appearance { get; }
    /// <summary>Whether the point can be held when the level starts; a behaviour switches it later.</summary>
    public bool Enabled { get; }
    /// <summary>The author's group name: `forge.selector.target.object_group` answers every placed object of one group.</summary>
    public string? Group { get; }
    // Derived from the native body once; not an authored field or a package-side range override.
    internal double ExtraReach { get; }

    public InteractionPoint PlacedIn(RoomPose room)
        => Placed ? this : new InteractionPoint(Id, Level, Room, room.Point(Position), room.Compose(Rotation),
            Duration, Prompt, true, Appearance, ExtraReach, Enabled, Group);
}

internal static class InteractionPointAddress
{
    public const string Category = "interaction_point";
    private const string Coordinate = "0";

    public static bool IsPointId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > InteractionPointManifest.MaximumIdLength
            || id.Trim() != id || id == Category || id.IndexOf('/') >= 0 || id.IndexOf(':') >= 0) return false;
        foreach (var character in id) if (char.IsControl(character)) return false;
        return true;
    }

    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, IsPointId) is { } address
            && address.Dimension == Coordinate && address.Layer == Coordinate && address.Zone == Coordinate
            ? address : null;

    public static MapObjectReference? Of(string? id)
        => IsPointId(id) ? new MapObjectReference(Category, Coordinate, Coordinate, Coordinate, id!) : null;
}

internal static class InteractionPointManifest
{
    public const int MaximumIdLength = 128;
    public const double MaximumPosition = float.MaxValue;
    private const string FileName = "interaction-points.forge";

    public static string FileNameOnly => FileName;

    public static bool TryParse(string text, out IReadOnlyList<InteractionPoint>? points, out string? code,
        out string? reason)
    {
        points = null; code = null; reason = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "interaction-point-json"; reason = error.Message; return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refuse(out points, out code, out reason, "interaction-point-json", "The document root is not an object.");
            if (!KnownOnce(root, out var unknown, "schemaVersion", "points"))
                return Refuse(out points, out code, out reason, "interaction-point-field", "Unknown field `" + unknown + "`.");
            if (!FormatVersion.Matches(root))
                return Refuse(out points, out code, out reason, "interaction-point-schema",
                    "The document must declare schemaVersion " + FormatVersion.Current + ".");
            if (!root.TryGetProperty("points", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return Refuse(out points, out code, out reason, "interaction-point-points", "The document carries no `points` array.");

            var parsed = new List<InteractionPoint>(rows.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryPoint(row, out var point, out code, out reason)) return false;
                if (!ids.Add(point!.Id))
                    return Refuse(out points, out code, out reason, "interaction-point-duplicate",
                        "Point id `" + point.Id + "` is claimed more than once.");
                parsed.Add(point);
            }
            points = Array.AsReadOnly(parsed.ToArray());
            return true;
        }
    }

    private static bool TryPoint(JsonElement row, out InteractionPoint? point, out string? code, out string? reason)
    {
        point = null; code = null; reason = null;
        if (row.ValueKind != JsonValueKind.Object)
            return RefusePoint(out point, out code, out reason, "interaction-point-json", "A point entry is not an object.");
        if (!KnownOnce(row, out var unknown, "id", "level", "room", "position", "rotation", "duration", "prompt", "appearance", "enabled", "group"))
            return RefusePoint(out point, out code, out reason, "interaction-point-field", "Unknown point field `" + unknown + "`.");
        if (!DocumentReading.Text(row, "id", MaximumIdLength, out var id) || !InteractionPointAddress.IsPointId(id))
            return RefusePoint(out point, out code, out reason, "interaction-point-id",
                "A point id is at most " + MaximumIdLength + " printable characters without `:` or `/`.");
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
            return RefusePoint(out point, out code, out reason, "interaction-point-level",
                "A point needs a level reference of the form `<rundown>:<tier>:<index>`.");
        if (!row.TryGetProperty("room", out var roomRow))
            return RefusePoint(out point, out code, out reason, "interaction-point-room", "A point names its room.");
        if (!RoomLocator.TryParse(roomRow, out var room, out _, out var roomReason))
            return RefusePoint(out point, out code, out reason, "interaction-point-room", roomReason!);
        if (!DocumentReading.Numbers(row, "position", 3, -MaximumPosition, MaximumPosition, out var position))
            return RefusePoint(out point, out code, out reason, "interaction-point-position",
                "A point position carries three finite room-local metre coordinates within the native Single range.");
        if (!DocumentReading.Numbers(row, "rotation", Rotations.Components, -1, 1, out var rotation)
            || !Rotations.IsUnit(rotation!))
            return RefusePoint(out point, out code, out reason, "interaction-point-rotation",
                "A point rotation is a unit quaternion in `x y z w` order.");
        if (!RuntimeJson.TryInteger(row, "duration", 1, int.MaxValue, out var duration))
            return RefusePoint(out point, out code, out reason, "interaction-point-duration",
                "A point duration is an integer tick count from 1 through " + int.MaxValue + ".");
        if (!DocumentReading.Text(row, "prompt", int.MaxValue, out var prompt)
            || !TextResourceContract.TryParseReference(prompt, false, out _))
            return RefusePoint(out point, out code, out reason, "interaction-point-prompt",
                "A point prompt is a compiled `forge:text:<id>@<Language>` reference.");
        InteractionPointAppearance? appearance = null;
        if (row.TryGetProperty("appearance", out var appearanceRow))
        {
            if (appearanceRow.ValueKind != JsonValueKind.Object
                || !KnownOnce(appearanceRow, out _, "placedModelId")
                || !DocumentReading.Text(appearanceRow, "placedModelId", PlacedModelManifest.MaximumIdLength,
                    out var placedModelId)
                || !PlacedModelAddress.IsId(placedModelId))
                return RefusePoint(out point, out code, out reason, "interaction-point-appearance",
                    "A point appearance names exactly one non-empty placedModelId.");
            appearance = new InteractionPointAppearance(placedModelId!);
        }

        var enabled = true;
        if (row.TryGetProperty("enabled", out var enabledRow))
        {
            if (enabledRow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return RefusePoint(out point, out code, out reason, "interaction-point-enabled", "A point's enabled flag is true or false.");
            enabled = enabledRow.GetBoolean();
        }
        if (!MapObjectGroup.TryRead(row, out var group))
            return RefusePoint(out point, out code, out reason, "interaction-point-group", MapObjectGroup.Rule);

        point = new InteractionPoint(id!, level, room!, position!, rotation!, (int)duration, prompt!, false, appearance,
            enabled: enabled, group: group);
        return true;
    }

    private static bool Refuse(out IReadOnlyList<InteractionPoint>? points, out string? code, out string? reason,
        string refused, string message)
    { points = null; code = refused; reason = message; return false; }

    private static bool RefusePoint(out InteractionPoint? point, out string? code, out string? reason,
        string refused, string message)
    { point = null; code = refused; reason = message; return false; }

    private static bool KnownOnce(JsonElement row, out string? invalid, params string[] allowed)
    {
        var known = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
        {
            if (!known.Contains(property.Name) || !seen.Add(property.Name))
            {
                invalid = property.Name;
                return false;
            }
        }
        invalid = null;
        return true;
    }
}

internal static class InteractionPointPlacement
{
    public static IReadOnlyList<InteractionPoint> Of(IReadOnlyList<InteractionPoint> points,
        MapLevelReference level, RoomLookup? rooms, Action<string> refused,
        Action<InteractionPoint, Exception>? resolverFailed = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(refused);
        var placed = new List<InteractionPoint>(points.Count);
        foreach (var point in points)
        {
            if (point.Level != level) continue;
            if (point.Appearance != null) { placed.Add(point); continue; }
            if (point.Placed) { placed.Add(point); continue; }
            RoomPose? pose = null;
            try { pose = rooms?.Invoke(point.Room); }
            catch (Exception error)
            {
                if (resolverFailed != null) resolverFailed(point, error);
                else refused(ResolverFailure(point, error));
                continue;
            }
            if (pose is not { } room)
            {
                refused("interaction-point-room: point `" + point.Id + "` has no resolved room.");
                continue;
            }
            placed.Add(point.PlacedIn(room));
        }
        return Array.AsReadOnly(placed.ToArray());
    }

    public static string ResolverFailure(InteractionPoint point, Exception error)
        => "interaction-point-room: point `" + point.Id + "` in room " + point.Room
            + " resolver failed." + Environment.NewLine + error;
}

internal enum InteractionPointPhase : byte { Started = 1, Progress = 2, Cancelled = 3, Completed = 4 }

internal static class InteractionPointContract
{
    /// <summary>Every phase, in <see cref="InteractionPointPhase"/> order, with the fact it publishes.</summary>
    public static readonly IReadOnlyList<(InteractionPointPhase Phase, string Fact)> Rows = Array.AsReadOnly(new[]
    {
        (InteractionPointPhase.Started, "interaction_point.hold_started"),
        (InteractionPointPhase.Progress, "interaction_point.hold_progress"),
        (InteractionPointPhase.Cancelled, "interaction_point.hold_cancelled"),
        (InteractionPointPhase.Completed, "interaction_point.hold_completed")
    });

    public static readonly string StartedBinding = ModuleDefinition.ProviderId + ".binding.interaction.interaction_point.hold_started";
    public static readonly string ProgressBinding = ModuleDefinition.ProviderId + ".binding.interaction.interaction_point.hold_progress";
    public static readonly string CancelledBinding = ModuleDefinition.ProviderId + ".binding.interaction.interaction_point.hold_cancelled";
    public static readonly string CompletedBinding = ModuleDefinition.ProviderId + ".binding.interaction.interaction_point.hold_completed";

    public static string Binding(InteractionPointPhase phase) => phase switch
    {
        InteractionPointPhase.Started => StartedBinding,
        InteractionPointPhase.Progress => ProgressBinding,
        InteractionPointPhase.Cancelled => CancelledBinding,
        InteractionPointPhase.Completed => CompletedBinding,
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.interaction_point.hold_started", Blocks.TriggerInteractionHoldStarted.Id, "gtfo.map_object.interaction_point.hold_started", "observe", new[] { "gtfo.map_object.read" }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.interaction_point.hold_progress", Blocks.TriggerInteractionHoldProgress.Id, "gtfo.map_object.interaction_point.hold_progress", "observe", new[] { "gtfo.map_object.read" }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.interaction_point.hold_cancelled", Blocks.TriggerInteractionHoldCancelled.Id, "gtfo.map_object.interaction_point.hold_cancelled", "observe", new[] { "gtfo.map_object.read" }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.interaction_point.hold_completed", Blocks.TriggerInteractionHoldCompleted.Id, "gtfo.map_object.interaction_point.hold_completed", "observe", new[] { "gtfo.map_object.read" })
    };
}
