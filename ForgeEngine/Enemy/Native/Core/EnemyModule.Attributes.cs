using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    private sealed class EnemyAttributeContribution
    {
        internal uint Id;
        internal Entry Entry = null!;
        internal AgentModifier Modifier;
        internal float? Observed;
        internal RuntimeHandle? Handle;
    }

    private readonly List<EnemyAttributeContribution> _enemyAttributes = new();

    /// <summary>The enemy recipient of the canonical attribute action, called by the Player domain's modifier adapter.</summary>
    internal EnemyAttributeReceipt ApplyEnemyAttributes(EntityReference target,
        IReadOnlyList<EnemyAttributeValue> members, double? cap, RuntimeHandle? handle)
    {
        CheckThread();
        static EnemyAttributeReceipt Refused(string code) => new(Status.Rejected, Commit.None, code, 0);
        static EnemyAttributeReceipt Unknown(string code) => new(Status.Failed, Commit.Unknown, code, 0);
        if (!CanExecute) return Refused(ResultCode.AuthorityOrPhase);
        var entry = Resolve(target);
        if (entry == null) return Refused(UnresolvedCode(target));
        if (!entry.Enemy.Alive) return Refused(ResultCode.NotAlive);
        if (members == null || members.Count == 0) return Refused(ResultCode.AttributeUnknown);
        if (cap.HasValue && (!double.IsFinite(cap.Value) || cap.Value == 0)) return Refused(ResultCode.InvalidCap);
        foreach (var member in members)
        {
            // Only these two enum members have a verified enemy consumer: ES_PathMoveBase reads both before
            // writing native navigation speed/acceleration. A valid player-only enum is not an enemy attribute.
            if (member.Modifier is not (250 or 251)) return Refused(ResultCode.AttributeUnsupported);
            if (!float.IsFinite(member.Amount) || member.Amount == 0 || Math.Abs((double)member.Amount) > 1000000)
                return Refused(ResultCode.AmountOutOfRange);
        }

        var taken = new List<EnemyAttributeContribution>(members.Count);
        double reported = 0;
        foreach (var member in members)
        {
            var modifier = (AgentModifier)member.Modifier;
            double total = 0;
            foreach (var held in _enemyAttributes)
            {
                if (!ReferenceEquals(held.Entry, entry) || held.Modifier != modifier) continue;
                if (!held.Observed.HasValue) return Unknown(ResultCode.ModifierReadbackUnavailable);
                total += held.Observed.Value;
            }
            double bounded = cap.HasValue
                ? cap.Value > 0 ? Math.Min(total + member.Amount, cap.Value) : Math.Max(total + member.Amount, cap.Value)
                : total + member.Amount;
            float contribution = (float)(bounded - total);
            string? refusal = !float.IsFinite(contribution) ? ResultCode.AmountOutOfRange
                : contribution == 0 ? ResultCode.CapReached
                : !CanExecute ? ResultCode.AuthorityOrPhase
                : Resolve(target) != entry ? ResultCode.ReceiverChangedDuringCommit
                : !entry.Enemy.Alive ? ResultCode.NotAlive : null;
            if (refusal != null)
                return RevokeEnemyAttributeMembers(taken) ? Refused(refusal) : Unknown(ResultCode.NativeClearException);
            uint id;
            try { id = ForgeCore.AgentModifiers.Add(entry.Enemy, modifier, contribution); }
            catch (Exception caught) { EnemyLog.Contained(caught); return Unknown(ResultCode.NativeCommitException); }
            if (id == 0)
                return RevokeEnemyAttributeMembers(taken) ? Refused(ResultCode.ModifierIdExhausted) : Unknown(ResultCode.NativeClearException);
            // File the id before reading native state: a readback failure still needs cancellation and world cleanup.
            var applied = new EnemyAttributeContribution { Id = id, Entry = entry, Modifier = modifier, Handle = handle };
            _enemyAttributes.Add(applied);
            taken.Add(applied);
            try
            {
                if (!TryReadEnemyAttribute(applied, out var actual)) return Unknown(ResultCode.ModifierReadbackUnavailable);
                applied.Observed = actual;
                if (taken.Count == 1) reported = actual;
                if (!CanExecute || Resolve(target) != entry || !entry.Enemy.Alive)
                    return Unknown(ResultCode.ReceiverChangedDuringCommit);
                // Native modifier limits may accept less than requested. A real, finite nonzero cached value
                // confirms that actual contribution; zero confirms no effect and revokes the whole member set.
                if (actual == 0)
                    return RevokeEnemyAttributeMembers(taken) ? Refused(ResultCode.ModifierNoEffect) : Unknown(ResultCode.NativeClearException);
            }
            catch (Exception caught) { EnemyLog.Contained(caught); return Unknown(ResultCode.ReadbackException); }
        }
        return new(Status.Succeeded, Commit.Confirmed, ResultCode.Committed, reported);
    }

    private static bool TryReadEnemyAttribute(EnemyAttributeContribution contribution, out float actual)
    {
        actual = 0;
        if (!ForgeCore.AgentModifiers.TryRead(contribution.Entry.Enemy, contribution.Id, contribution.Modifier, out var value)) return false;
        // Cached is exactly the signed value the native aggregate holds and Clear subtracts. The current value
        // is time-dependent; our zero decay does not need a second clock or a predicted aggregate.
        if (!float.IsFinite(value.Cached)) throw new InvalidOperationException("Native enemy modifier ownership or value changed.");
        actual = value.Cached;
        return true;
    }

    private bool ReleaseEnemyAttribute(EnemyAttributeContribution contribution)
    {
        try
        {
            // Read before clearing to refuse an id whose native modifier no longer matches our ownership.
            // Still send the native clear for a locally absent id: its synchronized peers may retain it.
            TryReadEnemyAttribute(contribution, out _);
            ForgeCore.AgentModifiers.Clear(contribution.Id);
            if (TryReadEnemyAttribute(contribution, out _)) return false;
            _enemyAttributes.Remove(contribution);
            return true;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return false; }
    }

    private bool RevokeEnemyAttributeMembers(List<EnemyAttributeContribution> members)
    {
        bool restored = true;
        for (int index = members.Count - 1; index >= 0; index--)
            restored &= ReleaseEnemyAttribute(members[index]);
        return restored;
    }

    internal void RestoreEnemyAttributes(RuntimeHandle handle)
    {
        CheckThread();
        bool restored = true;
        for (int index = _enemyAttributes.Count - 1; index >= 0; index--)
            if (_enemyAttributes[index].Handle == handle) restored &= ReleaseEnemyAttribute(_enemyAttributes[index]);
        if (!restored) throw new InvalidOperationException("native-clear-exception");
    }

    internal void ForgetEnemyAttributes(EntityReference target)
    {
        CheckThread();
        bool restored = true;
        for (int index = _enemyAttributes.Count - 1; index >= 0; index--)
            if (_enemyAttributes[index].Entry.Reference == target)
                restored &= ReleaseEnemyAttribute(_enemyAttributes[index]);
        if (!restored) throw new InvalidOperationException("native-clear-exception");
    }

    internal void ClearEnemyAttributes()
    {
        CheckThread();
        bool restored = true;
        for (int index = _enemyAttributes.Count - 1; index >= 0; index--)
            restored &= ReleaseEnemyAttribute(_enemyAttributes[index]);
        if (!restored) throw new InvalidOperationException("native-clear-exception");
    }

    // The module registration owns service removal; this only releases this receiver's native contributions.
    internal void DisposeEnemyAttributes() => ClearEnemyAttributes();
}
