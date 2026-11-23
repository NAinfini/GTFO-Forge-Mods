using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The one action of the effect-volume family: `forge.action.combat.effect_volume`, the native volume
/// `EffectVolumeManager` keeps registered against the sphere the game's own `EV_Sphere` describes.
///
/// Each submission does two native things and reports both:
/// <list type="number">
/// <item>It builds an `EV_Sphere` at the anchor's own position, fills in the four choices the row carries —
/// `contents` and `modification` are the game's own `eEffectVolumeContents` and `eEffectVolumeModification`,
/// `radius_min`/`radius_max` are the two distances `ComputeAmount` interpolates between, and `scale` is
/// `modificationScale` — and registers it with `EffectVolumeManager.RegisterVolume`. That registration is what
/// makes the volume real: the manager's own update loop asks every registered volume what it does to every
/// registered target, and the target's `ReceiveModification` applies it. Nothing in this package writes a
/// target's health or infection itself.</item>
/// <item>It allocates the game's own fog sphere (`FogSphereAllocator`) at the same position and outer radius, so
/// the volume has a visible body. That half is presentation: the game's own sphere budget can be full, and a
/// submission whose volume registered is still committed when it did. The result's own `code` column carries
/// which of the two happened, so an author can see that a volume is running without being drawn.</item>
/// </list>
///
/// Both native steps are the shared body's (`ForgeEnemy/Native/Observation/GameAreaVolumes.cs`), and what is left
/// after a placement — which volume follows its anchor, which ones a kernel handle releases — is the shared
/// ledger's (`ForgeEngine/Framework/Observation/AreaVolumePlacement.cs`). Neither is written a second time for the field
/// provider that places volumes of the same kind, which is the whole reason both live in the SDK. What stays here
/// is this row's own half: its four choices, its result columns and its refusal codes.
///
/// The step runs on the host only, and every machine holds its own copy of what the host placed: the placements
/// travel in the action-state snapshot (`EnemyModule.VolumeState.cs`), so the fog is drawn and the effect reaches the
/// local player on every machine alike.
///
/// A volume with `follow_anchor` is moved to its anchor's own position by the frame pump that already runs for the
/// tag, and every volume one dispatch placed is unregistered — with its fog sphere returned — when the effect that
/// dispatch applied ends. The ledger is the record of what is still registered, so a package that unloads
/// mid-level releases every volume it placed.
///
/// The row carries no lifetime port: the step's own `effect` block is the clock (I-PLAN §3.4), the kernel casts
/// the handle, and `RestoreVolume` is the callback it calls when that effect ends — by its duration, by a
/// cancellation, by a released plan or by the world. A volume is filed under the kernel's own handle for the
/// step, which is also what the row publishes as `volumes` when the card asked for a cancel handle; a card with
/// no `effect` block names no handle, and its volumes live until the module releases them. Every request is
/// checked before the first anchor, because a submission that cannot be carried out as asked must not
/// half-apply.</summary>
internal sealed partial class EnemyModule
{
    internal const string VolumeBinding = EnemyVolumeContract.VolumeBinding;
    internal const string VolumeHandler = EnemyVolumeContract.VolumeHandler;

    /// <summary>The scale bounds and nonnegative inner radius. The outer radius uses the shared Single length
    /// domain.</summary>
    private const double MinimumVolumeScale = 0.0, MaximumVolumeScale = 1000.0;
    private const double MinimumVolumeRadius = 0.0;

    /// <summary>Every volume this provider has placed and not yet released, and the kernel handle each one is
    /// filed under: `null` for a card whose step carried no effect, so the volume lives until the module lets it
    /// go. The ledger is the shared one — the same code the field provider places its own volumes through — and
    /// the native sphere, fog and manager calls behind it are the shared game body's.</summary>
    internal readonly AreaVolumeLedger _volumes = new(GameAreaVolumes.Shared);

    /// <summary>Every volume filed under one handle, released. Called by the kernel's own restore callback when
    /// the effect a dispatch applied ends; a handle that names nothing — an effect the module already released, or
    /// one whose volumes all failed to place — is the no-op it has to be.</summary>
    internal void RestoreVolume(RuntimeEffectContext context)
    {
        if (!context.HandleId.IsCast) return;
        try { _volumes.Release(context.HandleId); }
        finally { VolumesChanged(); }
    }

    /// <summary>The ledger changed under the host: the specs of released volumes go, and the snapshot every machine
    /// places its own copies from is written again.</summary>
    private void VolumesChanged()
    {
        if (_volumeSpecs.Count != 0)
        {
            var held = new HashSet<long>();
            for (int index = 0; index < _volumes.Count; index++) held.Add(_volumes.Entry(index).Id);
            foreach (var id in new List<long>(_volumeSpecs.Keys)) if (!held.Contains(id)) _volumeSpecs.Remove(id);
        }
        ActionStateChanged();
    }

    /// <summary>`forge.action.combat.effect_volume`. The request check runs before the first anchor, so a
    /// submission that cannot be carried out as asked writes nothing; every volume of one dispatch is filed under
    /// the kernel's handle for the step, which is what the effect's own ending calls the restore callback with.</summary>
    internal CommandResult EffectVolume(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        if (!TryChoices(context, out var choices, out var choicesCode))
            return CommandResult.Rejected(choicesCode);
        var followValue = context.Boolean(EnemyVolumeContract.FollowAnchorSlot);
        if (context.Present(EnemyVolumeContract.FollowAnchorSlot) && followValue == null)
            return CommandResult.Rejected(ResultCode.FollowFlagInvalid);
        // The kernel cast this handle for the step before the handler ran, and it is what the kernel calls the
        // restore callback back with. A card with no `effect` block has none: the volumes are still placed and
        // stay in the ledger, which the module's own release and the world's end both drain.
        var request = new VolumeRequest(this, choices, followValue == true, context.EffectHandleId);
        var outcome = context.EachTarget(ref request, VolumeStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatEffectVolume.RowTargetCount);
        if (request.Placed) ActionStateChanged();
        // A volume that is registered but whose fog sphere could not be allocated is still a committed volume, so
        // it counts as one. The `volumes` handle is the kernel's own, published into that port when the card asked
        // for a cancel handle, so the handler names nothing itself.
        return outcome;
    }

    private struct VolumeRequest
    {
        internal VolumeRequest(EnemyModule module, AreaVolumeChoices choices, bool follow, RuntimeHandle? handle)
        {
            Module = module; Choices = choices; Follow = follow; Handle = handle; Placed = false;
        }
        internal readonly EnemyModule Module;
        internal readonly AreaVolumeChoices Choices;
        internal readonly bool Follow;
        internal readonly RuntimeHandle? Handle;
        /// <summary>Whether any anchor of the dispatch placed a volume.</summary>
        internal bool Placed;
    }

    private static readonly TargetStep<VolumeRequest> VolumeStep = static (ref VolumeRequest request, EntityReference anchor, int _, RowWriter rows)
        => request.Module.VolumeOne(ref request, anchor, rows);

    /// <summary>One anchor. A placement whose fog sphere the game's own budget refused keeps the same status and
    /// commit state as a drawn one — it is a committed volume — and says which of the two happened in the `code`
    /// column, because the row's schema has exactly those five columns and no sixth of its own.</summary>
    private void VolumeOne(ref VolumeRequest request, EntityReference anchor, RowWriter rows)
    {
        var entry = Resolve(anchor);
        if (entry == null) { rows.End(Status.Rejected, UnresolvedCode(anchor)); return; }
        var enemy = entry.Enemy;
        Vector3 position;
        try
        {
            if (!enemy.Alive) { rows.End(Status.Rejected, ResultCode.NotAlive); return; }
            position = enemy.Position;
        }
        catch (Exception error) { NativeFault.Note("enemy.volume.position", error); rows.End(Status.Rejected, ResultCode.EnemyPositionUnavailable); return; }
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
        { rows.End(Status.Rejected, ResultCode.EnemyPositionUnavailable); return; }

        var where = new[] { (double)position.x, (double)position.y, (double)position.z };
        if (!_volumes.TryPlace(anchor, where, request.Choices, request.Follow, request.Handle, out var volumeId, out var drawn))
        { rows.End(Status.Failed, Commit.None, ResultCode.NativeCommitException); return; }
        // The manager now holds the sphere, so a receiver that changed under this dispatch would leave a
        // volume tracking an instance no plan can name any more. `Resolve` proves the instance's own pointer is
        // still the one the reference names, so this is the same check the entry was read with.
        if (Resolve(anchor) == null)
        {
            _volumes.Release(volumeId);
            rows.End(Status.Rejected, ResultCode.ReceiverChangedDuringCommit);
            return;
        }
        RecordVolume(volumeId, anchor, request.Choices, request.Follow, request.Handle);
        // The volume is registered either way; the code says whether the fog sphere could be drawn for it,
        // which is the one thing about the submission the row's own five columns can still report.
        rows.End(Status.Succeeded, drawn ? ResultCode.VolumePlaced : ResultCode.VolumePlacedUndrawn);
        request.Placed = true;
    }

    /// <summary>The per-tick half of the volume ledger: every volume that follows its anchor is moved to where the
    /// anchor now is. PERF(F4): it runs once per kernel tick from the module's lifecycle observer (`PumpTick`), not
    /// once per enemy behaviour update, so its cost no longer scales with the number of awake enemies. A volume
    /// whose step carried no effect is still moved by it until the module lets it go.
    ///
    /// A follow whose anchor is gone keeps the volume where it last was: an anchor no plan can name is not a
    /// position this pump may invent, and `Resolve` is what proves the reference still names a live instance before
    /// any position is read.</summary>
    internal void PumpVolumes()
    {
        for (int index = _volumes.Count - 1; index >= 0; index--)
        {
            var record = _volumes.Entry(index);
            if (!record.Follow) continue;
            var entry = Resolve(record.Anchor);
            if (entry == null) continue;
            try
            {
                var position = entry.Enemy.Position;
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) continue;
                double x = position.x, y = position.y, z = position.z;
                // PERF(F4): an anchor that has not moved since the last move is skipped. The body is already there,
                // so the native move and the ledger's own copy were pure cost. The ledger stores the position it was
                // handed, so this compares like with like.
                var last = record.Position;
                if (last.Count == 3 && last[0] == x && last[1] == y && last[2] == z) continue;
                _pumpPosition[0] = x; _pumpPosition[1] = y; _pumpPosition[2] = z;
                _volumes.MoveAt(index, _pumpPosition);
            }
            catch (Exception error) { NativeFault.Note("enemy.volume.follow", error); }
        }
    }

    /// <summary>PERF(F4): the one scratch position `PumpVolumes` hands to the ledger, instead of one array per volume
    /// per pump. It is safe to reuse because `AreaVolumeLedger.MoveAt` stores a copy and the game body does not keep
    /// the list.</summary>
    private readonly double[] _pumpPosition = new double[3];

    /// <summary>Drops every volume this package still holds, for the module that is going away. A package that
    /// unloads mid-level must not leave a volume registered with a manager that outlives it.</summary>
    internal void ReleaseVolumes() => ClearVolumeState();

    // ---- the row's own four choices ---------------------------------------------------------------------------

    /// <summary>The `contents`, `modification`, `scale` and the two radii as the shared vocabulary and the game's
    /// own arithmetic describe them, each read once and refused by its own code, so an author who wrote a member
    /// the game does not have is told which one instead of being told the volume failed.</summary>
    private static bool TryChoices(CommandContext context, out AreaVolumeChoices choices, out string code)
    {
        choices = new AreaVolumeChoices("", "", 0, 0, 0);
        code = ResultCode.ContentsUnknown;
        if (!TryContents(context, out var contents)) return false;
        code = ResultCode.ModificationUnknown;
        if (!TryModification(context, out var modification)) return false;
        if (contents == "fog" && modification != "inflict") { code = ResultCode.FogShieldUnsupported; return false; }
        code = ResultCode.ScaleOutOfRange;
        if (!TryScale(context, contents == "fog", out var scale)) return false;
        code = ResultCode.RadiusOutOfRange;
        if (!TryRadius(context, out var minRadius, out var maxRadius)) return false;
        if (contents == "fog" && minRadius != 0) { code = ResultCode.FogInnerRadiusUnsupported; return false; }
        choices = new AreaVolumeChoices(contents, modification, scale, minRadius, maxRadius);
        code = "";
        return true;
    }

    /// <summary>The `contents` parameter as one of the shared vocabulary's members. A value outside it is refused
    /// by name rather than mapped onto a neighbouring member.</summary>
    private static bool TryContents(CommandContext context, out string contents)
    {
        contents = AreaVolumeChoices.ContentsMembers[0];
        var member = context.Text(EnemyVolumeContract.ContentsSlot);
        foreach (var candidate in AreaVolumeChoices.ContentsMembers)
            if (candidate == member) { contents = candidate; return true; }
        return false;
    }

    /// <summary>The `modification` parameter as one of the shared vocabulary's members.</summary>
    private static bool TryModification(CommandContext context, out string modification)
    {
        modification = AreaVolumeChoices.ModificationMembers[0];
        var member = context.Text(EnemyVolumeContract.ModificationSlot);
        foreach (var candidate in AreaVolumeChoices.ModificationMembers)
            if (candidate == member) { modification = candidate; return true; }
        return false;
    }

    private static bool TryScale(CommandContext context, bool fog, out double scale)
    {
        var number = context.Number(EnemyVolumeContract.ScaleSlot);
        scale = 0;
        if (number == null) return false;
        if (!double.IsFinite(number.Value) || number.Value < MinimumVolumeScale
            || number.Value > (fog ? float.MaxValue : MaximumVolumeScale)) return false;
        scale = number.Value;
        return true;
    }

    /// <summary>The two radii, in the one check that keeps them a sphere: the inner radius may not exceed the
    /// outer one, because the game's own answer at or beyond the outer radius is "no effect" and an inner radius
    /// past it would name an amount nothing could ever receive.</summary>
    private static bool TryRadius(CommandContext context, out double minRadius, out double maxRadius)
    {
        minRadius = 0; maxRadius = 0;
        var minimumValue = context.Number(EnemyVolumeContract.RadiusMinSlot);
        var maximumValue = context.Number(EnemyVolumeContract.RadiusMaxSlot);
        if (minimumValue == null || maximumValue == null) return false;
        var minimum = minimumValue.Value;
        var maximum = maximumValue.Value;
        if (minimum < MinimumVolumeRadius || maximum <= MinimumVolumeRadius
            || maximum > AreaVolumeChoices.MaximumRadius || minimum > maximum) return false;
        minRadius = minimum; maxRadius = maximum;
        return true;
    }

}
