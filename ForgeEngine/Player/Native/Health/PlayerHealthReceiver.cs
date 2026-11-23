using System;
using ForgeEngine.Framework;
using Player;

namespace ForgePlayer.Native;

/// <summary>One current player life's health receiver, read from the game's own damage base: the life the
/// reference names, the two instances the write goes through, and the health the receiver holds.</summary>
internal readonly record struct PlayerHealthSnapshot(EntityReference Target, IntPtr AgentPointer,
    IntPtr ReceiverPointer, bool Alive, float Health, float Maximum);

/// <summary>The current player's receiver. Healing enters AddHealth with a delta and the actual source Agent;
/// its host-side ReceiveAddHealth owns clamping, replication and the eventual health write. Forge only reads
/// that result, and never retries through SendSetHealth when the native request is refused.</summary>
internal static class PlayerHealthReceiver
{
    /// <summary>Reads one current life's health receiver. A false answer carries the catalog code that names
    /// why the life cannot be healed right now; the caller writes exactly one row for it. The caller has already
    /// refused a reference of another kind, so a life with no current agent is a stale one.</summary>
    internal static bool TryRead(PlayerIdentityModule players, EntityReference reference, out PlayerHealthSnapshot snapshot, out string code)
    {
        snapshot = default;
        var agent = players.CurrentAgent(reference);
        if (agent == null)
        {
            PlayerCaughtFailures.Note("health-receiver", "target=" + reference.Id + " the identity module has no current agent for this life");
            code = ResultCode.StaleEntity; return false;
        }
        var first = Capture(agent);
        if (first == null || first != Capture(agent))
        {
            PlayerCaughtFailures.Note("health-receiver", "target=" + reference.Id + (first == null ? " the first read destroyed or replaced the agent" : " two reads in one call disagree"));
            code = ResultCode.StaleEntity; return false;
        }
        // An absent receiver and one the game has not set up are the same answer: there is no health state to
        // read or to write. A receiver that belongs to another agent is its own refusal, as it is for enemies.
        if (!first.Value.ReceiverReadable || !first.Value.Setup) { code = ResultCode.MissingHealthReceiver; return false; }
        if (!first.Value.OwnerMatches) { code = ResultCode.HealthReceiverOwnerMismatch; return false; }
        // The player's receive path stores health only while its owner is alive, so this refusal is the same
        // condition the native write would refuse on, checked before anything is submitted.
        if (!first.Value.Alive) { code = ResultCode.NotAlive; return false; }
        if (!float.IsFinite(first.Value.Health) || !float.IsFinite(first.Value.Maximum)
            || first.Value.Maximum <= 0 || first.Value.Health < 0 || first.Value.Health > first.Value.Maximum)
        { code = ResultCode.InvalidHealthState; return false; }
        snapshot = new PlayerHealthSnapshot(reference, first.Value.AgentPointer, first.Value.ReceiverPointer,
            first.Value.Alive, first.Value.Health, first.Value.Maximum);
        code = "";
        return true;
    }

    /// <summary>Submit a delta to the exact life and receiver observed during preflight.</summary>
    internal static bool TrySubmit(PlayerIdentityModule players, PlayerHealthSnapshot expected, float amount, Agents.Agent source)
    {
        if (!players.CanCommit || source == null || source.Pointer == IntPtr.Zero
            || !TryRead(players, expected.Target, out var current, out _) || current != expected) return false;
        var damage = players.CurrentAgent(expected.Target)?.Damage;
        if (damage == null || damage.Pointer != expected.ReceiverPointer) return false;
        damage.AddHealth(amount, source);
        return true;
    }
    /// <summary>The damage base's readable state, and the identity of the two instances it belongs to. A null
    /// answer is a receiver that cannot be read at all; `ReceiverReadable` says a damage base exists and is set
    /// up, `OwnerMatches` whether that damage base belongs to this agent.</summary>
    private readonly record struct Sample(IntPtr AgentPointer, IntPtr ReceiverPointer, bool Alive,
        bool ReceiverReadable, bool OwnerMatches, bool Setup, float Health, float Maximum);

    private static Sample? Capture(PlayerAgent agent)
    {
        if (agent == null || agent.Pointer == IntPtr.Zero) return null;
        var pointer = agent.Pointer;
        var damage = agent.Damage;
        var sample = new Sample(pointer, damage == null ? IntPtr.Zero : damage.Pointer, agent.Alive,
            damage != null && damage.Pointer != IntPtr.Zero, damage != null && damage.Owner != null && damage.Owner.Pointer == pointer,
            damage != null && damage.IsSetup, damage == null ? 0 : damage.Health, damage == null ? 0 : damage.HealthMax);
        // A read can destroy or replace the instances it reads; a changed identity is not a sample.
        return agent.Pointer == pointer ? sample : null;
    }
}
