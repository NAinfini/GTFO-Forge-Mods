using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>A checkpoint carries each placed zone's runtime state (enabled, visible, label, route clock, size). The
/// recall rebuilds the controller, so the saved states wait here and are applied to the new controller as it is placed.</summary>
internal sealed partial class TriggerZoneSession
{
    private const string CheckpointStateKey = "forge.map.trigger-zone";
    private DomainStateChannel.NativeStateSource? _checkpointState;
    private List<TriggerZoneClockState> _restored = new();

    private void RegisterCheckpointState()
        => _checkpointState = DomainStateChannel.RegisterNativeState(_kernel, CheckpointStateKey, CaptureCheckpoint, RestoreCheckpoint);

    private JsonElement CaptureCheckpoint()
    {
        var states = new List<TriggerZoneClockState>();
        if (_controller == null) states.AddRange(_restored);
        else
            for (var i = 0; i < _controller.Count; i++)
            {
                var zone = _controller.Zone(i);
                if (!zone.Placed || zone.Level != _placedLevel) continue;
                states.Add(Capture(i));
            }
        return JsonSerializer.SerializeToElement(states);
    }

    private void RestoreCheckpoint(JsonElement data)
    {
        if (_disposed) return;
        _restored = data.Deserialize<List<TriggerZoneClockState>>() ?? throw new FormatException("trigger-zone-state-format");
        ApplyRestored();
        // The host table is what every machine shows. The host writes what the restore gave it (a reload, or the image a
        // new host takes over); a client returns to the rows it holds, because a document that arrives after a row was
        // exported before it, and after a reload ForgeCore hands every client the whole table again.
        SyncNetwork();
    }

    /// <summary>Applies the saved states to the zones the current controller holds; run again after each placement.</summary>
    private void ApplyRestored()
    {
        if (_controller == null || _restored.Count == 0) return;
        for (var i = 0; i < _controller.Count; i++)
        {
            var id = _controller.Zone(i).Id;
            var saved = _restored.Find(state => state.ZoneId == id);
            if (saved != null) Apply(i, saved);
        }
    }

    /// <summary>One zone's state as the checkpoint and the host table carry it. A label that names a text resource
    /// travels by its id: every machine shows the text of its own language.</summary>
    private TriggerZoneClockState Capture(int index)
    {
        var state = _controller!.Motion(index).Capture();
        return state.LabelResourceId != null ? state with { LabelText = "" } : state;
    }

    private bool Apply(int index, TriggerZoneClockState state)
    {
        var result = _controller!.Motion(index).Apply(state, Now);
        if (result != ResultCode.Completed) { _report(result); return false; }
        if (!state.LabelVisible || state.LabelResourceId is not { } textId) return true;
        var zone = _controller.Zone(index);
        var block = GameData.GameDataBlockBase<GameData.TextDataBlock>.GetBlock(textId);
        if (block == null || block.WasCollected) { zone.LabelVisible = false; _report(TriggerZoneActionsContract.TextMissing); }
        else zone.LabelText = TextCardValue.Reference(TextResourceContract.TextValue.OfBlock(textId));
        return true;
    }
}
