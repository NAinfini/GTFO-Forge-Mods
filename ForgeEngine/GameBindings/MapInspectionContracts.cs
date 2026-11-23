using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>Author identities read from the owning Map tables. Native handles are game objects, never private domain state.</summary>
public sealed record MapRoomInspection(string ZoneAuthorId, int Dimension, int Layer, int LocalIndex,
    string RoomId, string Revision, string SourcePrefab, string InstanceId);
public sealed record MapDoorInspection(string ObjectId, string Key, string RoomId, string DoorwayId);
public sealed record MapModelInspection(object Collider, EntityReference Reference, string ModelId, string RoomId);
/// <summary>A copy of the effective production zone used by Map's membership tick, not a collider substitute.</summary>
public sealed record MapTriggerZoneInspection(string Id, string Level, long WorldEpoch, string Method,
    string Shape, string Who, bool Enabled, bool BlocksPlayers, IReadOnlyList<double> Position,
    IReadOnlyList<double> Rotation, IReadOnlyList<double> Size);
public sealed record MapGenerationRoomIdentity(string RoomId, string Revision, string? InstanceId);
public sealed record MapGeneratedConnectionInspection(string Id, MapGenerationRoomIdentity From, MapGenerationRoomIdentity To,
    object FromPlug, object ToPlug, object? Instance, object? Gate);

/// <summary>Immediate read-only Map ports for authoring checks; no scheduler, registry or substitute identities.</summary>
public interface IMapInspectionService
{
    long WorldEpoch { get; }
    string? LevelReference { get; }
    EntityReference? EnemyReference(string address, out string? refusal);
    object? TriggerCollider(string id, long worldEpoch);
    MapTriggerZoneInspection? TriggerZone(string id, long worldEpoch);
    string? GeneratorAddress(object native);
    MapRoomInspection? RoomReceipt(object native);
    MapDoorInspection? InteriorDoorIdentity(string value, bool isKey);
    string InteriorDoorAddress(int dimension, int layer, int zone, string room, string doorway);
    object? InteriorDoor(string address);
    object? InteractionPoint(string address, long worldEpoch);
    object? Container(string id, long worldEpoch);
    string? ModelId(string address);
    IReadOnlyList<MapModelInspection> ModelColliders(long worldEpoch);
    bool TryRenderText(string text, out string rendered);
    /// <summary>Generation receipts of one generated room, Area, exit or connection in the named level and world.
    /// A missing or stale receipt returns null with a refusal that names it; nothing is guessed.</summary>
    MapGenerationRoomIdentity? GeneratedRoom(object nativeRoom);
    object? GeneratedArea(long worldEpoch, string level, object nativeZone, object nativeRoom, string id, string revision, out string? refusal);
    object? GeneratedExit(long worldEpoch, string level, object nativeZone, object nativeRoom, string id, out string? refusal);
    MapGeneratedConnectionInspection? GeneratedConnection(long worldEpoch, string level, string id, out string? refusal);
}

public static class MapInspectionService
{
    public const string MissingCode = "map-inspection-provider-missing";
    public static IMapInspectionService Require(IForgeEngine? runtime)
        => runtime != null && runtime.TryService<IMapInspectionService>(out var service) ? service
            : throw new RuntimeContractException(MissingCode, "The Map provider is not installed or has not registered its inspection ports.");
}
