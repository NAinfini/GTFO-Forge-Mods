using Agents;
using Enemies;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of `forge.query.entity.dimension`: the dimension the entity a reference names is in.
///
/// <b>What is read.</b> The agent's own `DimensionIndex` — the dimension the game keeps on the agent itself
/// (`Agents.Agent.m_dimensionIndex`, set with its course node), which the interop declares on `Agents.Agent` for
/// players and enemies alike. Nothing is derived from a position and nothing is decoded from an address: a map
/// object's address carries the dimension of the zone it was placed in, and a coordinate carries a point, and
/// neither is the dimension an entity stands in after a warp.
///
/// <b>Why not the course node.</b> The same number is reachable through `agent.CourseNode.m_dimension`, but a
/// client does not maintain other players' course nodes: the node is host-side navigation state, so reading
/// through it answers nothing for every player but the local one and the dimension row refuses a readable
/// dimension. The agent's own field is what the game sets when it places the agent, so it is the one member that
/// answers on both peers.
///
/// <b>Where each kind's reader comes from.</b> A player reference is resolved through Runtime's player
/// directory. An enemy reference carries the native object the
/// runtime's published instance door answered with — the enemy package owns that kind's instances and registered
/// the lookup — so the object is read as the game's own `EnemyAgent` and no table of another package is reached.
/// The door hands over whatever the owner published; an object that is not an `EnemyAgent` answers nothing rather
/// than being read as one.
///
/// <b>What is refused.</b> A reference of another world is refused by the row before the reader is asked; an
/// agent whose node, or whose node's dimension, did not read answers null, which the row refuses rather than
/// reporting the main dimension.
/// </summary>
internal static class DimensionQuery
{
    /// <summary>The one reading this half makes, handed to the row's evaluator as its reader. The instance is the
    /// native object the runtime's instance door resolved for an enemy reference, and null for the player kind
    /// this half reads through the player directory.</summary>
    internal static int? ReadDimension(IPlayerDirectory players, EntityReference reference, object? instance, long worldEpoch)
    {
        if (reference.WorldEpoch != worldEpoch)
            throw new RuntimeContractException(EntityDimensionContract.StaleCode,
                "The entity belongs to another world: " + reference.Id);
        if (instance != null) return instance is EnemyAgent enemy ? NodeDimension(enemy) : null;
        var agent = players.AgentOf(reference);
        return agent == null ? null : NodeDimension(agent);
    }

    /// <summary>The dimension the agent itself is in, or null when the agent cannot be read. The read is one member
    /// family for both kinds, so a player and an enemy in the same place answer the same dimension.</summary>
    private static int? NodeDimension(Agent agent)
    {
        if (agent == null || agent.WasCollected) return null;
        return (int)agent.DimensionIndex;
    }
}
