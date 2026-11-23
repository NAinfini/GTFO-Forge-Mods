using System;
using Enemies;
using ForgeEngine.Framework;
using UnityEngine.AI;

namespace ForgeEnemy.Native.Observation;

/// <summary>The position and life state of one enemy instance, written into <paramref name="pose"/> without allocating.</summary>
internal delegate bool EnemyPoseReader(EnemyAgent enemy, EntityReference reference, out EntityPose pose);

/// <summary>Read-only, explicit-instance native data. No world query or allegiance inference.
///
/// Every field of the frozen snapshot is read here, for the reason the frame was frozen with them: an author who
/// asks "how hurt is this enemy", "is it still asleep", "where is it standing" or "which type is it" is asking
/// about the one enemy a reference already names, and the answer has to come from that instance rather than from a
/// ledger. Each reading is optional on its own terms — a receiver that does not read, a navigation component that
/// is not there, a behaviour state outside the game's own enumeration — and an unreadable reading is published as
/// null so a consumer refuses instead of computing with a substituted zero.
///
/// The behaviour state is the one reading that is not a raw native number: the frame's `aiState` is a member name
/// of the shared `ai_state` set, so the native `EB_States` value is mapped here, in one table, and the mapping is
/// the same one `EnemyModule`'s behaviour facts publish as an index. A native member with no counterpart in the
/// shared set is `disabled`, which is what the set's own last member means for a state no author can act on.</summary>
internal static class EnemyEntityObserver
{
    private static readonly string[] HealReceiver = { "health.heal" };
    private readonly record struct Sample(IntPtr Pointer, ushort Id, bool Alive,
        float X, float Y, float Z, Receiver Health, Vector? Facing, int Behaviour, double? Speed, Vector? Eye);
    private readonly record struct Receiver(IntPtr Pointer, IntPtr Owner, bool Setup,
        float Health, float Maximum);
    private readonly record struct Vector(float X, float Y, float Z);

    internal static RuntimeEntitySnapshot? Read(EnemyAgent enemy, EntityReference reference)
    {
        RuntimeEntityReferences.Validate(reference);
        // Both native captures still run in this call. Only their temporary storage is a value;
        // nothing is retained across a native write or shared with a previously frozen snapshot.
        if (Capture(enemy) is not { } first || first != Capture(enemy)) return null;
        const string prefix = EntityKinds.Enemy + ":";
        Span<char> digits = stackalloc char[5]; // ushort.MaxValue has five decimal digits.
        first.Id.TryFormat(digits, out int length, provider: System.Globalization.CultureInfo.InvariantCulture);
        if (!reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !reference.Id.AsSpan(prefix.Length).SequenceEqual(digits[..length])) return null;
        var health = first.Health;
        bool canHeal = first.Alive && health.Pointer != IntPtr.Zero && health.Owner == first.Pointer
            && health.Setup && float.IsFinite(health.Health) && float.IsFinite(health.Maximum)
            && health.Health > 0 && health.Maximum > 0 && health.Health <= health.Maximum;
        double? healthReading = canHeal ? health.Health : null;
        double? maximumReading = canHeal ? health.Maximum : null;
        return new RuntimeEntitySnapshot(reference, EntityKinds.Enemy, "enemy", first.Alive ? "alive" : "dead",
            Array.Empty<string>(), canHeal ? HealReceiver : Array.Empty<string>(),
            new double[] { first.X, first.Y, first.Z },
            health: healthReading, healthMaximum: maximumReading,
            // A facing that does not read is an absent port, never the zero vector: a consumer that compared
            // `rotation` with (0,0,0) could not tell "facing +Z-unreadable" from "facing nowhere", which is the
            // substituted zero this file's contract refuses everywhere else.
            rotation: first.Facing is { } facing ? new double[] { facing.X, facing.Y, facing.Z } : null,
            aiState: AiState(first.Behaviour), speed: first.Speed,
            eyePosition: first.Eye is { } eye ? new double[] { eye.X, eye.Y, eye.Z } : null);
    }

    /// <summary>The position and life state <see cref="Read"/> would publish for the same instance, without building
    /// the snapshot: the per-frame spatial readers (zone judgement) ask only these, and a pose allocates nothing.</summary>
    internal static bool ReadPose(EnemyAgent enemy, EntityReference reference, out EntityPose pose)
    {
        pose = default;
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return false;
        var pointer = enemy.Pointer;
        ushort id = enemy.GlobalID;
        const string prefix = EntityKinds.Enemy + ":";
        Span<char> digits = stackalloc char[5]; // ushort.MaxValue has five decimal digits.
        id.TryFormat(digits, out int length, provider: System.Globalization.CultureInfo.InvariantCulture);
        if (!reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !reference.Id.AsSpan(prefix.Length).SequenceEqual(digits[..length])) return false;
        bool alive = enemy.Alive;
        var position = enemy.Position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return false;
        if (enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup) return false;
        pose = new EntityPose(position.x, position.y, position.z, alive ? "alive" : "dead");
        return true;
    }

    /// <summary>The tag readings the `v-e-tagged` row answers: the enemy's own tag flag and the seconds of tag
    /// time it has left. Read through the same explicit-instance path the snapshot uses, and answered as an
    /// absence when either getter cannot be read.</summary>
    internal static TagReading? ReadTag(EnemyAgent enemy)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
        var pointer = enemy.Pointer;
        ushort id = enemy.GlobalID;
        bool tagged;
        float timer;
        try
        {
            tagged = enemy.IsTagged;
            timer = enemy.EnemyTaggedTimer;
        }
        catch (Exception error) { NativeFault.Note("enemy.observer.tag", error); return null; }
        // An untagged enemy has no tag time left by definition, so a timer field that holds no number is not a
        // reading that can be made for one that does: a tagged enemy whose timer does not read has no answer, and
        // the row refuses rather than claiming the tag has expired.
        if (!float.IsFinite(timer))
        {
            if (tagged) return null;
            timer = 0f;
        }
        if (timer < 0f) timer = 0f;
        if (enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup) return null;
        return new TagReading(tagged, timer);
    }

    internal readonly record struct TagReading(bool Tagged, float RemainingSeconds);

    private static Sample? Capture(EnemyAgent enemy)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
        var pointer = enemy.Pointer;
        ushort id = enemy.GlobalID;
        bool alive = enemy.Alive;
        var position = enemy.Position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
            return null;
        var damage = enemy.Damage;
        var health = default(Receiver);
        if (damage != null && damage.Pointer != IntPtr.Zero)
        {
            var owner = damage.Owner;
            health = new Receiver(damage.Pointer, owner == null ? IntPtr.Zero : owner.Pointer,
                damage.IsSetup, damage.Health, damage.HealthMax);
        }
        var facing = ReadFacing(enemy);
        int behaviour = ReadBehaviourState(enemy);
        double? speed = ReadSpeed(enemy);
        var eye = ReadEye(enemy);
        if (enemy == null || enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup)
            return null;
        return new Sample(pointer, id, alive, position.x, position.y, position.z, health, facing, behaviour, speed, eye);
    }

    /// <summary>Where the enemy's eyes are, from the agent's own <c>EyePosition</c>; absent when it does not read.</summary>
    private static Vector? ReadEye(EnemyAgent enemy)
    {
        try
        {
            var eye = enemy.EyePosition;
            if (float.IsFinite(eye.x) && float.IsFinite(eye.y) && float.IsFinite(eye.z))
                return new Vector(eye.x, eye.y, eye.z);
        }
        catch (Exception error) { NativeFault.Note("enemy.observer.eye", error); }
        return null;
    }

    /// <summary>The direction the enemy faces, read from the agent's own forward vector. A component that is not
    /// finite — including a getter that throws — makes the whole reading absent rather than a direction nobody can
    /// normalize, and the snapshot then carries no rotation instead of the zero vector.</summary>
    private static Vector? ReadFacing(EnemyAgent enemy)
    {
        try
        {
            var forward = enemy.Forward;
            if (float.IsFinite(forward.x) && float.IsFinite(forward.y) && float.IsFinite(forward.z))
                return new Vector(forward.x, forward.y, forward.z);
        }
        catch (Exception error) { NativeFault.Note("enemy.observer.direction", error); }
        return null;
    }

    /// <summary>The enemy's behaviour state, or -1 when no behaviour machine can be read: the frame then answers
    /// `disabled`, the one member of the shared set that means "no state an author can act on".</summary>
    private static int ReadBehaviourState(EnemyAgent enemy)
    {
        try
        {
            var behaviour = enemy.AI?.m_behaviour;
            return behaviour == null ? -1 : (int)behaviour.m_currentStateName;
        }
        catch (Exception error) { NativeFault.Note("enemy.observer.behaviour", error); return -1; }
    }

    /// <summary>The speed the enemy's own navigation component is set to. A component that is not there, or a
    /// value that is not a finite non-negative quantity, leaves the reading absent.</summary>
    private static double? ReadSpeed(EnemyAgent enemy)
    {
        try
        {
            INavigation? navigation = enemy.AI?.m_navMeshAgent;
            if (navigation == null) return null;
            double speed = navigation.speed;
            return double.IsFinite(speed) && speed >= 0 ? speed : null;
        }
        catch (Exception error) { NativeFault.Note("enemy.observer.speed", error); return null; }
    }

    /// <summary>Native `EB_States` to the shared `ai_state` set, one entry per declared native member. The member
    /// names are the set's own spelling, so a consumer compares with the vocabulary it already has rather than
    /// with an index into a table only this file knows.</summary>
    internal static string AiState(int nativeState) => nativeState switch
    {
        0 => "hibernating",       // Hibernating
        1 => "patrolling",        // Patrolling
        2 => "investigating",     // Patrolling_Investigate
        3 => "idle",              // FollowingGroup
        4 => "idle",              // FollowingGroup_MoveToNode
        5 => "pursuing",          // InCombat
        6 => "pursuing",          // InCombat_MoveToPoint
        7 => "pursuing",          // InCombat_MoveToTarget
        8 => "pursuing",          // InCombat_MoveToNextNode
        9 => "pursuing",          // InCombat_MoveToNextNode_PathBlocked
        10 => "pursuing",         // InCombat_MoveToNextNode_PathOpen
        11 => "pursuing",         // InCombat_MoveToNextNode_DestroyDoor
        12 => "hibernating",      // SquidBoss_Hibernating
        13 => "waking",           // SquidBoss_Intro: the boss leaving its hibernation
        14 => "attacking",        // SquidBoss_Combat
        15 => "attacking",        // SquidBoss_Raging: a combat phase
        16 => "attacking",        // SquidBoss_Spawning: spawning adds is an attack
        17 => "recovering",       // SquidBoss_Cooldown
        18 => "recovering",       // SquidBoss_RageTransition
        19 => "dead",             // Dead
        20 => "attacking",        // InCombat_ChargedAttack
        21 => "pursuing",         // Incombat_GraphTraversal_Flyer
        22 => "pursuing",         // Incombat_FlyOutOfBoss_Flyer
        23 => "pursuing",         // InCombat_Dash
        24 => "attacking",        // InCombat_HeldPlayer
        25 => "attacking",        // InCombat_AfterHeldPlayer
        26 => "attacking",        // InCombat_Consume
        27 => "attacking",        // InCombat_SpitOut
        28 => "recovering",       // InCombat_Stagger
        _ => "disabled"           // no mapped native member is a state an author can act on
    };

    /// <summary>The two native members the `v-e-sleeping` row calls hibernating: `EB_States.Hibernating` and the
    /// squid boss's own hibernation. The same pair `forge.trigger.enemy.awakened` treats as the sleeping
    /// baseline, so the event and the value cannot disagree about what sleep is.</summary>
    internal static bool Hibernating(int nativeState)
        => nativeState is (int)EB_States.Hibernating or (int)EB_States.SquidBoss_Hibernating;
}
