using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The composition half of the action families this package carries beside the node-list family: the
/// enemy control actions, the combat actions, the foam action, the behaviour actions and the profile's
/// `phase_set`, plus the one presentation audience this provider answers for.
///
/// Each family declares its own bindings, handlers and shapes next to the code that answers them. This file is
/// the one place those declarations are composed into the provider's registration, so a family cannot be
/// registered half-way — a handler whose shape is missing is refused at registration.
///
/// The foam family's handler lives on its own class rather than on this partial, so the module holds one instance
/// of it; it is built on the first foam request rather than in the constructor, because it is handed the
/// registration this module only has after `RegisterModule` returns.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The foam family's one handler and the ledger that honours a Forge-side foam lifetime.</summary>
    private GlueActions? _glue;
    private GlueActions Glue => _glue ??= new GlueActions(_registration, GlueTargetOf, GlueCleanupTargetOf,
        () => CanExecute, _kernel);

    /// <summary>What a foam request resolves a reference to: the module's own tracked life, so a reference no
    /// live enemy answers for is refused instead of being written through a stale pointer.</summary>
    private GlueTarget? GlueTargetOf(EntityReference reference)
    {
        var entry = Resolve(reference);
        return entry == null ? null : new GlueTarget(entry.Enemy, entry.EnemyPointer, entry.Enemy.Damage);
    }

    /// <summary>Resolves only cleanup already owned by this module. Registration may already be closed so new
    /// requests are refused, but the exact tracked life remains available until its contribution is read back as
    /// removed. This bypass never admits a command and never guesses a replacement life.</summary>
    private GlueTarget? GlueCleanupTargetOf(EntityReference reference)
    {
        var entry = ResolveTracked(reference);
        return entry == null ? null : new GlueTarget(entry.Enemy, entry.EnemyPointer, entry.Enemy.Damage);
    }

    /// <summary>Every handler the four action families register, in the order their rows are appended.</summary>
    internal Dictionary<string, CommandHandler> ActionFamilyHandlers()
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        // The control family's handler names are its declaration's, in the game-independent assembly; this half
        // supplies the one body per name it declares.
        handlers[EnemyControlContract.AwakenHandler] = Awaken;
        handlers[EnemyControlContract.SleepHandler] = Sleep;
        handlers[EnemyStationaryContract.Handler] = Stationary;
        handlers[EnemyWakeLockContract.Handler] = WakeLock;
        handlers[EnemyControlContract.MoveToHandler] = MoveTo;
        handlers[EnemyCombatContract.StaggerHandler] = Stagger;
        handlers[EnemyCombatContract.AttackInterruptHandler] = AttackInterrupt;
        // The two rows this provider binds to the game's own force channel and to its one damage funnel: a push
        // that carries no damage, and the standing rule the damage window applies to every hit after it.
        handlers[EnemyPushContract.HandlerName] = Push;
        handlers[CombatDamageRuleContract.HandlerName] = DamageRule;
        handlers[EnemyNativeStateContract.Handler(Blocks.ActionEnemyKnockdown.Id)] = Knockdown;
        handlers[EnemyNativeStateContract.Handler(Blocks.ActionEnemyLimbSet.Id)] = SetNativeLimb;
        handlers[GlueContract.FoamingHandler] = context => Glue.Foaming(context);
        // The volume family's one execute handler: the native effect volume and the fog sphere that draws it.
        handlers[VolumeHandler] = EffectVolume;
        foreach (var handler in BehaviorHandlers(this)) handlers[handler.Key] = handler.Value;
        // The profile family is one execute handler; its row and binding are declared in the root contract the
        // registration composes, so the two halves cannot drift.
        handlers[EnemyProfileContract.PhaseSetHandler] = PhaseSet;
        return handlers;
    }

    /// <summary>The sessions one `presentation` step of this provider is addressed to. The package's one
    /// presentation row is `forge.action.enemy.mark`, whose audience is the whole realm (ruling 122.3): every
    /// player sees a Forge mark, so the answer is the session hub's own player list rather than a second table of
    /// players kept here.
    ///
    /// The references the step's own `recipients` port carries name the enemies a mark is placed on, not the
    /// players it is shown to, which is why they do not narrow this answer. The sessions are the Player package's
    /// own list of the level's human clients: a bot has no client and no network session, so listing one would
    /// address the step to a peer that can never answer it. Null — no directory, or one that names nobody — is the
    /// refusal the kernel reports as `presentation-recipient`; a step nobody can be addressed with is never
    /// widened to an implicit everyone by this side.</summary>
    internal IReadOnlyList<string>? PresentationAudience(IReadOnlyList<EntityReference>? recipients)
    {
        _ = recipients;
        try { return ForgePlayer.Native.PlayerHost.Players is { } players ? players.EverySession() : null; }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
    }

    /// <summary>Drops the action families' state with the module that owns it: the foam ledger's timers and the
    /// effects it still holds are released here, and every effect volume this package registered is unregistered
    /// from the game's own manager, so a package that unloads mid-level leaves neither behind.</summary>
    internal void DisposeActionFamilies()
    {
        _glue?.Dispose();
        _glue = null;
        ReleaseVolumes();
    }

    /// <summary>Every foam volume one dispatch applied, taken back off the enemy it was applied to. Called by the
    /// kernel's own restore callback when the effect a dispatch applied ends — by its duration, by a cancellation,
    /// by a released plan or by the world — which is the only clock this row has: the foam family's handle is the
    /// kernel's own for the step, so a cancellation and an expiry are the same call. A module that never foamed has
    /// no ledger, and a handle that names nothing is the no-op it has to be.</summary>
    internal void RestoreFoam(RuntimeEffectContext context)
    {
        if (!context.HandleId.IsCast) return;
        _glue?.ClearFoamed(context.HandleId);
    }
}
