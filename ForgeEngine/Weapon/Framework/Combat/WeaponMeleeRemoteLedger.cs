using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The rule that keeps `forge.trigger.combat.melee_hit` published once per landed hit even though the row
/// has two native sources. Both halves are game-independent on purpose: which source may publish, and which hit
/// has already been published, are decisions this package can be held to without a game in the process.
///
/// <b>Which source publishes.</b> The row is written by the machine that performs the swing
/// (`MeleeWeaponFirstPerson.DoAttackDamage` for its own player, `MeleeWeaponThirdPerson.DoAttackDamage` for a bot
/// it replays) and, on the host, by the replicated damage a remote client's swing becomes
/// (`Dam_EnemyDamageBase.ReceiveMeleeDamage`). Those three overlap on exactly one machine — the host runs both its
/// own hit entry and the receive body for its own player's swing, because the game sends the damage to itself —
/// so the receive half publishes only for an attacker that is neither this machine's own player nor a bot. The
/// other two are already published by their own hooks, and a hit whose attacker is one of them is not this half's
/// to publish.
///
/// <b>Which hit has already been published.</b> The ledger is the second half of the same rule: a hit is claimed by
/// the source that publishes it, and a claim is live for a short window, so a packet the network delivers twice —
/// or a receive body reached twice for one swing — publishes once. A claim is keyed by the attacker, the target
/// and the limb, because that triple is what one landed hit is; a second swing at the same limb a moment later is
/// a different hit and is claimed again once the window has passed. The world epoch is part of the ledger's own
/// state, so a claim can never outlive the world it was made in.</summary>
internal sealed class MeleeHitLedger
{
    /// <summary>How long one claim stays live, in ticks. The window only has to cover the gap between the two
    /// observations of one hit (the swing's own body and the receive body it sends to), which is the same tick on
    /// the machine that runs both and one network hop on the host; eight ticks is a bound this row is willing to
    /// call "the same hit" and small enough that two real swings are never merged.</summary>
    internal const int WindowTicks = 8;

    /// <summary>The most claims one world may hold. A world that has produced more distinct hits than this inside
    /// one window is already past the point where an old claim could matter, so the least recently claimed are
    /// dropped first.</summary>
    internal const int Capacity = 256;

    private readonly record struct ClaimKey(string SourceId, string TargetId, int Limb);
    private readonly record struct Claim(ClaimKey Key, long Tick);
    // One node per key, ordered by its latest claim: a repeated claim moves its own node to the end instead of
    // queueing the key again, so the order never outgrows the table and eviction never drops a claim just made.
    private readonly Dictionary<ClaimKey, LinkedListNode<Claim>> _claims = new();
    private readonly LinkedList<Claim> _order = new();
    private long _world = long.MinValue;

    /// <summary>Whether the replicated-damage half may publish a hit whose attacker is described by these two
    /// flags. A bot's swing and this machine's own player's swing are published by the hit entries themselves.</summary>
    internal static bool PublishesRemote(bool attackerIsLocal, bool attackerIsBot)
        => !attackerIsLocal && !attackerIsBot;

    /// <summary>Claims one hit for the source that is about to publish it. False means another source already
    /// claimed the same attacker, target and limb inside the window, so this observation is the same hit and must
    /// not be published a second time.</summary>
    internal bool TryClaim(EntityReference? source, EntityReference? target, int limb, long tick, long world)
    {
        if (source?.Id == null || target?.Id == null) return false;
        if (world != _world) Reset(world);
        var key = new ClaimKey(source.Id, target.Id, limb);
        if (_claims.TryGetValue(key, out var node))
        {
            var claimed = node.Value.Tick;
            if (tick - claimed < WindowTicks && tick >= claimed) return false;
            node.Value = new Claim(key, tick);
            _order.Remove(node);
            _order.AddLast(node);
            return true;
        }
        if (_claims.Count >= Capacity)
        {
            var oldest = _order.First!;
            _order.RemoveFirst();
            _claims.Remove(oldest.Value.Key);
        }
        _claims[key] = _order.AddLast(new Claim(key, tick));
        return true;
    }

    /// <summary>How many entries the claim order holds; it never exceeds <see cref="Count"/>.</summary>
    internal int OrderCount => _order.Count;

    /// <summary>Forgets every claim. Called when the world changes, so a claim is never carried into a world whose
    /// ticks mean something else.</summary>
    internal void Reset(long world)
    {
        _claims.Clear();
        _order.Clear();
        _world = world;
    }

    /// <summary>How many claims are live right now, for this slice's own tests and for a diagnostic.</summary>
    internal int Count => _claims.Count;
}
