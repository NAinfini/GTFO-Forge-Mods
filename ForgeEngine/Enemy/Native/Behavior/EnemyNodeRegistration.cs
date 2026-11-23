using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The registration half of the node-list family this batch implements: the three observation bindings
/// its trigger rows publish through, the shapes of the nine handlers it adds, and the methods the module's own
/// constructor calls to compose all of it into its registration.
///
/// It exists so the wiring is one place rather than nine. The bindings themselves are declared next to the code
/// that answers them — the trigger bindings in `EnemyNodeTriggerContract`, the action bindings in
/// `EnemyNodeEffectContract`, the query bindings in `EnemyNodeValueContract` — and this file only names them.
///
/// One of the rows here is a canonical row this provider binds rather than owns: `forge.trigger.enemy.spawned`
/// belongs to the trigger contract's provider, which registers before any domain package.
///
/// The action families written by earlier slices — the enemy control actions, the combat actions, the foam action
/// and the behaviour actions — are composed beside this family rather than inside it: `EnemyActionFamilies` is the
/// one place their bindings, handlers and shapes join the same registration, and `EnemyModule`'s
/// constructor merges both tables.</summary>
internal sealed partial class EnemyModule
{
    internal static readonly string NodeTaggedBinding = EnemyNodeTriggerContract.TaggedBinding;
    internal static readonly string NodeGluedBinding = EnemyNodeTriggerContract.GluedBinding;

    /// <summary>The value source this module's query rows are answered through, attached for the module's own
    /// lifetime and detached when it goes. A session that never attached one refuses every value row by name
    /// instead of answering from an empty table.</summary>
    private EnemyNodeValueReads? _valueReads;

    /// <summary>The owning provider alone resolves the current native instance. Player entity numbers are private
    /// lifecycle identities, not game GlobalIDs; never decode them into the game's agent table.</summary>
    private Func<EntityReference, dynamic?> _agentOf = _ => null;

    /// <summary>The four node actions and the six value rows, each paired with the capability its shape was
    /// resolved against.</summary>
    internal Dictionary<string, CommandHandler> NodeActionHandlers() => new(StringComparer.Ordinal)
    {
        [KillHandler] = Kill,
        [RemoveHandler] = Remove,
        [MarkHandler] = Mark,
        [TargetHandler] = Target
    };

    /// <summary>The value rows' evaluators, one per query handler name.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> NodeEvaluators() => EnemyNodeValueReads.Evaluators();

    /// <summary>Every handler this provider's registration carries beyond the ones `EnemyModule` already built:
    /// the three node actions and the six value rows of this family. The four action families are the same kind
    /// of table, declared in their own contracts and merged into the registration by `EnemyActionFamilies`.</summary>
    internal Dictionary<string, CommandHandler> AllNodeHandlers() => NodeActionHandlers();

    /// <summary>Attaches the value source for this module's lifetime and registers the marker pump. Called once,
    /// where the module is built; a second call would install a second resolver for the same rows. The agent
    /// lookup may be handed over at the same time, which is how this package's focused suite drives the `target`
    /// row's accepted path with the double's own agent table.</summary>
    internal void AttachNodeFamily(IForgeEngine kernel, Func<EntityReference, dynamic?>? agentOf = null)
    {
        _valueReads = EnemyNodeValueReads.Attach(kernel, reference => Resolve(reference)?.Enemy, reference => Resolve(reference)?.Type,
            reference => Resolve(reference) is { SpawnModeKnown: true } entry ? entry.SpawnMode : null);
        _agentOf = agentOf ?? kernel.EntityInstance;
    }

    /// <summary>Detaches the value source and drops every marker this module placed, so a package that unloads
    /// mid-level leaves no marker behind that nothing can name.</summary>
    internal void DetachNodeFamily()
    {
        RemoveEveryMarker();
        if (_valueReads != null) EnemyNodeValueReads.Detach(_valueReads);
        _valueReads = null;
    }
}
