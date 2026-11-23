using System.Collections.Generic;
using ForgeEngine.Framework;
using Player;
using SNetwork;

namespace ForgeEngine.GameBindings;

/// <summary>Player identity and session conversion, supplied by the owner of the player domain.
/// Null means the specified life or session cannot be resolved; an absent provider is a named contract refusal.</summary>
public interface IPlayerDirectory
{
    EntityReference? ReferenceOf(SNet_Player? player);
    EntityReference? ReferenceOf(PlayerAgent? agent);
    PlayerAgent? AgentOf(EntityReference reference);
    SNet_Player? PlayerOf(EntityReference reference);
    RuntimePlayerLife? LifeOf(EntityReference reference);
    string? SessionOf(SNet_Player? player);
    IReadOnlyList<string>? SessionsOf(IReadOnlyList<EntityReference>? recipients);
    IReadOnlyList<string>? EverySession();
    bool IsLocal(string? session);
    PlayerAgent? LocalAgent { get; }
}

public readonly record struct RuntimePlayerLife(bool Alive, bool Downed, bool Revived);

/// <summary>How another plugin obtains the player directory the engine publishes.</summary>
public static class PlayerDirectoryService
{
    public const string MissingCode = "player-directory-missing";

    public static IPlayerDirectory Require(IForgeEngine kernel)
        => kernel.TryService<IPlayerDirectory>(out var players) ? players
            : throw new RuntimeContractException(MissingCode, "The player-directory provider is not installed or has not registered.");
}

/// <summary>One player recipient of the canonical `forge.action.combat.heal`: the exact life, the actor the
/// healing is attributed to (with the native instance the caller resolved for it, when it has one), and the
/// request's amount, ceiling and structural policy. The owner of the player domain judges the recipient.</summary>
internal readonly record struct PlayerHealRequest(EntityReference Target, EntityReference Source, object? SourceInstance,
    double Amount, double? Cap, string OverhealPolicy, bool CancelRegen);

/// <summary>How one player recipient ended: the row status, the commit state, the code that names why, and the
/// health the same receiver actually gained.</summary>
internal readonly record struct PlayerHealReceipt(Status Status, Commit Commit, string Code, double Amount);
