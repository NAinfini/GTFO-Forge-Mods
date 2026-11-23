using System;
using System.Collections.Generic;
using Agents;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    internal bool DamageRulesActive
    {
        get
        {
            foreach (var state in _actionStates)
                if (state.Kind == ActionStateKind.DamageRule && state.Current.Policy.Policy != IncomingDamagePolicy.Clear) return true;
            return false;
        }
    }
    internal CommandResult DamageRule(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        IncomingDamagePolicy policy;
        try { policy = IncomingDamagePolicy.Read(context); }
        catch (RuntimeContractException error) { return CommandResult.Rejected(error.Code, error.Message); }
        if (policy.Source is { } source && !_kernel.IsEntityCurrent(source)) return CommandResult.Rejected(ResultCode.StaleSourceFilter);
        var handle = context.EffectHandleId;
        // Targets that are every player present install the one standing policy that also covers a player who joins or respawns later.
        var everyone = ForgePlayer.Native.PlayerIncomingDamageRules.Installed is { } shared
            && AreEveryPlayer(context.Entities(CombatDamageRuleContract.TargetsSlot)) ? shared : null;
        IncomingDamageReceipt? standing = everyone?.InstallEveryPlayer(policy, handle);
        // A rule covers enemies and players alike, so the skeleton is asked for no one kind.
        var request = new DamageRuleRequest(this, policy, handle, everyone, standing);
        var outcome = context.EachTarget(ref request, DamageRuleStep, null,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatDamageRule.RowTargetCount);
        context.Outputs.Number(Blocks.ActionCombatDamageRule.OutRejected, request.Blocked);
        return outcome;
    }

    private struct DamageRuleRequest
    {
        internal DamageRuleRequest(EnemyModule module, IncomingDamagePolicy policy, RuntimeHandle? handle,
            ForgePlayer.Native.PlayerIncomingDamageRules? everyone, IncomingDamageReceipt? standing)
        { Module = module; Policy = policy; Handle = handle; Everyone = everyone; Standing = standing; Blocked = 0; }
        internal readonly EnemyModule Module;
        internal readonly IncomingDamagePolicy Policy;
        internal readonly RuntimeHandle? Handle;
        internal readonly ForgePlayer.Native.PlayerIncomingDamageRules? Everyone;
        internal readonly IncomingDamageReceipt? Standing;
        /// <summary>What the rules the rows report had blocked, summed: the step's `rejected` output.</summary>
        internal double Blocked;
    }

    private static readonly TargetStep<DamageRuleRequest> DamageRuleStep = static (ref DamageRuleRequest request, EntityReference target, int _, RowWriter rows) =>
    {
        var receipt = request.Standing is { } every ? Standing(request.Everyone!, every, target, request.Policy, request.Handle)
            : request.Module.InstallDamageRule(target, request.Policy, request.Handle);
        request.Blocked += receipt.Rejected;
        rows.Number(Blocks.ActionCombatDamageRule.RowRejected, receipt.Rejected)
            .End(receipt.Status, receipt.Commit, receipt.Code);
    };

    /// <summary>True when every target is a player and together they are exactly the players the game currently has. The player list is the
    /// same candidate source `forge.selector.target.players` reads; when it cannot be read the targets are treated one by one.</summary>
    private bool AreEveryPlayer(EntityReference[] targets)
    {
        var present = new HashSet<EntityReference>(targets);
        foreach (var target in present)
            if (!EntityKinds.Is(target, EntityKinds.Player)) return false;
        var players = _kernel.EnumerateEntityCandidates(EntityKinds.Player);
        if (!players.IsComplete || players.Items.Count != present.Count) return false;
        foreach (var item in players.Items)
            if (!present.Contains(item.Reference)) return false;
        return true;
    }

    /// <summary>One row of the standing every-player install. A clear also removes the target's own rule, which the standing policy
    /// would otherwise never replace, and reports what that rule had blocked.</summary>
    private static IncomingDamageReceipt Standing(ForgePlayer.Native.PlayerIncomingDamageRules players, IncomingDamageReceipt standing,
        EntityReference target, IncomingDamagePolicy policy, RuntimeHandle? handle)
    {
        if (standing.Commit != Commit.Confirmed || policy.Policy != IncomingDamagePolicy.Clear) return standing;
        var own = players.Install(target, policy, handle);
        return own.Commit == Commit.Confirmed ? own : standing;
    }

    private IncomingDamageReceipt InstallDamageRule(EntityReference target, IncomingDamagePolicy policy, RuntimeHandle? handle)
    {
        IncomingDamageReceipt Refused(string code) => new(Status.Rejected, Commit.None, code, 0);
        var entry = Resolve(target);
        if (entry == null)
            return EntityKinds.Is(target, EntityKinds.Player) && ForgePlayer.Native.PlayerIncomingDamageRules.Installed is { } players
                ? players.Install(target, policy, handle) : Refused(UnresolvedCode(target));
        var receiver = entry.Enemy.Damage;
        if (receiver == null || !receiver.IsSetup || receiver.Pointer == IntPtr.Zero) return Refused(ResultCode.MissingHealthReceiver);
        if (receiver.Owner?.Pointer != entry.EnemyPointer) return Refused(ResultCode.HealthReceiverOwnerMismatch);
        try { if (!entry.Enemy.Alive || !(receiver.Health > 0)) return Refused(ResultCode.NotAlive); }
        catch (Exception caught) { EnemyLog.Contained(caught); return Refused(ResultCode.HealthReceiverUnavailable); }
        if (policy.Source is { } source && _kernel.EntityInstance(source) is not Agent)
            return Refused(ResultCode.SourceFilterUnavailable);
        if (policy.Source is { } actor && EntityKinds.Is(actor, EntityKinds.Player)
            && ForgePlayer.Native.PlayerHost.Session?.Module.BindCurrent(actor)?.Invoke() != true)
            return Refused(ResultCode.SourceFilterUnavailable);
        double blocked = 0;
        foreach (var state in _actionStates)
            if (state.Kind == ActionStateKind.DamageRule && ReferenceEquals(state.Entry, entry))
            { blocked = state.Layers.Count == 0 ? 0 : state.Layers[^1].Blocked; break; }
        var outcome = InstallActionState(new ActionState
        {
            Entry = entry,
            Kind = ActionStateKind.DamageRule,
            Damage = receiver,
            Baseline = new(Policy: new IncomingDamagePolicy(IncomingDamagePolicy.Clear, 0, 1, null, null))
        },
            new(Policy: policy), handle);
        return new(outcome.Status, outcome.Commit, outcome.Code, blocked);
    }

    internal void RestoreDamageRule(RuntimeEffectContext context)
    {
        if (!context.HandleId.IsCast) return;
        var handle = context.HandleId;
        try { RestoreActionHandle(handle, ActionStateKind.DamageRule); }
        finally { if (ForgePlayer.Native.PlayerIncomingDamageRules.Installed is { } players) players.Restore(handle); }
    }

    private void ForgetDamageRule(Entry entry)
    {
        for (int index = _actionStates.Count - 1; index >= 0; index--)
            if (_actionStates[index].Kind == ActionStateKind.DamageRule && ReferenceEquals(_actionStates[index].Entry, entry))
                RemoveActionState(index);
    }
    private void ClearDamageRules()
    {
        for (int index = _actionStates.Count - 1; index >= 0; index--)
            if (_actionStates[index].Kind == ActionStateKind.DamageRule) RemoveActionState(index);
        if (ForgePlayer.Native.PlayerIncomingDamageRules.Installed is { } players) players.Clear();
    }

    /// <summary>The amount a received hit lands with after the rule standing on its receiver. <paramref name="unruled"/> is ForgeCore's
    /// mark that this settlement is a kill (`Combat.Kill`): a rule that would change it is skipped, with one log line saying so.</summary>
    internal float ApplyDamageRule(Dam_EnemyDamageBase receiver, float incoming, Agent? sourceAgent = null, bool unruled = false)
    {
        if (_actionStates.Count == 0 || receiver == null || !float.IsFinite(incoming) || incoming <= 0 || !CanExecute) return incoming;
        foreach (var state in _actionStates)
        {
            if (state.Kind != ActionStateKind.DamageRule || state.Damage?.Pointer != receiver.Pointer) continue;
            if (!ActionReceiverCurrent(state) || !state.Entry.Enemy.Alive) return incoming;
            var policy = state.Current.Policy;
            if (policy.Policy == IncomingDamagePolicy.Clear) return incoming;
            // A filter retains its exact life. A pooled native pointer alone cannot keep a dead source valid.
            if (state.SourceReference != null && (!state.SourceCurrent
                || !ActionSourceCurrent(state) || state.Source == null || sourceAgent?.Pointer != state.Source.Pointer)) return incoming;
            if (policy.DamageKind is { } kind && kind != EffectiveDamageKind(receiver)) return incoming;
            float adjusted = policy.Adjust(incoming, receiver.Health);
            if (unruled)
            {
                if (adjusted != incoming && Log.Logs(RuntimeLogLevel.Info))
                    Log.Write(RuntimeLogLevel.Info, EnemyLogCodes.DamageRuleSkippedForKill, new RuntimeLogFacts
                    {
                        Entity = state.Entry.Reference.Id,
                        Values = new[] { RuntimeLogValue.Of("incoming", (double)incoming), RuntimeLogValue.Of("ruled", (double)adjusted) },
                        Detail = "A kill action's settlement is not rewritten by the damage rule standing on this enemy; the rule would have changed the amount."
                    });
                return incoming;
            }
            if (state.Layers.Count != 0) state.Layers[^1].Blocked += Math.Max(0, incoming - adjusted);
            return adjusted;
        }
        return incoming;
    }
}
