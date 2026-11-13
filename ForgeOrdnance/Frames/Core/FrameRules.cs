using System;

namespace ForgeOrdnance.Frames;

/// <summary>Life of one suit. The host owns the record; every other peer mirrors it from <see cref="FrameStateMessage"/>.</summary>
internal enum FrameState : byte
{
    /// <summary>Falling from above the ceiling (no collision); nobody can board yet.</summary>
    Arriving = 0,
    /// <summary>Standing empty; any player may board.</summary>
    Idle = 1,
    /// <summary>A pilot is climbing in: input locked, boarding sequence playing.</summary>
    Entering = 2,
    /// <summary>Worn by its pilot.</summary>
    Piloted = 3,
    /// <summary>The pilot is climbing out; the suit stays where it is.</summary>
    Exiting = 4,
    /// <summary>Wrecked; the pilot was thrown out. The record is dropped soon after.</summary>
    Destroyed = 5,
}

internal enum FrameRequestKind : byte { Enter = 1, Exit = 2, Ammo = 3, Boost = 4, Siege = 5, Rockets = 6 }

internal enum FrameRejection : byte
{
    None = 0,
    NotIdle = 1,
    TooFar = 2,
    AlreadyPiloting = 3,
    PilotDown = 4,
    NotPilot = 5,
    NoSpecial = 6,
    Cooling = 7,
    /// <summary>The suit is planted (or planting); it cannot charge.</summary>
    Planted = 8,
    /// <summary>The charge is still running.</summary>
    Charging = 9,
    /// <summary>The game holds the player (grabbed, stunned, controls taken): the suit's own actions wait, as the game's own do.</summary>
    Seized = 10,
    /// <summary>The host gave no answer in time, or left before it answered.</summary>
    NoAnswer = 11,
}

/// <summary>Why the host ended a piloted state without a request. <see cref="PilotTaken"/>: the game seized the pilot of a suit being
/// boarded or left, or put the pilot away from it (a warp, a dimension change); the suit stays empty where it stands.</summary>
internal enum FrameEjection : byte { None = 0, Destroyed = 1, PilotDown = 2, PilotLeft = 3, PilotTaken = 4 }

/// <summary>The host's truth about one suit; also the mirror every client keeps.</summary>
internal sealed class FrameRecord
{
    public FrameRecord(ulong thrower, uint throwId, FrameSpec spec)
    {
        Thrower = thrower; ThrowId = throwId; Spec = spec; SpecKey = FrameSpecKey.Of(spec);
        Armor = spec.Armor; Shield = spec.Shield?.Capacity ?? 0f; ShieldUp = spec.Shield != null; Magazine = spec.Weapon.Magazine; Reserve = spec.Weapon.Reserve;
    }

    public ulong Thrower { get; }
    /// <summary><see cref="FrameSpecKey.Of"/> of <see cref="Spec"/>, sent with every state.</summary>
    public ulong SpecKey { get; }
    public uint ThrowId { get; }
    public FrameSpec Spec { get; }
    /// <summary><c>OrdnanceDefinition.Key</c> of the definition this suit was called from: its display name comes from there, so the
    /// same model under two definitions keeps two names.</summary>
    public uint DefinitionKey;
    public FrameState State;
    /// <summary>Player lookup of the pilot, 0 when empty.</summary>
    public ulong Pilot;
    /// <summary>The suit's only health. Never regenerates, never healed.</summary>
    public float Armor;
    /// <summary>Tank barrier durability, and whether the barrier stands.</summary>
    public float Shield;
    public bool ShieldUp;
    public float ShieldLastHit = float.NegativeInfinity;
    public int Magazine, Reserve;
    public float X, Y, Z, Yaw;
    /// <summary>Game time the current timed state (arriving, entering, exiting, destroyed) ends.</summary>
    public float StateEndsAt;
    public float BoostEndsAt, BoostReadyAt;
    /// <summary>Host: game time the next rocket salvo may launch.</summary>
    public float RocketsReadyAt;
    public bool Sieged;
    public float SiegeDeployEndsAt;
    /// <summary>Host identity that issued Sequence; sequences are ordered only within one authority.</summary>
    public ulong Authority;
    public uint Sequence;
    /// <summary>Counts the times a pilot boarded: a request made in an earlier boarding is stale.</summary>
    public uint Drive;

    public bool Boosting(float now) => now < BoostEndsAt;
    public bool HasPilot => State is FrameState.Entering or FrameState.Piloted or FrameState.Exiting;
}

/// <summary>The host's decisions, free of the game so they are tested offline.</summary>
internal static class FrameRules
{
    /// <summary>A suit is boarded from close behind it: within <see cref="BoardDistance"/> of its feet; the walk-round arrow points at
    /// <see cref="BoardSpot"/> straight behind it. The host allows <see cref="BoardLag"/> more: the pilot's place it reads is a moment old.</summary>
    public const float BoardDistance = 1.2f, BoardSpot = 0.85f, BoardLag = 0.6f;
    /// <summary>How long a wreck stays (burning, then sinking) before it is removed.</summary>
    public const float WreckSeconds = 12f;

    public static FrameRejection CanEnter(FrameRecord frame, float distance, bool alreadyPiloting, bool pilotDown)
    {
        if (frame.State != FrameState.Idle) return FrameRejection.NotIdle;
        if (pilotDown) return FrameRejection.PilotDown;
        if (alreadyPiloting) return FrameRejection.AlreadyPiloting;
        if (!(distance <= BoardDistance)) return FrameRejection.TooFar;
        return FrameRejection.None;
    }

    public static void Enter(FrameRecord frame, ulong pilot, float now)
    {
        frame.State = FrameState.Entering;
        frame.Pilot = pilot;
        frame.StateEndsAt = now + FrameSequence.EnterSeconds;
        frame.Sieged = false;
        frame.SiegeDeployEndsAt = 0f;
        frame.Drive++;
        frame.Sequence++;
    }

    public static FrameRejection CanExit(FrameRecord frame, ulong sender)
        => frame.State != FrameState.Piloted ? FrameRejection.NotIdle : frame.Pilot != sender ? FrameRejection.NotPilot : FrameRejection.None;

    /// <summary>The pilot climbs out where it stands (<paramref name="x"/>.. are the pilot's feet).</summary>
    public static void Exit(FrameRecord frame, float now, float x, float y, float z, float yaw)
    {
        frame.State = FrameState.Exiting;
        frame.StateEndsAt = now + FrameSequence.ExitSeconds;
        frame.BoostEndsAt = 0f;
        frame.Sieged = false;
        frame.SiegeDeployEndsAt = 0f;
        Place(frame, x, y, z, yaw);
    }

    /// <summary>Forced out at once (pilot down or gone): the suit is empty and standing immediately.</summary>
    public static void Eject(FrameRecord frame, float x, float y, float z, float yaw)
    {
        frame.State = FrameState.Idle;
        frame.Pilot = 0;
        frame.BoostEndsAt = 0f;
        frame.Sieged = false;
        frame.SiegeDeployEndsAt = 0f;
        Place(frame, x, y, z, yaw);
    }

    /// <summary>How far the pilot of a suit being boarded or left may stand from it: the furthest exit spot (1.5 m behind the suit and
    /// the navigation's 0.6 m sample around it) plus a margin. The pilot never walks during either sequence, so further away the game moved
    /// the pilot (a warp, a dimension change, a grab that drags).</summary>
    public const float TetherReach = 3.0f;

    /// <summary>Whether the game took the pilot of a suit being boarded or left: it holds the pilot (<paramref name="seized"/>) or the
    /// pilot's feet (<paramref name="x"/>..) are further than <see cref="TetherReach"/> from the suit. That sequence ends at once with the
    /// suit empty where it stands (<see cref="FrameEjection.PilotTaken"/>). A worn suit is never interrupted: it moves with its pilot.</summary>
    public static bool Interrupted(FrameRecord frame, bool seized, float x, float y, float z)
    {
        if (frame.State is not (FrameState.Entering or FrameState.Exiting)) return false;
        float dx = x - frame.X, dy = y - frame.Y, dz = z - frame.Z;
        return seized || !(dx * dx + dy * dy + dz * dz <= TetherReach * TetherReach);
    }

    public static void Place(FrameRecord frame, float x, float y, float z, float yaw)
    {
        frame.X = x; frame.Y = y; frame.Z = z; frame.Yaw = yaw;
        frame.Sequence++;
    }

    /// <summary>The part of a hit the armour takes: the model's damage reduction, and the planted suit's on top of it.</summary>
    public static float Absorbed(FrameRecord frame, float damage)
    {
        var kept = 1f - frame.Spec.DamageReduction;
        if (frame.Sieged && frame.Spec.Siege != null) kept *= 1f - frame.Spec.Siege.DamageReduction;
        return damage * kept;
    }

    /// <summary>Host: a hit on the pilot lands on the suit's armour instead (reduced by <see cref="Absorbed"/>). Returns true
    /// when this hit wrecks the suit; the pilot is then thrown out where it stands.</summary>
    public static bool Damage(FrameRecord frame, float damage, float now)
    {
        if (frame.State is not (FrameState.Entering or FrameState.Piloted or FrameState.Exiting)) return false;
        if (!(damage > 0f)) return false;
        frame.Armor = Math.Max(0f, frame.Armor - Absorbed(frame, damage));
        frame.Sequence++;
        if (frame.Armor > 0f) return false;
        frame.State = FrameState.Destroyed;
        frame.Pilot = 0;
        frame.StateEndsAt = now + WreckSeconds;
        frame.BoostEndsAt = 0f;
        frame.Sieged = false;
        frame.SiegeDeployEndsAt = 0f;
        return true;
    }

    public static FrameRejection CanBoost(FrameRecord frame, ulong sender, float now)
    {
        if (frame.State != FrameState.Piloted || frame.Pilot != sender) return FrameRejection.NotPilot;
        if (frame.Spec.Boost == null) return FrameRejection.NoSpecial;
        if (frame.Sieged || frame.SiegeDeployEndsAt > 0f) return FrameRejection.Planted;
        if (frame.Boosting(now)) return FrameRejection.Charging;
        if (now < frame.BoostReadyAt) return FrameRejection.Cooling;
        return FrameRejection.None;
    }

    public static void Boost(FrameRecord frame, float now)
    {
        var b = frame.Spec.Boost!;
        frame.BoostEndsAt = now + b.Seconds;
        frame.BoostReadyAt = now + b.Seconds + b.Cooldown;
        frame.Sequence++;
    }

    /// <summary>Host: a rocket salvo asked for by <paramref name="sender"/>; on success the cooldown starts.</summary>
    public static FrameRejection Rockets(FrameRecord frame, ulong sender, float now)
    {
        if (frame.State != FrameState.Piloted || frame.Pilot != sender) return FrameRejection.NotPilot;
        if (frame.Spec.Rockets == null) return FrameRejection.NoSpecial;
        if (now < frame.RocketsReadyAt) return FrameRejection.Cooling;
        frame.RocketsReadyAt = now + frame.Spec.Rockets.Cooldown;
        frame.Sequence++;
        return FrameRejection.None;
    }

    public const float RocketInterval = 0.12f;

    /// <summary>Whether a salvo aimed <paramref name="distance"/> metres away leaves the suit out of every blast (the point's spread plus the blast's radius and half a metre).</summary>
    public static bool RocketsClear(RocketsSpec rockets, float distance) => distance >= rockets.Spread + rockets.Radius + 0.5f;

    /// <summary>The suit is planted or planting: it stands still, on the ground.</summary>
    public static bool Planted(FrameRecord frame) => frame.Sieged || frame.SiegeDeployEndsAt > 0f;

    /// <summary>Where rocket <paramref name="index"/> of a salvo lands relative to the aimed point, and when it leaves (seconds
    /// after launch). Every peer computes the same values from the host's <paramref name="seed"/>.</summary>
    public static void RocketShot(ushort seed, int index, float spread, out float dx, out float dz, out float delay)
    {
        var h = unchecked((uint)seed * 2654435761u ^ (uint)(index + 1) * 2246822519u);
        h ^= h >> 15; h = unchecked(h * 2654435761u); h ^= h >> 13;
        var angle = (h & 0xFFFF) / 65536f * 2f * MathF.PI;
        var radius = spread * MathF.Sqrt((h >> 16) / 65536f);
        dx = radius * MathF.Cos(angle);
        dz = radius * MathF.Sin(angle);
        delay = index * RocketInterval;
    }

    public static FrameRejection CanSiege(FrameRecord frame, ulong sender)
        => frame.State != FrameState.Piloted || frame.Pilot != sender ? FrameRejection.NotPilot : frame.Spec.Siege == null ? FrameRejection.NoSpecial : FrameRejection.None;

    /// <summary>Whether the scout scan tags an enemy in this state: only awake ones (alert, hunting); sleeping and dead are left alone.</summary>
    public static bool ScanTags(ForgeCore.EnemyAlert alert) => alert is ForgeCore.EnemyAlert.Awake or ForgeCore.EnemyAlert.Hunting;

    /// <summary>Whether the suit's barrier stands right now: it has one, it is charged (not broken), the suit is piloted, and a barrier
    /// that stands only while planted (<see cref="ShieldSpec.WhileSieged"/>) has its suit planted. Every rule and every view of the
    /// barrier asks this.</summary>
    public static bool BarrierUp(FrameRecord frame)
    {
        var spec = frame.Spec.Shield;
        return spec != null && frame.ShieldUp && frame.State == FrameState.Piloted && (!spec.WhileSieged || frame.Sieged);
    }

    /// <summary>Host: an enemy attack stopped by the barrier. Returns true when it breaks the barrier.</summary>
    public static bool ShieldHit(FrameRecord frame, float damage, float now)
    {
        if (!BarrierUp(frame) || !(damage > 0f)) return false;
        frame.ShieldLastHit = now;
        frame.Shield = Math.Max(0f, frame.Shield - damage);
        if (frame.Shield <= 0f) frame.ShieldUp = false;
        frame.Sequence++;
        return !frame.ShieldUp;
    }

    /// <summary>Whether a raised barrier stops an attack from <c>source</c> on a player at <c>target</c>: the suit is piloted, the
    /// player stands inside the dome and the attacker is outside it. An attacker that got inside, or a hit with no attacker
    /// (falls, the level), is not stopped.</summary>
    public static bool Shelters(FrameRecord frame, float px, float py, float pz, float tx, float ty, float tz, float sx, float sy, float sz)
    {
        if (!BarrierUp(frame)) return false;
        var r2 = frame.Spec.Shield!.Radius * frame.Spec.Shield.Radius;
        float txd = tx - px, tyd = ty - (py + 1.2f), tzd = tz - pz;
        float sxd = sx - px, syd = sy - (py + 1.2f), szd = sz - pz;
        return txd * txd + tyd * tyd + tzd * tzd <= r2 && sxd * sxd + syd * syd + szd * szd > r2;
    }

    /// <summary>Whether a raised barrier pushes out an enemy standing at (x, y, z): the suit is piloted, its barrier stands and
    /// the enemy is inside the dome.</summary>
    public static bool Repels(FrameRecord frame, float x, float y, float z)
    {
        return BarrierUp(frame) && Distance2(frame, x, y, z) <= frame.Spec.Shield!.Radius * frame.Spec.Shield.Radius;
    }

    private static float Distance2(FrameRecord frame, float x, float y, float z)
    {
        // The dome stands on the suit's feet; heights are measured from its middle (about 1.2 m up).
        float dx = x - frame.X, dy = y - (frame.Y + 1.2f), dz = z - frame.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Host, every frame: refills the barrier after its quiet time, raising it again at its raise fraction.
    /// Returns true when the record changed.</summary>
    public static bool RegenShield(FrameRecord frame, float now, float dt)
    {
        var spec = frame.Spec.Shield;
        if (spec == null || frame.Shield >= spec.Capacity || now - frame.ShieldLastHit < spec.RegenDelay || !(dt > 0f)) return false;
        frame.Shield = Math.Min(spec.Capacity, frame.Shield + spec.RegenRate * dt);
        if (!frame.ShieldUp && frame.Shield >= spec.Capacity * spec.RaiseFraction) frame.ShieldUp = true;
        frame.Sequence++;
        return true;
    }

    /// <summary>Plants or stands the suit down as asked; false (nothing changed) when it already is as asked.</summary>
    public static bool SetSiege(FrameRecord frame, float now, bool on)
    {
        if (Planted(frame) == on) return false;
        ToggleSiege(frame, now);
        return true;
    }

    public static void ToggleSiege(FrameRecord frame, float now)
    {
        if (frame.Sieged || frame.SiegeDeployEndsAt > 0f)
        {
            frame.Sieged = false;
            frame.SiegeDeployEndsAt = 0f;
        }
        else
        {
            var seconds = frame.Spec.Siege!.DeploySeconds;
            if (seconds == 0f) frame.Sieged = true;
            else frame.SiegeDeployEndsAt = now + seconds;
            frame.BoostEndsAt = 0f;
        }
        frame.Sequence++;
    }

    /// <summary>Host, every frame: ends timed states. Returns true when the record changed (broadcast it) and sets
    /// <paramref name="remove"/> when a wreck's time is up.</summary>
    public static bool Tick(FrameRecord frame, float now, out bool remove)
    {
        remove = false;
        if (frame.State == FrameState.Piloted && frame.SiegeDeployEndsAt > 0f && now >= frame.SiegeDeployEndsAt)
        {
            frame.SiegeDeployEndsAt = 0f;
            frame.Sieged = true;
            frame.Sequence++;
            return true;
        }
        switch (frame.State)
        {
            case FrameState.Arriving when now >= frame.StateEndsAt:
                frame.State = FrameState.Idle;
                break;
            case FrameState.Entering when now >= frame.StateEndsAt:
                frame.State = FrameState.Piloted;
                break;
            case FrameState.Exiting when now >= frame.StateEndsAt:
                frame.State = FrameState.Idle;
                frame.Pilot = 0;
                break;
            case FrameState.Destroyed when now >= frame.StateEndsAt:
                remove = true;
                return false;
            default:
                return false;
        }
        frame.Sequence++;
        return true;
    }

    /// <summary>Host: the pilot's report of its gun. Only lowers the whole stock (a client cannot mint ammunition); how it is split is the gun's own (a gear's clip can be smaller than the definition's <see cref="WeaponSpec.Magazine"/>, the rest then stands in the reserve).</summary>
    public static bool ReportAmmo(FrameRecord frame, ulong sender, int magazine, int reserve)
    {
        if (!frame.HasPilot || frame.Pilot != sender) return false;
        // Not capped by the definition: a placement's Reserve override may stand above it.
        magazine = Math.Max(magazine, 0);
        reserve = Math.Max(reserve, 0);
        if ((long)magazine + reserve > frame.Magazine + frame.Reserve) return false;
        if (magazine == frame.Magazine && reserve == frame.Reserve) return false;
        frame.Magazine = magazine; frame.Reserve = reserve;
        frame.Sequence++;
        return true;
    }

    /// <summary>The game's carry-slot wield block is lifted for exactly one inventory: the local player's, while piloting a suit that
    /// carries heavy objects on its back. Every other player and every other suit keeps the game's rule.</summary>
    public static bool CarryBlockReleased(bool localPlayersInventory, bool piloting, bool carriesHeavy) => localPlayersInventory && piloting && carriesHeavy;

    /// <summary>Movement multiplier the pilot's locomotion uses this frame.</summary>
    public static float SpeedMultiplier(FrameRecord frame, float now)
        => Planted(frame) ? 0f : frame.Spec.Movement.Walk * (frame.Boosting(now) && frame.Spec.Boost != null ? frame.Spec.Boost.SpeedMultiplier : 1f);
}

/// <summary>The worn suit's sprint heat, 0 (cold) to 1: it rises while the pilot sprints and moves, cools otherwise; once full
/// the sprint gives no extra speed until the heat is back below <see cref="Rearm"/>.</summary>
internal sealed class SprintHeat
{
    public const float Rearm = 0.35f;

    public float Value { get; private set; }
    public bool Overheated { get; private set; }

    public void Reset() { Value = 0f; Overheated = false; }

    /// <summary>One step of <paramref name="dt"/> seconds; returns whether the sprint bonus applies now.</summary>
    public bool Step(MovementSpec spec, bool sprinting, float dt)
    {
        if (!(dt > 0f)) return sprinting && !Overheated;
        if (sprinting && !Overheated) Value = Math.Min(1f, Value + dt / spec.SprintSeconds);
        else Value = Math.Max(0f, Value - dt / spec.SprintRecovery);
        if (Value >= 1f) Overheated = true;
        else if (Overheated && Value <= Rearm) Overheated = false;
        return sprinting && !Overheated;
    }
}

