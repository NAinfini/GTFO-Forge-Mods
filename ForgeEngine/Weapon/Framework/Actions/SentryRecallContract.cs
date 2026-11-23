using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The one native half behind <see cref="SentryRecallContract"/>: it recalls the sentries one player placed
/// and answers with the same three outcomes the detonation row uses. A committed answer may carry its own code (a
/// player with nothing placed is a successful no-op with its own row code); an empty code is the shared commit code.</summary>
internal interface ISentryRecallSource
{
    DetonateOutcome Recall(EntityReference player, out string code);
}

/// <summary>`forge.action.inventory.recall_sentry`: the sentries the named players placed go back to their backpacks.
/// Scope is the `players` input — every player through the players selector, or one player. One row per named player;
/// one player's outcome never stops the next, because repeating a pickup request changes nothing.
///
/// The row is the generated block table's. The body has the shape of <see cref="DetonateWrites"/>: a process with no
/// native half declares the row and refuses it by code.</summary>
internal static class SentryRecallContract
{
    public const string CapabilityId = Blocks.ActionInventoryRecallSentry.Id;
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.inventory_recall_sentry";
    public const string HandlerName = "gtfo.inventory.recall_sentry";

    public static readonly HandlerShape Shape = new HandlerShape().Inputs("players").Outputs("result");
    public static readonly HandlerSlot PlayersSlot = Shape.InputSlot("players");

    private static ISentryRecallSource? _source;

    /// <summary>Attaches one session's source; the returned action detaches exactly that source, so a disposal
    /// cannot detach a session that replaced it.</summary>
    public static Action Attach(ISentryRecallSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        return () => { if (ReferenceEquals(_source, source)) _source = null; };
    }

    public static IReadOnlyDictionary<string, CommandHandler> Handlers() => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    {
        [HandlerName] = Recall
    };

    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, HandlerName, "execute", new[] { DetonateContract.WritePermission }, Shape)
    };

    /// <summary>One row per named player through the action skeleton; the row's own column is the number of players
    /// the step named.</summary>
    private static CommandResult Recall(CommandContext context)
    {
        if (_source is not { } source) return Outcome.Rejected(ResultCode.RecallSentrySourceUnavailable);
        return context.EachTarget(ref source, Step, maximum: CommandResult.MaximumFacts, exceptionCode: ResultCode.NativeCommitException,
            countColumn: Blocks.ActionInventoryRecallSentry.RowTargetCount);
    }

    private static readonly TargetStep<ISentryRecallSource> Step = static (ref ISentryRecallSource source, EntityReference player, int index, RowWriter rows) =>
    {
        var outcome = source.Recall(player, out var code);
        switch (outcome)
        {
            case DetonateOutcome.Committed: rows.End(Status.Succeeded, Commit.Confirmed, code.Length == 0 ? ResultCode.Committed : code); break;
            case DetonateOutcome.Rejected: rows.End(Status.Rejected, Commit.None, code); break;
            default: rows.End(Status.Failed, Commit.Unknown, code); break;
        }
    };
}
