using System;
using Agents;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>The three write actions of the player domain. Each one is a host action that ends in a game entry
/// point which replicates by itself, so this half never writes a field a client would have to guess at:
///
/// - damage submits the receiver's own sender that matches `damage_kind` — the packet-sending half of the game's
///   damage path: `direct` BulletDamage, `melee` MeleeDamage, `tentacle` TentacleAttackDamage, `projectile`
///   ShooterProjectileDamage, `explosion` ExplosionDamage, `fall` FallDamage, `dot` FireDamage — with the attacker the plan named
///   where the sender has an attacker argument, and the victim's own position. The amount the game lands is read
///   back from the receiver's health, so the row reports what happened and not what was asked for. Any other
///   `damage_kind` is refused and `limb` is refused because the entries have no limb argument; the game's own
///   receiver rules are the only mitigation this path applies.
/// - revive submits `AgentReplicatedActions.PlayerReviveAction`, the game's own host-authoritative revive action,
///   from the reviver the plan named to the downed target. The game owns restored health and replication.
/// - down submits the receiver's `SendSetDead` with the row's one structural parameter as its argument. The
///   argument is the game's own `allowRevive` flag, so the two behaviours the game has are two authored choices.
///
/// Every refusal happens before a write, and every write is confirmed by reading the life back: an effect that
/// cannot be observed is reported as an unknown commit, never as a success.</summary>
internal static class PlayerCommandActions
{
    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;

    // ---- forge.action.combat.damage ----------------------------------------------------------------------

    /// <summary>The native sender each supported `damage_kind` member is carried by. tank_grab and trap_grab have
    /// senders (`TentacleTankGrabDamage`, `TentacleTrapGrabDamage`) but they belong to a grab that is started and
    /// stopped as a whole, and no evidence shows one can be committed on its own; shrapnel, collision, environment and
    /// reflection have no reviewed player sender. They stay refused. dot is the game's own damage-over-time sender,
    /// `Dam_SyncedDamageBase.FireDamage(float, Agent)` (build 20403457, RVA 0x161e4c0), which carries the attacker and is
    /// observed back as `damage_kind=dot` / `attack_kind=fire` by the receive hook of the same entry.</summary>
    internal enum PlayerDamageRoute { Bullet, Melee, Tentacle, Projectile, Explosion, Fall, Fire }

    internal static bool TryRoute(string? kind, out PlayerDamageRoute route)
    {
        switch (kind)
        {
            case "direct": route = PlayerDamageRoute.Bullet; return true;
            case "melee": route = PlayerDamageRoute.Melee; return true;
            case "tentacle": route = PlayerDamageRoute.Tentacle; return true;
            case "projectile": route = PlayerDamageRoute.Projectile; return true;
            case "explosion": route = PlayerDamageRoute.Explosion; return true;
            case "fall": route = PlayerDamageRoute.Fall; return true;
            case "dot": route = PlayerDamageRoute.Fire; return true;
            default: route = default; return false;
        }
    }

    private static bool TakesHitOrigin(PlayerDamageRoute route)
        => route is PlayerDamageRoute.Bullet or PlayerDamageRoute.Melee or PlayerDamageRoute.Explosion;

    /// <summary>The kinds whose native entry is proved to work with no attacker: explosion, projectile and fall have no
    /// attacker argument, and FireDamage passes a null one through a path that does not dereference it (offline bodies in
    /// evidence/damage-kind-presentation.json, not yet confirmed in game). The direct, melee and tentacle bodies are unread.</summary>
    private static bool AllowsNoInstigator(PlayerDamageRoute route)
        => route is PlayerDamageRoute.Projectile or PlayerDamageRoute.Explosion or PlayerDamageRoute.Fall or PlayerDamageRoute.Fire;

    internal static CommandResult Damage(PlayerIdentityModule? players, CommandContext context,
        Func<EntityReference, global::Enemies.EnemyAgent?>? enemies = null)
    {
        if (players == null || !players.CanCommit)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        // Each kind the plan names goes to the native sender whose meaning matches it; a kind with no reviewed
        // sender is refused by name, never carried by another sender.
        if (!TryRoute(context.Text(PlayerCommandContract.DamageKindSlot), out var route))
            return Outcome.Rejected(ResultCode.DamageKindUnsupported);
        if (context.Present(PlayerCommandContract.DamageLimbSlot)) return Outcome.Rejected(ResultCode.LimbUnsupported);
        // The receiver's own entry has no multiplier arguments at all; both ports are refused by name so a plan
        // cannot believe it asked for a precision or stagger multiplier this path dropped.
        if (context.Present(PlayerCommandContract.DamagePrecisionSlot)) return Outcome.Rejected(ResultCode.PrecisionUnsupported);
        if (context.Present(PlayerCommandContract.DamageStaggerSlot)) return Outcome.Rejected(ResultCode.StaggerUnsupported);
        Vector3? hitOrigin = null;
        if (context.Present(PlayerCommandContract.DamageHitOriginSlot))
        {
            if (!TakesHitOrigin(route)) return Outcome.Rejected(ResultCode.HitOriginUnsupported);
            if (GameVectors.Unity(context.Vector(PlayerCommandContract.DamageHitOriginSlot)) is not { } origin)
                return Outcome.Rejected(ResultCode.HitOriginInvalid);
            hitOrigin = origin;
        }
        double amount = context.Number(PlayerCommandContract.DamageAmountSlot) ?? double.NaN;
        if (!double.IsFinite(amount) || amount < MinimumAmount || amount > MaximumAmount)
            return Outcome.Rejected(ResultCode.AmountOutOfRange);
        // The native attacker is the instigator, not the effect's optional source or its owner. The explosion,
        // shooter-projectile and fall senders carry no attacker argument at all; for them a wired instigator is still
        // resolved so a plan cannot name an attacker that does not exist, and the native packet is what it is.
        // An unwired port (Missing, not a value that answered null) is the plan saying nobody caused this hit: only
        // the kinds in AllowsNoInstigator take that. A wired instigator that cannot be found is refused for every
        // kind and never becomes "nobody", and nothing else stands in for it.
        global::Agents.Agent? nativeInstigator = null;
        if (context.Kind(PlayerCommandContract.DamageInstigatorSlot) == ValueKind.Missing)
        {
            if (!AllowsNoInstigator(route)) return Outcome.Rejected(ResultCode.InstigatorMissing);
        }
        else
        {
            if (!context.TryEntity(PlayerCommandContract.DamageInstigatorSlot, out var instigator)) return Outcome.Rejected(ResultCode.InstigatorMissing);
            nativeInstigator = players.CurrentAgent(instigator);
            nativeInstigator ??= enemies?.Invoke(instigator);
            if (nativeInstigator == null) return Outcome.Rejected(ResultCode.InstigatorMissing);
        }
        var command = new DamageCommand
        {
            Players = players,
            Route = route,
            Instigator = nativeInstigator,
            Amount = amount,
            HitOrigin = hitOrigin
        };
        return context.EachTarget(ref command, DamageStep, EntityKinds.Player, CommandResult.MaximumFacts, ResultCode.NativeCommitException,
            countColumn: Blocks.ActionCombatDamage.RowTargetCount);
    }

    private struct DamageCommand
    {
        internal PlayerIdentityModule Players;
        internal PlayerDamageRoute Route;
        internal global::Agents.Agent? Instigator;
        internal double Amount;
        internal Vector3? HitOrigin;
    }

    private static readonly TargetStep<DamageCommand> DamageStep = DamageTarget;

    private static void DamageTarget(ref DamageCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = DamageOne(command.Players, target, command.Route, command.Instigator, command.Amount, command.HitOrigin);
        rows.End(row.Status, row.Commit, row.Code);
    }

    private static Outcome DamageOne(PlayerIdentityModule players, EntityReference target, PlayerDamageRoute route, global::Agents.Agent? nativeInstigator, double amount, Vector3? hitOrigin)
    {
        if (!EntityKinds.Is(target, EntityKinds.Player)) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (!players.CanCommit) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!PlayerHealthReceiver.TryRead(players, target, out var before, out var code)) return Outcome.Rejected(code);
        if (!before.Alive) return Outcome.Rejected(ResultCode.NotAlive);
        // A life still standing with no health left has nothing a hit can take: the game's `OnIncomingDamage` floors
        // health at 0 and only downs the player from there, and for a player simulated on another peer the host's
        // `SetDead` zeroes the host's health before that peer's body goes down. Writing would only report an
        // unobservable hit, so the row is refused by name instead.
        if (before.Health <= 0f) return Outcome.Rejected(ResultCode.NoHealthLeft);
        var agent = players.CurrentAgent(target);
        if (agent == null || agent.Pointer != before.AgentPointer)
        {
            PlayerCaughtFailures.Note("damage-receiver", "target=" + target.Id + " the current agent is " + (agent == null ? "absent" : "not the agent the receiver was read from"));
            return Outcome.Rejected(ResultCode.StaleEntity);
        }
        Vector3 position;
        try { position = agent.Position; }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Outcome.Rejected(ResultCode.StaleEntity); }
        // `hit_origin` names where the hit came from and the receiver's own position is the hit point, so the
        // entry's direction argument is origin -> target. Without the port the row keeps the neutral direction it
        // submitted before the port existed.
        var direction = Vector3.zero;
        Vector3 explosionOrigin = Vector3.zero;
        if (route == PlayerDamageRoute.Melee || route == PlayerDamageRoute.Explosion)
        {
            // A melee or explosion with no named origin comes from the instigator itself, which is where the
            // attacker really is; the origin is read once, before the write. An explosion with neither is centred on
            // the receiver below.
            if (!hitOrigin.HasValue && nativeInstigator != null)
            {
                try { hitOrigin = nativeInstigator.Position; }
                catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.InstigatorMissing, error); return Outcome.Rejected(ResultCode.InstigatorMissing); }
                if (hitOrigin.Value == position) hitOrigin = null;
            }
        }
        if (hitOrigin.HasValue)
        {
            explosionOrigin = hitOrigin.Value;
            direction = position - hitOrigin.Value;
            if (!float.IsFinite(direction.x) || !float.IsFinite(direction.y) || !float.IsFinite(direction.z)
                || direction.sqrMagnitude <= 0f) return Outcome.Rejected(ResultCode.HitOriginInvalid);
            direction = direction.normalized;
        }
        // The receiver is resolved once more before the write: a life replaced since the preflight is refused
        // rather than written through, and so is one whose health moved under the preflight's reading.
        if (!PlayerHealthReceiver.TryRead(players, target, out var current, out var recode)) return Outcome.Rejected(recode);
        if (current != before)
        {
            bool replaced = current.AgentPointer != before.AgentPointer || current.ReceiverPointer != before.ReceiverPointer;
            PlayerCaughtFailures.Note("damage-receiver", FormattableString.Invariant($"target={target.Id} the receiver changed between preflight and write: health {before.Health}->{current.Health}, sameAgent={current.AgentPointer == before.AgentPointer} sameReceiver={current.ReceiverPointer == before.ReceiverPointer}"));
            return Outcome.Rejected(replaced ? ResultCode.ReceiverChangedDuringCommit : ResultCode.StateChangedBeforeCommit);
        }

        try
        {
            var damage = players.CurrentAgent(target)?.Damage;
            if (damage == null) return Outcome.Rejected(ResultCode.MissingHealthReceiver);
            if (damage.Pointer != before.ReceiverPointer) return Outcome.Rejected(ResultCode.ReceiverChangedDuringCommit);
            // The game's own god mode (`IgnoreAllDamage`) discards every hit before it touches health; that is a
            // known refusal by the receiver, so it is named instead of being reported as an unobserved write.
            if (damage.IgnoreAllDamage) return Outcome.Rejected(ResultCode.ReceiverIgnoresDamage);
            float requested = (float)amount;
            switch (route)
            {
                case PlayerDamageRoute.Bullet: ForgeCore.Combat.BulletDamage(damage, requested, nativeInstigator, position, direction, Vector3.zero); break;
                case PlayerDamageRoute.Melee: ForgeCore.Combat.Melee(damage, requested, nativeInstigator, position, direction); break;
                case PlayerDamageRoute.Tentacle: ForgeCore.Combat.Tentacle(damage, requested, nativeInstigator, position); break;
                case PlayerDamageRoute.Projectile: ForgeCore.Combat.Projectile(damage, requested, position); break;
                case PlayerDamageRoute.Explosion: ForgeCore.Combat.ExplosionDamage(damage, requested, hitOrigin.HasValue ? explosionOrigin : position, Vector3.zero); break;
                case PlayerDamageRoute.Fall: ForgeCore.Combat.Fall(damage, requested); break;
                case PlayerDamageRoute.Fire: ForgeCore.Combat.Fire(damage, requested, nativeInstigator); break;
                default: return Outcome.Rejected(ResultCode.DamageKindUnsupported);
            }
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return Outcome.FailedUnknown(ResultCode.NativeCommitException); }

        if (!PlayerHealthReceiver.TryRead(players, target, out var after, out _))
        {
            // A hit that downs or kills the player makes the receiver unreadable on purpose (as in `DownOne`), so the
            // life's own state is the settlement: the same life, no longer standing, took what health it had.
            if (players.CurrentAgent(target)?.Pointer != before.AgentPointer)
                return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
            if (players.LifeOf(target) is { } ended && (!ended.Alive || ended.Downed))
                return Outcome.Succeeded();
            return Outcome.FailedUnknown(ResultCode.ReadbackException);
        }
        if (after.AgentPointer != before.AgentPointer || after.ReceiverPointer != before.ReceiverPointer)
            return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
        double landed = before.Health - after.Health;
        // A damage that landed nothing is not a committed damage: the receiver may have refused it, or applied a
        // smaller amount than the game's own float encoding can hold. Both are unknown, never success.
        if (!(landed > 0)) return Outcome.FailedUnknown(ResultCode.DamageNotObserved);
        return Outcome.Succeeded();
    }

    // ---- forge.action.combat.revive ----------------------------------------------------------------------

    internal static CommandResult Revive(PlayerIdentityModule? players, CommandContext context)
    {
        var command = new ReviveCommand
        {
            Players = players,
            World = context.WorldEpoch,
            Source = context.TryEntity(PlayerCommandContract.ReviveSourceSlot, out var reference) ? reference : null
        };
        return context.EachTarget(ref command, ReviveStep, EntityKinds.Player, CommandResult.MaximumFacts, ResultCode.NativeCommitException,
            countColumn: Blocks.ActionCombatRevive.RowTargetCount);
    }

    private struct ReviveCommand
    {
        internal PlayerIdentityModule? Players;
        internal EntityReference? Source;
        internal long World;
    }

    private static readonly TargetStep<ReviveCommand> ReviveStep = ReviveTarget;

    private static void ReviveTarget(ref ReviveCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = ReviveOne(command.Players, target, command.Source, command.World);
        rows.End(row.Status, row.Commit, row.Code);
    }

    private static Outcome ReviveOne(PlayerIdentityModule? players, EntityReference target, EntityReference? sourceReference, long world)
    {
        if (players == null || !players.CanCommit) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!EntityKinds.Is(target, EntityKinds.Player)) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (target.WorldEpoch != world) return Outcome.Rejected(ResultCode.StaleEntity);
        if (!players.IsCurrent(target) || players.LifeOf(target) is not { } life) return Outcome.Rejected(ResultCode.StaleEntity);
        // Native DoPlayerRevive applies only to a not-Alive target whose locomotion is Downed.
        // Alive alone cannot distinguish a rescuable down from an ended life.
        if (!life.Alive && !life.Downed) return Outcome.Rejected(ResultCode.NotAlive);
        // The native revive callback may run before locomotion leaves Downed on its next update.
        if (!life.Downed || life.Revived) return Outcome.Rejected(ResultCode.NotDowned);
        if (sourceReference == null || !EntityKinds.Is(sourceReference, EntityKinds.Player)) return Outcome.Rejected(ResultCode.SourceMissing);
        if (sourceReference.WorldEpoch != world) return Outcome.Rejected(ResultCode.StaleEntity);
        if (!players.IsCurrent(sourceReference)) return Outcome.Rejected(ResultCode.StaleEntity);
        var source = players.CurrentAgent(sourceReference);
        if (source == null) return Outcome.Rejected(ResultCode.SourceMissing);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Outcome.Rejected(ResultCode.StaleEntity);
        var pointer = agent.Pointer;
        Vector3 where;
        try { where = agent.Position; }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); return Outcome.Rejected(ResultCode.ReadbackException); }

        try { AgentReplicatedActions.PlayerReviveAction(agent, source, where); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return Outcome.FailedUnknown(ResultCode.NativeCommitException); }

        try
        {
            if (players.CurrentAgent(target)?.Pointer != pointer || players.LifeOf(target) is not { } after)
                return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
            // OnPlayerRevived commits m_isRevived and native healthData before the animation restores Alive.
            // That flag is native settlement; independent effects still wait for the final standing state.
            if (!after.Revived && !(after.Alive && !after.Downed)) return Outcome.FailedUnknown(ResultCode.ReviveNotObserved);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); return Outcome.FailedUnknown(ResultCode.ReadbackException); }
        return Outcome.Succeeded();
    }

    // ---- forge.action.player.down ------------------------------------------------------------------------

    internal static CommandResult Down(PlayerIdentityModule? players, CommandContext context)
    {
        if (players == null || !players.CanCommit)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        bool allowRevive;
        switch (context.Text(PlayerCommandContract.DownRevivePolicySlot))
        {
            case "allowed": allowRevive = true; break;
            case "denied": allowRevive = false; break;
            default: throw new RuntimeContractException(ResultCode.RevivePolicyUnsupported, "Unknown revive policy.");
        }
        var command = new DownCommand { Players = players, AllowRevive = allowRevive };
        return context.EachTarget(ref command, DownStep, EntityKinds.Player, CommandResult.MaximumFacts, ResultCode.NativeCommitException,
            countColumn: Blocks.ActionPlayerDown.RowTargetCount);
    }

    private struct DownCommand
    {
        internal PlayerIdentityModule Players;
        internal bool AllowRevive;
    }

    private static readonly TargetStep<DownCommand> DownStep = DownTarget;

    private static void DownTarget(ref DownCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = DownOne(command.Players, target, command.AllowRevive);
        rows.End(row.Status, row.Commit, row.Code);
    }

    private static Outcome DownOne(PlayerIdentityModule players, EntityReference target, bool allowRevive)
    {
        if (!EntityKinds.Is(target, EntityKinds.Player)) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (!players.CanCommit) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (players.LifeOf(target) is not { } life) return Outcome.Rejected(ResultCode.StaleEntity);
        if (!life.Alive || life.Downed) return Outcome.Rejected(ResultCode.AlreadyDown);
        if (!PlayerHealthReceiver.TryRead(players, target, out var before, out var code)) return Outcome.Rejected(code);

        Dam_PlayerDamageBase? damage;
        bool here;
        try
        {
            var agent = players.CurrentAgent(target);
            damage = agent?.Damage;
            if (agent == null || damage == null) return Outcome.Rejected(ResultCode.MissingHealthReceiver);
            if (damage.Pointer != before.ReceiverPointer) return Outcome.Rejected(ResultCode.ReceiverChangedDuringCommit);
            here = ForgeCore.Players.SimulatedHere(agent.Owner);
            damage.SendSetDead(allowRevive);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return Outcome.FailedUnknown(ResultCode.NativeCommitException); }

        // The confirmation is the life's own state and not the health receiver: the game's dead transition makes
        // the receiver unreadable on purpose (its read refuses a life that is no longer alive), so a readback
        // through health would report every successful down as unknown.
        if (players.CurrentAgent(target)?.Pointer != before.AgentPointer) return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
        if (players.LifeOf(target) is not { } after) return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
        if (!after.Alive || after.Downed) return Outcome.Succeeded();
        // `SendSetDead` is the host's entry: it sends the set-dead packet and runs the receiver's `ReceiveSetDead` on
        // the host in the same call, which zeroes the host's authoritative health. Only the machine that simulates
        // the player (`Dam_PlayerDamageLocal.ReceiveSetDead`) then puts the body in the downed state, and the host's
        // copy of that state follows that machine's own locomotion sync. So for a player simulated elsewhere the
        // host's settlement is the health its receiver holds after the call; the body's state is not the host's to
        // read in this call.
        if (!here)
        {
            try { if (damage.Pointer == before.ReceiverPointer && damage.Health <= 0f) return Outcome.Succeeded(); }
            catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.DownNotObserved, error); }
        }
        return Outcome.FailedUnknown(ResultCode.DownNotObserved);
    }
}
