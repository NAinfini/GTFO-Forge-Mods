using System.Text.Json;

namespace ForgeMap.Native;

/// <summary>Zone state on every machine: one row per placed zone in the host table <see cref="MapNet.TriggerZones"/>,
/// keyed by the zone's index in the level's zone table (the same documents give every machine the same table; the row
/// names the zone, so an index that means another zone here is refused). The host writes a row whenever a zone's state
/// changes. ForgeCore sends the rows that changed, the whole table to a player who joins and to everyone after a
/// checkpoint reload or a host change, and empties it with the level. The engine's state stream carries the zones only
/// as part of a whole document (a checkpoint, a migration image, what a joining player is handed), never per change.</summary>
internal sealed partial class TriggerZoneSession
{
    /// <summary>After a placement and after a restore: the host writes every placed zone (a row whose bytes did not
    /// change is not sent), every other machine takes the rows it already holds.</summary>
    private void SyncNetwork()
    {
        if (_controller == null) return;
        var host = _authority();
        for (var i = 0; i < _controller.Count; i++)
        {
            if (!_controller.Zone(i).Placed || _controller.Zone(i).Level != _placedLevel) continue;
            if (host) Publish(i);
            else if (MapNet.TriggerZones.TryGet(i, out var row)) Receive(i, row);
        }
    }


    // ForgeCore reads the session's host at its next frame or message, a moment after the game names it. A change in
    // that moment is written by the restore that completes the host change.
    private void Publish(int index)
    {
        if (MapNet.Channel.IsHost) MapNet.TriggerZones.Set(index, JsonSerializer.SerializeToUtf8Bytes(Capture(index)));
    }

    /// <summary>The table's one handler. The host's own zones already hold what it wrote; a removed row is the level ending.</summary>
    private void RowChanged(int index, byte[]? row)
    {
        if (row == null || _disposed || _controller == null || _authority()) return;
        Receive(index, row);
        Advance(); WorkScheduled?.Invoke();
    }

    private void Receive(int index, byte[] row)
    {
        if (index >= _controller!.Count) { _report(ForgeEngine.Framework.ResultCode.TriggerZoneMissing); return; }
        if (Apply(index, JsonSerializer.Deserialize<TriggerZoneClockState>(row)!)) StateChanged(index);
    }
}
