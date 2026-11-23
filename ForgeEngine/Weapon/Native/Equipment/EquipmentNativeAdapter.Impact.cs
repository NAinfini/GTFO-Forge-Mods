using System;
using Agents;
using Enemies;
using ForgeEngine.Framework;
using Player;

namespace ForgeWeapon.Native;

internal sealed partial class EquipmentNativeAdapter
{
    /// <summary>The Forge reference of the object a shot hit, chosen by that object's own native type: the damage
    /// limb the bullet was resolved against names its base agent, and the agent's own type decides whether the
    /// enemy domain or the player domain is asked. Each kind is asked with the instance its own domain records —
    /// ForgeEnemy keys its registry by the agent, ForgePlayer keys its player registry by the SNet_Player the agent
    /// belongs to, and ForgeMap's map objects are asked with the hit object itself, because a door or a terminal
    /// is reached through the component the bullet was resolved against and only that domain can climb from one
    /// to the other — so no reference is ever derived from the shooter or from allegiance. A hit on world
    /// geometry, a deployable or anything else this build cannot name answers null, which is what leaves the port
    /// out of the fact instead of publishing a null reference.</summary>
    internal EntityReference? HitTarget(Weapon.WeaponHitData? data)
    {
        var collider = data == null ? null : data.rayHit.collider;
        if (collider == null) return null;
        var climb = Climb(collider);
        if (climb.Enemy is { } enemy) return _kernel.ResolveEntityInstance(EntityKinds.Enemy, enemy);
        if (climb.Player is { } player) return OwnerOf(player);
        return _kernel.ResolveEntityInstance(EntityKinds.MapObject, collider);
    }

    /// <summary>Whether the ray's collider belongs to an enemy or player damage limb, the bodies the game's bullet
    /// entry resolves a hit against. A door, terminal or prop has a map-object reference but no limb, so the game's
    /// `false` there means nothing damageable was reached, not that a damage submission is unknown.</summary>
    internal static bool ReachedDamageLimb(Weapon.WeaponHitData data)
    {
        var collider = data.rayHit.collider;
        return Climb(data.rayHit.collider).Kind != LimbKind.None;
    }

    internal enum LimbKind : byte { None, Enemy, Player }

    /// <summary>The damage limb one hit object sits under and the agent it belongs to: an enemy limb names its enemy and
    /// its own `m_limbID`, a player limb its player and `-1` (a player limb declares no index in this build). An enemy
    /// limb is looked for first; a limb whose agent is not set yet answers its kind with no agent.</summary>
    internal readonly struct LimbClimb
    {
        internal static readonly LimbClimb None = new(LimbKind.None, null, null, -1);
        internal LimbClimb(LimbKind kind, EnemyAgent? enemy, PlayerAgent? player, int part)
        {
            Kind = kind; Enemy = enemy; Player = player; Part = part;
        }
        internal LimbKind Kind { get; }
        internal EnemyAgent? Enemy { get; }
        internal PlayerAgent? Player { get; }
        internal int Part { get; }
        internal Agent? Agent => Enemy is { } enemy ? enemy : Player;
    }

    private const int LimbCacheLimit = 512;
    /// <summary>Every climb of this package, kept by the hit object's instance id: a collider never changes the agent it
    /// belongs to, a destroyed object is never hit again, and instance ids are not reused in a session. Only a settled
    /// answer is kept (an unset limb agent is climbed again).</summary>
    private static readonly System.Collections.Generic.Dictionary<int, LimbClimb> _climbs = new();

    /// <summary>The one collider-to-agent climb every hit read of this package goes through (ray hits, melee hits, the
    /// projectile sweep, the shot rules and the client report).</summary>
    internal static LimbClimb Climb(UnityEngine.Component? hit)
    {
        if (hit == null) return LimbClimb.None;
        var id = hit.GetInstanceID();
        return _climbs.TryGetValue(id, out var known) ? known : Remember(id, hit.GetComponentInParent<Dam_EnemyDamageLimb>(), hit, null);
    }

    /// <summary>The same climb from a hit game object (a melee hit carries one rather than a collider).</summary>
    internal static LimbClimb Climb(UnityEngine.GameObject? hit)
    {
        if (hit == null) return LimbClimb.None;
        var id = hit.GetInstanceID();
        return _climbs.TryGetValue(id, out var known) ? known : Remember(id, hit.GetComponentInParent<Dam_EnemyDamageLimb>(), null, hit);
    }

    private static LimbClimb Remember(int id, Dam_EnemyDamageLimb? part, UnityEngine.Component? component, UnityEngine.GameObject? gameObject)
    {
        LimbClimb climb;
        if (part != null)
        {
            var enemy = part.GetBaseAgent()?.TryCast<EnemyAgent>();
            climb = new LimbClimb(LimbKind.Enemy, enemy, null, part.m_limbID);
            if (enemy == null) return climb;
        }
        else if ((component != null ? component.GetComponentInParent<Dam_PlayerDamageLimb>() : gameObject!.GetComponentInParent<Dam_PlayerDamageLimb>()) is { } playerPart)
        {
            var player = playerPart.GetBaseAgent()?.TryCast<PlayerAgent>();
            climb = new LimbClimb(LimbKind.Player, null, player, -1);
            if (player == null) return climb;
        }
        else climb = LimbClimb.None;
        if (_climbs.Count >= LimbCacheLimit) _climbs.Clear();
        _climbs[id] = climb;
        return climb;
    }
    // NOTE: `Impact` below is this same climb plus the body's geometry; the projectile flight keeps this lighter
    // read because it never records a hit context.

    /// <summary>The Forge reference of the thing a melee body was handed, together with the part index it landed
    /// on. It is the same climb <see cref="HitTarget"/> makes, entered from the game object a melee hit carries
    /// instead of from a ray's collider, and it answers no map object: a melee body is handed a damageable, not
    /// the collider a bullet was resolved against, so a door or a terminal reached this way has no instance its
    /// own domain would recognise. `-1` is the part index the game does not apply (a player limb declares none in
    /// this build), and a game object this machine cannot name answers null, which is what keeps a fact from
    /// carrying an invented target. Both melee rows read through this one member, so a swing and a push can never
    /// name the same hit differently.</summary>
    internal EntityReference? DamageTarget(UnityEngine.GameObject? damageGO, out int limb)
    {
        var climb = Climb(damageGO);
        limb = climb.Part;
        if (climb.Enemy is { } enemy) return _kernel.ResolveEntityInstance(EntityKinds.Enemy, enemy);
        return climb.Player is { } player ? OwnerOf(player) : null;
    }

    /// <summary>What one ray impact says about the thing it reached, besides the reference: the part index the ray
    /// hit, the world direction the body that part belongs to faces, and the world position that body stood at when
    /// the impact happened. All three are read from the same collider chain <see cref="HitTarget"/> climbs, because
    /// that chain is the only object that knows them together — the bullet resolved against a damage limb, and the
    /// limb's own body owns the facing and the position a from-behind relation and an anchored hit point are
    /// measured against. `-1` is the part index the game does not apply to what was hit (a player limb carries no
    /// index, and world geometry carries no part at all); an empty facing is a body this machine cannot orient, and
    /// an empty position one it cannot place.</summary>
    // WHY: P5 — a value, not a record with two arrays: the shot observer reads one per hit, subscribed or not.
    internal readonly struct RayImpact
    {
        internal RayImpact(EntityReference? target, IntPtr body, long limb)
        {
            Target = target; Body = body; Limb = limb;
        }

        /// <summary>The Forge reference of what the ray reached, exactly as <see cref="HitTarget"/> answers it.</summary>
        internal EntityReference? Target { get; }
        /// <summary>The native identity of the agent body the part belongs to; zero for anything that is no agent.</summary>
        internal IntPtr Body { get; }
        internal long Limb { get; }
        internal bool HasFacing { get; init; }
        internal double FacingX { get; init; }
        internal double FacingY { get; init; }
        internal double FacingZ { get; init; }
        internal bool HasPosition { get; init; }
        internal double X { get; init; }
        internal double Y { get; init; }
        internal double Z { get; init; }
    }

    /// <summary>One impact's reference, part index, target facing and target position, read in one climb of the
    /// collider chain. The reference is the one <see cref="HitTarget"/> names; `m_limbID` is the enemy limb's own
    /// public field, which is the same value the enemy domain's limb rows name; a player limb declares no index in
    /// this build, so its part is the game's own "no part" answer rather than a guess. Anything the chain carries no
    /// damage limb for has no body, no facing and no position, so the caller records no relation for it.</summary>
    internal RayImpact Impact(Weapon.WeaponHitData? data)
    {
        var collider = data == null ? null : data.rayHit.collider;
        if (collider == null) return new RayImpact(null, IntPtr.Zero, -1);
        var climb = Climb(collider);
        if (climb.Enemy is { } enemy) return Body(_kernel.ResolveEntityInstance(EntityKinds.Enemy, enemy), enemy, climb.Part);
        if (climb.Player is { } player) return Body(OwnerOf(player), player, -1);
        return new RayImpact(_kernel.ResolveEntityInstance(EntityKinds.MapObject, collider), IntPtr.Zero, -1);
    }

    /// <summary>The body a hit object belongs to and the limb it landed on, in the terms another machine can name: the same climb as
    /// <see cref="Impact"/>, with no reference, because a client holds no enemy identity. Null for anything that is no agent body.</summary>
    internal static Agent? BodyOf(UnityEngine.GameObject? hit, out int limb)
    {
        var climb = Climb(hit);
        limb = climb.Part;
        return climb.Agent;
    }

    /// <summary>An agent's replicated key, which names the same agent on every machine; zero for an agent with none.</summary>
    internal static ushort KeyOf(Agent agent)
    {
        var handle = new pAgent();
        handle.Set(agent);
        return handle.pRep.keyPlusOne;
    }

    internal static Agent? AgentBy(ushort key)
    {
        var handle = new pAgent();
        handle.pRep.keyPlusOne = key;
        return handle.TryGet(out var agent) ? agent : null;
    }

    /// <summary>The impact on an agent a client named by key, as <see cref="Impact"/> reads it on the shooter's machine.</summary>
    internal RayImpact ImpactOn(Agent agent, long limb)
    {
        if (agent.TryCast<EnemyAgent>() is { } enemy) return Body(_kernel.ResolveEntityInstance(EntityKinds.Enemy, enemy), enemy, limb);
        return agent.TryCast<PlayerAgent>() is { } player ? Body(OwnerOf(player), player, -1) : default;
    }

    /// <summary>One agent body's reading: the world direction it faces, as a unit vector, and the world position it
    /// occupies, each absent for a destroyed object or a non-finite value. The facing is what a from-behind relation
    /// is measured against and the position is what the hit point's offset is taken from, so both are read from the
    /// transform the game itself moves at the moment of impact rather than derived later.</summary>
    private static RayImpact Body(EntityReference? target, Agent agent, long limb)
    {
        var transform = agent == null ? null : agent.transform;
        if (transform == null) return new RayImpact(target, agent?.Pointer ?? IntPtr.Zero, limb);
        var impact = new RayImpact(target, agent!.Pointer, limb);
        var forward = transform.forward;
        if (float.IsFinite(forward.x) && float.IsFinite(forward.y) && float.IsFinite(forward.z))
        {
            var length = Math.Sqrt(forward.x * forward.x + forward.y * forward.y + forward.z * forward.z);
            if (length > double.Epsilon)
                impact = impact with
                {
                    HasFacing = true,
                    FacingX = forward.x / length,
                    FacingY = forward.y / length,
                    FacingZ = forward.z / length
                };
        }
        var position = transform.position;
        if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z))
            impact = impact with { HasPosition = true, X = position.x, Y = position.y, Z = position.z };
        return impact;
    }

    /// <summary>
    /// A destroyed Unity object reads as null through its own overloaded equality, and its transform then throws;
    // a non-finite coordinate is refused rather than published as a position. The one reader answers both the
    // observer snapshots and the deployment fact, so a position can never have a second spelling in this package.
    internal static double[]? Position(Item? instance)
    {
        var transform = instance == null ? null : instance.transform;
        if (transform == null) return null;
        var position = transform.position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

}
