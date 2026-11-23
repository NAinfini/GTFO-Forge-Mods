using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>Typed authoring reads over Map's production identities and native request path.</summary>
internal sealed class MapCaptureService : IMapCaptureService
{
    private readonly MapPluginSession session;

    internal MapCaptureService(MapPluginSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        TaskObjectMoveObservations.Clear();
    }

    private void Current(long? epoch = null)
    {
        if (session.Faulted || session.MapObjects?.Registration.IsRegistered != true)
            throw new RuntimeContractException("map-capture-session-unavailable",
                "The Map capture session is released or faulted.");
        if (epoch != null && epoch != session.WorldEpoch)
            throw new RuntimeContractException("map-capture-stale-world",
                "The requested Map world is no longer current.");
    }

    public string FixedItemKey(int dimension, int layer, int zone, string id)
    {
        Current();
        return FixedItemIdentity.Key(dimension, layer, zone, id)
            ?? throw new RuntimeContractException("map-fixed-item-identity-unresolved",
                "The authored fixed-item identity cannot be represented as a Map address.");
    }

    public string? FixedItemKeyOf(long worldEpoch, int nativeId)
    { Current(worldEpoch); return FixedItemIdentity.KeyOf(worldEpoch, nativeId); }

    public string? RoomInstanceId(LG_Geomorph room)
    {
        Current();
        return room == null || room.WasCollected ? null : GeneratedRoomRegistry.InstanceIdFor(room);
    }

    public RuntimeMapRoomReceipt? RoomReceipt(LG_Geomorph room)
    {
        Current();
        return room == null || room.WasCollected ? null : GeneratedRoomRegistry.Snapshot(room);
    }

    public RuntimeMapTaskMoveSnapshot TaskMoves(long worldEpoch)
    { Current(worldEpoch); return TaskObjectMoveObservations.Snapshot(worldEpoch); }
}
