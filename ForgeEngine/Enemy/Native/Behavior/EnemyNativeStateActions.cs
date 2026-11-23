using System;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    internal CommandResult Knockdown(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        var module = this;
        return context.EachTarget(ref module, KnockdownStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyKnockdown.RowTargetCount);
    }

    private static readonly TargetStep<EnemyModule> KnockdownStep = static (ref EnemyModule module, EntityReference target, int _, RowWriter rows)
        => rows.End(module.ForceKnockdown(target));

    private BehaviorOutcome ForceKnockdown(EntityReference target)
    {
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        if (!entry.Enemy.Alive) return BehaviorOutcome.Refused(ResultCode.NotAlive);
        var locomotion = entry.Enemy.Locomotion;
        if (locomotion == null || locomotion.m_agent?.Pointer != entry.EnemyPointer)
            return BehaviorOutcome.Refused(ResultCode.MissingLocomotion);
        var state = locomotion.Knockdown;
        if (state == null || state.Pointer == IntPtr.Zero) return BehaviorOutcome.Refused(ResultCode.NoKnockdownState);
        try { locomotion.ForceState(state); }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
        try
        {
            if (Resolve(target) != entry || entry.Enemy.Locomotion?.Pointer != locomotion.Pointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            return locomotion.CurrentStateEnum == ES_StateEnum.Knockdown ? BehaviorOutcome.Committed()
                : BehaviorOutcome.Unseen(ResultCode.UnexpectedStateReadback);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
    }

    internal CommandResult SetNativeLimb(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        var rawId = context.Integer(LimbIdSlot);
        var weakspot = context.Number(LimbWeakspotSlot);
        var armor = context.Number(LimbArmorSlot);
        int type = context.Text(LimbTypeSlot) switch { "normal" => 0, "weakspot" => 1, "armor" => 2, _ => -1 };
        if (rawId is not { } id || id < 0 || id > int.MaxValue) return CommandResult.Rejected(ResultCode.LimbIdOutOfRange);
        if (type < 0) return CommandResult.Rejected(ResultCode.LimbTypeInvalid);
        if (weakspot is not { } weak || armor is not { } arm || weak < 0 || arm < 0
            || !float.IsFinite((float)weak) || !float.IsFinite((float)arm)) return CommandResult.Rejected(ResultCode.LimbMultiplierInvalid);
        var request = new LimbRequest(this, (int)id, type, (float)weak, (float)arm, ActionEffectHandle(context));
        return context.EachTarget(ref request, LimbStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyLimbSet.RowTargetCount);
    }

    private static readonly HandlerSlot LimbIdSlot = EnemyNativeStateContract.LimbSetShape.InputSlot("limb_id");
    private static readonly HandlerSlot LimbWeakspotSlot = EnemyNativeStateContract.LimbSetShape.InputSlot("weakspot_multiplier");
    private static readonly HandlerSlot LimbArmorSlot = EnemyNativeStateContract.LimbSetShape.InputSlot("armor_multiplier");
    private static readonly HandlerSlot LimbTypeSlot = EnemyNativeStateContract.LimbSetShape.InputSlot("limb_type");

    private readonly record struct LimbRequest(EnemyModule Module, int Id, int Type, float Weakspot, float Armor, RuntimeHandle? Handle);

    private static readonly TargetStep<LimbRequest> LimbStep = static (ref LimbRequest request, EntityReference target, int _, RowWriter rows)
        => rows.Integer(Blocks.ActionEnemyLimbSet.RowLimbId, request.Id)
            .End(request.Module.WriteNativeLimb(request, target));

    private BehaviorOutcome WriteNativeLimb(in LimbRequest request, EntityReference target)
    {
        int id = request.Id, type = request.Type;
        float weakspot = request.Weakspot, armor = request.Armor;
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        if (!entry.Enemy.Alive) return BehaviorOutcome.Refused(ResultCode.NotAlive);
        Dam_EnemyDamageLimb limb;
        try { limb = EnemyLimbAccess.Get(entry.Enemy, id); }
        catch (RuntimeContractException error) { return BehaviorOutcome.Refused(error.Code); }
        var installed = InstallActionState(new ActionState { Entry = entry, Kind = ActionStateKind.Limb, LimbId = id, Limb = limb },
            new(Type: type, Weakspot: weakspot, Armor: armor), request.Handle);
        if (installed.Commit != Commit.Confirmed)
            return installed.Code == ResultCode.StateBaselineUnavailable ? BehaviorOutcome.Refused(ResultCode.LimbUnavailable) : installed;
        try
        {
            if (Resolve(target) != entry || EnemyLimbAccess.Get(entry.Enemy, id).Pointer != limb.Pointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            return (int)limb.m_type == type && limb.m_weakspotDamageMulti == weakspot && limb.m_armorDamageMulti == armor
                ? BehaviorOutcome.Committed() : BehaviorOutcome.Unseen(ResultCode.UnexpectedLimbReadback);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
    }
}
