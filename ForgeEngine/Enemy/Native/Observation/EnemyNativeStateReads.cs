using System;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

internal static class EnemyLimbAccess
{
    internal static Dam_EnemyDamageLimb Get(EnemyAgent enemy, int limbId)
    {
        if (limbId < 0) throw new RuntimeContractException("limb-id-out-of-range", "The native limb id is nonnegative.");
        var receiver = enemy.Damage;
        if (receiver == null || receiver.Owner?.Pointer != enemy.Pointer || !receiver.IsSetup)
            throw new RuntimeContractException(ResultCode.LimbUnavailable, "The enemy damage receiver is unavailable.");
        var limbs = receiver.DamageLimbs;
        Dam_EnemyDamageLimb? found = null;
        if (limbs != null) foreach (var limb in limbs)
        {
            if (limb == null || limb.Pointer == IntPtr.Zero || limb.m_limbID != limbId) continue;
            if (found != null) throw new RuntimeContractException(ResultCode.LimbIdAmbiguous, "The native limb id is duplicated.");
            if (limb.m_base?.Pointer != receiver.Pointer)
                throw new RuntimeContractException(ResultCode.LimbUnavailable, "The limb belongs to another damage receiver.");
            found = limb;
        }
        return found ?? throw new RuntimeContractException(ResultCode.LimbUnavailable, "The exact native limb does not exist.");
    }

    internal static (int Type, float Weakspot, float Armor) Answer(Dam_EnemyDamageLimb limb)
    {
        int type = (int)limb.m_type;
        float weakspot = limb.m_weakspotDamageMulti, armor = limb.m_armorDamageMulti;
        if (type is < 0 or > 2 || !float.IsFinite(weakspot) || !float.IsFinite(armor))
            throw new RuntimeContractException(ResultCode.LimbUnavailable, "The native limb fields are unreadable.");
        return (type, weakspot, armor);
    }
}

internal sealed partial class EnemyNodeValueReads
{
    private static void InCombat(EnemyNodeValueReads reads, EvaluationContext context,
        EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        var enemy = reads._enemyOf(reference);
        var ai = enemy?.AI;
        var behaviour = ai?.m_behaviour;
        if (enemy == null || behaviour == null || behaviour.m_ai?.Pointer != ai!.Pointer)
            throw new RuntimeContractException(StateUnavailableCode, "This enemy's behavior does not read.");
        int state = (int)behaviour.m_currentStateName;
        var value = EnemyModule.NativeInCombat(state);
        if (value == null || reads._enemyOf(reference)?.Pointer != enemy.Pointer
            || enemy.AI?.m_behaviour?.Pointer != behaviour.Pointer || (int)behaviour.m_currentStateName != state)
            throw new RuntimeContractException(StateUnavailableCode, "The behavior changed during the read.");
        outputs.Boolean(0, value.Value);
    }

    private static void Limb(EnemyNodeValueReads reads, EvaluationContext context,
        EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        if (context.Integer(EnemyNativeStateContract.LimbReadIdSlot) is not { } raw || raw < int.MinValue || raw > int.MaxValue)
            throw new RuntimeContractException("limb-id-out-of-range", "An Int32 native limb id is required.");
        int id = (int)raw;
        var enemy = reads._enemyOf(reference)
            ?? throw new RuntimeContractException(StateUnavailableCode, "This enemy life is unavailable.");
        var limb = EnemyLimbAccess.Get(enemy, id);
        var answer = EnemyLimbAccess.Answer(limb);
        if (reads._enemyOf(reference)?.Pointer != enemy.Pointer || EnemyLimbAccess.Get(enemy, id).Pointer != limb.Pointer)
            throw new RuntimeContractException(ResultCode.LimbUnavailable, "The limb changed during the read.");
        outputs.Enum(0, answer.Type);
        outputs.Number(1, answer.Weakspot);
        outputs.Number(2, answer.Armor);
    }
}
