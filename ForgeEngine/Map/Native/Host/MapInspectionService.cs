using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Read the production identity tables without exposing their private representation to other packages.</summary>
internal sealed class MapInspectionService : IMapInspectionService
{
    private readonly MapPluginSession session;
    internal MapInspectionService(MapPluginSession session) => this.session = session;
    private void Current(long? epoch = null)
    {
        if (session.Faulted || session.MapObjects?.Registration.IsRegistered != true)
            throw new RuntimeContractException("map-inspection-session-unavailable", "The Map inspection session is released or faulted.");
        if (epoch != null && epoch != session.WorldEpoch)
            throw new RuntimeContractException("map-inspection-stale-world", "The requested Map world is no longer current.");
    }
    public long WorldEpoch { get { Current(); return session.WorldEpoch; } }
    public string? LevelReference { get { Current(); return session.LevelReference(); } }
    public EntityReference? EnemyReference(string address, out string? refusal)
    {
        Current(); refusal = "fixed-placement-session-unavailable";
        return session.Placements?.EnemyReference(address, out refusal);
    }
    public object? TriggerCollider(string id, long worldEpoch)
    { Current(worldEpoch); return session.TriggerZones?.InspectCollider(id, worldEpoch); }
    public MapTriggerZoneInspection? TriggerZone(string id, long worldEpoch)
    { Current(worldEpoch); return session.TriggerZones?.InspectState(id, worldEpoch); }
    public string? GeneratorAddress(object native) { Current(); return GeneratorObservation.AddressOf(native)?.ToString(); }
    public MapRoomInspection? RoomReceipt(object native)
    {
        Current();
        if (native is not LG_Geomorph room || room.WasCollected) return null;
        var locator = GeneratedRoomRegistry.LocatorFor(room);
        return locator?.InstanceId is not { } instance ? null : new(locator.ZoneAuthorId, locator.Dimension,
            locator.Layer, locator.LocalIndex, locator.RoomId, locator.Revision, locator.SourcePrefab, instance);
    }
    private static MapGenerationRoomIdentity? Identity(RoomLocator? locator)
        => locator == null ? null : new(locator.RoomId, locator.Revision, locator.InstanceId);
    public MapGenerationRoomIdentity? GeneratedRoom(object native)
    { Current(); return native is LG_Geomorph room && !room.WasCollected ? Identity(GeneratedRoomRegistry.LocatorFor(room)) : null; }
    public object? GeneratedArea(long worldEpoch, string level, object nativeZone, object nativeRoom, string id, string revision, out string? refusal)
    {
        Current(worldEpoch);
        if (MapLevelReference.TryParse(level) is not { } parsed) { refusal = "level-unavailable"; return null; }
        if (nativeZone is not LG_Zone zone || nativeRoom is not LG_Geomorph room || GeneratedRoomRegistry.LocatorFor(room) is not { } locator)
        { refusal = "authored-room-unbound"; return null; }
        return GeneratedRoomRegistry.FindArea(worldEpoch, parsed, zone, locator, id, revision, out refusal);
    }
    public object? GeneratedExit(long worldEpoch, string level, object nativeZone, object nativeRoom, string id, out string? refusal)
    {
        Current(worldEpoch);
        if (MapLevelReference.TryParse(level) is not { } parsed) { refusal = "level-unavailable"; return null; }
        if (nativeZone is not LG_Zone zone || nativeRoom is not LG_Geomorph room || GeneratedRoomRegistry.LocatorFor(room) is not { } locator)
        { refusal = "authored-room-unbound"; return null; }
        return GeneratedRoomRegistry.FindExit(worldEpoch, parsed, zone, locator, id, out refusal);
    }
    public MapGeneratedConnectionInspection? GeneratedConnection(long worldEpoch, string level, string id, out string? refusal)
    {
        Current(worldEpoch);
        if (MapLevelReference.TryParse(level) is not { } parsed) { refusal = "level-unavailable"; return null; }
        var receipt = GeneratedRoomRegistry.GeneratedConnection(worldEpoch, parsed, id, out refusal);
        return receipt == null ? null : new(receipt.Id, Identity(receipt.From)!, Identity(receipt.To)!, receipt.FromPlug, receipt.ToPlug,
            receipt.Instance, receipt.Gate);
    }
    public MapDoorInspection? InteriorDoorIdentity(string value, bool isKey)
    {
        Current();
        var pair = isKey ? ForgeMap.InteriorDoorIdentity.ParseKey(value) : ForgeMap.InteriorDoorIdentity.ParseObjectId(value);
        return pair is not { } id ? null : new(ForgeMap.InteriorDoorIdentity.ObjectId(id.Room, id.Door),
            ForgeMap.InteriorDoorIdentity.Key(id.Room, id.Door), id.Room, id.Door);
    }
    public string InteriorDoorAddress(int dimension, int layer, int zone, string room, string doorway)
    {
        Current(); return new MapObjectReference("door", dimension.ToString(System.Globalization.CultureInfo.InvariantCulture),
        layer.ToString(System.Globalization.CultureInfo.InvariantCulture), zone.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ForgeMap.InteriorDoorIdentity.Key(room, doorway)).ToString();
    }
    public object? InteriorDoor(string address)
    { Current(); return MapObjectDoorAddress.TryParse(address) is { } parsed ? InteriorDoorObservation.Current?.ByAddress(parsed) : null; }
    public object? InteractionPoint(string address, long worldEpoch)
    { Current(worldEpoch); return session.InteractionPoints?.InspectInteraction(address, worldEpoch); }
    public object? Container(string id, long worldEpoch)
    { Current(worldEpoch); return session.Placements?.InspectContainer(id, worldEpoch); }
    public bool TryRenderText(string text, out string rendered)
    { Current(); return StationTextValue.TryRender(text, session.Kernel, out rendered); }
    public string? ModelId(string address) { Current(); return PlacedModelAddress.TryParse(address)?.Key; }
    public IReadOnlyList<MapModelInspection> ModelColliders(long worldEpoch)
    {
        Current(worldEpoch);
        if (session.MapObjects == null) throw new RuntimeContractException("map-objects-unavailable", "The production Map identity table is unavailable.");
        return PlacedModelSession.Current?.InspectColliders(worldEpoch, session.MapObjects)
            ?? Array.Empty<MapModelInspection>();
    }
}
