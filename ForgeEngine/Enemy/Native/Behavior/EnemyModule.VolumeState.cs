using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>
/// Every machine holds the effect volumes the host placed. `effect_volume` runs on the host only, but the native volume
/// manager and the fog spheres are each machine's own objects, so a volume the host alone holds is a fog the teammates
/// never see and an effect their own player never receives. The host's placements therefore travel in the action-state
/// snapshot (`volumes`, beside the enemy lives they anchor to): each machine places its own copy of what the host
/// holds and releases what the host released, and a volume that follows its anchor is moved by the local pump with
/// the local enemy. A copy anchored to an enemy this machine has not spawned yet waits for it.
/// </summary>
internal sealed partial class EnemyModule
{
    /// <summary>What a placed volume was placed with, and the key two machines agree on for it: the kernel handle, the
    /// anchor's id, the four choices and the follow flag. The ledger keeps neither the choices nor a machine-neutral
    /// identity, and the snapshot diff needs both.</summary>
    private sealed record VolumeSpec(AreaVolumeChoices Choices, RuntimeHandle? Handle, string Key);
    private sealed record SavedVolume(EntityReference? Anchor, string AnchorId, RuntimeHandle? Handle, AreaVolumeChoices Choices,
        bool Follow, double[] Position)
    {
        internal string Key => VolumeKey(Handle, AnchorId, Choices, Follow);
    }

    private readonly Dictionary<long, VolumeSpec> _volumeSpecs = new();
    private readonly List<SavedVolume> _savedVolumes = new();
    private bool _volumesSynced;

    private static string VolumeKey(RuntimeHandle? handle, string anchorId, AreaVolumeChoices choices, bool follow)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Join("|", handle is { } value ? value.ToString() : "", anchorId, choices.Contents, choices.Modification, choices.Scale.ToString("R", inv),
            choices.MinRadius.ToString("R", inv), choices.MaxRadius.ToString("R", inv), follow ? "1" : "0");
    }

    private void RecordVolume(long id, EntityReference anchor, AreaVolumeChoices choices, bool follow, RuntimeHandle? handle)
        => _volumeSpecs[id] = new VolumeSpec(choices, handle, VolumeKey(handle, anchor.Id, choices, follow));

    /// <summary>The snapshot's `volumes` rows, naming each live anchor so the client binds the same lives the action
    /// states bind.</summary>
    private List<object> CaptureVolumes(Action<Entry?> name)
    {
        var rows = new List<object>();
        for (int index = 0; index < _volumes.Count; index++)
        {
            var volume = _volumes.Entry(index);
            if (!_volumeSpecs.TryGetValue(volume.Id, out var spec)) continue;
            var anchor = Resolve(volume.Anchor);
            if (anchor != null) name(anchor);
            rows.Add(new
            {
                anchor = anchor == null ? null : anchor.ActionLife ?? anchor.Reference,
                anchorId = volume.Anchor.Id,
                handle = spec.Handle?.ToJson(),
                contents = spec.Choices.Contents,
                modification = spec.Choices.Modification,
                scale = spec.Choices.Scale,
                radiusMin = spec.Choices.MinRadius,
                radiusMax = spec.Choices.MaxRadius,
                follow = volume.Follow,
                x = volume.Position[0],
                y = volume.Position[1],
                z = volume.Position[2]
            });
        }
        return rows;
    }

    private static SavedVolume ReadSavedVolume(JsonElement row, long worldEpoch, IReadOnlyDictionary<ushort, ActionLife> lives)
    {
        ActionSnapshotShape(row, "anchor anchorId handle contents modification scale radiusMin radiusMax follow x y z");
        EntityReference? anchor = null;
        if (row.GetProperty("anchor").ValueKind != JsonValueKind.Null)
        {
            anchor = RuntimeJson.Entity(row.GetProperty("anchor"));
            RequireActionSnapshot(anchor.WorldEpoch == worldEpoch && lives.TryGetValue(ActionLifeId(anchor), out var life) && life.Target == anchor,
                "enemy-action-life", "A volume names an enemy life the snapshot does not admit.");
        }
        double Number(string field)
        {
            double number = row.GetProperty(field).GetDouble();
            RequireActionSnapshot(double.IsFinite(number), "enemy-action-snapshot", "Nonfinite volume value.");
            return number;
        }
        var handle = row.GetProperty("handle");
        RuntimeHandle restored = default;
        RequireActionSnapshot(handle.ValueKind == JsonValueKind.Null || RuntimeHandle.TryRead(handle, out restored) && restored.IsCast,
            "enemy-action-snapshot", "A volume handle is a Runtime handle or null.");
        string contents = row.GetProperty("contents").GetString() ?? "", modification = row.GetProperty("modification").GetString() ?? "";
        RequireActionSnapshot(Array.IndexOf(AreaVolumeChoices.ContentsMembers, contents) >= 0
            && Array.IndexOf(AreaVolumeChoices.ModificationMembers, modification) >= 0, "enemy-action-snapshot", "Unknown volume contents or modification.");
        double scale = Number("scale"), minimum = Number("radiusMin"), maximum = Number("radiusMax");
        RequireActionSnapshot(scale >= 0 && minimum >= 0 && maximum > 0 && minimum <= maximum, "enemy-action-snapshot", "Volume scale or radius is out of range.");
        return new SavedVolume(anchor, row.GetProperty("anchorId").GetString() ?? "", handle.ValueKind == JsonValueKind.Null ? null : restored,
            new AreaVolumeChoices(contents, modification, scale, minimum, maximum), row.GetProperty("follow").GetBoolean(),
            new[] { Number("x"), Number("y"), Number("z") });
    }

    /// <summary>Makes this machine's volumes the host's: releases the ones the snapshot no longer holds and places the
    /// ones it holds that this machine lacks, once their anchor's life is bound here. Nothing is released or placed
    /// on a machine that never took a snapshot, so a host's own volumes are never touched.</summary>
    private void ReconcileVolumes()
    {
        if (!_volumesSynced) return;
        try
        {
            var unmatched = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var saved in _savedVolumes) unmatched[saved.Key] = unmatched.GetValueOrDefault(saved.Key) + 1;
            var held = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = _volumes.Count - 1; index >= 0; index--)
            {
                long id = _volumes.Entry(index).Id;
                if (!_volumeSpecs.TryGetValue(id, out var spec)) continue;
                int wanted = unmatched.GetValueOrDefault(spec.Key), have = held.GetValueOrDefault(spec.Key);
                if (have < wanted) { held[spec.Key] = have + 1; continue; }
                _volumes.Release(id);
                _volumeSpecs.Remove(id);
            }
            foreach (var saved in _savedVolumes)
            {
                string key = saved.Key;
                if (held.TryGetValue(key, out var left) && left > 0) { held[key] = left - 1; continue; }
                // An anchor the host no longer had is a still volume: no enemy of this machine can stand for it.
                var anchor = new EntityReference(saved.AnchorId, _kernel.WorldEpoch, long.MaxValue);
                if (saved.Anchor != null)
                {
                    if (!_actionLives.TryGetValue(ActionLifeId(saved.Anchor), out var life) || life.Target != saved.Anchor
                        || life.Local is not { } local || life.AwaitSpawn || !IsLive(local) || !local.Enemy.Alive) continue;
                    anchor = local.Reference;
                }
                if (_volumes.TryPlace(anchor, saved.Position, saved.Choices, saved.Follow, saved.Handle, out var placed, out _))
                    _volumeSpecs[placed] = new VolumeSpec(saved.Choices, saved.Handle, key);
            }
        }
        catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, error); }
    }

    private void TakeSavedVolumes(List<SavedVolume> volumes)
    {
        _savedVolumes.Clear(); _savedVolumes.AddRange(volumes);
        _volumesSynced = true;
        ReconcileVolumes();
    }

    /// <summary>A host that took over from a snapshot owns its volumes from now on: they stay, and the saved list stops
    /// naming what this machine may release.</summary>
    private void EndVolumeSync() { _volumesSynced = false; _savedVolumes.Clear(); }

    private void ClearVolumeState()
    {
        EndVolumeSync();
        try { _volumes.ReleaseAll(); }
        finally { _volumeSpecs.Clear(); }
    }
}
