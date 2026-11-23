using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgePlayer.Native;

/// <summary>
/// The one answer to "which entity, agent, life or session is this player", published as <see cref="IPlayerDirectory"/>
/// on the Player registration. Map and Weapon reach players only through it instead of keeping a second player table,
/// and it leaves with that registration.
///
/// The entity half is <see cref="PlayerIdentityModule"/>'s: the agent a reference resolves to, then the
/// `SNet_Player` that agent's own `Owner` points at. Every read goes through the identity half this directory was
/// built on, and answers nothing once that half's registration is gone, so a disposed session names nobody rather
/// than answering from a stale table.
///
/// A session is the address a presentation or owner command is sent to. Its one spelling is the runtime's
/// <see cref="PlayerAddress"/> — the game's own player slot — which is also what the host's command routing
/// compares an arriving request with, so the directory and the routing cannot drift into two spellings of one
/// player. `SNet_Player.Lookup` is the account id and stays what it is elsewhere in this package: a private identity
/// key that is compared and never spelled.
/// </summary>
internal sealed class PlayerDirectory : IPlayerDirectory
{
    private PlayerIdentityModule? _module;

    /// <summary>Attaches the identity half of the started Player session, or detaches it (null) when that session ends.</summary>
    internal void Attach(PlayerIdentityModule? module) => _module = module;

    /// <summary>Whether a Player session is running, so a caller can name its own refusal instead of catching this one.</summary>
    internal bool Running => _module != null;

    /// <summary>The attached identity half. While no Player session is running every read is refused with
    /// <see cref="PlayerDirectoryService.MissingCode"/>.</summary>
    private PlayerIdentityModule Module => _module ?? throw Missing();

    private static RuntimeContractException Missing()
        => new(PlayerDirectoryService.MissingCode, "The Player domain is not running.");

    /// <summary>The identity half, or null once its registration is gone: a read through a released half would
    /// name a life no registration owns.</summary>
    private PlayerIdentityModule? Live => Module.IsRegistered ? Module : null;

    internal long IdentityRevision => Module.IdentityRevision;

    public EntityReference? ReferenceOf(SNet_Player? player) => player == null ? null : Live?.ReferenceOf(player);

    public EntityReference? ReferenceOf(PlayerAgent? agent) => agent == null ? null : Live?.ReferenceOf((object)agent);

    public PlayerAgent? AgentOf(EntityReference reference) => Live?.CurrentAgent(reference);

    /// <summary>The `SNet_Player` a recorded life belongs to, or null when this process cannot reach it. The
    /// identity module answers with the agent; the agent's own `Owner` is the player the life was recorded for,
    /// and every comparison is made against that object, never against a copy of its key.</summary>
    public SNet_Player? PlayerOf(EntityReference reference)
    {
        var agent = AgentOf(reference);
        if (agent == null) return null;
        var owner = agent.Owner;
        return owner != null && owner.Pointer != IntPtr.Zero ? owner : null;
    }

    public RuntimePlayerLife? LifeOf(EntityReference reference) => Live?.LifeOf(reference);

    public string? SessionOf(SNet_Player? player) => _module != null ? PlayerAddress.Of(player) : throw Missing();

    /// <summary>The sessions one presentation step is addressed to, from the entity references the step's own
    /// `recipients` port carries. It is the one answer the runtime's presentation routing asks this provider for,
    /// so an environment row and a player row are routed by the same conversion: the entity a plan named, the
    /// player it resolves to, and that player's own session id. A named recipient that cannot be converted refuses
    /// the whole answer — addressing the rest of the list would present a step to players the plan did not ask for
    /// — and null is what the routing refuses by name instead of widening the step to everyone.
    /// A bot is a real player a plan may name (every "all players" selector lists the team's bots in a solo run),
    /// but it has no client and no screen: it is a recipient with nothing to present to, not one that failed to
    /// resolve, so it is left out of the session list. A list made only of bots answers an empty list, which the
    /// runtime reports as a step with no viewer rather than as one that could not be routed.</summary>
    public IReadOnlyList<string>? SessionsOf(IReadOnlyList<EntityReference>? recipients)
    {
        if (recipients == null || recipients.Count == 0) return null;
        var sessions = new List<string>(recipients.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in recipients)
        {
            var player = PlayerOf(reference);
            if (player == null) return null;
            if (player.IsBot) continue;
            if (SessionOf(player) is not { } session) return null;
            if (seen.Add(session)) sessions.Add(session);
        }
        sessions.Sort(StringComparer.Ordinal);
        return sessions;
    }

    /// <summary>Every non-bot player-client session this machine can name in the level, or null when it can name none. It is the
    /// address list a step about the whole level rather than one player is routed with (the Map package's
    /// map-object presentation rows): the game's own player list, read the way the identity half reads it, so a
    /// player who is in the level is addressed and one who is not is not.</summary>
    public IReadOnlyList<string>? EverySession()
    {
        if (_module == null) throw Missing();
        int count = Players.Count;
        var sessions = new SortedSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            var agent = Players.At(index);
            if (agent == null || agent.Owner == null || agent.Owner.IsBot) continue;
            if (SessionOf(agent.Owner) is { } session) sessions.Add(session);
        }
        return sessions.Count == 0 ? null : new List<string>(sessions);
    }

    public bool IsLocal(string? session)
        => _module == null ? throw Missing() : !string.IsNullOrEmpty(session) && SNet.HasLocalPlayer
            && string.Equals(SessionOf(SNet.LocalPlayer), session, StringComparison.Ordinal);

    public PlayerAgent? LocalAgent
        => _module == null ? throw Missing()
            : SNet.HasLocalPlayer && SNet.LocalPlayer != null ? SNet.LocalPlayer.PlayerAgent?.TryCast<PlayerAgent>() : null;
}
