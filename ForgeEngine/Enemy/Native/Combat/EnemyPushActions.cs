using System;
using ForgeEngine.Framework;
using ForgePlayer;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The native half of <see cref="EnemyPushContract"/>: one push per named enemy, with no damage of its
/// own.
///
/// <b>Which native entry carries it.</b> `Dam_EnemyDamageBase.SendExplosionForce(Vector3 origin, float
/// forceMagnitude)` — a force and nothing else. The magnitude is the length of the force the row shapes, and the
/// origin is the point the force is directed away from. `Dam_SyncedDamageBase.PushDamage` is not used: its only
/// scalar is damage, and its receiver hands the packet to `ProcessReceivedDamage` like every other damage receiver.
///
/// <b>The origin.</b> The entry takes the point a force pushes away from. The origin is therefore one unit behind
/// the enemy along the force — `position - normalize(force)` — so that the force the receiver derives points exactly
/// along it and the distance behind the enemy is the same for every recipient.
///
/// <b>What a row can claim.</b> The entry returns void and the game keeps no readback of the force it applied, so a
/// submitted push is answered `push-submitted-unobserved`: the request reached the enemy's own receiver, and whether
/// the body moved (glued, climbing, in a stationary state) is not observed.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>`forge.action.combat.impulse` for enemy recipients: one native force submission per recipient, in
    /// the plan's own order. Every refusal that can be decided before the native call is decided before it, so a
    /// rejected recipient never leaves a half-submitted push behind.</summary>
    internal CommandResult Push(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        if (!CombatImpulseContract.TryForce(context, out double x, out double y, out double z, out string? code))
            return Outcome.Rejected(code!);
        double length = Math.Sqrt(x * x + y * y + z * z);
        float magnitude = (float)length;
        float offsetX = (float)(x / length), offsetY = (float)(y / length), offsetZ = (float)(z / length);
        if (!float.IsFinite(magnitude) || magnitude <= 0 || !float.IsFinite(offsetX) || !float.IsFinite(offsetY) || !float.IsFinite(offsetZ))
            return Outcome.Rejected(ResultCode.ImpulseDirectionInvalid);
        var request = new PushRequest(this, offsetX, offsetY, offsetZ, magnitude);
        return context.EachTarget(ref request, PushStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatImpulse.RowTargetCount);
    }

    private readonly record struct PushRequest(EnemyModule Module, float OffsetX, float OffsetY, float OffsetZ, float Magnitude);

    private static readonly TargetStep<PushRequest> PushStep = static (ref PushRequest request, EntityReference target, int _, RowWriter rows)
        => request.Module.PushOne(request, target, rows);

    private void PushOne(in PushRequest request, EntityReference target, RowWriter rows)
    {
        var entry = Resolve(target);
        if (entry == null) { rows.End(Status.Rejected, UnresolvedCode(target)); return; }
        var enemy = entry.Enemy;
        var receiver = enemy.Damage;
        if (receiver == null || !receiver.IsSetup || receiver.Pointer == IntPtr.Zero)
        { rows.End(Status.Rejected, ResultCode.MissingHealthReceiver); return; }
        if (receiver.Owner == null || receiver.Owner.Pointer != entry.EnemyPointer)
        { rows.End(Status.Rejected, ResultCode.HealthReceiverOwnerMismatch); return; }
        var receiverPointer = receiver.Pointer;
        Vector3 position;
        try
        {
            if (!enemy.Alive) { rows.End(Status.Rejected, ResultCode.NotAlive); return; }
            position = enemy.Position;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Rejected, ResultCode.EnemyPositionUnavailable); return; }
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
        { rows.End(Status.Rejected, ResultCode.EnemyPositionUnavailable); return; }

        // Entering the native entry point is the only place the world is written. The call is one dispatch and
        // the life it was made against is its only proof, so a failure after it stays unknown.
        var origin = new Vector3(position.x - request.OffsetX, position.y - request.OffsetY, position.z - request.OffsetZ);
        try { ForgeCore.Combat.Force(receiver, origin, request.Magnitude); }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Failed, ResultCode.NativeCommitException); return; }
        try
        {
            if (!CanExecute) { rows.End(Status.Failed, ResultCode.AuthorityOrPhase); return; }
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || enemy.Damage == null
                || enemy.Damage.Pointer != receiverPointer)
            { rows.End(Status.Failed, ResultCode.ReceiverChangedDuringCommit); return; }
        }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Failed, ResultCode.ReadbackException); return; }
        rows.End(Status.Succeeded, ResultCode.PushSubmittedUnobserved);
    }
}
