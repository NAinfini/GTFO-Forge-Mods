using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

public sealed record RuntimeMapRoomLocator(string ZoneAuthorId, int Dimension, int Layer, int LocalIndex,
    string RoomId, string Revision, string SourcePrefab);

/// <summary>A native room plus the Map generation receipt that admitted it. Locator is absent for an entry source
/// whose document proves only the authored instance id.</summary>
public sealed record RuntimeMapRoomReceipt(LG_Geomorph Room, string InstanceId, RuntimeMapRoomLocator? Locator);

/// <summary>One paired before/after read from Map's production task-object relocation path.</summary>
public sealed record RuntimeMapTaskMove(string Id, int NativeInstanceId, long WorldEpoch,
    float[]? BeforePosition, float[]? AfterPosition, float[]? AfterRotation, string Status, string? Refusal);

/// <summary>A bounded task-object observation. Dropped is explicit so a partial capture is never an empty success.</summary>
public sealed record RuntimeMapTaskMoveSnapshot(long WorldEpoch, IReadOnlyList<RuntimeMapTaskMove> Rows, int Dropped);

/// <summary>Read-only authoring evidence owned by Map.</summary>
public interface IMapCaptureService
{
    string FixedItemKey(int dimension, int layer, int zone, string id);
    string? FixedItemKeyOf(long worldEpoch, int nativeId);
    string? RoomInstanceId(LG_Geomorph room);
    RuntimeMapRoomReceipt? RoomReceipt(LG_Geomorph room);
    RuntimeMapTaskMoveSnapshot TaskMoves(long worldEpoch);
}

public static class MapCaptureService
{
    public const string MissingCode = "map-capture-provider-missing";

    public static IMapCaptureService Require(IForgeEngine? runtime)
        => runtime != null && runtime.TryService<IMapCaptureService>(out var service) ? service
            : throw new RuntimeContractException(MissingCode,
                "The Map provider is not installed or has not registered its capture ports.");
}
