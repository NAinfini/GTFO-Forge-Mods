using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using Player;
using SNetwork;

namespace ForgePlayer.Native;

/// <summary>One player snapshot from explicit native reads on build 20403457. Every read is paired with a
/// re-read of the instance identity, so a life that changed mid-read is refused instead of reported.</summary>
internal static class PlayerObservation
{
    private const string Kind = EntityKinds.Player;
    private const string Faction = "player";
    private const string LocalPlayerTag = "player.local";
    private const string HostPlayerTag = "player.host";
    private const string BotTag = "player.bot";
    private const string SlotTagPrefix = "player.slot-";

    /// <summary>The one recipient capability this provider serves for a player. It is advertised only where the
    /// reader can also serve it — a verified receiver on a living agent — so a dead player or one without a
    /// readable damage base never claims a heal it could not accept.</summary>
    private static readonly string[] HealReceiver = { "health.heal" };

    private sealed record Health(bool Setup, float Current, float Maximum);

    internal static RuntimeEntitySnapshot? Read(PlayerAgent agent, SNet_Player player, EntityReference reference)
    {
        RuntimeEntityReferences.Validate(reference);
        // The reference is the identity the module already resolved to this exact agent and player; the read
        // below only has to disagree with itself to be refused. A player's slot number is a fact this observer
        // publishes as a tag, not the entity number the module assigned that life.
        var first = Capture(agent, player);
        if (first == null || first != Capture(agent, player)) return null;
        return new RuntimeEntitySnapshot(reference, Kind, Faction, first.LifeState, Tags(player, first.SlotIndex),
            first.Alive && first.Health.Setup ? HealReceiver : Array.Empty<string>(),
            new double[] { first.X, first.Y, first.Z },
            first.Health.Setup ? first.Health.Current : null,
            first.Health.Setup ? first.Health.Maximum : null,
            eyePosition: first.Eye is { } eye ? new double[] { eye.x, eye.y, eye.z } : null);
    }

    private sealed record PlayerSample(IntPtr PlayerPointer, IntPtr AgentPointer, string LifeState, bool Alive, int SlotIndex,
        float X, float Y, float Z, Health Health, UnityEngine.Vector3? Eye);

    private static PlayerSample? Capture(PlayerAgent agent, SNet_Player player)
    {
        if (agent == null || agent.Pointer == IntPtr.Zero || player == null || player.Pointer == IntPtr.Zero
            || player.PlayerAgent == null) return null;
        var playerPointer = player.Pointer;
        var agentPointer = agent.Pointer;
        int slotIndex = player.PlayerSlotIndex(), agentSlot = agent.PlayerSlotIndex;
        bool alive = agent.Alive;
        var position = agent.Position;
        if (slotIndex < 0 || slotIndex != agentSlot
            || !float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        var health = ReadHealth(agent);
        // The eyes vanilla judges fog infection at (PlayerAgent.get_EffectVolumeTargetData); a non-finite reading
        // leaves the eyes unknown rather than refusing the whole snapshot.
        var eye = agent.EyePosition;
        UnityEngine.Vector3? eyes = float.IsFinite(eye.x) && float.IsFinite(eye.y) && float.IsFinite(eye.z) ? eye : null;
        // Native revive accepts a not-Alive player in the rescuable Downed state. Preserve Alive as read,
        // but classify this state before interpreting the same flag as an ended life.
        string lifeState = IsDowned(agent) ? "downed" : !alive ? "dead" : "alive";
        // Native reads can trigger teardown or replacement; a changed instance never yields a snapshot.
        if (agent.Pointer != agentPointer || player.Pointer != playerPointer || player.PlayerAgent == null) return null;
        return new PlayerSample(playerPointer, agentPointer, lifeState, alive, slotIndex, position.x, position.y, position.z, health, eyes);
    }

    private static Health ReadHealth(PlayerAgent agent)
    {
        var damage = agent.Damage;
        if (damage == null || damage.Pointer == IntPtr.Zero) return new Health(false, 0, 0);
        float current = damage.Health, maximum = damage.HealthMax;
        // The receiver's own setup flag is part of the evidence: a receiver that has not been set up, belongs
        // to another agent, or holds an impossible range is never published as readable health.
        bool setup = damage.IsSetup && damage.Owner != null && damage.Owner.Pointer == agent.Pointer
            && float.IsFinite(current) && float.IsFinite(maximum) && maximum > 0 && current >= 0 && current <= maximum;
        return new Health(setup, current, maximum);
    }

    /// <summary>The position and life state <see cref="Read"/> publishes for the same agent, without building the
    /// snapshot: the per-frame spatial readers (zone judgement) ask only these, and a pose allocates no managed array.</summary>
    internal static bool ReadPose(PlayerAgent agent, SNet_Player player, out EntityPose pose)
    {
        pose = default;
        if (agent == null || agent.Pointer == IntPtr.Zero || player == null || player.Pointer == IntPtr.Zero) return false;
        var agentPointer = agent.Pointer;
        int slotIndex = player.PlayerSlotIndex(), agentSlot = agent.PlayerSlotIndex;
        bool alive = agent.Alive;
        var position = agent.Position;
        if (slotIndex < 0 || slotIndex != agentSlot
            || !float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return false;
        string lifeState = IsDowned(agent) ? "downed" : !alive ? "dead" : "alive";
        if (agent.Pointer != agentPointer) return false;
        pose = new EntityPose(position.x, position.y, position.z, lifeState);
        return true;
    }

    /// <summary>Whether locomotion is in the native Downed state (the state <see cref="DownedState"/> returns).</summary>
    private static bool IsDowned(PlayerAgent agent)
    {
        var locomotion = agent.Locomotion;
        return locomotion != null && locomotion.Pointer != IntPtr.Zero
            && locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.Downed;
    }

    /// <summary>The native Downed state, including its revive animation before locomotion leaves it.
    /// Agent.Alive is independent; m_isRevived distinguishes rescuable from already reviving.</summary>
    internal static PLOC_Downed? DownedState(PlayerAgent agent)
    {
        var locomotion = agent.Locomotion;
        return locomotion != null && locomotion.Pointer != IntPtr.Zero
            && locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.Downed
            ? locomotion.CurrentState?.TryCast<PLOC_Downed>() : null;
    }

    /// <summary>Tags carry player identity only. Health is first-class snapshot Data and never encoded into tags.</summary>
    private static IReadOnlyList<string> Tags(SNet_Player player, int slotIndex)
    {
        var tags = new List<string>(4);
        if (player.IsLocal) tags.Add(LocalPlayerTag);
        if (player.IsMaster) tags.Add(HostPlayerTag);
        if (player.IsBot) tags.Add(BotTag);
        tags.Add(SlotTagPrefix + slotIndex.ToString(CultureInfo.InvariantCulture));
        return tags;
    }
}
